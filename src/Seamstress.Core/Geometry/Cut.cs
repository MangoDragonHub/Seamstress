using System.Numerics;
using Seamstress.Core.Model;

namespace Seamstress.Core.Geometry;

/// <summary>
/// Cuts a piece along a straight line. The line is intersected with the piece's outline and holes, the boundary is
/// split at the crossings, and the regions on each side become separate pieces. Curves are kept; the cut itself is
/// a straight edge. Only the shapes are worked out here; the workspace applies them.
/// </summary>
public static class Cut
{
    /// <summary>One piece resulting from a cut, in the original piece's local coordinates.</summary>
    public sealed class Shape
    {
        public List<PatternPoint> Outline { get; } = new();
        public List<List<PatternPoint>> Holes { get; } = new();

        /// <summary>
        /// For every edge of the original piece that this shape contains in one piece, its new edge number and
        /// whether the outline now walks it the other way. Edges split by the cut are left out.
        /// </summary>
        public Dictionary<int, (int Edge, bool Flipped)> WholeEdges { get; } = new();
    }

    public sealed class Result
    {
        public List<Shape> Shapes { get; } = new();
    }

    private sealed class Node
    {
        public Vector2 Position;
        public readonly List<int> Out = new();
    }

    private sealed class HalfEdge
    {
        public int From, To, Twin;
        public Vector2 Start, End;
        public Vector2? Curve;
        public int OriginalEdge = -1;
        public float F0, F1;
        public bool IsCut;
        public bool Reversed;
        public double Angle;
    }

    private readonly record struct Segment(Vector2 A, Vector2? C, Vector2 B, int Edge, float F0, float F1);
    private readonly record struct Crossing(float S, Vector2 Point);

    private const float NodeTolerance = 1e-4f;

    /// <summary>
    /// Splits <paramref name="piece"/> along the segment from <paramref name="start"/> to <paramref name="end"/>, in
    /// the piece's local centimetres. Returns null with a reason when the line does not divide the piece.
    /// </summary>
    public static Result? Plan(PatternPiece piece, Vector2 start, Vector2 end, out string? problem)
        => Plan(piece, new[] { start, end }, out problem);

    /// <summary>
    /// Splits <paramref name="piece"/> along a polyline in the piece's local centimetres, as used by a rip. The path
    /// is only cut between its first and last boundary crossing, so a stroke that starts or ends inside the cloth
    /// does not leave a loose slit. Returns null with a reason when the line does not divide the piece.
    /// </summary>
    public static Result? Plan(PatternPiece piece, IReadOnlyList<Vector2> path, out string? problem)
    {
        problem = null;
        var points = new List<Vector2>();
        foreach (var point in path)
        {
            if (points.Count == 0 || Vector2.DistanceSquared(points[^1], point) > 1e-8f)
                points.Add(point);
        }
        if (points.Count < 2)
        {
            problem = Lang.T("Draw the cut line across a piece.");
            return null;
        }

        var outer = Outline.Flatten(piece);
        var holes = Outline.FlattenHoles(piece);
        bool Material(Vector2 p) => Outline.Contains(outer, p) && !holes.Any(h => Outline.Contains(h, p));

        // Split every boundary edge where the path crosses it. Crossings are kept per path segment so the cut
        // edges can follow every turn of a freehand rip.
        var splits = new Dictionary<int, List<float>>();
        var crossingPoints = new List<(float Along, Vector2 Point)>[points.Count - 1];
        for (int i = 1; i < points.Count; i++)
        {
            var a = points[i - 1];
            var direction = points[i] - a;
            float lengthSquared = direction.LengthSquared();
            var found = crossingPoints[i - 1] = new List<(float, Vector2)>();
            if (lengthSquared < 1e-8f)
                continue;
            foreach (var (loop, first) in piece.Loops())
            {
                for (int k = 0; k < loop.Count; k++)
                {
                    var from = loop[k];
                    var to = loop[(k + 1) % loop.Count];
                    foreach (float t in LineCrossings(a, direction, from.Position, from.Curve, to.Position))
                    {
                        var point = Eval(from.Position, from.Curve, to.Position, t);
                        float along = Vector2.Dot(point - a, direction) / lengthSquared;
                        if (along < -1e-4f || along > 1f + 1e-4f)
                            continue;
                        if (t > 1e-4f && t < 1f - 1e-4f)
                        {
                            int edge = first + k;
                            if (!splits.TryGetValue(edge, out var list))
                                splits[edge] = list = new List<float>();
                            list.Add(t);
                        }
                        found.Add((Math.Clamp(along, 0f, 1f), point));
                    }
                }
            }
        }

        var events = new List<(Vector2 Point, bool Crossing)>();
        void AddEvent(Vector2 point, bool crossing)
        {
            if (events.Count > 0 && Vector2.DistanceSquared(events[^1].Point, point) <= NodeTolerance * NodeTolerance)
            {
                if (crossing)
                    events[^1] = (point, true);
                return;
            }
            events.Add((point, crossing));
        }
        for (int i = 1; i < points.Count; i++)
        {
            AddEvent(points[i - 1], false);
            foreach (var crossing in crossingPoints[i - 1].OrderBy(c => c.Along))
                AddEvent(crossing.Point, true);
            AddEvent(points[i], false);
        }
        int firstCrossing = events.FindIndex(e => e.Crossing);
        int lastCrossing = events.FindLastIndex(e => e.Crossing);
        if (firstCrossing < 0 || lastCrossing <= firstCrossing)
        {
            problem = Lang.T("The cut line must cross the piece.");
            return null;
        }

        var segments = new List<Segment>();
        foreach (var (loop, first) in piece.Loops())
        {
            for (int k = 0; k < loop.Count; k++)
            {
                var from = loop[k];
                var to = loop[(k + 1) % loop.Count];
                var stops = new List<float> { 0f };
                if (splits.TryGetValue(first + k, out var split))
                {
                    split.Sort();
                    foreach (float t in split)
                    {
                        if (t - stops[^1] > 1e-5f && 1f - t > 1e-5f)
                            stops.Add(t);
                    }
                }
                if (1f - stops[^1] > 1e-5f)
                    stops.Add(1f);
                for (int j = 1; j < stops.Count; j++)
                {
                    float t0 = stops[j - 1], t1 = stops[j];
                    var part = SubCurve(from.Position, from.Curve, to.Position, t0, t1);
                    if ((part.B - part.A).LengthSquared() < 1e-12f && part.C == null)
                        continue;
                    segments.Add(new Segment(part.A, part.C, part.B, first + k, t0, t1));
                }
            }
        }

        var nodes = new List<Node>();
        var edges = new List<HalfEdge>();

        int GetNode(Vector2 at)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                if (Vector2.DistanceSquared(nodes[i].Position, at) <= NodeTolerance * NodeTolerance)
                    return i;
            }
            nodes.Add(new Node { Position = at });
            return nodes.Count - 1;
        }

        (int Forward, int Back) AddPair(int from, int to, Vector2? curve, int originalEdge, float f0, float f1, bool cut)
        {
            int forward = edges.Count;
            edges.Add(new HalfEdge
            {
                From = from, To = to, Start = nodes[from].Position, End = nodes[to].Position,
                Curve = curve, OriginalEdge = originalEdge, F0 = f0, F1 = f1, IsCut = cut,
                Angle = AngleOf(OutDirection(nodes[from].Position, curve, nodes[to].Position)),
            });
            int back = edges.Count;
            edges.Add(new HalfEdge
            {
                From = to, To = from, Start = nodes[to].Position, End = nodes[from].Position,
                Curve = curve, OriginalEdge = originalEdge, F0 = f0, F1 = f1, IsCut = cut, Reversed = true,
                Angle = AngleOf(OutDirection(nodes[to].Position, curve, nodes[from].Position)),
            });
            edges[forward].Twin = back;
            edges[back].Twin = forward;
            nodes[from].Out.Add(forward);
            nodes[to].Out.Add(back);
            return (forward, back);
        }

        foreach (var segment in segments)
        {
            int from = GetNode(segment.A), to = GetNode(segment.B);
            if (from != to)
                AddPair(from, to, segment.C, segment.Edge, segment.F0, segment.F1, cut: false);
        }

        // The parts of the path that lie on the cloth, from its first boundary crossing to its last, are new edges.
        bool hasCut = false;
        for (int i = firstCrossing; i < lastCrossing; i++)
        {
            var a = events[i].Point;
            var b = events[i + 1].Point;
            if (Vector2.DistanceSquared(a, b) < 1e-12f || !Material((a + b) / 2))
                continue;
            int from = GetNode(a), to = GetNode(b);
            if (from == to)
                continue;
            AddPair(from, to, null, -1, 0, 1, cut: true);
            hasCut = true;
        }
        if (!hasCut)
        {
            problem = Lang.T("The cut line must cross the cloth.");
            return null;
        }

        // Face traversal: at each vertex the next half-edge is the one just clockwise from the reverse of the
        // incoming edge, which keeps the face on the left.
        var sorted = new List<int>[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            sorted[i] = nodes[i].Out.OrderBy(e => edges[e].Angle).ThenBy(e => e).ToList();
            if (sorted[i].Count == 0)
                continue;
        }
        var next = new int[edges.Count];
        for (int e = 0; e < edges.Count; e++)
        {
            var list = sorted[edges[e].To];
            int twin = list.IndexOf(edges[e].Twin);
            next[e] = list[(twin - 1 + list.Count) % list.Count];
        }

        var used = new bool[edges.Count];
        var cycles = new List<List<int>>();
        for (int e = 0; e < edges.Count; e++)
        {
            if (used[e])
                continue;
            var cycle = new List<int>();
            int current = e;
            while (!used[current])
            {
                used[current] = true;
                cycle.Add(current);
                current = next[current];
            }
            if (cycle.Count >= 3)
                cycles.Add(cycle);
        }

        var positive = new List<(List<int> Cycle, float Area)>();
        var negative = new List<(List<int> Cycle, float Area)>();
        foreach (var cycle in cycles)
        {
            float area = CycleArea(cycle, edges);
            if (area > 1e-4f && TouchesMaterial(cycle, edges, Material))
                positive.Add((cycle, area));
            else if (area < -1e-4f && TouchesMaterial(cycle, edges, Material))
                negative.Add((cycle, area));
        }

        if (positive.Count < 2)
        {
            problem = Lang.T("The cut does not separate the piece into separate pieces.");
            return null;
        }

        positive.Sort((a, b) => b.Area.CompareTo(a.Area));
        var assigned = new Dictionary<int, List<List<int>>>();
        foreach (var (hole, _) in negative)
        {
            var point = edges[hole[0]].Start;
            int best = -1;
            float bestArea = float.MaxValue;
            for (int i = 0; i < positive.Count; i++)
            {
                var polygon = positive[i].Cycle.Select(e => edges[e].Start).ToList();
                if (positive[i].Area < bestArea && Outline.Contains(polygon, point))
                {
                    best = i;
                    bestArea = positive[i].Area;
                }
            }
            if (best < 0)
                continue;
            if (!assigned.TryGetValue(best, out var list))
                assigned[best] = list = new List<List<int>>();
            list.Add(hole);
        }

        var result = new Result();
        foreach (var (cycle, _) in positive)
        {
            var shape = new Shape();
            AddLoop(shape.Outline, cycle, edges);
            int outlineCount = shape.Outline.Count;
            if (outlineCount < 3)
                continue;
            MapWholeEdges(shape, cycle, 0, edges);
            int edgeNumber = outlineCount;
            // Holes are added in a stable order, largest first.
            if (assigned.Count > 0)
            {
                int index = positive.FindIndex(p => ReferenceEquals(p.Cycle, cycle));
                if (index >= 0 && assigned.TryGetValue(index, out var holeCycles))
                {
                    holeCycles.Sort((a, b) => MathF.Abs(CycleArea(b, edges)).CompareTo(MathF.Abs(CycleArea(a, edges))));
                    foreach (var hole in holeCycles)
                    {
                        var loop = new List<PatternPoint>();
                        AddLoop(loop, hole, edges);
                        if (loop.Count < 3)
                            continue;
                        shape.Holes.Add(loop);
                        MapWholeEdges(shape, hole, edgeNumber, edges);
                        edgeNumber += loop.Count;
                    }
                }
            }
            if (!Valid(shape))
            {
                problem = Lang.T("The cut produced an invalid piece.");
                return null;
            }
            result.Shapes.Add(shape);
        }

        if (result.Shapes.Count < 2)
        {
            problem = Lang.T("The cut does not separate the piece into separate pieces.");
            return null;
        }

        float before = Outline.Area(piece);
        float after = result.Shapes.Sum(s => Outline.Area(new PatternPiece { Points = s.Outline, Holes = s.Holes }));
        if (MathF.Abs(after - before) > MathF.Max(0.5f, before * 0.01f))
        {
            problem = Lang.T("The cut does not separate the piece into separate pieces.");
            return null;
        }
        return result;
    }

    private static void AddLoop(List<PatternPoint> to, List<int> cycle, List<HalfEdge> edges)
    {
        foreach (int e in cycle)
        {
            var edge = edges[e];
            to.Add(new PatternPoint { Position = edge.Start, Curve = edge.Curve });
        }
    }

    private static void MapWholeEdges(Shape shape, List<int> cycle, int firstEdge, List<HalfEdge> edges)
    {
        for (int i = 0; i < cycle.Count; i++)
        {
            var edge = edges[cycle[i]];
            if (edge.IsCut || edge.F0 > 1e-4f || edge.F1 < 1f - 1e-4f)
                continue;
            if (edge.OriginalEdge < 0)
                continue;
            if (!shape.WholeEdges.ContainsKey(edge.OriginalEdge))
                shape.WholeEdges[edge.OriginalEdge] = (firstEdge + i, edge.Reversed);
        }
    }

    private static bool Valid(Shape shape)
    {
        var outer = Outline.Flatten(new PatternPiece { Points = shape.Outline });
        if (shape.Outline.Count < 3 || !Outline.IsSimple(outer) || Outline.SignedArea(outer) <= 1e-4f)
            return false;
        var holes = Outline.FlattenHoles(new PatternPiece { Points = shape.Outline, Holes = shape.Holes });
        for (int i = 0; i < shape.Holes.Count; i++)
        {
            var hole = holes[i];
            if (shape.Holes[i].Count < 3 || !Outline.IsSimple(hole) || Outline.SignedArea(hole) >= -1e-4f)
                return false;
            if (!Outline.Contains(outer, hole[0]) || Outline.Crosses(hole, outer))
                return false;
            for (int j = 0; j < i; j++)
            {
                if (Outline.Crosses(hole, holes[j]) || Outline.Contains(hole, holes[j][0]) || Outline.Contains(holes[j], hole[0]))
                    return false;
            }
        }
        return true;
    }

    private static float CycleArea(List<int> cycle, List<HalfEdge> edges)
    {
        double sum = 0;
        foreach (int e in cycle)
        {
            var a = edges[e].Start;
            var b = edges[e].End;
            sum += (double)a.X * b.Y - (double)b.X * a.Y;
        }
        return (float)(sum * 0.5);
    }

    private static bool TouchesMaterial(List<int> cycle, List<HalfEdge> edges, Func<Vector2, bool> material)
    {
        foreach (int e in cycle)
        {
            var edge = edges[e];
            var tangent = Tangent(edge.Start, edge.Curve, edge.End, 0.5f);
            if (tangent.LengthSquared() < 1e-12f)
                continue;
            var normal = new Vector2(-tangent.Y, tangent.X) / tangent.Length();
            var at = Eval(edge.Start, edge.Curve, edge.End, 0.5f);
            float reach = MathF.Min(0.01f, 0.1f * Vector2.Distance(edge.Start, edge.End));
            foreach (float step in new[] { reach, reach * 0.1f })
            {
                if (material(at + normal * step))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Where the infinite line through <paramref name="p"/> along <paramref name="d"/> crosses an edge.</summary>
    private static List<float> LineCrossings(Vector2 p, Vector2 d, Vector2 a, Vector2? c, Vector2 b)
    {
        var roots = new List<float>();
        float k0 = Cross(d, a - p);
        float k1, k2;
        if (c is { } control)
        {
            k1 = 2 * Cross(d, control - a);
            k2 = Cross(d, a - 2 * control + b);
        }
        else
        {
            k1 = Cross(d, b - a);
            k2 = 0;
        }
        float scale = MathF.Max(MathF.Abs(k1), MathF.Max(MathF.Abs(k2), 1e-9f));
        if (MathF.Abs(k2) < 1e-7f * scale)
        {
            if (MathF.Abs(k1) > 1e-12f)
                roots.Add(-k0 / k1);
        }
        else
        {
            float disc = k1 * k1 - 4 * k2 * k0;
            if (disc >= 0)
            {
                float sq = MathF.Sqrt(disc);
                roots.Add((-k1 + sq) / (2 * k2));
                roots.Add((-k1 - sq) / (2 * k2));
            }
        }
        return roots.Where(t => t >= -1e-4f && t <= 1f + 1e-4f)
            .Select(t => Math.Clamp(t, 0f, 1f))
            .OrderBy(t => t)
            .ToList();
    }

    private static (Vector2 A, Vector2? C, Vector2 B) SubCurve(Vector2 a, Vector2? c, Vector2 b, float t0, float t1)
    {
        if (c is null)
            return (Vector2.Lerp(a, b, t0), null, Vector2.Lerp(a, b, t1));
        var first = SplitAt(a, c.Value, b, t1);
        if (t0 <= 1e-7f)
            return (a, first.FirstCurve, first.Middle);
        var left = SplitAt(a, first.FirstCurve!.Value, first.Middle, t0 / t1);
        return (left.Middle, left.SecondCurve, first.Middle);
    }

    private static (Vector2? FirstCurve, Vector2 Middle, Vector2? SecondCurve) SplitAt(Vector2 a, Vector2 c, Vector2 b, float t)
    {
        var c1 = Vector2.Lerp(a, c, t);
        var c2 = Vector2.Lerp(c, b, t);
        var middle = Vector2.Lerp(c1, c2, t);
        return (c1, middle, c2);
    }

    private static Vector2 Eval(Vector2 a, Vector2? c, Vector2 b, float t)
    {
        if (c is not { } control)
            return Vector2.Lerp(a, b, t);
        float u = 1 - t;
        return u * u * a + 2 * u * t * control + t * t * b;
    }

    private static Vector2 Tangent(Vector2 a, Vector2? c, Vector2 b, float t)
    {
        if (c is not { } control)
            return b - a;
        return 2 * (1 - t) * (control - a) + 2 * t * (b - control);
    }

    private static Vector2 OutDirection(Vector2 start, Vector2? curve, Vector2 end)
        => curve is { } control && (control - start).LengthSquared() > 1e-12f ? control - start : end - start;

    private static double AngleOf(Vector2 direction)
        => direction.LengthSquared() > 1e-12f ? Math.Atan2(direction.Y, direction.X) : 0;

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
}
