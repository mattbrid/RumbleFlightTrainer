using UnityEngine;
using UnityEngine.Rendering;

namespace RumbleFlightTrainer;

internal sealed partial class FlightHudView
{
    private const float OutlineThickness = 0.0006f;

    private HudLine Line(Transform parent, string name, Material material, float width, DrawOrder order,
        params Vector3[] points)
    {
        // Draw the black backing before the colored strokes at each existing HUD layer.
        int sortingOrder = (int)order * 2;
        var stroke = CreateLineRenderer(Child(parent, name), material, width, sortingOrder + 1);
        var outline = CreateLineRenderer(Child(stroke.transform, "Black outline"), lineOutline,
            width + 2 * OutlineThickness, sortingOrder);
        var line = new HudLine(stroke, outline, width) { PositionCount = points.Length };
        for (int i = 0; i < points.Length; i++)
            line.SetPosition(i, points[i]);
        return line;
    }

    private static LineRenderer CreateLineRenderer(GameObject owner, Material material, float width, int sortingOrder)
    {
        var line = owner.AddComponent<LineRenderer>();
        line.useWorldSpace = false;
        line.sharedMaterial = material;
        line.sortingOrder = sortingOrder;
        line.shadowCastingMode = ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.startWidth = width;
        line.endWidth = width;
        line.numCapVertices = 5;
        return line;
    }

    private sealed class HudLine
    {
        private readonly LineRenderer stroke;
        private readonly LineRenderer outline;
        private readonly float width;

        public HudLine(LineRenderer stroke, LineRenderer outline, float width)
        {
            this.stroke = stroke;
            this.outline = outline;
            this.width = width;
        }

        public Transform Transform => stroke.transform;
        public Material Material { set => stroke.sharedMaterial = value; }

        public bool Enabled
        {
            get => stroke.enabled;
            set => stroke.enabled = outline.enabled = value;
        }

        public int PositionCount
        {
            get => stroke.positionCount;
            set => stroke.positionCount = outline.positionCount = value;
        }

        public int CapVertices { set => stroke.numCapVertices = outline.numCapVertices = value; }
        public bool Loop { set => stroke.loop = outline.loop = value; }

        public void SetPosition(int index, Vector3 position)
        {
            stroke.SetPosition(index, position);
            outline.SetPosition(index, position);
        }

        public void SetWidthCurve(AnimationCurve curve)
        {
            // A shared curve brings both widths to zero at the dash gaps.
            stroke.widthCurve = curve;
            outline.widthCurve = curve;
            stroke.widthMultiplier = width;
            outline.widthMultiplier = width + 2 * OutlineThickness;
        }
    }
}
