using System.Globalization;
using System.Numerics;
using System.Text;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;

namespace Seamstress.Core.Editing;

/// <summary>Which handles the transform gizmos show, in both views.</summary>
public enum GizmoMode
{
    Full,
    Move,
    Rotate,
    Scale,
}

public enum PatternTool
{
    Select,
    EditPoints,
    TransformPoints,
    Sew,
    Polygon,
    Rectangle,
    Ellipse,
    Ruler,
}

/// <summary>
/// Interaction logic of the 2D viewport: turns pointer events (already converted to pattern centimetres, Y up) into
/// edits on the workspace, and describes what the viewport should draw.
/// </summary>
public sealed class PatternEditor
{
    private enum Drag { None, Pieces, Point, Control, Bend, Shape, Uv, PointsMove, PointsRotate, PointsScale, Marquee, Ruler }

    /// <summary>Sizes of the 2D handles in screen pixels: the length of the arrows and the radius of the ring.</summary>
    public const float GizmoArrow = 64, GizmoRing = 88;

    /// <summary>
    /// Where the scale boxes sit, in pixels from the centre. With every handle showing they are outside the ring,
    /// and the box that scales evenly is tucked between the arrows; on their own they take the arrows' place.
    /// </summary>
    public const float GizmoScaleFull = 112, GizmoScaleAlone = 64, GizmoScaleEven = 40;

    private readonly record struct PointStart(string PieceId, int Index, Vector2 World, Vector2? Curve, bool MoveCurve);

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
    private readonly List<(string PieceId, int Index)> _points = new();   // selection of the Transform Points tool
    private List<PointStart> _pointStarts = new();
    private int _pointsAxis;            // 0 = free (or even scaling), 1 = along X, 2 = along Y
    private float _pointsPixel = 1;     // world units per screen pixel when the drag began
    private GizmoMode _gizmoMode;
    private Vector2 _pointsPivot;
    private Vector2? _marqueeEnd;
    private bool _constrain;
    private bool _onHandle;                 // the last press landed on one of the 2D handles
    private Vector2? _rulerStart, _rulerEnd;

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
            else if ((_drag is Drag.Point or Drag.Control) && _dragMoved)
                DragPoint(_dragLast);
            else if (_drag == Drag.Ruler && _dragMoved)
                _rulerEnd = RulerPoint(_dragLast, _rulerTolerance, _rulerStart);
            else if (_tool == PatternTool.Polygon && _draft.Count > 0 && _draftCursor != null)
                _draftCursor = PolygonPoint(_draftCursorRaw);
            else
                return;
            Changed?.Invoke();
        }
    }

    /// <summary>Length in centimetres that constrained polygon sides are rounded to.</summary>
    public float LengthStep { get; set; } = 1f;

    /// <summary>Size in centimetres of the grid squares the 2D view shows; Edit Points snaps to it with Shift.</summary>
    public float GridStep { get; set; } = 1f;

    private Vector2 SnapToGrid(Vector2 world)
        => GridStep > 0 ? new Vector2(MathF.Round(world.X / GridStep), MathF.Round(world.Y / GridStep)) * GridStep : world;

    private float _rulerTolerance;

    /// <summary>
    /// Where a ruler end goes: onto a pattern point when it is near one, otherwise under the pointer. With Shift the
    /// far end keeps to 15° steps from the near one.
    /// </summary>
    private Vector2 RulerPoint(Vector2 world, float tolerance, Vector2? from)
    {
        if (HitPoint(world, tolerance) is { } point)
            return point.Piece.PointAt(point.Index).Position + point.Piece.Origin;
        if (_constrain && from is { } a)
        {
            var d = world - a;
            const float angleStep = MathF.PI / 12;
            float angle = MathF.Round(MathF.Atan2(d.Y, d.X) / angleStep) * angleStep;
            return a + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * d.Length();
        }
        return world;
    }

    /// <summary>The two ends of the ruler in pattern centimetres, or null when nothing is being measured.</summary>
    public (Vector2 A, Vector2 B)? Ruler => _rulerStart is { } a && _rulerEnd is { } b ? (a, b) : null;

    /// <summary>
    /// Double-click on a piece: switches to Transform Points with every point of the piece selected, so the whole
    /// shape can be moved, turned or scaled. Ctrl or Shift adds them to the points already selected.
    /// </summary>
    private bool SelectPiecePoints(Vector2 world, bool additive)
    {
        if (HitPiece(world) is not { } piece)
            return false;
        if (_workspace.History.InTransaction)
            _workspace.History.Cancel();
        _drag = Drag.None;
        var keep = additive && _tool == PatternTool.TransformPoints ? _points.ToList() : new List<(string, int)>();
        Tool = PatternTool.TransformPoints;
        _points.Clear();
        _points.AddRange(keep);
        for (int i = 0; i < piece.PointCount; i++)
        {
            if (!_points.Contains((piece.Id, i)))
                _points.Add((piece.Id, i));
        }
        _workspace.SelectPiece(piece.Id, additive);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Moves the point or curve control being dragged in Edit Points; Shift puts it on the grid.</summary>
    private void DragPoint(Vector2 world)
    {
        if (Doc.FindPiece(_dragPiece) is not { } owner || _dragIndex >= owner.PointCount)
            return;
        var target = (_constrain ? SnapToGrid(world) : world) - owner.Origin;
        if (_drag == Drag.Point)
            owner.PointAt(_dragIndex).Position = target;
        else
            owner.PointAt(_dragIndex).Curve = target;
        _workspace.History.NotifyLiveChange();
    }

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
        ? Lang.T("UV layout of the active garment: drag a piece to move it in texture space. The checker shows texel density.")
        : _tool switch
        {
            PatternTool.Select => Lang.T("Click a piece to select it, drag to move. Use the handles to move, rotate or scale it (Q/W/E/R). Click an edge to see its length. Ctrl+click adds to the selection. Double-click a piece to select all its points."),
            PatternTool.EditPoints => Lang.T("Drag a point to move it, drag an edge to curve it, double-click an edge to add a point (or inside the piece to select all its points), Delete removes the selected point. Hold Shift to snap to the grid. Click an edge to see its length."),
            PatternTool.TransformPoints => Lang.T("Click a point to select it, Ctrl+click to add, drag a box around several, or double-click a piece for all of its points. Drag an arrow to move them, the ring to rotate them, a box to scale them (Q/W/E/R choose the handles)."),
            PatternTool.Sew => _pendingEdge == null ? Lang.T("Click the first edge to sew.") : Lang.T("Click the edge to sew it to. Esc cancels."),
            PatternTool.Polygon => _draft.Count == 0 ? Lang.T("Click to place the first corner.") : Lang.T("Click to add corners; click the first corner, double-click or press Enter to finish. Hold Shift to snap the side's angle and length."),
            PatternTool.Rectangle => Lang.T("Drag to draw a rectangular piece. Hold Shift for a square."),
            PatternTool.Ellipse => Lang.T("Drag to draw an elliptical piece. Hold Shift for a circle."),
            PatternTool.Ruler => Lang.T("Drag to measure a distance; the ends catch on pattern points. Hold Shift to keep to 15° steps. Click to clear."),
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
            // A hole shows what is underneath, so a click there goes through to it.
            if (Outline.Contains(piece, world - piece.Origin))
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
            for (int i = 0; i < piece.PointCount; i++)
            {
                float d = Vector2.Distance(piece.PointAt(i).Position + piece.Origin, world);
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
            for (int e = 0; e < piece.EdgeCount; e++)
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
            for (int e = 0; e < piece.EdgeCount; e++)
            {
                if (piece.PointAt(e).Curve is { } c && Vector2.Distance(c + piece.Origin, world) <= tolerance)
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
            if (Outline.Contains(polygon, uv) && !Outline.FlattenHoles(piece).Any(h => Outline.Contains(h.Select(p => layout.Map(piece, p)).ToList(), uv)))
                return piece;
        }
        return null;
    }

    // ---------------------------------------------------------------- transform points

    /// <summary>The selected points that still exist, in selection order.</summary>
    public IReadOnlyList<(string PieceId, int Index)> SelectedPoints
    {
        get
        {
            _points.RemoveAll(p => Doc.FindPiece(p.PieceId) is not { } piece || p.Index >= piece.PointCount);
            return _points;
        }
    }

    /// <summary>Which handles are shown: all of them, or only those that move, rotate or scale.</summary>
    public GizmoMode GizmoMode
    {
        get => _gizmoMode;
        set
        {
            if (_gizmoMode == value)
                return;
            _gizmoMode = value;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// What the 2D handles act on: the selected points with the Transform Points tool, and every point of the
    /// selected pieces with the Select tool.
    /// </summary>
    private IReadOnlyList<(string PieceId, int Index)> GizmoTargets()
    {
        if (_uvMode)
            return Array.Empty<(string, int)>();
        if (_tool == PatternTool.TransformPoints)
            return SelectedPoints;
        if (_tool != PatternTool.Select)
            return Array.Empty<(string, int)>();
        var all = new List<(string, int)>();
        foreach (var (_, piece) in VisiblePieces())
        {
            if (!_workspace.SelectedPieces.Contains(piece.Id))
                continue;
            for (int i = 0; i < piece.PointCount; i++)
                all.Add((piece.Id, i));
        }
        return all;
    }

    private Vector2? PointsCentre()
    {
        var points = GizmoTargets();
        if (points.Count == 0)
            return null;
        var sum = Vector2.Zero;
        foreach (var (pieceId, index) in points)
        {
            var piece = Doc.FindPiece(pieceId)!;
            sum += piece.PointAt(index).Position + piece.Origin;
        }
        return sum / points.Count;
    }

    /// <summary>Ctrl+A with the Transform Points tool: every point of the active garment.</summary>
    public bool SelectAllPoints()
    {
        if (_tool != PatternTool.TransformPoints || _uvMode || _workspace.ActiveGarment is not { } garment)
            return false;
        _points.Clear();
        foreach (var piece in garment.Pieces.Where(p => Workspace.IsEffectivelyVisible(garment, p)))
        {
            for (int i = 0; i < piece.PointCount; i++)
                _points.Add((piece.Id, i));
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Delete with the Transform Points tool: removes the selected points. False when there are none.</summary>
    public bool DeleteSelectedPoints()
    {
        if (_tool != PatternTool.TransformPoints || SelectedPoints.Count == 0)
            return false;
        var doomed = _points.ToList();
        _points.Clear();
        _workspace.DeletePoints(doomed);
        Changed?.Invoke();
        return true;
    }

    private void BeginPointsDrag(Drag drag, int axis, Vector2 pivot)
    {
        _drag = drag;
        _pointsAxis = axis;
        _pointsPivot = pivot;
        var chosen = GizmoTargets().ToHashSet();
        _pointStarts = chosen.Select(p =>
        {
            var piece = Doc.FindPiece(p.PieceId)!;
            var point = piece.PointAt(p.Index);
            // The curve of an edge comes along only when both of its ends do; otherwise it keeps its handle.
            bool both = chosen.Contains((p.PieceId, piece.NextPoint(p.Index)));
            return new PointStart(p.PieceId, p.Index, point.Position + piece.Origin, point.Curve + piece.Origin, both);
        }).ToList();
    }

    private void ApplyPoints(Func<Vector2, Vector2> transform)
    {
        foreach (var start in _pointStarts)
        {
            if (Doc.FindPiece(start.PieceId) is not { } piece || start.Index >= piece.PointCount)
                continue;
            var point = piece.PointAt(start.Index);
            point.Position = transform(start.World) - piece.Origin;
            if (start.MoveCurve && start.Curve is { } curve)
                point.Curve = transform(curve) - piece.Origin;
        }
        _workspace.History.NotifyLiveChange();
    }

    private bool ShowsMove => _gizmoMode is GizmoMode.Full or GizmoMode.Move;
    private bool ShowsRotate(int count) => count >= 2 && (_gizmoMode == GizmoMode.Full || _gizmoMode == GizmoMode.Rotate);
    private bool ShowsScale(int count) => count >= 2 && (_gizmoMode == GizmoMode.Full || _gizmoMode == GizmoMode.Scale);
    private float ScaleBoxDistance => _gizmoMode == GizmoMode.Full ? GizmoScaleFull : GizmoScaleAlone;
    private float ScaleEvenOffset => _gizmoMode == GizmoMode.Full ? GizmoScaleEven : 0;

    /// <summary>Starts a drag if the pointer went down on one of the 2D handles.</summary>
    private bool GizmoDown(Vector2 world, float tolerance)
    {
        if (PointsCentre() is not { } centre)
            return false;
        float pixel = _pointsPixel = tolerance / 9f;
        _onHandle = true;
        int count = GizmoTargets().Count;
        var d = (world - centre) / pixel;
        bool On(float x, float y) => MathF.Abs(d.X - x) <= 8 && MathF.Abs(d.Y - y) <= 8;
        float reach = GizmoArrow + 10;
        if (ShowsScale(count) && On(ScaleBoxDistance, 0))
            BeginPointsDrag(Drag.PointsScale, 1, centre);
        else if (ShowsScale(count) && On(0, ScaleBoxDistance))
            BeginPointsDrag(Drag.PointsScale, 2, centre);
        else if (ShowsScale(count) && On(ScaleEvenOffset, ScaleEvenOffset))
            BeginPointsDrag(Drag.PointsScale, 0, centre);
        else if (ShowsMove && d.Length() <= 9)
            BeginPointsDrag(Drag.PointsMove, 0, centre);
        else if (ShowsMove && d.X > 0 && d.X <= reach && MathF.Abs(d.Y) <= 7)
            BeginPointsDrag(Drag.PointsMove, 1, centre);
        else if (ShowsMove && d.Y > 0 && d.Y <= reach && MathF.Abs(d.X) <= 7)
            BeginPointsDrag(Drag.PointsMove, 2, centre);
        else if (ShowsRotate(count) && MathF.Abs(d.Length() - GizmoRing) <= 7)
            BeginPointsDrag(Drag.PointsRotate, 0, centre);
        else
        {
            _onHandle = false;
            return false;
        }
        // Whole pieces are moved by their origin, which keeps their draped shape in the 3D view.
        if (_tool == PatternTool.Select && _drag == Drag.PointsMove)
            _drag = Drag.Pieces;
        return true;
    }

    private void PointsToolDown(Vector2 world, float tolerance, bool additive)
    {
        if (GizmoDown(world, tolerance))
            return;
        if (HitPoint(world, tolerance) is { } hit)
        {
            var key = (hit.Piece.Id, hit.Index);
            if (additive)
            {
                // Ctrl+click toggles one point in or out of the selection.
                if (!_points.Remove(key))
                    _points.Add(key);
            }
            else if (!_points.Contains(key))
            {
                _points.Clear();
                _points.Add(key);
            }
            // A selected point can be dragged directly, taking the rest of the selection with it.
            if (_points.Contains(key))
                BeginPointsDrag(Drag.PointsMove, 0, world);
        }
        else
        {
            if (!additive)
                _points.Clear();
            _drag = Drag.Marquee;
            _marqueeEnd = null;
        }
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- pointer events

    public void PointerDown(Vector2 world, float tolerance, bool additive)
    {
        _dragStart = _dragLast = world;
        _dragMoved = false;
        _drag = Drag.None;
        _onHandle = false;

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
                    _pointsAxis = 0;
                    if (!additive && GizmoDown(world, tolerance))
                    {
                        Measure();
                        break;
                    }
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
                        Measure(new EdgeRef(point.Piece.Id, point.Piece.PreviousPoint(point.Index)), new EdgeRef(point.Piece.Id, point.Index));
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
            case PatternTool.TransformPoints:
                PointsToolDown(world, tolerance, additive);
                break;
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
            case PatternTool.Ruler:
                // Each drag measures afresh; a click without dragging clears the ruler.
                _rulerTolerance = tolerance;
                _rulerStart = _rulerEnd = RulerPoint(world, tolerance, null);
                _drag = Drag.Ruler;
                Changed?.Invoke();
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
                case Drag.Pieces: _workspace.History.Begin(_workspace.SelectedPieces.Count == 1 ? Lang.T("Move piece") : Lang.T("Move pieces")); break;
                case Drag.Point: _workspace.History.Begin(Lang.T("Move point")); break;
                case Drag.Control:
                case Drag.Bend: _workspace.History.Begin(Lang.T("Curve edge")); break;
                case Drag.Uv: _workspace.History.Begin(Lang.T("Move UVs")); break;
                case Drag.PointsMove: _workspace.History.Begin(_pointStarts.Count == 1 ? Lang.T("Move point") : Lang.T("Move points")); break;
                case Drag.PointsRotate: _workspace.History.Begin(_tool == PatternTool.Select ? Lang.T("Rotate pieces") : Lang.T("Rotate points")); break;
                case Drag.PointsScale: _workspace.History.Begin(_tool == PatternTool.Select ? Lang.T("Scale pieces") : Lang.T("Scale points")); break;
            }
        }
        _dragLast = world;

        switch (_drag)
        {
            case Drag.Pieces:
                // Dragged by an arrow, the pieces keep to that arrow's axis.
                if (_pointsAxis == 1)
                    delta.Y = 0;
                else if (_pointsAxis == 2)
                    delta.X = 0;
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
            case Drag.Control:
                DragPoint(world);
                return;
            case Drag.Bend:
                if (Doc.FindPiece(_dragPiece) is { } bent && _dragIndex < bent.EdgeCount)
                {
                    // Choose the control point that makes the curve's midpoint follow the cursor.
                    var a = Outline.EdgeStart(bent, _dragIndex);
                    var b = Outline.EdgeEnd(bent, _dragIndex);
                    bent.PointAt(_dragIndex).Curve = 2 * (world - bent.Origin) - (a + b) / 2;
                    _workspace.History.NotifyLiveChange();
                }
                return;
            case Drag.Shape:
                BuildShape(world);
                Changed?.Invoke();
                return;
            case Drag.PointsMove:
                {
                    var offset = world - _dragStart;
                    // Shift on a free drag keeps to whichever of the two axes the drag mostly follows.
                    int axis = _pointsAxis != 0 ? _pointsAxis : !_constrain ? 0 : MathF.Abs(offset.X) >= MathF.Abs(offset.Y) ? 1 : 2;
                    if (axis == 1)
                        offset.Y = 0;
                    else if (axis == 2)
                        offset.X = 0;
                    ApplyPoints(p => p + offset);
                    return;
                }
            case Drag.PointsRotate:
                {
                    var from = _dragStart - _pointsPivot;
                    var to = world - _pointsPivot;
                    float angle = MathF.Atan2(to.Y, to.X) - MathF.Atan2(from.Y, from.X);
                    if (_constrain)
                        angle = MathF.Round(angle / (MathF.PI / 12)) * (MathF.PI / 12);
                    var turn = Matrix3x2.CreateRotation(angle, _pointsPivot);
                    ApplyPoints(p => Vector2.Transform(p, turn));
                    return;
                }
            case Drag.PointsScale:
                {
                    // A box on an axis scales by how far it is pulled from the centre; the even box by the drag
                    // up and to the right. Shift keeps to steps of a tenth.
                    float Ratio(float now, float then) => MathF.Abs(then) < 1e-5f ? 1 : now / then;
                    float factor = _pointsAxis switch
                    {
                        1 => Ratio(world.X - _pointsPivot.X, _dragStart.X - _pointsPivot.X),
                        2 => Ratio(world.Y - _pointsPivot.Y, _dragStart.Y - _pointsPivot.Y),
                        _ => 1 + (world.X - _dragStart.X + world.Y - _dragStart.Y) / (80 * _pointsPixel),
                    };
                    if (_constrain)
                        factor = MathF.Round(factor * 10) / 10;
                    factor = MathF.Max(factor, 0.05f);
                    var scale = new Vector2(_pointsAxis == 2 ? 1 : factor, _pointsAxis == 1 ? 1 : factor);
                    ApplyPoints(p => _pointsPivot + (p - _pointsPivot) * scale);
                    return;
                }
            case Drag.Marquee:
                _marqueeEnd = world;
                Changed?.Invoke();
                return;
            case Drag.Ruler:
                _rulerEnd = RulerPoint(world, tolerance, _rulerStart);
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
            case Drag.PointsMove:
            case Drag.PointsRotate:
            case Drag.PointsScale:
                if (_dragMoved)
                    _workspace.History.Commit();
                break;
            case Drag.Ruler:
                if (!_dragMoved)
                    _rulerStart = _rulerEnd = null;
                Changed?.Invoke();
                break;
            case Drag.Marquee:
                {
                    if (_dragMoved && _marqueeEnd is { } end)
                    {
                        var lo = Vector2.Min(_dragStart, end);
                        var hi = Vector2.Max(_dragStart, end);
                        foreach (var (_, piece) in VisiblePieces())
                        {
                            for (int i = 0; i < piece.PointCount; i++)
                            {
                                var p = piece.PointAt(i).Position + piece.Origin;
                                if (p.X >= lo.X && p.X <= hi.X && p.Y >= lo.Y && p.Y <= hi.Y && !_points.Contains((piece.Id, i)))
                                    _points.Add((piece.Id, i));
                            }
                        }
                    }
                    _marqueeEnd = null;
                    Changed?.Invoke();
                    break;
                }
            case Drag.Shape:
                {
                    var outline = _draft.ToList();
                    _draft.Clear();
                    if (_dragMoved && outline.Count >= 3)
                        _workspace.AddPiece(outline, _tool == PatternTool.Rectangle ? Lang.N("Rectangle") : Lang.N("Ellipse"));
                    Changed?.Invoke();
                    break;
                }
        }
    }

    public void DoubleClick(Vector2 world, float tolerance, bool additive = false)
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
        else if ((_tool is PatternTool.Select or PatternTool.EditPoints or PatternTool.TransformPoints)
                 && !_onHandle && HitPoint(world, tolerance) == null)
        {
            SelectPiecePoints(world, additive);
        }
    }

    private void FinishPolygon()
    {
        var outline = _draft.ToList();
        _draft.Clear();
        _draftCursor = null;
        if (outline.Count >= 3)
            _workspace.AddPiece(outline, Lang.N("Piece"));
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
        _points.Clear();
        _marqueeEnd = null;
        _rulerStart = _rulerEnd = null;
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
                var holes = Outline.FlattenHoles(piece);
                if (holes.Count > 0)
                {
                    sb.Append(",\"holes\":[");
                    for (int h = 0; h < holes.Count; h++)
                    {
                        if (h > 0)
                            sb.Append(',');
                        AppendPoints(sb, holes[h].Select(Map));
                    }
                    sb.Append(']');
                }
                sb.Append(",\"points\":");
                AppendPoints(sb, piece.AllPoints().Select(p => Map(p.Position)));
                var centre = outline.Aggregate(Vector2.Zero, (a, b) => a + b) / outline.Count;
                sb.Append(inv, $",\"label\":[{centre.X:0.###},{centre.Y:0.###}]");
                sb.Append(inv, $",\"density\":{layout.TexelDensity(piece, material, textureResolution):0.##}");
                if (_workspace.SelectedPoint is { } sp && sp.PieceId == piece.Id)
                    sb.Append(",\"selectedPoint\":").Append(sp.Index);
                if (!_uvMode && _tool == PatternTool.TransformPoints && _points.Any(p => p.PieceId == piece.Id))
                    sb.Append(",\"selectedPoints\":[").AppendJoin(',', SelectedPoints.Where(p => p.PieceId == piece.Id).Select(p => p.Index)).Append(']');
                if (!_uvMode && selected && _tool == PatternTool.EditPoints)
                {
                    sb.Append(",\"controls\":[");
                    bool firstControl = true;
                    for (int e = 0; e < piece.EdgeCount; e++)
                    {
                        if (piece.PointAt(e).Curve is not { } c)
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

        if (!_uvMode && (_tool == PatternTool.TransformPoints || _tool == PatternTool.Select))
        {
            // Handles at the middle of what is selected: x, y, then in pixels the arrow length, the ring radius, how
            // far out the scale boxes are and where the box that scales evenly sits; 0 leaves a handle out. A single
            // point has nothing to turn or scale about. While turning or scaling, they stay where the drag began.
            int count = GizmoTargets().Count;
            if ((_drag is Drag.PointsRotate or Drag.PointsScale ? _pointsPivot : PointsCentre()) is { } handles)
                sb.Append(inv, $",\"pointGizmo\":[{handles.X:0.###},{handles.Y:0.###},{(ShowsMove ? GizmoArrow : 0):0},{(ShowsRotate(count) ? GizmoRing : 0):0},{(ShowsScale(count) ? ScaleBoxDistance : 0):0},{ScaleEvenOffset:0}]");
            if (_drag == Drag.Marquee && _marqueeEnd is { } corner)
                sb.Append(inv, $",\"marquee\":[{_dragStart.X:0.###},{_dragStart.Y:0.###},{corner.X:0.###},{corner.Y:0.###}]");
        }

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
                // Towards the middle of the piece; for a hole, away from the hole, where the cloth is.
                var inward = piece.InHole(edge.Edge)
                    ? at - piece.Origin - piece.LoopOf(edge.Edge).Loop.Aggregate(Vector2.Zero, (sum, p) => sum + p.Position) / piece.LoopOf(edge.Edge).Loop.Count
                    : piece.Points.Aggregate(Vector2.Zero, (sum, p) => sum + p.Position) / piece.Points.Count + piece.Origin - at;
                inward = inward.LengthSquared() > 1e-8f ? Vector2.Normalize(inward) : Vector2.Zero;
                sb.Append(firstMeasure ? "" : ",").Append(inv, $"{at.X:0.###},{at.Y:0.###},{Outline.EdgeLength(piece, edge.Edge):0.###},{inward.X:0.##},{inward.Y:0.##}");
                firstMeasure = false;
            }
            sb.Append(']');
        }

        // The ruler: both ends and the distance between them.
        if (!_uvMode && Ruler is { } ruler)
            sb.Append(inv, $",\"ruler\":[{ruler.A.X:0.###},{ruler.A.Y:0.###},{ruler.B.X:0.###},{ruler.B.Y:0.###},{Vector2.Distance(ruler.A, ruler.B):0.###}]");

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
