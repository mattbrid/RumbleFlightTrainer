using System.Numerics;

namespace RumbleFlightTrainer;

internal enum HudLossKind { Separated, SeparatedIntact, Broken, SignalLost }
internal sealed record HudLossMark(HudPairGeometry[] Contacts, HudLossKind Kind, double TimeSeconds, HudBreak? Break = null);
internal sealed record HudBreak(StructureKey Key, string? Name, BreakAnalysis? Analysis, double TimeSeconds)
{
    public HudPlayerBreakContact? PlayerContact => Analysis?.PlayerContact;
}
internal sealed record HudPlayerBreakContact(HudContactPlacement? Placement, HudContactPatch? Patch, Vector3 CenterWorld,
    Vector3? RelativeVelocityWorld, float? NormalMetersPerSecond, bool LinearEstimate, bool EstimatedContact = false,
    int? PlayerColliderId = null, Vector3? NormalTowardPlayerWorld = null);

internal sealed record HudPairGeometry(
    ContactKey Pair, long? Tick, HudContactPatch[] Patches, HudContactPlacement? Placement);

internal sealed record HudNearbyCollider(int ColliderId, HudContactPlacement Placement);

internal readonly record struct HudPairFrame(
    HudPairGeometry Geometry, Vector3 PushWorld, bool HasImpulse)
{
    public HudContactPatch[] Patches => Geometry.Patches;
}

internal sealed record HudStructureFrame(
    StructureKey Key, HudPairFrame[] ActivePairs, Vector3 PushWorld,
    bool HasImpulse, bool PartialImpulse, HudLossMark[] Losses, IReadOnlyList<HudNearbyCollider>? NearbyGeometry = null)
{
    public bool Touching => ActivePairs.Length != 0;
}

internal sealed record FlightHudFrame(
    Quaternion BodyYaw, Vector3 PushWorld, HudStructureFrame[] Structures,
    int TouchingCount, int MeasuredCount, bool PartialImpulse, bool ObserverAvailable, long? GeometryTick,
    HudBreak[] Breaks)
{
    public HudBreak? RecentBreak => Breaks.LastOrDefault(broken => broken.PlayerContact is not null) ?? Breaks.LastOrDefault();

    public string? Outcome
    {
        get
        {
            var last = Structures.SelectMany(structure => structure.Losses)
                .MaxBy(loss => loss.Break?.TimeSeconds ?? loss.TimeSeconds);
            if (RecentBreak is { } recent && (recent.PlayerContact?.Placement is not null ||
                last is null || recent.TimeSeconds >= (last.Break?.TimeSeconds ?? last.TimeSeconds)))
                return FormatBreak(recent);
            if (last is null)
                return null;
            if (last.Break is { } broken)
                return FormatBreak(broken);
            return last.Kind == HudLossKind.SignalLost ? "Lost Contact · signal interrupted" : "Lost Contact";
        }
    }

    private static string FormatBreak(HudBreak item)
    {
        var match = item.Analysis?.Match;
        var description = DescribeBreak(match);
        var contact = item.PlayerContact;
        if (contact?.EstimatedContact == true)
            description += "\nEstimated contact";
        var normalSpeed = contact?.NormalMetersPerSecond ?? match?.Impact?.DisplayPoint?.ClosingMetersPerSecond;
        if (normalSpeed is not { } speed)
            return contact is { EstimatedContact: false } || match?.Impact is not null
                ? description + "\nNormal speed unavailable" : description;
        return description + FormattableString.Invariant($"\nNormal speed {speed:+0.0;-0.0;0.0} m/s") +
            ((contact?.LinearEstimate ?? match?.Impact?.LinearEstimate) == true ? " (linear estimate)" : string.Empty);
    }

    private static string DescribeBreak(BreakCandidate? match)
    {
        if (match?.IronBoots is { } boots)
            return boots.IsLowRelativeSpeed || match.PlayerDamage == 0
                ? FormattableString.Invariant($"Structure Broke · Iron Boots ({boots.Alignment.AngleDegrees:F0}°)")
                : FormattableString.Invariant($"Structure Broke · Iron Boots? ({boots.Alignment.AngleDegrees:F0}°)");

        string cause = match?.Event switch
        {
            "player_hit" or "player_hit_from_beneath" when match.DelayedPlayerHit => "after hitting you",
            "player_hit" or "player_hit_from_beneath" => "hit you",
            "jump_on_structure" when match.PlayerDamage is > 0 => "hit you",
            "jump_on_structure" => "jump contact",
            "structure_collision" => $"hit {match.CounterpartName ?? "another structure"}",
            "environment_collision" => $"hit {match.Environment}",
            _ => "cause unknown"
        };
        return $"Structure Broke · {cause}";
    }
}

internal sealed class FlightHudState
{
    private const int ImpulseWindowTicks = 4;
    internal const float DefaultContactHoldSeconds = 3f;

    private readonly int physicsBodyId;
    private readonly double contactHoldSeconds;
    private readonly Dictionary<StructureKey, Structure> structures = new();
    private readonly Dictionary<ContactKey, StructureKey> activePairs = new();
    private readonly Dictionary<int, int> endedGenerations = new();
    private readonly HashSet<(ContactKey Pair, StructureKey Structure, long Tick)> measuredTicks = new();
    private readonly List<HudBreak> breaks = new();
    private PhysicsSample? step;
    private Quaternion bodyYaw = Quaternion.Identity;
    private int? segment;
    private int touchingCount;
    private bool observerAvailable = true;

    public FlightHudState(int physicsBodyId, double contactHoldSeconds = DefaultContactHoldSeconds)
    {
        this.physicsBodyId = physicsBodyId;
        this.contactHoldSeconds = double.IsFinite(contactHoldSeconds) && contactHoldSeconds >= 0
            ? contactHoldSeconds : DefaultContactHoldSeconds;
    }

    private sealed class Structure
    {
        public Dictionary<ContactKey, Pair> Pairs { get; } = new();
        public HudLossMark? Loss { get; set; }
        public List<HudPairGeometry> EndingContacts { get; } = new();
        public long? EndTick { get; set; }
    }

    private sealed class Pair
    {
        public Pair(ContactKey contact) =>
            Geometry = new HudPairGeometry(contact, null, Array.Empty<HudContactPatch>(), null);

        public HudPairGeometry Geometry { get; set; }
        public Queue<(long Tick, Vector3 World)> Impulses { get; } = new(ImpulseWindowTicks);
    }

    public void Observe(TraceRecord observation)
    {
        if (segment != observation.Stamp.Segment)
            Reset(observation.Stamp.Segment);

        switch (observation)
        {
            case PhysicsSample sample:
                step = sample;
                bodyYaw = FlightContactPatches.Yaw(sample.Physics.BodyWorldPose.Rotation) ?? Quaternion.Identity;
                observerAvailable = sample.StructureTelemetry?.ContactObserverAvailable == true;
                measuredTicks.RemoveWhere(impulse => impulse.Tick <= sample.Tick - ImpulseWindowTicks);
                ConfirmContinuingLife(sample);
                break;
            case ContactObservation { Structure: { } structure, StructureSideProbe: false } contact:
                ObserveContact(contact, structure);
                break;
            case GameplayObservation { Event: "structure_destroyed" or "structure_pool_return", Structure: { } key } end:
                EndStructure(key, end);
                break;
        }
    }

    public FlightHudFrame Read(double now, IReadOnlyDictionary<StructureKey, List<HudNearbyCollider>>? nearby = null)
    {
        var result = new List<HudStructureFrame>(structures.Count);
        Vector3 total = Vector3.Zero;
        int measuredCount = 0;
        bool partial = false;
        long? geometryTick = null;

        foreach (var (key, structure) in structures)
        {
            var nearbyGeometry = nearby is not null && !HasEnded(key) ? nearby.GetValueOrDefault(key) : null;
            if (structure.Loss is { } loss && now >= loss.TimeSeconds + contactHoldSeconds)
                structure.Loss = null;
            if (structure.Pairs.Count == 0 && structure.Loss is null)
            {
                // Dictionary removal during enumeration is supported on .NET 6.
                structures.Remove(key);
                continue;
            }

            var pairs = new HudPairFrame[structure.Pairs.Count];
            int index = 0;
            Vector3 push = Vector3.Zero;
            bool measured = false;
            bool missing = false;
            foreach (var pair in structure.Pairs.Values)
            {
                ExpireImpulses(pair, step?.Tick);
                Vector3 pairSum = Vector3.Zero;
                foreach (var impulse in pair.Impulses)
                    pairSum += impulse.World;

                bool hasImpulse = pair.Impulses.Count > 0;
                pairs[index++] = new HudPairFrame(pair.Geometry, pairSum / ImpulseWindowTicks, hasImpulse);
                push += pairSum;
                measured |= hasImpulse;
                missing |= !hasImpulse;
                if (pair.Geometry.Tick is { } tick && (geometryTick is null || tick > geometryTick))
                    geometryTick = tick;
            }

            total += push;
            if (measured)
                measuredCount++;
            partial |= missing;
            result.Add(new HudStructureFrame(key, pairs, push / ImpulseWindowTicks, measured, missing,
                structure.Loss is { } retained ? new[] { retained } : Array.Empty<HudLossMark>(), nearbyGeometry));
        }

        if (nearby is not null)
            foreach (var (key, colliders) in nearby)
                if (!structures.ContainsKey(key) && !HasEnded(key))
                    result.Add(new HudStructureFrame(key, Array.Empty<HudPairFrame>(), Vector3.Zero,
                        false, false, Array.Empty<HudLossMark>(), colliders));

        breaks.RemoveAll(broken => now >= broken.TimeSeconds + contactHoldSeconds);

        return new FlightHudFrame(bodyYaw, total / ImpulseWindowTicks, result.ToArray(),
            touchingCount, measuredCount, partial, observerAvailable, geometryTick, breaks.ToArray());
    }

    private void Reset(int newSegment)
    {
        structures.Clear();
        activePairs.Clear();
        measuredTicks.Clear();
        step = null;
        bodyYaw = Quaternion.Identity;
        touchingCount = 0;
        breaks.Clear();
        segment = newSegment;
        observerAvailable = true;
    }

    private void ObserveContact(ContactObservation contact, StructureKey key)
    {
        long endTick = contact.PhysicsTick ?? step?.Tick ?? 0;
        bool active = activePairs.TryGetValue(contact.Pair, out var priorKey);
        if (contact.Phase is "exit" or "interrupted")
        {
            if (active && priorKey == key)
            {
                HudLossKind kind = contact.Phase == "exit" ? HudLossKind.Separated : HudLossKind.SignalLost;
                EndPair(key, contact.Pair, endTick, contact.Stamp.TimeSeconds, kind);
            }
            return;
        }

        if (contact.Phase is not ("enter" or "stay"))
            return;
        if (HasEnded(key))
            return;

        if (active && priorKey != key)
            EndPair(priorKey, contact.Pair, endTick, contact.Stamp.TimeSeconds, HudLossKind.SignalLost);

        var pair = BeginContact(key, contact.Pair);
        if (contact.Points.Length > 0)
            pair.Geometry = BuildGeometry(key, contact.Pair, contact.Stamp.Segment, contact.GeometryTick, contact.PlayerBodyId,
                contact.Points, contact.Frames, SameStep(contact.Impact?.First), SameStep(contact.Impact?.Second), step);

        if (contact.PhysicsTick is { } impulseTick && FlightContactPatches.PlayerImpulse(contact) is { } world &&
            measuredTicks.Add((contact.Pair, key, impulseTick)))
        {
            ExpireImpulses(pair, impulseTick);
            pair.Impulses.Enqueue((impulseTick, world));
        }
    }

    private Pair BeginContact(StructureKey key, ContactKey contact)
    {
        if (!structures.TryGetValue(key, out var structure))
            structures[key] = structure = new Structure();
        if (structure.Pairs.TryGetValue(contact, out var pair))
            return pair;

        if (structure.Pairs.Count == 0)
        {
            touchingCount++;
            structure.EndTick = null;
            structure.EndingContacts.Clear();
        }
        activePairs[contact] = key;
        return structure.Pairs[contact] = new Pair(contact);
    }

    private void EndPair(StructureKey key, ContactKey contact, long endTick, double now, HudLossKind kind)
    {
        var structure = structures[key];
        var pair = structure.Pairs[contact];
        if (structure.EndTick != endTick)
        {
            structure.EndTick = endTick;
            structure.EndingContacts.Clear();
        }
        structure.EndingContacts.Add(pair.Geometry);
        structure.Pairs.Remove(contact);
        activePairs.Remove(contact);
        if (structure.Pairs.Count != 0)
            return;

        touchingCount--;
        structure.Loss = new HudLossMark(structure.EndingContacts.ToArray(), kind, now);
        structure.EndingContacts.Clear();
    }

    private void EndStructure(StructureKey key, GameplayObservation end)
    {
        endedGenerations[key.InstanceId] = Math.Max(key.PoolGeneration, endedGenerations.GetValueOrDefault(key.InstanceId));
        bool broken = end.Event == "structure_destroyed";
        var explanation = broken ? new HudBreak(key, end.StructureName, end.Break, end.Stamp.TimeSeconds) : null;
        if (explanation is not null)
            breaks.Add(explanation);
        if (!structures.TryGetValue(key, out var structure))
            return;

        foreach (var contact in structure.Pairs.Keys.ToArray())
            EndPair(key, contact, end.LastPhysicsTick ?? step?.Tick ?? 0, end.Stamp.TimeSeconds,
                broken ? HudLossKind.Broken : HudLossKind.SignalLost);

        if (explanation is not null && structure.Loss is { } loss && end.Stamp.TimeSeconds - loss.TimeSeconds < contactHoldSeconds)
            structure.Loss = loss with { Kind = HudLossKind.Broken, Break = explanation, TimeSeconds = end.Stamp.TimeSeconds };
    }

    private bool HasEnded(StructureKey key) =>
        endedGenerations.TryGetValue(key.InstanceId, out int endedGeneration) && key.PoolGeneration <= endedGeneration;

    private void ConfirmContinuingLife(PhysicsSample sample)
    {
        if (sample.StructureTelemetry is not { } telemetry)
            return;

        foreach (var motion in telemetry.Structures)
        {
            if (!structures.TryGetValue(motion.Key, out var structure))
                continue;
            if (structure.Loss is { Kind: HudLossKind.Separated } loss && sample.Tick > structure.EndTick)
                structure.Loss = loss with { Kind = HudLossKind.SeparatedIntact };
        }
    }

    private HudPairGeometry BuildGeometry(StructureKey key, ContactKey pair, int targetSegment, long? tick,
        int? receiverId, ContactGeometry[] points, ContactFrames? frames,
        FixedMotionSample? receiverMotion, FixedMotionSample? structureMotion, PhysicsSample? sample)
    {
        var body = receiverId == physicsBodyId || frames is not null ? MainBodyAt(targetSegment, tick, sample) : null;
        var patches = FlightContactPatches.Build(points, body);
        HudContactPlacement? placement = null;
        if (body is { } origin && tick is not null && points.Length > 0)
        {
            Pose? receiverPose = receiverId == physicsBodyId ? sample!.Physics.BodyWorldPose : null;
            if (receiverPose is null && receiverMotion is { } motion &&
                motion.Tick == tick && motion.Segment == targetSegment && motion.Body.BodyId == receiverId)
                receiverPose = motion.Body.WorldPose;
            if (receiverPose is null && frames?.PlayerBody is { } callback && callback.BodyId == receiverId)
                receiverPose = callback.WorldPose;

            var player = HudPlayerCollider.Build(pair, targetSegment, receiverId, frames, receiverPose);
            var collider = frames is not null ?
                HudStructureCollider.Resolve(key, pair, targetSegment, tick, frames, structureMotion, sample) : null;
            placement = collider is { } observed
                ? observed.Place(origin, player, tick)
                : new HudContactPlacement(origin, player, null);
        }
        return new HudPairGeometry(pair, tick, patches, placement);
    }

    private static FixedMotionSample? SameStep(AgedMotionSample? motion) =>
        motion is { SameFixedStep: true } ? motion.Sample : null;

    private static HudBodyFrame? MainBodyAt(int targetSegment, long? geometryTick, PhysicsSample? sample)
    {
        if (sample is null || sample.Tick != geometryTick || sample.Stamp.Segment != targetSegment)
            return null;

        return HudBodyFrame.From(sample.Physics);
    }

    private static void ExpireImpulses(Pair pair, long? now)
    {
        if (now is { } tick)
            while (pair.Impulses.TryPeek(out var impulse) && impulse.Tick <= tick - ImpulseWindowTicks)
                pair.Impulses.Dequeue();
    }

}
