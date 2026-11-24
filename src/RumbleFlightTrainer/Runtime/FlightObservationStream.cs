using System.Diagnostics;

namespace RumbleFlightTrainer;

internal sealed class FlightObservationStream
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly FlightBreakTimeline breakTimeline;
    private readonly Queue<GameplayObservation> pendingBreaks = new();
    private PhysicsSample? previousStep;
    private long sequence;
    private long tick;

    public FlightObservationStream(CaptureMetadata capture)
    {
        GameVersion = capture.GameVersion;
        breakTimeline = new FlightBreakTimeline(capture);
    }

    public event Action<TraceRecord>? ObservationCaptured;
    public long NextPhysicsTick => tick + 1;
    public long? LastPhysicsTick => previousStep?.Tick;
    public int Segment { get; private set; } = 1;
    public string GameVersion { get; }
    public double ElapsedSeconds => clock.Elapsed.TotalSeconds;

    public void CapturePhysics(int frame, PhysicsState state, StructureFrame? structures = null)
    {
        FlushBreaks();
        string? transition = null;
        double? timeSincePrevious = null;
        if (previousStep is { Physics: var previous })
        {
            timeSincePrevious = state.FixedGameTimeSeconds - previous.FixedGameTimeSeconds;
            if (state.Grounded != previous.Grounded)
                transition = state.Grounded ? "became_grounded" : "became_airborne";
        }
        previousStep = new PhysicsSample(NextStamp(frame), ++tick, state, timeSincePrevious, transition, structures);
        Publish(previousStep);
    }

    public void CaptureContact(int frame, ContactObservation contact)
    {
        contact = contact with { Stamp = NextStamp(frame), PhysicsTick = LastPhysicsTick };
        Publish(contact);
    }

    public ColliderDescriptorObservation CaptureCollider(int frame, ColliderDescriptor descriptor)
    {
        var definition = new ColliderDescriptorObservation(NextStamp(frame), descriptor);
        Publish(definition);
        return definition;
    }

    public long CaptureImpact(int frame, StructureImpactObservation impact)
    {
        var stamp = NextStamp(frame);
        Publish(impact with { Stamp = stamp, PhysicsTick = LastPhysicsTick });
        return stamp.Sequence;
    }

    public void DropStructureImpact(int frame, string source, StructureKey? structure, StructureKey? otherStructure = null) =>
        Publish(new StructureImpactGap(NextStamp(frame), source, structure, otherStructure));

    public void CaptureGameplay(int frame, GameplayObservation observation)
    {
        observation = observation with { Stamp = NextStamp(frame), LastPhysicsTick = LastPhysicsTick };
        if (observation.Event == "structure_destroyed")
            pendingBreaks.Enqueue(observation);
        else
            Publish(observation);
    }

    public void FlushBreaks()
    {
        // Contact and combat callbacks for the destruction step can follow the destruction event.
        while (pendingBreaks.TryDequeue(out var destruction))
            Publish(destruction);
    }

    public void CaptureCombatHealth(int frame, StructureKey structure, string kind, int timestamp, short before, short after) =>
        Publish(new CombatHealthObservation(NextStamp(frame), LastPhysicsTick, structure, kind, timestamp, before, after));

    public void StartIronBootsContact(int frame) =>
        Publish(new IronBootsContactStarted(NextStamp(frame)));

    public void StartSegment(int frame, string source, string reason)
    {
        pendingBreaks.Clear();
        Segment++;
        previousStep = null;
        Publish(new TraceEvent(NextStamp(frame), "segment_started", source, null, reason));
    }

    private TraceStamp NextStamp(int frame) => new(++sequence, ElapsedSeconds, Segment, frame);

    private void Publish(TraceRecord record)
    {
        if (breakTimeline.Observe(record) is { } analysis &&
            record is GameplayObservation destruction)
            record = destruction with { Break = analysis };
        ObservationCaptured?.Invoke(record);
    }
}
