using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Seamstress.Core.Model;

namespace Seamstress.Core.IO;

/// <summary>
/// Reads and writes Seamstress projects as JSON. The file is meant to be read and edited by hand: short values such as
/// points and vectors stay on one line, and every length names its unit in the "units" header.
/// </summary>
public static class GarmentFile
{
    public const string FormatName = "seamstress-garment";
    public const int FormatVersion = 1;
    public const string Extension = ".garment.json";

    public static void Save(Document document, string path)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
        File.WriteAllText(path, Serialize(document, folder), new UTF8Encoding(false));
    }

    public static Document Load(string path)
        => Deserialize(File.ReadAllText(path), Path.GetDirectoryName(Path.GetFullPath(path)));

    private static string N(float v) => (MathF.Round(v, 4) + 0f).ToString("0.####", CultureInfo.InvariantCulture);
    private static string V(Vector2 v) => $"[{N(v.X)}, {N(v.Y)}]";
    private static string V(Vector3 v) => $"[{N(v.X)}, {N(v.Y)}, {N(v.Z)}]";

    /// <summary>Serializes a document. Texture paths inside <paramref name="baseFolder"/> are stored relative to it.</summary>
    public static string Serialize(Document doc, string? baseFolder = null)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("format", FormatName);
            w.WriteNumber("version", FormatVersion);
            w.WriteString("name", doc.Name);
            w.WritePropertyName("units");
            w.WriteRawValue("{ \"pattern\": \"cm\", \"world\": \"m\", \"angles\": \"degrees\" }");

            w.WriteStartObject("simulation");
            w.WriteNumber("gravity", doc.Simulation.Gravity);
            w.WriteNumber("substeps", doc.Simulation.Substeps);
            w.WriteNumber("timeScale", doc.Simulation.TimeScale);
            w.WriteBoolean("selfCollision", doc.Simulation.SelfCollision);
            w.WriteBoolean("groundPlane", doc.Simulation.GroundPlane);
            w.WriteEndObject();

            w.WriteStartObject("avatar");
            w.WriteString("kind", doc.Avatar.Kind.ToString());
            if (doc.Avatar.Path != null)
                w.WriteString("path", doc.Avatar.Path);
            w.WriteNumber("scale", doc.Avatar.Scale);
            if (doc.Avatar.Kind == AvatarKind.MakeHuman)
            {
                // MakeHuman body shape: 0 to 1 each, 0.5 is MakeHuman's default; gender 0 is female, 1 is male.
                w.WritePropertyName("shape");
                w.WriteRawValue($"{{ \"gender\": {N(doc.Avatar.Gender)}, \"age\": {N(doc.Avatar.Age)}, \"muscle\": {N(doc.Avatar.Muscle)}, "
                    + $"\"weight\": {N(doc.Avatar.Weight)}, \"height\": {N(doc.Avatar.Height)}, \"proportions\": {N(doc.Avatar.Proportions)} }}");
            }
            w.WriteEndObject();

            w.WriteStartArray("materials");
            foreach (var m in doc.Materials)
            {
                w.WriteStartObject();
                w.WriteString("id", m.Id);
                w.WriteString("name", m.Name);
                w.WriteString("color", m.Color);
                if (m.Texture != null)
                    w.WriteString("texture", Relativize(m.Texture, baseFolder));
                w.WriteNumber("repeat", m.Repeat);
                w.WriteNumber("roughness", m.Roughness);
                w.WriteNumber("metallic", m.Metallic);
                w.WriteNumber("opacity", m.Opacity);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("garments");
            foreach (var g in doc.Garments)
            {
                w.WriteStartObject();
                w.WriteString("id", g.Id);
                w.WriteString("name", g.Name);
                w.WriteBoolean("visible", g.Visible);
                w.WriteBoolean("simulateWithOthers", g.SimulateWithOthers);
                w.WriteNumber("particleDistance", g.ParticleDistance);
                w.WriteString("topology", g.Topology == MeshTopology.Quads ? "quads" : "triangles");

                w.WriteStartArray("groups");
                foreach (var group in g.Groups)
                {
                    w.WriteStartObject();
                    w.WriteString("id", group.Id);
                    w.WriteString("name", group.Name);
                    if (group.ParentId != null)
                        w.WriteString("parent", group.ParentId);
                    w.WriteBoolean("visible", group.Visible);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartArray("pieces");
                foreach (var p in g.Pieces)
                    WritePiece(w, p);
                w.WriteEndArray();

                // Order of groups and pieces among their siblings in the layers panel.
                var order = g.OrderedLayerIds();
                if (g.LayerOrder.Count > 0)
                {
                    w.WritePropertyName("layerOrder");
                    w.WriteRawValue("[" + string.Join(", ", order.Select(Quote)) + "]");
                }

                // "edge" n is the edge that starts at point n of the piece (counting from 0, outline first, then holes).
                w.WritePropertyName("seams");
                w.WriteRawValue(Lines(g.Seams.Select(s =>
                    $"{{ \"id\": {Quote(s.Id)}, \"from\": {{ \"piece\": {Quote(s.A.PieceId)}, \"edge\": {s.A.Edge} }}, "
                    + $"\"to\": {{ \"piece\": {Quote(s.B.PieceId)}, \"edge\": {s.B.Edge} }}, \"reversed\": {(s.Reversed ? "true" : "false")} }}"), 4));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static string Quote(string s) => JsonSerializer.Serialize(s);

    /// <summary>A JSON array with one compact item per line, indented to sit at the given nesting depth.</summary>
    private static string Lines(IEnumerable<string> items, int depth)
    {
        var list = items.ToList();
        if (list.Count == 0)
            return "[]";
        string indent = new(' ', depth * 2);
        return "[\n" + string.Join(",\n", list.Select(i => indent + i)) + "\n" + new string(' ', (depth - 1) * 2) + "]";
    }

    private static void WritePiece(Utf8JsonWriter w, PatternPiece p)
    {
        w.WriteStartObject();
        w.WriteString("id", p.Id);
        w.WriteString("name", p.Name);
        if (p.GroupId != null)
            w.WriteString("group", p.GroupId);
        w.WriteBoolean("visible", p.Visible);
        w.WritePropertyName("origin");
        w.WriteRawValue(V(p.Origin));

        // One outline point per line; "curve" is the control point bending the edge that leaves the point.
        static string Point(PatternPoint point) => point.Curve is { } c
            ? $"{{ \"at\": {V(point.Position)}, \"curve\": {V(c)} }}"
            : $"{{ \"at\": {V(point.Position)} }}";
        w.WritePropertyName("points");
        w.WriteRawValue(Lines(p.Points.Select(Point), 6));
        if (p.Holes.Count > 0)
        {
            // Holes go clockwise; their points are numbered on from the outline's, which is how seams name their edges.
            w.WritePropertyName("holes");
            w.WriteRawValue(Lines(p.Holes.Select(hole => Lines(hole.Select(Point), 7)), 6));
        }

        w.WritePropertyName("placement");
        w.WriteRawValue($"{{ \"position\": {V(p.Placement.Position)}, \"rotation\": {V(p.Placement.Rotation)}, \"curvature\": {N(p.Placement.Curvature)} }}");

        w.WriteStartObject("physics");
        w.WriteString("preset", p.Physics.Preset);
        w.WriteNumber("density", p.Physics.Density);
        w.WriteNumber("stretch", p.Physics.Stretch);
        w.WriteNumber("shear", p.Physics.Shear);
        w.WriteNumber("bend", p.Physics.Bend);
        w.WriteNumber("damping", p.Physics.Damping);
        w.WriteNumber("friction", p.Physics.Friction);
        w.WriteNumber("thickness", p.Physics.Thickness);
        w.WriteEndObject();

        if (p.MaterialId != null)
            w.WriteString("material", p.MaterialId);
        if (p.ParticleDistance is { } distance)
            w.WriteNumber("particleDistance", distance);
        w.WritePropertyName("uv");
        w.WriteRawValue($"{{ \"offset\": {V(p.Uv.Offset)}, \"scale\": {N(p.Uv.Scale)}, \"rotation\": {N(p.Uv.Rotation)} }}");
        if (p.FlipNormals)
            w.WriteBoolean("flipNormals", true);

        if (p.Drape != null && p.DrapeKey != null)
        {
            // Simulated shape: x, y, z per mesh vertex. Safe to delete; the piece then returns to its flat placement.
            w.WriteStartObject("drape");
            w.WriteString("mesh", p.DrapeKey);
            var sb = new StringBuilder(p.Drape.Length * 8);
            sb.Append('[');
            for (int i = 0; i < p.Drape.Length; i++)
            {
                if (i > 0)
                    sb.Append(i % 3 == 0 ? ", " : ",");
                sb.Append(N(p.Drape[i]));
            }
            sb.Append(']');
            w.WritePropertyName("positions");
            w.WriteRawValue(sb.ToString());
            w.WriteEndObject();
        }
        if (p.Pins.Count > 0 && p.PinKey != null)
        {
            // Vertices held in place during simulation, for the mesh they were chosen on.
            w.WriteStartObject("pins");
            w.WriteString("mesh", p.PinKey);
            w.WritePropertyName("vertices");
            w.WriteRawValue("[" + string.Join(", ", p.Pins.OrderBy(i => i)) + "]");
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    private static string Relativize(string path, string? baseFolder)
    {
        if (baseFolder == null || !Path.IsPathRooted(path))
            return path;
        string relative = Path.GetRelativePath(baseFolder, path);
        return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative.Replace('\\', '/');
    }

    /// <summary>Parses a project. Missing properties fall back to defaults so hand-written files can stay minimal.</summary>
    public static Document Deserialize(string json, string? baseFolder = null)
    {
        using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = parsed.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(Lang.T("Not a Seamstress garment file: the top level must be a JSON object."));
        if (root.TryGetProperty("format", out var format) && format.GetString() != FormatName)
            throw new InvalidDataException(Lang.F("Not a Seamstress garment file (format is '{0}').", format.GetString()));
        if (root.TryGetProperty("version", out var version) && version.TryGetInt32(out int v) && v > FormatVersion)
            throw new InvalidDataException(Lang.F("This file was written by a newer version of Seamstress (format version {0}).", v));

        var doc = new Document { Name = Str(root, "name") ?? "Untitled" };
        if (root.TryGetProperty("simulation", out var sim) && sim.ValueKind == JsonValueKind.Object)
        {
            doc.Simulation.Gravity = Num(sim, "gravity", doc.Simulation.Gravity);
            doc.Simulation.Substeps = (int)Num(sim, "substeps", doc.Simulation.Substeps);
            doc.Simulation.TimeScale = Num(sim, "timeScale", doc.Simulation.TimeScale);
            doc.Simulation.SelfCollision = Bool(sim, "selfCollision", doc.Simulation.SelfCollision);
            doc.Simulation.GroundPlane = Bool(sim, "groundPlane", doc.Simulation.GroundPlane);
        }
        if (root.TryGetProperty("avatar", out var avatar) && avatar.ValueKind == JsonValueKind.Object)
        {
            if (Enum.TryParse<AvatarKind>(Str(avatar, "kind"), true, out var kind))
                doc.Avatar.Kind = kind;
            doc.Avatar.Path = Str(avatar, "path");
            doc.Avatar.Scale = Num(avatar, "scale", 1f);
            if (avatar.TryGetProperty("shape", out var shape) && shape.ValueKind == JsonValueKind.Object)
            {
                doc.Avatar.Gender = Math.Clamp(Num(shape, "gender", 0.5f), 0, 1);
                doc.Avatar.Age = Math.Clamp(Num(shape, "age", 0.5f), 0, 1);
                doc.Avatar.Muscle = Math.Clamp(Num(shape, "muscle", 0.5f), 0, 1);
                doc.Avatar.Weight = Math.Clamp(Num(shape, "weight", 0.5f), 0, 1);
                doc.Avatar.Height = Math.Clamp(Num(shape, "height", 0.5f), 0, 1);
                doc.Avatar.Proportions = Math.Clamp(Num(shape, "proportions", 0.5f), 0, 1);
            }
        }
        foreach (var m in Items(root, "materials"))
        {
            var material = new FabricMaterial();
            material.Id = Str(m, "id") ?? material.Id;
            material.Name = Str(m, "name") ?? material.Name;
            material.Color = Str(m, "color") ?? material.Color;
            material.Texture = Resolve(Str(m, "texture"), baseFolder);
            material.Repeat = Num(m, "repeat", material.Repeat);
            material.Roughness = Num(m, "roughness", material.Roughness);
            material.Metallic = Num(m, "metallic", material.Metallic);
            material.Opacity = Num(m, "opacity", material.Opacity);
            doc.Materials.Add(material);
        }
        foreach (var g in Items(root, "garments"))
        {
            var garment = new Garment();
            garment.Id = Str(g, "id") ?? garment.Id;
            garment.Name = Str(g, "name") ?? garment.Name;
            garment.Visible = Bool(g, "visible", true);
            garment.SimulateWithOthers = Bool(g, "simulateWithOthers", false);
            garment.ParticleDistance = Math.Clamp(Num(g, "particleDistance", garment.ParticleDistance), 0.3f, 20f);
            garment.Topology = string.Equals(Str(g, "topology"), "quads", StringComparison.OrdinalIgnoreCase)
                ? MeshTopology.Quads : MeshTopology.Triangles;
            foreach (var gr in Items(g, "groups"))
            {
                var group = new LayerGroup();
                group.Id = Str(gr, "id") ?? group.Id;
                group.Name = Str(gr, "name") ?? group.Name;
                group.ParentId = Str(gr, "parent");
                group.Visible = Bool(gr, "visible", true);
                garment.Groups.Add(group);
            }
            foreach (var p in Items(g, "pieces"))
                garment.Pieces.Add(ReadPiece(p));
            if (g.TryGetProperty("layerOrder", out var layerOrder) && layerOrder.ValueKind == JsonValueKind.Array)
            {
                foreach (var id in layerOrder.EnumerateArray())
                {
                    if (id.ValueKind == JsonValueKind.String && id.GetString() is { } value)
                        garment.LayerOrder.Add(value);
                }
            }
            foreach (var s in Items(g, "seams"))
            {
                var seam = new Seam();
                seam.Id = Str(s, "id") ?? seam.Id;
                if (s.TryGetProperty("from", out var from))
                    seam.A = new EdgeRef(Str(from, "piece") ?? "", (int)Num(from, "edge", 0));
                if (s.TryGetProperty("to", out var to))
                    seam.B = new EdgeRef(Str(to, "piece") ?? "", (int)Num(to, "edge", 0));
                seam.Reversed = Bool(s, "reversed", false);
                garment.Seams.Add(seam);
            }
            // Drop references a hand edit may have broken rather than failing later.
            garment.Seams.RemoveAll(s => !EdgeExists(garment, s.A) || !EdgeExists(garment, s.B));
            foreach (var group in garment.Groups)
            {
                if (group.ParentId != null && (garment.FindGroup(group.ParentId) == null || garment.IsGroupWithin(group.ParentId, group.Id)))
                    group.ParentId = null;
            }
            foreach (var piece in garment.Pieces)
            {
                if (piece.GroupId != null && garment.FindGroup(piece.GroupId) == null)
                    piece.GroupId = null;
                if (piece.MaterialId != null && doc.FindMaterial(piece.MaterialId) == null)
                    piece.MaterialId = null;
            }
            doc.Garments.Add(garment);
        }
        return doc;
    }

    private static bool EdgeExists(Garment garment, EdgeRef edge)
    {
        var piece = garment.FindPiece(edge.PieceId);
        return piece != null && edge.Edge >= 0 && edge.Edge < piece.EdgeCount;
    }

    private static PatternPiece ReadPiece(JsonElement p)
    {
        var piece = new PatternPiece();
        piece.Id = Str(p, "id") ?? piece.Id;
        piece.Name = Str(p, "name") ?? piece.Name;
        piece.GroupId = Str(p, "group");
        piece.Visible = Bool(p, "visible", true);
        piece.Origin = Vec2(p, "origin", Vector2.Zero);
        foreach (var pt in Items(p, "points"))
            piece.Points.Add(ReadPoint(pt));
        foreach (var h in Items(p, "holes"))
        {
            // A hole needs three points; one with fewer would leave no shape to cut.
            var hole = h.ValueKind == JsonValueKind.Array
                ? h.EnumerateArray().Where(pt => pt.ValueKind is JsonValueKind.Object or JsonValueKind.Array).Select(ReadPoint).ToList()
                : new List<PatternPoint>();
            if (hole.Count >= 3)
                piece.Holes.Add(hole);
        }
        if (p.TryGetProperty("placement", out var placement) && placement.ValueKind == JsonValueKind.Object)
        {
            piece.Placement.Position = Vec3(placement, "position", piece.Placement.Position);
            piece.Placement.Rotation = Vec3(placement, "rotation", Vector3.Zero);
            piece.Placement.Curvature = Num(placement, "curvature", 0);
        }
        if (p.TryGetProperty("physics", out var physics) && physics.ValueKind == JsonValueKind.Object)
        {
            var preset = Str(physics, "preset");
            if (preset != null)
                piece.Physics = FabricPhysics.FromPreset(preset);
            piece.Physics.Preset = preset ?? piece.Physics.Preset;
            piece.Physics.Density = Num(physics, "density", piece.Physics.Density);
            piece.Physics.Stretch = Num(physics, "stretch", piece.Physics.Stretch);
            piece.Physics.Shear = Num(physics, "shear", piece.Physics.Shear);
            piece.Physics.Bend = Num(physics, "bend", piece.Physics.Bend);
            piece.Physics.Damping = Num(physics, "damping", piece.Physics.Damping);
            piece.Physics.Friction = Num(physics, "friction", piece.Physics.Friction);
            piece.Physics.Thickness = Num(physics, "thickness", piece.Physics.Thickness);
        }
        piece.MaterialId = Str(p, "material");
        if (p.TryGetProperty("particleDistance", out var pd) && pd.ValueKind == JsonValueKind.Number)
            piece.ParticleDistance = Math.Clamp(pd.GetSingle(), 0.3f, 20f);
        piece.FlipNormals = p.TryGetProperty("flipNormals", out var flip) && flip.ValueKind == JsonValueKind.True;
        if (p.TryGetProperty("uv", out var uv) && uv.ValueKind == JsonValueKind.Object)
        {
            piece.Uv.Offset = Vec2(uv, "offset", Vector2.Zero);
            piece.Uv.Scale = Num(uv, "scale", 1f);
            piece.Uv.Rotation = Num(uv, "rotation", 0f);
        }
        if (p.TryGetProperty("drape", out var drape) && drape.ValueKind == JsonValueKind.Object
            && drape.TryGetProperty("positions", out var positions) && positions.ValueKind == JsonValueKind.Array)
        {
            var values = new float[positions.GetArrayLength()];
            int i = 0;
            bool valid = true;
            foreach (var value in positions.EnumerateArray())
            {
                valid &= value.ValueKind == JsonValueKind.Number;
                values[i++] = valid ? value.GetSingle() : 0;
            }
            if (valid && values.Length % 3 == 0)
            {
                piece.Drape = values;
                piece.DrapeKey = Str(drape, "mesh");
            }
        }
        if (p.TryGetProperty("pins", out var pins) && pins.ValueKind == JsonValueKind.Object
            && pins.TryGetProperty("vertices", out var vertices) && vertices.ValueKind == JsonValueKind.Array)
        {
            foreach (var vertex in vertices.EnumerateArray())
            {
                if (vertex.ValueKind == JsonValueKind.Number && vertex.TryGetInt32(out int index) && index >= 0)
                    piece.Pins.Add(index);
            }
            if (piece.Pins.Count > 0)
                piece.PinKey = Str(pins, "mesh");
        }
        return piece;
    }

    /// <summary>A point written as { "at": [x, y], "curve": [x, y] }, or just as [x, y].</summary>
    private static PatternPoint ReadPoint(JsonElement pt)
    {
        var point = new PatternPoint();
        if (pt.ValueKind == JsonValueKind.Array)
        {
            point.Position = ToVec2(pt, Vector2.Zero);
        }
        else if (pt.ValueKind == JsonValueKind.Object)
        {
            point.Position = Vec2(pt, "at", Vector2.Zero);
            if (pt.TryGetProperty("curve", out var curve) && curve.ValueKind == JsonValueKind.Array)
                point.Curve = ToVec2(curve, Vector2.Zero);
        }
        return point;
    }

    private static string? Resolve(string? path, string? baseFolder)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        if (!Path.IsPathRooted(path) && baseFolder != null)
            return Path.GetFullPath(Path.Combine(baseFolder, path));
        return path;
    }

    private static IEnumerable<JsonElement> Items(JsonElement parent, string name)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    yield return item;
            }
        }
    }

    private static string? Str(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static float Num(JsonElement parent, string name, float fallback)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetSingle() : fallback;

    private static bool Bool(JsonElement parent, string name, bool fallback)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? e.GetBoolean() : fallback;

    private static Vector2 Vec2(JsonElement parent, string name, Vector2 fallback)
        => parent.TryGetProperty(name, out var e) ? ToVec2(e, fallback) : fallback;

    private static Vector2 ToVec2(JsonElement e, Vector2 fallback)
    {
        if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() < 2)
            return fallback;
        return e[0].ValueKind == JsonValueKind.Number && e[1].ValueKind == JsonValueKind.Number
            ? new Vector2(e[0].GetSingle(), e[1].GetSingle()) : fallback;
    }

    private static Vector3 Vec3(JsonElement parent, string name, Vector3 fallback)
    {
        if (!parent.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.Array || e.GetArrayLength() < 3)
            return fallback;
        for (int i = 0; i < 3; i++)
        {
            if (e[i].ValueKind != JsonValueKind.Number)
                return fallback;
        }
        return new Vector3(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());
    }
}
