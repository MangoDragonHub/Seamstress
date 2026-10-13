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
public sealed class ClothSolver
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
    private readonly float[] _radius;
    private readonly bool[] _stitched;
    private readonly int[] _pieceOf;
    private readonly Vector2[] _rest2D;

    private int[] _ca = Array.Empty<int>(), _cb = Array.Empty<int>();
    private float[] _rest = Array.Empty<float>(), _compliance = Array.Empty<float>();
    private int[] _sa = Array.Empty<int>(), _sb = Array.Empty<int>();
    private readonly List<(int A, int B)> _contacts = new();
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
        _radius = new float[count];
        _stitched = new bool[count];
        _pieceOf = new int[count];
        _rest2D = new Vector2[count];
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
            var garment = (cloth.Simulated ? simulated : obstacles).First(g => g.Pieces.Contains(mesh)).Garment;
            float spacing = garment.EffectiveParticleDistance(cloth.Piece) * 0.01f;

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
                solver._radius[p] = spacing * 0.45f;
                solver._pieceOf[p] = pi;
                solver._rest2D[p] = mesh.Points[i];
            }
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

            float bendStiffness = MathF.Pow(10f, -2f + 3.5f * Math.Clamp(physics.Bend, 0, 100) / 100f);
            foreach (var (key, opposite) in edges)
            {
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
        foreach (var g in simulated)
        {
            foreach (var stitch in g.Stitches)
            {
                int a = offsets[g.Pieces[stitch.PieceA]] + stitch.VertexA;
                int b = offsets[g.Pieces[stitch.PieceB]] + stitch.VertexB;
                sa.Add(a);
                sb.Add(b);
                solver._stitched[a] = true;
                solver._stitched[b] = true;
            }
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
        solver._sa = sa.ToArray();
        solver._sb = sb.ToArray();
        return solver;
    }

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
        for (int c = 0; c < _sa.Length; c++)
        {
            int g = _garmentOfPiece[_pieceOf[_sa[c]]];
            _gap[g] = MathF.Max(_gap[g], Vector3.DistanceSquared(x[_sa[c]], x[_sb[c]]));
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

        if (Settings.SelfCollision)
            FindContacts();

        for (int step = 0; step < substeps; step++)
        {
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
        var move = Positions[i] - _previous[i];
        var tangent = move - normal * Vector3.Dot(move, normal);
        float slide = tangent.Length();
        if (slide < 1e-9f)
            return;
        float grip = _friction[i] * depth;
        Positions[i] -= slide <= grip ? tangent : tangent * (grip / slide);
    }

    private void FindContacts()
    {
        _contacts.Clear();
        var x = Positions;
        int n = x.Length;
        if (n == 0)
            return;
        float maxRadius = 0;
        for (int i = 0; i < n; i++)
            maxRadius = MathF.Max(maxRadius, _radius[i]);
        float cell = MathF.Max(maxRadius * 2.5f, 1e-3f);
        var buckets = new Dictionary<long, List<int>>();
        long Key(int cx, int cy, int cz) => ((long)(cx & 0x1FFFFF) << 42) | ((long)(cy & 0x1FFFFF) << 21) | (long)(cz & 0x1FFFFF);
        for (int i = 0; i < n; i++)
        {
            long key = Key((int)MathF.Floor(x[i].X / cell), (int)MathF.Floor(x[i].Y / cell), (int)MathF.Floor(x[i].Z / cell));
            if (!buckets.TryGetValue(key, out var list))
                buckets[key] = list = new List<int>(4);
            list.Add(i);
        }
        for (int i = 0; i < n; i++)
        {
            int cx = (int)MathF.Floor(x[i].X / cell), cy = (int)MathF.Floor(x[i].Y / cell), cz = (int)MathF.Floor(x[i].Z / cell);
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (!buckets.TryGetValue(Key(cx + dx, cy + dy, cz + dz), out var list))
                            continue;
                        foreach (int j in list)
                        {
                            if (j <= i || (_invMass[i] == 0 && _invMass[j] == 0))
                                continue;
                            float reach = (_radius[i] + _radius[j]) * 1.25f;
                            if (Vector3.DistanceSquared(x[i], x[j]) > reach * reach)
                                continue;
                            if (_pieceOf[i] == _pieceOf[j])
                            {
                                // Particles that are neighbours in the flat pattern are held apart by the cloth itself.
                                if (Vector2.Distance(_rest2D[i], _rest2D[j]) * 0.01f < (_radius[i] + _radius[j]) * 1.05f)
                                    continue;
                            }
                            else if (_stitched[i] && _stitched[j])
                            {
                                continue;
                            }
                            _contacts.Add((i, j));
                        }
                    }
                }
            }
        }
    }

    private void SolveContacts()
    {
        var x = Positions;
        foreach (var (a, b) in _contacts)
        {
            float wa = _invMass[a], wb = _invMass[b];
            float w = wa + wb;
            if (w == 0)
                continue;
            var d = x[b] - x[a];
            float length = d.Length();
            float min = _radius[a] + _radius[b];
            if (length >= min || length < 1e-9f)
                continue;
            var correction = d * ((min - length) / (length * w));
            x[a] -= correction * wa;
            x[b] += correction * wb;
        }
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
