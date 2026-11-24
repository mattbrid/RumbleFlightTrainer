using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using static RumbleFlightTrainer.FlightRuntime;

namespace RumbleFlightTrainer;

internal sealed partial class StructureTelemetry
{
    private const float HudRangeMetres = 2.5f;

    private Il2CppReferenceArray<Collider>? hudOverlap;
    private readonly Dictionary<StructureKey, List<HudNearbyCollider>> hudNearby = new();
    private readonly Dictionary<(StructureKey Key, int ColliderId), HudMeshShape> hudMeshes = new();
    private readonly HashSet<StructureKey> destroyedHudKeys = new();
    private bool hudOverlapFailed;

    private sealed record HudMeshShape(ColliderShape Base, Vec3 Scale, ColliderShape Outline);

    public IReadOnlyDictionary<StructureKey, List<HudNearbyCollider>> ReadNearbyHudGeometry()
    {
        hudNearby.Clear();
        var center = body.worldCenterOfMass;
        var origin = new HudBodyFrame(FlightContactPatches.Vector(ToVec3(center)), System.Numerics.Quaternion.Identity);

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

            // The baked wireframe belongs to the structure's primary collision mesh.
            var primary = structure.Collider;
            if (primary != null && collider != primary && primary.TryCast<MeshCollider>() is { } mesh)
            {
                var asset = mesh.sharedMesh;
                if (asset != null && FlightBakedWireframes.HasBakedOutline(asset.name))
                    continue;
            }

            AddHudOutline(KeyFor(structure), collider, origin);
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
                    AddHudOutline(entry.Key, collider, origin);
            }
        }

        foreach (var identity in hudMeshes.Keys)
            if (!hudNearby.ContainsKey(identity.Key))
                hudMeshes.Remove(identity);

        return hudNearby;
    }

    public HudNearbyCollider? ReadHudPlayerReference()
    {
        var collider = controller.PlayerPhysics.pillBodyCollider;
        if (!ColliderActive(collider))
            return null;

        var frame = HudColliderFrame.From(Frame(ColliderPhysicsFrame(collider, collider.attachedRigidbody)));
        var player = HudPlayerCollider.Build(ReadShape(collider), frame);
        var origin = new HudBodyFrame(FlightContactPatches.Vector(ToVec3(body.worldCenterOfMass)),
            System.Numerics.Quaternion.Identity);
        return player is null ? null : new HudNearbyCollider(collider.GetInstanceID(), new HudContactPlacement(origin, player, null));
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

        if (!hudNearby.TryGetValue(key, out var colliders))
            hudNearby[key] = colliders = new List<HudNearbyCollider>();
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
        if (placement.Box is not null || placement.Mesh is not null)
            colliders.Add(new HudNearbyCollider(colliderId, placement));
    }
}
