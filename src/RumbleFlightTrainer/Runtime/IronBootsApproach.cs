using System.Numerics;
using static RumbleFlightTrainer.FlightContactPatches;

namespace RumbleFlightTrainer;

internal sealed record IronBootsApproach(long Tick, int Segment, double SampleSeconds, StructureKey Structure,
    Vector3 Feet, Vector3 StructureOrigin, Vector3 VrVelocity, HudStructureCollider Box, IronBootsAlignment? Alignment,
    HudPlayerCollider? Player = null, ContactGeometry? Contact = null, HudPlayerCollider? Foot = null)
{
    private const double MaxSampleAgeSeconds = 0.15;

    public bool IsCurrent(long? tick, int segment, double now) =>
        Tick == tick && Segment == segment && now - SampleSeconds is >= 0 and <= MaxSampleAgeSeconds;

    public static ContactGeometry? FindFootContact(ContactObservation contact, float centerHeight, Vector3 feet)
    {
        if (contact.Phase is not ("enter" or "stay") || contact.PlayerHitbox is not ("foot" or "capsule"))
            return null;

        var impulse = PlayerImpulse(contact) ?? Vector3.Zero;
        ContactGeometry? best = null;
        float bestDistance = float.PositiveInfinity;
        foreach (var point in contact.Points)
        {
            var normal = Vector(point.NormalTowardPlayer);
            float length = normal.Length();
            bool loaded = float.IsFinite(point.Separation) &&
                (point.Separation <= 0 || Vector3.Dot(impulse, normal) > 0);
            if (!loaded || !float.IsFinite(length) || length <= IronBootsAlignment.NormalizeEpsilon ||
                contact.PlayerHitbox == "capsule" && (point.PointWorld.Y >= centerHeight || normal.Y < 0.5f * length))
                continue;

            float distance = Vector3.DistanceSquared(Vector(point.PointWorld), feet);
            if (distance < bestDistance)
            {
                best = point;
                bestDistance = distance;
            }
        }
        return best;
    }
}

internal readonly record struct IronBootsDisplay(IronBootsApproach Sample, bool Retained)
{
    public static IronBootsDisplay? Read(IronBootsApproach sample, int segment, bool active) =>
        sample.Segment == segment ? new IronBootsDisplay(sample, !active) : null;
}

internal readonly record struct IronBootsAlignment(float AngleDegrees, float SpeedTowardOrigin,
    float DownDot, float OriginDot, float DownThreshold, float OriginThreshold)
{
    public const float NormalizeEpsilon = 1e-5f;

    public bool MeetsDownCutoff => DownDot > DownThreshold;
    public bool MeetsOriginCutoff => OriginDot >= OriginThreshold;

    public static IronBootsAlignment? Evaluate(Vector3 vrVelocity, Vector3 feet, Vector3 structureOrigin,
        float downThreshold, float originThreshold)
    {
        var toOrigin = structureOrigin - feet;
        float speed = vrVelocity.Length();
        float distance = toOrigin.Length();
        if (!IsFinite(feet) || !IsFinite(structureOrigin) ||
            !float.IsFinite(speed) || speed <= NormalizeEpsilon ||
            !float.IsFinite(distance) || distance <= NormalizeEpsilon ||
            !float.IsFinite(downThreshold) || !float.IsFinite(originThreshold))
            return null;

        var direction = vrVelocity / speed;
        var originDirection = toOrigin / distance;
        float downDot = -direction.Y;
        float originDot = Vector3.Dot(direction, originDirection);
        float angle = MathF.Atan2(Vector3.Cross(direction, originDirection).Length(), originDot) * (180f / MathF.PI);
        return new IronBootsAlignment(angle, speed * originDot, downDot, originDot, downThreshold, originThreshold);
    }
}

internal readonly record struct IronBootsBreakEvidence(IronBootsAlignment Alignment, float RelativeLinearSpeed)
{
    // Trainer heuristic. The game's jump validator supplies its own directional thresholds.
    private const float LowRelativeSpeedCutoff = 2.0f;

    public bool MatchesDirection => Alignment.DownDot > 0 && Alignment.MeetsDownCutoff && Alignment.MeetsOriginCutoff;
    public bool IsLowRelativeSpeed => RelativeLinearSpeed < LowRelativeSpeedCutoff;

    public static IronBootsBreakEvidence? Measure(CombatMeasurement motion, Vector3 feet, Vector3 origin,
        float downThreshold, float originThreshold)
    {
        if (motion.PlayerVrBody is not { } player)
            return null;

        var velocity = Vector(player.Positional);
        var alignment = IronBootsAlignment.Evaluate(velocity, feet, origin, downThreshold, originThreshold);
        float relativeSpeed = Vector3.Distance(velocity, Vector(motion.FirstStructure.Positional));
        return alignment is { } direction && float.IsFinite(relativeSpeed)
            ? new IronBootsBreakEvidence(direction, relativeSpeed) : null;
    }
}
