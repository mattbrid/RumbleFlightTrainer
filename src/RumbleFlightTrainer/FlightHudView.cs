using Il2CppTMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace RumbleFlightTrainer;

internal sealed partial class FlightHudView : IDisposable
{
    private const float FullArrowImpulse = 40f;
    private const float MinimumArrowImpulse = 0.4f;
    private const float MinimumArrowSpeed = 0.1f;
    private const float VelocityArrowSeconds = 0.15f;
    private const int LiveBranchLimit = 2;
    private const int LossDashes = 6;
    private const float InactiveWireframeBrightness = 0.9f;

    private static readonly Color[] StructureColors =
    {
        new(1f, 0.72f, 0.38f), new(0.48f, 0.77f, 1f), new(1f, 0.58f, 0.81f),
        new(0.87f, 0.95f, 0.50f), new(0.79f, 0.67f, 1f), new(1f, 0.61f, 0.55f),
        new(0.47f, 0.95f, 0.92f)
    };
    private static readonly Color StatusTextColor = Color.white;
    private static readonly Color OutcomeTextColor = new(1f, 0.84f, 0.62f);

    private readonly GameObject root = new("Flight Trainer HUD");
    private readonly List<Material> materials = new();
    private readonly Transform anchor;
    private readonly Material lineOutline;
    private readonly Material hitWarningOutline;
    private readonly Material hitbox;
    private readonly Material[] structureMaterials;
    private readonly Material[] dimStructureMaterials;
    private readonly Dictionary<StructureKey, int> structureColors = new();
    private readonly HashSet<StructureKey> displayedStructures = new();
    private readonly Arrow totalArrow;
    private readonly Arrow? gravityArrow;
    private readonly Arrow[] branches = Array.Empty<Arrow>();
    private readonly List<Arrow> velocityArrows = new();
    private readonly bool perStructureImpulseAtContact;
    private readonly bool showStructureVelocity;
    private readonly bool showNormalSpeed;
    private readonly bool showVelocityOnBreak;
    private readonly bool showNormalSpeedOnBreak;
    private readonly List<Marker> liveMarkers = new();
    private readonly List<Marker> lossMarkers = new();
    private readonly TextMeshPro status;
    private Quaternion viewYaw = Quaternion.identity;
    private bool hasViewYaw;

    private enum DrawOrder { Wireframe, StructureVelocity, StructureNormalSpeed, NetPush, StructurePush, LossFace, LossRim, LiveFace, LiveRim, Text }

    private sealed record Arrow(Transform Root, HudLine Shaft, HudLine Head, HudLine? Badge);

    private sealed record Marker(
        GameObject Root, HudLine Rim, HudLine Dot,
        HudLine Face, HudLine Span);

    public FlightHudView(bool showPerStructureImpulse, bool perStructureImpulseAtContact, bool showStructureVelocity,
        bool showNormalSpeed, bool showVelocityOnBreak, bool showNormalSpeedOnBreak)
    {
        this.perStructureImpulseAtContact = perStructureImpulseAtContact;
        this.showStructureVelocity = showStructureVelocity;
        this.showNormalSpeed = showNormalSpeed;
        this.showVelocityOnBreak = showVelocityOnBreak;
        this.showNormalSpeedOnBreak = showNormalSpeedOnBreak;
        try
        {
            anchor = root.transform;
            root.SetActive(false);
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
                throw new InvalidOperationException("The HUD line overlay shader was not available.");

            lineOutline = ColorMaterial(shader, Color.black);
            hitWarningOutline = ColorMaterial(shader, new Color(1f, 0.16f, 0.13f));
            hitbox = ColorMaterial(shader, new Color(0.71f, 0.84f, 0.84f));
            structureMaterials = StructureColors.Select(color => ColorMaterial(shader, color)).ToArray();
            dimStructureMaterials = StructureColors.Select(color =>
                ColorMaterial(shader, Color.Lerp(Color.black, color, InactiveWireframeBrightness))).ToArray();

            var push = ColorMaterial(shader, new Color(0.62f, 1f, 0.81f));
            totalArrow = CreateArrow(anchor, "Net impulse", push, 0.0045f, DrawOrder.NetPush);
            if (showPerStructureImpulse)
            {
                var gravity = ColorMaterial(shader, new Color(0.82f, 0.84f, 0.95f));
                gravityArrow = CreateArrow(anchor, "Gravity impulse", gravity, 0.002f, DrawOrder.StructurePush);
                var branchRoot = Child(anchor, "Structure pushes").transform;
                branches = new Arrow[LiveBranchLimit];
                for (int i = 0; i < branches.Length; i++)
                    branches[i] = CreateArrow(branchRoot, $"Branch {i + 1}", structureMaterials[0], 0.002f,
                        DrawOrder.StructurePush, badge: true);
            }

            root.SetActive(true);
            status = CreateHudLabel(anchor, "Contact status", new Vector2(32f, 8f), TextAlignmentOptions.Top);
            status.transform.localPosition = new Vector3(0, -0.195f, -0.003f);
            root.SetActive(false);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Hide() => root.SetActive(false);

    public void Draw(Transform headset, FlightHudFrame frame, HudNearbyCollider? playerReference,
        System.Numerics.Vector3 offset, System.Numerics.Vector3 ironBootsOffset, IronBootsDisplay? ironBoots,
        IReadOnlyDictionary<StructureKey, HudStructureVelocity>? velocities, IReadOnlySet<StructureKey>? hitWarnings)
    {
        Vector3 forward = headset.forward;
        var horizontal = new Vector3(forward.x, 0, forward.z);
        if (horizontal.sqrMagnitude > 0.0025f || !hasViewYaw)
        {
            if (horizontal.sqrMagnitude <= 0.0025f)
            {
                var right = headset.right;
                horizontal = Vector3.Cross(new Vector3(right.x, 0, right.z), Vector3.up);
            }
            if (horizontal.sqrMagnitude > 0.0025f)
                viewYaw = Quaternion.LookRotation(horizontal, Vector3.up);
            hasViewYaw = true;
        }

        Vector3 defaultHudPosition = headset.position + forward * 0.84f - headset.up * 0.23f;
        Vector3 position = defaultHudPosition + headset.right * offset.X + headset.up * offset.Y + forward * offset.Z;
        anchor.SetPositionAndRotation(position, viewYaw);
        Quaternion worldToView = Quaternion.Inverse(viewYaw);
        Quaternion bodyToView = worldToView * ToUnity(frame.BodyYaw);
        AssignStructureColors(frame.Structures, frame.Breaks);
        DrawStructureVelocities(frame, velocities, worldToView, bodyToView);
        DrawNormalSpeedArrows(frame, velocities, worldToView, bodyToView);

        var (missingLocation, capsuleCenter) = DrawContactGeometry(frame, worldToView, bodyToView, playerReference, hitWarnings);
        DrawImpulses(frame, worldToView, capsuleCenter);
        DrawBreakOutlines(frame.Breaks, bodyToView);
        var outcome = frame.Outcome;

        var message = Status(frame, outcome, missingLocation);
        if (status.text != message)
            status.text = message;
        status.color = outcome is not null ? OutcomeTextColor : StatusTextColor;
        if (!root.activeSelf)
            root.SetActive(true);
        DrawIronBoots(headset, defaultHudPosition, ironBootsOffset, ironBoots);
    }

    private void DrawImpulses(FlightHudFrame frame, Quaternion worldToView, Vector3? capsuleCenter)
    {
        DrawPush(totalArrow, worldToView * ToUnity(frame.CombinedImpulseWorld));
        if (gravityArrow is { } gravity)
        {
            gravity.Root.localPosition = capsuleCenter.GetValueOrDefault();
            DrawPush(gravity, capsuleCenter.HasValue ? worldToView * ToUnity(frame.GravityImpulseWorld) : Vector3.zero,
                narrowHead: true);
        }
        if (branches.Length == 0)
            return;
        var live = frame.Structures.Where(item => item.Touching)
            .OrderByDescending(item => item.HasImpulse)
            .ThenByDescending(item => item.PushWorld.LengthSquared())
            .ThenBy(item => item.Key.PoolGeneration).ThenBy(item => item.Key.InstanceId)
            .Take(LiveBranchLimit).ToArray();
        for (int i = 0; i < branches.Length; i++)
        {
            if (i >= live.Length)
            {
                DrawPush(branches[i], Vector3.zero);
                continue;
            }

            var structure = live[i];
            var contactOffset = perStructureImpulseAtContact ? structure.ImpulseContactOffset(frame.GeometryTick) : null;
            var arrow = branches[i];
            arrow.Root.localPosition = worldToView * ToUnity(contactOffset.GetValueOrDefault());
            DrawPush(arrow, worldToView * ToUnity(structure.PushWorld), StructureMaterial(structure.Key), narrowHead: true);
        }
    }

    private void AssignStructureColors(HudStructureFrame[] structures, HudBreak[] breaks)
    {
        displayedStructures.Clear();
        foreach (var structure in structures)
            displayedStructures.Add(structure.Key);
        foreach (var broken in breaks)
            if (broken.PlayerContact?.Placement is not null)
                displayedStructures.Add(broken.Key);

        Span<int> usage = stackalloc int[StructureColors.Length];
        usage.Clear();
        foreach (var (key, color) in structureColors)
        {
            // Retained outlines share the same reservation as the live marker for that lifetime.
            if (displayedStructures.Contains(key))
                usage[color]++;
            else
                structureColors.Remove(key);
        }

        foreach (var key in displayedStructures)
        {
            if (structureColors.ContainsKey(key))
                continue;

            uint generation = unchecked((uint)key.PoolGeneration);
            int color = (int)(generation % StructureColors.Length);
            for (int i = 0; i < usage.Length && usage[color] != 0; i++)
                if (usage[i] < usage[color])
                    color = i;
            structureColors.Add(key, color);
            usage[color]++;
        }
    }

    private Material StructureMaterial(StructureKey key) => structureMaterials[structureColors[key]];

    private Arrow CreateArrow(Transform parent, string name, Material material, float width, DrawOrder order,
        bool badge = false)
    {
        var arrow = Child(parent, name).transform;
        return new Arrow(arrow,
            Line(arrow, "Shaft", material, width, order, Vector3.zero, Vector3.up),
            Line(arrow, "Head", material, width, order, Vector3.left, Vector3.up, Vector3.right),
            badge ? Line(arrow, "Structure circle", material, width, order, Arc(0.007f, 24)) : null);
    }

    private void DrawStructureVelocities(FlightHudFrame frame,
        IReadOnlyDictionary<StructureKey, HudStructureVelocity>? velocities, Quaternion worldToView, Quaternion bodyToView)
    {
        int used = 0;
        if (showStructureVelocity && velocities is not null)
        {
            foreach (var structure in frame.Structures)
            {
                if (velocities.TryGetValue(structure.Key, out var sample))
                    DrawVelocityArrow(structure.Key, sample.CenterOffset, sample.RelativeVelocity, worldToView, ref used);
            }
        }
        if (showVelocityOnBreak)
            foreach (var broken in frame.Breaks)
                if (broken.PlayerContact is { Placement: { } placement, RelativeVelocityWorld: { } velocity } impact)
                    DrawVelocityArrow(broken.Key, ToUnity(placement.Offset(impact.CenterWorld)), ToUnity(velocity),
                        LossRotation(placement, bodyToView), ref used);

        HideUnused(velocityArrows, used);
    }

    private void DrawVelocityArrow(StructureKey key, Vector3 offset, Vector3 velocity, Quaternion rotation, ref int used)
    {
        float speed = velocity.magnitude;
        if (!float.IsFinite(speed) || speed < MinimumArrowSpeed)
            return;

        float length = Mathf.Clamp(speed * VelocityArrowSeconds * HudContactPlacement.Scale, 0.008f, 0.14f);
        DrawMotionArrow(velocityArrows, ref used, StructureMaterial(key), rotation * offset, rotation * (velocity / speed),
            length, DrawOrder.StructureVelocity);
    }

    private void DrawMotionArrow(List<Arrow> pool, ref int used, Material color, Vector3 position,
        Vector3 direction, float length, DrawOrder order)
    {
        if (used == pool.Count)
        {
            bool normalSpeed = order == DrawOrder.StructureNormalSpeed;
            pool.Add(CreateArrow(anchor, normalSpeed ? "Normal speed" : "Relative velocity", color,
                normalSpeed ? 0.0025f : 0.002f, order));
        }
        var arrow = pool[used++];
        arrow.Root.localPosition = position;
        arrow.Shaft.Material = color;
        arrow.Head.Material = color;
        float headLength = Mathf.Min(length * 0.45f, 0.014f);
        SetArrowDirection(arrow, direction, length, headLength, headLength * 0.55f);
        arrow.Root.gameObject.SetActive(true);
    }

    private static void HideUnused(List<Arrow> arrows, int used)
    {
        for (int i = used; i < arrows.Count; i++)
            arrows[i].Root.gameObject.SetActive(false);
    }

    private static void DrawPush(Arrow arrow, Vector3 impulse, Material? color = null, bool narrowHead = false)
    {
        float magnitude = impulse.magnitude;
        bool visible = float.IsFinite(magnitude) && magnitude >= MinimumArrowImpulse;
        arrow.Shaft.Enabled = visible;
        arrow.Head.Enabled = visible;
        if (arrow.Badge is { } badge)
            badge.Enabled = visible;
        if (!visible)
            return;

        Vector3 direction = impulse / magnitude;
        float length = 0.019f + 0.077f * Mathf.Clamp01(magnitude / FullArrowImpulse);
        SetArrowDirection(arrow, direction, length, narrowHead ? 0.012f : 0.016f, narrowHead ? 0.006f : 0.0095f);

        if (color is not null && arrow.Badge is { } marker)
        {
            arrow.Shaft.Material = color;
            arrow.Head.Material = color;
            marker.Transform.localPosition = direction * length + Vector3.back * 0.003f;
        }
    }

    private static void SetArrowDirection(Arrow arrow, Vector3 direction, float length, float headLength, float headWidth)
    {
        Vector3 tip = direction * length;
        Vector3 wing = Vector3.Cross(direction, Vector3.forward);
        if (wing.sqrMagnitude < 0.03f)
            wing = Vector3.Cross(direction, Vector3.up);
        wing = wing.normalized * headWidth;
        Vector3 fork = tip - direction * headLength;
        arrow.Shaft.SetPosition(1, tip);
        arrow.Head.SetPosition(0, fork + wing);
        arrow.Head.SetPosition(1, tip);
        arrow.Head.SetPosition(2, fork - wing);
    }

    private Marker GetMarker(List<Marker> pool, int index, bool lost)
    {
        if (index < pool.Count)
            return pool[index];

        var marker = Child(anchor, lost ? "Lost contact" : "Live contact");
        float radius = lost ? 0.017f : 0.010f;
        int segments = lost ? 72 : 24;
        var circle = Arc(radius, segments);
        var faceOrder = lost ? DrawOrder.LossFace : DrawOrder.LiveFace;
        var rimOrder = lost ? DrawOrder.LossRim : DrawOrder.LiveRim;
        var material = structureMaterials[0];
        var rim = Line(marker.transform, "Outline", material, 0.0024f, rimOrder, circle);
        var face = Line(marker.transform, "Contact face", material, 0.0018f, faceOrder, Arc(radius + 0.006f, segments));
        var span = Line(marker.transform, "Contact span", material, 0.0022f, faceOrder, Vector3.zero, Vector3.one);
        var dot = Line(marker.transform, "Contact point", material, lost ? 0.005f : 0.009f, rimOrder,
            Vector3.left * 0.001f, Vector3.right * 0.001f);
        if (lost)
        {
            var dashes = DashWidth();
            rim.SetWidthCurve(dashes);
            face.SetWidthCurve(dashes);
        }
        pool.Add(new Marker(marker, rim, dot, face, span));
        return pool[index];
    }

    private static void DrawMarker(Marker marker, HudContactPatch patch, Vector3 position, Quaternion directionToHud, Material color,
        bool measured)
    {
        marker.Root.transform.localPosition = position;
        marker.Rim.Material = color;
        marker.Face.Material = color;
        marker.Dot.Material = color;
        marker.Dot.Enabled = measured;
        marker.Face.Enabled = patch.BodyNormal.HasValue;
        if (patch.BodyNormal is { } normal)
            marker.Face.Transform.localRotation = Quaternion.FromToRotation(Vector3.forward, directionToHud * ToUnity(normal));
        marker.Root.SetActive(true);
    }

    private static void HideUnused(List<Marker> pool, int used)
    {
        for (int i = used; i < pool.Count; i++)
            pool[i].Root.SetActive(false);
    }

    private string Status(FlightHudFrame frame, string? outcome, bool missingLocation)
    {
        var lines = new List<string>();
        if (!frame.ObserverAvailable)
            lines.Add("Contact data unavailable");
        if (frame.TouchingCount > 1)
        {
            string counts = $"{frame.TouchingCount} touching, {frame.MeasuredCount} measured";
            if (branches.Length > 0 && frame.TouchingCount > LiveBranchLimit)
                counts += $", +{frame.TouchingCount - LiveBranchLimit} more";
            lines.Add(counts);
        }

        if (frame.PartialImpulse)
            lines.Add(frame.MeasuredCount == 0 ? "Impulse unavailable" : "Partial impulse");
        if (missingLocation)
            lines.Add("Contact location unavailable");

        if (outcome is not null)
            lines.Add(outcome);
        return string.Join("\n", lines);
    }

    private Material ColorMaterial(Shader shader, Color color)
    {
        var material = new Material(shader);
        materials.Add(material);
        material.SetColor("_Color", color);
        material.SetInt("_Cull", (int)CullMode.Off);
        material.SetInt("_ZTest", (int)CompareFunction.Always);
        material.SetInt("_ZWrite", 0);
        material.renderQueue = (int)RenderQueue.Overlay;
        return material;
    }

    private static GameObject Child(Transform parent, string name)
    {
        var child = new GameObject(name);
        child.transform.SetParent(parent, false);
        return child;
    }

    private static Vector3[] Arc(float radius, int segments)
    {
        var points = new Vector3[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            float angle = i * (2 * Mathf.PI / segments);
            points[i] = new Vector3(radius * Mathf.Cos(angle), radius * Mathf.Sin(angle), 0);
        }
        return points;
    }

    private static AnimationCurve DashWidth(int dashCount = LossDashes)
    {
        // Width goes to zero between dashes so an outline can use a single renderer.
        var keys = new Keyframe[dashCount * 4 + 1];
        for (int i = 0; i <= dashCount; i++)
        {
            keys[i * 4] = new Keyframe((float)i / dashCount, 1f);
            if (i == dashCount)
                continue;
            keys[i * 4 + 1] = new Keyframe((i + 0.55f) / dashCount, 1f);
            keys[i * 4 + 2] = new Keyframe((i + 0.59f) / dashCount, 0f);
            keys[i * 4 + 3] = new Keyframe((i + 0.96f) / dashCount, 0f);
        }
        return new AnimationCurve(keys);
    }

    private static Vector3 ToUnity(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
    private static Quaternion ToUnity(System.Numerics.Quaternion q) => new(q.X, q.Y, q.Z, q.W);

    public void Dispose()
    {
        if (root != null)
        {
            root.SetActive(false);
            UnityEngine.Object.Destroy(root);
        }
        foreach (var material in materials)
            if (material != null)
                UnityEngine.Object.Destroy(material);
        materials.Clear();
    }
}
