using System.Numerics;
using static RumbleFlightTrainer.FlightContactPatches;

namespace RumbleFlightTrainer;

internal static class FlightMeshContours
{
    private const int SamplesPerSection = 32;

    private static readonly (Vector3 Across, Vector3 Up, Vector3 Normal)[] Planes =
    {
        (Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ),
        (Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX),
        (Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY)
    };

    public static Vec3[][] Sample(MeshLocalBounds bounds, Func<Vector3, Vector3, Vector3?> raycast)
    {
        var center = Vector(bounds.Center);
        var size = Vector(bounds.Size);
        if (!IsFinite(center) || !IsFinite(size) || size.X < 0 || size.Y < 0 || size.Z < 0)
            return Array.Empty<Vec3[]>();

        // Twice the source half extents puts each ray's start outside its section's bounds.
        var reach = size + new Vector3(0.05f);
        // A shallow offset on the widest section makes the disk's thickness visible.
        var thin = size.X <= size.Y && size.X <= size.Z ? Vector3.UnitX
            : size.Y <= size.Z ? Vector3.UnitY : Vector3.UnitZ;
        float thickness = Vector3.Dot(size, thin);
        float faceOffset = thickness < 0.35f * MathF.Max(size.X, MathF.Max(size.Y, size.Z)) ? thickness * 0.3f : 0;
        var contours = new List<Vec3[]>(Planes.Length + 1);
        foreach (var (across, up, normal) in Planes)
        {
            if (normal == thin && faceOffset > 0)
            {
                AddSection(center + normal * faceOffset, across, up);
                AddSection(center - normal * faceOffset, across, up);
            }
            else
                AddSection(center, across, up);
        }
        return contours.ToArray();

        void AddSection(Vector3 section, Vector3 across, Vector3 up)
        {
            var path = new Vec3[SamplesPerSection + 1];
            for (int i = 0; i < SamplesPerSection; i++)
            {
                float angle = i * (2 * MathF.PI / SamplesPerSection);
                var radial = MathF.Cos(angle) * across + MathF.Sin(angle) * up;
                if (raycast(section + reach * radial, section) is not { } hit || !IsFinite(hit))
                    return;
                path[i] = new Vec3(hit.X, hit.Y, hit.Z);
            }
            path[^1] = path[0];
            contours.Add(path);
        }
    }
}
