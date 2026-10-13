using System.Numerics;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;

namespace Seamstress.Core.IO;

public sealed class ExportOptions
{
    /// <summary>Merge the vertices joined by seams so each garment becomes one connected surface.</summary>
    public bool WeldSeams { get; set; } = true;

    /// <summary>Restrict the export to these garments; null exports every visible garment.</summary>
    public IReadOnlyCollection<string>? GarmentIds { get; set; }

    public bool IncludeHidden { get; set; }
}

/// <summary>Triangles of one pattern piece inside an <see cref="ExportMesh"/>.</summary>
public sealed class ExportPart
{
    public required string Name { get; init; }
    public required FabricMaterial Material { get; init; }

    /// <summary>Three position indices per triangle.</summary>
    public required int[] PositionIndices { get; init; }

    /// <summary>Three UV indices per triangle, parallel to <see cref="PositionIndices"/>.</summary>
    public required int[] UvIndices { get; init; }

    /// <summary>
    /// The same surface as polygons (3 or 4 corners each) for formats that can store quads: the number of corners
    /// of each face, and the position and UV index of every corner in face order.
    /// </summary>
    public required int[] FaceSizes { get; init; }
    public required int[] FacePositionIndices { get; init; }
    public required int[] FaceUvIndices { get; init; }
}

/// <summary>One garment ready for export. Positions are metres, Y up; UVs have V up.</summary>
public sealed class ExportMesh
{
    public required string Name { get; init; }
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public required Vector2[] Uvs { get; init; }
    public required List<ExportPart> Parts { get; init; }

    public int TriangleCount => Parts.Sum(p => p.PositionIndices.Length / 3);
    public int FaceCount => Parts.Sum(p => p.FaceSizes.Length);
}

/// <summary>Format-independent description of what gets exported; every exporter starts from this.</summary>
public sealed class ExportScene
{
    public static readonly FabricMaterial DefaultMaterial = new() { Id = "default", Name = "Default Fabric" };

    /// <summary>Stitched vertices closer than this (metres) are merged when welding.</summary>
    public const float WeldDistance = 0.015f;

    public required List<ExportMesh> Meshes { get; init; }

    public IEnumerable<FabricMaterial> Materials()
        => Meshes.SelectMany(m => m.Parts).Select(p => p.Material).Distinct();

    public static ExportScene Build(Document document, Func<Garment, GarmentMesh> meshFor, ExportOptions options)
    {
        var meshes = new List<ExportMesh>();
        foreach (var garment in document.Garments)
        {
            if (options.GarmentIds != null ? !options.GarmentIds.Contains(garment.Id) : (!garment.Visible && !options.IncludeHidden))
                continue;
            var mesh = BuildGarment(document, garment, meshFor(garment), options);
            if (mesh != null)
                meshes.Add(mesh);
        }
        return new ExportScene { Meshes = meshes };
    }

    private static ExportMesh? BuildGarment(Document document, Garment garment, GarmentMesh garmentMesh, ExportOptions options)
    {
        var layout = UvLayout.For(garment);
        var offsets = new int[garmentMesh.Pieces.Count];
        int total = 0;
        for (int i = 0; i < offsets.Length; i++)
        {
            offsets[i] = total;
            total += garmentMesh.Pieces[i].VertexCount;
        }
        if (total == 0)
            return null;

        var raw = new Vector3[total];
        var uvs = new Vector2[total];
        for (int i = 0; i < offsets.Length; i++)
        {
            var pieceMesh = garmentMesh.Pieces[i];
            var piece = garment.FindPiece(pieceMesh.PieceId)!;
            Mesher.CurrentPositions(piece, pieceMesh).CopyTo(raw, offsets[i]);
            layout.Map(piece, pieceMesh).CopyTo(uvs, offsets[i]);
        }

        // Union the vertices joined by stitches; each set becomes one exported position.
        var parent = new int[total];
        for (int i = 0; i < total; i++)
            parent[i] = i;
        int Find(int a)
        {
            while (parent[a] != a)
                a = parent[a] = parent[parent[a]];
            return a;
        }
        if (options.WeldSeams)
        {
            foreach (var stitch in garmentMesh.Stitches)
            {
                int va = offsets[stitch.PieceA] + stitch.VertexA, vb = offsets[stitch.PieceB] + stitch.VertexB;
                // Only stitches the simulation has actually closed are merged; a garment exported before it was
                // simulated keeps its pieces separate instead of being stretched across the gap.
                if (Vector3.Distance(raw[va], raw[vb]) > WeldDistance)
                    continue;
                int a = Find(va), b = Find(vb);
                if (a != b)
                    parent[Math.Max(a, b)] = Math.Min(a, b);
            }
        }
        var remap = new int[total];
        var sums = new List<Vector3>();
        var counts = new List<int>();
        var rootIndex = new Dictionary<int, int>();
        for (int i = 0; i < total; i++)
        {
            int root = Find(i);
            if (!rootIndex.TryGetValue(root, out int index))
            {
                index = sums.Count;
                rootIndex[root] = index;
                sums.Add(Vector3.Zero);
                counts.Add(0);
            }
            remap[i] = index;
            sums[index] += raw[i];
            counts[index]++;
        }
        var positions = new Vector3[sums.Count];
        for (int i = 0; i < positions.Length; i++)
            positions[i] = sums[i] / counts[i];

        var parts = new List<ExportPart>();
        var allTriangles = new List<int>();
        for (int i = 0; i < offsets.Length; i++)
        {
            var pieceMesh = garmentMesh.Pieces[i];
            var piece = garment.FindPiece(pieceMesh.PieceId)!;
            if (!piece.Visible && !options.IncludeHidden)
                continue;
            var positionIndices = new int[pieceMesh.Triangles.Length];
            var uvIndices = new int[pieceMesh.Triangles.Length];
            for (int k = 0; k < positionIndices.Length; k++)
            {
                uvIndices[k] = offsets[i] + pieceMesh.Triangles[k];
                positionIndices[k] = remap[uvIndices[k]];
            }
            allTriangles.AddRange(positionIndices);
            var sizes = new List<int>();
            var facePositions = new List<int>();
            var faceUvs = new List<int>();
            foreach (var face in pieceMesh.Faces())
            {
                sizes.Add(face.Length);
                foreach (int v in face)
                {
                    faceUvs.Add(offsets[i] + v);
                    facePositions.Add(remap[offsets[i] + v]);
                }
            }
            parts.Add(new ExportPart
            {
                Name = piece.Name,
                Material = document.FindMaterial(piece.MaterialId) ?? DefaultMaterial,
                PositionIndices = positionIndices,
                UvIndices = uvIndices,
                FaceSizes = sizes.ToArray(),
                FacePositionIndices = facePositions.ToArray(),
                FaceUvIndices = faceUvs.ToArray(),
            });
        }
        if (parts.Count == 0)
            return null;

        return new ExportMesh
        {
            Name = garment.Name,
            Positions = positions,
            Normals = Mesher.ComputeNormals(positions, allTriangles.ToArray()),
            Uvs = uvs,
            Parts = parts,
        };
    }

    /// <summary>Parses "#RRGGBB" into linear-ish 0-1 components (no gamma conversion).</summary>
    public static (float R, float G, float B) ParseColor(string? hex)
    {
        if (hex != null && hex.Length == 7 && hex[0] == '#'
            && int.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out int rgb))
            return (((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);
        return (0.8f, 0.8f, 0.8f);
    }

    public static string SafeName(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray();
        var safe = new string(chars).Trim('_');
        return safe.Length == 0 ? "garment" : safe;
    }
}
