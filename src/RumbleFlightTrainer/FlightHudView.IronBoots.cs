using Il2CppTMPro;
using UnityEngine;

namespace RumbleFlightTrainer;

internal sealed partial class FlightHudView
{
    private static readonly Color AlignedAngle = new(0.20f, 0.53f, 0.33f);
    private static readonly Color UnalignedAngle = new(0.70f, 0.31f, 0.22f);

    private IronBootsPanel? ironBootsPanel;
    private IronBootsApproach? shownIronBoots;
    private IronBootsDiagram? shownDiagram;

    private sealed record IronBootsPanel(GameObject Root, LineRenderer Box, LineRenderer Player, LineRenderer Foot,
        LineRenderer Velocity, LineRenderer Arrowhead, LineRenderer OriginRay, LineRenderer Origin,
        LineRenderer Feet, LineRenderer Contact, LineRenderer Arc, TextMeshPro Angle, TextMeshPro LastContact,
        Material Aligned, Material Unaligned);

    private void DrawIronBoots(Transform headset, Vector3 hudPosition, IronBootsDisplay? frame)
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
        panel.Root.transform.SetPositionAndRotation(
            hudPosition + headset.right * 0.31f + headset.up * 0.035f, headset.rotation);
        if (updated)
        {
            SetDiagramPath(panel.Box, diagram.Section);
            SetDiagramPath(panel.Player, diagram.Player);
            panel.Player.enabled = diagram.Player.Length > 0;
            SetDiagramPath(panel.Foot, diagram.Foot);
            panel.Foot.enabled = diagram.Foot.Length > 0;
            panel.Velocity.SetPosition(0, DiagramPoint(diagram.Feet));
            panel.Velocity.SetPosition(1, DiagramPoint(diagram.VelocityTip));
            panel.Velocity.enabled = diagram.Arrowhead.Length > 0;
            SetDiagramPath(panel.Arrowhead, diagram.Arrowhead);
            panel.Arrowhead.enabled = panel.Velocity.enabled;
            panel.OriginRay.SetPosition(0, DiagramPoint(diagram.Feet));
            panel.OriginRay.SetPosition(1, DiagramPoint(diagram.Origin));
            SetDiagramDot(panel.Origin, diagram.Origin);
            SetDiagramDot(panel.Feet, diagram.Feet);
            panel.Contact.transform.localPosition = DiagramPoint(diagram.Contact);
            panel.LastContact.text = approach.EstimatedContact ? "Last contact (estimated)" : "Last contact";
            SetDiagramPath(panel.Arc, diagram.Arc);
            panel.Arc.enabled = diagram.Arc.Length > 0 && approach.Alignment.HasValue;

            bool aligned = approach.Alignment is { MeetsDownCutoff: true, MeetsOriginCutoff: true };
            panel.Arc.sharedMaterial = aligned ? panel.Aligned : panel.Unaligned;
            panel.Angle.color = aligned ? AlignedAngle : UnalignedAngle;
            panel.Angle.text = approach.Alignment is { } alignment
                ? FormattableString.Invariant($"{alignment.AngleDegrees:F0}°") : string.Empty;
            panel.Angle.transform.localPosition = DiagramPoint(diagram.AngleLabel) + Vector3.back * 0.004f;

            int fallback = (int)(unchecked((uint)approach.Structure.PoolGeneration) % StructureColors.Length);
            var color = structureColors.TryGetValue(approach.Structure, out int index)
                ? structureMaterials[index] : structureMaterials[fallback];
            panel.Box.sharedMaterial = color;
            panel.OriginRay.sharedMaterial = color;
            panel.Origin.sharedMaterial = color;
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
        box.loop = true;
        var player = Line(transform, "Player profile", hitbox, 0.0018f, DrawOrder.Wireframe);
        player.loop = true;
        var foot = Line(transform, "Foot collider section", hitbox, 0.0014f, DrawOrder.Wireframe);
        foot.loop = true;
        var velocity = Line(transform, "VR velocity", hitbox, 0.0022f, DrawOrder.StructurePush, Vector3.zero, Vector3.one);
        var arrowhead = Line(transform, "Velocity arrowhead", hitbox, 0.0022f, DrawOrder.StructurePush);
        var ray = Line(transform, "Ray to structure origin", hitbox, 0.0022f, DrawOrder.StructurePush, Vector3.zero, Vector3.one);
        var origin = Line(transform, "Structure origin", hitbox, 0.0055f, DrawOrder.LiveRim, Vector3.zero, Vector3.one);
        var feet = Line(transform, "Feet", hitbox, 0.0055f, DrawOrder.LiveRim, Vector3.zero, Vector3.one);
        var contact = Line(transform, "Contact on box surface", hitbox, 0.0012f, DrawOrder.LiveRim, Arc(0.006f, 24));
        var aligned = ColorMaterial(hitbox.shader, AlignedAngle);
        var unaligned = ColorMaterial(hitbox.shader, UnalignedAngle);
        var arc = Line(transform, "Approach angle arc", aligned, 0.0018f, DrawOrder.LiveFace);

        var angle = Label("Approach angle", 0.024f, 8f);
        var last = Label("Last contact", 0.017f, 28f);
        last.transform.localPosition = new Vector3(0, 0.095f, -0.004f);
        last.text = "Last contact";
        last.color = StatusTextColor;
        last.gameObject.SetActive(false);
        return new IronBootsPanel(panel, box, player, foot, velocity, arrowhead, ray, origin, feet, contact, arc,
            angle, last, aligned, unaligned);

        TextMeshPro Label(string name, float scale, float width)
        {
            var text = Child(transform, name).AddComponent<TextMeshPro>();
            text.transform.localScale = Vector3.one * scale;
            text.rectTransform.sizeDelta = new Vector2(width, 4f);
            text.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            text.font = status.font;
            text.fontSharedMaterial = status.fontSharedMaterial;
            text.fontSize = status.fontSize;
            text.alignment = TextAlignmentOptions.Center;
            text.richText = false;
            SetTextDrawOrder(text);
            return text;
        }
    }

    private static void SetDiagramPath(LineRenderer line, System.Numerics.Vector2[] points)
    {
        if (line.positionCount != points.Length)
            line.positionCount = points.Length;
        for (int i = 0; i < points.Length; i++)
            line.SetPosition(i, DiagramPoint(points[i]));
    }

    private static void SetDiagramDot(LineRenderer line, System.Numerics.Vector2 point)
    {
        var center = DiagramPoint(point);
        line.SetPosition(0, center + Vector3.left * 0.0004f);
        line.SetPosition(1, center + Vector3.right * 0.0004f);
    }

    private static Vector3 DiagramPoint(System.Numerics.Vector2 point) => new(point.X, point.Y, 0);
}
