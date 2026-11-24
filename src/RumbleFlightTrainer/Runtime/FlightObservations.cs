namespace RumbleFlightTrainer;

internal sealed record CaptureMetadata(string GameVersion, short LocalActorNumber,
    bool StructureCollisionProbeInstalled = true, bool EnvironmentCollisionProbeInstalled = true);

internal readonly record struct TraceStamp(long Sequence, double TimeSeconds, int Segment, int RenderFrame);
internal readonly record struct Vec3(float X, float Y, float Z);
internal readonly record struct Quat(float X, float Y, float Z, float W);
internal readonly record struct Pose(Vec3 Position, Quat Rotation);
internal sealed record PhysicsState(
    double FixedGameTimeSeconds, float FixedDeltaSeconds, Pose BodyWorldPose, Vec3 WorldCenterOfMass,
    Vec3 LinearVelocityWorld, Vec3 AngularVelocityWorld, bool Grounded, Vec3? GravityImpulseWorld = null);

internal abstract record TraceRecord(TraceStamp Stamp);

internal sealed record PhysicsSample(
    TraceStamp Stamp, long Tick, PhysicsState Physics, double? GameTimeSincePreviousStepSeconds,
    string? GroundedTransition, StructureFrame? StructureTelemetry) : TraceRecord(Stamp);

internal readonly record struct StructureKey(int InstanceId, int PoolGeneration);
internal readonly record struct ContactKey(int PlayerColliderId, int OtherColliderId);

internal sealed record StructureMotion(
    StructureKey Key, string Name, int RigidbodyId, Pose WorldPose, Vec3 WorldCenterOfMass,
    Vec3 LinearVelocityWorld, Vec3 AngularVelocityWorld, bool Frozen);

internal sealed record StructureFrame(
    StructureMotion[] Structures,
    bool NearbySearchAvailable, bool NearbySearchSaturated, bool SnapshotLimitReached, bool ContactObserverAvailable,
    bool? ImpactBodyLimitReached = null);

internal sealed record ContactGeometry(
    Vec3 PointWorld, Vec3 NormalTowardPlayer, float Separation, Vec3? PointInStructure, Vec3? NormalInStructure);

internal sealed record ContactObservation(
    TraceStamp Stamp, long? PhysicsTick, string Phase, ContactKey Pair, string PlayerHitbox, int PlayerBodyId,
    int? OtherBodyId, string OtherKind, int? OtherLayer, StructureKey? Structure, string? StructureName,
    ContactGeometry[] Points, int ReportedPointCount, Vec3? PairImpulseRaw, bool? PlayerIsSecondInNativePair,
    long? GeometryTick, StructureMotion? StructureAtFixedSample, string? Reason = null,
    double? ObservedFixedGameTimeSeconds = null, ContactFrames? Frames = null,
    ImpactMotion? Impact = null, Vec3? RelativeLinearVelocity = null) : TraceRecord(Stamp);

// A local-to-parent affine map. Its basis vectors preserve scale and shear.
internal readonly record struct AffineFrame(Vec3 Origin, Vec3 XAxis, Vec3 YAxis, Vec3 ZAxis);
internal readonly record struct MeshLocalBounds(Vec3 Center, Vec3 Size);
internal readonly record struct MeshEdge(Vec3 Start, Vec3 End);
internal sealed record ColliderShape(
    string Kind, Vec3? Center = null, Vec3? Size = null, float? Radius = null, float? Height = null,
    int? Direction = null, int? MeshId = null, string? MeshName = null, bool? Convex = null,
    string? CookingOptions = null, MeshLocalBounds? MeshBounds = null, Vec3[][]? MeshContours = null,
    MeshEdge[]? MeshEdges = null);
internal sealed record ColliderDescriptor(
    int ColliderId, int? AttachedBodyId, StructureKey? Structure, int RootId, int? ReferenceBodyId,
    int? ParentTransformId, ColliderShape Shape, AffineFrame ColliderToRoot, AffineFrame? RootToReferenceBody);
internal sealed record ColliderDescriptorObservation(TraceStamp Stamp, ColliderDescriptor Descriptor) : TraceRecord(Stamp);

internal sealed record ColliderFrameReference(
    long DescriptorSequence, AffineFrame? ColliderToRootOverride, AffineFrame? RootToReferenceBodyOverride,
    ColliderDescriptorObservation? Definition = null);
internal sealed record ContactBodyFrame(int BodyId, Pose WorldPose, Vec3 WorldCenterOfMass);
internal sealed record ContactFrames(
    ColliderFrameReference? PlayerCollider, ColliderFrameReference? StructureCollider,
    ContactBodyFrame? PlayerBody, AffineFrame? StructureRootWorld)
{
    public string Phase => "collision_callback";
}

internal sealed record BodyKinematics(
    int BodyId, Pose WorldPose, Vec3 WorldCenterOfMass, Vec3 LinearVelocityWorld, Vec3 AngularVelocityWorld);
internal sealed record FixedMotionSample(
    BodyKinematics Body, long Tick, int Segment, double SampleSeconds, double FixedGameTimeSeconds,
    string SimulationMode, bool? Frozen)
{
    public string Phase => "melon_fixed_update";
}

internal sealed record AgedMotionSample(FixedMotionSample Sample, double AgeSeconds, bool SameFixedStep);
internal sealed record ImpactMotion(
    AgedMotionSample? First, AgedMotionSample? Second, string ObservationPhase, double ObservedSeconds,
    bool AdditionalBodyLimitReached);
internal sealed record ImpactPoint(Vec3 PointWorld, Vec3 NormalWorld, float Separation);
internal sealed record ImpactParticipant(
    StructureKey? Structure, string Name, int? ColliderId, int? BodyId, ColliderFrameReference? Collider);
internal sealed record ImpactColliderPair(int ReceiverColliderId, int OtherColliderId, int? ReceiverBodyId, int? OtherBodyId);
internal sealed record EnvironmentContact(int Layer, string Phase, bool StaticCollider);
internal sealed record StructureImpactObservation(
    TraceStamp Stamp, long? PhysicsTick, double? ObservedFixedGameTimeSeconds,
    ImpactParticipant First, ImpactParticipant Second, ImpactPoint[] Points, int ReportedPointCount,
    string NormalConvention, ImpactMotion Motion, ImpactColliderPair CallbackPair,
    Vec3? RelativeLinearVelocity = null, EnvironmentContact? Environment = null) : TraceRecord(Stamp);

internal sealed record StructureImpactGap(TraceStamp Stamp, string Source, StructureKey? Structure,
    StructureKey? OtherStructure) : TraceRecord(Stamp);

internal sealed record GameplayObservation(
    TraceStamp Stamp, long? LastPhysicsTick, string Event, string Source, short? Actor,
    StructureKey? Structure = null, string? StructureName = null, StructureKey? OtherStructure = null,
    string? Detail = null, long? CombatTimestamp = null,
    CombatMeasurement? Combat = null, BreakAnalysis? Break = null, long? CollisionSequence = null,
    IronBootsBreakEvidence? IronBoots = null, IronBootsApproach? JumpDiagram = null) : TraceRecord(Stamp);

internal sealed record CombatVelocity(Vec3 Positional, Vec3 Angular);
internal sealed record CombatMeasurement(CombatVelocity FirstStructure, CombatVelocity? SecondStructure = null,
    Vec3? CollisionPoint = null, CombatVelocity? PlayerPhysicsBody = null, CombatVelocity? PlayerVrBody = null);
internal sealed record CombatHealthObservation(TraceStamp Stamp, long? Tick, StructureKey Structure, string Event,
    long CombatTimestamp, short Before, short After) : TraceRecord(Stamp)
{
    public int? Damage => After <= Before ? Before - After : null;
}

internal sealed record BreakAnalysis(
    TraceStamp? KillRequest, BreakCandidate[] Candidates, bool TimelineComplete,
    long? DirectCollisionSequence = null)
{
    public BreakCandidate? Match
    {
        get
        {
            if (DirectCollisionSequence is { } direct)
                return Candidates.FirstOrDefault(candidate => candidate.Stamp.Sequence == direct);
            if (!TimelineComplete)
                return null;
            if (Candidates.Length == 1)
            {
                var candidate = Candidates[0];
                return candidate.Actor.HasValue || candidate.Counterpart.HasValue || candidate.Environment is not null
                    ? candidate : null;
            }

            // A verified player hit can resolve competing jump evidence on the same structure.
            BreakCandidate? damagingHit = null;
            foreach (var candidate in Candidates)
            {
                if (candidate.Event is not ("jump_on_structure" or "player_hit" or "player_hit_from_beneath"))
                    return null;
                if (candidate.Event != "jump_on_structure" && candidate.PlayerDamage is > 0)
                {
                    if (damagingHit is not null)
                        return null;
                    damagingHit = candidate;
                }
            }
            return damagingHit;
        }
    }
}

internal sealed record BreakCandidate(TraceStamp Stamp, long? Tick, string Event, short? Actor,
    StructureKey? Counterpart, string? CounterpartName, long? CombatTimestamp, CombatMeasurement? Combat,
    BreakImpact? Impact, float? LinearImpactMetersPerSecond = null, string? Environment = null,
    IronBootsBreakEvidence? IronBoots = null, IronBootsApproach? JumpDiagram = null,
    int? PlayerDamage = null);

internal sealed record BreakImpact(TraceStamp Stamp, long Tick, string Phase, int BreakingBodyId, int? CounterpartBodyId,
    int ReportedPointCount, double? BreakingSampleAgeSeconds, double? CounterpartSampleAgeSeconds,
    ImpactSpeed[] Points, ImpactSpeed? GameContactSpeed = null);
internal sealed record ImpactSpeed(Vec3 PointWorld, float RelativeMetersPerSecond, float? ClosingMetersPerSecond);
internal sealed record TraceEvent(
    TraceStamp Stamp, string Event, string Source, long? LastPhysicsTick, string? Reason = null) : TraceRecord(Stamp);
internal sealed record IronBootsContactStarted(TraceStamp Stamp) : TraceRecord(Stamp);
