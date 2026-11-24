using System.Numerics;

namespace RumbleFlightTrainer;

internal sealed class FlightBreakTimeline
{
    // Saved Gym delays run from the same tick through 13 ticks (about 0.27 seconds).
    internal const double MatchSeconds = 0.30;
    private const double DelayedPlayerHitSeconds = 0.45;
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

        double oldest = record.Stamp.TimeSeconds - DelayedPlayerHitSeconds;
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

    private BreakAnalysis Explain(GameplayObservation end, StructureKey key, double delayedOldest)
    {
        double oldest = end.Stamp.TimeSeconds - MatchSeconds;
        var relevant = history.OfType<GameplayObservation>().Where(row => row.Structure == key || row.OtherStructure == key);
        var kill = relevant.LastOrDefault(row => row.Event == "structure_kill_requested" && row.Stamp.TimeSeconds >= oldest);
        long? impactTick = kill?.LastPhysicsTick ?? end.LastPhysicsTick;
        var combat = relevant.Where(row => row.Event == "structure_collision" || row.Actor == localActor &&
            row.Structure == key && (row.Event is "player_hit" or "player_hit_from_beneath" or "jump_on_structure"));
        var candidates = combat.Where(row => row.Stamp.TimeSeconds >= oldest)
            .Select(row => Candidate(row, key, impactTick, oldest)).ToList();
        long? directCollision = end.CollisionSequence ?? kill?.CollisionSequence;
        foreach (var hit in history.OfType<StructureImpactObservation>().Where(row => row.Environment is not null && row.First.Structure == key))
        {
            if (hit.Stamp.Sequence == directCollision ||
                hit.Stamp.TimeSeconds >= oldest && impactTick is not null && hit.PhysicsTick == impactTick &&
                (kill is null || hit.Stamp.Sequence < kill.Stamp.Sequence) && IsWall(hit))
                candidates.Add(EnvironmentCandidate(hit));
        }

        BreakCandidate? directPlayerCombat = null;
        if (directCollision is null && end.PlayerCombatEvent is { } kind && end.CombatTimestamp is { } timestamp)
        {
            var source = combat.LastOrDefault(row => row.Structure == key && row.Event == kind &&
                row.CombatTimestamp == timestamp);
            directPlayerCombat = Candidate(source ?? end with { Event = kind, Actor = localActor }, key, impactTick, delayedOldest);
        }

        bool complete = HasCompleteCollisionHistory(key, oldest);
        if (directCollision is null && directPlayerCombat is null && candidates.Count == 0 &&
            FindDelayedPlayerHit(end, key, combat) is { } delayedHit)
            candidates.Add(Candidate(delayedHit, key, impactTick, delayedOldest) with { DelayedPlayerHit = true });

        var analysis = new BreakAnalysis(kill?.Stamp, candidates.ToArray(), complete, directCollision,
            DirectPlayerCombat: directPlayerCombat);
        var match = analysis.Match;
        if (directCollision is not null || match is { Event: "structure_collision" or "environment_collision" } ||
            match?.PlayerContact is { Placement: not null, NormalMetersPerSecond: not null })
            return analysis;

        // A physical touch can be retained even when processed events do not identify the break's cause.
        var observed = match?.PlayerContact;
        var atDestruction = (impactTick ?? match?.Tick) is { } tick ? MatchPlayerContact(key, tick, oldest) : null;
        if (atDestruction is null && history.OfType<ContactObservation>().LastOrDefault(row =>
            row.Structure == key && row.Reason == "structure_destroyed") is { GeometryTick: { } lastTick } closed)
            atDestruction = history.OfType<ContactObservation>().LastOrDefault(row => row.Structure == key &&
                row.Stamp.TimeSeconds >= oldest && row.Pair == closed.Pair && row.PhysicsTick == lastTick &&
                row.Phase is "enter" or "stay" && row.Points.Length > 0);
        if (atDestruction is { PhysicsTick: { } contactTick } contact)
        {
            var step = history.OfType<PhysicsSample>().LastOrDefault(row => row.Tick == contactTick);
            var atBreak = CapturePlayerBreakContact(key, contactTick, contact, PlayerImpact(contact, contactTick),
                step, contactTick == match?.Tick ? match?.Combat : null);
            if (atBreak is { NormalMetersPerSecond: not null } || observed is null)
                observed = atBreak;
        }
        var solePlayerCandidate = candidates.Count == 1 && candidates[0].Actor == localActor ? candidates[0] : null;
        var estimate = end.PlayerContactEstimate;
        if (estimate?.NormalMetersPerSecond is null)
            estimate = (match ?? solePlayerCandidate)?.PlayerContactEstimate ?? estimate;
        return analysis with { ObservedPlayerContact = observed?.Placement is not null ? observed : estimate ?? observed };
    }

    private bool HasCompleteCollisionHistory(StructureKey key, double oldest) => probesAvailable && oldest > droppedThrough &&
        !history.OfType<StructureImpactGap>().Any(gap => gap.Stamp.TimeSeconds >= oldest &&
            (gap.Structure == key || gap.OtherStructure == key || (gap.Structure is null && gap.OtherStructure is null)));

    private GameplayObservation? FindDelayedPlayerHit(GameplayObservation end, StructureKey key,
        IEnumerable<GameplayObservation> combat)
    {
        var latest = combat.LastOrDefault(row => row.Stamp.Sequence < end.Stamp.Sequence);
        if (latest is not { } hit || hit.Event is not ("player_hit" or "player_hit_from_beneath") ||
            !HasCompleteCollisionHistory(key, hit.Stamp.TimeSeconds))
            return null;

        // The delayed hit remains a plausible explanation only if no other impact follows it before destruction.
        bool laterImpact = history.OfType<StructureImpactObservation>().Any(row => AfterHit(row, row.PhysicsTick) &&
            (row.First.Structure == key || row.Second.Structure == key)) ||
            history.OfType<ContactObservation>().Any(row => AfterHit(row, row.PhysicsTick) && row.Structure == key && row.Phase == "enter" &&
                row.PhysicsTick > hit.LastPhysicsTick);
        return laterImpact ? null : hit;

        // The stream publishes a break after the callbacks for its destruction step.
        bool AfterHit(TraceRecord row, long? tick) => row.Stamp.Sequence > hit.Stamp.Sequence &&
            (row.Stamp.Sequence < end.Stamp.Sequence || tick is not null && tick == end.LastPhysicsTick);
    }

    private BreakCandidate Candidate(GameplayObservation combat, StructureKey key, long? impactTick, double oldest)
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
        HudPlayerBreakContact? playerContact = null;
        int? playerDamage = null;
        if (combat.LastPhysicsTick is { } tick)
        {
            if (structurePair)
            {
                var registration = MatchRegistration(combat, key, counterpart, tick, oldest);
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
                var contact = MatchPlayerContact(key, tick, oldest) ?? history.OfType<ContactObservation>().LastOrDefault(row =>
                    row.Structure == key && row.Stamp.TimeSeconds >= oldest && row.PhysicsTick < tick &&
                    row.Phase == "enter" && row.Points.Length > 0);
                var fixedStep = history.OfType<PhysicsSample>().LastOrDefault(row => row.Tick == tick);
                if (contact is { PhysicsTick: { } contactTick })
                {
                    impact = PlayerImpact(contact, contactTick);
                    var contactStep = contactTick == tick ? fixedStep :
                        history.OfType<PhysicsSample>().LastOrDefault(row => row.Tick == contactTick);
                    playerContact = CapturePlayerBreakContact(key, contactTick, contact, impact, contactStep,
                        contactTick == tick ? combat.Combat : null);
                }
                if (contact?.Impact?.Second?.Sample.Frozen != true)
                    linearSpeed = FiniteSpeed(contact?.RelativeLinearVelocity);
                if (combat.Combat is { } snapshots)
                {
                    var gamePlayer = snapshots.PlayerPhysicsBody?.Positional;
                    var fixedPlayer = fixedStep?.Physics.LinearVelocityWorld;
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
            PlayerContact: playerContact, PlayerContactEstimate: structurePair ? null : combat.PlayerContactEstimate,
            PlayerDamage: playerDamage);
    }

    private ContactObservation? MatchPlayerContact(StructureKey key, long tick, double oldest)
    {
        // Hits can be processed on the step after the first physical contact.
        return history.OfType<ContactObservation>().Where(row => row.Structure == key && row.Stamp.TimeSeconds >= oldest &&
                row.PhysicsTick >= tick - 1 && row.PhysicsTick <= tick &&
                row.Points.Length > 0 && row.Phase is "enter" or "stay")
            .GroupBy(row => (row.PhysicsTick, row.Pair))
            .Select(pair => pair.MaxBy(row => (HasMotion(row), !row.StructureSideProbe, row.Stamp.Sequence))!)
            .MaxBy(row => (HasMotion(row), row.Phase == "enter", row.PhysicsTick, -row.Points.Min(point => point.Separation)));

        static bool HasMotion(ContactObservation row) =>
            FlightContactPatches.StructureRelativeLinearVelocity(row) is not null ||
            row.Impact is { First.SameFixedStep: true, Second.SameFixedStep: true };
    }

    private static HudPlayerBreakContact? CapturePlayerBreakContact(StructureKey key, long tick, ContactObservation contact,
        BreakImpact? impact, PhysicsSample? fixedStep, CombatMeasurement? combat)
    {
        var impactPoint = impact?.DisplayPoint;
        var point = impactPoint is { } measured
            ? contact.Points.FirstOrDefault(point => point.PointWorld == measured.PointWorld)
            : contact.Points.Where(point => FlightContactPatches.IsFinite(FlightContactPatches.Vector(point.PointWorld)))
                .MinBy(point => point.Separation);
        if (point is null)
            return null;

        Vector3? relative = impactPoint?.RelativeVelocityWorld;
        var contactNormal = impactPoint?.NormalTowardCounterpart ?? UnitContactNormal(FlightContactPatches.Vector(point.NormalTowardPlayer));
        float? normalSpeed = impactPoint?.ClosingMetersPerSecond;
        bool linearEstimate = impact?.LinearEstimate == true;
        if (relative is null && FlightContactPatches.StructureRelativeLinearVelocity(contact) is { } header)
        {
            relative = header;
            normalSpeed = contactNormal is { } normal ? Vector3.Dot(header, normal) : null;
            linearEstimate = true;
        }
        if (relative is null && combat is not null && contact.OtherBodyId is { } structureBody &&
            contact.Frames?.StructureCollider?.Definition?.Descriptor.ReferenceBodyId == structureBody &&
            contact.Impact?.Second?.Sample.Frozen != true &&
            UsableImpact(contact.Impact?.First, contact.PlayerBodyId, tick, contact.Stamp, false))
        {
            var estimate = FlightContactPatches.Vector(combat.FirstStructure.Positional) -
                FlightContactPatches.Vector(contact.Impact!.First!.Sample.Body.LinearVelocityWorld);
            if (FiniteSpeed(estimate) is not null)
            {
                relative = estimate;
                normalSpeed = contactNormal is { } normal ? Vector3.Dot(estimate, normal) : null;
                linearEstimate = true;
            }
        }

        HudContactPlacement? placement = null;
        HudContactPatch? patch = null;
        if (fixedStep is not null && HudBodyFrame.From(fixedStep.Physics) is { } origin)
        {
            var collider = contact.Frames is { } frames
                ? HudStructureCollider.Resolve(key, contact.Pair, contact.Stamp.Segment, tick, frames, null, null)
                : null;
            var player = HudPlayerCollider.Build(contact.Pair, contact.Stamp.Segment, contact.PlayerBodyId,
                contact.Frames, contact.Frames?.PlayerBody?.WorldPose);
            placement = collider?.Place(origin, player, tick) ?? new HudContactPlacement(origin, player, null);
            patch = FlightContactPatches.Build(new[] { point }, origin)[0];
            if (contactNormal is { } normal)
                patch = patch with { BodyNormal = Vector3.Transform(normal, Quaternion.Conjugate(origin.Yaw)) };
        }

        var center = placement?.Box?.Center ?? placement?.Mesh?.Center ??
            FlightContactPatches.Vector(contact.Frames?.StructureCenterWorld ?? point.PointWorld);
        if (!FlightContactPatches.IsFinite(center))
            center = FlightContactPatches.Vector(point.PointWorld);
        return new HudPlayerBreakContact(placement, patch, center, relative, normalSpeed, linearEstimate,
            PlayerColliderId: contact.Pair.PlayerColliderId, NormalTowardPlayerWorld: contactNormal);
    }

    private static BreakImpact? PlayerImpact(ContactObservation contact, long tick)
    {
        if (contact.OtherBodyId is not { } structureBody)
            return null;
        return MeasureImpact(contact.Stamp, tick, contact.StructureSideProbe ? "structure_collision_callback" : "collision_callback",
            structureBody, contact.PlayerBodyId, false, true,
            contact.Impact?.Second, contact.Impact?.First, contact.ReportedPointCount,
            contact.Points.Select(point => new ImpactPoint(point.PointWorld, point.NormalTowardPlayer, point.Separation)),
            linearRelative: FlightContactPatches.StructureRelativeLinearVelocity(contact));
    }

    private StructureImpactObservation? MatchRegistration(GameplayObservation combat, StructureKey key, StructureKey? counterpart,
        long tick, double oldest)
    {
        if (counterpart is null || counterpart == key)
            return null;

        var registrations = history.OfType<StructureImpactObservation>().Where(row =>
            row.Environment is null && row.Stamp.TimeSeconds >= oldest && row.Stamp.Sequence < combat.Stamp.Sequence && row.PhysicsTick == tick &&
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
        Vector3? linear = null;
        if (combat.Combat is { SecondStructure: { } second } snapshots)
        {
            var firstMinusSecond = FlightContactPatches.Vector(snapshots.FirstStructure.Positional) -
                FlightContactPatches.Vector(second.Positional);
            linear = combat.Structure == key ? firstMinusSecond : -firstMinusSecond;
        }
        return MeasureImpact(registration.Stamp, tick, registration.Motion.ObservationPhase, structureBody, otherBody,
            true, registration.NormalConvention == "toward_first", breakingFirst ? registration.Motion.First : registration.Motion.Second,
            breakingFirst ? registration.Motion.Second : registration.Motion.First, registration.ReportedPointCount,
            registration.Points, negateNormal: breakingFirst, gamePoint: combat.Combat?.CollisionPoint, linearRelative: linear);
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
        IEnumerable<ImpactPoint> points, bool negateNormal = false, Vec3? gamePoint = null, bool staticCounterpart = false,
        Vector3? linearRelative = null)
    {
        var speeds = new List<ImpactSpeed>();
        ImpactSpeed? atGamePoint = null;
        bool hasPointMotion = UsableImpact(breaking, breakingId, tick, stamp, true) && (staticCounterpart ||
            otherId is { } id && UsableImpact(other, id, tick, stamp, otherIsStructure) &&
            breaking!.Sample.FixedGameTimeSeconds == other!.Sample.FixedGameTimeSeconds);
        bool useLinear = !hasPointMotion && linearRelative is { } linear && FiniteSpeed(linear) is not null;
        if (hasPointMotion || useLinear)
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
            breaking?.AgeSeconds, other?.AgeSeconds, speeds.ToArray(), atGamePoint, LinearEstimate: useLinear);

        ImpactSpeed? AtPoint(Vec3 worldPoint, Vec3 worldNormal)
        {
            var position = FlightContactPatches.Vector(worldPoint);
            if (!FlightContactPatches.IsFinite(position))
                return null;
            var relative = hasPointMotion
                ? PointVelocity(breaking!.Sample.Body, position) -
                    (staticCounterpart ? Vector3.Zero : PointVelocity(other!.Sample.Body, position))
                : linearRelative!.Value;
            if (FiniteSpeed(relative) is not { } magnitude)
                return null;
            var normal = FlightContactPatches.Vector(worldNormal);
            var unit = normalKnown ? UnitContactNormal(negateNormal ? -normal : normal) : null;
            float? closing = unit is { } direction ? Vector3.Dot(relative, direction) : null;
            return new ImpactSpeed(worldPoint, magnitude, closing, relative, unit);
        }
    }

    private static Vector3? UnitContactNormal(Vector3 normal)
    {
        var lengthSquared = normal.LengthSquared();
        return float.IsFinite(lengthSquared) && lengthSquared > 1e-8f ? normal / MathF.Sqrt(lengthSquared) : null;
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
