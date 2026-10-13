using System.Numerics;
using Seamstress.Core.Model;

namespace Seamstress.Core.Geometry;

/// <summary>Geometry helpers for piece outlines (local centimetres).</summary>
public static class Outline
{
    private const int CurveSteps = 24;

    public static Vector2 EdgeStart(PatternPiece piece, int edge) => piece.Points[edge].Position;

    public static Vector2 EdgeEnd(PatternPiece piece, int edge)
        => piece.Points[(edge + 1) % piece.Points.Count].Position;

    public static bool IsCurved(PatternPiece piece, int edge) => piece.Points[edge].Curve.HasValue;

    /// <summary>Point on an edge at Bezier parameter <paramref name="t"/> (not arc-length).</summary>
    public static Vector2 EdgePoint(PatternPiece piece, int edge, float t)
    {
        var a = EdgeStart(piece, edge);
        var b = EdgeEnd(piece, edge);
        var c = piece.Points[edge].Curve;
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

    /// <summary>Whole outline as a closed polygon (last point not repeated), in local coordinates.</summary>
    public static List<Vector2> Flatten(PatternPiece piece)
    {
        var list = new List<Vector2>();
        for (int e = 0; e < piece.Points.Count; e++)
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

    /// <summary>Surface area of the piece in square centimetres.</summary>
    public static float Area(PatternPiece piece) => MathF.Abs(SignedArea(Flatten(piece)));

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

    /// <summary>Makes the outline counter-clockwise. Only safe before any seam refers to the piece.</summary>
    public static void EnsureCounterClockwise(PatternPiece piece)
    {
        if (piece.Points.Count < 3 || SignedArea(Flatten(piece)) >= 0)
            return;
        // Reversing the point order moves each curve control to the edge that now starts at the following point.
        var old = piece.Points;
        int n = old.Count;
        var reversed = new List<PatternPoint>(n);
        for (int i = 0; i < n; i++)
        {
            var src = old[(n - i) % n];
            var edgeOwner = old[(n - i - 1 + n) % n];
            reversed.Add(new PatternPoint { Position = src.Position, Curve = edgeOwner.Curve });
        }
        piece.Points = reversed;
    }
}
