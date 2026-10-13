using System.Globalization;
using System.Numerics;
using System.Text;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;

namespace Seamstress.Core.Editing;

public enum PatternTool
{
    Select,
    EditPoints,
    Sew,
    Polygon,
    Rectangle,
    Ellipse,
}

/// <summary>
/// Interaction logic of the 2D viewport: turns pointer events (already converted to pattern centimetres, Y up) into
/// edits on the workspace, and describes what the viewport should draw.
/// </summary>
public sealed class PatternEditor
{
    private enum Drag { None, Pieces, Point, Control, Bend, Shape, Uv }

    private readonly Workspace _workspace;
    private PatternTool _tool = PatternTool.Select;
    private bool _uvMode;

    private Drag _drag;
    private Vector2 _dragStart;
    private Vector2 _dragLast;
    private bool _dragMoved;
    private string _dragPiece = "";
    private int _dragIndex;
    private readonly List<Vector2> _draft = new();
    private Vector2? _draftCursor;
    private Vector2 _draftCursorRaw;
    private EdgeRef? _hoverEdge;
    private EdgeRef? _pendingEdge;
    private readonly List<EdgeRef> _measured = new();
    private bool _constrain;

    /// <summary>Raised when something only the 2D view shows (hover, draft shape, tool) changed.</summary>
    public event Action? Changed;

    public PatternEditor(Workspace workspace) => _workspace = workspace;

    public PatternTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value)
                return;
            Cancel();
            _tool = value;
            Changed?.Invoke();
        }
    }

    /// <summary>When set, the view shows the UV layout and dragging moves pieces in texture space.</summary>
    public bool UvMode
    {
        get => _uvMode;
        set
        {
            if (_uvMode == value)
                return;
            Cancel();
            _uvMode = value;
            Changed?.Invoke();
        }
    }

    public EdgeRef? PendingEdge => _pendingEdge;

    /// <summary>
    /// Shift held while drawing: rectangles become squares, ellipses become circles, and each polygon side snaps
    /// to 15° steps and to a whole number of <see cref="LengthStep"/>.
    /// </summary>
    public bool Constrain
    {
        get => _constrain;
        set
        {
            if (_constrain == value)
                return;
            _constrain = value;
            // Pressing or releasing Shift updates the shape being drawn without waiting for the pointer to move.
            if (_drag == Drag.Shape && _dragMoved)
                BuildShape(_dragLast);
            else if (_tool == PatternTool.Polygon && _draft.Count > 0 && _draftCursor != null)
                _draftCursor = PolygonPoint(_draftCursorRaw);
            else
                return;
            Changed?.Invoke();
        }
    }

    /// <summary>Length in centimetres that constrained polygon sides are rounded to.</summary>
    public float LengthStep { get; set; } = 1f;

    private Vector2 PolygonPoint(Vector2 world)
    {
        if (!_constrain || _draft.Count == 0)
            return world;
        var from = _draft[^1];
        var d = world - from;
        float length = d.Length();
        if (length < 1e-4f)
            return world;
        const float angleStep = MathF.PI / 12;
        float angle = MathF.Round(MathF.Atan2(d.Y, d.X) / angleStep) * angleStep;
        if (LengthStep > 0)
            length = MathF.Max(LengthStep, MathF.Round(length / LengthStep) * LengthStep);
        return from + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * length;
    }

    private void Measure(params EdgeRef[] edges)
    {
        _measured.Clear();
        _measured.AddRange(edges);
        Changed?.Invoke();
    }

    private Document Doc => _workspace.Document;

    public string Hint => _uvMode
        ? "UV layout of the active garment: drag a piece to move it in texture space. The checker shows texel density."
        : _tool switch
        {
            PatternTool.Select => "Click a piece to select it, drag to move. Click an edge to see its length. Ctrl+click adds to the selection.",
            PatternTool.EditPoints => "Drag a point to move it, drag an edge to curve it, double-click an edge to add a point, Delete removes the selected point. Click an edge to see its length.",
            PatternTool.Sew => _pendingEdge == null ? "Click the first edge to sew." : "Click the edge to sew it to. Esc cancels.",
            PatternTool.Polygon => _draft.Count == 0 ? "Click to place the first corner." : "Click to add corners; click the first corner, double-click or press Enter to finish. Hold Shift to snap the side's angle and length.",
            PatternTool.Rectangle => "Drag to draw a rectangular piece. Hold Shift for a square.",
            PatternTool.Ellipse => "Drag to draw an elliptical piece. Hold Shift for a circle.",
            _ => "",
        };

    // ---------------------------------------------------------------- hit testing

    private IEnumerable<(Garment Garment, PatternPiece Piece)> VisiblePieces()
    {
        // Later pieces are drawn on top, so test them first.
        for (int g = Doc.Garments.Count - 1; g >= 0; g--)
        {
            var garment = Doc.Garments[g];
            for (int p = garment.Pieces.Count - 1; p >= 0; p--)
            {
                if (Workspace.IsEffectivelyVisible(garment, garment.Pieces[p]) && garment.Pieces[p].Points.Count >= 3)
                    yield return (garment, garment.Pieces[p]);
            }
        }
    }

    public PatternPiece? HitPiece(Vector2 world)
    {
        foreach (var (_, piece) in VisiblePieces())
        {
            if (Outline.Contains(Outline.Flatten(piece), world - piece.Origin))
                return piece;
        }
        return null;
    }

    public (PatternPiece Piece, int Index)? HitPoint(Vector2 world, float tolerance)
    {
        (PatternPiece, int)? best = null;
        float bestDistance = tolerance;
        foreach (var (_, piece) in VisiblePieces())
        {
            for (int i = 0; i < piece.Points.Count; i++)
            {
                float d = Vector2.Distance(piece.Points[i].Position + piece.Origin, world);
                if (d <= bestDistance)
                {
                    bestDistance = d;
                    best = (piece, i);
                }
            }
        }
        return best;
    }

    public (PatternPiece Piece, int Edge, Vector2 Closest)? HitEdge(Vector2 world, float tolerance)
    {
        (PatternPiece, int, Vector2)? best = null;
        float bestDistance = tolerance;
        foreach (var (_, piece) in VisiblePieces())
        {
            var local = world - piece.Origin;
            for (int e = 0; e < piece.Points.Count; e++)
            {
                var pts = Outline.FlattenEdge(piece, e);
                for (int i = 1; i < pts.Count; i++)
                {
                    float d = Outline.DistanceToSegment(local, pts[i - 1], pts[i], out float t);
                    if (d <= bestDistance)
                    {
                        bestDistance = d;
                        best = (piece, e, Vector2.Lerp(pts[i - 1], pts[i], t));
                    }
                }
            }
        }
        return best;
    }

    private (PatternPiece Piece, int Edge)? HitControl(Vector2 world, float tolerance)
    {
        foreach (var piece in _workspace.SelectedPieceObjects())
        {
            for (int e = 0; e < piece.Points.Count; e++)
            {
                if (piece.Points[e].Curve is { } c && Vector2.Distance(c + piece.Origin, world) <= tolerance)
                    return (piece, e);
            }
        }
        return null;
    }

    private static Vector2 EdgeMiddle(PatternPiece piece, int edge)
    {
        var pts = Outline.SampleEdge(piece, edge, 2);
        return pts[1] + piece.Origin;
    }

    public Seam? HitSeam(Vector2 world, float tolerance)
    {
        foreach (var garment in Doc.Garments)
        {
            foreach (var seam in garment.Seams)
            {
                var a = garment.FindPiece(seam.A.PieceId);
                var b = garment.FindPiece(seam.B.PieceId);
                if (a == null || b == null || !Workspace.IsEffectivelyVisible(garment, a) || !Workspace.IsEffectivelyVisible(garment, b))
                    continue;
                if (Outline.DistanceToSegment(world, EdgeMiddle(a, seam.A.Edge), EdgeMiddle(b, seam.B.Edge), out _) <= tolerance)
                    return seam;
            }
        }
        return null;
    }

    private PatternPiece? HitUvPiece(Vector2 uv)
    {
        foreach (var (garment, piece) in VisiblePieces())
        {
            if (garment.Id != _workspace.ActiveGarmentId)
                continue;
            var layout = UvLayout.For(garment);
            var polygon = Outline.Flatten(piece).Select(p => layout.Map(piece, p)).ToList();
            if (Outline.Contains(polygon, uv))
                return piece;
        }
        return null;
    }

    // ---------------------------------------------------------------- pointer events

    public void PointerDown(Vector2 world, float tolerance, bool additive)
    {
        _dragStart = _dragLast = world;
        _dragMoved = false;
        _drag = Drag.None;

        if (_uvMode)
        {
            var uvPiece = HitUvPiece(world);
            if (uvPiece == null)
            {
                _workspace.ClearSelection();
                return;
            }
            if (!_workspace.SelectedPieces.Contains(uvPiece.Id) || additive)
                _workspace.SelectPiece(uvPiece.Id, additive);
            _drag = Drag.Uv;
            return;
        }

        switch (_tool)
        {
            case PatternTool.Select:
                {
                    // An edge is hit a little outside the piece too, so it can be clicked to read its length.
                    var edge = HitEdge(world, tolerance);
                    var piece = HitPiece(world) ?? edge?.Piece;
                    var seam = piece == null || !_workspace.SelectedPieces.Contains(piece.Id) ? HitSeam(world, tolerance) : null;
                    if (seam != null)
                    {
                        _workspace.SelectSeam(seam.Id);
                    }
                    else if (piece != null)
                    {
                        if (!_workspace.SelectedPieces.Contains(piece.Id) || additive)
                            _workspace.SelectPiece(piece.Id, additive);
                        if (_workspace.SelectedPieces.Contains(piece.Id))
                            _drag = Drag.Pieces;
                    }
                    else
                    {
                        _workspace.ClearSelection();
                    }
                    if (seam == null && edge is { } clicked && clicked.Piece == piece)
                        Measure(new EdgeRef(clicked.Piece.Id, clicked.Edge));
                    else
                        Measure();
                    break;
                }
            case PatternTool.EditPoints:
                {
                    if (HitPoint(world, tolerance) is { } point)
                    {
                        _workspace.SelectPoint(point.Piece.Id, point.Index);
                        _drag = Drag.Point;
                        _dragPiece = point.Piece.Id;
                        _dragIndex = point.Index;
                        // The two edges that meet at the point change length as it moves.
                        int count = point.Piece.Points.Count;
                        Measure(new EdgeRef(point.Piece.Id, (point.Index + count - 1) % count), new EdgeRef(point.Piece.Id, point.Index));
                    }
                    else if (HitControl(world, tolerance) is { } control)
                    {
                        _drag = Drag.Control;
                        _dragPiece = control.Piece.Id;
                        _dragIndex = control.Edge;
                        Measure(new EdgeRef(control.Piece.Id, control.Edge));
                    }
                    else if (HitEdge(world, tolerance) is { } edge)
                    {
                        _workspace.SelectPiece(edge.Piece.Id);
                        _drag = Drag.Bend;
                        _dragPiece = edge.Piece.Id;
                        _dragIndex = edge.Edge;
                        Measure(new EdgeRef(edge.Piece.Id, edge.Edge));
                    }
                    else if (HitPiece(world) is { } piece)
                    {
                        _workspace.SelectPiece(piece.Id);
                        Measure();
                    }
                    else
                    {
                        _workspace.ClearSelection();
                        Measure();
                    }
                    break;
                }
            case PatternTool.Sew:
                {
                    var edge = HitEdge(world, tolerance);
                    if (edge == null)
                    {
                        if (HitSeam(world, tolerance) is { } seam)
                            _workspace.SelectSeam(seam.Id);
                        else if (_pendingEdge == null)
                            _workspace.ClearSelection();
                        break;
                    }
                    var hit = new EdgeRef(edge.Value.Piece.Id, edge.Value.Edge);
                    if (_pendingEdge == null)
                    {
                        _pendingEdge = hit;
                    }
                    else
                    {
                        if (!(hit.PieceId == _pendingEdge.PieceId && hit.Edge == _pendingEdge.Edge))
                            _workspace.AddSeam(_pendingEdge, hit);
                        _pendingEdge = null;
                    }
                    Changed?.Invoke();
                    break;
                }
            case PatternTool.Polygon:
                {
                    if (_draft.Count >= 3 && Vector2.Distance(_draft[0], world) <= tolerance * 1.5f)
                    {
                        FinishPolygon();
                    }
                    else
                    {
                        var corner = PolygonPoint(world);
                        _draft.Add(corner);
                        _draftCursorRaw = world;
                        _draftCursor = corner;
                        Changed?.Invoke();
                    }
                    break;
                }
            case PatternTool.Rectangle:
            case PatternTool.Ellipse:
                _drag = Drag.Shape;
                break;
        }
    }

    public void PointerMove(Vector2 world, float tolerance)
    {
        var delta = world - _dragLast;
        if (_drag != Drag.None && !_dragMoved)
        {
            // Ignore jitter so a plain click never counts as a drag.
            if (Vector2.Distance(world, _dragStart) < tolerance * 0.35f)
                return;
            _dragMoved = true;
            delta = world - _dragStart;
            switch (_drag)
            {
                case Drag.Pieces: _workspace.History.Begin(_workspace.SelectedPieces.Count == 1 ? "Move piece" : "Move pieces"); break;
                case Drag.Point: _workspace.History.Begin("Move point"); break;
                case Drag.Control:
                case Drag.Bend: _workspace.History.Begin("Curve edge"); break;
                case Drag.Uv: _workspace.History.Begin("Move UVs"); break;
            }
        }
        _dragLast = world;

        switch (_drag)
        {
            case Drag.Pieces:
                foreach (var piece in _workspace.SelectedPieceObjects())
                    piece.Origin += delta;
                _workspace.History.NotifyLiveChange();
                return;
            case Drag.Uv:
                foreach (var piece in _workspace.SelectedPieceObjects())
                    piece.Uv.Offset += delta;
                _workspace.History.NotifyLiveChange();
                return;
            case Drag.Point:
                if (Doc.FindPiece(_dragPiece) is { } owner && _dragIndex < owner.Points.Count)
                {
                    owner.Points[_dragIndex].Position = world - owner.Origin;
                    _workspace.History.NotifyLiveChange();
                }
                return;
            case Drag.Control:
                if (Doc.FindPiece(_dragPiece) is { } curved && _dragIndex < curved.Points.Count)
                {
                    curved.Points[_dragIndex].Curve = world - curved.Origin;
                    _workspace.History.NotifyLiveChange();
                }
                return;
            case Drag.Bend:
                if (Doc.FindPiece(_dragPiece) is { } bent && _dragIndex < bent.Points.Count)
                {
                    // Choose the control point that makes the curve's midpoint follow the cursor.
                    var a = Outline.EdgeStart(bent, _dragIndex);
                    var b = Outline.EdgeEnd(bent, _dragIndex);
                    bent.Points[_dragIndex].Curve = 2 * (world - bent.Origin) - (a + b) / 2;
                    _workspace.History.NotifyLiveChange();
                }
                return;
            case Drag.Shape:
                BuildShape(world);
                Changed?.Invoke();
                return;
        }

        if (_uvMode)
            return;
        if (_tool == PatternTool.Polygon && _draft.Count > 0)
        {
            _draftCursorRaw = world;
            _draftCursor = PolygonPoint(world);
            Changed?.Invoke();
        }
        else if (_tool is PatternTool.Sew or PatternTool.EditPoints)
        {
            var edge = HitEdge(world, tolerance);
            var hover = edge == null ? null : new EdgeRef(edge.Value.Piece.Id, edge.Value.Edge);
            if (hover?.PieceId != _hoverEdge?.PieceId || hover?.Edge != _hoverEdge?.Edge)
            {
                _hoverEdge = hover;
                Changed?.Invoke();
            }
        }
    }

    private void BuildShape(Vector2 world)
    {
        _draft.Clear();
        if (_constrain)
        {
            // Equal width and height, growing in the direction of the drag.
            var d = world - _dragStart;
            float side = MathF.Max(MathF.Abs(d.X), MathF.Abs(d.Y));
            world = _dragStart + new Vector2(d.X < 0 ? -side : side, d.Y < 0 ? -side : side);
        }
        var min = Vector2.Min(_dragStart, world);
        var max = Vector2.Max(_dragStart, world);
        if (_tool == PatternTool.Rectangle)
        {
            _draft.Add(min);
            _draft.Add(new Vector2(max.X, min.Y));
            _draft.Add(max);
            _draft.Add(new Vector2(min.X, max.Y));
        }
        else
        {
            var centre = (min + max) / 2;
            var radius = (max - min) / 2;
            const int steps = 16;
            for (int i = 0; i < steps; i++)
            {
                float a = i * MathF.Tau / steps;
                _draft.Add(centre + new Vector2(MathF.Cos(a) * radius.X, MathF.Sin(a) * radius.Y));
            }
        }
    }

    public void PointerUp(Vector2 world)
    {
        var drag = _drag;
        _drag = Drag.None;
        switch (drag)
        {
            case Drag.Pieces:
            case Drag.Point:
            case Drag.Control:
            case Drag.Bend:
            case Drag.Uv:
                if (_dragMoved)
                    _workspace.History.Commit();
                break;
            case Drag.Shape:
                {
                    var outline = _draft.ToList();
                    _draft.Clear();
                    if (_dragMoved && outline.Count >= 3)
                        _workspace.AddPiece(outline, _tool == PatternTool.Rectangle ? "Rectangle" : "Ellipse");
                    Changed?.Invoke();
                    break;
                }
        }
    }

    public void DoubleClick(Vector2 world, float tolerance)
    {
        if (_uvMode)
            return;
        if (_tool == PatternTool.Polygon)
        {
            // The first click of the double-click already added a corner; drop it if it duplicates the previous one.
            if (_draft.Count >= 2 && Vector2.Distance(_draft[^1], _draft[^2]) <= tolerance)
                _draft.RemoveAt(_draft.Count - 1);
            FinishPolygon();
        }
        else if (_tool == PatternTool.EditPoints && HitPoint(world, tolerance) == null && HitEdge(world, tolerance) is { } edge)
        {
            if (_workspace.History.InTransaction)
                _workspace.History.Cancel();
            _drag = Drag.None;
            _workspace.InsertPoint(edge.Piece.Id, edge.Edge, edge.Closest);
        }
    }

    private void FinishPolygon()
    {
        var outline = _draft.ToList();
        _draft.Clear();
        _draftCursor = null;
        if (outline.Count >= 3)
            _workspace.AddPiece(outline, "Piece");
        Changed?.Invoke();
    }

    /// <summary>Enter: completes the polygon being drawn.</summary>
    public void Confirm()
    {
        if (_tool == PatternTool.Polygon && _draft.Count >= 3)
            FinishPolygon();
    }

    /// <summary>Esc: abandons whatever is in progress.</summary>
    public void Cancel()
    {
        if (_workspace.History.InTransaction)
            _workspace.History.Cancel();
        _drag = Drag.None;
        _draft.Clear();
        _draftCursor = null;
        _pendingEdge = null;
        _hoverEdge = null;
        _measured.Clear();
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- scene description

    private static readonly string[] SeamColors = { "#FF5A5F", "#2EC4B6", "#FFB400", "#8E7CFF", "#5BC0EB", "#F15BB5", "#9BC53D", "#FF8C42" };

    private static void AppendPoints(StringBuilder sb, IEnumerable<Vector2> points)
    {
        var inv = CultureInfo.InvariantCulture;
        sb.Append('[');
        bool first = true;
        foreach (var p in points)
        {
            if (!first)
                sb.Append(',');
            first = false;
            sb.Append(p.X.ToString("0.###", inv)).Append(',').Append(p.Y.ToString("0.###", inv));
        }
        sb.Append(']');
    }

    private static string Q(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    /// <summary>Everything the 2D viewport draws, as JSON. Coordinates are pattern centimetres, or UV units in UV mode.</summary>
    public string SceneJson(int textureResolution)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(4096);
        sb.Append("{\"mode\":").Append(_uvMode ? "\"uv\"" : "\"pattern\"");
        sb.Append(",\"tool\":").Append(Q(_tool.ToString()));
        sb.Append(",\"pieces\":[");
        bool firstPiece = true;
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var garment in Doc.Garments)
        {
            // Each garment has its own texture space, so the UV view shows one garment at a time.
            if (_uvMode && garment.Id != _workspace.ActiveGarmentId)
                continue;
            var layout = UvLayout.For(garment);
            foreach (var piece in garment.Pieces)
            {
                if (piece.Points.Count < 3 || !Workspace.IsEffectivelyVisible(garment, piece))
                    continue;
                Vector2 Map(Vector2 local) => _uvMode ? layout.Map(piece, local) : local + piece.Origin;
                var outline = Outline.Flatten(piece).Select(Map).ToList();
                foreach (var p in outline)
                {
                    min = Vector2.Min(min, p);
                    max = Vector2.Max(max, p);
                }
                var material = Doc.FindMaterial(piece.MaterialId);
                bool selected = _workspace.SelectedPieces.Contains(piece.Id);
                if (!firstPiece)
                    sb.Append(',');
                firstPiece = false;
                sb.Append("{\"id\":").Append(Q(piece.Id));
                sb.Append(",\"name\":").Append(Q(piece.Name));
                sb.Append(",\"color\":").Append(Q(material?.Color ?? "#C8CED8"));
                sb.Append(",\"selected\":").Append(selected ? "true" : "false");
                sb.Append(",\"active\":").Append(garment.Id == _workspace.ActiveGarmentId ? "true" : "false");
                sb.Append(",\"outline\":");
                AppendPoints(sb, outline);
                sb.Append(",\"points\":");
                AppendPoints(sb, piece.Points.Select(p => Map(p.Position)));
                var centre = outline.Aggregate(Vector2.Zero, (a, b) => a + b) / outline.Count;
                sb.Append(inv, $",\"label\":[{centre.X:0.###},{centre.Y:0.###}]");
                sb.Append(inv, $",\"density\":{layout.TexelDensity(piece, material, textureResolution):0.##}");
                if (_workspace.SelectedPoint is { } sp && sp.PieceId == piece.Id)
                    sb.Append(",\"selectedPoint\":").Append(sp.Index);
                if (!_uvMode && selected && _tool == PatternTool.EditPoints)
                {
                    sb.Append(",\"controls\":[");
                    bool firstControl = true;
                    for (int e = 0; e < piece.Points.Count; e++)
                    {
                        if (piece.Points[e].Curve is not { } c)
                            continue;
                        if (!firstControl)
                            sb.Append(',');
                        firstControl = false;
                        AppendPoints(sb, new[] { Outline.EdgeStart(piece, e) + piece.Origin, c + piece.Origin, Outline.EdgeEnd(piece, e) + piece.Origin });
                    }
                    sb.Append(']');
                }
                sb.Append('}');
            }
        }
        sb.Append("],\"seams\":[");
        if (!_uvMode)
        {
            bool firstSeam = true;
            int colorIndex = 0;
            foreach (var garment in Doc.Garments)
            {
                foreach (var seam in garment.Seams)
                {
                    string color = SeamColors[colorIndex++ % SeamColors.Length];
                    var a = garment.FindPiece(seam.A.PieceId);
                    var b = garment.FindPiece(seam.B.PieceId);
                    if (a == null || b == null || seam.A.Edge >= a.EdgeCount || seam.B.Edge >= b.EdgeCount)
                        continue;
                    if (!Workspace.IsEffectivelyVisible(garment, a) || !Workspace.IsEffectivelyVisible(garment, b))
                        continue;
                    if (!firstSeam)
                        sb.Append(',');
                    firstSeam = false;
                    sb.Append("{\"id\":").Append(Q(seam.Id));
                    sb.Append(",\"color\":").Append(Q(color));
                    sb.Append(",\"selected\":").Append(seam.Id == _workspace.SelectedSeamId ? "true" : "false");
                    sb.Append(",\"reversed\":").Append(seam.Reversed ? "true" : "false");
                    sb.Append(",\"a\":");
                    AppendPoints(sb, Outline.FlattenEdge(a, seam.A.Edge).Select(p => p + a.Origin));
                    sb.Append(",\"b\":");
                    AppendPoints(sb, Outline.FlattenEdge(b, seam.B.Edge).Select(p => p + b.Origin));
                    sb.Append('}');
                }
            }
        }
        sb.Append(']');

        void Edge(string name, EdgeRef? edge)
        {
            var piece = edge == null ? null : Doc.FindPiece(edge.PieceId);
            if (piece == null || edge!.Edge >= piece.EdgeCount || _uvMode)
                return;
            sb.Append(",\"").Append(name).Append("\":");
            AppendPoints(sb, Outline.FlattenEdge(piece, edge.Edge).Select(p => p + piece.Origin));
        }
        Edge("hover", _hoverEdge);
        Edge("pending", _pendingEdge);

        // Length read-outs as x, y, length, nx, ny: the middle of the edge, its length in centimetres, and the
        // direction into the piece, so labels of two edges that lie against each other can be kept apart.
        if (!_uvMode)
        {
            var edges = new List<EdgeRef>();
            if (_tool is PatternTool.Select or PatternTool.EditPoints)
                edges.AddRange(_measured.Where(m => _workspace.SelectedPieces.Contains(m.PieceId)));
            if (_hoverEdge != null)
                edges.Add(_hoverEdge);
            if (_pendingEdge != null)
                edges.Add(_pendingEdge);
            if (_workspace.SelectedSeamId != null && Doc.Garments.SelectMany(g => g.Seams).FirstOrDefault(s => s.Id == _workspace.SelectedSeamId) is { } chosen)
            {
                edges.Add(chosen.A);
                edges.Add(chosen.B);
            }
            sb.Append(",\"measures\":[");
            bool firstMeasure = true;
            var seen = new HashSet<(string, int)>();
            foreach (var edge in edges)
            {
                var piece = Doc.FindPiece(edge.PieceId);
                if (piece == null || edge.Edge >= piece.EdgeCount || !seen.Add((edge.PieceId, edge.Edge)))
                    continue;
                var at = EdgeMiddle(piece, edge.Edge);
                var inward = piece.Points.Aggregate(Vector2.Zero, (sum, p) => sum + p.Position) / piece.Points.Count + piece.Origin - at;
                inward = inward.LengthSquared() > 1e-8f ? Vector2.Normalize(inward) : Vector2.Zero;
                sb.Append(firstMeasure ? "" : ",").Append(inv, $"{at.X:0.###},{at.Y:0.###},{Outline.EdgeLength(piece, edge.Edge):0.###},{inward.X:0.##},{inward.Y:0.##}");
                firstMeasure = false;
            }
            sb.Append(']');
        }

        if (_draft.Count > 0)
        {
            var draft = _tool == PatternTool.Polygon && _draftCursor is { } cursor ? _draft.Append(cursor).ToList() : _draft;
            sb.Append(",\"draft\":");
            AppendPoints(sb, draft);
            sb.Append(",\"draftClosed\":").Append(_tool == PatternTool.Polygon ? "false" : "true");
            if (_tool == PatternTool.Ellipse)
            {
                // An ellipse is measured across, not along its sides.
                var lo = draft.Aggregate(Vector2.Min);
                var hi = draft.Aggregate(Vector2.Max);
                sb.Append(inv, $",\"draftSize\":[{(lo.X + hi.X) / 2:0.###},{(lo.Y + hi.Y) / 2:0.###},{hi.X - lo.X:0.###},{hi.Y - lo.Y:0.###}]");
            }
            else
            {
                // Every side of a polygon being drawn; a rectangle's width and height.
                int sides = _tool == PatternTool.Rectangle ? Math.Min(2, draft.Count - 1) : draft.Count - 1;
                sb.Append(",\"draftMeasures\":[");
                for (int i = 0; i < sides; i++)
                {
                    var mid = (draft[i] + draft[i + 1]) / 2;
                    sb.Append(i == 0 ? "" : ",").Append(inv, $"{mid.X:0.###},{mid.Y:0.###},{Vector2.Distance(draft[i], draft[i + 1]):0.###}");
                }
                sb.Append(']');
            }
        }
        if (_uvMode)
        {
            min = Vector2.Zero;
            max = Vector2.One;
        }
        else if (firstPiece)
        {
            min = new Vector2(-20, -20);
            max = new Vector2(120, 100);
        }
        sb.Append(inv, $",\"bounds\":[{min.X:0.###},{min.Y:0.###},{max.X:0.###},{max.Y:0.###}]");
        sb.Append(",\"hint\":").Append(Q(Hint));
        sb.Append('}');
        return sb.ToString();
    }
}
