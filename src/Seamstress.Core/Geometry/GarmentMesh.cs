using System.Numerics;
using Seamstress.Core.Model;

namespace Seamstress.Core.Geometry;

/// <summary>Triangle mesh of one pattern piece.</summary>
public sealed class PieceMesh
{
    public required string PieceId { get; init; }

    /// <summary>Vertices in the piece's local pattern space (cm). Boundary vertices come first.</summary>
    public required Vector2[] Points { get; init; }

    /// <summary>Counter-clockwise triangles.</summary>
    public required int[] Triangles { get; init; }

    /// <summary>For each outline edge, its vertices from the edge's start point to its end point.</summary>
    public required int[][] EdgeChains { get; init; }

    public required int BoundaryCount { get; init; }

    /// <summary>Fingerprint of the vertex layout; a stored drape is only valid for the same key.</summary>
    public required string Key { get; init; }

    public int VertexCount => Points.Length;
    public int TriangleCount => Triangles.Length / 3;
}

/// <summary>A pair of mesh vertices joined by a seam.</summary>
public readonly record struct Stitch(int PieceA, int VertexA, int PieceB, int VertexB);

/// <summary>Meshes of every piece in a garment plus the vertex pairs its seams join.</summary>
public sealed class GarmentMesh
{
    public required Garment Garment { get; init; }
    public required List<PieceMesh> Pieces { get; init; }
    public required List<Stitch> Stitches { get; init; }

    public int TriangleCount => Pieces.Sum(p => p.TriangleCount);
    public int VertexCount => Pieces.Sum(p => p.VertexCount);

    public PieceMesh? Find(string pieceId) => Pieces.FirstOrDefault(p => p.PieceId == pieceId);
}

public static class Mesher
{
    /// <summary>Builds simulation meshes for every piece of a garment at its current particle distance.</summary>
    public static GarmentMesh Build(Garment garment)
    {
        var pieces = garment.Pieces.Where(p => p.Points.Count >= 3).ToList();
        var pieceIndex = new Dictionary<string, int>();
        for (int i = 0; i < pieces.Count; i++)
            pieceIndex[pieces[i].Id] = i;

        // Sewn edges must have the same number of segments so their vertices pair up one to one.
        var edgeOffset = new int[pieces.Count + 1];
        for (int i = 0; i < pieces.Count; i++)
            edgeOffset[i + 1] = edgeOffset[i] + pieces[i].EdgeCount;
        var segments = new int[edgeOffset[^1]];
        var parent = new int[segments.Length];
        for (int i = 0; i < pieces.Count; i++)
        {
            float distance = garment.EffectiveParticleDistance(pieces[i]);
            for (int e = 0; e < pieces[i].EdgeCount; e++)
            {
                int id = edgeOffset[i] + e;
                parent[id] = id;
                float length = Outline.EdgeLength(pieces[i], e);
                int n = Math.Max(1, (int)MathF.Ceiling(length / distance - 1e-3f));
                if (Outline.IsCurved(pieces[i], e))
                    n = Math.Max(n, 3);
                segments[id] = Math.Min(n, 2000);
            }
        }

        int Find(int a)
        {
            while (parent[a] != a)
                a = parent[a] = parent[parent[a]];
            return a;
        }

        var validSeams = new List<(Seam Seam, int A, int B)>();
        foreach (var seam in garment.Seams)
        {
            if (!pieceIndex.TryGetValue(seam.A.PieceId, out int pa) || !pieceIndex.TryGetValue(seam.B.PieceId, out int pb))
                continue;
            if (seam.A.Edge < 0 || seam.A.Edge >= pieces[pa].EdgeCount || seam.B.Edge < 0 || seam.B.Edge >= pieces[pb].EdgeCount)
                continue;
            int a = edgeOffset[pa] + seam.A.Edge, b = edgeOffset[pb] + seam.B.Edge;
            if (a == b)
                continue;
            validSeams.Add((seam, a, b));
            int ra = Find(a), rb = Find(b);
            if (ra != rb)
            {
                parent[rb] = ra;
                segments[ra] = Math.Max(segments[ra], segments[rb]);
            }
        }
        for (int i = 0; i < segments.Length; i++)
            segments[i] = segments[Find(i)];

        var meshes = new List<PieceMesh>(pieces.Count);
        for (int i = 0; i < pieces.Count; i++)
        {
            var counts = new int[pieces[i].EdgeCount];
            Array.Copy(segments, edgeOffset[i], counts, 0, counts.Length);
            meshes.Add(BuildPiece(pieces[i], counts, garment.EffectiveParticleDistance(pieces[i])));
        }

        var stitches = new List<Stitch>();
        foreach (var (seam, a, b) in validSeams)
        {
            int pa = pieceIndex[seam.A.PieceId], pb = pieceIndex[seam.B.PieceId];
            var chainA = meshes[pa].EdgeChains[seam.A.Edge];
            var chainB = meshes[pb].EdgeChains[seam.B.Edge];
            int n = Math.Min(chainA.Length, chainB.Length);
            for (int k = 0; k < n; k++)
            {
                int va = chainA[k];
                int vb = seam.Reversed ? chainB[k] : chainB[chainB.Length - 1 - k];
                if (pa == pb && va == vb)
                    continue;
                stitches.Add(new Stitch(pa, va, pb, vb));
            }
        }

        return new GarmentMesh { Garment = garment, Pieces = meshes, Stitches = stitches };
    }

    /// <summary>Triangulates one piece with the given number of segments on each outline edge.</summary>
    public static PieceMesh BuildPiece(PatternPiece piece, int[] edgeSegments, float particleDistance)
    {
        int edgeCount = piece.EdgeCount;
        var boundary = new List<Vector2>();
        var chains = new int[edgeCount][];
        var starts = new int[edgeCount];
        var samples = new Vector2[edgeCount][];
        for (int e = 0; e < edgeCount; e++)
        {
            starts[e] = boundary.Count;
            samples[e] = Outline.SampleEdge(piece, e, edgeSegments[e]);
            for (int k = 0; k < edgeSegments[e]; k++)
                boundary.Add(samples[e][k]);
        }
        int boundaryCount = boundary.Count;
        for (int e = 0; e < edgeCount; e++)
        {
            var chain = new int[edgeSegments[e] + 1];
            for (int k = 0; k < edgeSegments[e]; k++)
                chain[k] = starts[e] + k;
            chain[^1] = starts[(e + 1) % edgeCount];
            chains[e] = chain;
        }

        float h = particleDistance;
        float orientation = Outline.SignedArea(boundary) >= 0 ? 1f : -1f;

        // Hexagonal lattice of interior points, kept clear of the outline so boundary edges survive triangulation.
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var p in boundary)
        {
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p);
        }
        var points = new List<Vector2>(boundary);
        var centre = (min + max) / 2;
        float rowHeight = h * 0.8660254f;
        int rows = (int)MathF.Ceiling((max.Y - min.Y) / rowHeight / 2) + 1;
        int cols = (int)MathF.Ceiling((max.X - min.X) / h / 2) + 1;
        float clearance = h * 0.55f;
        if ((long)rows * cols < 4_000_000)
        {
            for (int r = -rows; r <= rows; r++)
            {
                float y = centre.Y + r * rowHeight;
                float shift = (r & 1) == 0 ? 0 : h * 0.5f;
                for (int c = -cols; c <= cols; c++)
                {
                    var p = new Vector2(centre.X + c * h + shift, y);
                    if (p.X < min.X || p.X > max.X || p.Y < min.Y || p.Y > max.Y)
                        continue;
                    if (!Outline.Contains(boundary, p))
                        continue;
                    if (Outline.DistanceToPolyline(p, boundary, true) < clearance)
                        continue;
                    points.Add(p);
                }
            }
        }

        // Triangulate slightly perturbed copies of the points: collinear boundary samples are bowed inwards so they
        // never form zero-area triangles inside the piece, and lattice points are jittered out of exact alignment.
        int total = points.Count;
        var xs = new double[total];
        var ys = new double[total];
        for (int e = 0; e < edgeCount; e++)
        {
            int n = edgeSegments[e];
            var a = samples[e][0];
            var b = samples[e][n];
            var dir = b - a;
            float len = dir.Length();
            var inward = len > 1e-9f ? new Vector2(-dir.Y, dir.X) / len * orientation : Vector2.Zero;
            for (int k = 0; k < n; k++)
            {
                double t = k / (double)n;
                double bow = 4 * t * (1 - t) * h * 1e-5;
                xs[starts[e] + k] = samples[e][k].X + inward.X * bow;
                ys[starts[e] + k] = samples[e][k].Y + inward.Y * bow;
            }
        }
        uint seed = 0x9E3779B9;
        for (int i = boundaryCount; i < total; i++)
        {
            seed = seed * 1664525 + 1013904223;
            double jx = ((seed >> 8) & 0xFFFF) / 65535.0 - 0.5;
            seed = seed * 1664525 + 1013904223;
            double jy = ((seed >> 8) & 0xFFFF) / 65535.0 - 0.5;
            xs[i] = points[i].X + jx * h * 2e-3;
            ys[i] = points[i].Y + jy * h * 2e-3;
        }

        var raw = Delaunay.Triangulate(xs, ys);
        var displaced = new Vector2[boundaryCount];
        for (int i = 0; i < boundaryCount; i++)
            displaced[i] = new Vector2((float)xs[i], (float)ys[i]);

        var triangles = new List<int>(raw.Length);
        double minArea = (double)h * h * 1e-3;
        for (int t = 0; t < raw.Length; t += 3)
        {
            int a = raw[t], b = raw[t + 1], c = raw[t + 2];
            double area = ((xs[b] - xs[a]) * (ys[c] - ys[a]) - (ys[b] - ys[a]) * (xs[c] - xs[a])) * 0.5;
            if (area < minArea)
                continue;
            var centroid = new Vector2((float)((xs[a] + xs[b] + xs[c]) / 3), (float)((ys[a] + ys[b] + ys[c]) / 3));
            if (!Outline.Contains(displaced, centroid))
                continue;
            triangles.Add(a);
            triangles.Add(b);
            triangles.Add(c);
        }

        var pointArray = points.ToArray();
        return new PieceMesh
        {
            PieceId = piece.Id,
            Points = pointArray,
            Triangles = triangles.ToArray(),
            EdgeChains = chains,
            BoundaryCount = boundaryCount,
            Key = Fingerprint(pointArray, triangles.Count),
        };
    }

    private static string Fingerprint(Vector2[] points, int indexCount)
    {
        ulong hash = 14695981039346656037UL;
        void Mix(int value)
        {
            hash ^= (uint)value;
            hash *= 1099511628211UL;
        }
        foreach (var p in points)
        {
            Mix((int)MathF.Round(p.X * 100));
            Mix((int)MathF.Round(p.Y * 100));
        }
        Mix(indexCount);
        return $"{points.Length}-{hash:x}";
    }

    /// <summary>Positions of a flat piece mesh placed in 3D (metres) according to the piece's placement.</summary>
    public static Vector3[] Arrange(PatternPiece piece, PieceMesh mesh)
    {
        var (min, max) = Outline.Bounds(piece);
        var centre = (min + max) / 2;
        float width = MathF.Max((max.X - min.X) * 0.01f, 1e-4f);
        var placement = piece.Placement;
        float arc = placement.Curvature * MathF.PI / 180f;
        var rotation = Matrix4x4.CreateRotationX(placement.Rotation.X * MathF.PI / 180f)
                     * Matrix4x4.CreateRotationY(placement.Rotation.Y * MathF.PI / 180f)
                     * Matrix4x4.CreateRotationZ(placement.Rotation.Z * MathF.PI / 180f);
        var result = new Vector3[mesh.Points.Length];
        for (int i = 0; i < result.Length; i++)
        {
            float x = (mesh.Points[i].X - centre.X) * 0.01f;
            float y = (mesh.Points[i].Y - centre.Y) * 0.01f;
            Vector3 local;
            if (MathF.Abs(arc) > 1e-3f)
            {
                float radius = width / arc;
                float angle = x / radius;
                local = new Vector3(radius * MathF.Sin(angle), y, -radius * (1 - MathF.Cos(angle)));
            }
            else
            {
                local = new Vector3(x, y, 0);
            }
            result[i] = Vector3.Transform(local, rotation) + placement.Position;
        }
        return result;
    }

    /// <summary>Current 3D positions of a piece: its stored drape if still valid, otherwise the flat arrangement.</summary>
    public static Vector3[] CurrentPositions(PatternPiece piece, PieceMesh mesh)
    {
        if (piece.Drape != null && piece.DrapeKey == mesh.Key && piece.Drape.Length == mesh.Points.Length * 3)
        {
            var result = new Vector3[mesh.Points.Length];
            for (int i = 0; i < result.Length; i++)
                result[i] = new Vector3(piece.Drape[3 * i], piece.Drape[3 * i + 1], piece.Drape[3 * i + 2]);
            return result;
        }
        return Arrange(piece, mesh);
    }

    public static Vector3[] ComputeNormals(Vector3[] positions, int[] triangles)
    {
        var normals = new Vector3[positions.Length];
        for (int t = 0; t < triangles.Length; t += 3)
        {
            int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
            var n = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            normals[a] += n;
            normals[b] += n;
            normals[c] += n;
        }
        for (int i = 0; i < normals.Length; i++)
        {
            float len = normals[i].Length();
            normals[i] = len > 1e-12f ? normals[i] / len : Vector3.UnitZ;
        }
        return normals;
    }
}
