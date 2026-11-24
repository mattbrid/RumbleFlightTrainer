using Il2CppRUMBLE.MoveSystem;
using UnityEngine;
using static RumbleFlightTrainer.FlightContactPatches;
using static RumbleFlightTrainer.FlightRuntime;

namespace RumbleFlightTrainer;

internal sealed partial class StructureTelemetry
{
    private readonly IronBootsContactGate ironBootsGate;
    private (long Tick, Rigidbody Body, float VerticalVelocity)? ironBootsPreSolve;
    private IronBootsTarget? ironBootsTarget;

    private sealed record IronBootsTarget(Structure? Structure, ContactKey? Pair, bool FootContact,
        Rigidbody? VrBody, Transform TrackingOrigin, IronBootsApproach Approach, bool ShowOnLoss);

    private IronBootsBreakEvidence? MeasureIronBootsBreak(Tracked entry, CombatMeasurement motion,
        out IronBootsApproach? jumpDiagram)
    {
        jumpDiagram = null;
        if (session.LastPhysicsTick is not { } tick)
            return null;
        if (ironBootsGate.ObserveJump(entry.Key, tick))
            StartIronBootsContact();
        if (!ironBootsGate.Allows(entry.Key, motion.PlayerVrBody?.Positional.Y))
            return null;

        var playerVr = controller.PlayerVR;
        var manager = CombatManager.instance;
        if (playerVr == null || manager == null)
            return null;

        var feet = Vector(ToVec3(playerVr.GetFeetPosition()));
        var origin = Vector(ToVec3(entry.Structure.transform.position));
        var evidence = IronBootsBreakEvidence.Measure(motion, feet, origin,
            manager.playerJumpEventDotFactorPlayerVelocity, manager.minDotForJumpEvent);
        if (ironBootsEnabled && motion.PlayerVrBody is { } velocity && IsFinite(feet) && IsFinite(origin))
        {
            try
            {
                var collider = entry.Structure.Collider;
                if (ColliderActive(collider) && collider.TryCast<BoxCollider>() is { } box)
                {
                    var physics = controller.PlayerPhysics;
                    jumpDiagram = new IronBootsApproach(tick, session.Segment, session.ElapsedSeconds, entry.Key,
                        feet, origin, Vector(velocity.Positional), ReadIronBootsBox(box), evidence?.Alignment,
                        ReadHudPlayerCollider(physics?.pillBodyCollider), Foot: ReadHudPlayerCollider(physics?.FootCollider),
                        EstimatedContact: true);
                }
            }
            catch (Exception error)
            {
                ReportFailure("iron_boots_jump_diagram", error);
            }
        }
        return evidence;
    }

    private static HudStructureCollider ReadIronBootsBox(BoxCollider box)
    {
        var frame = HudColliderFrame.From(Frame(ColliderPhysicsFrame(box, box.attachedRigidbody)));
        return new HudStructureCollider(new ColliderShape("box", Center: ToVec3(box.center), Size: ToVec3(box.size)), frame);
    }

    private void SampleIronBoots(long tick, double observedAt, ContactObservation contact, Tracked entry,
        Collider? playerCollider, Collider structureCollider)
    {
        try
        {
            if (!ColliderActive(playerCollider) || !IronBootsApproach.HasLoadedContact(contact))
                return;

            var playerPhysics = controller.PlayerPhysics;
            var playerVr = controller.PlayerVR;
            var vrBody = playerPhysics?.VRrigidbody;
            System.Numerics.Vector3? feet = playerVr != null && contact.PlayerHitbox is "foot" or "capsule"
                ? Vector(ToVec3(playerVr.GetFeetPosition())) : null;
            var point = feet is { } currentFeet ? IronBootsApproach.FindFootContact(contact, body.worldCenterOfMass.y, currentFeet) : null;
            if (ironBootsGate.ObserveContact(contact.Pair, entry.Key, tick, point is not null))
                StartIronBootsContact();
            if (!ironBootsEnabled || !ironBootsGate.IsCurrentFootContact(entry.Key) || point is null ||
                feet is not { } measuredFeet || structureCollider.TryCast<BoxCollider>() is not { } box)
                return;

            bool footContact = contact.PlayerHitbox == "foot";
            if (ironBootsTarget is { } current && current.Pair != contact.Pair &&
                current.Approach.IsCurrent(tick, session.Segment, observedAt) && IsIronBootsContactActive(current) &&
                (current.FootContact || !footContact))
                return;
            var trackingOrigin = playerVr?.Origin;
            if (trackingOrigin == null)
                return;
            var manager = CombatManager.instance;
            var origin = Vector(ToVec3(entry.Structure.transform.position));
            var vrVelocity = vrBody == null ? System.Numerics.Vector3.Zero : Vector(ToVec3(vrBody.linearVelocity));
            var alignment = manager == null ? null : IronBootsAlignment.Evaluate(vrVelocity, measuredFeet, origin,
                manager.playerJumpEventDotFactorPlayerVelocity, manager.minDotForJumpEvent);

            var player = ReadHudPlayerCollider(footContact ? playerPhysics?.pillBodyCollider : playerCollider);
            var foot = footContact ? ReadHudPlayerCollider(playerCollider) : null;
            var approach = new IronBootsApproach(tick, session.Segment, observedAt, entry.Key, measuredFeet, origin, vrVelocity,
                ReadIronBootsBox(box), alignment, player, point, foot);
            bool belowThreshold = ironBootsGate.Allows(entry.Key, ReadIronBootsVerticalVelocity(vrBody, tick, vrVelocity.Y));
            ironBootsTarget = new IronBootsTarget(entry.Structure, contact.Pair, footContact, vrBody, trackingOrigin, approach, belowThreshold);
        }
        catch (Exception error)
        {
            ReportFailure("iron_boots_contact", error);
        }
    }

    private void SampleIronBootsVelocity(long tick)
    {
        ironBootsPreSolve = null;
        try
        {
            var vrBody = controller.PlayerPhysics?.VRrigidbody;
            if (vrBody != null)
                ironBootsPreSolve = (tick, vrBody, vrBody.linearVelocity.y);
        }
        catch (Exception error)
        {
            ReportFailure("iron_boots_vertical_velocity", error);
        }
    }

    private float? ReadIronBootsVerticalVelocity(Rigidbody? vrBody, long? tick, float? fallback = null)
    {
        if (vrBody == null)
            return null;
        return ironBootsPreSolve is { } sample && sample.Tick == tick && sample.Body == vrBody
            ? sample.VerticalVelocity : fallback ?? vrBody.linearVelocity.y;
    }

    private void StartIronBootsContact()
    {
        ironBootsTarget = null;
        session.StartIronBootsContact(Time.frameCount);
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

            bool active = IsIronBootsContactActive(target);
            bool visible = target.ShowOnLoss;
            if (active)
            {
                visible = ironBootsGate.Allows(target.Approach.Structure,
                    ReadIronBootsVerticalVelocity(target.VrBody, session.LastPhysicsTick));
                if (visible != target.ShowOnLoss)
                    ironBootsTarget = target with { ShowOnLoss = visible };
            }
            return visible ? IronBootsDisplay.Read(target.Approach, session.Segment, active) : null;
        }
        catch (Exception error)
        {
            ReportFailure("iron_boots_read", error);
            return null;
        }
    }

    public void RetainIronBootsBreak(GameplayObservation destruction)
    {
        if (!ironBootsEnabled)
            return;

        var previous = ironBootsTarget?.Approach;
        var sample = IronBootsDisplay.AfterBreak(destruction, previous);
        if (ReferenceEquals(sample, previous))
            return;

        ironBootsTarget = null;
        var origin = controller.PlayerVR?.Origin;
        if (sample is not null && origin != null)
            ironBootsTarget = new IronBootsTarget(null, null, false, controller.PlayerPhysics?.VRrigidbody, origin, sample, true);
    }

    private bool IsIronBootsContactActive(IronBootsTarget target)
    {
        if (target.Pair is not { } key || !contacts.TryGetValue(key, out var contact) ||
            contact.Last.Structure != target.Approach.Structure ||
            !ColliderActive(contact.PlayerCollider) || !ColliderActive(contact.OtherCollider) ||
            target.Structure == null || !target.Structure.gameObject.activeInHierarchy)
            return false;

        var physics = controller.PlayerPhysics;
        Collider? playerCollider = target.FootContact ? physics?.FootCollider : physics?.pillBodyCollider;
        return playerCollider == contact.PlayerCollider && IronBootsApproach.FindFootContact(
            contact.Last, body.worldCenterOfMass.y, target.Approach.Feet) is not null;
    }
}
