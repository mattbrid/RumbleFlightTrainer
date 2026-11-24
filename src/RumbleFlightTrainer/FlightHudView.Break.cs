using UnityEngine;

namespace RumbleFlightTrainer;

internal sealed partial class FlightHudView
{
    private readonly List<BreakOutline> breakOutlines = new();
    private readonly Dictionary<int, AnimationCurve> breakDashCurves = new();
    private sealed record BreakOutline(Transform Root)
    {
        public List<LineRenderer> Edges { get; } = new();
        public HudPlayerBreakContact? Contact { get; set; }
        public Material? Color { get; set; }
    }

    private void DrawBreakOutlines(HudBreak[] breaks, Quaternion bodyToView)
    {
        int used = 0;
        foreach (var broken in breaks)
        {
            if (broken.PlayerContact is not { Placement: { } placement } contact || placement is { Box: null, Mesh: null })
                continue;
            if (used == breakOutlines.Count)
                breakOutlines.Add(new BreakOutline(Child(anchor, "Broken structure outline").transform));
            DrawBreakOutline(breakOutlines[used++], contact, placement, StructureMaterial(broken.Key), bodyToView);
        }
        for (int i = used; i < breakOutlines.Count; i++)
            breakOutlines[i].Root.gameObject.SetActive(false);
    }

    private void DrawBreakOutline(BreakOutline widget, HudPlayerBreakContact contact, HudContactPlacement placement,
        Material color, Quaternion bodyToView)
    {
        var outline = widget.Root;
        var breakEdges = widget.Edges;
        outline.localRotation = LossRotation(placement, bodyToView);
        if (!ReferenceEquals(widget.Contact, contact) || widget.Color != color)
        {
            widget.Contact = contact;
            widget.Color = color;
            int used = 0;
            if (placement.Box is { } box)
                foreach (var (from, to) in FlightBoxPreview.Edges)
                    DrawEdge(box.Corners[from], box.Corners[to]);

            if (placement.Mesh is { } mesh)
            {
                if (mesh.Edges is { } edges)
                    foreach (var edge in edges)
                        DrawEdge(MeshPoint(edge.Start), MeshPoint(edge.End));
                else if (mesh.Sections is { } sections)
                    foreach (var section in sections)
                        for (int i = 1; i < section.Length; i++)
                            DrawEdge(MeshPoint(section[i - 1]), MeshPoint(section[i]));

                System.Numerics.Vector3 MeshPoint(Vec3 local) =>
                    mesh.Center + mesh.Offsets.Point(FlightContactPatches.Vector(local));
            }

            for (int i = used; i < breakEdges.Count; i++)
                breakEdges[i].enabled = false;

            void DrawEdge(System.Numerics.Vector3 from, System.Numerics.Vector3 to)
            {
                var start = ToUnity(placement.Offset(from));
                var end = ToUnity(placement.Offset(to));
                var length = (end - start).magnitude;
                if (!float.IsFinite(length) || length <= 0.0001f)
                    return;

                const float width = 0.0017f;
                if (used == breakEdges.Count)
                {
                    var newEdge = Line(outline, "Dashed structure edge", color, width, DrawOrder.Wireframe);
                    newEdge.numCapVertices = 0;
                    breakEdges.Add(newEdge);
                }

                var line = breakEdges[used++];
                line.sharedMaterial = color;
                int intervals = 4 * Mathf.Clamp(Mathf.CeilToInt(length / 0.018f), 1, 24);
                line.positionCount = intervals + 1;
                // LineRenderer samples width at vertices. Add vertices at every dash and gap boundary.
                for (int i = 0; i <= intervals; i++)
                    line.SetPosition(i, Vector3.Lerp(start, end, (float)i / intervals));
                if (!breakDashCurves.TryGetValue(intervals, out var curve))
                {
                    var keys = new Keyframe[intervals + 1];
                    for (int i = 0; i <= intervals; i++)
                        keys[i] = new Keyframe((float)i / intervals, i % 4 is 1 or 2 ? 1f : 0f);
                    breakDashCurves[intervals] = curve = new AnimationCurve(keys);
                }
                line.widthCurve = curve;
                line.widthMultiplier = width;
                line.enabled = true;
            }
        }

        if (!outline.gameObject.activeSelf)
            outline.gameObject.SetActive(true);
    }
}
