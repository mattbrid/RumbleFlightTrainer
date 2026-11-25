using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppRUMBLE.MoveSystem;
using UnityEngine;
using static RumbleFlightTrainer.FlightRuntime;

namespace RumbleFlightTrainer;

internal readonly record struct HudNormalSpeed(float Value, float? EstimateGapSquared = null);
internal readonly record struct HudStructureVelocity(Vector3 CenterOffset, Vector3 RelativeVelocity, HudNormalSpeed? Normal = null);

internal sealed partial class StructureTelemetry
{
    private const float HudRangeMetres = 2.5f;

    private Il2CppReferenceArray<Collider>? hudOverlap;
    private readonly Dictionary<StructureKey, List<HudNearbyCollider>> hudNearby = new();
    private readonly Dictionary<StructureKey, HudStructureVelocity> hudVelocities = new();
    private readonly Dictionary<(StructureKey Key, int ColliderId), HudMeshShape> hudMeshes = new();
    private readonly HashSet<StructureKey> destroyedHudKeys = new();
    private bool hudOverlapFailed;

    private sealed record HudMeshShape(ColliderShape Base, Vec3 Scale, ColliderShape Outline);

    public (IReadOnlyDictionary<StructureKey, List<HudNearbyCollider>> Geometry,
        IReadOnlyDictionary<StructureKey, HudStructureVelocity> Velocities, IReadOnlySet<StructureKey> HitWarnings)
        ReadNearbyHudStructures(bool showVelocity, bool showNormal, bool showHitWarning, float hitWarningPullAcceleration)
    {
        hudNearby.Clear();
        hudVelocities.Clear();
        var center = body.worldCenterOfMass;
        bool sampleVelocity = showVelocity || showNormal;
        var playerVelocity = sampleVelocity ? body.linearVelocity : Vector3.zero;
        var capsule = showNormal ? controller.PlayerPhysics.pillBodyCollider : null;
        var origin = new HudBodyFrame(FlightContactPatches.Vector(ToVec3(center)), System.Numerics.Quaternion.Identity);
        HudHitWarningContext? hitWarning = null;
        bool warningContextRead = false;
        ResetHudHitWarnings(showHitWarning);

        int count = hudOverlapFailed ? 0 : QueryHudColliders(center);
        for (int i = 0; i < count; i++)
        {
            var collider = hudOverlap![i];
            if (!ColliderActive(collider))
                continue;
            var structure = FindStructure(collider);
            if (structure == null || !structure.gameObject.activeInHierarchy ||
                returned.Contains(structure.GetInstanceID()) || IsCaged(structure))
                continue;

            var key = KeyFor(structure);
            CheckHitWarning(key, structure, collider);

            // The baked wireframe belongs to the structure's primary collision mesh.
            var primary = structure.Collider;
            if (primary != null && collider != primary && primary.TryCast<MeshCollider>() is { } mesh)
            {
                var asset = mesh.sharedMesh;
                if (asset != null && FlightBakedWireframes.HasBakedOutline(asset.name))
                    continue;
            }

            AddHudOutline(key, collider, origin);
            if (sampleVelocity)
                AddHudVelocity(key, structure, center, playerVelocity, collider, capsule);
        }

        if (hudOverlapFailed)
        {
            foreach (var entry in tracked.Values)
            {
                if (entry.CombatOnly || !IsActive(entry))
                    continue;
                var collider = entry.Structure.Collider;
                if (ColliderActive(collider) &&
                    (collider.ClosestPoint(center) - center).sqrMagnitude <= HudRangeMetres * HudRangeMetres)
                {
                    AddHudOutline(entry.Key, collider, origin);
                    CheckHitWarning(entry.Key, entry.Structure, collider);
                    if (sampleVelocity)
                        AddHudVelocity(entry.Key, entry.Structure, center, playerVelocity, collider, capsule);
                }
            }
        }

        // A live contact may still be displayed beyond the proximity query.
        if (sampleVelocity)
            foreach (var contact in contacts.Values)
            {
                if (contact.Last.Structure is not { } key || !tracked.TryGetValue(key, out var entry) || !IsActive(entry))
                    continue;
                AddHudVelocity(key, entry.Structure, center, playerVelocity, contact.OtherCollider, capsule);
                if (showNormal && hudVelocities.TryGetValue(key, out var velocity) &&
                    velocity.Normal is not { EstimateGapSquared: null } &&
                    contact.Last.Points.MinBy(point => point.Separation) is { NormalTowardPlayer: var normal } &&
                    ProjectHudSpeed(velocity.RelativeVelocity, new Vector3(normal.X, normal.Y, normal.Z)) is { } speed)
                    hudVelocities[key] = velocity with { Normal = new HudNormalSpeed(speed) };
            }

        foreach (var identity in hudMeshes.Keys)
            if (!hudNearby.ContainsKey(identity.Key))
                hudMeshes.Remove(identity);

        return (hudNearby, hudVelocities, hudHitWarnings);

        void CheckHitWarning(StructureKey key, Structure structure, Collider collider)
        {
            if (!showHitWarning || hudTouchingKeys.Contains(key))
                return;

            if (!warningContextRead)
            {
                warningContextRead = true;
                hitWarning = ReadHudHitWarningContext(center, hitWarningPullAcceleration);
            }
            if (hitWarning is { } context)
                AddHudHitWarning(key, structure, collider, context);
        }
    }

    private void AddHudVelocity(StructureKey key, Structure structure, Vector3 playerCenter, Vector3 playerVelocity,
        Collider collider, Collider? capsule)
    {
        if (destroyedHudKeys.Contains(key))
            return;
        if (!hudVelocities.TryGetValue(key, out var sample))
        {
            var structureBody = structure.Rigidbody;
            if (structureBody == null)
                return;
            var offset = (structureBody.worldCenterOfMass - playerCenter) * HudContactPlacement.Scale;
            var velocity = structureBody.linearVelocity - playerVelocity;
            if (!float.IsFinite(offset.sqrMagnitude) || !float.IsFinite(velocity.sqrMagnitude))
                return;
            hudVelocities[key] = sample = new HudStructureVelocity(offset, velocity);
        }

        if (!ColliderActive(capsule) || !ColliderActive(collider) || sample.Normal is { EstimateGapSquared: null })
            return;
        try
        {
            var (onStructure, onPlayer) = ClosestHudSurfaces(collider, capsule!);
            var gap = onPlayer - onStructure;
            float gapSquared = gap.sqrMagnitude;
            if ((sample.Normal?.EstimateGapSquared ?? float.PositiveInfinity) > gapSquared &&
                ProjectHudSpeed(sample.RelativeVelocity, gap) is { } estimate)
                hudVelocities[key] = sample with { Normal = new HudNormalSpeed(estimate, gapSquared) };
        }
        catch (Exception error)
        {
            ReportFailure("hud_normal_speed_estimate", error);
        }
    }

    private static (Vector3 Structure, Vector3 Player) ClosestHudSurfaces(Collider structure, Collider capsule)
    {
        var first = structure.ClosestPoint(capsule.bounds.center);
        var surface = structure.ClosestPoint(capsule.ClosestPoint(first));
        return (surface, capsule.ClosestPoint(surface));
    }

    private static float? ProjectHudSpeed(Vector3 velocity, Vector3 normal)
    {
        var length = normal.magnitude;
        if (!float.IsFinite(length) || length <= 0.0001f)
            return null;
        var speed = Vector3.Dot(velocity, normal / length);
        return float.IsFinite(speed) ? speed : null;
    }

    private HudPlayerBreakContact? ReadHudBreakEstimate(Tracked entry, CombatMeasurement combat)
    {
        var physics = controller.PlayerPhysics;
        var collider = entry.Structure.Collider;
        if (!ColliderActive(collider))
            return null;
        var capsule = EstimateAgainstCollider(physics.pillBodyCollider, collider!);
        return capsule is { NormalMetersPerSecond: not null } ? capsule :
            EstimateAgainstCollider(physics.FootCollider, collider!) ?? capsule;

        HudPlayerBreakContact? EstimateAgainstCollider(Collider? hitbox, Collider shape)
        {
            if (!ColliderActive(hitbox) || ReadHudPlayerCollider(hitbox) is not { } player)
                return null;
            return ReadHudBreakEstimate(entry.Key, combat, hitbox!, shape, player);
        }
    }

    private HudPlayerBreakContact? ReadHudBreakEstimate(StructureKey key, CombatMeasurement combat, Collider hitbox,
        Collider structure, HudPlayerCollider player)
    {
        var (structurePoint, playerPoint) = ClosestHudSurfaces(structure, hitbox);
        var gap = playerPoint - structurePoint;
        if (!(gap.sqrMagnitude <= 0.25f * 0.25f))
            return null;

        var origin = new HudBodyFrame(FlightContactPatches.Vector(ToVec3(body.worldCenterOfMass)),
            FlightContactPatches.Yaw(ToQuat(body.rotation)) ?? System.Numerics.Quaternion.Identity);
        var placement = ReadHudOutlinePlacement(key, structure, origin) with { Player = player };
        var worldPoint = FlightContactPatches.Vector(ToVec3(playerPoint));
        var point = player.PointOnSurface(worldPoint) ?? worldPoint;
        var center = placement.Box?.Center ?? placement.Mesh?.Center ?? FlightContactPatches.Vector(ToVec3(structure.bounds.center));
        var receiver = hitbox.attachedRigidbody;
        bool mainBody = receiver == body;
        Vec3? sampledVelocity = receiver != null && fixedBodies.TryGetValue(receiver.GetInstanceID(), out var cached) &&
            cached.Motion.Segment == session.Segment && cached.Motion.Tick == session.LastPhysicsTick
            ? cached.Motion.Body.LinearVelocityWorld : null;
        var playerVelocity = (mainBody ? combat.PlayerPhysicsBody?.Positional : null) ?? sampledVelocity ??
            ToVec3(receiver == null ? body.linearVelocity : receiver.linearVelocity);
        var relative = FlightContactPatches.Vector(combat.FirstStructure.Positional) - FlightContactPatches.Vector(playerVelocity);
        var normal = EstimateBreakNormal(hitbox, structure, playerPoint, gap).normalized;
        var normalSpeed = ProjectHudSpeed(new Vector3(relative.X, relative.Y, relative.Z), normal);
        return new HudPlayerBreakContact(placement, new HudContactPatch(Array.Empty<ContactGeometry>(), null, null, point),
            center, FlightContactPatches.IsFinite(relative) ? relative : null, normalSpeed, LinearEstimate: true, EstimatedContact: true,
            PlayerColliderId: hitbox.GetInstanceID(),
            NormalTowardPlayerWorld: normalSpeed.HasValue ? FlightContactPatches.Vector(ToVec3(normal)) : null);
    }

    private Vector3 EstimateBreakNormal(Collider hitbox, Collider structure, Vector3 playerPoint, Vector3 gap)
    {
        float distance = gap.magnitude;
        if (distance > 0.0001f)
        {
            try
            {
                if (structure.Raycast(new Ray(playerPoint, -gap / distance), out var hit, distance + 0.01f) &&
                    Vector3.Dot(hit.normal, gap) > 0f)
                    return hit.normal;
            }
            catch (Exception error)
            {
                ReportFailure("hud_break_surface_normal", error);
            }
            return gap;
        }

        try
        {
            var playerPose = hitbox.transform;
            var structurePose = structure.transform;
            if (Physics.ComputePenetration(hitbox, playerPose.position, playerPose.rotation,
                    structure, structurePose.position, structurePose.rotation, out var normal, out _))
                return normal;
        }
        catch (Exception error)
        {
            ReportFailure("hud_break_overlap_normal", error);
        }
        return Vector3.zero;
    }

    public HudNearbyCollider? ReadHudPlayerReference()
    {
        var collider = controller.PlayerPhysics.pillBodyCollider;
        if (collider == null || ReadHudPlayerCollider(collider) is not { } player)
            return null;

        var origin = new HudBodyFrame(FlightContactPatches.Vector(ToVec3(body.worldCenterOfMass)),
            System.Numerics.Quaternion.Identity);
        return new HudNearbyCollider(collider.GetInstanceID(), new HudContactPlacement(origin, player, null));
    }

    private HudPlayerCollider? ReadHudPlayerCollider(Collider? collider)
    {
        if (collider == null || !ColliderActive(collider))
            return null;

        var frame = HudColliderFrame.From(Frame(ColliderPhysicsFrame(collider, collider.attachedRigidbody)));
        return HudPlayerCollider.Build(ReadShape(collider), frame);
    }

    private int QueryHudColliders(Vector3 center)
    {
        try
        {
            hudOverlap ??= new Il2CppReferenceArray<Collider>(32);
            while (true)
            {
                int count = Physics.OverlapSphereNonAlloc(center, HudRangeMetres, hudOverlap, Physics.AllLayers,
                    QueryTriggerInteraction.Ignore);
                if (count < hudOverlap.Length)
                    return count;

                hudOverlap = new Il2CppReferenceArray<Collider>(checked(count * 2));
            }
        }
        catch (Exception error)
        {
            hudOverlapFailed = true;
            ReportFailure("hud_proximity_query", error);
            return 0;
        }
    }

    private void AddHudOutline(StructureKey key, Collider collider, HudBodyFrame origin)
    {
        if (destroyedHudKeys.Contains(key))
            return;

        if (!hudNearby.TryGetValue(key, out var colliders))
            hudNearby[key] = colliders = new List<HudNearbyCollider>();
        var placement = ReadHudOutlinePlacement(key, collider, origin);
        if (placement.Box is not null || placement.Mesh is not null)
            colliders.Add(new HudNearbyCollider(collider.GetInstanceID(), placement));
    }

    private HudContactPlacement ReadHudOutlinePlacement(StructureKey key, Collider collider, HudBodyFrame origin)
    {
        int colliderId = collider.GetInstanceID();
        var attached = collider.attachedRigidbody;
        var frame = HudColliderFrame.From(Frame(ColliderPhysicsFrame(collider, attached)));
        var shape = ReadShape(collider);
        if (shape is { Kind: "mesh", Convex: true, MeshBounds: { } bounds })
        {
            var identity = (key, colliderId);
            var scale = ToVec3(collider.transform.lossyScale);
            if (!hudMeshes.TryGetValue(identity, out var cached) || cached.Base != shape || !Near(cached.Scale, scale))
            {
                var edges = FlightBakedWireframes.Match(shape, session.GameVersion, scale);
                var outline = edges is not null
                    ? shape with { MeshEdges = edges }
                    : shape with { MeshContours = CaptureMeshContours(collider, attached, bounds) };
                hudMeshes[identity] = cached = new HudMeshShape(shape, scale, outline);
            }
            shape = cached.Outline;
        }

        var observed = new HudStructureCollider(shape, frame);
        var placement = observed.Place(origin, null, null);
        if (placement is { Box: null, Mesh: null } && observed.AllowsBoundsPreview)
        {
            HudBoxPreview? boundsBox = null;
            if (shape.MeshBounds is { } previewBounds)
                boundsBox = FlightBoxPreview.Build(null,
                    new ColliderShape("box", Center: previewBounds.Center, Size: previewBounds.Size), frame);
            if (boundsBox is null)
            {
                var worldBounds = collider.bounds;
                var worldFrame = HudColliderFrame.From(Frame(Matrix4x4.identity));
                boundsBox = FlightBoxPreview.Build(null, new ColliderShape("box", Center: ToVec3(worldBounds.center),
                    Size: ToVec3(worldBounds.size)), worldFrame);
            }
            placement = placement with { Box = boundsBox };
        }
        return placement;
    }
}
