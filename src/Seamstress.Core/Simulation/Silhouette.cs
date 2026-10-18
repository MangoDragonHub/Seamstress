using System.Numerics;

namespace Seamstress.Core.Simulation;

/// <summary>
/// Traces the outline of a body mesh as seen from the front (X to the right, Y up), for the faded reference figure
/// drawn in the 2D pattern view. The mesh is projected onto XY, its triangles are rasterised into a small grid, and
/// the contour is read back with marching squares, so the result is a few smooth loops rather than every face edge.
/// </summary>
public static class Silhouette
{
    /// <summary>Spacing of the tracer grid in pattern centimetres.</summary>
    public const float DefaultCellCm = 0.25f;

    /// <summary>
    /// The front-view outline of <paramref name="mesh"/> in pattern centimetres: each loop is a flat array of x,y
    /// pairs, biggest first. Loops inside the first one (the gap between an arm and the body, say) are drawn as
    /// holes by the caller.
    /// </summary>
    public static List<Vector2[]> FrontOutline(TriMesh mesh, float cellCm = DefaultCellCm)
    {
        if (mesh.Positions.Length == 0 || mesh.Triangles.Length < 3)
            return new List<Vector2[]>();

        var projected = new Vector2[mesh.Positions.Length];
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        for (int i = 0; i < projected.Length; i++)
        {
            var p = mesh.Positions[i];
            projected[i] = new Vector2(p.X * 100f, p.Y * 100f);
            min = Vector2.Min(min, projected[i]);
            max = Vector2.Max(max, projected[i]);
        }

        // A very large mesh gets a coarser grid rather than a huge one.
        cellCm = MathF.Max(cellCm, 0.01f);
        while (((max.X - min.X) / cellCm + 6) * ((max.Y - min.Y) / cellCm + 6) > 4_000_000)
            cellCm *= 2;
        // A two-cell margin keeps the contour away from the grid border.
        min -= new Vector2(2 * cellCm);
        max += new Vector2(2 * cellCm);
        int nx = (int)MathF.Ceiling((max.X - min.X) / cellCm) + 1;
        int ny = (int)MathF.Ceiling((max.Y - min.Y) / cellCm) + 1;

        var field = new float[nx * ny];
        for (int t = 0; t + 2 < mesh.Triangles.Length; t += 3)
        {
            var a = projected[mesh.Triangles[t]];
            var b = projected[mesh.Triangles[t + 1]];
            var c = projected[mesh.Triangles[t + 2]];
            if (MathF.Abs(Cross(b - a, c - a)) < 1e-12f)
                continue;
            int x0 = Math.Max(0, (int)((MathF.Min(a.X, MathF.Min(b.X, c.X)) - min.X) / cellCm));
            int x1 = Math.Min(nx - 1, (int)((MathF.Max(a.X, MathF.Max(b.X, c.X)) - min.X) / cellCm) + 1);
            int y0 = Math.Max(0, (int)((MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) - min.Y) / cellCm));
            int y1 = Math.Min(ny - 1, (int)((MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) - min.Y) / cellCm) + 1);
            for (int y = y0; y <= y1; y++)
            {
                var p = new Vector2(min.X + x0 * cellCm, min.Y + y * cellCm);
                for (int x = x0; x <= x1; x++, p.X += cellCm)
                {
                    if (field[y * nx + x] == 0 && Inside(a, b, c, p))
                        field[y * nx + x] = 1;
                }
            }
        }

        // Round the stepped coverage off so the contour runs smoothly between the samples.
        var temp = new float[field.Length];
        for (int pass = 0; pass < 2; pass++)
        {
            Smooth(field, temp, nx, ny, true);
            Smooth(temp, field, nx, ny, false);
        }

        return Trace(field, nx, ny, min, cellCm);
    }

    private static float Cross(Vector2 u, Vector2 v) => u.X * v.Y - u.Y * v.X;

    /// <summary>True when the sample lies on or inside the triangle.</summary>
    private static bool Inside(Vector2 a, Vector2 b, Vector2 c, Vector2 p)
    {
        float d1 = Cross(b - a, p - a);
        float d2 = Cross(c - b, p - b);
        float d3 = Cross(a - c, p - c);
        return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
    }

    /// <summary>One 1-2-1 smoothing pass along one axis, without letting the border darken the values near it.</summary>
    private static void Smooth(float[] src, float[] dst, int nx, int ny, bool horizontal)
    {
        for (int y = 0; y < ny; y++)
        {
            for (int x = 0; x < nx; x++)
            {
                float sum = 2 * src[y * nx + x], weight = 2;
                int back = horizontal ? x - 1 : y - 1, forward = horizontal ? x + 1 : y + 1;
                if (back >= 0)
                {
                    sum += horizontal ? src[y * nx + back] : src[back * nx + x];
                    weight += 1;
                }
                if (forward < (horizontal ? nx : ny))
                {
                    sum += horizontal ? src[y * nx + forward] : src[forward * nx + x];
                    weight += 1;
                }
                dst[y * nx + x] = sum / weight;
            }
        }
    }

    /// <summary>Reads the 0.5 level of the smoothed field back as closed loops, biggest first.</summary>
    private static List<Vector2[]> Trace(float[] field, int nx, int ny, Vector2 origin, float cell)
    {
        const float level = 0.5f;
        var segments = new List<(long A, long B, Vector2 Pa, Vector2 Pb)>();
        // A crossing point on the edge between samples x,y and x+1,y (horizontal) or x,y and x,y+1 (vertical).
        long KeyH(int x, int y) => (((long)y * (nx - 1) + x) * 2);
        long KeyV(int x, int y) => (((long)y * nx + x) * 2 + 1);
        Vector2 Horizontal(int x, int y, float left, float right) => new(x + (level - left) / (right - left), y);
        Vector2 Vertical(int x, int y, float bottom, float top) => new(x, y + (level - bottom) / (top - bottom));
        void Add(long a, Vector2 pa, long b, Vector2 pb) => segments.Add((a, b, pa, pb));

        for (int y = 0; y < ny - 1; y++)
        {
            for (int x = 0; x < nx - 1; x++)
            {
                float v0 = field[y * nx + x], v1 = field[y * nx + x + 1];
                float v2 = field[(y + 1) * nx + x + 1], v3 = field[(y + 1) * nx + x];
                int code = (v0 >= level ? 1 : 0) | (v1 >= level ? 2 : 0) | (v2 >= level ? 4 : 0) | (v3 >= level ? 8 : 0);
                if (code == 0 || code == 15)
                    continue;
                long k = KeyH(x, y), r = KeyV(x + 1, y), t = KeyH(x, y + 1), l = KeyV(x, y);
                var pk = Horizontal(x, y, v0, v1);
                var pr = Vertical(x + 1, y, v1, v2);
                var pt = Horizontal(x, y + 1, v3, v2);
                var pl = Vertical(x, y, v0, v3);
                switch (code)
                {
                    case 1: Add(l, pl, k, pk); break;
                    case 2: Add(k, pk, r, pr); break;
                    case 3: Add(l, pl, r, pr); break;
                    case 4: Add(r, pr, t, pt); break;
                    // The two opposite corners are inside; the centre value decides how the contour crosses.
                    case 5 when (v0 + v1 + v2 + v3) / 4 >= level:
                        Add(k, pk, r, pr); Add(t, pt, l, pl); break;
                    case 5:
                        Add(l, pl, k, pk); Add(r, pr, t, pt); break;
                    case 6: Add(k, pk, t, pt); break;
                    case 7: Add(l, pl, t, pt); break;
                    case 8: Add(t, pt, l, pl); break;
                    case 9: Add(k, pk, t, pt); break;
                    case 10 when (v0 + v1 + v2 + v3) / 4 >= level:
                        Add(l, pl, k, pk); Add(r, pr, t, pt); break;
                    case 10:
                        Add(k, pk, r, pr); Add(t, pt, l, pl); break;
                    case 11: Add(r, pr, t, pt); break;
                    case 12: Add(l, pl, r, pr); break;
                    case 13: Add(k, pk, r, pr); break;
                    case 14: Add(l, pl, k, pk); break;
                }
            }
        }

        // Every crossing edge belongs to the two segments on either side of it, so the segments chain into loops.
        var sides = new Dictionary<long, (int First, int Second)>();
        for (int i = 0; i < segments.Count; i++)
        {
            foreach (long key in new[] { segments[i].A, segments[i].B })
            {
                if (sides.TryGetValue(key, out var pair))
                    sides[key] = (pair.First, i);
                else
                    sides[key] = (i, -1);
            }
        }

        var used = new bool[segments.Count];
        var loops = new List<Vector2[]>();
        for (int s = 0; s < segments.Count; s++)
        {
            if (used[s])
                continue;
            var loop = new List<Vector2>();
            long start = segments[s].A, from = start;
            int current = s;
            while (true)
            {
                used[current] = true;
                var segment = segments[current];
                long to = segment.A == from ? segment.B : segment.A;
                loop.Add(segment.A == from ? segment.Pb : segment.Pa);
                if (to == start)
                    break;
                var pair = sides[to];
                if (pair.Second < 0)
                    break;
                current = pair.First == current ? pair.Second : pair.First;
                from = to;
            }
            if (loop.Count >= 4)
                loops.Add(Simplify(loop, origin, cell));
        }
        loops.Sort((a, b) => MathF.Abs(Area(b)).CompareTo(MathF.Abs(Area(a))));
        return loops;
    }

    private static float Area(Vector2[] loop)
    {
        float sum = 0;
        for (int i = 0; i < loop.Length; i++)
        {
            var a = loop[i];
            var b = loop[(i + 1) % loop.Length];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return sum / 2;
    }

    /// <summary>Turns grid coordinates into pattern centimetres and drops the points the contour does not need.</summary>
    private static Vector2[] Simplify(List<Vector2> loop, Vector2 origin, float cell)
    {
        for (int i = 0; i < loop.Count; i++)
            loop[i] = origin + loop[i] * cell;

        // Split the closed loop at its widest points so each half can be simplified as an open line.
        int left = 0, right = 0;
        for (int i = 1; i < loop.Count; i++)
        {
            if (loop[i].X < loop[left].X)
                left = i;
            if (loop[i].X > loop[right].X)
                right = i;
        }
        if (left == right)
            return loop.ToArray();
        int n = loop.Count;
        var rotated = new List<Vector2>(n);
        for (int i = 0; i < n; i++)
            rotated.Add(loop[(left + i) % n]);
        int m = (right - left + n) % n;

        var first = SimplifyOpen(rotated, 0, m, cell * 0.6f);
        var second = new List<Vector2>();
        for (int i = m; i < n; i++)
            second.Add(rotated[i]);
        second.Add(rotated[0]);
        var back = SimplifyOpen(second, 0, second.Count - 1, cell * 0.6f);
        // The two chains share their end points; keep one copy of each.
        for (int i = 1; i < back.Count - 1; i++)
            first.Add(back[i]);
        return first.ToArray();
    }

    /// <summary>Ramer-Douglas-Peucker: keeps the points of pts[first..last] that are further than epsilon from the line.</summary>
    private static List<Vector2> SimplifyOpen(List<Vector2> pts, int first, int last, float epsilon)
    {
        var keep = new bool[pts.Count];
        keep[first] = keep[last] = true;
        var stack = new Stack<(int First, int Last)>();
        stack.Push((first, last));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            if (b - a < 2)
                continue;
            var pa = pts[a];
            var pb = pts[b];
            var direction = pb - pa;
            float length = direction.Length();
            int worst = -1;
            float far = epsilon;
            for (int i = a + 1; i < b; i++)
            {
                float distance = length > 1e-9f
                    ? MathF.Abs(Cross(direction / length, pts[i] - pa))
                    : Vector2.Distance(pts[i], pa);
                if (distance > far)
                {
                    far = distance;
                    worst = i;
                }
            }
            if (worst < 0)
                continue;
            keep[worst] = true;
            stack.Push((a, worst));
            stack.Push((worst, b));
        }
        var result = new List<Vector2>();
        for (int i = first; i <= last; i++)
        {
            if (keep[i])
                result.Add(pts[i]);
        }
        return result;
    }
}
