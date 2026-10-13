using System.Numerics;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;

namespace Seamstress.Core.Simulation;

/// <summary>Where one piece's particles live inside the solver arrays.</summary>
public sealed class ClothPiece
{
    public required PatternPiece Piece { get; init; }
    public required PieceMesh Mesh { get; init; }
    public required int Offset { get; init; }

    /// <summary>False for pieces of garments that are only present as obstacles.</summary>
    public required bool Simulated { get; init; }

    /// <summary>Index of the garment the piece belongs to among the garments in the solver.</summary>
    public required int Garment { get; init; }
}

/// <summary>
/// Position-based cloth solver (XPBD with small substeps). Every mesh edge is a stretch constraint, every pair of
/// adjacent triangles a bend constraint, and every stitch pulls two particles together.
/// </summary>
public sealed partial class ClothSolver
{
    private const float FrameTime = 1f / 60f;
    private const float MaxSpeed = 8f;
    private const float SeamClosingSpeed = 0.8f;
    private const float SkinOffset = 0.002f;

    public Vector3[] Positions { get; }
    private readonly Vector3[] _previous;
    private readonly Vector3[] _velocity;
    private readonly float[] _invMass;
    private readonly float[] _thickness;
    private readonly float[] _friction;
    private readonly float[] _drag;
    private readonly float[] _clothThickness;
    private readonly bool[] _openStitch;
    private readonly bool[] _stitched;
    private readonly int[] _pieceOf;

    private int[] _ca = Array.Empty<int>(), _cb = Array.Empty<int>();
    private float[] _rest = Array.Empty<float>(), _compliance = Array.Empty<float>();
    private int[] _sa = Array.Empty<int>(), _sb = Array.Empty<int>();
    private int[] _ha = Array.Empty<int>(), _hb = Array.Empty<int>(); // bending across seams
    private float[] _hrest = Array.Empty<float>(), _hcompliance = Array.Empty<float>();
    private Vector3[] _pieceShift = Array.Empty<Vector3>();
    private int[] _pieceStitches = Array.Empty<int>();
    private int[] _garmentOfPiece = Array.Empty<int>();
    private float[] _sewing = Array.Empty<float>();   // per garment: 1 while its seams are far apart, 0 once sewn
    private float[] _gap = Array.Empty<float>();
    private float[] _bestGap = Array.Empty<float>();
    private int[] _stalled = Array.Empty<int>();
    private bool[] _sewn = Array.Empty<bool>();
    private const int StallFrames = 45;

    public IReadOnlyList<ClothPiece> Pieces { get; }
    public SdfGrid? Collider { get; }
    public SimulationSettings Settings { get; }
    public int ParticleCount => Positions.Length;
    public int ConstraintCount => _ca.Length + _sa.Length;

    /// <summary>Largest distance between two stitched particles before the last step, in metres.</summary>
    public float SeamGap { get; private set; }

    /// <summary>Fastest particle after the last step, in metres per second.</summary>
    public float PeakSpeed { get; private set; }

    private ClothSolver(int count, List<ClothPiece> pieces, SdfGrid? collider, SimulationSettings settings)
    {
        Positions = new Vector3[count];
        _previous = new Vector3[count];
        _velocity = new Vector3[count];
        _invMass = new float[count];
        _thickness = new float[count];
        _friction = new float[count];
        _drag = new float[count];
        _clothThickness = new float[count];
        _openStitch = new bool[count];
        _stitched = new bool[count];
        _pieceOf = new int[count];
        Pieces = pieces;
        Collider = collider;
        Settings = settings;
    }

    /// <summary>
    /// Creates a solver for the <paramref name="simulated"/> garments. <paramref name="obstacles"/> are garments that
    /// stay frozen but that the simulated cloth may collide with.
    /// </summary>
    public static ClothSolver Create(IReadOnlyList<GarmentMesh> simulated, IReadOnlyList<GarmentMesh> obstacles,
        SdfGrid? collider, SimulationSettings settings)
    {
        var pieces = new List<ClothPiece>();
        int count = 0;
        var offsets = new Dictionary<PieceMesh, int>();
        int garmentCount = 0;
        void Register(GarmentMesh garmentMesh, bool isSimulated)
        {
            foreach (var mesh in garmentMesh.Pieces)
            {
                var piece = garmentMesh.Garment.FindPiece(mesh.PieceId)!;
                offsets[mesh] = count;
                pieces.Add(new ClothPiece { Piece = piece, Mesh = mesh, Offset = count, Simulated = isSimulated, Garment = garmentCount });
                count += mesh.VertexCount;
            }
            garmentCount++;
        }
        foreach (var g in simulated)
            Register(g, true);
        if (settings.SelfCollision)
        {
            foreach (var g in obstacles)
                Register(g, false);
        }

        var solver = new ClothSolver(count, pieces, collider, settings);
        // For every piece: boundary edge -> the vertex opposite it, used to resist folding across seams.
        var rims = new Dictionary<PieceMesh, Dictionary<long, int>>();
        var triangles = new List<int>();
        var ca = new List<int>();
        var cb = new List<int>();
        var rest = new List<float>();
        var compliance = new List<float>();

        for (int pi = 0; pi < pieces.Count; pi++)
        {
            var cloth = pieces[pi];
            var mesh = cloth.Mesh;
            var physics = cloth.Piece.Physics;
            int off = cloth.Offset;

            var positions = Mesher.CurrentPositions(cloth.Piece, mesh);
            var area = new float[mesh.VertexCount];
            for (int t = 0; t < mesh.Triangles.Length; t += 3)
            {
                int a = mesh.Triangles[t], b = mesh.Triangles[t + 1], c = mesh.Triangles[t + 2];
                var ab = mesh.Points[b] - mesh.Points[a];
                var ac = mesh.Points[c] - mesh.Points[a];
                float triArea = MathF.Abs(ab.X * ac.Y - ab.Y * ac.X) * 0.5f * 1e-4f; // cm² to m²
                area[a] += triArea / 3;
                area[b] += triArea / 3;
                area[c] += triArea / 3;
            }
            float density = MathF.Max(physics.Density, 5f) / 1000f; // g/m² to kg/m²
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                int p = off + i;
                solver.Positions[p] = positions[i];
                solver._previous[p] = positions[i];
                float mass = MathF.Max(area[i] * density, 1e-7f);
                solver._invMass[p] = cloth.Simulated ? 1f / mass : 0f;
                solver._thickness[p] = MathF.Max(physics.Thickness, 0.1f) * 0.001f + SkinOffset;
                solver._friction[p] = Math.Clamp(physics.Friction / 100f, 0, 1) * 1.5f;
                solver._drag[p] = 0.2f + Math.Clamp(physics.Damping, 0, 100) * 0.06f;
                solver._clothThickness[p] = MathF.Max(physics.Thickness, 0.5f) * 0.001f;
                solver._pieceOf[p] = pi;
            }
            foreach (int index in mesh.Triangles)
                triangles.Add(off + index);
            if (!cloth.Simulated)
                continue;

            // Unique edges with the vertices opposite them (one per adjacent triangle).
            var edges = new Dictionary<long, (int C, int D)>();
            for (int t = 0; t < mesh.Triangles.Length; t += 3)
            {
                for (int k = 0; k < 3; k++)
                {
                    int a = mesh.Triangles[t + k], b = mesh.Triangles[t + (k + 1) % 3], c = mesh.Triangles[t + (k + 2) % 3];
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    edges[key] = edges.TryGetValue(key, out var existing) ? (existing.C, c) : (c, -1);
                }
            }

            float bendStiffness = BendStiffness(physics);
            var rim = rims[mesh] = new Dictionary<long, int>();
            foreach (var (key, opposite) in edges)
            {
                if (opposite.D < 0)
                    rim[key] = opposite.C;
                int a = (int)(key >> 32), b = (int)(key & 0xFFFFFFFF);
                var d2 = mesh.Points[b] - mesh.Points[a];
                float length = d2.Length() * 0.01f;
                if (length < 1e-6f)
                    continue;
                // Edges along the weave resist with the stretch stiffness, diagonal ones with the shear stiffness.
                float sin2 = 2 * d2.X * d2.Y / d2.LengthSquared();
                float blend = sin2 * sin2;
                float grade = Math.Clamp(physics.Stretch + (physics.Shear - physics.Stretch) * blend, 0, 100);
                float stiffness = MathF.Pow(10f, 1f + 3f * grade / 100f);
                ca.Add(off + a);
                cb.Add(off + b);
                rest.Add(length);
                compliance.Add(1f / stiffness);

                if (opposite.D >= 0)
                {
                    float across = Vector2.Distance(mesh.Points[opposite.C], mesh.Points[opposite.D]) * 0.01f;
                    ca.Add(off + opposite.C);
                    cb.Add(off + opposite.D);
                    rest.Add(across);
                    compliance.Add(1f / bendStiffness);
                }
            }
        }

        var sa = new List<int>();
        var sb = new List<int>();
        var welds = new List<(int, int)>();
        var ha = new List<int>();
        var hb = new List<int>();
        var hrest = new List<float>();
        var hcompliance = new List<float>();
        static long EdgeKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        foreach (var g in simulated)
        {
            for (int k = 0; k < g.Stitches.Count; k++)
            {
                var stitch = g.Stitches[k];
                var meshA = g.Pieces[stitch.PieceA];
                var meshB = g.Pieces[stitch.PieceB];
                int a = offsets[meshA] + stitch.VertexA;
                int b = offsets[meshB] + stitch.VertexB;
                sa.Add(a);
                sb.Add(b);
                welds.Add((a, b));
                solver._stitched[a] = true;
                solver._stitched[b] = true;

                // A seam is a hinge unless something resists folding across it. Tie the vertices facing each other
                // over the seam together at the distance they would have if both pieces lay flat, just like the
                // bend constraints inside a piece.
                if (k + 1 >= g.Stitches.Count)
                    continue;
                var next = g.Stitches[k + 1];
                if (next.PieceA != stitch.PieceA || next.PieceB != stitch.PieceB
                    || !rims[meshA].TryGetValue(EdgeKey(stitch.VertexA, next.VertexA), out int c)
                    || !rims[meshB].TryGetValue(EdgeKey(stitch.VertexB, next.VertexB), out int d))
                    continue;
                static (float Along, float Height) Local(PieceMesh m, int from, int to, int v)
                {
                    var e = m.Points[to] - m.Points[from];
                    float length = MathF.Max(e.Length(), 1e-6f);
                    var r = m.Points[v] - m.Points[from];
                    return (Vector2.Dot(r, e) / length / length, MathF.Abs(e.X * r.Y - e.Y * r.X) / length);
                }
                var lc = Local(meshA, stitch.VertexA, next.VertexA, c);
                var ld = Local(meshB, stitch.VertexB, next.VertexB, d);
                float edge = 0.5f * (Vector2.Distance(meshA.Points[stitch.VertexA], meshA.Points[next.VertexA])
                                   + Vector2.Distance(meshB.Points[stitch.VertexB], meshB.Points[next.VertexB]));
                float along = (lc.Along - ld.Along) * edge;
                float flat = MathF.Sqrt(along * along + (lc.Height + ld.Height) * (lc.Height + ld.Height)) * 0.01f;
                var pieceA = g.Garment.FindPiece(meshA.PieceId)!;
                var pieceB = g.Garment.FindPiece(meshB.PieceId)!;
                ha.Add(offsets[meshA] + c);
                hb.Add(offsets[meshB] + d);
                hrest.Add(flat);
                hcompliance.Add(2f / (BendStiffness(pieceA.Physics) + BendStiffness(pieceB.Physics)));
            }
        }
        if (settings.SelfCollision)
        {
            foreach (var g in obstacles)
            {
                foreach (var stitch in g.Stitches)
                    welds.Add((offsets[g.Pieces[stitch.PieceA]] + stitch.VertexA, offsets[g.Pieces[stitch.PieceB]] + stitch.VertexB));
            }
            solver.BuildContactTopology(triangles.ToArray(), welds);
        }

        solver._garmentOfPiece = pieces.Select(p => p.Garment).ToArray();
        solver._sewing = new float[garmentCount];
        solver._gap = new float[garmentCount];
        solver._bestGap = Enumerable.Repeat(float.MaxValue, garmentCount).ToArray();
        solver._stalled = new int[garmentCount];
        solver._sewn = new bool[garmentCount];
        solver._pieceShift = new Vector3[pieces.Count];
        solver._pieceStitches = new int[pieces.Count];
        solver._ca = ca.ToArray();
        solver._cb = cb.ToArray();
        solver._rest = rest.ToArray();
        solver._compliance = compliance.ToArray();
        solver._ha = ha.ToArray();
        solver._hb = hb.ToArray();
        solver._hrest = hrest.ToArray();
        solver._hcompliance = hcompliance.ToArray();
        solver._sa = sa.ToArray();
        solver._sb = sb.ToArray();
        return solver;
    }

    private static float BendStiffness(FabricPhysics physics)
        => MathF.Pow(10f, -2f + 3.5f * Math.Clamp(physics.Bend, 0, 100) / 100f);

    /// <summary>Advances the cloth by one display frame (1/60 s of simulated time at time scale 1).</summary>
    public void Step()
    {
        int substeps = Math.Clamp(Settings.Substeps, 1, 64);
        float h = FrameTime * Math.Clamp(Settings.TimeScale, 0.05f, 4f) / substeps;
        var x = Positions;
        int n = x.Length;

        // While a garment's seams are still far apart its pieces are being sewn together; gravity is held back for
        // that garment so the pieces do not fall off the avatar before they are attached. If the seams stop getting
        // closer (a seam that cannot close, such as a garment too tight for the body) the phase ends anyway.
        Array.Clear(_gap);
        Array.Clear(_openStitch);
        for (int c = 0; c < _sa.Length; c++)
        {
            int g = _garmentOfPiece[_pieceOf[_sa[c]]];
            float apart = Vector3.DistanceSquared(x[_sa[c]], x[_sb[c]]);
            _gap[g] = MathF.Max(_gap[g], apart);
            if (apart > 0.002f * 0.002f)
                _openStitch[_sa[c]] = _openStitch[_sb[c]] = true;
        }
        float widest = 0;
        for (int g = 0; g < _gap.Length; g++)
        {
            _gap[g] = MathF.Sqrt(_gap[g]);
            widest = MathF.Max(widest, _gap[g]);
            if (_gap[g] < _bestGap[g] - 0.001f)
            {
                _bestGap[g] = _gap[g];
                _stalled[g] = 0;
            }
            else if (++_stalled[g] > StallFrames)
            {
                _sewn[g] = true;
            }
            _sewing[g] = _sewn[g] ? 0f : Math.Clamp((_gap[g] - 0.01f) / 0.04f, 0f, 1f);
            if (_sewing[g] == 0)
                _sewn[g] = true;
        }
        SeamGap = widest;
        var fullGravity = new Vector3(0, Settings.Gravity, 0);

        // Cloth pairs have to be found before they touch, so fast-moving cloth is searched several times a frame:
        // often enough that nothing travels further than the search distance in between.
        float travel = 2f * PeakSpeed * h * substeps;
        int searchEvery = Math.Max(1, substeps / Math.Clamp((int)MathF.Ceiling(travel / (0.75f * ContactMargin)), 1, substeps));

        for (int step = 0; step < substeps; step++)
        {
            if (Settings.SelfCollision && step % searchEvery == 0)
                FindContacts();
            for (int i = 0; i < n; i++)
            {
                if (_invMass[i] == 0)
                    continue;
                float sewing = _sewing[_garmentOfPiece[_pieceOf[i]]];
                var v = (_velocity[i] + fullGravity * ((1f - sewing) * h)) * MathF.Max(0, 1 - (_drag[i] + 6f * sewing) * h);
                float speed = v.Length();
                if (speed > MaxSpeed)
                    v *= MaxSpeed / speed;
                _previous[i] = x[i];
                x[i] += v * h;
            }

            SolveSeams(h, true);
            SolveDistances(h);
            SolveSeamHinges(h);
            SolveSeams(h, false);
            if (Settings.SelfCollision)
                SolveContacts();
            SolveBody();

            float invH = 1f / h;
            for (int i = 0; i < n; i++)
            {
                if (_invMass[i] != 0)
                    _velocity[i] = (x[i] - _previous[i]) * invH;
            }
        }

        float peak = 0;
        for (int i = 0; i < n; i++)
            peak = MathF.Max(peak, _velocity[i].LengthSquared());
        PeakSpeed = MathF.Sqrt(peak);
    }

    private void SolveDistances(float h)
    {
        var x = Positions;
        float invH2 = 1f / (h * h);
        for (int c = 0; c < _ca.Length; c++)
        {
            int a = _ca[c], b = _cb[c];
            float wa = _invMass[a], wb = _invMass[b];
            float w = wa + wb;
            if (w == 0)
                continue;
            var d = x[b] - x[a];
            float length = d.Length();
            if (length < 1e-9f)
                continue;
            float lambda = (length - _rest[c]) / (w + _compliance[c] * invH2);
            var correction = d * (lambda / length);
            x[a] += correction * wa;
            x[b] -= correction * wb;
        }
    }

    /// <summary>Bending resistance across seams; only once the seam has been pulled shut.</summary>
    private void SolveSeamHinges(float h)
    {
        var x = Positions;
        float invH2 = 1f / (h * h);
        for (int c = 0; c < _ha.Length; c++)
        {
            int a = _ha[c], b = _hb[c];
            float w = _invMass[a] + _invMass[b];
            var d = x[b] - x[a];
            float length = d.Length();
            if (w == 0 || length < 1e-9f || length > _hrest[c] * 1.5f)
                continue;
            float lambda = (length - _hrest[c]) / (w + _hcompliance[c] * invH2);
            var correction = d * (lambda / length);
            x[a] += correction * _invMass[a];
            x[b] -= correction * _invMass[b];
        }
    }

    private void SolveSeams(float h, bool assist)
    {
        var x = Positions;
        float closing = SeamClosingSpeed * h;
        if (assist)
        {
            Array.Clear(_pieceShift);
            Array.Clear(_pieceStitches);
        }
        for (int c = 0; c < _sa.Length; c++)
        {
            int a = _sa[c], b = _sb[c];
            float wa = _invMass[a], wb = _invMass[b];
            float w = wa + wb;
            if (w == 0)
                continue;
            var d = x[b] - x[a];
            float length = d.Length();
            if (length < 1e-9f)
                continue;
            // Stitched particles approach each other at a limited speed, so sewing far-apart edges draws the
            // pieces together gently instead of whipping the cloth around.
            float target = MathF.Max(0, Vector3.Distance(_previous[a], _previous[b]) - closing);
            if (length <= target)
                continue;
            var correction = d * ((length - target) / (length * w));
            x[a] += correction * wa;
            x[b] -= correction * wb;
            if (assist)
            {
                _pieceShift[_pieceOf[a]] += correction * wa;
                _pieceStitches[_pieceOf[a]]++;
                _pieceShift[_pieceOf[b]] -= correction * wb;
                _pieceStitches[_pieceOf[b]]++;
            }
        }
        if (!assist)
            return;
        // While pieces are still being sewn, the rest of each piece follows the average pull on its seams. Without
        // this only the seam edges would race ahead and curl over, because tension spreads slowly through the mesh.
        for (int i = 0; i < x.Length; i++)
        {
            int piece = _pieceOf[i];
            float sewing = _sewing[_garmentOfPiece[piece]];
            if (sewing == 0 || _invMass[i] == 0 || _stitched[i] || _pieceStitches[piece] == 0)
                continue;
            x[i] += _pieceShift[piece] * (sewing / _pieceStitches[piece]);
        }
    }

    private void SolveBody()
    {
        var x = Positions;
        var sdf = Collider;
        bool ground = Settings.GroundPlane;
        for (int i = 0; i < x.Length; i++)
        {
            if (_invMass[i] == 0)
                continue;
            if (sdf != null)
            {
                float distance = sdf.Sample(x[i], out var normal);
                float depth = _thickness[i] - distance;
                if (depth > 0)
                {
                    x[i] += normal * depth;
                    ApplyFriction(i, normal, depth);
                }
            }
            if (ground && x[i].Y < _thickness[i])
            {
                float depth = _thickness[i] - x[i].Y;
                x[i].Y = _thickness[i];
                ApplyFriction(i, Vector3.UnitY, depth);
            }
        }
    }

    private void ApplyFriction(int i, Vector3 normal, float depth)
    {
        // A stitch that is still open slides freely: thread tension beats friction, and otherwise two stitched
        // points resting on opposite sides of a ridge (a shoulder) would never meet.
        if (_openStitch[i])
            return;
        var move = Positions[i] - _previous[i];
        var tangent = move - normal * Vector3.Dot(move, normal);
        float slide = tangent.Length();
        if (slide < 1e-9f)
            return;
        float grip = _friction[i] * depth;
        Positions[i] -= slide <= grip ? tangent : tangent * (grip / slide);
    }

    /// <summary>Copies the current particle positions of a piece into a new drape array.</summary>
    public float[] ExtractDrape(ClothPiece piece)
    {
        var drape = new float[piece.Mesh.VertexCount * 3];
        for (int i = 0; i < piece.Mesh.VertexCount; i++)
        {
            var p = Positions[piece.Offset + i];
            drape[3 * i] = p.X;
            drape[3 * i + 1] = p.Y;
            drape[3 * i + 2] = p.Z;
        }
        return drape;
    }
}
