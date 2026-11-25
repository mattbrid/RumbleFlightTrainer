using MelonLoader;
using RumbleFlightTrainer;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using GameSceneManager = Il2CppRUMBLE.Managers.SceneManager;
using static RumbleFlightTrainer.FlightRuntime;

[assembly: MelonInfo(typeof(TrainerMod), "RumbleFlightTrainer", TrainerMod.Version, "GloriousCandle119")]
[assembly: MelonGame("Buckethead Entertainment", "RUMBLE")]

namespace RumbleFlightTrainer;

public sealed class TrainerMod : MelonMod
{
    internal const string Version = "1.1.0";
    private const double StartupDelaySeconds = 3;

    private bool hudRequested;
    private bool showPerStructureImpulse;
    private bool showStructureVelocity;
    private bool showNormalSpeed;
    private bool showNormalSpeedOnBreak;
    private bool showVelocityOnBreak;
    private float contactHoldSeconds;
    private float ironBootsVerticalVelocityThreshold;
    private System.Numerics.Vector3 hudOffset;
    private System.Numerics.Vector3 ironBootsHudOffset;
    private Scene? activeMap;
    private double? mapReadySince;
    private (int Controller, int Body, int Origin, int Headset) readyTarget;
    private FlightRuntime? runtime;
    private FlightHudState? state;
    private FlightHudView? view;

    public override void OnInitializeMelon()
    {
        var preferences = MelonPreferences.CreateCategory("RumbleFlightTrainer");
        hudRequested = preferences.CreateEntry("HudEnabledOnStartup", true,
            description: "Enable the flight HUD in Gym and Park on startup. F8 toggles it for the current session.").Value;
        showPerStructureImpulse = preferences.CreateEntry("HudShowPerStructureImpulse", false,
            description: "Also show the gravity arrow and strongest individual contact impulse arrows.", oldIdentifier: "HudShowImpulse").Value;
        showStructureVelocity = preferences.CreateEntry("HudShowStructureVelocity", false,
            description: "Show relative velocity arrows for nearby structures.").Value;
        showNormalSpeed = preferences.CreateEntry("HudShowNormalSpeed", true,
            description: "Draw live normal-speed arrows toward the player when closing and away when separating. Before contact, estimate speed from the gap.").Value;
        showNormalSpeedOnBreak = preferences.CreateEntry("HudShowNormalSpeedOnBreak", true,
            description: "Draw an arrow along the contact normal on retained breaks. The sign of normal speed sets its direction.").Value;
        showVelocityOnBreak = preferences.CreateEntry("HudShowVelocityOnBreak", false,
            description: "Show the frozen relative velocity arrow on retained breaks.").Value;
        contactHoldSeconds = preferences.CreateEntry("HudContactHoldSeconds", FlightHudState.DefaultContactHoldSeconds,
            description: "Seconds to hold lost contacts and break reports. Zero hides retained contacts.").Value;
        float threshold = preferences.CreateEntry("IronBootsVerticalVelocityThreshold",
            IronBootsContactGate.DefaultVerticalVelocityThreshold,
            description: "Show Iron Boots feedback during foot contact while the VR body's vertical velocity in m/s is below this value. Negative means downward.").Value;
        ironBootsVerticalVelocityThreshold = float.IsFinite(threshold) ? threshold : IronBootsContactGate.DefaultVerticalVelocityThreshold;
        hudOffset = ReadOffsets("HudOffset", "main HUD", System.Numerics.Vector3.Zero);
        var ironBootsOrigin = preferences.CreateEntry("IronBootsHudOffsetsUseMainOrigin", false,
            description: "Interpret Iron Boots offsets from the shared HUD origin.", is_hidden: true);
        ironBootsHudOffset = ReadOffsets("IronBootsHudOffset", "Iron Boots HUD", new(0.31f, 0.035f, 0f),
            migrateFromPanelOrigin: !ironBootsOrigin.Value);
        ironBootsOrigin.Value = true;
        ironBootsOrigin.EditedValue = true;

        System.Numerics.Vector3 ReadOffsets(string prefix, string panel, System.Numerics.Vector3 defaults,
            bool migrateFromPanelOrigin = false) => new(
            ReadOffset(prefix + "X", $"Move the {panel} right by this many metres. Use a negative value to move left.",
                defaults.X, migrateFromPanelOrigin),
            ReadOffset(prefix + "Y", $"Move the {panel} up by this many metres. Use a negative value to move down.",
                defaults.Y, migrateFromPanelOrigin),
            ReadOffset(prefix + "Z", $"Move the {panel} farther away by this many metres. Use a negative value to move closer.",
                defaults.Z, migrateFromPanelOrigin));

        float ReadOffset(string key, string description, float defaultValue, bool migrateFromPanelOrigin)
        {
            var entry = preferences.CreateEntry(key, migrateFromPanelOrigin ? 0f : defaultValue, description: description);
            float offset = entry.Value + (migrateFromPanelOrigin ? defaultValue : 0f);
            offset = float.IsFinite(offset) ? offset : defaultValue;
            if (migrateFromPanelOrigin)
            {
                entry.DefaultValue = defaultValue;
                entry.Value = offset;
                entry.EditedValue = offset;
            }
            return offset;
        }
    }

    public override void OnApplicationQuit() => Stop("application_quit");

    public override void OnDeinitializeMelon() => Stop("trainer_unloaded");

    public override void OnFixedUpdate()
    {
        if (runtime is null)
            return;

        try
        {
            if (UpdateActiveMap() is not null)
                runtime?.FixedUpdate();
        }
        catch (Exception error)
        {
            DisableAfterError(error);
        }
    }

    public override void OnLateUpdate()
    {
        try
        {
            if (UpdateActiveMap() is not { } map)
                return;

            if (Keyboard.current?.f8Key.wasPressedThisFrame == true)
            {
                hudRequested = !hudRequested;
                if (!hudRequested)
                {
                    Stop("operator_stopped");
                    return;
                }
            }

            if (!hudRequested)
                return;
            if (runtime is null && StartupDelayElapsed(map))
                TryEnable(map);

            if (runtime is not { } active || !active.BeginFrame() || state is not { } hud)
                return;

            var telemetry = active.Telemetry;
            var nearby = telemetry?.ReadNearbyHudStructures(showStructureVelocity, showNormalSpeed);
            var frame = hud.Read(active.Observations.ElapsedSeconds, nearby?.Geometry);
            var vr = active.Controller.PlayerVR;
            Transform? headset = active.Origin == null || vr == null ? null : vr.Headset?.Transform;
            if (headset == null)
            {
                view?.Hide();
                return;
            }

            view ??= new FlightHudView(showPerStructureImpulse, showStructureVelocity, showNormalSpeed,
                showVelocityOnBreak, showNormalSpeedOnBreak);
            var playerReference = frame.Structures.Length > 0 || frame.Breaks.Any(broken => broken.PlayerContact?.Placement is not null)
                ? telemetry?.ReadHudPlayerReference() : null;
            var ironBoots = map.name == Gym ? telemetry?.ReadIronBoots(active.Origin) : null;
            view.Draw(headset, frame, playerReference, hudOffset, ironBootsHudOffset, ironBoots, nearby?.Velocities);
        }
        catch (Exception error)
        {
            DisableAfterError(error);
        }
    }

    private Scene? UpdateActiveMap()
    {
        var manager = GameSceneManager.instance;
        bool loading = manager != null && manager.IsLoadingScene;
        Scene scene = SceneManager.GetActiveScene();
        bool playable = !loading && scene.IsValid() && scene.isLoaded && IsTrainerMap(scene.name);
        if (activeMap is { } previous && (!playable || previous != scene))
        {
            activeMap = null;
            mapReadySince = null;
            Stop(loading ? "map_loading" : "active_map_changed");
        }

        if (!playable)
            return null;

        if (activeMap is null)
        {
            activeMap = scene;
            LoggerInstance.Msg($"Flight trainer entered {scene.name}. F8 toggles the HUD.");
        }
        return scene;
    }

    private bool StartupDelayElapsed(Scene map)
    {
        if (ReadReadyTarget() is not { } target)
        {
            mapReadySince = null;
            return false;
        }

        double now = Time.realtimeSinceStartupAsDouble;
        if (mapReadySince is null || readyTarget != target)
        {
            mapReadySince = now;
            readyTarget = target;
            LoggerInstance.Msg($"{map.name} player and headset transform ready. Flight HUD starts in {StartupDelaySeconds:0} seconds.");
        }
        return now - mapReadySince.Value >= StartupDelaySeconds;
    }

    private static (int Controller, int Body, int Origin, int Headset)? ReadReadyTarget()
    {
        var controller = FindLocalController();
        var body = controller?.PlayerPhysics?.PhysicsRigidbody;
        if (controller == null || controller.PlayerMovement == null || body == null)
            return null;

        var vr = controller.PlayerVR;
        var origin = vr == null ? null : vr.Origin;
        var headset = vr == null ? null : vr.Headset?.Transform;
        return origin == null || headset == null ? null
            : (controller.GetInstanceID(), body.GetInstanceID(), origin.GetInstanceID(), headset.GetInstanceID());
    }

    private void TryEnable(Scene map)
    {
        var active = TryOpenTrainer(map, LoggerInstance, ironBootsVerticalVelocityThreshold);
        if (active is null)
            return;

        runtime = active;
        var hud = new FlightHudState(active.PhysicsBodyInstanceId, contactHoldSeconds);
        state = hud;
        active.AttachTrainer(hud.Observe, HandleRuntimeStop);
        LoggerInstance.Msg($"Flight HUD enabled in {map.name}.");
    }

    private void HandleRuntimeStop(string reason)
    {
        mapReadySince = null;
        if (reason is "fixed_capture_error" or "observation_error")
            hudRequested = false;
        Stop(reason);
    }

    private void DisableAfterError(Exception error)
    {
        hudRequested = false;
        LoggerInstance.Warning($"Flight HUD disabled: {error}");
        Stop("trainer_update_error");
    }

    private void Stop(string reason)
    {
        var active = runtime;
        var enabledView = view;
        runtime = null;
        state = null;
        view = null;
        active?.DetachTrainer();
        try
        {
            enabledView?.Dispose();
        }
        catch (Exception error)
        {
            LoggerInstance.Warning($"Could not release flight HUD: {error.Message}");
        }
        if (active is not null)
            LoggerInstance.Msg($"Flight HUD stopped ({reason}).");
    }
}
