using HarmonyLib;
using System.Reflection;
using Il2CppRUMBLE.MoveSystem;
using Il2CppRUMBLE.Players.Subsystems;
using MelonLoader;
using UnityEngine;

namespace RumbleFlightTrainer;

internal sealed class FlightGameHooks : IDisposable
{
    private readonly HarmonyLib.Harmony harmony = new("GloriousCandle119.RumbleFlightRuntime");
    private readonly MelonLogger.Instance logger;
    private readonly List<MethodBase> methods = new();
    private static StructureTelemetry? Active => FlightRuntime.ActiveTelemetry;
    public bool StructureCollisionProbeInstalled { get; }
    public bool EnvironmentCollisionProbeInstalled { get; }

    public FlightGameHooks(MelonLogger.Instance logger)
    {
        this.logger = logger;
        Patch(typeof(Structure), nameof(Structure.OnFetchFromPool), nameof(FetchPostfix), postfix: true);
        Patch(typeof(Structure), nameof(Structure.OnReturnToPool), nameof(ReturnPrefix));
        Patch(typeof(Structure), nameof(Structure.Kill), nameof(KillPrefix));
        Patch(typeof(HoldModifier), nameof(HoldModifier.Execute), nameof(HoldPrefix));
        Patch(typeof(FlickModifier), nameof(FlickModifier.Execute), nameof(FlickPrefix));
        StructureCollisionProbeInstalled = Patch(typeof(CombatManager), nameof(CombatManager.RegisterStructureCollisionEvent),
            nameof(StructureCollisionPrefix), parameterTypes: new[] { typeof(Structure), typeof(Structure), typeof(Collision) });
        bool environmentEnter = Patch(typeof(Structure), nameof(Structure.OnCollisionEnter), nameof(EnvironmentEnterPrefix),
            finalizer: nameof(EnvironmentCollisionFinalizer));
        bool environmentStay = Patch(typeof(Structure), nameof(Structure.OnCollisionStay), nameof(EnvironmentStayPrefix),
            finalizer: nameof(EnvironmentCollisionFinalizer));
        EnvironmentCollisionProbeInstalled = environmentEnter && environmentStay;
    }

    public void Dispose()
    {
        foreach (var method in methods)
        {
            try
            {
                harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id);
            }
            catch (Exception error)
            {
                logger.Warning($"Could not remove flight hook {method.Name}: {error.Message}");
            }
        }
    }

    private bool Patch(Type gameType, string methodName, string hook, bool postfix = false, Type[]? parameterTypes = null,
        string? finalizer = null)
    {
        try
        {
            var method = AccessTools.DeclaredMethod(gameType, methodName, parameterTypes)
                ?? throw new MissingMethodException(gameType.FullName, methodName);
            if (methods.Contains(method))
                return true;
            var patch = new HarmonyMethod(typeof(FlightGameHooks), hook);
            if (postfix)
                harmony.Patch(method, postfix: patch);
            else
                harmony.Patch(method, prefix: patch, finalizer: finalizer is null ? null : new HarmonyMethod(typeof(FlightGameHooks), finalizer));
            methods.Add(method);
            return true;
        }
        catch (Exception error)
        {
            logger.Warning($"Flight hook {gameType.Name}.{methodName} failed: {error.Message}");
            return false;
        }
    }

    private sealed record EnvironmentFrame(StructureTelemetry Owner, StructureTelemetry.EnvironmentCollision? Previous);

    private static void EnvironmentEnterPrefix(Structure __instance, Collision __0, out EnvironmentFrame? __state) =>
        BeginEnvironmentCollision(__instance, __0, "enter", out __state);

    private static void EnvironmentStayPrefix(Structure __instance, Collision __0, out EnvironmentFrame? __state) =>
        BeginEnvironmentCollision(__instance, __0, "stay", out __state);

    private static void BeginEnvironmentCollision(Structure structure, Collision collision, string phase, out EnvironmentFrame? state)
    {
        state = null;
        if (Active is not { } capture)
            return;

        state = new EnvironmentFrame(capture, capture.ActiveEnvironmentCollision);
        capture.ActiveEnvironmentCollision = null;
        capture.Guard("structure_environment_collision", () => capture.BeginEnvironmentCollision(structure, collision, phase));
    }

    private static Exception? EnvironmentCollisionFinalizer(Exception? __exception, EnvironmentFrame? __state)
    {
        if (__state is { } frame)
            frame.Owner.ActiveEnvironmentCollision = frame.Previous;
        return __exception;
    }

    private static void StructureCollisionPrefix(Structure __0, Structure __1, Collision __2)
    {
        if (Active is { } capture)
            capture.Guard("structure_collision_probe", () => capture.OnStructureCollision(__0, __1, __2));
    }

    private static void FetchPostfix(Structure __instance)
    {
        if (Active is { } capture)
            capture.Guard("pool_fetch", () => capture.OnFetch(__instance));
    }

    private static void ReturnPrefix(Structure __instance)
    {
        if (Active is { } capture)
            capture.Guard("pool_return", () => capture.OnReturn(__instance));
    }

    private static void KillPrefix(Structure __instance)
    {
        if (Active is { } capture)
            capture.Guard("structure_kill", () => capture.OnKill(__instance));
    }

    private static void HoldPrefix(IProcessor __0, StackConfiguration __1)
    {
        if (Active is { } capture)
            capture.Guard("hold_execute", () => capture.OnModifier(__0, __1));
    }

    private static void FlickPrefix(IProcessor __0, StackConfiguration __1)
    {
        if (Active is { } capture)
            capture.Guard("flick_execute", () => capture.OnModifier(__0, __1));
    }
}
