using System.Numerics;

namespace RumbleFlightTrainer;

internal static class FlightHitWarningSweep
{
    public const float LookAheadSeconds = 0.35f;
    public const float DefaultPullForce = 300f;
    public const float SupportNormalY = 0.65f;

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

internal readonly record struct HitWarningTurn(Vector3 Forward, Vector3 Sideways, float Speed, float Rate, float AngleLimit)
{
    private const float MinimumSpeed = 0.5f;
    public bool IsActive => Rate > 0f;

    public static HitWarningTurn TowardPlayer(Vector3 velocity, Vector3 toPlayer, float force, float mass, bool grounded)
    {
        if (force <= 0f || mass <= 0f || !float.IsFinite(force) || !float.IsFinite(mass))
            return default;
        if (grounded)
        {
            velocity.Y = 0f;
            toPlayer.Y = 0f;
        }
        float speed = velocity.Length();
        if (speed < MinimumSpeed || !float.IsFinite(speed))
            return default;

        var forward = velocity / speed;
        float along = Vector3.Dot(toPlayer, forward);
        var sideways = toPlayer - along * forward;
        float distance = sideways.Length();
        float rate = force / mass / speed;
        if (distance <= 1e-4f || !float.IsFinite(distance) || rate <= 0f || !float.IsFinite(rate))
            return default;

        // Stop once the original bearing to the player is reached, or after a quarter turn.
        return new HitWarningTurn(forward, sideways / distance, speed, rate, MathF.Atan2(distance, MathF.Max(0f, along)));
    }

    public Vector3 VelocityChange(float seconds)
    {
        if (!IsActive)
            return Vector3.Zero;
        float angle = MathF.Min(seconds * Rate, AngleLimit);
        float halfSine = MathF.Sin(angle * 0.5f);
        return Speed * (MathF.Sin(angle) * Sideways - 2f * halfSine * halfSine * Forward);
    }

    public Vector3 Displacement(float seconds)
    {
        var (forward, sideways) = DisplacementComponents(seconds);
        return forward * Forward + sideways * Sideways;
    }

    public Vector3 DisplacementBound(float seconds)
    {
        var (forward, sideways) = DisplacementComponents(seconds);
        // The two terms can cancel along a world axis. Bound each before adding.
        return MathF.Abs(forward) * Vector3.Abs(Forward) + sideways * Vector3.Abs(Sideways);
    }

    private (float Forward, float Sideways) DisplacementComponents(float seconds)
    {
        if (!IsActive || seconds <= 0f)
            return default;
        float turnTime = MathF.Min(seconds, AngleLimit / Rate);
        float angle = turnTime * Rate;
        if (angle <= 0f)
            return default;
        float sine = MathF.Sin(angle);
        float halfSine = MathF.Sin(angle * 0.5f);
        float distance = Speed * turnTime;
        float coastDistance = Speed * (seconds - turnTime);
        return (distance * (sine / angle - 1f) - coastDistance * (2f * halfSine * halfSine),
            distance * (2f * halfSine * (halfSine / angle)) + coastDistance * sine);
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
    float RotationRadius, HitWarningDrive Drive = default, HitWarningTurn Turn = default)
{
    public Vector3 Displacement(float seconds) => seconds * (Velocity + 0.5f * seconds * Acceleration) +
        Drive.Displacement(seconds) + Turn.Displacement(seconds);

    public Vector3 VelocityAt(float seconds) => Velocity + seconds * Acceleration + Drive.VelocityChange(seconds) +
        Turn.VelocityChange(seconds);

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
            Vector3.Abs(Drive.Displacement(horizon)) + Turn.DisplacementBound(horizon) + new Vector3(rotationMargin);
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
