using System.Numerics;

namespace RumbleFlightTrainer;

internal static class FlightHitWarningSweep
{
    public const float LookAheadSeconds = 0.35f;
    public const float DefaultPullAcceleration = 2f;
    public const float SupportNormalY = 0.65f;

    public static Vector3 PullTowardPlayer(Vector3 toPlayer, float strength, bool grounded)
    {
        if (grounded)
            toPlayer.Y = 0f;
        float distance = toPlayer.Length();
        return distance > 1e-4f && float.IsFinite(distance) && float.IsFinite(strength) && strength > 0f
            ? toPlayer / distance * strength : Vector3.Zero;
    }

    public static bool IsGentleSupport(Vector3 normalTowardPlayer, Vector3 relativePointVelocity) =>
        normalTowardPlayer.Y >= SupportNormalY && relativePointVelocity.LengthSquared() <= 1.75f * 1.75f &&
        Vector3.Dot(relativePointVelocity, normalTowardPlayer) <= 1.1f;

    public static bool TryOverlapWindow(Vector3 offset, Vector3 combinedExtents, Vector3 relativeVelocity,
        out float enter, out float exit, float horizon = LookAheadSeconds)
    {
        exit = horizon;
        enter = 0f;
        return FlightContactPatches.IsFinite(offset) && FlightContactPatches.IsFinite(combinedExtents) &&
            FlightContactPatches.IsFinite(relativeVelocity) &&
            ClipAxis(offset.X, combinedExtents.X, relativeVelocity.X, ref enter, ref exit) &&
            ClipAxis(offset.Y, combinedExtents.Y, relativeVelocity.Y, ref enter, ref exit) &&
            ClipAxis(offset.Z, combinedExtents.Z, relativeVelocity.Z, ref enter, ref exit);
    }

    private static bool ClipAxis(float offset, float extent, float velocity, ref float enter, ref float exit)
    {
        if (velocity == 0f)
            return MathF.Abs(offset) < extent;

        float near = (-extent - offset) / velocity;
        float far = (extent - offset) / velocity;
        enter = MathF.Max(enter, MathF.Min(near, far));
        exit = MathF.Min(exit, MathF.Max(near, far));
        return exit > enter;
    }
}

internal readonly record struct HitWarningDrive(Vector3 Acceleration, float Duration)
{
    public static HitWarningDrive Forecast(Vector3 changePerStep, float step, bool queued, bool repeating)
    {
        if (!FlightContactPatches.IsFinite(changePerStep) || changePerStep.LengthSquared() <= 1e-8f ||
            !float.IsFinite(step) || step <= 0f || !queued && !repeating)
            return default;
        float duration = repeating ? MathF.Max(step, MathF.Min(step * 3f, 0.075f)) : step;
        return new HitWarningDrive(changePerStep / step, duration);
    }

    public Vector3 VelocityChange(float seconds) => Acceleration * MathF.Min(seconds, Duration);

    public Vector3 Displacement(float seconds)
    {
        float active = MathF.Min(seconds, Duration);
        return Acceleration * (active * (seconds - 0.5f * active));
    }
}

internal readonly record struct HitWarningMotion(Vector3 Velocity, Vector3 Acceleration, Vector3 AngularVelocity,
    float RotationRadius, HitWarningDrive Drive = default)
{
    public Vector3 Displacement(float seconds) => seconds * (Velocity + 0.5f * seconds * Acceleration) +
        Drive.Displacement(seconds);

    public Vector3 VelocityAt(float seconds) => Velocity + seconds * Acceleration + Drive.VelocityChange(seconds);

    public HitWarningMotion RelativeTo(Vector3 velocity, Vector3 acceleration) =>
        this with { Velocity = Velocity - velocity, Acceleration = Acceleration - acceleration };

    public Quaternion RotationAt(float seconds)
    {
        float angularSpeed = AngularVelocity.Length();
        return angularSpeed > 1e-4f
            ? Quaternion.CreateFromAxisAngle(AngularVelocity / angularSpeed, seconds * angularSpeed)
            : Quaternion.Identity;
    }

    public bool TryOverlapWindow(Vector3 offset, Vector3 combinedExtents, float horizon, out float enter, out float exit)
    {
        float angle = MathF.Min(MathF.PI, AngularVelocity.Length() * horizon);
        float rotationMargin = 2f * RotationRadius * MathF.Sin(angle * 0.5f);
        var margin = Vector3.Abs(0.5f * horizon * horizon * Acceleration) +
            Vector3.Abs(Drive.Displacement(horizon)) + new Vector3(rotationMargin);
        if (Velocity.LengthSquared() > 1e-8f || margin.LengthSquared() > 1e-8f)
            return FlightHitWarningSweep.TryOverlapWindow(offset, combinedExtents + margin, Velocity, out enter, out exit, horizon);
        enter = exit = 0f;
        return false;
    }

    public int SampleCount(float enter, float exit)
    {
        float speed = MathF.Max(VelocityAt(enter).Length(), VelocityAt(exit).Length()) +
            RotationRadius * AngularVelocity.Length();
        if (Drive.Duration > enter && Drive.Duration < exit)
            speed = MathF.Max(speed, VelocityAt(Drive.Duration).Length() + RotationRadius * AngularVelocity.Length());
        return (int)Math.Clamp(MathF.Ceiling((exit - enter) * speed / 0.10f), 8f, 16f);
    }
}
