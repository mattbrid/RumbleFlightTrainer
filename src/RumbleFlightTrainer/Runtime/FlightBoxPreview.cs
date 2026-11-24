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

    public Vector3 NearestSurfacePoint(Vector3 worldPoint)
    {
        var local = Vector3.Transform(worldPoint - Center, WorldToBox);
        var point = Vector3.Clamp(local, -HalfSize, HalfSize);
        if (point == local)
        {
            var distances = (HalfSize - Vector3.Abs(local)) *
                new Vector3(Frame.X.Length(), Frame.Y.Length(), Frame.Z.Length());
            if (distances.X <= distances.Y && distances.X <= distances.Z)
                point.X = MathF.CopySign(HalfSize.X, local.X);
            else if (distances.Y <= distances.Z)
                point.Y = MathF.CopySign(HalfSize.Y, local.Y);
            else
                point.Z = MathF.CopySign(HalfSize.Z, local.Z);
        }
        return Center + Frame.Point(point);
    }
}

internal static class FlightBoxPreview
{
    public const float FaceToleranceMetres = 0.02f;

    private static readonly (int From, int To)[] EdgesByCorner =
    {
        (0, 1), (2, 3), (4, 5), (6, 7),
        (0, 2), (1, 3), (4, 6), (5, 7),
        (0, 4), (1, 5), (2, 6), (3, 7)
    };

    public static ReadOnlySpan<(int From, int To)> Edges => EdgesByCorner;

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
