using Il2CppInterop.Runtime.Injection;
using Il2CppRUMBLE.Players;
using Il2CppRUMBLE.Players.Subsystems;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using PlayerManager = Il2CppRUMBLE.Managers.PlayerManager;

namespace RumbleFlightTrainer;

internal sealed class FlightRuntime
{
    public const string Gym = "Gym";
    private static bool observerRegistered;
    private static FlightRuntime? current;

    private readonly Scene scene;
    private readonly string sceneExitReason;
    private readonly Rigidbody body;
    private readonly MelonLogger.Instance logger;
    private readonly FlightGameHooks hooks;
    private Action<TraceRecord>? trainer;
    private Action<string>? stopTrainer;
    private int? frameProcessed;
    private int? originId;
    private bool originObserved;
    private string? pendingStop;

    private FlightRuntime(Scene scene, PlayerController controller, PlayerMovement movement, Rigidbody body,
        MelonLogger.Instance logger, float ironBootsVerticalVelocityThreshold)
    {
        this.scene = scene;
        sceneExitReason = scene.name == Gym ? "left_active_gym" : "left_active_map";
        this.body = body;
        this.logger = logger;
        Controller = controller;
        Movement = movement;
        hooks = new FlightGameHooks(logger);
        try
        {
            if (!observerRegistered)
            {
                ClassInjector.RegisterTypeInIl2Cpp<FlightContactObserver>();
                observerRegistered = true;
            }
            var capture = new CaptureMetadata(Application.version, controller.ActorNo,
                hooks.StructureCollisionProbeInstalled, hooks.EnvironmentCollisionProbeInstalled);
            Observations = new FlightObservationStream(capture);
            Observations.ObservationCaptured += Publish;
            Telemetry = new StructureTelemetry(Observations, controller, body, logger, scene.name == Gym,
                ironBootsVerticalVelocityThreshold);
        }
        catch
        {
            hooks.Dispose();
            throw;
        }
    }

    public static StructureTelemetry? ActiveTelemetry => current?.Telemetry;
    public PlayerController Controller { get; }
    public PlayerMovement Movement { get; }
    public int PhysicsBodyInstanceId => body.GetInstanceID();
    public FlightObservationStream Observations { get; }
    public StructureTelemetry? Telemetry { get; private set; }
    public Transform? Origin { get; private set; }

    public static bool IsTrainerMap(string name) => name is Gym or "Park";

    public static FlightRuntime? TryOpenTrainer(Scene scene, MelonLogger.Instance logger, float ironBootsVerticalVelocityThreshold) =>
        IsTrainerMap(scene.name) ? TryOpen(scene, logger, ironBootsVerticalVelocityThreshold) : null;

    private static FlightRuntime? TryOpen(Scene scene, MelonLogger.Instance logger, float ironBootsVerticalVelocityThreshold)
    {
        if (current is { } existing)
        {
            if (existing.IsCurrent(scene))
                return existing;
            existing.StopTrainer("local_player_changed_or_unavailable");
        }

        var controller = FindLocalController();
        var movement = controller?.PlayerMovement;
        var body = controller?.PlayerPhysics?.PhysicsRigidbody;
        return controller == null || movement == null || body == null ? null :
            current = new FlightRuntime(scene, controller, movement, body, logger, ironBootsVerticalVelocityThreshold);
    }

    public void AttachTrainer(Action<TraceRecord> observe, Action<string> stop)
    {
        trainer = observe;
        stopTrainer = stop;
    }

    public void DetachTrainer()
    {
        trainer = null;
        stopTrainer = null;
        Release();
    }

    private void Release()
    {
        if (current != this)
            return;
        current = null;
        Observations.ObservationCaptured -= Publish;
        ReleaseTelemetry();
        hooks.Dispose();
    }

    public bool BeginFrame()
    {
        if (current != this)
            return false;
        int frame = Time.frameCount;
        if (frameProcessed == frame)
            return true;
        frameProcessed = frame;

        Scene active = SceneManager.GetActiveScene();
        string? reason = pendingStop ?? (active != scene ? sceneExitReason :
            !IsCurrent(active) ? "local_player_changed_or_unavailable" : null);
        if (reason is not null)
        {
            StopTrainer(reason);
            return false;
        }

        var vr = Controller.PlayerVR;
        Origin = vr == null ? null : vr.Origin;
        int? nextOriginId = Origin == null ? null : Origin.GetInstanceID();
        if (Keyboard.current?.f7Key.wasPressedThisFrame == true)
            ResetSegment(frame, "desktop_f7", "operator_marked_discontinuity");
        else if (originObserved && originId != nextOriginId)
            ResetSegment(frame, "melon_late_update", "tracking_origin_instance_changed");
        originId = nextOriginId;
        originObserved = true;
        Observations.FlushBreaks();
        return current == this;
    }

    public void FixedUpdate()
    {
        if (current != this || pendingStop is not null)
            return;

        try
        {
            Scene active = SceneManager.GetActiveScene();
            if (!IsCurrent(active))
            {
                pendingStop = active != scene ? sceneExitReason : "local_player_changed_or_unavailable";
                return;
            }

            Observations.FlushBreaks();
            if (current != this)
                return;

            int frame = Time.frameCount;
            double sampleSeconds = Observations.ElapsedSeconds;
            float fixedDelta = Time.fixedDeltaTime;
            Vec3? gravityImpulse = body.useGravity && !body.isKinematic ? ToVec3(Physics.gravity * (body.mass * fixedDelta)) : null;
            var playerState = new PhysicsState(Time.fixedTimeAsDouble, fixedDelta, new Pose(ToVec3(body.position), ToQuat(body.rotation)),
                ToVec3(body.worldCenterOfMass), ToVec3(body.linearVelocity), ToVec3(body.angularVelocity), Movement.IsGrounded(), gravityImpulse);
            StructureFrame? structures = null;
            try
            {
                structures = Telemetry?.Sample(Observations.NextPhysicsTick, playerState, sampleSeconds);
            }
            catch (Exception error)
            {
                logger.Warning($"Structure telemetry stopped: {error}");
                ReleaseTelemetry();
            }
            Observations.CapturePhysics(frame, playerState, structures);
        }
        catch (Exception error)
        {
            logger.Error($"Flight fixed-step observation failed: {error}");
            pendingStop = "fixed_capture_error";
        }
    }

    internal static PlayerController? FindLocalController()
    {
        var manager = PlayerManager.instance;
        if (manager == null || !manager.IsInitialized)
            return null;
        var controller = manager.LocalPlayer?.Controller;
        return controller == null || !controller.IsInitialized || controller.ControllerType != ControllerType.Local ? null : controller;
    }

    private bool IsCurrent(Scene active) => active == scene && Controller != null && body != null && Movement != null &&
        FindLocalController() == Controller && Controller.PlayerPhysics?.PhysicsRigidbody == body;

    private void ResetSegment(int frame, string source, string reason)
    {
        Telemetry?.ResetContacts();
        Observations.StartSegment(frame, source, reason);
    }

    private void Publish(TraceRecord observation)
    {
        try
        {
            if (observation is GameplayObservation { Event: "structure_destroyed" } destruction && Telemetry is { } telemetry)
                telemetry.Guard("iron_boots_break_diagram", () => telemetry.RetainIronBootsBreak(destruction));
            trainer?.Invoke(observation);
        }
        catch (Exception error)
        {
            logger.Error($"Flight HUD observation failed: {error}");
            StopTrainer("observation_error");
        }
    }

    private void StopTrainer(string reason)
    {
        stopTrainer?.Invoke(reason);
        Release();
    }

    private void ReleaseTelemetry()
    {
        var telemetry = Telemetry;
        Telemetry = null;
        try
        {
            telemetry?.Dispose();
        }
        catch (Exception error)
        {
            logger.Warning($"Could not release structure telemetry: {error.Message}");
        }
    }

    internal static Vec3 ToVec3(Vector3 value) => new(value.x, value.y, value.z);
    internal static Quat ToQuat(Quaternion value) => new(value.x, value.y, value.z, value.w);
}
