using System.Numerics;
using static RumbleFlightTrainer.FlightContactPatches;

namespace RumbleFlightTrainer;

internal readonly record struct HudColliderFrame(Vector3 Origin, Vector3 X, Vector3 Y, Vector3 Z)
{
    public static HudColliderFrame From(AffineFrame frame) =>
        new(Vector(frame.Origin), Vector(frame.XAxis), Vector(frame.YAxis), Vector(frame.ZAxis));

    public static HudColliderFrame? From(Pose pose)
    {
        var q = new Quaternion(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W);
        var origin = Vector(pose.Position);
        if (!IsFinite(origin) || !float.IsFinite(q.LengthSquared()) || q.LengthSquared() < 1e-6f)
            return null;

        q = Quaternion.Normalize(q);
        return new HudColliderFrame(origin, Vector3.Transform(Vector3.UnitX, q),
            Vector3.Transform(Vector3.UnitY, q), Vector3.Transform(Vector3.UnitZ, q));
    }

    private Vector3 Direction(Vector3 local) => local.X * X + local.Y * Y + local.Z * Z;
    public Vector3 Point(Vector3 local) => Origin + Direction(local);
    public HudColliderFrame Compose(AffineFrame child) =>
        new(Point(Vector(child.Origin)), Direction(Vector(child.XAxis)), Direction(Vector(child.YAxis)), Direction(Vector(child.ZAxis)));
}

internal readonly record struct HudStructureCollider(ColliderShape Shape, HudColliderFrame Frame)
{
    public bool AllowsBoundsPreview => !FlightBakedWireframes.HasBakedOutline(Shape.MeshName);

    public HudContactPlacement Place(HudBodyFrame origin, HudPlayerCollider? player, long? tick) =>
        new(origin, player, FlightBoxPreview.Build(tick, Shape, Frame), HudMeshPreview.Build(Shape, Frame));

    public static HudStructureCollider? Resolve(StructureKey structure, ContactKey pair, int segment, long? tick,
        ContactFrames frames, FixedMotionSample? motion, PhysicsSample? step)
    {
        if (frames.StructureCollider is not { Definition: { } definition } collider ||
            definition.Stamp.Sequence != collider.DescriptorSequence || definition.Stamp.Segment != segment ||
            definition.Descriptor is not { } descriptor || descriptor.Structure != structure ||
            descriptor.ColliderId != pair.OtherColliderId)
            return null;

        // Use the observed callback placement for a child attached to a different rigidbody.
        HudColliderFrame? root = frames.StructureRootWorld is { } callbackRoot ? HudColliderFrame.From(callbackRoot) : null;
        if (descriptor.ReferenceBodyId is { } bodyId && descriptor.AttachedBodyId == bodyId &&
            (collider.RootToReferenceBodyOverride ?? descriptor.RootToReferenceBody) is { } rootToBody &&
            FixedPose(structure, bodyId, segment, tick, motion, step) is { } fixedPose)
            root = fixedPose.Compose(rootToBody);

        return root is { } rootWorld
            ? new HudStructureCollider(descriptor.Shape, rootWorld.Compose(collider.ColliderToRootOverride ?? descriptor.ColliderToRoot))
            : null;
    }

    private static HudColliderFrame? FixedPose(StructureKey key, int bodyId, int segment, long? tick,
        FixedMotionSample? motion, PhysicsSample? step)
    {
        if (motion?.Tick == tick && motion is { Segment: var sampleSegment, Body: var body } &&
            sampleSegment == segment && body.BodyId == bodyId)
            return HudColliderFrame.From(body.WorldPose);

        if (step is not null && step.Tick == tick && step.Stamp.Segment == segment &&
            step.StructureTelemetry is { } telemetry)
            foreach (var structure in telemetry.Structures)
                if (structure.Key == key && structure.RigidbodyId == bodyId)
                    return HudColliderFrame.From(structure.WorldPose);

        return null;
    }
}

internal sealed record HudMeshPreview(Vector3 Center, HudColliderFrame Offsets, Vec3[][]? Sections, MeshEdge[]? Edges)
{
    public static HudMeshPreview? Build(ColliderShape shape, HudColliderFrame frame)
    {
        if (shape is not { Kind: "mesh", Convex: true, MeshBounds: { } bounds } ||
            !IsFinite(frame.X) || !IsFinite(frame.Y) || !IsFinite(frame.Z))
            return null;

        var center = frame.Point(Vector(bounds.Center));
        if (!IsFinite(center))
            return null;
        var offsets = frame with { Origin = frame.Origin - center };
        if (shape.MeshEdges is { Length: >= 6 and <= FlightBakedWireframes.MaxEdges } edges &&
            edges.All(edge => edge.Start != edge.End && IsFinite(Vector(edge.Start)) && IsFinite(Vector(edge.End))))
            return new HudMeshPreview(center, offsets, null, edges);
        if (shape.MeshContours is { Length: > 0 and <= 4 } paths &&
            paths.All(path => path is { Length: >= 4 and <= 33 } && path[0] == path[^1] &&
                path.All(point => IsFinite(Vector(point)))))
            return new HudMeshPreview(center, offsets, paths, null);
        return null;
    }
}

// A sphere is the same surface with a zero-length central segment.
internal sealed record HudPlayerCollider(Vector3 Center, Vector3 Axis, float HalfSpan, float Radius)
{
    public float SurfaceDistance(Vector3 worldPoint)
    {
        return MathF.Abs(Vector3.Distance(worldPoint, AxisPoint(worldPoint)) - Radius);
    }

    public Vector3? PointOnSurface(Vector3 point)
    {
        var axisPoint = AxisPoint(point);
        var radial = point - axisPoint;
        float length = radial.Length();
        return length > 1e-6f ? axisPoint + radial * (Radius / length) : null;
    }

    private Vector3 AxisPoint(Vector3 point) =>
        Center + Axis * Math.Clamp(Vector3.Dot(point - Center, Axis), -HalfSpan, HalfSpan);

    public static HudPlayerCollider? Build(ContactKey pair, int segment, int? receiverId,
        ContactFrames? frames, Pose? receivingPose)
    {
        if (receiverId is null || frames?.PlayerCollider is not { Definition: { } definition } collider ||
            definition.Stamp.Sequence != collider.DescriptorSequence || definition.Stamp.Segment != segment ||
            definition.Descriptor is not { Structure: null } descriptor ||
            descriptor.ColliderId != pair.PlayerColliderId || descriptor.AttachedBodyId != receiverId ||
            descriptor.ReferenceBodyId != receiverId ||
            (collider.RootToReferenceBodyOverride ?? descriptor.RootToReferenceBody) is not { } rootToBody ||
            receivingPose is not { } pose || HudColliderFrame.From(pose) is not { } body)
            return null;

        var world = body.Compose(rootToBody).Compose(collider.ColliderToRootOverride ?? descriptor.ColliderToRoot);
        return Build(descriptor.Shape, world);
    }

    public static HudPlayerCollider? Build(ColliderShape shape, HudColliderFrame world)
    {
        if (shape is not { Center: { } center, Radius: > 0 })
            return null;

        var centerWorld = world.Point(Vector(center));
        var scale = new Vector3(world.X.Length(), world.Y.Length(), world.Z.Length());
        if (!IsFinite(centerWorld) || !IsFinite(scale) || scale.X <= 0 || scale.Y <= 0 || scale.Z <= 0)
            return null;

        var x = world.X / scale.X;
        var y = world.Y / scale.Y;
        var z = world.Z / scale.Z;
        const float transformTolerance = 0.001f;
        if (MathF.Abs(Vector3.Dot(x, y)) > transformTolerance ||
            MathF.Abs(Vector3.Dot(x, z)) > transformTolerance ||
            MathF.Abs(Vector3.Dot(y, z)) > transformTolerance)
            return null;

        var dimensions = shape switch
        {
            { Kind: "capsule", Direction: 0, Height: > 0 } => (x, scale.X, scale.Y, scale.Z),
            { Kind: "capsule", Direction: 1, Height: > 0 } => (y, scale.Y, scale.Z, scale.X),
            { Kind: "capsule", Direction: 2, Height: > 0 } => (z, scale.Z, scale.X, scale.Y),
            { Kind: "sphere" } when MathF.Abs(scale.X - scale.Y) <= transformTolerance => (y, scale.Y, scale.X, scale.Z),
            _ => default
        };
        var (axis, axialScale, radialScale, otherRadialScale) = dimensions;
        // Only reconstruct a round cross-section when the measured radial scales agree.
        if (axialScale == 0 || MathF.Abs(radialScale - otherRadialScale) > transformTolerance)
            return null;

        float radius = shape.Radius.Value * radialScale;
        float height = shape.Kind == "sphere" ? 2 * radius : shape.Height!.Value * axialScale;
        return float.IsFinite(height) && float.IsFinite(radius)
            ? new HudPlayerCollider(centerWorld, axis, MathF.Max(0, height * 0.5f - radius), radius) : null;
    }
}

internal sealed record HudContactPlacement(HudBodyFrame Origin, HudPlayerCollider? Player, HudBoxPreview? Box,
    HudMeshPreview? Mesh = null)
{
    public const float Scale = 0.085f;

    public Vector3 Offset(Vector3 worldPoint) => (worldPoint - Origin.Center) * Scale;

    public Vector3? MarkerPoint(HudContactPatch patch)
    {
        if (Player is not { } player || patch.WorldPoint is not { } representative)
            return null;

        float maxTolerance = 0;
        foreach (var point in patch.Points)
        {
            float tolerance = FlightBoxPreview.FaceToleranceMetres + MathF.Abs(point.Separation);
            if (!float.IsFinite(tolerance) || Box?.Contains(point) == false ||
                !(player.SurfaceDistance(Vector(point.PointWorld)) <= tolerance))
                return null;
            maxTolerance = MathF.Max(tolerance, maxTolerance);
        }

        // The measured span stays in place. Snap the dot only when the representative is near the hitbox.
        return player.SurfaceDistance(representative) <= maxTolerance ? player.PointOnSurface(representative) : null;
    }
}
