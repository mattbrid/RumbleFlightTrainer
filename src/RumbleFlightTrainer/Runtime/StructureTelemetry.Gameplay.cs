using Il2CppRUMBLE.MoveSystem;
using Il2CppRUMBLE.Players;
using Il2CppRUMBLE.Players.Subsystems;
using UnityEngine;
using UnityEngine.Events;
using static RumbleFlightTrainer.FlightRuntime;

namespace RumbleFlightTrainer;

internal sealed partial class StructureTelemetry
{
    private CombatManager? combatManager;
    private UnityEvent<ICombatEvent>? combatEvent;
    private UnityAction<ICombatEvent>? combatListener;
    private bool combatManagerFailed;
    private bool combatListenerFailed;

    public void OnFetch(Structure structure)
    {
        int id = structure.GetInstanceID();
        if (keys.Remove(id, out var oldKey))
        {
            destroyedHudKeys.Remove(oldKey);
            CloseContacts(oldKey, "new_pool_lifetime");
            Forget(oldKey);
            InvalidateImpactCapture(oldKey);
            nearby.Remove(oldKey);
            sampledKeys.Remove(oldKey);
        }
        cages.Remove(id);
        returned.Remove(id);
    }

    public void OnReturn(Structure structure)
    {
        int id = structure.GetInstanceID();
        returned.Add(id);
        if (!keys.TryGetValue(id, out var key))
            return;

        destroyedHudKeys.Remove(key);
        if (tracked.TryGetValue(key, out var entry))
            Record("structure_pool_return", "Structure.OnReturnToPool", entry);
        CloseContacts(key, "structure_returned");
        Forget(key);
        InvalidateImpactCapture(key);
        nearby.Remove(key);
        sampledKeys.Remove(key);
    }

    public void OnKill(Structure structure)
    {
        if (TryTracked(structure, out var entry))
            Record("structure_kill_requested", "Structure.Kill", entry,
                collisionSequence: entry is null ? null : DirectEnvironmentImpact(entry.Key));
    }

    private void OnDestroyed(Tracked entry)
    {
        if (entry.Destroyed || disposed)
            return;
        entry.Destroyed = true;
        destroyedHudKeys.Add(entry.Key);
        session.CaptureGameplay(Time.frameCount, new GameplayObservation(default, null, "structure_destroyed",
            "Structure.onStructureDestroyed", null, entry.Key, entry.Name,
            CollisionSequence: DirectEnvironmentImpact(entry.Key)));
        CloseContacts(entry.Key, "structure_destroyed");
        InvalidateImpactCapture(entry.Key);
    }

    public void OnModifier(IProcessor processor, StackConfiguration? configuration)
    {
        if (!IsLocalProcessor(processor) && !IsLocalProcessor(configuration?.Processor))
            return;

        var component = configuration?.TargetProcessable?.TryCast<ProcessableComponent>();
        var structure = component == null ? null : component.GetComponentInParent<Structure>();
        Tracked? entry = structure == null ? null : Promote(structure);
        if (entry is not null)
            entry.LocalModifierObserved = true;
    }

    private bool IsLocalProcessor(IProcessor? processor) =>
        processor?.TryCast<PlayerStackProcessor>() is { } player && player == controller.PlayerProcessor;

    private void RefreshCombatSource()
    {
        if (combatManager == null)
        {
            if (combatManagerFailed)
                return;
            try
            {
                combatManager = CombatManager.instance;
                if (combatManager == null)
                    return;
                string layerName = combatManager.environmentLayerName;
                int layer = string.IsNullOrEmpty(layerName) ? -1 : LayerMask.NameToLayer(layerName);
                terrainLayer = layer < 0 ? null : layer;
                Guard("combat_listener_cleanup", ReleaseCombatSource);
            }
            catch (Exception error)
            {
                combatManagerFailed = true;
                ReportFailure("combat_manager", error);
                return;
            }
        }
        if (combatListener != null || combatListenerFailed)
            return;

        try
        {
            combatEvent = CombatManager.OnProcessCombatEvent;
            if (combatEvent == null)
                return;
            combatListener = (Action<ICombatEvent>)(combat => Guard("combat_callback", () => OnCombat(combat)));
            combatEvent.AddListener(combatListener);
        }
        catch (Exception error)
        {
            combatListenerFailed = true;
            ReportFailure("combat_listener", error);
            Guard("combat_listener_cleanup", ReleaseCombatSource);
        }
    }

    private void ReleaseCombatSource()
    {
        var listener = combatListener;
        var source = combatEvent;
        combatListener = null;
        combatEvent = null;
        if (source != null && listener != null)
            source.RemoveListener(listener);
    }

    private void OnCombat(ICombatEvent combat)
    {
        if (disposed)
            return;
        if (combat.TryCast<PlayerHitEvent>() is { } hit)
            RecordLocalCombat("player_hit", hit.Player, hit.StructureSnapshot, combat.Timestamp);
        else if (combat.TryCast<PlayerHitFromBeneathEvent>() is { } beneath)
            RecordLocalCombat("player_hit_from_beneath", beneath.Player, beneath.StructureSnapshot, combat.Timestamp);
        else if (combat.TryCast<JumpOnStructureEvent>() is { } jump)
            RecordLocalCombat("jump_on_structure", jump.Player, jump.StructureSnapshot, combat.Timestamp,
                new CombatMeasurement(ReadCombatVelocity(jump.StructureSnapshot.Velocity),
                    PlayerPhysicsBody: ReadCombatVelocity(jump.PlayerPhysicsBodyVelocitySnapshot)),
                jump: jump);
        else if (combat.TryCast<StructureCollisionEvent>() is { } collision)
            RecordStructureCollision(collision, combat.Timestamp);
    }

    private void RecordLocalCombat(string kind, Player? player, StructureSnapshot snapshot, int timestamp,
        CombatMeasurement? measurement = null, JumpOnStructureEvent? jump = null)
    {
        if (player?.Controller != controller)
            return;
        Structure? structure = snapshot.Structure;
        var entry = structure == null ? null : Promote(structure);
        StructureKey? knownKey = entry?.Key;
        if (knownKey is null && structure != null && keys.TryGetValue(structure.GetInstanceID(), out var key))
            knownKey = key;

        IronBootsBreakEvidence? ironBoots = null;
        if (jump is not null && measurement is not null && entry is not null && IsActive(entry))
        {
            try
            {
                measurement = measurement with { PlayerVrBody = ReadCombatVelocity(jump.PlayerVRBodyVelocitySnapshot) };
                ironBoots = MeasureIronBootsBreak(entry.Structure, measurement);
            }
            catch (Exception error)
            {
                ReportFailure("iron_boots_jump_event", error);
            }
        }

        session.CaptureGameplay(Time.frameCount, new GameplayObservation(default, null, kind,
            "CombatManager.OnProcessCombatEvent", actor, knownKey,
            entry?.Name ?? snapshot.StructureName, CombatTimestamp: timestamp,
            Combat: measurement ?? new CombatMeasurement(ReadCombatVelocity(snapshot.Velocity)), IronBoots: ironBoots));
    }

    private void RecordStructureCollision(StructureCollisionEvent collision, int timestamp)
    {
        var first = collision.FirstStructureSnapshot;
        var second = collision.SecondStructureSnapshot;
        var firstStructure = first.Structure;
        var secondStructure = second.Structure;
        TryTracked(firstStructure, out var primary);
        TryTracked(secondStructure, out var secondary);
        if (primary?.CombatOnly != false && secondary?.CombatOnly != false)
            return;
        if (firstStructure != null)
            primary ??= Promote(firstStructure, combatOnly: true);
        if (secondStructure != null)
            secondary ??= Promote(secondStructure, combatOnly: true);
        session.CaptureGameplay(Time.frameCount, new GameplayObservation(default, null, "structure_collision",
            "CombatManager.OnProcessCombatEvent", null, primary?.Key ?? CombatCounterpartKey(firstStructure),
            primary?.Name ?? first.StructureName, secondary?.Key ?? CombatCounterpartKey(secondStructure),
            Detail: secondary?.Name ?? second.StructureName, CombatTimestamp: timestamp,
            Combat: new CombatMeasurement(ReadCombatVelocity(first.Velocity), ReadCombatVelocity(second.Velocity),
                ToVec3(collision.CollisionPoint))));
    }

    private static CombatVelocity ReadCombatVelocity(Il2CppRUMBLE.Physics.VelocitySnapshot snapshot) =>
        new(ToVec3(snapshot.Positional), ToVec3(snapshot.Angular));

    private StructureKey? CombatCounterpartKey(Structure? structure)
    {
        if (structure == null || IsCaged(structure))
            return null;
        int id = structure.GetInstanceID();
        if (returned.Contains(id))
            return keys.TryGetValue(id, out var earlier) ? earlier : null;
        return KeyFor(structure);
    }

    private bool TryTracked(Structure? structure, out Tracked? entry)
    {
        entry = null;
        return structure != null && keys.TryGetValue(structure.GetInstanceID(), out var key) &&
            tracked.TryGetValue(key, out entry);
    }
}
