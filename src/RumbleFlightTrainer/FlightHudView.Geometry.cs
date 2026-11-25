using UnityEngine;

namespace RumbleFlightTrainer;

internal sealed partial class FlightHudView
{
    private const int CapsuleCapSteps = 16;
    private const float HitWarningOutlineThickness = 0.0012f;

    private readonly List<BoxWidget> boxes = new();
    private readonly List<MeshWidget> meshes = new();
    private readonly List<PlayerWidget> playerColliders = new();
    private readonly HashSet<int> drawnPlayerColliders = new();
    private readonly Dictionary<int, HudPairGeometry> retainedPlayerColliders = new();
    private readonly HashSet<int> drawnStructureColliders = new();

    private sealed record BoxWidget(Transform Root, HudLine[] Edges)
    {
        public HudBoxPreview? Shape { get; set; }
        public Material? Color { get; set; }
        public bool HitWarning { get; set; }
    }

    private sealed record PlayerWidget(Transform Root, HudLine Front, HudLine Side)
    {
        public float HalfSpan { get; set; }
        public float Radius { get; set; }
    }

    private sealed record MeshWidget(Transform Root)
    {
        public List<HudLine> Lines { get; } = new();
        public HudMeshPreview? Shape { get; set; }
        public Material? Color { get; set; }
        public bool HitWarning { get; set; }
    }

    private bool DrawContactGeometry(FlightHudFrame frame, Quaternion worldToView, Quaternion bodyToView,
        HudNearbyCollider? playerReference, IReadOnlySet<StructureKey>? hitWarnings)
    {
        int boxCount = 0;
        int meshCount = 0;
        int playerColliderCount = 0;
        int liveMarkerCount = 0;
        int lostMarkerCount = 0;
        bool missingLocation = false;
        drawnPlayerColliders.Clear();
        retainedPlayerColliders.Clear();

        foreach (var structure in frame.Structures)
        {
            int colorIndex = structureColors[structure.Key];
            var color = structureMaterials[colorIndex];
            var wireframeColor = structure.Touching ? color : dimStructureMaterials[colorIndex];
            bool hitWarning = hitWarnings?.Contains(structure.Key) == true;
            drawnStructureColliders.Clear();
            foreach (var pair in structure.ActivePairs)
            {
                var geometry = pair.Geometry;
                var placement = geometry.Tick == frame.GeometryTick ? geometry.Placement : null;
                if (placement?.Player is { } player && drawnPlayerColliders.Add(geometry.Pair.PlayerColliderId))
                    DrawPlayerCollider(playerColliderCount++, player, placement, worldToView);
                if (placement?.Box is { } box && drawnStructureColliders.Add(geometry.Pair.OtherColliderId))
                    DrawBox(boxCount++, box, placement, wireframeColor, worldToView, hitWarning);
                if (placement?.Mesh is { } mesh && drawnStructureColliders.Add(geometry.Pair.OtherColliderId))
                    DrawMesh(meshCount++, mesh, placement, wireframeColor, worldToView, hitWarning);

                missingLocation |= DrawContactMarkers(geometry, placement, worldToView,
                    color, lost: false, liveMarkers, ref liveMarkerCount);
            }

            if (structure.NearbyGeometry is { } nearby)
            {
                foreach (var collider in nearby)
                {
                    if (!drawnStructureColliders.Add(collider.ColliderId))
                        continue;
                    var placement = collider.Placement;
                    if (placement.Box is { } box)
                        DrawBox(boxCount++, box, placement, wireframeColor, worldToView, hitWarning);
                    if (placement.Mesh is { } mesh)
                        DrawMesh(meshCount++, mesh, placement, wireframeColor, worldToView, hitWarning);
                }
            }

            foreach (var loss in structure.Losses)
            {
                foreach (var geometry in loss.Contacts.OrderByDescending(contact => contact.Tick))
                {
                    if (geometry.Placement is not { } placement)
                        continue;

                    var lossToView = LossRotation(placement, bodyToView);
                    int previousMarkerCount = lostMarkerCount;
                    bool replacedByImpact = loss.Break?.PlayerContact is { Placement: not null, Patch.WorldPoint: not null };
                    if (!replacedByImpact)
                        DrawContactMarkers(geometry, placement, lossToView, color, lost: true,
                            lossMarkers, ref lostMarkerCount);
                    int colliderId = geometry.Pair.PlayerColliderId;
                    if ((lostMarkerCount > previousMarkerCount || replacedByImpact && placement.Player is not null) &&
                        (!retainedPlayerColliders.TryGetValue(colliderId, out var previous) || geometry.Tick > previous.Tick))
                        retainedPlayerColliders[colliderId] = geometry;
                }
            }
        }

        foreach (var broken in frame.Breaks)
        {
            if (broken.PlayerContact is not { Placement: { } impactPlacement, Patch: { WorldPoint: { } point } patch } impact)
                continue;
            var marker = GetMarker(lossMarkers, lostMarkerCount++, lost: true);
            var rotation = LossRotation(impactPlacement, bodyToView);
            if (impactPlacement.Player is { } player)
            {
                point = player.PointOnSurface(point) ?? player.PointOnSurface(impact.CenterWorld) ?? point;
                if (impact.PlayerColliderId is { } id && drawnPlayerColliders.Add(id))
                    DrawPlayerCollider(playerColliderCount++, player, impactPlacement, rotation);
            }
            DrawMarker(marker, patch, rotation * ToUnity(impactPlacement.Offset(point)), bodyToView,
                StructureMaterial(broken.Key), measured: !impact.EstimatedContact);
            marker.Span.Enabled = false;
        }

        foreach (var (colliderId, geometry) in retainedPlayerColliders)
        {
            if (drawnPlayerColliders.Add(colliderId) && geometry.Placement is { Player: { } player } placement)
                DrawPlayerCollider(playerColliderCount++, player, placement, LossRotation(placement, bodyToView));
        }
        if (playerReference is { Placement: { Player: { } capsule } reference } &&
            drawnPlayerColliders.Add(playerReference.ColliderId))
            DrawPlayerCollider(playerColliderCount++, capsule, reference, worldToView);

        for (int i = boxCount; i < boxes.Count; i++)
            boxes[i].Root.gameObject.SetActive(false);
        for (int i = meshCount; i < meshes.Count; i++)
            meshes[i].Root.gameObject.SetActive(false);
        for (int i = playerColliderCount; i < playerColliders.Count; i++)
            playerColliders[i].Root.gameObject.SetActive(false);
        HideUnused(liveMarkers, liveMarkerCount);
        HideUnused(lossMarkers, lostMarkerCount);
        return missingLocation;
    }

    // Carry the sampled offsets with the player's current yaw after a loss.
    private static Quaternion LossRotation(HudContactPlacement placement, Quaternion bodyToView) =>
        bodyToView * Quaternion.Inverse(ToUnity(placement.Origin.Yaw));

    private bool DrawContactMarkers(HudPairGeometry geometry, HudContactPlacement? placement, Quaternion worldToHud,
        Material color, bool lost, List<Marker> pool, ref int count)
    {
        if (placement is null)
            return true;

        var directionToHud = worldToHud * ToUnity(placement.Origin.Yaw);
        bool missing = geometry.Patches.Length == 0;
        foreach (var patch in geometry.Patches)
        {
            var markerPoint = lost && patch.WorldPoint is { } worldPoint
                ? placement.Player?.PointOnSurface(worldPoint) ?? worldPoint : placement.MarkerPoint(patch);
            if (markerPoint is not { } point)
            {
                missing = true;
                continue;
            }

            var position = worldToHud * ToUnity(placement.Offset(point));
            var marker = GetMarker(pool, count++, lost);
            DrawMarker(marker, patch, position, directionToHud, color, measured: true);
            marker.Span.Enabled = patch.Points.Length > 1;
            if (marker.Span.Enabled)
            {
                var (start, end) = PatchSpan(patch.Points);
                marker.Span.Enabled = start != end;
                marker.Span.Material = color;
                marker.Span.SetPosition(0, worldToHud * ToUnity(placement.Offset(start)) - position);
                marker.Span.SetPosition(1, worldToHud * ToUnity(placement.Offset(end)) - position);
            }
        }
        return missing;
    }

    private void DrawBox(int index, HudBoxPreview box, HudContactPlacement placement, Material color, Quaternion rotation,
        bool hitWarning)
    {
        if (index == boxes.Count)
        {
            var root = Child(anchor, "Structure box").transform;
            var lines = new HudLine[FlightBoxPreview.Edges.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                var line = Line(root, "Box edge", color, 0.0016f, DrawOrder.Wireframe);
                line.CapVertices = 0;
                line.PositionCount = 2;
                lines[i] = line;
            }
            boxes.Add(new BoxWidget(root, lines));
        }

        var widget = boxes[index];
        widget.Root.localPosition = rotation * ToUnity(placement.Offset(box.Center));
        widget.Root.localRotation = rotation;
        if (widget.Shape?.Frame != box.Frame || widget.Shape?.HalfSize != box.HalfSize || widget.Color != color)
        {
            widget.Shape = box;
            widget.Color = color;
            for (int i = 0; i < FlightBoxPreview.Edges.Length; i++)
            {
                var (from, to) = FlightBoxPreview.Edges[i];
                var line = widget.Edges[i];
                line.Material = color;
                line.SetPosition(0, ToUnity(box.Corners[from] - box.Center) * HudContactPlacement.Scale);
                line.SetPosition(1, ToUnity(box.Corners[to] - box.Center) * HudContactPlacement.Scale);
            }
        }
        if (widget.HitWarning != hitWarning)
        {
            widget.HitWarning = hitWarning;
            foreach (var line in widget.Edges)
                SetStructureOutline(line, hitWarning);
        }
        widget.Root.gameObject.SetActive(true);
    }

    private void DrawPlayerCollider(int index, HudPlayerCollider player, HudContactPlacement placement, Quaternion rotation)
    {
        if (index == playerColliders.Count)
        {
            var root = Child(anchor, "Player collider").transform;
            var front = Line(root, "Collider profile", hitbox, 0.0017f, DrawOrder.Wireframe);
            var side = Line(root, "Collider depth", hitbox, 0.0011f, DrawOrder.Wireframe);
            front.PositionCount = side.PositionCount = 2 * (CapsuleCapSteps + 1) + 1;
            playerColliders.Add(new PlayerWidget(root, front, side));
        }

        var widget = playerColliders[index];
        widget.Root.localPosition = rotation * ToUnity(placement.Offset(player.Center));
        widget.Root.localRotation = rotation * Quaternion.FromToRotation(Vector3.up, ToUnity(player.Axis));
        if (widget.HalfSpan != player.HalfSpan || widget.Radius != player.Radius)
        {
            widget.HalfSpan = player.HalfSpan;
            widget.Radius = player.Radius;
            float span = player.HalfSpan * HudContactPlacement.Scale;
            float radius = player.Radius * HudContactPlacement.Scale;
            for (int i = 0; i <= CapsuleCapSteps; i++)
            {
                float angle = i * (Mathf.PI / CapsuleCapSteps);
                float across = radius * Mathf.Cos(angle);
                float height = span + radius * Mathf.Sin(angle);
                int opposite = i + CapsuleCapSteps + 1;
                widget.Front.SetPosition(i, new Vector3(across, height, 0));
                widget.Front.SetPosition(opposite, new Vector3(-across, -height, 0));
                widget.Side.SetPosition(i, new Vector3(0, height, across));
                widget.Side.SetPosition(opposite, new Vector3(0, -height, -across));
            }
            int last = widget.Front.PositionCount - 1;
            widget.Front.SetPosition(last, new Vector3(radius, span, 0));
            widget.Side.SetPosition(last, new Vector3(0, span, radius));
        }
        widget.Root.gameObject.SetActive(true);
    }

    private void DrawMesh(int index, HudMeshPreview mesh, HudContactPlacement placement, Material color, Quaternion rotation,
        bool hitWarning)
    {
        if (index == meshes.Count)
            meshes.Add(new MeshWidget(Child(anchor, "Structure mesh").transform));

        var widget = meshes[index];
        widget.Root.localPosition = rotation * ToUnity(placement.Offset(mesh.Center));
        widget.Root.localRotation = rotation;
        if (widget.Shape?.Offsets != mesh.Offsets || widget.Shape?.Edges != mesh.Edges ||
            widget.Shape?.Sections != mesh.Sections || widget.Color != color)
        {
            widget.Shape = mesh;
            widget.Color = color;
            int lineCount = mesh.Edges?.Length ?? mesh.Sections!.Length;
            for (int i = 0; i < lineCount; i++)
            {
                const float width = 0.0016f;
                if (i == widget.Lines.Count)
                {
                    var created = Line(widget.Root, "Collider outline", color, width, DrawOrder.Wireframe);
                    created.CapVertices = 0;
                    if (widget.HitWarning)
                        SetStructureOutline(created, true);
                    widget.Lines.Add(created);
                }

                var line = widget.Lines[i];
                line.Material = color;
                if (mesh.Edges is { } edges)
                {
                    line.PositionCount = 2;
                    line.SetPosition(0, Point(edges[i].Start));
                    line.SetPosition(1, Point(edges[i].End));
                }
                else
                {
                    var section = mesh.Sections![i];
                    line.PositionCount = section.Length;
                    for (int j = 0; j < section.Length; j++)
                        line.SetPosition(j, Point(section[j]));
                }
                line.Enabled = true;
            }
            for (int i = lineCount; i < widget.Lines.Count; i++)
                widget.Lines[i].Enabled = false;
        }
        if (widget.HitWarning != hitWarning)
        {
            widget.HitWarning = hitWarning;
            foreach (var line in widget.Lines)
                SetStructureOutline(line, hitWarning);
        }
        widget.Root.gameObject.SetActive(true);

        Vector3 Point(Vec3 local) =>
            ToUnity(mesh.Offsets.Point(FlightContactPatches.Vector(local))) * HudContactPlacement.Scale;
    }

    private void SetStructureOutline(HudLine line, bool hitWarning) =>
        line.SetOutline(hitWarning ? hitWarningOutline : lineOutline,
            hitWarning ? HitWarningOutlineThickness : OutlineThickness);

    private static (System.Numerics.Vector3 Start, System.Numerics.Vector3 End) PatchSpan(ContactGeometry[] points)
    {
        var first = FlightContactPatches.Vector(points[0].PointWorld);
        var result = (first, first);
        float farthest = 0;
        for (int i = 0; i < points.Length; i++)
        for (int j = i + 1; j < points.Length; j++)
        {
            var start = FlightContactPatches.Vector(points[i].PointWorld);
            var end = FlightContactPatches.Vector(points[j].PointWorld);
            float distance = System.Numerics.Vector3.DistanceSquared(start, end);
            if (distance > farthest)
            {
                result = (start, end);
                farthest = distance;
            }
        }
        return result;
    }
}
