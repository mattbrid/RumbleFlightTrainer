using Il2CppRUMBLE.MoveSystem;
using UnityEngine;

namespace RumbleFlightTrainer;

internal sealed partial class StructureTelemetry
{
    public bool ObserveHudModifierSteps { get; private set; }

    internal readonly record struct HudModifierStep(StructureTelemetry Owner, StructureKey Key, Rigidbody Body,
        IntPtr Routine, string Modifier, double FixedTime, float StepSeconds, Vector3 ForceBefore);

    private readonly record struct HudModifierForce(IntPtr Routine, string Modifier, int BodyId, double FixedTime,
        double ObservedSeconds, float StepSeconds, Vector3 VelocityChange, bool Repeated, bool Continues);

    public HudModifierStep? BeginHudModifierStep(IntPtr routine, string modifier, IProcessor? processor,
        StackConfiguration? configuration, Structure? structure, Rigidbody? target)
    {
        if (Physics.simulationMode != SimulationMode.FixedUpdate ||
            !IsLocalProcessor(processor) && !IsLocalProcessor(configuration?.Processor))
            return null;

        if (structure == null)
            structure = ModifierStructure(configuration);
        if (structure == null || Promote(structure) is not { Destroyed: false } entry)
            return null;
        if (target == null)
            target = structure.Rigidbody;
        float step = Time.fixedDeltaTime;
        if (target == null || target.isKinematic || !float.IsFinite(step) || step <= 0f)
            return null;

        return new HudModifierStep(this, entry.Key, target, routine, modifier, Time.fixedTimeAsDouble,
            step, target.GetAccumulatedForce(step));
    }

    public void EndHudModifierStep(HudModifierStep step, bool continues)
    {
        if (!tracked.TryGetValue(step.Key, out var entry) || entry.Destroyed || step.Body == null)
            return;

        float mass = step.Body.mass;
        if (!float.IsFinite(mass) || mass <= 0f)
            return;
        int bodyId = step.Body.GetInstanceID();
        var delta = (step.Body.GetAccumulatedForce(step.StepSeconds) - step.ForceBefore) * (step.StepSeconds / mass);
        HudModifierForce? prior = entry.ModifierForce is { } previous &&
            previous.Routine == step.Routine && previous.BodyId == bodyId ? previous : null;
        if (!float.IsFinite(delta.sqrMagnitude) || delta.sqrMagnitude <= 1e-8f)
        {
            if (prior is { } lastCommand && (!continues || step.FixedTime > lastCommand.FixedTime))
                entry.ModifierForce = lastCommand with { Continues = false };
            return;
        }

        bool repeated = false;
        if (prior is { } last)
        {
            double interval = step.FixedTime - last.FixedTime;
            if (interval == 0d)
                delta += last.VelocityChange;
            repeated = interval == 0d ? last.Repeated :
                interval >= step.StepSeconds * 0.5 && interval <= step.StepSeconds * 1.5 && last.Continues;
        }
        entry.ModifierForce = new HudModifierForce(step.Routine, step.Modifier, bodyId, step.FixedTime,
            session.ElapsedSeconds, step.StepSeconds, delta, repeated, continues);
    }

    private HitWarningDrive ReadHudModifierDrive(StructureKey key, Rigidbody target, bool grounded)
    {
        if (!tracked.TryGetValue(key, out var entry) || entry.ModifierForce is not { } force ||
            force.BodyId != target.GetInstanceID() || session.ElapsedSeconds - force.ObservedSeconds > 0.10)
            return default;

        // The command may already be reflected in linearVelocity. Check the upcoming step before
        // predicting a one-off command, and use consecutive commands to forecast an ongoing push.
        double fixedAge = Time.fixedTimeAsDouble - force.FixedTime;
        if (fixedAge < 0d || fixedAge > force.StepSeconds * 1.5)
            return default;
        bool queued = fixedAge == 0d &&
            Vector3.Dot(target.GetAccumulatedForce(force.StepSeconds), force.VelocityChange) > 0f;
        var change = Numerics(force.VelocityChange);
        if (grounded)
            change.Y = 0f;
        return HitWarningDrive.Forecast(change, force.StepSeconds, queued, force.Repeated && force.Continues);
    }
}
