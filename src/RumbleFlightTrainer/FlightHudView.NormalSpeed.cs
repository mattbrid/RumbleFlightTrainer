using UnityEngine;

namespace RumbleFlightTrainer;

internal sealed partial class FlightHudView
{
    private readonly List<Arrow> normalSpeedArrows = new();

    private void DrawNormalSpeedArrows(FlightHudFrame frame, IReadOnlyDictionary<StructureKey, HudStructureVelocity>? velocities,
        Quaternion worldToView, Quaternion bodyToView)
    {
        int used = 0;
        if (showNormalSpeed && velocities is not null)
            foreach (var structure in frame.Structures)
                if (velocities.TryGetValue(structure.Key, out var sample) && sample.Normal is { } normal)
                    DrawNormalSpeedArrow(structure.Key, sample.CenterOffset, normal.Value, worldToView,
                        normal.EstimateGapSquared.HasValue, ref used);

        if (showNormalSpeedOnBreak)
            foreach (var broken in frame.Breaks)
                if (broken.PlayerContact is { Placement: { } placement, NormalMetersPerSecond: { } speed } impact)
                    DrawNormalSpeedArrow(broken.Key, ToUnity(placement.Offset(impact.CenterWorld)), speed,
                        LossRotation(placement, bodyToView), impact.LinearEstimate, ref used);

        HideUnused(normalSpeedArrows, used);
    }

    private void DrawNormalSpeedArrow(StructureKey key, Vector3 centerOffset, float normalSpeed, Quaternion rotation,
        bool estimated, ref int used)
    {
        float speed = Mathf.Abs(normalSpeed);
        float distance = centerOffset.magnitude;
        if (!float.IsFinite(speed) || speed == 0f || !float.IsFinite(distance) || distance < 0.0001f)
            return;

        var color = estimated ? dimStructureMaterials[structureColors[key]] : StructureMaterial(key);
        var position = rotation * centerOffset;
        // Use the magnitude for length and the player reference for direction, even when separating.
        float length = Mathf.Min(speed * VelocityArrowSeconds * HudContactPlacement.Scale, 0.14f);
        DrawMotionArrow(normalSpeedArrows, ref used, color, position, -position / distance, length, DrawOrder.StructureNormalSpeed);
    }
}
