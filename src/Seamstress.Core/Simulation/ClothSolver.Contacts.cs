using System.Numerics;

namespace Seamstress.Core.Simulation;

// Cloth-against-cloth collision: every particle is kept on its side of every nearby triangle, at a distance of
// the two fabrics' thickness. Covers a garment folding onto itself and garments layered over each other.
public sealed partial class ClothSolver
{
    private const float ContactMargin = 0.02f;
    private const float ContactGap = 0.0015f;

    private struct Contact
    {
        public int Particle;
        public int Triangle;
        public float Side;
    }

    private int[] _tris = Array.Empty<int>();
    private int[] _weld = Array.Empty<int>();
    private int[]?[] _near = Array.Empty<int[]?>();
    private Contact[] _contacts = Array.Empty<Contact>();
    private int _contactCount;
    private Dictionary<long, float> _sides = new();
    private Dictionary<long, float> _sidesNext = new();
    private int[] _bucketStart = Array.Empty<int>();
    private int[] _bucketFill = Array.Empty<int>();
    private int[] _bucketItems = Array.Empty<int>();
    private int[] _triStamp = Array.Empty<int>();
    private int[] _triPiece = Array.Empty<int>();
    private Vector2[] _triRest = Array.Empty<Vector2>();
    private Vector2[] _rest2D = Array.Empty<Vector2>();

    // Within one piece, cloth closer than this in the flat pattern (cm) is held apart by the fabric itself.
    private const float SamePieceRestDistance = 5f;

    /// <summary>Number of particle-triangle pairs currently being watched.</summary>
    public int ContactCount => _contactCount;

    /// <summary>
    /// Records which particles are neighbours in the sewn-together surface. A particle never collides with triangles
    /// within two edges of it: those are held apart by the cloth itself.
    /// </summary>
    private void BuildContactTopology(int[] triangles, List<(int A, int B)> welds)
    {
        _tris = triangles;
        _triStamp = new int[triangles.Length / 3];
        int n = Positions.Length;
        _rest2D = new Vector2[n];
        foreach (var piece in Pieces)
            piece.Mesh.Points.CopyTo(_rest2D, piece.Offset);
        _triPiece = new int[triangles.Length / 3];
        _triRest = new Vector2[triangles.Length / 3];
        for (int t = 0; t < _triPiece.Length; t++)
        {
            _triPiece[t] = _pieceOf[triangles[3 * t]];
            _triRest[t] = (_rest2D[triangles[3 * t]] + _rest2D[triangles[3 * t + 1]] + _rest2D[triangles[3 * t + 2]]) / 3f;
        }
        _weld = new int[n];
        for (int i = 0; i < n; i++)
            _weld[i] = i;
        int Find(int a)
        {
            while (_weld[a] != a)
                a = _weld[a] = _weld[_weld[a]];
            return a;
        }
        foreach (var (a, b) in welds)
        {
            int ra = Find(a), rb = Find(b);
            if (ra != rb)
                _weld[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }
        for (int i = 0; i < n; i++)
            _weld[i] = Find(i);

        var ring = new HashSet<int>[n];
        void Link(int a, int b)
        {
            (ring[a] ??= new HashSet<int>()).Add(b);
            (ring[b] ??= new HashSet<int>()).Add(a);
        }
        for (int t = 0; t < triangles.Length; t += 3)
        {
            int a = _weld[triangles[t]], b = _weld[triangles[t + 1]], c = _weld[triangles[t + 2]];
            Link(a, b);
            Link(b, c);
            Link(c, a);
        }
        _near = new int[]?[n];
        for (int i = 0; i < n; i++)
        {
            if (ring[i] == null)
                continue;
            var two = new HashSet<int>(ring[i]) { i };
            foreach (int neighbour in ring[i])
                two.UnionWith(ring[neighbour]);
            var sorted = two.ToArray();
            Array.Sort(sorted);
            _near[i] = sorted;
        }
    }

    private bool AreNeighbours(int particle, int triangle)
    {
        var near = _near[_weld[particle]];
        if (near == null)
            return false;
        for (int k = 0; k < 3; k++)
        {
            if (Array.BinarySearch(near, _weld[_tris[3 * triangle + k]]) >= 0)
                return true;
        }
        return false;
    }

    private static int Bucket(int x, int y, int z, int mask)
        => ((x * 73856093) ^ (y * 19349663) ^ (z * 83492791)) & mask;

    /// <summary>Finds the particle-triangle pairs close enough to touch before the next search.</summary>
    private void FindContacts()
    {
        _contactCount = 0;
        var x = Positions;
        int triCount = _tris.Length / 3;
        if (triCount == 0)
            return;

        const float margin = ContactMargin;
        float cell = MathF.Max(0.04f, margin);
        float inv = 1f / cell;
        int size = 1024;
        while (size < triCount * 4)
            size <<= 1;
        int mask = size - 1;
        if (_bucketStart.Length != size + 1)
        {
            _bucketStart = new int[size + 1];
            _bucketFill = new int[size];
        }
        Array.Clear(_bucketStart);

        // Two passes over the triangles' padded bounding boxes: count, then fill.
        for (int pass = 0; pass < 2; pass++)
        {
            int total = 0;
            for (int t = 0; t < triCount; t++)
            {
                var a = x[_tris[3 * t]];
                var b = x[_tris[3 * t + 1]];
                var c = x[_tris[3 * t + 2]];
                var lo = (Vector3.Min(a, Vector3.Min(b, c)) - new Vector3(margin)) * inv;
                var hi = (Vector3.Max(a, Vector3.Max(b, c)) + new Vector3(margin)) * inv;
                int x0 = (int)MathF.Floor(lo.X), x1 = (int)MathF.Floor(hi.X);
                int y0 = (int)MathF.Floor(lo.Y), y1 = (int)MathF.Floor(hi.Y);
                int z0 = (int)MathF.Floor(lo.Z), z1 = (int)MathF.Floor(hi.Z);
                // A triangle stretched across the scene (mid-sewing) would flood the grid; it cannot be in contact anyway.
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1) > 400)
                    continue;
                for (int iz = z0; iz <= z1; iz++)
                {
                    for (int iy = y0; iy <= y1; iy++)
                    {
                        for (int ix = x0; ix <= x1; ix++)
                        {
                            int bucket = Bucket(ix, iy, iz, mask);
                            if (pass == 0)
                                _bucketStart[bucket + 1]++;
                            else
                                _bucketItems[_bucketStart[bucket] + _bucketFill[bucket]++] = t;
                            total++;
                        }
                    }
                }
            }
            if (pass == 0)
            {
                for (int i = 0; i < size; i++)
                    _bucketStart[i + 1] += _bucketStart[i];
                if (_bucketItems.Length < total)
                    _bucketItems = new int[total + total / 2];
                Array.Clear(_bucketFill);
            }
        }

        _sidesNext.Clear();
        float reach = margin * margin;
        for (int p = 0; p < x.Length; p++)
        {
            var pos = x[p];
            int bucket = Bucket((int)MathF.Floor(pos.X * inv), (int)MathF.Floor(pos.Y * inv), (int)MathF.Floor(pos.Z * inv), mask);
            for (int k = _bucketStart[bucket]; k < _bucketStart[bucket + 1]; k++)
            {
                int t = _bucketItems[k];
                if (_triStamp[t] == p + 1)
                    continue;
                _triStamp[t] = p + 1;
                bool samePiece = _triPiece[t] == _pieceOf[p];
                if (samePiece && Vector2.DistanceSquared(_triRest[t], _rest2D[p]) < SamePieceRestDistance * SamePieceRestDistance)
                    continue;
                int ia = _tris[3 * t], ib = _tris[3 * t + 1], ic = _tris[3 * t + 2];
                if (_invMass[p] == 0 && _invMass[ia] == 0 && _invMass[ib] == 0 && _invMass[ic] == 0)
                    continue;
                var q = SdfGrid.ClosestPointOnTriangle(pos, x[ia], x[ib], x[ic]);
                var r = pos - q;
                float distance2 = r.LengthSquared();
                if (distance2 > reach)
                    continue;
                var normal = Vector3.Cross(x[ib] - x[ia], x[ic] - x[ia]);
                float area = normal.Length();
                if (area < 1e-12f)
                    continue;
                // Only a particle that is above or below the triangle has a meaningful side. One that is merely
                // beside it (cloth continuing past the triangle's edge) is left alone until it moves over it.
                float height = Vector3.Dot(r, normal) / area;
                if (height * height < 0.5f * distance2)
                    continue;
                if (AreNeighbours(p, t))
                    continue;

                // Remember which face of the triangle the particle is on; it has to stay there for as long as the
                // pair remains close, even if it gets pushed through for a moment.
                long key = (long)p * triCount + t;
                if (!_sides.TryGetValue(key, out float side))
                    side = height >= 0 ? 1f : -1f;
                _sidesNext[key] = side;
                if (_contactCount == _contacts.Length)
                    Array.Resize(ref _contacts, Math.Max(1024, _contacts.Length * 2));
                _contacts[_contactCount++] = new Contact { Particle = p, Triangle = t, Side = side };
            }
        }
        (_sides, _sidesNext) = (_sidesNext, _sides);
    }

    /// <summary>Every substep: pushes particles and triangles apart where they are closer than the fabrics allow.</summary>
    private void SolveContacts()
    {
        var x = Positions;
        for (int i = 0; i < _contactCount; i++)
        {
            ref var contact = ref _contacts[i];
            int p = contact.Particle;
            int ia = _tris[3 * contact.Triangle], ib = _tris[3 * contact.Triangle + 1], ic = _tris[3 * contact.Triangle + 2];
            var normal = Vector3.Cross(x[ib] - x[ia], x[ic] - x[ia]);
            float area = normal.Length();
            if (area < 1e-12f)
                continue;
            normal *= contact.Side / area;

            var q = SdfGrid.ClosestPointOnTriangle(x[p], x[ia], x[ib], x[ic], out float wa, out float wb, out float wc);
            var r = x[p] - q;
            float height = Vector3.Dot(r, normal);
            float separation = 0.5f * (_clothThickness[p] + (_clothThickness[ia] + _clothThickness[ib] + _clothThickness[ic]) / 3f) + ContactGap;
            // Only when the particle is over the triangle itself; beside it, the neighbouring triangle is in charge.
            if ((r - normal * height).LengthSquared() > separation * separation)
                continue;
            // A deep overlap is worked off over several substeps rather than thrown out in one.
            float depth = MathF.Min(separation - height, separation);
            if (depth <= 0)
                continue;

            float mp = _invMass[p], ma = _invMass[ia], mb = _invMass[ib], mc = _invMass[ic];
            float w = mp + wa * wa * ma + wb * wb * mb + wc * wc * mc;
            if (w == 0)
                continue;
            var push = normal * (depth / w);
            x[p] += push * mp;
            x[ia] -= push * (wa * ma);
            x[ib] -= push * (wb * mb);
            x[ic] -= push * (wc * mc);

            // Friction between the two fabrics: cancel the sliding between the particle and the spot it touches,
            // completely if it is small compared with how hard they press together.
            var slide = (x[p] - _previous[p])
                      - ((x[ia] - _previous[ia]) * wa + (x[ib] - _previous[ib]) * wb + (x[ic] - _previous[ic]) * wc);
            slide -= normal * Vector3.Dot(slide, normal);
            float sliding = slide.Length();
            if (sliding < 1e-9f)
                continue;
            float grip = 0.5f * (_friction[p] + (_friction[ia] + _friction[ib] + _friction[ic]) / 3f) * depth;
            var cancel = slide * ((sliding <= grip ? 1f : grip / sliding) / w);
            x[p] -= cancel * mp;
            x[ia] += cancel * (wa * ma);
            x[ib] += cancel * (wb * mb);
            x[ic] += cancel * (wc * mc);
        }
    }
}
