using Il2CppRUMBLE.MoveSystem;
using UnityEngine;
using static RumbleFlightTrainer.FlightRuntime;

namespace RumbleFlightTrainer;

internal sealed partial class StructureTelemetry
{
    private const int MaxStructureImpactsPerTick = 16;
    private const int MaxPreviousMotionTicks = 3;
    private const float FrameToleranceSquared = 1e-8f;

    private readonly Dictionary<int, Rigidbody> receivingBodies = new();
    private readonly Dictionary<int, BodyRequest> requestedBodies = new();
    private readonly Dictionary<int, (StructureKey? Owner, FixedMotionSample Motion)> fixedBodies = new();
    private readonly Dictionary<(int Collider, StructureKey? Lifetime), ColliderDescriptorObservation> descriptors = new();
    private long fixedTick;
    private double fixedGameTime;
    private string fixedSimulationMode = string.Empty;
    private int structureImpactAttempts;

    private sealed record BodyRequest(Rigidbody Body, Structure Structure, StructureKey Key, long Until);
    internal sealed record EnvironmentCollision(StructureKey Key, string Name, Collision Collision, Collider Surface, string Phase)
    {
        public long? Sequence { get; set; }
    }

    internal EnvironmentCollision? ActiveEnvironmentCollision { get; set; }

    private static Structure? FindStructure(Collider collider)
    {
        var owner = collider.GetComponentInParent<Structure>();
        var attached = collider.attachedRigidbody;
        return owner != null ? owner : attached == null ? null : attached.GetComponentInParent<Structure>();
    }

    private void StartFixedCapture(long tick, PhysicsState player, double playerSampleSeconds)
    {
        foreach (var (id, sample) in fixedBodies)
            if (tick - sample.Motion.Tick > MaxPreviousMotionTicks)
                fixedBodies.Remove(id);
        structureImpactAttempts = 0;
        fixedTick = tick;
        fixedGameTime = player.FixedGameTimeSeconds;
        fixedSimulationMode = Physics.simulationMode.ToString();
        int main = body.GetInstanceID();
        CacheBody(null, new BodyKinematics(main, player.BodyWorldPose, player.WorldCenterOfMass,
            player.LinearVelocityWorld, player.AngularVelocityWorld), playerSampleSeconds, null);
        foreach (var (id, receiver) in receivingBodies)
        {
            if (id != main && receiver != null && receiver.gameObject.activeInHierarchy)
                CacheBody(null, ReadBody(receiver), session.ElapsedSeconds, null);
        }
    }

    private StructureMotion? ReadFixedStructureMotion(Structure structure, StructureKey key, string name)
    {
        double sampledAt = session.ElapsedSeconds;
        var motion = ReadMotion(structure, key, name);
        if (motion is not null)
            CacheBody(key, new BodyKinematics(motion.RigidbodyId, motion.WorldPose, motion.WorldCenterOfMass,
                motion.LinearVelocityWorld, motion.AngularVelocityWorld), sampledAt, motion.Frozen);
        return motion;
    }

    private void CacheBody(StructureKey? owner, BodyKinematics motion, double sampledAt, bool? frozen)
    {
        if (!Valid(motion))
            return;

        fixedBodies[motion.BodyId] = (owner, new FixedMotionSample(motion, fixedTick, session.Segment,
            sampledAt, fixedGameTime, fixedSimulationMode, frozen));
    }

    private int SampleRequestedBodies()
    {
        int sampled = 0;
        foreach (var (id, request) in requestedBodies)
        {
            if (request.Until < fixedTick || request.Body == null || request.Structure == null ||
                !request.Body.gameObject.activeInHierarchy || !request.Structure.gameObject.activeInHierarchy ||
                !keys.TryGetValue(request.Key.InstanceId, out var current) || current != request.Key)
            {
                requestedBodies.Remove(id);
                continue;
            }
            if (HasCurrentSample(id, request.Key))
                continue;

            double sampledAt = session.ElapsedSeconds;
            // A child body can move independently. The structure's freeze flag describes its primary body.
            bool? frozen = request.Body == request.Structure.Rigidbody ? request.Structure.PhysicsFrozen : null;
            CacheBody(request.Key, ReadBody(request.Body), sampledAt, frozen);
            sampled++;
        }
        return sampled;
    }

    private bool RequestBody(Rigidbody? rigidbody, Structure structure, StructureKey key)
    {
        if (rigidbody == null)
            return false;

        int id = rigidbody.GetInstanceID();
        // A primary body already sampled for a tracked structure does not need a supplemental slot.
        if (rigidbody == structure.Rigidbody && sampledKeys.Contains(key) && HasCurrentSample(id, key))
            return false;

        if (!requestedBodies.ContainsKey(id) && requestedBodies.Count >= MaxNearby)
            return true;

        requestedBodies[id] = new BodyRequest(rigidbody, structure, key,
            session.NextPhysicsTick + KeepAfterRelevanceTicks);
        return false;
    }

    private bool HasCurrentSample(int id, StructureKey owner) =>
        fixedBodies.TryGetValue(id, out var sample) && sample.Owner == owner && sample.Motion.Tick == fixedTick;

    private void InvalidateImpactCapture(StructureKey key)
    {
        foreach (var (id, request) in requestedBodies)
            if (request.Key == key)
                requestedBodies.Remove(id);
        foreach (var (id, sample) in fixedBodies)
            if (sample.Owner == key)
                fixedBodies.Remove(id);
        foreach (var (identity, _) in descriptors)
            if (identity.Lifetime == key)
                descriptors.Remove(identity);
    }

    private AgedMotionSample? MotionAt(int? id, StructureKey? owner, double observedAt, double? observedFixedTime)
    {
        if (id is not { } bodyId || !fixedBodies.TryGetValue(bodyId, out var cached) || cached.Owner != owner ||
            cached.Motion.Segment != session.Segment || cached.Motion.SampleSeconds > observedAt)
            return null;

        var sample = cached.Motion;
        bool sameStep = sample.Tick == session.LastPhysicsTick && observedFixedTime == sample.FixedGameTimeSeconds;
        return new AgedMotionSample(sample, observedAt - sample.SampleSeconds, sameStep);
    }

    private ContactObservation CaptureContactDetails(ContactObservation contact, Tracked entry, Collider? own,
        Collider other, Rigidbody? otherBody, double now)
    {
        bool limited = !entry.Destroyed && RequestBody(otherBody, entry.Structure, entry.Key);
        var impact = new ImpactMotion(
            MotionAt(contact.PlayerBodyId, null, now, contact.ObservedFixedGameTimeSeconds),
            MotionAt(contact.OtherBodyId, entry.Key, now, contact.ObservedFixedGameTimeSeconds),
            "collision_callback", now, limited);
        contact = contact with { Impact = impact };
        try
        {
            var root = entry.Structure.transform;
            receivingBodies.TryGetValue(contact.PlayerBodyId, out var receiver);
            Rigidbody? receiverForCollider = own == null ? receiver : own.attachedRigidbody;
            var playerShape = own == null ? null : CaptureCollider(own, receiverForCollider, null, controller.transform, receiver);
            var structureShape = CaptureCollider(other, otherBody, entry.Key, root, entry.Structure.Rigidbody, sampleMesh: true);

            var playerPose = receiver == null ? null : new ContactBodyFrame(contact.PlayerBodyId,
                ReadPose(receiver), ToVec3(receiver.worldCenterOfMass));
            var rootWorld = Frame(root.localToWorldMatrix);
            var structureCenter = otherBody == null ? other.bounds.center : otherBody.worldCenterOfMass;
            var frames = new ContactFrames(playerShape, structureShape, playerPose, rootWorld, ToVec3(structureCenter));
            return contact with { Frames = frames };
        }
        catch (Exception error)
        {
            ReportFailure("contact_geometry_capture", error);
            return contact;
        }
    }

    internal void CaptureStructureSidePlayerContact(Structure structure, Collision collision, string phase)
    {
        if (disposed || structure == null || collision == null || session.LastPhysicsTick is not { } tick)
            return;
        var playerCollider = collision.collider;
        var playerBody = playerCollider == null ? null : playerCollider.attachedRigidbody;
        if (playerBody == null || !receivingBodies.ContainsKey(playerBody.GetInstanceID()) || playerCollider == null)
            return;

        var nativePair = collision.m_Pair;
        var structureCollider = collision.Flipped ? nativePair.otherCollider : nativePair.collider;
        int count = collision.contactCount;
        if (structureCollider == null || count == 0)
            return;
        var pair = new ContactKey(playerCollider.GetInstanceID(), structureCollider.GetInstanceID());
        var entry = FindContactEntry(structure, tick);
        if (entry is null || contacts.TryGetValue(pair, out var existing) &&
            existing.Last.Structure == entry.Key && existing.Last.GeometryTick == tick && existing.Last.Points.Length > 0)
            return;

        var structureBody = structureCollider.attachedRigidbody;
        var points = ReadPlayerContactPoints(collision, structure.transform, count, structureSide: true);
        var observation = new ContactObservation(default, null, phase, pair, PlayerHitbox(pair.PlayerColliderId),
            playerBody.GetInstanceID(), structureBody == null ? null : structureBody.GetInstanceID(), "structure",
            structureCollider.gameObject.layer, entry.Key, entry.Name, points, count, null, !collision.Flipped, tick, null,
            ObservedFixedGameTimeSeconds: Time.inFixedTimeStep ? Time.fixedTimeAsDouble : null,
            RelativeLinearVelocity: TryCollisionLinearVelocity(collision), StructureSideProbe: true);
        session.CaptureContact(Time.frameCount, CaptureContactDetails(observation, entry, playerCollider, structureCollider,
            structureBody, session.ElapsedSeconds));
    }

    public void OnStructureCollision(Structure first, Structure second, Collision collision)
    {
        if (disposed || first == null || second == null || collision == null)
            return;

        if (!LocallyRelevant(first) && !LocallyRelevant(second))
            return;
        if (structureImpactAttempts++ >= MaxStructureImpactsPerTick)
        {
            session.DropStructureImpact(Time.frameCount, "CombatManager.RegisterStructureCollisionEvent",
                CombatCounterpartKey(first), CombatCounterpartKey(second));
            return;
        }
        Promote(first, combatOnly: true);
        Promote(second, combatOnly: true);

        double now = session.ElapsedSeconds;
        double? observedFixedTime = Time.inFixedTimeStep ? Time.fixedTimeAsDouble : null;
        var pair = collision.m_Pair;
        Collider? receiver = collision.Flipped ? pair.otherCollider : pair.collider;
        Collider? other = collision.collider;
        var receiverOwner = receiver == null ? null : FindStructure(receiver);
        var otherOwner = other == null ? null : FindStructure(other);
        bool? receiverIsFirst = ReceiverIsFirst(receiverOwner, otherOwner, first, second);
        Collider? firstCollider = receiverIsFirst switch { true => receiver, false => other, _ => null };
        Collider? secondCollider = receiverIsFirst switch { true => other, false => receiver, _ => null };
        var firstParticipant = Participant(first, firstCollider, out bool firstLimited);
        var secondParticipant = Participant(second, secondCollider, out bool secondLimited);

        int count = collision.contactCount;
        var points = new ImpactPoint[Math.Min(count, MaxContactPoints)];
        for (int i = 0; i < points.Length; i++)
        {
            var point = collision.GetContact(i);
            points[i] = new ImpactPoint(ToVec3(point.point),
                ToVec3(receiverIsFirst == false ? -point.normal : point.normal), point.separation);
        }

        var motion = new ImpactMotion(
            MotionAt(firstParticipant.BodyId, firstParticipant.Structure, now, observedFixedTime),
            MotionAt(secondParticipant.BodyId, secondParticipant.Structure, now, observedFixedTime),
            "registration_prefix", now, firstLimited || secondLimited);
        var callbackPair = new ImpactColliderPair(
            collision.Flipped ? pair.otherColliderInstanceID : pair.colliderInstanceID,
            collision.Flipped ? pair.colliderInstanceID : pair.otherColliderInstanceID,
            AttachedBodyId(receiver), AttachedBodyId(other));
        session.CaptureImpact(Time.frameCount, new StructureImpactObservation(
            default, null, observedFixedTime, firstParticipant, secondParticipant, points, count,
            receiverIsFirst.HasValue ? "toward_first" : "callback_receiver_unresolved", motion, callbackPair,
            TryCollisionLinearVelocity(collision)));
    }

    internal void BeginEnvironmentCollision(Structure structure, Collision collision, string phase)
    {
        if (disposed || terrainLayer is null || collision == null ||
            !TryTracked(structure, out var entry) || entry is null || !IsActive(entry))
            return;
        var surface = collision.collider;
        if (surface == null || surface.gameObject.layer != terrainLayer || FindStructure(surface) != null)
            return;

        var scope = new EnvironmentCollision(entry.Key, entry.Name, collision, surface, phase);
        ActiveEnvironmentCollision = scope;
        if (phase == "enter")
        {
            if (structureImpactAttempts++ < MaxStructureImpactsPerTick)
                scope.Sequence = CaptureEnvironmentImpact(scope);
            else
                session.DropStructureImpact(Time.frameCount, "Structure.OnCollisionEnter", entry.Key);
        }
    }

    private long? DirectEnvironmentImpact(StructureKey key)
    {
        if (ActiveEnvironmentCollision is not { } scope || scope.Key != key)
            return null;
        Guard("structure_environment_impact", () => scope.Sequence ??= CaptureEnvironmentImpact(scope));
        return scope.Sequence;
    }

    private long CaptureEnvironmentImpact(EnvironmentCollision scope)
    {
        var collision = scope.Collision;
        var surface = scope.Surface;
        double now = session.ElapsedSeconds;
        double? observedFixedTime = Time.inFixedTimeStep ? Time.fixedTimeAsDouble : null;
        var pair = collision.m_Pair;
        var receiver = collision.Flipped ? pair.otherCollider : pair.collider;
        int receiverId = collision.Flipped ? pair.otherColliderInstanceID : pair.colliderInstanceID;
        int? bodyId = AttachedBodyId(receiver);
        int? surfaceBodyId = AttachedBodyId(surface);
        int surfaceId = surface.GetInstanceID();
        var points = new ImpactPoint[Math.Min(collision.contactCount, MaxContactPoints)];
        for (int i = 0; i < points.Length; i++)
        {
            var point = collision.GetContact(i);
            points[i] = new ImpactPoint(ToVec3(point.point), ToVec3(point.normal), point.separation);
        }

        return session.CaptureImpact(Time.frameCount, new StructureImpactObservation(default, null, observedFixedTime,
            new ImpactParticipant(scope.Key, scope.Name, receiverId, bodyId, null),
            new ImpactParticipant(null, surface.name, surfaceId, surfaceBodyId, null),
            points, collision.contactCount, "toward_first", new ImpactMotion(
                MotionAt(bodyId, scope.Key, now, observedFixedTime), null, "structure_collision_callback", now, false),
            new ImpactColliderPair(receiverId, surfaceId, bodyId, surfaceBodyId), TryCollisionLinearVelocity(collision),
            new EnvironmentContact(surface.gameObject.layer, scope.Phase, surfaceBodyId is null)));
    }

    private Vec3? TryCollisionLinearVelocity(Collision collision)
    {
        try
        {
            // This installed build strips Collision.relativeVelocity. Preserve the native pair order here.
            var velocity = ToVec3(collision.m_Header.m_RelativeVelocity);
            return Finite(velocity) ? velocity : null;
        }
        catch (Exception error)
        {
            ReportFailure("collision_linear_velocity", error);
            return null;
        }
    }

    private static bool? ReceiverIsFirst(Structure? receiver, Structure? other, Structure first, Structure second)
    {
        if (receiver == first && (other == second || other == null) || other == second && receiver == null)
            return true;
        if (receiver == second && (other == first || other == null) || other == first && receiver == null)
            return false;
        return null;
    }

    private static int? AttachedBodyId(Collider? collider)
    {
        var attached = collider == null ? null : collider.attachedRigidbody;
        return attached == null ? null : attached.GetInstanceID();
    }

    private bool LocallyRelevant(Structure structure) =>
        TryTracked(structure, out var entry) && entry is { CombatOnly: false } && IsActive(entry) &&
        entry.Until >= session.NextPhysicsTick - 1;

    private ImpactParticipant Participant(Structure structure, Collider? collider, out bool limited)
    {
        var key = CombatCounterpartKey(structure);
        Rigidbody? attached = collider == null ? null : collider.attachedRigidbody;
        limited = key is { } lifetime && !returned.Contains(lifetime.InstanceId) && RequestBody(attached, structure, lifetime);
        var descriptor = collider == null || key is null ? null :
            CaptureCollider(collider, attached, key, structure.transform, structure.Rigidbody);

        return new ImpactParticipant(key, structure.name,
            collider == null ? null : collider.GetInstanceID(), attached == null ? null : attached.GetInstanceID(), descriptor);
    }

    private ColliderFrameReference CaptureCollider(Collider collider, Rigidbody? attached, StructureKey? key,
        Transform root, Rigidbody? reference, bool sampleMesh = false)
    {
        var transform = collider.transform;
        Transform? parent = transform.parent;
        var colliderToRoot = Frame(root == transform ? Matrix4x4.identity : root.worldToLocalMatrix * transform.localToWorldMatrix);
        AffineFrame? rootToReference = null;
        if (reference != null)
        {
            var bodyTransform = reference.transform;
            // Exclude scale from the reference frame so this map composes with a recorded rigidbody pose.
            var worldToReference = Matrix4x4.TRS(bodyTransform.position, bodyTransform.rotation, Vector3.one).inverse;
            rootToReference = Frame(worldToReference * root.localToWorldMatrix);
        }

        int colliderId = collider.GetInstanceID();
        var identity = (colliderId, key);
        descriptors.TryGetValue(identity, out var definition);
        var shape = ReadShape(collider);
        if (shape is { Convex: true, MeshBounds: { } bounds })
        {
            var previous = definition?.Descriptor.Shape;
            if (sampleMesh &&
                FlightBakedWireframes.Match(shape, session.GameVersion, ToVec3(transform.lossyScale)) is { } edges)
                shape = shape with { MeshEdges = edges };
            else if ((previous is { MeshEdges: not null } or { MeshContours: not null }) &&
                (!sampleMesh || previous.MeshEdges is null) &&
                previous with { MeshContours = null, MeshEdges = null } == shape)
                shape = previous;
            else if (sampleMesh)
                shape = shape with { MeshContours = CaptureMeshContours(collider, attached, bounds) };
        }

        var current = new ColliderDescriptor(colliderId,
            attached == null ? null : attached.GetInstanceID(), key, root.GetInstanceID(),
            reference == null ? null : reference.GetInstanceID(), parent == null ? null : parent.GetInstanceID(),
            shape, colliderToRoot, rootToReference);
        if (definition is null ||
            definition.Descriptor != current with
            {
                ColliderToRoot = definition.Descriptor.ColliderToRoot,
                RootToReferenceBody = definition.Descriptor.RootToReferenceBody
            })
        {
            definition = session.CaptureCollider(Time.frameCount, current);
            descriptors[identity] = definition;
        }

        var baseline = definition.Descriptor;
        return new ColliderFrameReference(definition.Stamp.Sequence,
            NearlyEqual(baseline.ColliderToRoot, colliderToRoot) ? null : colliderToRoot,
            rootToReference is { } pose && !NearlyEqual(baseline.RootToReferenceBody, pose) ? pose : null, definition);
    }

    private ColliderShape ReadShape(Collider collider)
    {
        if (collider.TryCast<BoxCollider>() is { } box)
            return new ColliderShape("box", Center: ToVec3(box.center), Size: ToVec3(box.size));
        if (collider.TryCast<SphereCollider>() is { } sphere)
            return new ColliderShape("sphere", Center: ToVec3(sphere.center), Radius: sphere.radius);
        if (collider.TryCast<CapsuleCollider>() is { } capsule)
            return new ColliderShape("capsule", Center: ToVec3(capsule.center), Radius: capsule.radius,
                Height: capsule.height, Direction: capsule.direction);
        if (collider.TryCast<MeshCollider>() is { } mesh)
        {
            var asset = mesh.sharedMesh;
            var shape = new ColliderShape("mesh", MeshId: asset == null ? null : asset.GetInstanceID(),
                MeshName: asset == null ? null : asset.name, Convex: mesh.convex, CookingOptions: mesh.cookingOptions.ToString());
            if (asset != null && shape.Convex == true && FlightBakedWireframes.HasBakedOutline(shape.MeshName))
            {
                try
                {
                    var bounds = asset.bounds;
                    shape = shape with { MeshBounds = new MeshLocalBounds(ToVec3(bounds.center), ToVec3(bounds.size)) };
                }
                catch (Exception error)
                {
                    ReportFailure("structure_mesh_bounds", error);
                }
            }
            return shape;
        }
        return new ColliderShape("unsupported");
    }

    private Vec3[][] CaptureMeshContours(Collider collider, Rigidbody? attached, MeshLocalBounds bounds)
    {
        try
        {
            var colliderToWorld = ColliderPhysicsFrame(collider, attached);
            var worldToCollider = colliderToWorld.inverse;
            return FlightMeshContours.Sample(bounds, (start, end) =>
            {
                if (CastCollider(collider, colliderToWorld,
                    new Vector3(start.X, start.Y, start.Z), new Vector3(end.X, end.Y, end.Z)) is not { } hit)
                    return null;
                var local = worldToCollider.MultiplyPoint3x4(hit.point);
                return new System.Numerics.Vector3(local.x, local.y, local.z);
            });
        }
        catch (Exception error)
        {
            ReportFailure("structure_mesh_contours", error);
            return Array.Empty<Vec3[]>();
        }
    }

    private static Matrix4x4 ColliderPhysicsFrame(Collider collider, Rigidbody? attached)
    {
        var colliderToWorld = collider.transform.localToWorldMatrix;
        if (attached == null)
            return colliderToWorld;

        var bodyTransform = attached.transform;
        var renderBody = Matrix4x4.TRS(bodyTransform.position, bodyTransform.rotation, Vector3.one);
        var physicsBody = Matrix4x4.TRS(attached.position, attached.rotation, Vector3.one);
        return physicsBody * renderBody.inverse * colliderToWorld;
    }

    private static RaycastHit? CastCollider(Collider collider, Matrix4x4 colliderToWorld, Vector3 start, Vector3 end)
    {
        var origin = colliderToWorld.MultiplyPoint3x4(start);
        var delta = colliderToWorld.MultiplyPoint3x4(end) - origin;
        float distance = delta.magnitude;
        return distance > 0 && collider.Raycast(new Ray(origin, delta / distance), out var hit, distance)
            ? hit : null;
    }

    private static AffineFrame Frame(Matrix4x4 matrix) => new(
        ToVec3(matrix.MultiplyPoint3x4(Vector3.zero)), ToVec3(matrix.MultiplyVector(Vector3.right)),
        ToVec3(matrix.MultiplyVector(Vector3.up)), ToVec3(matrix.MultiplyVector(Vector3.forward)));

    private static bool NearlyEqual(AffineFrame? baseline, AffineFrame frame) =>
        baseline is { } original && Near(original.Origin, frame.Origin) && Near(original.XAxis, frame.XAxis) &&
        Near(original.YAxis, frame.YAxis) && Near(original.ZAxis, frame.ZAxis);

    private static bool Near(Vec3 a, Vec3 b) =>
        (new Vector3(a.X, a.Y, a.Z) - new Vector3(b.X, b.Y, b.Z)).sqrMagnitude <= FrameToleranceSquared;

    private static Pose ReadPose(Rigidbody rigidbody) => new(ToVec3(rigidbody.position), ToQuat(rigidbody.rotation));

    private static BodyKinematics ReadBody(Rigidbody rigidbody) => new(rigidbody.GetInstanceID(), ReadPose(rigidbody),
        ToVec3(rigidbody.worldCenterOfMass), ToVec3(rigidbody.linearVelocity), ToVec3(rigidbody.angularVelocity));

    private static bool Valid(BodyKinematics motion)
    {
        var rotation = motion.WorldPose.Rotation;
        return Finite(motion.WorldPose.Position) && Finite(motion.WorldCenterOfMass) &&
            Finite(motion.LinearVelocityWorld) && Finite(motion.AngularVelocityWorld) &&
            float.IsFinite(rotation.X) && float.IsFinite(rotation.Y) && float.IsFinite(rotation.Z) && float.IsFinite(rotation.W) &&
            rotation.X * rotation.X + rotation.Y * rotation.Y + rotation.Z * rotation.Z + rotation.W * rotation.W > 0.000001f;
    }

    private static bool Finite(Vec3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
