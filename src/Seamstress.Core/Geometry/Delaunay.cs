namespace Seamstress.Core.Geometry;

/// <summary>
/// Incremental Delaunay triangulation (point insertion with Lawson edge flips).
/// Points are expected to be in general position; callers perturb collinear input.
/// </summary>
public sealed class Delaunay
{
    private readonly double[] _x;
    private readonly double[] _y;
    private readonly List<int> _v = new();   // 3 vertex indices per triangle, counter-clockwise
    private readonly List<int> _adj = new(); // neighbour across edge (v[k], v[k+1]) or -1
    private readonly Stack<int> _stack = new();
    private readonly int _count;
    private int _last;

    private Delaunay(double[] x, double[] y, int count)
    {
        _x = x;
        _y = y;
        _count = count;
    }

    /// <summary>Triangulates the points and returns counter-clockwise triangles as index triples.</summary>
    public static int[] Triangulate(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        int n = xs.Count;
        if (n < 3)
            return Array.Empty<int>();

        var x = new double[n + 3];
        var y = new double[n + 3];
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            x[i] = xs[i];
            y[i] = ys[i];
            minX = Math.Min(minX, x[i]);
            maxX = Math.Max(maxX, x[i]);
            minY = Math.Min(minY, y[i]);
            maxY = Math.Max(maxY, y[i]);
        }
        double size = Math.Max(Math.Max(maxX - minX, maxY - minY), 1e-6);
        double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
        // Super triangle far enough out that it does not disturb the hull of the real points.
        x[n] = cx - 30 * size; y[n] = cy - 20 * size;
        x[n + 1] = cx + 30 * size; y[n + 1] = cy - 20 * size;
        x[n + 2] = cx; y[n + 2] = cy + 40 * size;

        var d = new Delaunay(x, y, n);
        d.AddTriangle(n, n + 1, n + 2, -1, -1, -1);

        // Insert in a spatially coherent order so the walk from the previous triangle stays short.
        var order = new int[n];
        for (int i = 0; i < n; i++)
            order[i] = i;
        int cells = Math.Max(1, (int)Math.Sqrt(n / 4.0));
        long Key(int i)
        {
            int gy = Math.Min(cells - 1, (int)((y[i] - minY) / size * cells));
            int gx = Math.Min(cells - 1, (int)((x[i] - minX) / size * cells));
            if ((gy & 1) == 1)
                gx = cells - 1 - gx;
            return (long)gy * cells + gx;
        }
        var keys = new long[n];
        for (int i = 0; i < n; i++)
            keys[i] = Key(i);
        Array.Sort(keys, order);

        foreach (int p in order)
            d.Insert(p);

        var result = new List<int>(d._v.Count);
        for (int t = 0; t < d._v.Count; t += 3)
        {
            int a = d._v[t], b = d._v[t + 1], c = d._v[t + 2];
            if (a < n && b < n && c < n)
            {
                result.Add(a);
                result.Add(b);
                result.Add(c);
            }
        }
        return result.ToArray();
    }

    private int AddTriangle(int a, int b, int c, int n0, int n1, int n2)
    {
        int t = _v.Count / 3;
        _v.Add(a); _v.Add(b); _v.Add(c);
        _adj.Add(n0); _adj.Add(n1); _adj.Add(n2);
        return t;
    }

    private double Orient(int a, int b, double px, double py)
        => (_x[b] - _x[a]) * (py - _y[a]) - (_y[b] - _y[a]) * (px - _x[a]);

    private bool InCircle(int a, int b, int c, int d)
    {
        double ax = _x[a] - _x[d], ay = _y[a] - _y[d];
        double bx = _x[b] - _x[d], by = _y[b] - _y[d];
        double cx = _x[c] - _x[d], cy = _y[c] - _y[d];
        double det = (ax * ax + ay * ay) * (bx * cy - cx * by)
                   - (bx * bx + by * by) * (ax * cy - cx * ay)
                   + (cx * cx + cy * cy) * (ax * by - bx * ay);
        return det > 1e-12 * (Math.Abs(ax * ax + ay * ay) + Math.Abs(bx * bx + by * by) + Math.Abs(cx * cx + cy * cy));
    }

    private int Locate(double px, double py)
    {
        int t = _last;
        int total = _v.Count / 3;
        for (int guard = 0; guard < total + 16; guard++)
        {
            bool moved = false;
            for (int k = 0; k < 3; k++)
            {
                int a = _v[3 * t + k], b = _v[3 * t + (k + 1) % 3];
                if (Orient(a, b, px, py) < 0)
                {
                    int next = _adj[3 * t + k];
                    if (next >= 0)
                    {
                        t = next;
                        moved = true;
                        break;
                    }
                }
            }
            if (!moved)
                return t;
        }
        // The walk can cycle on degenerate input; fall back to an exhaustive search.
        for (t = 0; t < total; t++)
        {
            if (Orient(_v[3 * t], _v[3 * t + 1], px, py) >= 0
                && Orient(_v[3 * t + 1], _v[3 * t + 2], px, py) >= 0
                && Orient(_v[3 * t + 2], _v[3 * t], px, py) >= 0)
                return t;
        }
        return _last;
    }

    private void ReplaceNeighbour(int tri, int oldNeighbour, int newNeighbour)
    {
        if (tri < 0)
            return;
        for (int k = 0; k < 3; k++)
        {
            if (_adj[3 * tri + k] == oldNeighbour)
            {
                _adj[3 * tri + k] = newNeighbour;
                return;
            }
        }
    }

    private void Insert(int p)
    {
        int t = Locate(_x[p], _y[p]);
        int a = _v[3 * t], b = _v[3 * t + 1], c = _v[3 * t + 2];
        int n0 = _adj[3 * t], n1 = _adj[3 * t + 1], n2 = _adj[3 * t + 2];

        int t1 = _v.Count / 3;
        int t2 = t1 + 1;
        // t = (a, b, p), t1 = (b, c, p), t2 = (c, a, p)
        _v[3 * t + 2] = p;
        _adj[3 * t] = n0; _adj[3 * t + 1] = t1; _adj[3 * t + 2] = t2;
        AddTriangle(b, c, p, n1, t2, t);
        AddTriangle(c, a, p, n2, t, t1);
        ReplaceNeighbour(n1, t, t1);
        ReplaceNeighbour(n2, t, t2);

        _stack.Push(t);
        _stack.Push(t1);
        _stack.Push(t2);
        int flips = 0;
        int maxFlips = 64 + _v.Count;
        while (_stack.Count > 0)
        {
            int tri = _stack.Pop();
            int ip = _v[3 * tri] == p ? 0 : _v[3 * tri + 1] == p ? 1 : _v[3 * tri + 2] == p ? 2 : -1;
            if (ip < 0)
                continue;
            int ek = (ip + 1) % 3;
            int nb = _adj[3 * tri + ek];
            if (nb < 0)
                continue;
            int j = _adj[3 * nb] == tri ? 0 : _adj[3 * nb + 1] == tri ? 1 : 2;
            int u = _v[3 * tri + ek];
            int w = _v[3 * tri + (ek + 1) % 3];
            int d = _v[3 * nb + (j + 2) % 3];
            if (!InCircle(p, u, w, d) || ++flips > maxFlips)
                continue;

            int adjA = _adj[3 * tri + ip];               // across (p, u)
            int adjD = _adj[3 * tri + (ip + 2) % 3];     // across (w, p)
            int adjB = _adj[3 * nb + (j + 1) % 3];       // across (u, d)
            int adjC = _adj[3 * nb + (j + 2) % 3];       // across (d, w)

            // tri = (p, u, d), nb = (p, d, w)
            _v[3 * tri] = p; _v[3 * tri + 1] = u; _v[3 * tri + 2] = d;
            _adj[3 * tri] = adjA; _adj[3 * tri + 1] = adjB; _adj[3 * tri + 2] = nb;
            _v[3 * nb] = p; _v[3 * nb + 1] = d; _v[3 * nb + 2] = w;
            _adj[3 * nb] = tri; _adj[3 * nb + 1] = adjC; _adj[3 * nb + 2] = adjD;
            ReplaceNeighbour(adjB, nb, tri);
            ReplaceNeighbour(adjD, tri, nb);

            _stack.Push(tri);
            _stack.Push(nb);
        }
        _last = t;
    }
}
