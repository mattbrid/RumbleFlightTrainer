using Il2CppRUMBLE.MoveSystem;
using Il2CppRUMBLE.Players;
using Il2CppRUMBLE.Players.Subsystems.Hitboxes;
using UnityEngine;
using static RumbleFlightTrainer.FlightRuntime;

namespace RumbleFlightTrainer;

internal sealed partial class StructureTelemetry
{
    private readonly List<HudPlayerHitbox> hudHitboxes = new();
    private readonly HashSet<StructureKey> hudHitWarnings = new();
    private readonly HashSet<StructureKey> hudTouchingKeys = new();

    private enum HudHitApproach { Miss, GentleSupport, Hit }

    private readonly record struct HudPlayerHitbox(Collider Collider, Bounds Bounds, Vector3 Position, Quaternion Rotation);
    private readonly record struct HudHitWarningContext(CombatManager Manager, Player Player, int CollisionMask,
        Vector3 VrVelocity, Vector3 BodyVelocity, Vector3 BodyAcceleration, float MinimumSpeed, float ScoreThreshold,
        Vector3 PlayerCenter, float PullForce);

    private void ResetHudHitWarnings(bool enabled)
    {
        hudHitWarnings.Clear();
        hudTouchingKeys.Clear();
        hudHitboxes.Clear();
        if (ObserveHudModifierSteps && !enabled)
            foreach (var entry in tracked.Values)
                entry.ModifierForce = null;
        ObserveHudModifierSteps = enabled;
        if (!enabled)
            return;

        foreach (var contact in contacts.Values)
            if (contact.Last.Structure is { } key)
                hudTouchingKeys.Add(key);
    }

    private HudHitWarningContext? ReadHudHitWarningContext(Vector3 playerCenter, float pullForce)
    {
        try
        {
            var manager = CombatManager.instance;
            var player = controller.AssignedPlayer;
            var system = controller.PlayerHitboxSystem;
            var vrBody = controller.PlayerPhysics?.VRrigidbody;
            if (manager == null || player == null || system == null || !system.isActiveAndEnabled ||
                vrBody == null || system.Hitboxes is not { } hitboxes)
                return null;

            for (int i = 0, count = hitboxes.Count; i < count; i++)
            {
                var hitbox = hitboxes[i];
                if (hitbox == null || hitbox.TryCast<PlayerVCMuteHitbox>() is not null)
                    continue;
                var collider = hitbox.AttachedCollider;
                if (!ColliderActive(collider))
                    continue;
                var transform = collider!.transform;
                hudHitboxes.Add(new HudPlayerHitbox(collider, collider.bounds, transform.position, transform.rotation));
            }

            return hudHitboxes.Count == 0 ? null : new HudHitWarningContext(manager, player, system.CollisionMask.value,
                vrBody.linearVelocity, body.linearVelocity, ReadHudGravity(body, controller.PlayerMovement.IsGrounded()),
                manager.MinStructureSpeedForImpact,
                manager.playerStructureVelocityFactorTreshold, playerCenter, pullForce);
        }
        catch (Exception error)
        {
            ReportFailure("hud_hit_warning_setup", error);
            return null;
        }
    }

    private void AddHudHitWarning(StructureKey key, Structure structure, Collider collider, HudHitWarningContext context)
    {
        if (hudHitWarnings.Contains(key))
            return;

        try
        {
            var structureBody = collider.attachedRigidbody ?? structure.Rigidbody;
            if (destroyedHudKeys.Contains(key) || (context.CollisionMask & (1 << collider.gameObject.layer)) == 0 ||
                structureBody == null || structure.PhysicsFrozen || structure.Indistructable || structure.IsInPool)
            {
                return;
            }

            bool grounded = structure.IsGrounded;
            var velocity = structureBody.linearVelocity;
            var acceleration = ReadHudGravity(structureBody, grounded);
            var angularVelocity = structureBody.angularVelocity;
            var bounds = collider.bounds;
            var centerOfMass = structureBody.worldCenterOfMass;
            var transform = collider.transform;
            var position = transform.position;
            var rotation = transform.rotation;
            var drive = ReadHudModifierDrive(key, structureBody, grounded);
            var motion = new HitWarningMotion(Numerics(velocity), Numerics(acceleration),
                grounded ? System.Numerics.Vector3.Zero : Numerics(angularVelocity),
                bounds.extents.magnitude + (bounds.center - centerOfMass).magnitude, drive);
            if (TryWarn(motion, out var approach) ||
                approach == HudHitApproach.GentleSupport || context.PullForce <= 0f)
                return;

            var turn = HitWarningTurn.TowardPlayer(motion.Velocity, Numerics(context.PlayerCenter - centerOfMass),
                context.PullForce, structureBody.mass, grounded);
            if (turn.IsActive)
                TryWarn(motion with { Turn = turn }, out _);

            bool TryWarn(HitWarningMotion candidate, out HudHitApproach projected)
            {
                var relativeMotion = candidate.RelativeTo(Numerics(context.BodyVelocity), Numerics(context.BodyAcceleration));
                projected = ProjectHudHitApproach(collider, bounds, position, rotation, centerOfMass, angularVelocity,
                    relativeMotion, FlightHitWarningSweep.LookAheadSeconds, out float impactTime);
                if (projected != HudHitApproach.Hit || !PassesHitScore(UnityVector(candidate.VelocityAt(impactTime))))
                    return false;

                hudHitWarnings.Add(key);
                return true;
            }
        }
        catch (Exception error)
        {
            ReportFailure("hud_hit_warning", error);
        }

        bool PassesHitScore(Vector3 impactVelocity)
        {
            float impactSpeed = impactVelocity.magnitude;
            if (!float.IsFinite(impactSpeed) || !float.IsFinite(context.MinimumSpeed) ||
                impactSpeed < context.MinimumSpeed || context.Manager.HasEventRegistered(structure, context.Player))
                return false;
            if (structure.BaseStructureTier <= 0)
                return true;

            int tier = ReadHudImpactTier(structure, impactSpeed);
            if (tier <= 0 || !float.IsFinite(context.VrVelocity.sqrMagnitude))
                return false;
            var score = context.Manager.GetPjotrVelocityFactor(context.VrVelocity, 0, impactVelocity, tier);
            return float.IsFinite(score) && score > context.ScoreThreshold;
        }
    }

    private static int ReadHudImpactTier(Structure structure, float speed)
    {
        var tiers = structure.speedTiers;
        if (tiers is null || tiers.Length == 0)
            return structure.GetTotalTier();

        int baseAndStateTier = structure.BaseStructureTier + structure.GetStateTier();
        if (speed <= 0f)
            return baseAndStateTier;
        for (int i = 0; i < tiers.Length; i++)
        {
            var tier = tiers[i];
            var range = tier.VelocityRange;
            if (speed >= range.x && speed <= range.y || i == tiers.Length - 1)
                return baseAndStateTier + tier.Tier;
        }
        return structure.GetTotalTier();
    }

    private static Vector3 ReadHudGravity(Rigidbody rigidbody, bool grounded) =>
        !grounded && rigidbody.useGravity && !rigidbody.isKinematic ? Physics.gravity : Vector3.zero;

    private static System.Numerics.Vector3 Numerics(Vector3 value) => new(value.x, value.y, value.z);
    private static Vector3 UnityVector(System.Numerics.Vector3 value) => new(value.X, value.Y, value.Z);

    private HudHitApproach ProjectHudHitApproach(Collider collider, Bounds bounds, Vector3 position, Quaternion rotation,
        Vector3 centerOfMass, Vector3 angularVelocity, HitWarningMotion motion, float horizon, out float impactTime)
    {
        bool gentleSupport = false;
        float? earliestHit = null;

        foreach (var hitbox in hudHitboxes)
        {
            var offset = Numerics(bounds.center - hitbox.Bounds.center);
            var extents = Numerics(bounds.extents + hitbox.Bounds.extents);
            if (!motion.TryOverlapWindow(offset, extents, horizon, out float enter, out float exit) ||
                earliestHit is { } first && enter >= first)
                continue;

            // These combat volumes may report as ignored by Unity. Test their geometry directly.
            // Include the beginning of a narrow overlap window, then sample as the shapes move through it.
            int count = motion.SampleCount(enter, exit);
            bool overlappingAtStart = false;
            for (int sample = 0; sample <= count; sample++)
            {
                float seconds = Mathf.Lerp(enter, exit, (float)sample / count);
                if (earliestHit is { } previous && seconds >= previous)
                    break;
                var displacement = UnityVector(motion.Displacement(seconds));
                var spin = motion.RotationAt(seconds);
                var turn = new Quaternion(spin.X, spin.Y, spin.Z, spin.W);
                var center = centerOfMass + displacement;
                var movedPosition = center + turn * (position - centerOfMass);
                if (!Physics.ComputePenetration(hitbox.Collider, hitbox.Position, hitbox.Rotation,
                        collider, movedPosition, turn * rotation, out var normal, out _))
                {
                    overlappingAtStart = false;
                    continue;
                }

                overlappingAtStart |= seconds == 0f;
                var relativePointVelocity = UnityVector(motion.VelocityAt(seconds));
                if ((overlappingAtStart || normal.y >= FlightHitWarningSweep.SupportNormalY) &&
                    angularVelocity.sqrMagnitude > 0f)
                {
                    var centerAtCurrentPose = centerOfMass + Quaternion.Inverse(turn) * (hitbox.Bounds.center - center);
                    var lever = turn * (collider.ClosestPoint(centerAtCurrentPose) - centerOfMass);
                    relativePointVelocity += Vector3.Cross(angularVelocity, lever);
                }
                // A gameplay trigger can already overlap before an ordinary hit is registered.
                if (overlappingAtStart && Vector3.Dot(relativePointVelocity, normal) <= 0f)
                {
                    if (!motion.Turn.IsActive && motion.Drive.Duration == 0f &&
                        Vector3.Dot(UnityVector(motion.Acceleration), normal) <= 0f)
                        break;
                    continue;
                }
                if (!FlightHitWarningSweep.IsGentleSupport(Numerics(normal), Numerics(relativePointVelocity)))
                {
                    earliestHit = seconds;
                    if (seconds == 0f)
                    {
                        impactTime = 0f;
                        return HudHitApproach.Hit;
                    }
                    break;
                }

                gentleSupport = true;
                break;
            }
        }
        impactTime = earliestHit ?? 0f;
        return earliestHit.HasValue ? HudHitApproach.Hit : gentleSupport ? HudHitApproach.GentleSupport : HudHitApproach.Miss;
    }
}
