using Il2CppRUMBLE.MoveSystem;
using UnityEngine;
using static RumbleFlightTrainer.FlightContactPatches;
using static RumbleFlightTrainer.FlightRuntime;

namespace RumbleFlightTrainer;

internal sealed partial class StructureTelemetry
{
    private IronBootsTarget? ironBootsTarget;

    private sealed record IronBootsTarget(Structure Structure, ContactKey Pair, bool FootContact,
        Rigidbody? VrBody, Transform TrackingOrigin, IronBootsApproach Approach);

    private IronBootsBreakEvidence? MeasureIronBootsBreak(Structure structure, CombatMeasurement motion)
    {
        var playerVr = controller.PlayerVR;
        var manager = CombatManager.instance;
        if (playerVr == null || manager == null)
            return null;

        return IronBootsBreakEvidence.Measure(motion, Vector(ToVec3(playerVr.GetFeetPosition())),
            Vector(ToVec3(structure.transform.position)), manager.playerJumpEventDotFactorPlayerVelocity, manager.minDotForJumpEvent);
    }

    private void SampleIronBoots(long tick, double observedAt, ContactObservation contact, Tracked entry,
        Collider? playerCollider, Collider structureCollider)
    {
        bool footContact = contact.PlayerHitbox == "foot";
        if (contact.PlayerHitbox is not ("foot" or "capsule"))
            return;

        try
        {
            if (!ColliderActive(playerCollider) || structureCollider.TryCast<BoxCollider>() is not { } box)
                return;

            if (ironBootsTarget is { } current && current.Pair != contact.Pair &&
                current.Approach.IsCurrent(tick, session.Segment, observedAt) && IsIronBootsContactActive(current) &&
                (current.FootContact && !footContact || current.FootContact == footContact &&
                    current.Approach.Structure.PoolGeneration <= entry.Key.PoolGeneration))
                return;

            var playerPhysics = controller.PlayerPhysics;
            var playerVr = controller.PlayerVR;
            var vrBody = playerPhysics?.VRrigidbody;
            var trackingOrigin = playerVr?.Origin;
            var manager = CombatManager.instance;
            if (playerVr == null || trackingOrigin == null)
                return;

            var feet = Vector(ToVec3(playerVr.GetFeetPosition()));
            if (IronBootsApproach.FindFootContact(contact, body.worldCenterOfMass.y, feet) is not { } point)
                return;

            var origin = Vector(ToVec3(entry.Structure.transform.position));
            var vrVelocity = vrBody == null ? System.Numerics.Vector3.Zero : Vector(ToVec3(vrBody.linearVelocity));
            var alignment = manager == null ? null : IronBootsAlignment.Evaluate(vrVelocity, feet, origin,
                manager.playerJumpEventDotFactorPlayerVelocity, manager.minDotForJumpEvent);

            var boxFrame = HudColliderFrame.From(Frame(ColliderPhysicsFrame(box, box.attachedRigidbody)));
            var boxShape = new ColliderShape("box", Center: ToVec3(box.center), Size: ToVec3(box.size));
            var player = ReadHudPlayerCollider(footContact ? playerPhysics?.pillBodyCollider : playerCollider);
            var foot = footContact ? ReadHudPlayerCollider(playerCollider) : null;
            var approach = new IronBootsApproach(tick, session.Segment, observedAt, entry.Key, feet, origin, vrVelocity,
                new HudStructureCollider(boxShape, boxFrame), alignment, player, point, foot);
            ironBootsTarget = new IronBootsTarget(entry.Structure, contact.Pair, footContact, vrBody, trackingOrigin, approach);
        }
        catch (Exception error)
        {
            ReportFailure("iron_boots_contact", error);
        }
    }

    public IronBootsDisplay? ReadIronBoots(Transform? trackingOrigin)
    {
        try
        {
            if (ironBootsTarget is not { } target || trackingOrigin == null || trackingOrigin != target.TrackingOrigin)
                return null;

            var physics = controller.PlayerPhysics;
            if (physics == null || physics.VRrigidbody != target.VrBody)
                return null;

            return IronBootsDisplay.Read(target.Approach, session.Segment, IsIronBootsContactActive(target));
        }
        catch (Exception error)
        {
            ReportFailure("iron_boots_read", error);
            return null;
        }
    }

    private bool IsIronBootsContactActive(IronBootsTarget target)
    {
        if (!contacts.TryGetValue(target.Pair, out var contact) || contact.Last.Structure != target.Approach.Structure ||
            !ColliderActive(contact.PlayerCollider) || !ColliderActive(contact.OtherCollider) ||
            target.Structure == null || !target.Structure.gameObject.activeInHierarchy)
            return false;

        var physics = controller.PlayerPhysics;
        Collider? playerCollider = target.FootContact ? physics?.FootCollider : physics?.pillBodyCollider;
        return playerCollider == contact.PlayerCollider && IronBootsApproach.FindFootContact(
            contact.Last, body.worldCenterOfMass.y, target.Approach.Feet) is not null;
    }
}
