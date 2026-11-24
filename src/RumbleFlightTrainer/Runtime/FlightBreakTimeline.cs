using System.Numerics;

namespace RumbleFlightTrainer;

internal sealed class FlightBreakTimeline
{
    // Saved Gym delays run from the same tick through 13 ticks (about 0.27 seconds).
    internal const double MatchSeconds = 0.30;
    private const double MaxSampleAgeSeconds = 0.10;
    private const int MaxObservations = 4096;
    private readonly short localActor;
    private readonly bool probesAvailable;
    private readonly Queue<TraceRecord> history = new();
    private int? segment;
    private long ironBootsContactSequence;
    private double droppedThrough = double.NegativeInfinity;

    public FlightBreakTimeline(CaptureMetadata capture)
    {
        localActor = capture.LocalActorNumber;
        probesAvailable = capture.StructureCollisionProbeInstalled != false && capture.EnvironmentCollisionProbeInstalled != false;
    }

    public BreakAnalysis? Observe(TraceRecord record)
    {
        if (segment != record.Stamp.Segment)
        {
            segment = record.Stamp.Segment;
            history.Clear();
            ironBootsContactSequence = 0;
            droppedThrough = double.NegativeInfinity;
        }

        if (record is IronBootsContactStarted start)
            ironBootsContactSequence = start.Stamp.Sequence;

        double oldest = record.Stamp.TimeSeconds - MatchSeconds;
        while (history.TryPeek(out var first) && first.Stamp.TimeSeconds < oldest)
            history.Dequeue();

        if (record is GameplayObservation { Event: "structure_destroyed", Structure: { } key } end)
            return Explain(end, key, oldest);

        if (record is PhysicsSample or ContactObservation { Structure: not null } or
            StructureImpactObservation or StructureImpactGap or CombatHealthObservation ||
            record is GameplayObservation { Event: "player_hit" or "player_hit_from_beneath" or "jump_on_structure" or
                "structure_collision" or "structure_kill_requested" or "structure_pool_return" })
        {
            if (history.Count == MaxObservations)
                droppedThrough = history.Dequeue().Stamp.TimeSeconds;
            history.Enqueue(record);
        }
        return null;
    }

    private BreakAnalysis Explain(GameplayObservation end, StructureKey key, double oldest)
    {
        var relevant = history.OfType<GameplayObservation>().Where(row => row.Structure == key || row.OtherStructure == key);
        var kill = relevant.LastOrDefault(row => row.Event == "structure_kill_requested");
        long? impactTick = kill?.LastPhysicsTick ?? end.LastPhysicsTick;
        var candidates = relevant.Where(row =>
                row.Event == "structure_collision" ||
                row.Actor == localActor && (row.Event is "player_hit" or "player_hit_from_beneath" or "jump_on_structure"))
            .Select(row => Candidate(row, key, impactTick)).ToList();
        long? directCollision = end.CollisionSequence ?? kill?.CollisionSequence;
        foreach (var hit in history.OfType<StructureImpactObservation>().Where(row => row.Environment is not null && row.First.Structure == key))
        {
            if (hit.Stamp.Sequence == directCollision ||
                impactTick is not null && hit.PhysicsTick == impactTick &&
                (kill is null || hit.Stamp.Sequence < kill.Stamp.Sequence) && IsWall(hit))
                candidates.Add(EnvironmentCandidate(hit));
        }

        bool complete = probesAvailable && oldest > droppedThrough &&
            !history.OfType<StructureImpactGap>().Any(gap => gap.Structure == key || gap.OtherStructure == key ||
                (gap.Structure is null && gap.OtherStructure is null));
        return new BreakAnalysis(kill?.Stamp, candidates.ToArray(), complete, directCollision);
    }

    private BreakCandidate Candidate(GameplayObservation combat, StructureKey key, long? impactTick)
    {
        bool structurePair = combat.Event == "structure_collision";
        bool breakingFirst = combat.Structure == key;
        StructureKey? counterpart = breakingFirst ? combat.OtherStructure : combat.Structure;
        if (counterpart == key)
            counterpart = null;
        BreakImpact? impact = null;
        float? linearSpeed = null;
        IronBootsBreakEvidence? ironBoots = null;
        IronBootsApproach? jumpDiagram = null;
        int? playerDamage = null;
        if (combat.LastPhysicsTick is { } tick)
        {
            if (structurePair)
            {
                var registration = MatchRegistration(combat, key, counterpart, tick);
                if (registration is not null)
                {
                    impact = StructureImpact(combat, key, tick, registration);
                    if (registration.Motion.First?.Sample.Frozen != true && registration.Motion.Second?.Sample.Frozen != true)
                        linearSpeed = FiniteSpeed(registration.RelativeLinearVelocity);
                }
                if (linearSpeed is null && combat.Combat is { SecondStructure: { } second } snapshots)
                    linearSpeed = FiniteSpeed(FlightContactPatches.Vector(snapshots.FirstStructure.Positional) -
                        FlightContactPatches.Vector(second.Positional));
            }
            else
            {
                if (combat.CombatTimestamp is { } timestamp)
                    playerDamage = history.OfType<CombatHealthObservation>().Where(row =>
                            row.Structure == key && row.Event == combat.Event && row.Tick == tick && row.CombatTimestamp == timestamp)
                        .Select(row => row.Damage).Max();
                var contact = MatchPlayerContact(key, tick);
                impact = contact is null ? null : PlayerImpact(contact, tick);
                if (contact?.Impact?.Second?.Sample.Frozen != true)
                    linearSpeed = FiniteSpeed(contact?.RelativeLinearVelocity);
                if (combat.Combat is { } snapshots)
                {
                    var gamePlayer = snapshots.PlayerPhysicsBody?.Positional;
                    var fixedPlayer = history.OfType<PhysicsSample>().LastOrDefault(row => row.Tick == tick)?.Physics.LinearVelocityWorld;
                    if ((gamePlayer ?? fixedPlayer) is { } player)
                        linearSpeed ??= FiniteSpeed(FlightContactPatches.Vector(snapshots.FirstStructure.Positional) -
                            FlightContactPatches.Vector(player));
                }
                if (combat.Event == "jump_on_structure" && playerDamage is not > 0 && combat.Structure == key && tick == impactTick &&
                    combat.Stamp.Sequence > ironBootsContactSequence)
                {
                    if (combat.IronBoots is { MatchesDirection: true } evidence)
                        ironBoots = evidence;
                    jumpDiagram = combat.JumpDiagram;
                }
            }
        }
        return new BreakCandidate(combat.Stamp, combat.LastPhysicsTick, combat.Event, combat.Actor,
            structurePair ? counterpart : null, structurePair ? (breakingFirst ? combat.Detail : combat.StructureName) : null,
            combat.CombatTimestamp, combat.Combat, impact, linearSpeed, IronBoots: ironBoots, JumpDiagram: jumpDiagram,
            PlayerDamage: playerDamage);
    }

    private ContactObservation? MatchPlayerContact(StructureKey key, long tick)
    {
        // The processed event can precede the local collision callback within the same fixed tick.
        var pairs = history.OfType<ContactObservation>().Where(row => row.Structure == key && row.PhysicsTick == tick &&
            row.Phase is "enter" or "stay")
            .GroupBy(row => row.Pair).Take(2).ToArray();
        return pairs.Length == 1 ? pairs[0].Last() : null;
    }

    private static BreakImpact? PlayerImpact(ContactObservation contact, long tick)
    {
        if (contact.OtherBodyId is not { } structureBody)
            return null;
        return MeasureImpact(contact.Stamp, tick, "collision_callback", structureBody, contact.PlayerBodyId, false, true,
            contact.Impact?.Second, contact.Impact?.First, contact.ReportedPointCount,
            contact.Points.Select(point => new ImpactPoint(point.PointWorld, point.NormalTowardPlayer, point.Separation)));
    }

    private StructureImpactObservation? MatchRegistration(GameplayObservation combat, StructureKey key, StructureKey? counterpart, long tick)
    {
        if (counterpart is null || counterpart == key)
            return null;

        var registrations = history.OfType<StructureImpactObservation>().Where(row =>
            row.Environment is null && row.Stamp.Sequence < combat.Stamp.Sequence && row.PhysicsTick == tick &&
            (row.First.Structure == key && row.Second.Structure == counterpart ||
             row.Second.Structure == key && row.First.Structure == counterpart)).Take(2).ToArray();
        return registrations.Length == 1 ? registrations[0] : null;
    }

    private static BreakImpact? StructureImpact(GameplayObservation combat, StructureKey key, long tick, StructureImpactObservation registration)
    {
        bool breakingFirst = registration.First.Structure == key;
        var breaking = breakingFirst ? registration.First : registration.Second;
        var other = breakingFirst ? registration.Second : registration.First;
        if (breaking.BodyId is not { } structureBody || other.BodyId is not { } otherBody)
            return null;
        return MeasureImpact(registration.Stamp, tick, registration.Motion.ObservationPhase, structureBody, otherBody,
            true, registration.NormalConvention == "toward_first", breakingFirst ? registration.Motion.First : registration.Motion.Second,
            breakingFirst ? registration.Motion.Second : registration.Motion.First, registration.ReportedPointCount,
            registration.Points, negateNormal: breakingFirst, gamePoint: combat.Combat?.CollisionPoint);
    }

    private static bool IsWall(StructureImpactObservation hit) => hit.Points is { Length: > 0 } &&
        hit.Points.All(point => MathF.Abs(point.NormalWorld.Y) < 0.5f &&
            FlightContactPatches.Vector(point.NormalWorld).LengthSquared() > 1e-8f);

    private static BreakCandidate EnvironmentCandidate(StructureImpactObservation hit)
    {
        string environment = IsWall(hit) ? "map wall" : "map surface";
        BreakImpact? impact = null;
        if (hit.PhysicsTick is { } tick && hit.First.BodyId is { } bodyId)
            impact = MeasureImpact(hit.Stamp, tick, hit.Motion.ObservationPhase, bodyId, hit.Second.BodyId,
                false, hit.NormalConvention == "toward_first", hit.Motion.First, hit.Motion.Second, hit.ReportedPointCount,
                hit.Points, negateNormal: true, staticCounterpart: hit.Environment!.StaticCollider);

        float? linear = hit.Motion.First?.Sample.Frozen == true ? null : FiniteSpeed(hit.RelativeLinearVelocity);
        return new BreakCandidate(hit.Stamp, hit.PhysicsTick, "environment_collision", null, null, hit.Second.Name,
            null, null, impact, linear, environment);
    }

    private static BreakImpact MeasureImpact(TraceStamp stamp, long tick, string phase, int breakingId, int? otherId,
        bool otherIsStructure, bool normalKnown, AgedMotionSample? breaking, AgedMotionSample? other, int pointCount,
        IEnumerable<ImpactPoint> points, bool negateNormal = false, Vec3? gamePoint = null, bool staticCounterpart = false)
    {
        var speeds = new List<ImpactSpeed>();
        ImpactSpeed? atGamePoint = null;
        if (UsableImpact(breaking, breakingId, tick, stamp, true) && (staticCounterpart ||
            otherId is { } id && UsableImpact(other, id, tick, stamp, otherIsStructure) &&
            breaking!.Sample.FixedGameTimeSeconds == other!.Sample.FixedGameTimeSeconds))
        {
            float nearestDistance = float.PositiveInfinity;
            foreach (var point in points)
            {
                if (AtPoint(point.PointWorld, point.NormalWorld) is not { } speed)
                    continue;
                speeds.Add(speed);
                if (gamePoint is { } gameContact)
                {
                    float distance = Vector3.DistanceSquared(FlightContactPatches.Vector(gameContact),
                        FlightContactPatches.Vector(point.PointWorld));
                    if (distance < nearestDistance)
                    {
                        nearestDistance = distance;
                        atGamePoint = speed;
                    }
                }
            }
        }
        return new BreakImpact(stamp, tick, phase, breakingId, otherId, pointCount,
            breaking?.AgeSeconds, other?.AgeSeconds, speeds.ToArray(), atGamePoint);

        ImpactSpeed? AtPoint(Vec3 worldPoint, Vec3 worldNormal)
        {
            var position = FlightContactPatches.Vector(worldPoint);
            var relative = PointVelocity(breaking!.Sample.Body, position) -
                (staticCounterpart ? Vector3.Zero : PointVelocity(other!.Sample.Body, position));
            if (FiniteSpeed(relative) is not { } magnitude)
                return null;
            var normal = FlightContactPatches.Vector(worldNormal);
            float? closing = normalKnown && FlightContactPatches.IsFinite(normal) && normal.LengthSquared() > 1e-8f
                ? Vector3.Dot(relative, Vector3.Normalize(negateNormal ? -normal : normal)) : null;
            return new ImpactSpeed(worldPoint, magnitude, closing);
        }
    }

    private static float? FiniteSpeed(Vec3? velocity) =>
        velocity is { } value ? FiniteSpeed(FlightContactPatches.Vector(value)) : null;

    private static float? FiniteSpeed(Vector3 velocity)
    {
        float speed = velocity.Length();
        return float.IsFinite(speed) ? speed : null;
    }

    private static Vector3 PointVelocity(BodyKinematics body, Vector3 point) =>
        FlightContactPatches.Vector(body.LinearVelocityWorld) + Vector3.Cross(
            FlightContactPatches.Vector(body.AngularVelocityWorld), point - FlightContactPatches.Vector(body.WorldCenterOfMass));

    private static bool UsableImpact(AgedMotionSample? motion, int id, long tick, TraceStamp stamp, bool structure) =>
        motion is { SameFixedStep: true, AgeSeconds: >= 0 and <= MaxSampleAgeSeconds } &&
        Usable(motion.Sample, id, tick, stamp, structure);

    private static bool Usable(FixedMotionSample? sample, int id, long tick, TraceStamp stamp, bool structure) =>
        sample is not null && sample.Body.BodyId == id && sample.Segment == stamp.Segment && sample.Tick == tick &&
        sample.SimulationMode == "FixedUpdate" && (structure ? sample.Frozen == false : sample.Frozen != true) &&
        stamp.TimeSeconds - sample.SampleSeconds is >= 0 and <= MaxSampleAgeSeconds;
}
