using System.Numerics;

namespace RumbleFlightTrainer;

internal enum HudLossKind { Separated, SeparatedIntact, Broken, SignalLost }
internal sealed record HudLossMark(HudPairGeometry[] Contacts, HudLossKind Kind, double TimeSeconds, HudBreak? Break = null);
internal sealed record HudBreak(StructureKey Key, string? Name, BreakAnalysis? Analysis, double TimeSeconds);

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
    HudBreak? RecentBreak = null)
{
    public string? Outcome
    {
        get
        {
            var last = Structures.SelectMany(structure => structure.Losses)
                .MaxBy(loss => loss.Break?.TimeSeconds ?? loss.TimeSeconds);
            if (RecentBreak is { } recent && (last is null || recent.TimeSeconds >= (last.Break?.TimeSeconds ?? last.TimeSeconds)))
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
        if (match?.IronBoots is { } boots)
            return boots.IsLowRelativeSpeed
                ? FormattableString.Invariant($"Structure Broke · Iron Boots ({boots.Alignment.AngleDegrees:F0}°)")
                : FormattableString.Invariant($"Structure Broke · Iron Boots? ({boots.Alignment.AngleDegrees:F0}°|{boots.RelativeLinearSpeed:F1}m/s)");

        string cause = match?.Event switch
        {
            "player_hit" or "player_hit_from_beneath" => "hit you",
            "jump_on_structure" => "jump contact",
            "structure_collision" => $"hit {match.CounterpartName ?? "another structure"}",
            "environment_collision" => $"hit {match.Environment}",
            _ => "cause unknown"
        };
        if (match is null)
            return $"Structure Broke · {cause}";

        var impact = match.Impact;
        var point = impact?.GameContactSpeed ?? impact?.Points
            .MaxBy(point => (MathF.Max(0, point.ClosingMetersPerSecond ?? 0), point.RelativeMetersPerSecond));
        if (point is not null)
            return $"Structure Broke · {cause} · {point.RelativeMetersPerSecond:F1} m/s";
        if (match.LinearImpactMetersPerSecond is { } linear)
            return $"Structure Broke · {cause} · {linear:F1} m/s linear";
        return $"Structure Broke · {cause}";
    }
}

internal sealed class FlightHudState
{
    private const int ImpulseWindowTicks = 4;
    private const double MultipleContactLossSeconds = 3;
    private const double BreakWithoutContactSeconds = 3;

    private readonly int physicsBodyId;
    private readonly Dictionary<StructureKey, Structure> structures = new();
    private readonly Dictionary<ContactKey, StructureKey> activePairs = new();
    private readonly Dictionary<int, int> endedGenerations = new();
    private readonly HashSet<(ContactKey Pair, StructureKey Structure, long Tick)> measuredTicks = new();
    private PhysicsSample? step;
    private Quaternion bodyYaw = Quaternion.Identity;
    private int? segment;
    private int touchingCount;
    private long contactStarts;
    private long? lossTick;
    private int structuresAtFirstLoss;
    private bool observerAvailable = true;
    private HudBreak? recentBreak;

    public FlightHudState(int physicsBodyId) => this.physicsBodyId = physicsBodyId;

    private sealed class Structure
    {
        public Dictionary<ContactKey, Pair> Pairs { get; } = new();
        public List<Loss> Losses { get; } = new();
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

    private sealed record Loss(HudLossMark Mark, long ContactStarts, double EarliestClear, long EndTick);

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
            case ContactObservation { Structure: { } structure } contact:
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
            PruneLosses(structure, now, nearby is not null, nearbyGeometry is not null);
            if (structure.Pairs.Count == 0 && structure.Losses.Count == 0)
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
                structure.Losses.Select(loss => loss.Mark).ToArray(), nearbyGeometry));
        }

        if (nearby is not null)
            foreach (var (key, colliders) in nearby)
                if (!structures.ContainsKey(key) && !HasEnded(key))
                    result.Add(new HudStructureFrame(key, Array.Empty<HudPairFrame>(), Vector3.Zero,
                        false, false, Array.Empty<HudLossMark>(), colliders));

        if (recentBreak is not null && now - recentBreak.TimeSeconds >= BreakWithoutContactSeconds)
            recentBreak = null;

        return new FlightHudFrame(bodyYaw, total / ImpulseWindowTicks, result.ToArray(),
            touchingCount, measuredCount, partial, observerAvailable, geometryTick, recentBreak);
    }

    private void PruneLosses(Structure structure, double now, bool live, bool nearby)
    {
        if (!live)
        {
            structure.Losses.RemoveAll(loss => contactStarts > loss.ContactStarts && now >= loss.EarliestClear);
            return;
        }

        if (!nearby || structure.Pairs.Count > 0)
            structure.Losses.Clear();
        else if (structure.Losses.Count > 1)
            structure.Losses.RemoveRange(0, structure.Losses.Count - 1);
    }

    private void Reset(int newSegment)
    {
        structures.Clear();
        activePairs.Clear();
        measuredTicks.Clear();
        step = null;
        bodyYaw = Quaternion.Identity;
        touchingCount = 0;
        contactStarts = 0;
        lossTick = null;
        segment = newSegment;
        observerAvailable = true;
        recentBreak = null;
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
            contactStarts++;
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

        if (lossTick != endTick)
        {
            lossTick = endTick;
            structuresAtFirstLoss = touchingCount;
        }

        bool multiple = Math.Max(touchingCount, structuresAtFirstLoss) > 1;
        touchingCount--;
        structure.Losses.Add(new Loss(new HudLossMark(structure.EndingContacts.ToArray(), kind, now),
            contactStarts, now + (multiple ? MultipleContactLossSeconds : 0), endTick));
        structure.EndingContacts.Clear();
    }

    private void EndStructure(StructureKey key, GameplayObservation end)
    {
        endedGenerations[key.InstanceId] = Math.Max(key.PoolGeneration, endedGenerations.GetValueOrDefault(key.InstanceId));
        bool broken = end.Event == "structure_destroyed";
        var explanation = broken ? new HudBreak(key, end.StructureName, end.Break, end.Stamp.TimeSeconds) : null;
        if (explanation is not null)
            recentBreak = explanation;
        if (!structures.TryGetValue(key, out var structure))
            return;

        foreach (var contact in structure.Pairs.Keys.ToArray())
            EndPair(key, contact, end.LastPhysicsTick ?? step?.Tick ?? 0, end.Stamp.TimeSeconds,
                broken ? HudLossKind.Broken : HudLossKind.SignalLost);

        if (explanation is null)
            return;
        for (int i = 0; i < structure.Losses.Count; i++)
        {
            var loss = structure.Losses[i];
            structure.Losses[i] = loss with { Mark = loss.Mark with { Kind = HudLossKind.Broken, Break = explanation } };
        }
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
            for (int i = 0; i < structure.Losses.Count; i++)
            {
                var loss = structure.Losses[i];
                if (loss.Mark.Kind == HudLossKind.Separated && sample.Tick > loss.EndTick)
                    structure.Losses[i] = loss with { Mark = loss.Mark with { Kind = HudLossKind.SeparatedIntact } };
            }
        }
    }

    private HudPairGeometry BuildGeometry(StructureKey key, ContactKey pair, int targetSegment, long? tick,
        int? receiverId, ContactGeometry[] points, ContactFrames? frames,
        FixedMotionSample? receiverMotion, FixedMotionSample? structureMotion, PhysicsSample? sample)
    {
        var body = MainBodyAt(targetSegment, tick, sample);
        var patches = FlightContactPatches.Build(points, receiverId == physicsBodyId || frames is not null ? body : null);
        HudContactPlacement? placement = null;
        if (body is { } origin && tick is not null && points.Length > 0 && frames is not null)
        {
            Pose? receiverPose = receiverId == physicsBodyId ? sample!.Physics.BodyWorldPose : null;
            if (receiverPose is null && receiverMotion is { } motion &&
                motion.Tick == tick && motion.Segment == targetSegment && motion.Body.BodyId == receiverId)
                receiverPose = motion.Body.WorldPose;
            if (receiverPose is null && frames.PlayerBody is { } callback && callback.BodyId == receiverId)
                receiverPose = callback.WorldPose;

            var player = HudPlayerCollider.Build(pair, targetSegment, receiverId, frames, receiverPose);
            var collider = HudStructureCollider.Resolve(key, pair, targetSegment, tick, frames, structureMotion, sample);
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

        var center = FlightContactPatches.Vector(sample.Physics.WorldCenterOfMass);
        var yaw = FlightContactPatches.Yaw(sample.Physics.BodyWorldPose.Rotation);
        return FlightContactPatches.IsFinite(center) && yaw is { } rotation ? new HudBodyFrame(center, rotation) : null;
    }

    private static void ExpireImpulses(Pair pair, long? now)
    {
        if (now is { } tick)
            while (pair.Impulses.TryPeek(out var impulse) && impulse.Tick <= tick - ImpulseWindowTicks)
                pair.Impulses.Dequeue();
    }

}
