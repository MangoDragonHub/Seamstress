using System.Globalization;
using System.Numerics;
using System.Text;
using Seamstress.Core.Simulation;

namespace Seamstress.Core.IO;

/// <summary>
/// Writes a garment as a MakeHuman clothes asset (.mhclo + .obj + .mhmat). Each garment vertex is bound to a triangle
/// of the MakeHuman base mesh, so MakeHuman can refit the garment when the character's shape changes.
/// </summary>
public static class MakeHumanExporter
{
    // Base mesh vertex pairs MakeHuman uses to measure body size along each axis (as written by MakeClothes).
    private static readonly (int A, int B)[] ScaleVertices = { (5399, 11998), (791, 881), (962, 5320) };

    public sealed record Result(string Folder, string MhcloPath, int VertexCount);

    /// <param name="clothesFolder">MakeHuman's "data/clothes" folder inside its user data directory.</param>
    public static Result Export(ExportMesh mesh, MakeHumanBase baseMesh, string clothesFolder, string? author = null)
    {
        if (mesh.Parts.Count == 0 || mesh.Positions.Length == 0)
            throw new InvalidOperationException("The garment has no geometry to send.");
        string name = ExportScene.SafeName(mesh.Name);
        string folder = Path.Combine(clothesFolder, name.ToLowerInvariant());
        Directory.CreateDirectory(folder);
        string fileBase = name.ToLowerInvariant();
        var inv = CultureInfo.InvariantCulture;

        // MakeHuman assets carry one UV per vertex, so vertices on UV seams are duplicated.
        var lookup = new Dictionary<(int, int), int>();
        var positions = new List<Vector3>();
        var uvs = new List<Vector2>();
        var faces = new List<int>();
        foreach (var part in mesh.Parts)
        {
            for (int k = 0; k < part.PositionIndices.Length; k++)
            {
                var key = (part.PositionIndices[k], part.UvIndices[k]);
                if (!lookup.TryGetValue(key, out int index))
                {
                    index = positions.Count;
                    lookup[key] = index;
                    positions.Add(baseMesh.ToMakeHuman(mesh.Positions[key.Item1]));
                    uvs.Add(mesh.Uvs[key.Item2]);
                }
                faces.Add(index);
            }
        }

        var obj = new StringBuilder();
        obj.Append("# Exported by Seamstress for MakeHuman\n");
        foreach (var p in positions)
            obj.Append(inv, $"v {p.X:0.####} {p.Y:0.####} {p.Z:0.####}\n");
        foreach (var uv in uvs)
            obj.Append(inv, $"vt {uv.X:0.######} {uv.Y:0.######}\n");
        obj.Append("g ").Append(name).Append('\n');
        for (int t = 0; t < faces.Count; t += 3)
            obj.Append(inv, $"f {faces[t] + 1}/{faces[t] + 1} {faces[t + 1] + 1}/{faces[t + 1] + 1} {faces[t + 2] + 1}/{faces[t + 2] + 1}\n");
        File.WriteAllText(Path.Combine(folder, fileBase + ".obj"), obj.ToString());

        var locator = new TriangleLocator(baseMesh.Vertices, baseMesh.BodyTriangles);
        var clo = new StringBuilder();
        clo.Append("# Exported by Seamstress\n");
        clo.Append("author ").Append(string.IsNullOrWhiteSpace(author) ? Environment.UserName : author).Append('\n');
        clo.Append("license CC0\n");
        clo.Append("basemesh hm08\n");
        clo.Append("name ").Append(mesh.Name).Append('\n');
        clo.Append("uuid ").Append(StableUuid(name)).Append('\n');
        clo.Append("tag Seamstress\n");
        clo.Append("version 110\n");
        clo.Append("obj_file ").Append(fileBase).Append(".obj\n");
        string[] axes = { "x_scale", "y_scale", "z_scale" };
        for (int axis = 0; axis < 3; axis++)
        {
            var (a, b) = ScaleVertices[axis];
            if (a >= baseMesh.Vertices.Length || b >= baseMesh.Vertices.Length)
                throw new InvalidOperationException("The linked base mesh is not the MakeHuman hm08 base mesh.");
            var d = baseMesh.Vertices[a] - baseMesh.Vertices[b];
            float distance = MathF.Abs(axis == 0 ? d.X : axis == 1 ? d.Y : d.Z);
            clo.Append(inv, $"{axes[axis]} {a} {b} {distance:0.0000}\n");
        }
        clo.Append("z_depth 50\n");
        clo.Append("max_pole 8\n");
        clo.Append("material ").Append(fileBase).Append(".mhmat\n");
        clo.Append("verts 0\n");
        foreach (var p in positions)
        {
            var hit = locator.Closest(p);
            // The scale lines above equal 1 on the base mesh, so the offset is the plain distance from the triangle.
            var offset = p - hit.Point;
            clo.Append(inv, $"{hit.A} {hit.B} {hit.C} {hit.Wa:0.00000} {hit.Wb:0.00000} {hit.Wc:0.00000} {offset.X:0.00000} {offset.Y:0.00000} {offset.Z:0.00000}\n");
        }
        string mhcloPath = Path.Combine(folder, fileBase + ".mhclo");
        File.WriteAllText(mhcloPath, clo.ToString());

        var material = mesh.Parts[0].Material;
        var (r, g, b2) = ExportScene.ParseColor(material.Color);
        var mat = new StringBuilder();
        mat.Append("# Material for ").Append(mesh.Name).Append(" (exported by Seamstress)\n\n");
        mat.Append("name ").Append(name).Append('\n');
        mat.Append("description ").Append(material.Name).Append("\n\n");
        mat.Append("ambientColor 0.5 0.5 0.5\n");
        mat.Append(inv, $"diffuseColor {r:0.####} {g:0.####} {b2:0.####}\n");
        mat.Append(inv, $"specularColor {(1 - material.Roughness) * 0.3f:0.####} {(1 - material.Roughness) * 0.3f:0.####} {(1 - material.Roughness) * 0.3f:0.####}\n");
        mat.Append(inv, $"shininess {1 - material.Roughness:0.####}\n");
        mat.Append("emissiveColor 0.0 0.0 0.0\n");
        mat.Append(inv, $"opacity {material.Opacity:0.####}\n");
        mat.Append("translucency 0.0\n\nshadeless False\nwireframe False\n");
        mat.Append("transparent ").Append(material.Opacity < 0.999f ? "True" : "False").Append('\n');
        mat.Append("alphaToCoverage True\nbackfaceCull False\ndepthless False\n\ncastShadows True\nreceiveShadows True\n\n");
        string? texture = TextureFiles.CopyNextTo(material.Texture, folder);
        if (texture != null)
            mat.Append("diffuseTexture ").Append(texture).Append("\n\n");
        mat.Append("shaderConfig diffuse True\nshaderConfig bump False\nshaderConfig normal False\nshaderConfig displacement False\n");
        mat.Append("shaderConfig spec False\nshaderConfig vertexColors False\n");
        File.WriteAllText(Path.Combine(folder, fileBase + ".mhmat"), mat.ToString());

        return new Result(folder, mhcloPath, positions.Count);
    }

    /// <summary>The same garment name always gets the same id, so re-sending replaces the asset instead of duplicating it.</summary>
    private static string StableUuid(string name)
    {
        var hash = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes("seamstress:" + name));
        return new Guid(hash).ToString();
    }
}

/// <summary>Finds the closest triangle of a mesh to a point, using a uniform grid to avoid testing every triangle.</summary>
public sealed class TriangleLocator
{
    public readonly record struct Hit(int A, int B, int C, float Wa, float Wb, float Wc, Vector3 Point, float Distance);

    private readonly Vector3[] _positions;
    private readonly int[] _triangles;
    private readonly Dictionary<(int, int, int), List<int>> _cells = new();
    private readonly float _cell;
    private readonly int _maxRings;

    public TriangleLocator(Vector3[] positions, int[] triangles)
    {
        _positions = positions;
        _triangles = triangles;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        double edgeSum = 0;
        for (int t = 0; t < triangles.Length; t += 3)
        {
            var a = positions[triangles[t]];
            var b = positions[triangles[t + 1]];
            var c = positions[triangles[t + 2]];
            min = Vector3.Min(min, Vector3.Min(a, Vector3.Min(b, c)));
            max = Vector3.Max(max, Vector3.Max(a, Vector3.Max(b, c)));
            edgeSum += Vector3.Distance(a, b);
        }
        int count = Math.Max(1, triangles.Length / 3);
        _cell = MathF.Max((float)(edgeSum / count) * 2f, 1e-5f);
        var size = max - min;
        _maxRings = (int)(MathF.Max(size.X, MathF.Max(size.Y, size.Z)) / _cell) + 2;
        for (int t = 0; t < triangles.Length; t += 3)
        {
            var a = positions[triangles[t]];
            var b = positions[triangles[t + 1]];
            var c = positions[triangles[t + 2]];
            var lo = Cell(Vector3.Min(a, Vector3.Min(b, c)));
            var hi = Cell(Vector3.Max(a, Vector3.Max(b, c)));
            for (int z = lo.Item3; z <= hi.Item3; z++)
                for (int y = lo.Item2; y <= hi.Item2; y++)
                    for (int x = lo.Item1; x <= hi.Item1; x++)
                    {
                        if (!_cells.TryGetValue((x, y, z), out var list))
                            _cells[(x, y, z)] = list = new List<int>();
                        list.Add(t);
                    }
        }
    }

    private (int, int, int) Cell(Vector3 p)
        => ((int)MathF.Floor(p.X / _cell), (int)MathF.Floor(p.Y / _cell), (int)MathF.Floor(p.Z / _cell));

    public Hit Closest(Vector3 p)
    {
        var centre = Cell(p);
        var best = new Hit(0, 0, 0, 1, 0, 0, Vector3.Zero, float.MaxValue);
        for (int ring = 0; ring <= _maxRings * 2; ring++)
        {
            // Anything in a farther ring is at least (ring - 1) cells away, so stop once that cannot beat the best hit.
            if (best.Distance < (ring - 1) * _cell)
                break;
            for (int dz = -ring; dz <= ring; dz++)
            {
                for (int dy = -ring; dy <= ring; dy++)
                {
                    for (int dx = -ring; dx <= ring; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz))) != ring)
                            continue;
                        if (!_cells.TryGetValue((centre.Item1 + dx, centre.Item2 + dy, centre.Item3 + dz), out var list))
                            continue;
                        foreach (int t in list)
                        {
                            int a = _triangles[t], b = _triangles[t + 1], c = _triangles[t + 2];
                            var q = SdfGrid.ClosestPointOnTriangle(p, _positions[a], _positions[b], _positions[c], out float wa, out float wb, out float wc);
                            float d = Vector3.Distance(p, q);
                            if (d < best.Distance)
                                best = new Hit(a, b, c, wa, wb, wc, q, d);
                        }
                    }
                }
            }
        }
        return best;
    }
}
