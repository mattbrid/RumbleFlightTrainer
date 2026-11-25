using System.Numerics;
using static RumbleFlightTrainer.FlightContactPatches;

namespace RumbleFlightTrainer;

internal sealed record IronBootsApproach(long Tick, int Segment, double SampleSeconds, StructureKey Structure,
    Vector3 Feet, Vector3 StructureOrigin, Vector3 VrVelocity, HudStructureCollider Box, IronBootsAlignment? Alignment,
    HudPlayerCollider? Player = null, ContactGeometry? Contact = null, HudPlayerCollider? Foot = null,
    bool EstimatedContact = false)
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
            if (!IsLoaded(point, impulse) || !float.IsFinite(length) || length <= IronBootsAlignment.NormalizeEpsilon ||
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

    public static bool HasLoadedContact(ContactObservation contact)
    {
        var impulse = PlayerImpulse(contact) ?? Vector3.Zero;
        return contact.Points.Any(point => IsLoaded(point, impulse));
    }

    private static bool IsLoaded(ContactGeometry point, Vector3 impulse) =>
        IsFinite(Vector(point.PointWorld)) && float.IsFinite(point.Separation) &&
        (point.Separation <= 0 || Vector3.Dot(impulse, Vector(point.NormalTowardPlayer)) > 0);
}

internal sealed class IronBootsContactGate
{
    public const float DefaultVerticalVelocityThreshold = 1f;

    private readonly float threshold;
    private readonly Dictionary<ContactKey, StructureKey> pairs = new();
    private readonly HashSet<StructureKey> footContacts = new();
    private Attempt? latest;

    private readonly record struct Attempt(StructureKey Structure, long Tick, bool HasFootContact, bool PendingJump);

    public IronBootsContactGate(float threshold) => this.threshold = threshold;

    public bool IsCurrentFootContact(StructureKey structure) =>
        latest is { HasFootContact: true } attempt && attempt.Structure == structure;

    public bool Allows(StructureKey structure, float? verticalVelocity) => IsCurrentFootContact(structure) &&
        verticalVelocity is { } velocity && float.IsFinite(velocity) && velocity < threshold;

    public bool ObserveContact(ContactKey pair, StructureKey structure, long tick, bool foot)
    {
        if (pairs.TryGetValue(pair, out var previous) && previous != structure)
            End(pair);

        bool firstContact = !pairs.ContainsValue(structure);
        pairs[pair] = structure;
        if (firstContact && IsPendingJump(structure, tick))
        {
            // The game may process a jump before the local observer sees the same touch.
            footContacts.Add(structure);
            latest = latest!.Value with { PendingJump = false };
            return false;
        }

        bool firstFootContact = foot && footContacts.Add(structure);
        return (firstContact || firstFootContact) && Start(structure, tick, firstFootContact, false);
    }

    public bool ObserveJump(StructureKey structure, long tick)
    {
        if (footContacts.Contains(structure) || IsPendingJump(structure, tick))
            return false;

        bool observed = pairs.ContainsValue(structure);
        if (observed)
            footContacts.Add(structure);
        return Start(structure, tick, true, !observed);
    }

    public void End(ContactKey pair)
    {
        if (pairs.Remove(pair, out var structure) && !pairs.ContainsValue(structure))
            footContacts.Remove(structure);
    }

    public void Reset()
    {
        pairs.Clear();
        footContacts.Clear();
        latest = null;
    }

    private bool IsPendingJump(StructureKey structure, long tick) =>
        latest is { PendingJump: true } attempt && attempt.Structure == structure && attempt.Tick == tick;

    private bool Start(StructureKey structure, long tick, bool foot, bool pendingJump)
    {
        latest = new Attempt(structure, tick, foot, pendingJump);
        return true;
    }
}

internal readonly record struct IronBootsDisplay(IronBootsApproach Sample, bool Retained)
{
    public const float DefaultLastContactHoldSeconds = 5f;

    public static IronBootsDisplay? Read(IronBootsApproach sample, int segment, double now, double? contactEndedAt,
        double holdSeconds = DefaultLastContactHoldSeconds) =>
        sample.Segment != segment || contactEndedAt is { } ended && now >= ended + holdSeconds
            ? null : new IronBootsDisplay(sample, contactEndedAt.HasValue);

    public static IronBootsApproach? AfterBreak(GameplayObservation destruction, IronBootsApproach? current)
    {
        if (destruction.Break?.Match is not { Event: "jump_on_structure", Tick: { } tick } jump ||
            destruction.Structure is not { } structure ||
            jump.JumpDiagram is null && jump.IronBoots is null)
            return current;

        bool currentSegment = current?.Segment == destruction.Stamp.Segment;
        if (currentSegment && current is { } latest &&
            (latest.Tick > tick || latest.Structure != structure && latest.SampleSeconds > jump.Stamp.TimeSeconds))
            return current;

        var measured = currentSegment && current is { Contact: not null } contact &&
            contact.Structure == structure && contact.Tick == tick ? contact : null;
        if (jump.JumpDiagram is not { } sample)
            return measured;

        return measured is null ? sample : sample with
        {
            Box = measured.Box, Player = measured.Player ?? sample.Player,
            Foot = measured.Foot, Contact = measured.Contact, EstimatedContact = false
        };
    }
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
