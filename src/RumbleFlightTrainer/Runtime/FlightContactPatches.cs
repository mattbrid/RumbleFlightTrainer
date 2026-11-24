using System.Numerics;

namespace RumbleFlightTrainer;

internal readonly record struct HudBodyFrame(Vector3 Center, Quaternion Yaw)
{
    public static HudBodyFrame? From(PhysicsState physics)
    {
        var center = FlightContactPatches.Vector(physics.WorldCenterOfMass);
        var yaw = FlightContactPatches.Yaw(physics.BodyWorldPose.Rotation);
        return FlightContactPatches.IsFinite(center) && yaw is { } rotation ? new HudBodyFrame(center, rotation) : null;
    }
}
internal sealed record HudContactPatch(ContactGeometry[] Points, Vector3? BodyDirection, Vector3? BodyNormal,
    Vector3? WorldPoint);

internal static class FlightContactPatches
{
    private const float SameFaceNormalDot = 0.9962f;
    private const float SameFacePlaneDistance = 0.015f;
    private const float MinimumOffsetSquared = 0.000001f;

    private readonly record struct Point(ContactGeometry Geometry, Vector3 Position, Vector3 Normal);

    public static Vector3? PlayerImpulse(ContactObservation contact)
    {
        if (contact.PairImpulseRaw is not { } raw || contact.PlayerIsSecondInNativePair is not { } second)
            return null;
        var impulse = Vector(raw);
        // The v0.2.0 Gym drill verified this sign on upward-facing cube contacts.
        return IsFinite(impulse) ? (second ? -impulse : impulse) : null;
    }

    public static Vector3? StructureRelativeLinearVelocity(ContactObservation contact)
    {
        if (contact.RelativeLinearVelocity is not { } raw || contact.PlayerIsSecondInNativePair is not { } second)
            return null;
        // The recorded header vector is native first minus second. Point normals already face the player.
        var relative = Vector(raw);
        return IsFinite(relative) ? (second ? relative : -relative) : null;
    }

    public static HudContactPatch[] Build(ContactGeometry[] geometry, HudBodyFrame? body)
    {
        if (geometry.Length == 0)
            return Array.Empty<HudContactPatch>();

        var points = geometry.Select(point =>
            new Point(point, Vector(point.PointWorld), UnitNormal(point.NormalTowardPlayer))).ToArray();
        // A fixed order makes ambiguous group membership and the chosen plane independent of callback order.
        Array.Sort(points, Compare);

        var faces = new List<List<Point>>();
        foreach (var point in points)
        {
            var face = faces.Find(face => face.TrueForAll(other => SameFace(point, other)));
            if (face is null)
                faces.Add(face = new List<Point>());
            face.Add(point);
        }

        return faces.Select(face => Mark(face, body)).ToArray();
    }

    private static HudContactPatch Mark(List<Point> face, HudBodyFrame? body)
    {
        Vector3? direction = null;
        Vector3? normal = null;
        Vector3? worldPoint = null;
        if (body is { } frame && IsFinite(face[0].Position))
        {
            var inverse = Quaternion.Conjugate(frame.Yaw);
            var point = ClosestPoint(face, frame.Center);
            var offset = point - frame.Center;
            if (IsFinite(point))
                worldPoint = point;
            if (IsFinite(offset) && offset.LengthSquared() >= MinimumOffsetSquared)
                direction = Vector3.Transform(offset, inverse);
            if (face[0].Normal != Vector3.Zero)
                normal = Vector3.Transform(face[0].Normal, inverse);
        }
        return new HudContactPatch(face.Select(point => point.Geometry).ToArray(), direction, normal, worldPoint);
    }

    private static Vector3 ClosestPoint(List<Point> face, Vector3 center)
    {
        var origin = face[0].Position;
        if (face.Count == 1)
            return origin;
        if (face.Count == 2)
            return OnSegment(center, origin, face[1].Position);

        // Flatten small measurement deviations to the face plane for a bounded convex footprint.
        var normal = face[0].Normal;
        Vector3 projectedCenter = center - Vector3.Dot(center - origin, normal) * normal;
        Span<Vector3> vertices = stackalloc Vector3[face.Count];
        for (int i = 0; i < vertices.Length; i++)
        {
            var position = face[i].Position;
            vertices[i] = position - Vector3.Dot(position - origin, normal) * normal;
        }

        var closest = origin;
        float bestDistance = Vector3.DistanceSquared(projectedCenter, origin);
        for (int i = 0; i < vertices.Length; i++)
        for (int j = i + 1; j < vertices.Length; j++)
        {
            // Triangles from one vertex cover the convex hull. If outside it, an edge is nearest.
            if (i > 0 && InsideTriangle(projectedCenter, origin, vertices[i], vertices[j], normal))
                return projectedCenter;

            var candidate = OnSegment(projectedCenter, vertices[i], vertices[j]);
            float distance = Vector3.DistanceSquared(projectedCenter, candidate);
            if (distance < bestDistance)
            {
                closest = candidate;
                bestDistance = distance;
            }
        }
        return closest;
    }

    private static bool InsideTriangle(Vector3 point, Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
    {
        float area = Vector3.Dot(Vector3.Cross(b - a, c - a), normal);
        if (MathF.Abs(area) <= 1e-8f)
            return false;
        float side = MathF.Sign(area);
        return side * Vector3.Dot(Vector3.Cross(b - a, point - a), normal) >= 0 &&
               side * Vector3.Dot(Vector3.Cross(c - b, point - b), normal) >= 0 &&
               side * Vector3.Dot(Vector3.Cross(a - c, point - c), normal) >= 0;
    }

    private static Vector3 OnSegment(Vector3 point, Vector3 a, Vector3 b)
    {
        var span = b - a;
        float lengthSquared = span.LengthSquared();
        return lengthSquared > 1e-10f
            ? a + Math.Clamp(Vector3.Dot(point - a, span) / lengthSquared, 0f, 1f) * span : a;
    }

    private static bool SameFace(Point a, Point b)
    {
        var span = a.Position - b.Position;
        return Vector3.Dot(a.Normal, b.Normal) >= SameFaceNormalDot &&
               MathF.Abs(Vector3.Dot(span, a.Normal)) <= SameFacePlaneDistance &&
               MathF.Abs(Vector3.Dot(span, b.Normal)) <= SameFacePlaneDistance;
    }

    private static int Compare(Point a, Point b)
    {
        int pointOrder = CompareVector(a.Position, b.Position);
        return pointOrder == 0 ? CompareVector(a.Normal, b.Normal) : pointOrder;
    }

    private static int CompareVector(Vector3 a, Vector3 b)
    {
        int order = a.X.CompareTo(b.X);
        if (order != 0)
            return order;
        order = a.Y.CompareTo(b.Y);
        return order == 0 ? a.Z.CompareTo(b.Z) : order;
    }

    public static Quaternion? Yaw(Quat rotation)
    {
        var quaternion = new Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W);
        float lengthSquared = quaternion.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared < MinimumOffsetSquared)
            return null;

        var forward = Vector3.Transform(Vector3.UnitZ, Quaternion.Normalize(quaternion));
        return Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan2(forward.X, forward.Z));
    }

    private static Vector3 UnitNormal(Vec3 normal)
    {
        var value = Vector(normal);
        float lengthSquared = value.LengthSquared();
        return float.IsFinite(lengthSquared) && lengthSquared >= MinimumOffsetSquared
            ? value / MathF.Sqrt(lengthSquared) : Vector3.Zero;
    }

    public static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    public static Vector3 Vector(Vec3 value) => new(value.X, value.Y, value.Z);
}
