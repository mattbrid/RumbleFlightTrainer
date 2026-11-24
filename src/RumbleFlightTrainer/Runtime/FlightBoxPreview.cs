using System.Numerics;
using static RumbleFlightTrainer.FlightContactPatches;

namespace RumbleFlightTrainer;

internal sealed record HudBoxPreview(long? GeometryTick, Vector3 Center, Vector3[] Corners,
    Vector3 HalfSize, HudColliderFrame Frame, Matrix4x4 WorldToBox)
{
    public bool Contains(ContactGeometry point) =>
        SurfaceDistance(Vector(point.PointWorld)) <= FlightBoxPreview.FaceToleranceMetres + MathF.Abs(point.Separation);

    public float SurfaceDistance(Vector3 worldPoint)
    {
        var offset = worldPoint - Center;
        var local = Vector3.Transform(offset, WorldToBox);
        var nearest = Vector3.Clamp(local, -HalfSize, HalfSize);
        if (nearest != local)
            return Vector3.Distance(offset, Frame.Point(nearest));

        var distances = HalfSize - Vector3.Abs(local);
        return MathF.Min(distances.X * Frame.X.Length(),
            MathF.Min(distances.Y * Frame.Y.Length(), distances.Z * Frame.Z.Length()));
    }
}

internal static class FlightBoxPreview
{
    public const float FaceToleranceMetres = 0.02f;

    public static HudBoxPreview? Build(long? geometryTick, ColliderShape shape, HudColliderFrame box)
    {
        if (shape is not { Kind: "box", Center: { } center, Size: { } size })
            return null;

        var halfSize = Vector(size) * 0.5f;
        if (!IsFinite(halfSize) || halfSize.X <= 0 || halfSize.Y <= 0 || halfSize.Z <= 0 || !IsFinite(Vector(center)))
            return null;

        var boxCenterWorld = box.Point(Vector(center));
        var centeredBox = box with { Origin = Vector3.Zero };
        if (!IsFinite(boxCenterWorld) || !TryInverse(centeredBox, out var worldToBox))
            return null;

        var corners = new Vector3[8];
        for (int i = 0; i < corners.Length; i++)
        {
            var offset = new Vector3((i & 1) == 0 ? -halfSize.X : halfSize.X,
                (i & 2) == 0 ? -halfSize.Y : halfSize.Y, (i & 4) == 0 ? -halfSize.Z : halfSize.Z);
            corners[i] = boxCenterWorld + centeredBox.Point(offset);
        }

        return new HudBoxPreview(geometryTick, boxCenterWorld, corners, halfSize, centeredBox, worldToBox);
    }

    private static bool TryInverse(HudColliderFrame box, out Matrix4x4 inverse)
    {
        inverse = default;
        var (_, x, y, z) = box;
        if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z))
            return false;
        const float orthogonalTolerance = 0.001f;
        var unitX = Vector3.Normalize(x);
        var unitY = Vector3.Normalize(y);
        var unitZ = Vector3.Normalize(z);
        return MathF.Abs(Vector3.Dot(unitX, unitY)) <= orthogonalTolerance &&
            MathF.Abs(Vector3.Dot(unitX, unitZ)) <= orthogonalTolerance &&
            MathF.Abs(Vector3.Dot(unitY, unitZ)) <= orthogonalTolerance &&
            Matrix4x4.Invert(new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1), out inverse);
    }
}
