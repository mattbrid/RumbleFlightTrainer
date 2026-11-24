using Il2CppRUMBLE.MoveSystem;
using Il2CppRUMBLE.Players;
using MelonLoader;
using UnityEngine;
using UnityEngine.Events;
using static RumbleFlightTrainer.FlightRuntime;

namespace RumbleFlightTrainer;

internal sealed partial class StructureTelemetry : IDisposable
{
    private const float NearbyRadius = 4f;
    private const int MaxNearby = 24;
    private const int MaxNearbyResults = 64;
    private const int MaxSnapshots = 16;
    private const int KeepAfterRelevanceTicks = 100;
    private const int MaxContactPoints = 8;

    private readonly FlightObservationStream session;
    private readonly PlayerController controller;
    private readonly Rigidbody body;
    private readonly MelonLogger.Instance logger;
    private readonly short actor;
    private readonly int footId;
    private readonly int capsuleId;
    private readonly bool ironBootsEnabled;
    private readonly List<FlightContactObserver> observers = new();
    private readonly Dictionary<int, StructureKey> keys = new();
    private readonly HashSet<int> returned = new();
    private readonly Dictionary<int, bool> cages = new();
    private readonly Dictionary<StructureKey, Tracked> tracked = new();
    private readonly Dictionary<StructureKey, StructureMotion> nearby = new();
    private readonly HashSet<StructureKey> sampledKeys = new();
    private readonly HashSet<StructureKey> touchingKeys = new();
    private readonly Dictionary<ContactKey, ActiveContact> contacts = new();
    private readonly HashSet<ContactKey> impulsesThisTick = new();
    private readonly List<StructureKey> expired = new();
    private readonly List<ContactKey> ended = new();
    private readonly HashSet<string> warnings = new();
    private int generation;
    private int? terrainLayer;
    private bool nearbyFailed;
    private bool contactFailed;
    private bool disposed;

    private sealed class Tracked
    {
        public Tracked(Structure structure, StructureKey key, string name, long until)
        {
            Structure = structure;
            Key = key;
            Name = name;
            Until = until;
        }

        public Structure Structure { get; }
        public StructureKey Key { get; }
        public string Name { get; }
        public long Until { get; set; }
        public bool CombatOnly { get; set; }
        public bool LocalModifierObserved { get; set; }
        public bool Destroyed { get; set; }
        public UnityEvent? DestroyedEvent { get; set; }
        public UnityAction? DestroyedListener { get; set; }
    }

    private sealed record ActiveContact(Collider? PlayerCollider, Collider OtherCollider, ContactObservation Last);


    public StructureTelemetry(FlightObservationStream session, PlayerController controller, Rigidbody body,
        MelonLogger.Instance logger, bool ironBootsEnabled, float ironBootsVerticalVelocityThreshold)
    {
        this.session = session;
        this.controller = controller;
        this.body = body;
        this.logger = logger;
        this.ironBootsEnabled = ironBootsEnabled;
        ironBootsGate = new IronBootsContactGate(ironBootsVerticalVelocityThreshold);
        actor = controller.ActorNo;
        var physics = controller.PlayerPhysics;
        var foot = physics.FootCollider;
        var capsule = physics.pillBodyCollider;
        footId = foot == null ? 0 : foot.GetInstanceID();
        capsuleId = capsule == null ? 0 : capsule.GetInstanceID();

        // Collider-bearing bodies may be different from the main movement rigidbody.
        var receivers = new HashSet<int>();
        try
        {
            AddObserver(body, receivers);
            AddObserver(foot == null ? null : foot.attachedRigidbody, receivers);
            AddObserver(capsule == null ? null : capsule.attachedRigidbody, receivers);
        }
        catch (Exception error)
        {
            contactFailed = true;
            ReportFailure("local_collision_observer", error);
        }
    }

    public StructureFrame Sample(long tick, PhysicsState playerState, double playerSampleSeconds)
    {
        impulsesThisTick.Clear();
        SampleIronBootsVelocity(tick);
        RefreshCombatSource();
        ExpireInvalidContacts();
        // Touching bodies and active/recent local candidates share this capped set.
        // Prioritize current contacts, then local modifier targets, then recency.
        touchingKeys.Clear();
        foreach (var contact in contacts.Values)
            if (contact.Last.Structure is { } key && tracked.TryGetValue(key, out var touching))
            {
                touching.Until = tick + KeepAfterRelevanceTicks;
                touchingKeys.Add(key);
            }

        expired.Clear();
        var candidates = new List<Tracked>();
        foreach (var entry in tracked.Values)
        {
            if (!IsActive(entry))
            {
                expired.Add(entry.Key);
                continue;
            }

            if (entry.Until < tick)
            {
                var joint = entry.LocalModifierObserved ? entry.Structure.ActiveJointControl : null;
                if (joint == null || joint.AttachedBody == null)
                {
                    // Keep the lifetime listener while the HUD still follows a touched collider.
                    if (!hudNearby.ContainsKey(entry.Key))
                        expired.Add(entry.Key);
                    continue;
                }
                entry.Until = tick + KeepAfterRelevanceTicks;
            }
            candidates.Add(entry);
        }

        foreach (var key in expired)
            Forget(key);

        StartFixedCapture(tick, playerState, playerSampleSeconds);
        var motions = candidates.OrderByDescending(item => touchingKeys.Contains(item.Key))
            .ThenByDescending(item => item.LocalModifierObserved).ThenBy(item => item.CombatOnly)
            .ThenByDescending(item => item.Until).ThenBy(item => item.Key.InstanceId)
            .Take(MaxSnapshots).Select(item => ReadFixedStructureMotion(item.Structure, item.Key, item.Name))
            .OfType<StructureMotion>().ToArray();
        sampledKeys.Clear();
        foreach (var motion in motions)
            sampledKeys.Add(motion.Key);
        int additionalSamples = SampleRequestedBodies();
        bool searchFull = SampleNearby(MaxNearby - additionalSamples);
        return new StructureFrame(motions, !nearbyFailed && combatManager != null,
            searchFull, candidates.Count > MaxSnapshots, !contactFailed && observers.Count > 0,
            requestedBodies.Count >= MaxNearby);
    }

    public void ReportContact(int receiverId, Collision collision, string phase)
    {
        if (disposed || contactFailed)
            return;

        try
        {
            ObserveContact(receiverId, collision, phase);
        }
        catch (Exception error)
        {
            contactFailed = true;
            ReportFailure("local_collision_observer", error);
            CloseContacts(null, "observer_failed");
        }
    }

    private void ObserveContact(int receiverId, Collision collision, string phase)
    {
        ContactPair pair = collision.m_Pair;
        bool flipped = collision.Flipped;
        var key = flipped
            ? new ContactKey(pair.otherColliderInstanceID, pair.colliderInstanceID)
            : new ContactKey(pair.colliderInstanceID, pair.otherColliderInstanceID);
        if (phase == "exit")
        {
            if (contacts.Remove(key, out var endedPair))
                EndContact(endedPair.Last, "exit", null);
            return;
        }

        Collider? other = collision.collider;
        if (other == null)
            return;
        Collider? own = flipped ? pair.otherCollider : pair.collider;
        Rigidbody? otherBody = other.attachedRigidbody;
        var structure = FindStructure(other);
        int layer = other.gameObject.layer;
        if (structure is null || structure == null)
            return;

        var entry = Promote(structure);
        if (entry is null || entry.Destroyed)
            return;

        long? tick = session.LastPhysicsTick;
        if (contacts.TryGetValue(key, out var prior) && prior.Last.GeometryTick == tick &&
            prior.Last.Phase == phase && prior.Last.Structure == entry.Key)
            return;
        if (prior is not null && prior.Last.Structure != entry.Key)
            EndContact(prior.Last, "interrupted", "collider_identity_changed");

        double observedAt = session.ElapsedSeconds;
        int count = collision.contactCount;
        var points = new ContactGeometry[Math.Min(count, MaxContactPoints)];
        var structureFrame = structure.transform;
        for (int i = 0; i < points.Length; i++)
        {
            var point = collision.GetContact(i);
            points[i] = new ContactGeometry(ToVec3(point.point), ToVec3(point.normal), point.separation,
                ToVec3(structureFrame.InverseTransformPoint(point.point)),
                ToVec3(structureFrame.InverseTransformDirection(point.normal)));
        }

        Vec3? impulse = count > 0 && impulsesThisTick.Add(key) ? ToVec3(collision.impulse) : null;
        var observation = new ContactObservation(default, null, phase, key,
            key.PlayerColliderId == footId ? "foot" : key.PlayerColliderId == capsuleId ? "capsule" : "other",
            receiverId, otherBody == null ? null : otherBody.GetInstanceID(), "structure",
            layer, entry.Key, entry.Name, points, count, impulse, impulse.HasValue ? flipped : null,
            tick,
            !sampledKeys.Contains(entry.Key) && nearby.TryGetValue(entry.Key, out var preSolve)
                ? preSolve : null, ObservedFixedGameTimeSeconds: Time.inFixedTimeStep ? Time.fixedTimeAsDouble : null);
        if (count > 0)
            observation = CaptureContactDetails(observation with { RelativeLinearVelocity = TryCollisionLinearVelocity(collision) },
                entry, own, other, otherBody, observedAt);
        contacts[key] = new ActiveContact(own, other, observation);
        if (tick is { } contactTick)
            SampleIronBoots(contactTick, observedAt, observation, entry, own, other);
        session.CaptureContact(Time.frameCount, observation);
    }

    private void AddObserver(Rigidbody? receiver, HashSet<int> receivers)
    {
        if (receiver == null || !receivers.Add(receiver.GetInstanceID()))
            return;
        var observer = receiver.gameObject.AddComponent<FlightContactObserver>();
        observers.Add(observer);
        observer.Bind(this, receiver.GetInstanceID());
        receivingBodies[receiver.GetInstanceID()] = receiver;
    }

    private bool SampleNearby(int capacity)
    {
        nearby.Clear();
        if (nearbyFailed || combatManager == null)
            return false;
        if (capacity == 0)
            return true;

        try
        {
            // This native query uses a shared result array. Copy values while it is current.
            var matches = combatManager.GetStructuresInRange(body.worldCenterOfMass, NearbyRadius, false, out int count);
            if (matches is null)
                return false;
            int limit = Math.Min(count, Math.Min(MaxNearbyResults, matches.Length));
            for (int index = 0; index < limit; index++)
            {
                Structure? structure = matches[index];
                if (structure == null || !structure.gameObject.activeInHierarchy ||
                    returned.Contains(structure.GetInstanceID()) || IsCaged(structure))
                    continue;
                var key = KeyFor(structure);
                if (!sampledKeys.Contains(key) && !nearby.ContainsKey(key) &&
                    ReadFixedStructureMotion(structure, key, structure.name) is { } motion)
                {
                    nearby[key] = motion;
                }
                if (nearby.Count >= capacity && index + 1 < limit)
                    return true;
            }
            return count > MaxNearbyResults || (count > 0 && count >= matches.Length);
        }
        catch (Exception error)
        {
            nearbyFailed = true;
            ReportFailure("nearby_structure_query", error);
            return false;
        }
    }

    private Tracked? Promote(Structure structure, bool combatOnly = false)
    {
        if (!structure.gameObject.activeInHierarchy || returned.Contains(structure.GetInstanceID()) || IsCaged(structure))
            return null;

        var key = KeyFor(structure);
        long until = session.NextPhysicsTick + KeepAfterRelevanceTicks;
        if (tracked.TryGetValue(key, out var existing))
        {
            existing.Until = until;
            existing.CombatOnly &= combatOnly;
            return existing;
        }
        if (combatOnly && tracked.Values.Count(item => item.CombatOnly) >= MaxNearby)
            return null;

        var entry = new Tracked(structure, key, structure.name, until) { CombatOnly = combatOnly };
        tracked.Add(key, entry);
        try
        {
            entry.DestroyedEvent = structure.onStructureDestroyed;
            if (entry.DestroyedEvent != null)
            {
                entry.DestroyedListener = (Action)(() => Guard("structure_destroyed", () => OnDestroyed(entry)));
                entry.DestroyedEvent.AddListener(entry.DestroyedListener);
            }
        }
        catch (Exception error)
        {
            ReportFailure("structure_destroy_listener", error);
        }
        return entry;
    }

    private StructureKey KeyFor(Structure structure)
    {
        int instanceId = structure.GetInstanceID();
        if (!keys.TryGetValue(instanceId, out var key))
            keys[instanceId] = key = new StructureKey(instanceId, ++generation);
        return key;
    }

    private bool IsCaged(Structure structure)
    {
        int instanceId = structure.GetInstanceID();
        if (cages.TryGetValue(instanceId, out bool caged))
            return caged;

        var root = structure.transform;
        caged = root.name.Contains("cage", StringComparison.OrdinalIgnoreCase);
        for (int i = 0; i < root.childCount && !caged; i++)
        {
            string name = root.GetChild(i).name;
            caged = name.Equals("Ballcage", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Cubecage", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Wallcage", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Diskcage", StringComparison.OrdinalIgnoreCase);
        }
        cages[instanceId] = caged;
        return caged;
    }

    private static bool IsActive(Tracked entry) =>
        !entry.Destroyed && entry.Structure != null && entry.Structure.gameObject.activeInHierarchy;

    private StructureMotion? ReadMotion(Structure structure, StructureKey key, string name)
    {
        Rigidbody? rigidbody = structure.Rigidbody;
        if (rigidbody == null)
            return null;
        return new StructureMotion(key, name, rigidbody.GetInstanceID(),
            new Pose(ToVec3(rigidbody.position), ToQuat(rigidbody.rotation)), ToVec3(rigidbody.worldCenterOfMass),
            ToVec3(rigidbody.linearVelocity), ToVec3(rigidbody.angularVelocity), structure.PhysicsFrozen);
    }

    public void ResetContacts()
    {
        ironBootsTarget = null;
        ironBootsGate.Reset();
        ironBootsPreSolve = null;
        CloseContacts(null, "coordinate_segment_changed");
        nearby.Clear();
        sampledKeys.Clear();
        impulsesThisTick.Clear();
        fixedBodies.Clear();
        requestedBodies.Clear();
        descriptors.Clear();
        structureImpactAttempts = 0;
    }

    private void ExpireInvalidContacts()
    {
        ended.Clear();
        foreach (var (key, pair) in contacts)
        {
            if (!ColliderActive(pair.PlayerCollider) || !ColliderActive(pair.OtherCollider))
                ended.Add(key);
        }
        foreach (var key in ended)
            if (contacts.Remove(key, out var pair))
                EndContact(pair.Last, "interrupted", "collider_disabled_or_removed");
    }

    private static bool ColliderActive(Collider? collider) =>
        collider != null && collider.enabled && collider.gameObject.activeInHierarchy;

    private void CloseContacts(StructureKey? structure, string reason)
    {
        ended.Clear();
        foreach (var (key, pair) in contacts)
            if (structure is null || pair.Last.Structure == structure)
                ended.Add(key);
        foreach (var key in ended)
            if (contacts.Remove(key, out var pair))
                EndContact(pair.Last, "interrupted", reason);
    }

    private void EndContact(ContactObservation last, string phase, string? reason)
    {
        ironBootsGate.End(last.Pair);
        session.CaptureContact(Time.frameCount, last with
        {
            Phase = phase, PairImpulseRaw = null, PlayerIsSecondInNativePair = null,
            StructureAtFixedSample = null, Impact = null, RelativeLinearVelocity = null, Reason = reason,
            ObservedFixedGameTimeSeconds = Time.inFixedTimeStep ? Time.fixedTimeAsDouble : null
        });
    }

    private void Forget(StructureKey key)
    {
        if (!tracked.Remove(key, out var entry))
            return;

        Guard("structure_destroy_listener", () =>
        {
            if (entry.DestroyedEvent != null && entry.DestroyedListener != null)
                entry.DestroyedEvent.RemoveListener(entry.DestroyedListener);
        });
    }

    public void Guard(string source, Action action)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            ReportFailure(source, error);
        }
    }

    private void ReportFailure(string source, Exception error)
    {
        if (warnings.Add(source))
            logger.Warning($"Flight telemetry {source} failed: {error.Message}");
    }

    private void Record(string kind, string source, Tracked? entry = null, string? detail = null, long? collisionSequence = null) =>
        session.CaptureGameplay(Time.frameCount, new GameplayObservation(default, null, kind, source,
            null, entry?.Key, entry?.Name, Detail: detail, CollisionSequence: collisionSequence));

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        CloseContacts(null, "session_ended");
        Guard("combat_listener", ReleaseCombatSource);
        foreach (var key in tracked.Keys.ToArray())
            Forget(key);
        foreach (var observer in observers)
        {
            if (observer == null)
                continue;
            Guard("local_collision_observer", () =>
            {
                observer.Bind(null, 0);
                UnityEngine.Object.Destroy(observer);
            });
        }
        observers.Clear();
    }
}
