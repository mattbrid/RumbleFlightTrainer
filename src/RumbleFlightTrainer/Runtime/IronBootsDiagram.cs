using System.Numerics;
using static RumbleFlightTrainer.FlightContactPatches;

namespace RumbleFlightTrainer;

internal sealed record IronBootsDiagram(Vector3 PlaneNormal, Vector2[] Section, Vector2[] Player, Vector2[] Foot,
    Vector2 Contact, Vector2 Feet, Vector2 Origin, Vector2 VelocityTip, Vector2[] Arrowhead, Vector2[] Arc, Vector2 AngleLabel)
{
    private const float PlaneTolerance = 1e-5f;
    private const float DirectionTolerance = 1e-7f;
    private const float Width = 0.24f;
    private const float Height = 0.145f;

    public static IronBootsDiagram? Build(IronBootsApproach approach, Vector3? previousPlaneNormal = null)
    {
        if (FlightBoxPreview.Build(approach.Tick, approach.Box.Shape, approach.Box.Frame) is not { } box)
            return null;

        bool surfaceContact = approach.Contact is not null || approach.EstimatedContact;
        var anchor = approach.Contact is { } contact ? Vector(contact.PointWorld) : approach.Feet;
        var cutPoint = surfaceContact ? box.NearestSurfacePoint(anchor) : anchor;
        var velocity = Unit(approach.VrVelocity);
        var toOrigin = approach.StructureOrigin - approach.Feet;
        var originAxis = Unit(toOrigin);
        if (originAxis == Vector3.Zero)
            originAxis = Unit(box.Center - cutPoint);
        var normal = Plane(velocity, originAxis, previousPlaneNormal);
        var section = Slice(normal, out var right, out var up);
        bool drawMotion = velocity != Vector3.Zero;
        if (section.Count < 3 || surfaceContact && !ContactOnBoundary(section))
        {
            // If the angle plane only grazes the collider, use an inward section for contact inspection.
            normal = Plane(velocity, Unit(box.Center - cutPoint), previousPlaneNormal);
            section = Slice(normal, out right, out up);
            drawMotion = false;
        }
        if (section.Count < 3)
            return null;

        List<Vector2> Slice(Vector3 planeNormal, out Vector3 planeRight, out Vector3 planeUp)
        {
            var vertical = Vector3.UnitY - planeNormal * planeNormal.Y;
            if (vertical.LengthSquared() < DirectionTolerance * DirectionTolerance)
                vertical = Vector3.UnitZ - planeNormal * planeNormal.Z;
            planeUp = Vector3.Normalize(vertical);
            planeRight = Vector3.Cross(planeUp, planeNormal);
            return Intersect(box.Corners, cutPoint, planeNormal, planeRight, planeUp);
        }
        Vector2 Project(Vector3 delta) => new(Vector3.Dot(delta, right), Vector3.Dot(delta, up));

        var min = Vector2.Zero;
        var max = Vector2.Zero;
        foreach (var point in section)
        {
            min = Vector2.Min(min, point);
            max = Vector2.Max(max, point);
        }

        var feetPosition = Project(approach.Feet - cutPoint);
        var origin = Project(approach.StructureOrigin - cutPoint);
        var velocityDirection = drawMotion ? Vector2.Normalize(Project(velocity)) : Vector2.Zero;
        var tip = feetPosition + velocityDirection * (Vector2.Distance(min, max) * 0.63f);
        min = Vector2.Min(Vector2.Min(Vector2.Min(min, feetPosition), origin), tip);
        max = Vector2.Max(Vector2.Max(Vector2.Max(max, feetPosition), origin), tip);
        var playerOutline = ColliderSection(approach.Player, cutPoint, normal, right, up);
        var footOutline = ColliderSection(approach.Foot, cutPoint, normal, right, up);
        Include(playerOutline);
        Include(footOutline);
        void Include(Vector2[] outline)
        {
            foreach (var point in outline)
            {
                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
            }
        }
        float scale = MathF.Min(Width / (max.X - min.X), Height / (max.Y - min.Y));
        var center = (min + max) * 0.5f;
        Vector2 Fit(Vector2 point) => (point - center) * scale;

        var feet = Fit(feetPosition);
        var fitOrigin = Fit(origin);
        var fitTip = Fit(tip);
        var wing = new Vector2(-velocityDirection.Y, velocityDirection.X);
        var fork = fitTip - velocityDirection * 0.012f;
        Vector2[] arrowhead = drawMotion ? new[] { fork + wing * 0.006f, fitTip, fork - wing * 0.006f } : Array.Empty<Vector2>();

        var originRay = origin - feetPosition;
        var originDirection = originRay.LengthSquared() > DirectionTolerance * DirectionTolerance
            ? Vector2.Normalize(originRay) : Vector2.Zero;
        float side = velocityDirection.X * originDirection.Y - velocityDirection.Y * originDirection.X;
        float dot = Vector2.Dot(velocityDirection, originDirection);
        float span = MathF.Atan2(side, dot);
        if (MathF.Abs(side) < DirectionTolerance && dot < 0)
            span = wing.Y > 0 ? -MathF.PI : MathF.PI;

        float radius = Math.Clamp(Vector2.Distance(feet, fitOrigin) * 0.48f, 0.014f, 0.03f);
        const int arcSegments = 24;
        var arc = MathF.Abs(span) < DirectionTolerance ? Array.Empty<Vector2>() : new Vector2[arcSegments + 1];
        for (int i = 0; i < arc.Length; i++)
            arc[i] = feet + Rotate(velocityDirection, span * i / arcSegments) * radius;

        var labelDirection = MathF.Abs(span) < 0.35f
            ? Rotate(velocityDirection, span * 0.5f + 0.75f)
            : Rotate(velocityDirection, span * 0.5f);
        var label = feet + labelDirection * (radius + 0.026f);
        return new IronBootsDiagram(normal, section.Select(Fit).ToArray(), Array.ConvertAll(playerOutline, Fit),
            Array.ConvertAll(footOutline, Fit), Fit(Vector2.Zero), feet, fitOrigin, fitTip, arrowhead, arc,
            drawMotion ? label : feet + new Vector2(0, 0.025f));
    }

    private static Vector3 Unit(Vector3 value)
    {
        float length = value.Length();
        return float.IsFinite(length) && length > IronBootsAlignment.NormalizeEpsilon ? value / length : Vector3.Zero;
    }

    private static Vector3 Plane(Vector3 velocity, Vector3 toOrigin, Vector3? previous)
    {
        var normal = Vector3.Cross(velocity, toOrigin);
        if (normal.LengthSquared() < DirectionTolerance * DirectionTolerance)
        {
            var axis = toOrigin != Vector3.Zero ? toOrigin : velocity != Vector3.Zero ? velocity : Vector3.UnitY;
            if (previous is { } prior)
                normal = prior - axis * Vector3.Dot(prior, axis);
            if (normal.LengthSquared() < DirectionTolerance * DirectionTolerance)
                normal = Vector3.Cross(axis, Vector3.UnitY);
            if (normal.LengthSquared() < DirectionTolerance * DirectionTolerance)
                normal = Vector3.Cross(axis, Vector3.UnitX);
        }

        normal = Vector3.Normalize(normal);
        if (previous is { } last)
            return Vector3.Dot(last, normal) >= 0 ? normal : -normal;

        // Give the initial section a deterministic handedness independent of head rotation.
        var abs = Vector3.Abs(normal);
        float dominant = abs.Z >= abs.Y && abs.Z >= abs.X ? normal.Z : abs.X >= abs.Y ? normal.X : normal.Y;
        return dominant >= 0 ? normal : -normal;
    }

    private static List<Vector2> Intersect(Vector3[] corners, Vector3 feet, Vector3 normal, Vector3 right, Vector3 up)
    {
        var section = new List<Vector2>(6);
        Span<float> distances = stackalloc float[8];
        for (int i = 0; i < corners.Length; i++)
        {
            var delta = corners[i] - feet;
            distances[i] = Vector3.Dot(delta, normal);
            if (MathF.Abs(distances[i]) <= PlaneTolerance)
                Add(delta);
        }
        foreach (var (from, to) in FlightBoxPreview.Edges)
        {
            float start = distances[from];
            float end = distances[to];
            if (start > PlaneTolerance && end < -PlaneTolerance || start < -PlaneTolerance && end > PlaneTolerance)
                Add(Vector3.Lerp(corners[from] - feet, corners[to] - feet, start / (start - end)));
        }

        var center = Vector2.Zero;
        foreach (var point in section)
            center += point;
        if (section.Count > 0)
        {
            center /= section.Count;
            section.Sort((a, b) => VertexAngle(a - center).CompareTo(VertexAngle(b - center)));
        }
        return section;

        void Add(Vector3 delta)
        {
            var point = new Vector2(Vector3.Dot(delta, right), Vector3.Dot(delta, up));
            foreach (var existing in section)
                if (Vector2.DistanceSquared(point, existing) <= PlaneTolerance * PlaneTolerance)
                    return;
            section.Add(point);
        }

        static float VertexAngle(Vector2 point) => MathF.Atan2(point.Y, point.X);
    }

    private static bool ContactOnBoundary(List<Vector2> section)
    {
        for (int i = 0; i < section.Count; i++)
        {
            var point = section[i];
            var edge = section[(i + 1) % section.Count] - point;
            float t = Math.Clamp(Vector2.Dot(-point, edge) / edge.LengthSquared(), 0, 1);
            if ((point + edge * t).LengthSquared() <= PlaneTolerance * PlaneTolerance)
                return true;
        }
        return false;
    }

    private static Vector2[] ColliderSection(HudPlayerCollider? collider, Vector3 cutPoint,
        Vector3 normal, Vector3 right, Vector3 up)
    {
        if (collider is not { } player)
            return Array.Empty<Vector2>();

        // The nearest point of the central segment projects to an interior point when the plane cuts the capsule.
        var center = player.Center - cutPoint;
        float slope = Vector3.Dot(player.Axis, normal);
        float parameter = MathF.Abs(slope) > DirectionTolerance
            ? Math.Clamp(-Vector3.Dot(center, normal) / slope, -player.HalfSpan, player.HalfSpan) : 0;
        var axisPoint = center + player.Axis * parameter;
        float signedDistance = Vector3.Dot(axisPoint, normal);
        if (MathF.Abs(signedDistance) >= player.Radius)
            return Array.Empty<Vector2>();

        var seed = axisPoint - normal * signedDistance;
        var seed2D = new Vector2(Vector3.Dot(seed, right), Vector3.Dot(seed, up));
        float limit = Vector3.Distance(center, seed) + player.HalfSpan + player.Radius;
        float radiusSquared = player.Radius * player.Radius;
        var outline = new Vector2[64];
        for (int i = 0; i < outline.Length; i++)
        {
            var (sin, cos) = MathF.SinCos(MathF.Tau * i / outline.Length);
            var ray = right * cos + up * sin;
            float from = 0, to = limit;
            for (int iteration = 0; iteration < 16; iteration++)
            {
                float distance = (from + to) * 0.5f;
                var point = seed + ray * distance - center;
                var closest = player.Axis * Math.Clamp(Vector3.Dot(point, player.Axis), -player.HalfSpan, player.HalfSpan);
                if (Vector3.DistanceSquared(point, closest) <= radiusSquared)
                    from = distance;
                else
                    to = distance;
            }
            outline[i] = seed2D + new Vector2(cos, sin) * ((from + to) * 0.5f);
        }
        return outline;
    }

    private static Vector2 Rotate(Vector2 direction, float radians)
    {
        var (sin, cos) = MathF.SinCos(radians);
        return new Vector2(direction.X * cos - direction.Y * sin, direction.X * sin + direction.Y * cos);
    }
}
