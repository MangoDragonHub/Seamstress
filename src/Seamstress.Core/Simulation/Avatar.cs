using System.Globalization;
using System.Numerics;

namespace Seamstress.Core.Simulation;

/// <summary>The body garments are draped on: a render mesh plus the distance field used for collisions.</summary>
public sealed class Avatar
{
    public required string Name { get; init; }
    public required TriMesh Mesh { get; init; }
    public required SdfGrid Sdf { get; init; }

    /// <summary>Set when the avatar is the MakeHuman base mesh, which "Send to MakeHuman" binds garments to.</summary>
    public MakeHumanBase? MakeHuman { get; init; }

    /// <summary>Built-in stylised figure, about 1.67 m tall, standing on the ground plane facing +Z in an A-pose.</summary>
    public static Avatar CreateMannequin()
    {
        var parts = new List<(Vector3 A, Vector3 B, float Ra, float Rb, Vector3 Squash)>();
        void Part(Vector3 a, Vector3 b, float ra, float rb, float squashZ = 1f)
            => parts.Add((a, b, ra, rb, new Vector3(1, 1, 1f / squashZ)));
        void Both(Vector3 a, Vector3 b, float ra, float rb, float squashZ = 1f)
        {
            Part(a, b, ra, rb, squashZ);
            Part(a * new Vector3(-1, 1, 1), b * new Vector3(-1, 1, 1), ra, rb, squashZ);
        }

        // Proportions follow the MakeHuman base mesh (1.67 m, arms 45 degrees down, torso centred slightly forward of
        // the heels) so garments made on the mannequin also fit a MakeHuman character.
        Part(new(0, 1.545f, 0.05f), new(0, 1.585f, 0.055f), 0.082f, 0.085f);        // head
        Part(new(0, 1.38f, 0.035f), new(0, 1.49f, 0.045f), 0.050f, 0.047f);         // neck
        Part(new(-0.125f, 1.335f, 0.04f), new(0.125f, 1.335f, 0.04f), 0.058f, 0.058f); // shoulders
        Part(new(0, 1.25f, 0.045f), new(0, 1.15f, 0.05f), 0.140f, 0.128f, 0.75f);   // chest
        Part(new(0, 1.15f, 0.05f), new(0, 1.02f, 0.05f), 0.128f, 0.122f, 0.76f);    // waist
        Part(new(0, 1.02f, 0.05f), new(0, 0.86f, 0.04f), 0.122f, 0.165f, 0.72f);    // hips
        Both(new(0.09f, 0.82f, 0.04f), new(0.10f, 0.48f, 0.045f), 0.082f, 0.055f);  // thigh
        Both(new(0.10f, 0.48f, 0.045f), new(0.105f, 0.10f, 0.03f), 0.055f, 0.038f); // calf
        Both(new(0.105f, 0.045f, 0.01f), new(0.11f, 0.035f, 0.17f), 0.042f, 0.032f); // foot
        var shoulder = new Vector3(0.175f, 1.33f, 0.04f);
        var down = new Vector3(MathF.Cos(45 * MathF.PI / 180), -MathF.Sin(45 * MathF.PI / 180), 0);
        var elbow = shoulder + down * 0.27f;
        var wrist = elbow + down * 0.24f;
        Both(shoulder, elbow, 0.050f, 0.040f);                                   // upper arm
        Both(elbow, wrist, 0.040f, 0.030f);                                      // forearm
        Both(wrist, wrist + down * 0.10f, 0.032f, 0.030f, 0.6f);                 // hand

        float Field(Vector3 p)
        {
            float d = float.MaxValue;
            const float k = 0.035f;
            foreach (var (a, b, ra, rb, squash) in parts)
            {
                var q = (p - a) * squash;
                var ab = (b - a) * squash;
                float t = Math.Clamp(Vector3.Dot(q, ab) / ab.LengthSquared(), 0, 1);
                float dist = (q - ab * t).Length() - (ra + (rb - ra) * t);
                float h = Math.Clamp(0.5f + 0.5f * (dist - d) / k, 0, 1);
                d = d == float.MaxValue ? dist : dist + (d - dist) * h - k * h * (1 - h);
            }
            return d;
        }

        var coarse = SdfGrid.FromFunction(Field, new Vector3(-0.75f, -0.05f, -0.22f), new Vector3(0.75f, 1.78f, 0.32f), 0.0125f);
        var mesh = coarse.ExtractSurface();
        return new Avatar { Name = "Mannequin", Mesh = mesh, Sdf = SdfGrid.FromMesh(mesh) };
    }

    /// <summary>Loads any OBJ as an avatar, scaled to metres and stood on the ground plane.</summary>
    public static Avatar FromObj(string path, float scale)
    {
        var obj = ObjFile.Load(path);
        var mesh = Ground(obj.ToTriMesh(_ => true, scale), out _);
        return new Avatar { Name = Path.GetFileNameWithoutExtension(path), Mesh = mesh, Sdf = SdfGrid.FromMesh(mesh) };
    }

    /// <summary>Loads the MakeHuman base mesh (hm08) so garments can be fitted and sent back to MakeHuman.</summary>
    public static Avatar FromMakeHumanBase(string baseObjPath)
    {
        var obj = ObjFile.Load(baseObjPath);
        // base.obj also contains joint markers and clothing helpers; only the body is a real surface.
        bool IsBody(string group) => !group.StartsWith("joint-", StringComparison.Ordinal) && !group.StartsWith("helper-", StringComparison.Ordinal);
        var body = Ground(obj.ToTriMesh(IsBody, MakeHumanBase.UnitsToMetres), out var offset);
        var info = new MakeHumanBase
        {
            Path = baseObjPath,
            Vertices = obj.Positions,
            BodyTriangles = obj.Triangulate(IsBody),
            Offset = offset,
        };
        return new Avatar { Name = "MakeHuman", Mesh = body, Sdf = SdfGrid.FromMesh(body), MakeHuman = info };
    }

    private static TriMesh Ground(TriMesh mesh, out Vector3 offset)
    {
        var (min, max) = mesh.Bounds();
        offset = new Vector3(-(min.X + max.X) / 2, -min.Y, 0);
        var moved = new Vector3[mesh.Positions.Length];
        for (int i = 0; i < moved.Length; i++)
            moved[i] = mesh.Positions[i] + offset;
        return new TriMesh { Positions = moved, Triangles = mesh.Triangles };
    }
}

/// <summary>The MakeHuman base mesh in its own units, kept so garment vertices can be bound to it.</summary>
public sealed class MakeHumanBase
{
    /// <summary>MakeHuman models are authored in decimetres.</summary>
    public const float UnitsToMetres = 0.1f;

    public required string Path { get; init; }

    /// <summary>All base mesh vertices in file order (MakeHuman units); .mhclo files refer to these indices.</summary>
    public required Vector3[] Vertices { get; init; }

    /// <summary>Triangulated body faces as indices into <see cref="Vertices"/>.</summary>
    public required int[] BodyTriangles { get; init; }

    /// <summary>Translation (metres) applied after scaling to stand the avatar on the ground.</summary>
    public required Vector3 Offset { get; init; }

    public Vector3 ToMakeHuman(Vector3 metres) => (metres - Offset) / UnitsToMetres;
}

/// <summary>Minimal Wavefront OBJ reader: positions and faces with their group names.</summary>
public sealed class ObjFile
{
    public required Vector3[] Positions { get; init; }
    public required List<(string Group, int[] Indices)> Faces { get; init; }

    public static ObjFile Load(string path)
    {
        var positions = new List<Vector3>();
        var faces = new List<(string, int[])>();
        string group = "";
        foreach (var raw in File.ReadLines(path))
        {
            if (raw.Length < 2)
                continue;
            if (raw[0] == 'v' && raw[1] == ' ')
            {
                var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4)
                    positions.Add(new Vector3(Parse(parts[1]), Parse(parts[2]), Parse(parts[3])));
            }
            else if (raw[0] == 'g' && raw[1] == ' ')
            {
                group = raw[2..].Trim();
            }
            else if (raw[0] == 'f' && raw[1] == ' ')
            {
                var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var indices = new int[parts.Length - 1];
                bool ok = indices.Length >= 3;
                for (int i = 0; i < indices.Length && ok; i++)
                {
                    var token = parts[i + 1];
                    int slash = token.IndexOf('/');
                    if (slash >= 0)
                        token = token[..slash];
                    ok = int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index);
                    indices[i] = index < 0 ? positions.Count + index : index - 1;
                    ok &= indices[i] >= 0;
                }
                if (ok)
                    faces.Add((group, indices));
            }
        }
        foreach (var (_, indices) in faces)
        {
            foreach (int i in indices)
            {
                if (i >= positions.Count)
                    throw new InvalidDataException($"'{path}' refers to vertex {i + 1} but only has {positions.Count}.");
            }
        }
        if (faces.Count == 0)
            throw new InvalidDataException($"'{path}' contains no faces.");
        return new ObjFile { Positions = positions.ToArray(), Faces = faces };
    }

    private static float Parse(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>Fan-triangulates the faces of the selected groups, keeping original vertex indices.</summary>
    public int[] Triangulate(Func<string, bool> includeGroup)
    {
        var triangles = new List<int>();
        foreach (var (group, indices) in Faces)
        {
            if (!includeGroup(group))
                continue;
            for (int i = 1; i < indices.Length - 1; i++)
            {
                triangles.Add(indices[0]);
                triangles.Add(indices[i]);
                triangles.Add(indices[i + 1]);
            }
        }
        return triangles.ToArray();
    }

    /// <summary>Compact triangle mesh of the selected groups, scaled by <paramref name="scale"/>.</summary>
    public TriMesh ToTriMesh(Func<string, bool> includeGroup, float scale)
    {
        var source = Triangulate(includeGroup);
        var remap = new Dictionary<int, int>();
        var positions = new List<Vector3>();
        var triangles = new int[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            if (!remap.TryGetValue(source[i], out int index))
            {
                index = positions.Count;
                remap[source[i]] = index;
                positions.Add(Positions[source[i]] * scale);
            }
            triangles[i] = index;
        }
        return new TriMesh { Positions = positions.ToArray(), Triangles = triangles };
    }
}
