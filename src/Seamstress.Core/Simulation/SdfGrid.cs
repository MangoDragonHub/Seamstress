using System.Numerics;

namespace Seamstress.Core.Simulation;

/// <summary>A triangle mesh in metres.</summary>
public sealed class TriMesh
{
    public required Vector3[] Positions { get; init; }
    public required int[] Triangles { get; init; }

    public (Vector3 Min, Vector3 Max) Bounds()
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in Positions)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return (min, max);
    }
}

/// <summary>
/// Signed distance field sampled on a regular grid; negative inside the body. Cloth particles query it for collisions,
/// which costs the same no matter how detailed the avatar mesh is.
/// </summary>
public sealed class SdfGrid
{
    public Vector3 Origin { get; }
    public float Cell { get; }
    public int Nx { get; }
    public int Ny { get; }
    public int Nz { get; }
    private readonly float[] _d;

    private SdfGrid(Vector3 origin, float cell, int nx, int ny, int nz)
    {
        Origin = origin;
        Cell = cell;
        Nx = nx;
        Ny = ny;
        Nz = nz;
        _d = new float[nx * ny * nz];
    }

    private int Index(int x, int y, int z) => (z * Ny + y) * Nx + x;

    public float At(int x, int y, int z) => _d[Index(x, y, z)];

    /// <summary>Samples an implicit function (negative inside). The result is only used to extract a surface.</summary>
    public static SdfGrid FromFunction(Func<Vector3, float> f, Vector3 min, Vector3 max, float cell)
    {
        int nx = (int)MathF.Ceiling((max.X - min.X) / cell) + 1;
        int ny = (int)MathF.Ceiling((max.Y - min.Y) / cell) + 1;
        int nz = (int)MathF.Ceiling((max.Z - min.Z) / cell) + 1;
        var grid = new SdfGrid(min, cell, nx, ny, nz);
        Parallel.For(0, nz, z =>
        {
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                    grid._d[grid.Index(x, y, z)] = f(min + new Vector3(x, y, z) * cell);
        });
        return grid;
    }

    /// <summary>Builds a signed distance field around a closed triangle mesh.</summary>
    public static SdfGrid FromMesh(TriMesh mesh, float cell = 0.01f, float padding = 0.12f)
    {
        var (bmin, bmax) = mesh.Bounds();
        var min = bmin - new Vector3(padding);
        var max = bmax + new Vector3(padding);
        int nx = (int)MathF.Ceiling((max.X - min.X) / cell) + 1;
        int ny = (int)MathF.Ceiling((max.Y - min.Y) / cell) + 1;
        int nz = (int)MathF.Ceiling((max.Z - min.Z) / cell) + 1;
        var grid = new SdfGrid(min, cell, nx, ny, nz);
        var d = grid._d;
        const int band = 3;
        float far = cell * (band + 1);
        var unsigned = new float[d.Length];
        var facing = new float[d.Length]; // how squarely the nearest triangle faces the cell, used to settle ties
        var inside = new bool[d.Length];
        Array.Fill(unsigned, float.MaxValue);

        // Bin triangles by the Z slices their distance band touches so slices can be filled in parallel.
        var pos = mesh.Positions;
        var tris = mesh.Triangles;
        int triCount = tris.Length / 3;
        var slices = new List<int>[nz];
        for (int z = 0; z < nz; z++)
            slices[z] = new List<int>();
        for (int t = 0; t < triCount; t++)
        {
            var a = pos[tris[3 * t]];
            var b = pos[tris[3 * t + 1]];
            var c = pos[tris[3 * t + 2]];
            float lo = MathF.Min(a.Z, MathF.Min(b.Z, c.Z)), hi = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));
            int z0 = Math.Max(0, (int)MathF.Floor((lo - min.Z) / cell) - band);
            int z1 = Math.Min(nz - 1, (int)MathF.Ceiling((hi - min.Z) / cell) + band);
            for (int z = z0; z <= z1; z++)
                slices[z].Add(t);
        }

        Parallel.For(0, nz, z =>
        {
            foreach (int t in slices[z])
            {
                var a = pos[tris[3 * t]];
                var b = pos[tris[3 * t + 1]];
                var c = pos[tris[3 * t + 2]];
                var normal = Vector3.Cross(b - a, c - a);
                float nl = normal.Length();
                if (nl < 1e-14f)
                    continue;
                normal /= nl;
                var tmin = Vector3.Min(a, Vector3.Min(b, c));
                var tmax = Vector3.Max(a, Vector3.Max(b, c));
                int x0 = Math.Max(0, (int)MathF.Floor((tmin.X - min.X) / cell) - band);
                int x1 = Math.Min(nx - 1, (int)MathF.Ceiling((tmax.X - min.X) / cell) + band);
                int y0 = Math.Max(0, (int)MathF.Floor((tmin.Y - min.Y) / cell) - band);
                int y1 = Math.Min(ny - 1, (int)MathF.Ceiling((tmax.Y - min.Y) / cell) + band);
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        var p = min + new Vector3(x, y, z) * cell;
                        var q = ClosestPointOnTriangle(p, a, b, c);
                        var diff = p - q;
                        float dist = diff.Length();
                        if (dist > far)
                            continue;
                        int i = grid.Index(x, y, z);
                        float side = Vector3.Dot(diff, normal);
                        float face = dist > 1e-9f ? MathF.Abs(side) / dist : 1f;
                        // Where several triangles are equally near (edges, vertices), trust the one facing the cell most.
                        if (dist < unsigned[i] - 1e-6f || (dist < unsigned[i] + 1e-6f && face > facing[i]))
                        {
                            unsigned[i] = dist;
                            facing[i] = face;
                            inside[i] = side < 0;
                        }
                    }
                }
            }
        });

        // Cells outside the band: flood from the grid corner to find the exterior, everything else is interior.
        var state = new byte[d.Length]; // 0 unknown, 1 exterior
        var queue = new Queue<int>();
        void Visit(int x, int y, int z)
        {
            if (x < 0 || y < 0 || z < 0 || x >= nx || y >= ny || z >= nz)
                return;
            int i = grid.Index(x, y, z);
            if (state[i] != 0 || unsigned[i] != float.MaxValue)
                return;
            state[i] = 1;
            queue.Enqueue(i);
        }
        Visit(0, 0, 0);
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int x = i % nx, y = i / nx % ny, z = i / (nx * ny);
            Visit(x - 1, y, z); Visit(x + 1, y, z);
            Visit(x, y - 1, z); Visit(x, y + 1, z);
            Visit(x, y, z - 1); Visit(x, y, z + 1);
        }
        for (int i = 0; i < d.Length; i++)
        {
            if (unsigned[i] == float.MaxValue)
                inside[i] = state[i] == 0;
        }

        // Extend approximate distances beyond the band so the field has a usable gradient everywhere.
        Chamfer(unsigned, nx, ny, nz, cell);
        for (int i = 0; i < d.Length; i++)
            d[i] = inside[i] ? -unsigned[i] : unsigned[i];
        return grid;
    }

    private static void Chamfer(float[] dist, int nx, int ny, int nz, float cell)
    {
        float Step(int dx, int dy, int dz) => cell * MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        void Sweep(bool forward)
        {
            int zs = forward ? 0 : nz - 1, ze = forward ? nz : -1, s = forward ? 1 : -1;
            for (int z = zs; z != ze; z += s)
            {
                for (int y = forward ? 0 : ny - 1; y != (forward ? ny : -1); y += s)
                {
                    for (int x = forward ? 0 : nx - 1; x != (forward ? nx : -1); x += s)
                    {
                        int i = (z * ny + y) * nx + x;
                        float best = dist[i];
                        for (int dz = -1; dz <= 0; dz++)
                        {
                            for (int dy = -1; dy <= 1; dy++)
                            {
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    // Only neighbours already visited in this sweep direction.
                                    if (dz == 0 && (dy > 0 || (dy == 0 && dx >= 0)))
                                        continue;
                                    int qx = x + dx * s, qy = y + dy * s, qz = z + dz * s;
                                    if (qx < 0 || qy < 0 || qz < 0 || qx >= nx || qy >= ny || qz >= nz)
                                        continue;
                                    float other = dist[(qz * ny + qy) * nx + qx];
                                    if (other == float.MaxValue)
                                        continue;
                                    float candidate = other + Step(dx, dy, dz);
                                    if (candidate < best)
                                        best = candidate;
                                }
                            }
                        }
                        dist[i] = best;
                    }
                }
            }
        }
        Sweep(true);
        Sweep(false);
        for (int i = 0; i < dist.Length; i++)
        {
            if (dist[i] == float.MaxValue)
                dist[i] = cell * (nx + ny + nz);
        }
    }

    /// <summary>Signed distance (metres) and outward direction at a point. Far outside the grid it reports "no contact".</summary>
    public float Sample(Vector3 p, out Vector3 normal)
    {
        var g = (p - Origin) / Cell;
        if (g.X < 0 || g.Y < 0 || g.Z < 0 || g.X >= Nx - 1 || g.Y >= Ny - 1 || g.Z >= Nz - 1)
        {
            normal = Vector3.UnitY;
            return float.MaxValue;
        }
        int x = (int)g.X, y = (int)g.Y, z = (int)g.Z;
        float fx = g.X - x, fy = g.Y - y, fz = g.Z - z;
        int i = (z * Ny + y) * Nx + x;
        int sy = Nx, sz = Nx * Ny;
        float c000 = _d[i], c100 = _d[i + 1], c010 = _d[i + sy], c110 = _d[i + sy + 1];
        float c001 = _d[i + sz], c101 = _d[i + sz + 1], c011 = _d[i + sz + sy], c111 = _d[i + sz + sy + 1];

        float x00 = c000 + (c100 - c000) * fx, x10 = c010 + (c110 - c010) * fx;
        float x01 = c001 + (c101 - c001) * fx, x11 = c011 + (c111 - c011) * fx;
        float y0 = x00 + (x10 - x00) * fy, y1 = x01 + (x11 - x01) * fy;
        float value = y0 + (y1 - y0) * fz;

        float gx = ((c100 - c000) * (1 - fy) + (c110 - c010) * fy) * (1 - fz) + ((c101 - c001) * (1 - fy) + (c111 - c011) * fy) * fz;
        float gy = (x10 - x00) * (1 - fz) + (x11 - x01) * fz;
        float gz = y1 - y0;
        var grad = new Vector3(gx, gy, gz);
        float len = grad.Length();
        normal = len > 1e-9f ? grad / len : Vector3.UnitY;
        return value;
    }

    /// <summary>Extracts the zero surface with the surface-nets method (one vertex per cell the surface crosses).</summary>
    public TriMesh ExtractSurface()
    {
        var cellVertex = new Dictionary<int, int>();
        var positions = new List<Vector3>();
        int cx = Nx - 1, cy = Ny - 1;
        int CellId(int x, int y, int z) => (z * cy + y) * cx + x;
        var v = new float[8];

        for (int z = 0; z < Nz - 1; z++)
        {
            for (int y = 0; y < Ny - 1; y++)
            {
                for (int x = 0; x < Nx - 1; x++)
                {
                    int mask = 0;
                    for (int k = 0; k < 8; k++)
                    {
                        v[k] = At(x + (k & 1), y + ((k >> 1) & 1), z + ((k >> 2) & 1));
                        if (v[k] < 0)
                            mask |= 1 << k;
                    }
                    if (mask == 0 || mask == 255)
                        continue;
                    var sum = Vector3.Zero;
                    int crossings = 0;
                    for (int a = 0; a < 8; a++)
                    {
                        for (int axis = 0; axis < 3; axis++)
                        {
                            int b = a | (1 << axis);
                            if (b == a || (v[a] < 0) == (v[b] < 0))
                                continue;
                            float t = v[a] / (v[a] - v[b]);
                            var pa = new Vector3(a & 1, (a >> 1) & 1, (a >> 2) & 1);
                            var pb = new Vector3(b & 1, (b >> 1) & 1, (b >> 2) & 1);
                            sum += Vector3.Lerp(pa, pb, t);
                            crossings++;
                        }
                    }
                    cellVertex[CellId(x, y, z)] = positions.Count;
                    positions.Add(Origin + (new Vector3(x, y, z) + sum / crossings) * Cell);
                }
            }
        }

        var triangles = new List<int>();
        void Quad(int a, int b, int c, int d, bool flip)
        {
            if (flip)
                (b, d) = (d, b);
            triangles.Add(a); triangles.Add(b); triangles.Add(c);
            triangles.Add(a); triangles.Add(c); triangles.Add(d);
        }
        for (int z = 1; z < Nz - 1; z++)
        {
            for (int y = 1; y < Ny - 1; y++)
            {
                for (int x = 1; x < Nx - 1; x++)
                {
                    float here = At(x, y, z);
                    // Each grid edge that crosses the surface is shared by four cells, whose vertices form a quad.
                    if ((here < 0) != (At(x + 1, y, z) < 0)
                        && cellVertex.TryGetValue(CellId(x, y - 1, z - 1), out int a)
                        && cellVertex.TryGetValue(CellId(x, y, z - 1), out int b)
                        && cellVertex.TryGetValue(CellId(x, y, z), out int c)
                        && cellVertex.TryGetValue(CellId(x, y - 1, z), out int d))
                        Quad(a, b, c, d, here >= 0);
                    if ((here < 0) != (At(x, y + 1, z) < 0)
                        && cellVertex.TryGetValue(CellId(x - 1, y, z - 1), out a)
                        && cellVertex.TryGetValue(CellId(x - 1, y, z), out b)
                        && cellVertex.TryGetValue(CellId(x, y, z), out c)
                        && cellVertex.TryGetValue(CellId(x, y, z - 1), out d))
                        Quad(a, b, c, d, here >= 0);
                    if ((here < 0) != (At(x, y, z + 1) < 0)
                        && cellVertex.TryGetValue(CellId(x - 1, y - 1, z), out a)
                        && cellVertex.TryGetValue(CellId(x, y - 1, z), out b)
                        && cellVertex.TryGetValue(CellId(x, y, z), out c)
                        && cellVertex.TryGetValue(CellId(x - 1, y, z), out d))
                        Quad(a, b, c, d, here >= 0);
                }
            }
        }
        return new TriMesh { Positions = positions.ToArray(), Triangles = triangles.ToArray() };
    }

    public static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        => ClosestPointOnTriangle(p, a, b, c, out _, out _, out _);

    /// <summary>Closest point on triangle abc to p, with its barycentric weights.</summary>
    public static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out float wa, out float wb, out float wc)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) { wa = 1; wb = 0; wc = 0; return a; }

        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) { wa = 0; wb = 1; wc = 0; return b; }

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
        {
            float v = d1 / (d1 - d3);
            wa = 1 - v; wb = v; wc = 0;
            return a + ab * v;
        }

        var cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) { wa = 0; wb = 0; wc = 1; return c; }

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
        {
            float w = d2 / (d2 - d6);
            wa = 1 - w; wb = 0; wc = w;
            return a + ac * w;
        }

        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0)
        {
            float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            wa = 0; wb = 1 - w; wc = w;
            return b + (c - b) * w;
        }

        float denom = 1f / (va + vb + vc);
        float bv = vb * denom, bw = vc * denom;
        wa = 1 - bv - bw; wb = bv; wc = bw;
        return a + ab * bv + ac * bw;
    }
}
