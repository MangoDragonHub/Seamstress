using System.Numerics;
using Seamstress.Core.Model;

namespace Seamstress.Core.Geometry;

/// <summary>
/// Inset Band: cuts a strip of a chosen width off a piece along some of its edges, so a waistband, collar band or
/// facing is cut from the very piece it is sewn to. Consecutive chosen edges of an outline or hole make one band; the
/// cut line runs parallel to them, inside the cloth, and meets the edges either side of them, which are split there.
/// A whole outline or hole chosen gives a ring. This class only works out the shapes; the workspace applies them.
/// </summary>
public static class Inset
{
    /// <summary>
    /// Part of an original edge after the cut: the edge that now starts at <see cref="Start"/>, in the base piece when
    /// <see cref="Owner"/> is -1 and otherwise in that band, covering fractions <see cref="F0"/> to <see cref="F1"/> of
    /// the original edge's length.
    /// </summary>
    public readonly record struct Part(int Owner, PatternPoint Start, float F0, float F1);

    /// <summary>A band to be made into a new piece, in the base piece's local coordinates.</summary>
    public sealed class Band
    {
        public List<PatternPoint> Outline { get; } = new();
        public List<List<PatternPoint>> Holes { get; } = new();
    }

    /// <summary>What cutting the bands does to one piece.</summary>
    public sealed class Result
    {
        /// <summary>The base piece's new outline.</summary>
        public List<PatternPoint> Points { get; } = new();

        /// <summary>The base piece's new holes.</summary>
        public List<List<PatternPoint>> Holes { get; } = new();

        public List<Band> Bands { get; } = new();

        /// <summary>For every edge of the piece before the cut, the edges it became, in order along it.</summary>
        public Dictionary<int, List<Part>> Edges { get; } = new();

        /// <summary>
        /// The edges along the cut, to be sewn: the base piece's edge starting at <c>Base</c> and the edge of band
        /// <c>Band</c> starting at <c>BandStart</c> are the same line walked in opposite directions.
        /// </summary>
        public List<(PatternPoint Base, int Band, PatternPoint BandStart)> Cuts { get; } = new();

        /// <summary>The cut lines as polylines in local coordinates, for drawing.</summary>
        public List<List<Vector2>> CutLines { get; } = new();
    }

    /// <summary>An edge: straight from A to B, or a quadratic Bezier through control C.</summary>
    private struct Seg
    {
        public Vector2 A;
        public Vector2? C;
        public Vector2 B;

        public Seg(Vector2 a, Vector2? c, Vector2 b)
        {
            A = a;
            C = c;
            B = b;
        }
    }

    /// <summary>The cut line may stray this far (cm) from a true parallel before a curve is split to follow it closer.</summary>
    private const float Tolerance = 0.05f;

    /// <summary>
    /// Works out the bands cut off <paramref name="piece"/> along <paramref name="edges"/> at <paramref name="width"/>
    /// centimetres. Returns null with a reason when no shape can be made. When shapes can be made but do not work
    /// (the cut crosses another edge, say), returns them for drawing and gives the reason as well: only a result
    /// without a problem may be applied.
    /// </summary>
    public static Result? Plan(PatternPiece piece, IReadOnlyCollection<int> edges, float width, out string? problem)
    {
        problem = null;
        if (!(width > 0) || float.IsInfinity(width))
        {
            problem = Lang.T("Set a width greater than zero.");
            return null;
        }
        if (piece.Points.Count < 3)
        {
            problem = Lang.T("A piece needs at least three points to cut a band from.");
            return null;
        }
        var chosen = edges.Where(e => e >= 0 && e < piece.EdgeCount).ToHashSet();
        var result = new Result();
        int loopIndex = 0;
        foreach (var (loop, start) in piece.Loops())
        {
            var selected = Enumerable.Range(0, loop.Count).Select(i => chosen.Contains(start + i)).ToArray();
            List<PatternPoint>? replaced;
            if (!selected.Any(s => s))
            {
                replaced = loop;
                for (int i = 0; i < loop.Count; i++)
                    result.Edges[start + i] = new List<Part> { new(-1, loop[i], 0, 1) };
            }
            else if (selected.All(s => s))
            {
                replaced = Ring(loop, start, loopIndex > 0, width, result, out problem);
            }
            else
            {
                replaced = Runs(loop, start, selected, width, result, out problem);
            }
            if (replaced == null)
                return null;
            if (loopIndex == 0)
                result.Points.AddRange(replaced);
            else
                result.Holes.Add(replaced);
            loopIndex++;
        }
        problem = Check(piece, result);
        return result;
    }

    /// <summary>A whole outline or hole chosen: the band is a ring and the piece keeps what is inside the cut.</summary>
    private static List<PatternPoint>? Ring(List<PatternPoint> loop, int start, bool isHole, float width, Result result, out string? problem)
    {
        problem = null;
        int n = loop.Count;
        var cut = new List<Seg>();
        for (int i = 0; i < n; i++)
        {
            var segs = new List<Seg>();
            if (!Offset(EdgeSeg(loop, i), width, 0, segs))
            {
                problem = Lang.T("Too wide for this curve.");
                return null;
            }
            if (cut.Count > 0 && !Join(cut, segs, loop[i].Position, width))
            {
                problem = Lang.T("Too wide: the cut line would cross another edge or a hole.");
                return null;
            }
            cut.AddRange(segs);
        }
        if (!Join(cut, cut, loop[0].Position, width))
        {
            problem = Lang.T("Too wide: the cut line would cross another edge or a hole.");
            return null;
        }
        Tidy(cut, closed: true);
        int k = cut.Count;
        if (k < 3)
        {
            problem = Lang.T("Too wide: the cut line would cross another edge or a hole.");
            return null;
        }

        int bandIndex = result.Bands.Count;
        var band = new Band();
        result.Bands.Add(band);
        var copies = loop.Select(p => new PatternPoint { Position = p.Position, Curve = p.Curve }).ToList();
        for (int i = 0; i < n; i++)
            result.Edges[start + i] = new List<Part> { new(bandIndex, copies[i], 0, 1) };

        // The band's side of the cut walks it backwards: c0, c(k-1), ..., c1, each with the control of the edge
        // that ends at it in the forward direction.
        var backwards = new PatternPoint[k];
        var reversedLoop = new List<PatternPoint>(k);
        for (int r = 0; r < k; r++)
        {
            int j = (k - r) % k;
            var point = new PatternPoint { Position = cut[j].A, Curve = cut[(j - 1 + k) % k].C };
            backwards[j] = point;
            reversedLoop.Add(point);
        }
        if (isHole)
        {
            band.Outline.AddRange(reversedLoop);
            band.Holes.Add(copies);
        }
        else
        {
            band.Outline.AddRange(copies);
            band.Holes.Add(reversedLoop);
        }

        var inner = new List<PatternPoint>(k);
        for (int j = 0; j < k; j++)
        {
            var point = new PatternPoint { Position = cut[j].A, Curve = cut[j].C };
            inner.Add(point);
            result.Cuts.Add((point, bandIndex, backwards[(j + 1) % k]));
        }
        var line = Polyline(cut);
        line.Add(line[0]);
        result.CutLines.Add(line);
        return inner;
    }

    private sealed class Run
    {
        public int First, Count, Previous, Next, Band;
        public List<Seg> Cut = new();
        public float TailT = 1, HeadT;          // where the edges either side are split (1 and 0: not split)
        public PatternPoint? TailPoint, HeadPoint; // the band's points that start those split-off parts
        public PatternPoint[] BandCut = Array.Empty<PatternPoint>();
    }

    /// <summary>Some edges of an outline or hole chosen: one band for each run of consecutive edges.</summary>
    private static List<PatternPoint>? Runs(List<PatternPoint> loop, int start, bool[] selected, float width, Result result, out string? problem)
    {
        problem = null;
        int n = loop.Count;
        var runs = new List<Run>();
        for (int i = 0; i < n; i++)
        {
            if (!selected[i] || selected[(i - 1 + n) % n])
                continue;
            int count = 1;
            while (selected[(i + count) % n])
                count++;
            runs.Add(new Run { First = i, Count = count, Previous = (i - 1 + n) % n, Next = (i + count) % n });
        }

        foreach (var run in runs)
        {
            for (int j = 0; j < run.Count; j++)
            {
                int edge = (run.First + j) % n;
                var segs = new List<Seg>();
                if (!Offset(EdgeSeg(loop, edge), width, 0, segs))
                {
                    problem = Lang.T("Too wide for this curve.");
                    return null;
                }
                if (run.Cut.Count > 0 && !Join(run.Cut, segs, loop[edge].Position, width))
                {
                    problem = Lang.T("Too wide: the cut line would cross another edge or a hole.");
                    return null;
                }
                run.Cut.AddRange(segs);
            }
            if (!CutEnds(loop, run, width, out problem))
                return null;
            Tidy(run.Cut, closed: false);
        }

        // An edge between two runs is split twice; the two cuts must not pass each other on it.
        foreach (var run in runs)
        {
            var before = runs.FirstOrDefault(r => r.Next == run.Previous);
            if (before != null && before.HeadT >= run.TailT - 1e-3f)
            {
                problem = Lang.T("Too wide: the cut line would cross another edge or a hole.");
                return null;
            }
        }

        // The bands.
        foreach (var run in runs)
        {
            run.Band = result.Bands.Count;
            var band = new Band();
            result.Bands.Add(band);
            var cut = run.Cut;
            int k = cut.Count;
            run.BandCut = new PatternPoint[k + 1];
            for (int j = 0; j < run.Count; j++)
            {
                int edge = (run.First + j) % n;
                var copy = new PatternPoint { Position = loop[edge].Position, Curve = loop[edge].Curve };
                band.Outline.Add(copy);
                result.Edges[start + edge] = new List<Part> { new(run.Band, copy, 0, 1) };
                if (j == 0)
                    run.BandCut[0] = copy;
            }
            var nextSeg = EdgeSeg(loop, run.Next);
            var end = new PatternPoint { Position = nextSeg.A };
            band.Outline.Add(end);
            if (run.HeadT > 0)
            {
                end.Curve = Split(nextSeg, run.HeadT).First.C;
                run.HeadPoint = end;
                var b = new PatternPoint { Position = cut[k - 1].B, Curve = cut[k - 1].C };
                band.Outline.Add(b);
                run.BandCut[k] = b;
            }
            else
            {
                end.Curve = cut[k - 1].C;
                run.BandCut[k] = end;
            }
            for (int j = k - 1; j >= 1; j--)
            {
                var point = new PatternPoint { Position = cut[j].A, Curve = cut[j - 1].C };
                band.Outline.Add(point);
                run.BandCut[j] = point;
            }
            if (run.TailT < 1)
            {
                var tail = new PatternPoint { Position = cut[0].A, Curve = Split(EdgeSeg(loop, run.Previous), run.TailT).Second.C };
                band.Outline.Add(tail);
                run.TailPoint = tail;
            }
            result.CutLines.Add(Polyline(cut));
        }

        // What is left of the outline or hole: walking round it, each edge that was not chosen (shortened where a cut
        // meets it) and, after an edge that a run follows, that run's cut line.
        var inner = new List<PatternPoint>();
        int first = Array.FindIndex(selected, s => !s);
        for (int step = 0; step < n; step++)
        {
            int edge = (first + step) % n;
            if (selected[edge])
                continue;
            var seg = EdgeSeg(loop, edge);
            var head = runs.FirstOrDefault(r => r.Next == edge);
            var tail = runs.FirstOrDefault(r => r.Previous == edge);
            float headT = head?.HeadT ?? 0, tailT = tail?.TailT ?? 1;
            var middle = Middle(seg, headT, tailT);
            var point = new PatternPoint { Position = middle.A, Curve = middle.C };
            inner.Add(point);
            float headF = headT > 0 ? ArcOfT(seg, headT) : 0, tailF = tailT < 1 ? ArcOfT(seg, tailT) : 1;
            var parts = new List<Part>();
            if (head != null && head.HeadPoint != null)
                parts.Add(new Part(head.Band, head.HeadPoint, 0, headF));
            parts.Add(new Part(-1, point, headF, tailF));
            if (tail != null && tail.TailPoint != null)
                parts.Add(new Part(tail.Band, tail.TailPoint, tailF, 1));
            result.Edges[start + edge] = parts;
            if (tail != null)
            {
                for (int j = 0; j < tail.Cut.Count; j++)
                {
                    var c = new PatternPoint { Position = tail.Cut[j].A, Curve = tail.Cut[j].C };
                    inner.Add(c);
                    result.Cuts.Add((c, tail.Band, tail.BandCut[j + 1]));
                }
            }
        }
        return inner;
    }

    /// <summary>
    /// Brings each end of a run's cut line to the edge beside it. Where the cut already crosses that edge it is cut
    /// short there; otherwise it carries on in a straight line along its own direction until it meets it. Where that
    /// edge carries straight on (or nearly), the cut ends square instead, with a short edge across to the corner.
    /// </summary>
    private static bool CutEnds(List<PatternPoint> loop, Run run, float width, out string? problem)
    {
        problem = null;
        string shortEdge = Lang.T("Too wide: the cut would run past the end of the edge next to the band.");
        var cut = run.Cut;
        const float margin = 1e-3f;

        var previous = EdgeSeg(loop, run.Previous);
        var t0 = StartTangent(cut[0]);
        if (Parallel(t0, EndTangent(previous)))
        {
            cut.Insert(0, new Seg(loop[run.First].Position, null, cut[0].A));
        }
        else
        {
            bool trimmed = false;
            for (int j = 0; j < cut.Count && !trimmed; j++)
            {
                var crossings = Crossings(cut[j], previous);
                if (crossings.Count == 0)
                    continue;
                var (ta, tb) = crossings.MinBy(c => c.Ta);
                if (tb <= margin || tb >= 1 - margin)
                {
                    problem = shortEdge;
                    return false;
                }
                var rest = Split(cut[j], ta).Second;
                rest.A = Eval(previous, tb);
                cut.RemoveRange(0, j);
                cut[0] = rest;
                run.TailT = tb;
                trimmed = true;
            }
            if (!trimmed)
            {
                var first = cut[0];
                if (LineCrossing(previous, first.A, t0, nearEnd: true) is not { } u || Vector2.Dot(Eval(previous, u) - first.A, t0) >= 0)
                {
                    problem = shortEdge;
                    return false;
                }
                var at = Eval(previous, u);
                if (first.C == null)
                    cut[0] = first with { A = at };
                else
                    cut.Insert(0, new Seg(at, null, first.A));
                run.TailT = u;
            }
        }

        var next = EdgeSeg(loop, run.Next);
        var t1 = EndTangent(cut[^1]);
        if (Parallel(t1, StartTangent(next)))
        {
            cut.Add(new Seg(cut[^1].B, null, loop[run.Next].Position));
        }
        else
        {
            bool trimmed = false;
            for (int j = cut.Count - 1; j >= 0 && !trimmed; j--)
            {
                var crossings = Crossings(cut[j], next);
                if (crossings.Count == 0)
                    continue;
                var (ta, tb) = crossings.MaxBy(c => c.Ta);
                if (tb <= margin || tb >= 1 - margin)
                {
                    problem = shortEdge;
                    return false;
                }
                var kept = Split(cut[j], ta).First;
                kept.B = Eval(next, tb);
                cut.RemoveRange(j + 1, cut.Count - j - 1);
                cut[j] = kept;
                run.HeadT = tb;
                trimmed = true;
            }
            if (!trimmed)
            {
                var last = cut[^1];
                if (LineCrossing(next, last.B, t1, nearEnd: false) is not { } u || Vector2.Dot(Eval(next, u) - last.B, t1) <= 0)
                {
                    problem = shortEdge;
                    return false;
                }
                var at = Eval(next, u);
                if (last.C == null)
                    cut[^1] = last with { B = at };
                else
                    cut.Add(new Seg(last.B, null, at));
                run.HeadT = u;
            }
        }
        return cut.Count > 0;
    }

    /// <summary>Where two edges cross, as pairs of their curve parameters.</summary>
    private static List<(float Ta, float Tb)> Crossings(Seg a, Seg b)
    {
        var pa = Flatten(a, 64);
        var pb = Flatten(b, 64);
        var found = new List<(float, float)>();
        for (int i = 1; i < pa.Count; i++)
        {
            var lo = Vector2.Min(pa[i - 1], pa[i]);
            var hi = Vector2.Max(pa[i - 1], pa[i]);
            for (int j = 1; j < pb.Count; j++)
            {
                if (MathF.Max(pb[j - 1].X, pb[j].X) < lo.X || MathF.Min(pb[j - 1].X, pb[j].X) > hi.X
                    || MathF.Max(pb[j - 1].Y, pb[j].Y) < lo.Y || MathF.Min(pb[j - 1].Y, pb[j].Y) > hi.Y)
                    continue;
                var d1 = pa[i] - pa[i - 1];
                var d2 = pb[j] - pb[j - 1];
                float den = Cross(d1, d2);
                if (MathF.Abs(den) < 1e-12f)
                    continue;
                var r = pb[j - 1] - pa[i - 1];
                float s = Cross(r, d2) / den, u = Cross(r, d1) / den;
                if (s >= 0 && s <= 1 && u >= 0 && u <= 1)
                    found.Add(((i - 1 + s) / (pa.Count - 1), (j - 1 + u) / (pb.Count - 1)));
            }
        }
        return found;
    }

    /// <summary>Within 15° of each other (either way round).</summary>
    private static bool Parallel(Vector2 a, Vector2 b)
    {
        float la = a.Length(), lb = b.Length();
        if (la < 1e-9f || lb < 1e-9f)
            return true;
        return MathF.Abs(Cross(a, b)) / (la * lb) < 0.26f && Vector2.Dot(a, b) > 0;
    }

    /// <summary>
    /// Where the line through <paramref name="p"/> along <paramref name="d"/> crosses an edge, as the edge's curve
    /// parameter, strictly between its ends; the crossing nearest its end (or start) when there are two.
    /// </summary>
    private static float? LineCrossing(Seg seg, Vector2 p, Vector2 d, bool nearEnd)
    {
        // Points of the edge: A + 2u(C - A) + u²(A - 2C + B), or A + u(B - A) for a straight edge.
        float k0 = Cross(d, seg.A - p);
        float k1, k2;
        if (seg.C is { } c)
        {
            k1 = 2 * Cross(d, c - seg.A);
            k2 = Cross(d, seg.A - 2 * c + seg.B);
        }
        else
        {
            k1 = Cross(d, seg.B - seg.A);
            k2 = 0;
        }
        var roots = new List<float>();
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
        const float margin = 1e-3f;
        var inside = roots.Where(u => u > margin && u < 1 - margin).ToList();
        if (inside.Count == 0)
            return null;
        return nearEnd ? inside.Max() : inside.Min();
    }

    /// <summary>
    /// Offsets an edge by <paramref name="width"/> to its left (into the cloth). A curve is offset by moving both legs
    /// of its control polygon and meeting them again, split in two while that strays from a true parallel. False when
    /// the curve bends too tightly for the width and the offset would fold over.
    /// </summary>
    private static bool Offset(Seg seg, float width, int depth, List<Seg> output)
    {
        if (IsStraight(seg))
        {
            var n = Left(seg.B - seg.A);
            output.Add(new Seg(seg.A + n * width, null, seg.B + n * width));
            return true;
        }
        var c = seg.C!.Value;
        var d0 = c - seg.A;
        var d1 = seg.B - c;
        var a = seg.A + Left(d0) * width;
        var b = seg.B + Left(d1) * width;
        var control = Intersect(a, d0, b, d1) ?? (a + b) / 2;
        bool folded = Vector2.Dot(control - a, d0) <= 0 || Vector2.Dot(b - control, d1) <= 0;
        var candidate = new Seg(a, control, b);
        if (!folded && Error(seg, candidate, width) <= Tolerance)
        {
            output.Add(candidate);
            return true;
        }
        if (depth >= 6)
        {
            if (folded)
                return false;
            output.Add(candidate);
            return true;
        }
        var (first, second) = Split(seg, 0.5f);
        return Offset(first, width, depth + 1, output) && Offset(second, width, depth + 1, output);
    }

    /// <summary>How far an offset curve strays from lying <paramref name="width"/> away from the original.</summary>
    private static float Error(Seg original, Seg offset, float width)
    {
        var line = Flatten(original, 64);
        float worst = 0;
        for (int i = 1; i < 16; i++)
        {
            float d = Outline.DistanceToPolyline(Eval(offset, i / 16f), line, false);
            worst = MathF.Max(worst, MathF.Abs(d - width));
        }
        return worst;
    }

    /// <summary>
    /// Joins the end of the last segment of <paramref name="before"/> to the start of the first of
    /// <paramref name="after"/> (the same list for a closed ring). Where the outline turns into the cloth the two
    /// overlap and are cut short where they cross; where it turns away they are carried on in straight lines until
    /// they meet, or, at a very sharp corner, joined straight across. False when the cut folds back on itself.
    /// </summary>
    private static bool Join(List<Seg> before, List<Seg> after, Vector2 corner, float width)
    {
        var p = before[^1];
        var q = after[0];
        if (Vector2.Distance(p.B, q.A) < 0.01f)
        {
            // Edges that meet smoothly (or nearly): their offsets meet too, give or take a tenth of a millimetre.
            var meet = (p.B + q.A) / 2;
            before[^1] = p with { B = meet };
            after[0] = q with { A = meet };
            return true;
        }
        var tEnd = EndTangent(p);
        var tStart = StartTangent(q);
        if (Cross(tEnd, tStart) > 0)
        {
            var crossings = Crossings(p, q);
            if (crossings.Count == 0)
                return false;
            var (ta, tb) = crossings.MaxBy(c => c.Ta);
            var at = Eval(p, ta);
            var kept = Split(p, ta).First;
            var rest = Split(q, tb).Second;
            kept.B = at;
            rest.A = at;
            before[^1] = kept;
            after[0] = rest;
            return true;
        }
        var x = Intersect(p.B, tEnd, q.A, tStart);
        if (x == null || Vector2.Distance(x.Value, corner) > 4 * width
            || Vector2.Dot(x.Value - p.B, tEnd) <= 0 || Vector2.Dot(q.A - x.Value, tStart) <= 0)
        {
            before.Add(new Seg(p.B, null, q.A));
            return true;
        }
        // A straight segment simply reaches the corner; a curve keeps its shape and a straight piece carries it on.
        bool extendP = p.C != null, extendQ = q.C != null;
        if (!extendP)
            before[^1] = p with { B = x.Value };
        if (!extendQ)
            after[0] = q with { A = x.Value };
        if (extendP)
            before.Add(new Seg(p.B, null, x.Value));
        if (extendQ)
            after.Insert(0, new Seg(x.Value, null, q.A));
        return true;
    }

    /// <summary>Drops slivers of cut line too short to be an edge.</summary>
    private static void Tidy(List<Seg> cut, bool closed)
    {
        for (int i = 0; i < cut.Count && cut.Count > (closed ? 3 : 1);)
        {
            if (Vector2.Distance(cut[i].A, cut[i].B) >= 0.01f)
            {
                i++;
                continue;
            }
            var gone = cut[i];
            cut.RemoveAt(i);
            if (i < cut.Count)
                cut[i] = cut[i] with { A = gone.A };
            else if (closed)
                cut[0] = cut[0] with { A = gone.A };
            else
                cut[^1] = cut[^1] with { B = gone.B };
        }
    }

    /// <summary>Why the planned pieces would not work, or null.</summary>
    private static string? Check(PatternPiece piece, Result result)
    {
        string bad = Lang.T("Too wide: the cut line would cross another edge or a hole.");
        var shapes = new List<PatternPiece> { new() { Points = result.Points, Holes = result.Holes } };
        shapes.AddRange(result.Bands.Select(b => new PatternPiece { Points = b.Outline, Holes = b.Holes }));
        if (!shapes.All(Valid))
            return bad;
        float before = Outline.Area(piece);
        float after = shapes.Sum(Outline.Area);
        return MathF.Abs(after - before) <= 0.01f * before + 0.5f ? null : bad;
    }

    private static bool Valid(PatternPiece shape)
    {
        if (shape.Points.Count < 3 || shape.Holes.Any(h => h.Count < 3))
            return false;
        var outer = Outline.Flatten(shape);
        if (!Outline.IsSimple(outer) || Outline.SignedArea(outer) <= 1e-3f)
            return false;
        var holes = Outline.FlattenHoles(shape);
        for (int i = 0; i < holes.Count; i++)
        {
            var hole = holes[i];
            if (!Outline.IsSimple(hole) || Outline.SignedArea(hole) >= -1e-3f)
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

    // ---------------------------------------------------------------- curves

    private static Seg EdgeSeg(List<PatternPoint> loop, int i)
        => new(loop[i].Position, loop[i].Curve, loop[(i + 1) % loop.Count].Position);

    private static bool IsStraight(Seg seg)
    {
        if (seg.C is not { } c)
            return true;
        var ab = seg.B - seg.A;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-12f)
            return true;
        return MathF.Abs(Cross(ab, c - seg.A)) <= 1e-5f * len2
               && Vector2.Dot(c - seg.A, ab) >= 0 && Vector2.Dot(c - seg.B, -ab) >= 0;
    }

    private static Vector2 Eval(Seg seg, float t)
    {
        if (seg.C is not { } c)
            return Vector2.Lerp(seg.A, seg.B, t);
        float u = 1 - t;
        return u * u * seg.A + 2 * u * t * c + t * t * seg.B;
    }

    private static (Seg First, Seg Second) Split(Seg seg, float t)
    {
        if (seg.C is not { } c)
        {
            var m = Vector2.Lerp(seg.A, seg.B, t);
            return (new Seg(seg.A, null, m), new Seg(m, null, seg.B));
        }
        var c1 = Vector2.Lerp(seg.A, c, t);
        var c2 = Vector2.Lerp(c, seg.B, t);
        var mid = Vector2.Lerp(c1, c2, t);
        return (new Seg(seg.A, c1, mid), new Seg(mid, c2, seg.B));
    }

    /// <summary>The part of an edge between curve parameters <paramref name="t0"/> and <paramref name="t1"/>.</summary>
    private static Seg Middle(Seg seg, float t0, float t1)
    {
        if (t1 < 1)
            seg = Split(seg, t1).First;
        if (t0 > 0)
            seg = Split(seg, t0 / t1).Second;
        return seg;
    }

    private static List<Vector2> Flatten(Seg seg, int steps)
    {
        if (seg.C == null)
            return new List<Vector2> { seg.A, seg.B };
        var list = new List<Vector2>(steps + 1);
        for (int i = 0; i <= steps; i++)
            list.Add(Eval(seg, i / (float)steps));
        return list;
    }

    private static float Length(Seg seg)
    {
        var pts = Flatten(seg, 24);
        float len = 0;
        for (int i = 1; i < pts.Count; i++)
            len += Vector2.Distance(pts[i - 1], pts[i]);
        return len;
    }

    /// <summary>The fraction of the edge's length that lies before curve parameter <paramref name="t"/>.</summary>
    private static float ArcOfT(Seg seg, float t)
    {
        if (seg.C == null)
            return t;
        float total = Length(seg);
        return total > 1e-9f ? Length(Split(seg, t).First) / total : t;
    }

    /// <summary>The curve parameter a fraction <paramref name="f"/> of the way along the edge.</summary>
    private static float TOfArc(Seg seg, float f)
    {
        if (seg.C == null)
            return f;
        float lo = 0, hi = 1;
        for (int i = 0; i < 30; i++)
        {
            float mid = (lo + hi) / 2;
            if (ArcOfT(seg, mid) < f)
                lo = mid;
            else
                hi = mid;
        }
        return (lo + hi) / 2;
    }

    /// <summary>
    /// Splits the edge from <paramref name="a"/> to <paramref name="b"/> (curved through <paramref name="curve"/>) a
    /// fraction of its length along, keeping its shape: the curve control of the first part, the point between the
    /// parts, and the control of the second part.
    /// </summary>
    public static (Vector2? FirstCurve, Vector2 Middle, Vector2? SecondCurve) SplitEdge(Vector2 a, Vector2? curve, Vector2 b, float fraction)
    {
        var seg = new Seg(a, curve, b);
        var (first, second) = Split(seg, TOfArc(seg, fraction));
        return (first.C, first.B, second.C);
    }

    private static List<Vector2> Polyline(List<Seg> cut)
    {
        var line = new List<Vector2>();
        foreach (var seg in cut)
        {
            var pts = Flatten(seg, 16);
            line.AddRange(line.Count == 0 ? pts : pts.Skip(1));
        }
        return line;
    }

    private static Vector2 StartTangent(Seg seg)
        => seg.C is { } c && Vector2.DistanceSquared(c, seg.A) > 1e-10f ? c - seg.A : seg.B - seg.A;

    private static Vector2 EndTangent(Seg seg)
        => seg.C is { } c && Vector2.DistanceSquared(seg.B, c) > 1e-10f ? seg.B - c : seg.B - seg.A;

    private static Vector2 Left(Vector2 d)
    {
        var n = new Vector2(-d.Y, d.X);
        float len = n.Length();
        return len > 1e-12f ? n / len : Vector2.Zero;
    }

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    /// <summary>Where the line through <paramref name="p"/> along <paramref name="dp"/> meets the one through <paramref name="q"/> along <paramref name="dq"/>.</summary>
    private static Vector2? Intersect(Vector2 p, Vector2 dp, Vector2 q, Vector2 dq)
    {
        float den = Cross(dp, dq);
        if (MathF.Abs(den) < 1e-9f * dp.Length() * dq.Length() || MathF.Abs(den) < 1e-14f)
            return null;
        float s = Cross(q - p, dq) / den;
        return p + s * dp;
    }
}
