using Il2CppTMPro;
using UnityEngine;

namespace RumbleFlightTrainer;

internal sealed partial class FlightHudView
{
    private static readonly Color AlignedAngle = new(0.62f, 1f, 0.81f);
    private static readonly Color UnalignedAngle = new(1f, 0.62f, 0.48f);

    private IronBootsPanel? ironBootsPanel;
    private IronBootsApproach? shownIronBoots;
    private IronBootsDiagram? shownDiagram;

    private sealed record IronBootsPanel(GameObject Root, HudLine Box, HudLine Player, HudLine Foot,
        HudLine Velocity, HudLine Arrowhead, HudLine OriginRay, HudLine Origin,
        HudLine Feet, HudLine Contact, HudLine Arc, TextMeshPro Angle, TextMeshPro LastContact,
        Material Aligned, Material Unaligned);

    private void DrawIronBoots(Transform headset, Vector3 defaultHudPosition, System.Numerics.Vector3 offset, IronBootsDisplay? frame)
    {
        if (frame is not { Sample: var approach, Retained: var retained })
        {
            if (ironBootsPanel is { } hidden && hidden.Root.activeSelf)
                hidden.Root.SetActive(false);
            shownIronBoots = null;
            shownDiagram = null;
            return;
        }

        bool updated = !ReferenceEquals(shownIronBoots, approach);
        if (updated)
        {
            var previousPlane = shownIronBoots?.Structure == approach.Structure ? shownDiagram?.PlaneNormal : null;
            shownDiagram = IronBootsDiagram.Build(approach, previousPlane);
            shownIronBoots = approach;
        }
        if (shownDiagram is not { } diagram)
        {
            ironBootsPanel?.Root.SetActive(false);
            return;
        }

        var panel = ironBootsPanel ??= CreateIronBootsPanel();
        var position = defaultHudPosition + headset.right * offset.X + headset.up * offset.Y + headset.forward * offset.Z;
        panel.Root.transform.SetPositionAndRotation(position, headset.rotation);
        if (updated)
        {
            SetDiagramPath(panel.Box, diagram.Section);
            SetDiagramPath(panel.Player, diagram.Player);
            panel.Player.Enabled = diagram.Player.Length > 0;
            SetDiagramPath(panel.Foot, diagram.Foot);
            panel.Foot.Enabled = diagram.Foot.Length > 0;
            panel.Velocity.SetPosition(0, DiagramPoint(diagram.Feet));
            panel.Velocity.SetPosition(1, DiagramPoint(diagram.VelocityTip));
            panel.Velocity.Enabled = diagram.Arrowhead.Length > 0;
            SetDiagramPath(panel.Arrowhead, diagram.Arrowhead);
            panel.Arrowhead.Enabled = panel.Velocity.Enabled;
            panel.OriginRay.SetPosition(0, DiagramPoint(diagram.Feet));
            panel.OriginRay.SetPosition(1, DiagramPoint(diagram.Origin));
            SetDiagramDot(panel.Origin, diagram.Origin);
            SetDiagramDot(panel.Feet, diagram.Feet);
            panel.Contact.Transform.localPosition = DiagramPoint(diagram.Contact);
            panel.LastContact.text = approach.EstimatedContact ? "Last contact (estimated)" : "Last contact";
            SetDiagramPath(panel.Arc, diagram.Arc);
            panel.Arc.Enabled = diagram.Arc.Length > 0 && approach.Alignment.HasValue;

            bool aligned = approach.Alignment is { MeetsDownCutoff: true, MeetsOriginCutoff: true };
            panel.Arc.Material = aligned ? panel.Aligned : panel.Unaligned;
            panel.Angle.color = Color.Lerp(aligned ? AlignedAngle : UnalignedAngle, Color.white, 0.30f);
            panel.Angle.text = approach.Alignment is { } alignment
                ? FormattableString.Invariant($"{alignment.AngleDegrees:F0}°") : string.Empty;
            panel.Angle.transform.localPosition = DiagramPoint(diagram.AngleLabel) + Vector3.back * 0.004f;

            int fallback = (int)(unchecked((uint)approach.Structure.PoolGeneration) % StructureColors.Length);
            var color = structureColors.TryGetValue(approach.Structure, out int index)
                ? structureMaterials[index] : structureMaterials[fallback];
            panel.Box.Material = color;
            panel.OriginRay.Material = color;
            panel.Origin.Material = color;
        }

        if (panel.LastContact.gameObject.activeSelf != retained)
            panel.LastContact.gameObject.SetActive(retained);
        if (!panel.Root.activeSelf)
            panel.Root.SetActive(true);
    }

    private IronBootsPanel CreateIronBootsPanel()
    {
        var panel = Child(anchor, "Iron Boots diagram");
        var transform = panel.transform;
        var box = Line(transform, "Box cross-section", hitbox, 0.0016f, DrawOrder.Wireframe);
        box.Loop = true;
        var player = Line(transform, "Player profile", hitbox, 0.0018f, DrawOrder.Wireframe);
        player.Loop = true;
        var foot = Line(transform, "Foot collider section", hitbox, 0.0014f, DrawOrder.Wireframe);
        foot.Loop = true;
        var velocity = Line(transform, "VR velocity", hitbox, 0.0022f, DrawOrder.StructurePush, Vector3.zero, Vector3.one);
        var arrowhead = Line(transform, "Velocity arrowhead", hitbox, 0.0022f, DrawOrder.StructurePush);
        var ray = Line(transform, "Ray to structure origin", hitbox, 0.0022f, DrawOrder.StructurePush, Vector3.zero, Vector3.one);
        var origin = Line(transform, "Structure origin", hitbox, 0.0055f, DrawOrder.LiveRim, Vector3.zero, Vector3.one);
        var feet = Line(transform, "Feet", hitbox, 0.0055f, DrawOrder.LiveRim, Vector3.zero, Vector3.one);
        var contact = Line(transform, "Contact on box surface", hitbox, 0.0012f, DrawOrder.LiveRim, Arc(0.006f, 24));
        var aligned = ColorMaterial(hitbox.shader, AlignedAngle);
        var unaligned = ColorMaterial(hitbox.shader, UnalignedAngle);
        var arc = Line(transform, "Approach angle arc", aligned, 0.0018f, DrawOrder.LiveFace);

        var angle = CreateHudLabel(transform, "Approach angle", 0.024f, new Vector2(8f, 4f), TextAlignmentOptions.Center);
        var last = CreateHudLabel(transform, "Last contact", 0.017f, new Vector2(28f, 4f), TextAlignmentOptions.Center);
        last.transform.localPosition = new Vector3(0, 0.095f, -0.004f);
        last.gameObject.SetActive(false);
        return new IronBootsPanel(panel, box, player, foot, velocity, arrowhead, ray, origin, feet, contact, arc,
            angle, last, aligned, unaligned);
    }

    private static void SetDiagramPath(HudLine line, System.Numerics.Vector2[] points)
    {
        if (line.PositionCount != points.Length)
            line.PositionCount = points.Length;
        for (int i = 0; i < points.Length; i++)
            line.SetPosition(i, DiagramPoint(points[i]));
    }

    private static void SetDiagramDot(HudLine line, System.Numerics.Vector2 point)
    {
        var center = DiagramPoint(point);
        line.SetPosition(0, center + Vector3.left * 0.0004f);
        line.SetPosition(1, center + Vector3.right * 0.0004f);
    }

    private static Vector3 DiagramPoint(System.Numerics.Vector2 point) => new(point.X, point.Y, 0);
}
