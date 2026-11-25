using System.Reflection;
using System.Text.Json;

namespace RumbleFlightTrainer;

internal static class FlightBakedWireframes
{
    public const int MaxEdges = 96;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly Lazy<Dictionary<string, Wireframe>> Assets = new(Load);

    private sealed record Wireframe(int FormatVersion, string GameVersion, string MeshName, string CookingOptions,
        MeshLocalBounds Bounds, Vec3 Scale, MeshEdge[] Edges);

    public static bool HasBakedOutline(string? meshName) => meshName is
        "Ball" or "Structure_Ball" or "Structures_Ball" or
        "SmallRock" or "Strucutre_SmallRock" or "LargeRock" or "Structure_Boulder" or "Structure_Disc";

    public static MeshEdge[]? Match(ColliderShape shape, string gameVersion, Vec3 scale)
    {
        if (shape is not { Kind: "mesh", Convex: true, MeshName: { } name, MeshBounds: { } bounds } ||
            !Assets.Value.TryGetValue(name, out var asset) ||
            asset.GameVersion != gameVersion || asset.CookingOptions != shape.CookingOptions ||
            !Near(asset.Bounds.Center, bounds.Center) || !Near(asset.Bounds.Size, bounds.Size) || !Near(asset.Scale, scale))
            return null;
        return asset.Edges;
    }

    private static Dictionary<string, Wireframe> Load()
    {
        var assets = new Dictionary<string, Wireframe>(StringComparer.Ordinal);
        var assembly = Assembly.GetExecutingAssembly();
        foreach (string name in new[] { "Ball", "SmallRock", "LargeRock", "Disk" })
        {
            using var stream = assembly.GetManifestResourceStream($"RumbleFlightTrainer.Wireframes.{name}.json");
            if (stream is not null && JsonSerializer.Deserialize<Wireframe>(stream, Json) is
                { FormatVersion: 1, Edges.Length: >= 6 and <= MaxEdges } asset)
                assets.Add(asset.MeshName, asset);
        }
        return assets;
    }

    private static bool Near(Vec3 left, Vec3 right)
    {
        const float tolerance = 0.0001f;
        return MathF.Abs(left.X - right.X) <= tolerance && MathF.Abs(left.Y - right.Y) <= tolerance &&
            MathF.Abs(left.Z - right.Z) <= tolerance;
    }
}
