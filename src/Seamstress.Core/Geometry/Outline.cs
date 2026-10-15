using System.Numerics;
using Seamstress.Core.Model;

namespace Seamstress.Core.Geometry;

/// <summary>Geometry helpers for piece outlines (local centimetres).</summary>
public static class Outline
{
    private const int CurveSteps = 24;

    public static Vector2 EdgeStart(PatternPiece piece, int edge) => piece.PointAt(edge).Position;

    public static Vector2 EdgeEnd(PatternPiece piece, int edge) => piece.PointAt(piece.NextPoint(edge)).Position;

    public static bool IsCurved(PatternPiece piece, int edge) => piece.PointAt(edge).Curve.HasValue;

    /// <summary>Point on an edge at Bezier parameter <paramref name="t"/> (not arc-length).</summary>
    public static Vector2 EdgePoint(PatternPiece piece, int edge, float t)
    {
        var a = EdgeStart(piece, edge);
        var b = EdgeEnd(piece, edge);
        var c = piece.PointAt(edge).Curve;
        if (c is null)
            return Vector2.Lerp(a, b, t);
        float u = 1 - t;
        return u * u * a + 2 * u * t * c.Value + t * t * b;
    }

    /// <summary>Polyline approximation of one edge, including both end points.</summary>
    public static List<Vector2> FlattenEdge(PatternPiece piece, int edge)
    {
        var list = new List<Vector2>();
        if (!IsCurved(piece, edge))
        {
            list.Add(EdgeStart(piece, edge));
            list.Add(EdgeEnd(piece, edge));
            return list;
        }
        for (int i = 0; i <= CurveSteps; i++)
            list.Add(EdgePoint(piece, edge, i / (float)CurveSteps));
        return list;
    }

    public static float EdgeLength(PatternPiece piece, int edge)
    {
        var pts = FlattenEdge(piece, edge);
        float len = 0;
        for (int i = 1; i < pts.Count; i++)
            len += Vector2.Distance(pts[i - 1], pts[i]);
        return len;
    }

    /// <summary>Returns <paramref name="segments"/>+1 points spaced evenly by arc length along an edge.</summary>
    public static Vector2[] SampleEdge(PatternPiece piece, int edge, int segments)
    {
        segments = Math.Max(1, segments);
        var result = new Vector2[segments + 1];
        var pts = FlattenEdge(piece, edge);
        result[0] = pts[0];
        result[segments] = pts[^1];
        if (segments == 1)
            return result;

        var cumulative = new float[pts.Count];
        for (int i = 1; i < pts.Count; i++)
            cumulative[i] = cumulative[i - 1] + Vector2.Distance(pts[i - 1], pts[i]);
        float total = cumulative[^1];
        int seg = 1;
        for (int i = 1; i < segments; i++)
        {
            float target = total * i / segments;
            while (seg < pts.Count - 1 && cumulative[seg] < target)
                seg++;
            float span = cumulative[seg] - cumulative[seg - 1];
            float t = span > 1e-9f ? (target - cumulative[seg - 1]) / span : 0;
            result[i] = Vector2.Lerp(pts[seg - 1], pts[seg], t);
        }
        return result;
    }

    /// <summary>The outer outline as a closed polygon (last point not repeated), in local coordinates.</summary>
    public static List<Vector2> Flatten(PatternPiece piece) => FlattenLoop(piece, 0, piece.Points.Count);

    /// <summary>Each hole as a closed polygon, like <see cref="Flatten"/>.</summary>
    public static List<List<Vector2>> FlattenHoles(PatternPiece piece)
        => piece.Loops().Skip(1).Select(l => FlattenLoop(piece, l.Start, l.Loop.Count)).ToList();

    private static List<Vector2> FlattenLoop(PatternPiece piece, int start, int count)
    {
        var list = new List<Vector2>();
        for (int e = start; e < start + count; e++)
        {
            var pts = FlattenEdge(piece, e);
            for (int i = 0; i < pts.Count - 1; i++)
                list.Add(pts[i]);
        }
        return list;
    }

    public static float SignedArea(IReadOnlyList<Vector2> polygon)
    {
        double sum = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            sum += (double)a.X * b.Y - (double)b.X * a.Y;
        }
        return (float)(sum * 0.5);
    }

    /// <summary>Surface area of the piece in square centimetres, without its holes.</summary>
    public static float Area(PatternPiece piece)
        => MathF.Abs(SignedArea(Flatten(piece))) - FlattenHoles(piece).Sum(h => MathF.Abs(SignedArea(h)));

    /// <summary>True when a local point is on the cloth: inside the outline and not in a hole.</summary>
    public static bool Contains(PatternPiece piece, Vector2 local)
        => Contains(Flatten(piece), local) && !FlattenHoles(piece).Any(h => Contains(h, local));

    public static bool Contains(IReadOnlyList<Vector2> polygon, Vector2 p)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    public static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b, out float t)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        t = len2 > 1e-12f ? Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0, 1) : 0;
        return Vector2.Distance(p, a + ab * t);
    }

    public static float DistanceToPolyline(Vector2 p, IReadOnlyList<Vector2> pts, bool closed)
    {
        float best = float.MaxValue;
        int n = closed ? pts.Count : pts.Count - 1;
        for (int i = 0; i < n; i++)
            best = MathF.Min(best, DistanceToSegment(p, pts[i], pts[(i + 1) % pts.Count], out _));
        return best;
    }

    /// <summary>Bounds of the outline in local coordinates.</summary>
    public static (Vector2 Min, Vector2 Max) Bounds(PatternPiece piece)
    {
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var p in Flatten(piece))
        {
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p);
        }
        if (piece.Points.Count == 0)
            return (Vector2.Zero, Vector2.Zero);
        return (min, max);
    }

    /// <summary>
    /// Makes the outline counter-clockwise and every hole clockwise. Only safe before any seam refers to the piece.
    /// </summary>
    public static void EnsureCounterClockwise(PatternPiece piece)
    {
        if (piece.Points.Count >= 3 && SignedArea(Flatten(piece)) < 0)
            piece.Points = Reversed(piece.Points);
        var holes = FlattenHoles(piece);
        for (int h = 0; h < piece.Holes.Count; h++)
        {
            if (piece.Holes[h].Count >= 3 && SignedArea(holes[h]) > 0)
                piece.Holes[h] = Reversed(piece.Holes[h]);
        }
    }

    /// <summary>
    /// The same closed outline walked the other way, starting from the same point. Point k of the result is point
    /// n-k of the original, and the edge leaving it is original edge n-1-k, so each curve control moves with its edge.
    /// </summary>
    public static List<PatternPoint> Reversed(IReadOnlyList<PatternPoint> loop)
    {
        int n = loop.Count;
        var reversed = new List<PatternPoint>(n);
        for (int i = 0; i < n; i++)
        {
            var src = loop[(n - i) % n];
            var edgeOwner = loop[(n - i - 1 + n) % n];
            reversed.Add(new PatternPoint { Position = src.Position, Curve = edgeOwner.Curve });
        }
        return reversed;
    }

    /// <summary>True when two closed polygons cross or touch anywhere along their sides.</summary>
    public static bool Crosses(IReadOnlyList<Vector2> a, IReadOnlyList<Vector2> b)
    {
        for (int i = 0; i < a.Count; i++)
        {
            var p = a[i];
            var q = a[(i + 1) % a.Count];
            var lo = Vector2.Min(p, q);
            var hi = Vector2.Max(p, q);
            for (int j = 0; j < b.Count; j++)
            {
                var r = b[j];
                var s = b[(j + 1) % b.Count];
                if (MathF.Max(r.X, s.X) < lo.X || MathF.Min(r.X, s.X) > hi.X || MathF.Max(r.Y, s.Y) < lo.Y || MathF.Min(r.Y, s.Y) > hi.Y)
                    continue;
                if (SegmentsIntersect(p, q, r, s))
                    return true;
            }
        }
        return false;
    }

    private static bool SegmentsIntersect(Vector2 p, Vector2 q, Vector2 r, Vector2 s)
    {
        static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        float d1 = Cross(r, s, p), d2 = Cross(r, s, q), d3 = Cross(p, q, r), d4 = Cross(p, q, s);
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
            return true;
        // Touching: an end of one segment lies on the other.
        static bool On(Vector2 a, Vector2 b, Vector2 c, float cross)
            => MathF.Abs(cross) <= 1e-6f * MathF.Max(1, Vector2.DistanceSquared(a, b))
               && c.X >= MathF.Min(a.X, b.X) - 1e-5f && c.X <= MathF.Max(a.X, b.X) + 1e-5f
               && c.Y >= MathF.Min(a.Y, b.Y) - 1e-5f && c.Y <= MathF.Max(a.Y, b.Y) + 1e-5f;
        return On(r, s, p, d1) || On(r, s, q, d2) || On(p, q, r, d3) || On(p, q, s, d4);
    }
}
