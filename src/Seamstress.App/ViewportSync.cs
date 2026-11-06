using System.Globalization;
using System.Numerics;
using System.Text;
using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;

namespace Seamstress.App;

/// <summary>
/// Keeps the 3D viewport's geometry files up to date. Each visible piece gets a mesh file that is rewritten (under a
/// new name, so the renderer notices) whenever its shape or UVs change; the viewport is told which files to show.
/// </summary>
internal sealed class ViewportSync : IDisposable
{
    private sealed class Entry
    {
        public object? Drape;
        public string Key = "";
        public int Hash;
        public string Url = "";
        public readonly Queue<string> Files = new();
    }

    private readonly string _folder;
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly Queue<string> _seamFiles = new();
    private int _generation;
    private string _seamUrl = "";
    private Avatar? _avatar;
    private string _avatarUrl = "";

    public string CheckerUrl { get; }

    /// <summary>Handle meshes for the move/rotate gizmo: a unit arrow along +Y and a unit ring around the Y axis.</summary>
    public string ArrowUrl { get; }
    public string RingUrl { get; }

    /// <summary>When set, the outlines of all faces are written as a line mesh for the topology overlay.</summary>
    public bool ShowWire { get; set; }

    private readonly Queue<string> _wireFiles = new();
    private string _wireUrl = "";
    private readonly Queue<string> _pinFiles = new();
    private string _pinUrl = "";
    private int _pinHash;
    private readonly Queue<string> _pinSelectionFiles = new();
    private string _pinSelectionUrl = "";
    private int _pinSelectionHash;

    public ViewportSync()
    {
        string root = OperatingSystem.IsLinux() && Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath();
        RemoveStaleFolders(root);
        _folder = Path.Combine(root, $"seamstress-{Environment.ProcessId}");
        Directory.CreateDirectory(_folder);
        string checker = Path.Combine(_folder, "checker.png");
        Seamstress.Core.IO.PngWriter.WriteChecker(checker);
        CheckerUrl = ToUrl(checker);
        ArrowUrl = WriteArrow(Path.Combine(_folder, "gizmo_arrow.mesh"));
        RingUrl = WriteRing(Path.Combine(_folder, "gizmo_ring.mesh"));
    }

    private static string WriteArrow(string file)
    {
        const int sides = 16;
        var positions = new List<Vector3>();
        var triangles = new List<int>();
        void Ring(float y, float radius)
        {
            for (int i = 0; i < sides; i++)
            {
                float a = i * MathF.Tau / sides;
                positions.Add(new Vector3(MathF.Cos(a) * radius, y, MathF.Sin(a) * radius));
            }
        }
        void Band(int lower, int upper)
        {
            for (int i = 0; i < sides; i++)
            {
                int j = (i + 1) % sides;
                triangles.AddRange(new[] { lower + i, upper + i, lower + j, lower + j, upper + i, upper + j });
            }
        }
        Ring(0.12f, 0.022f);   // shaft
        Ring(0.78f, 0.022f);
        Ring(0.78f, 0.07f);    // head
        Ring(1.0f, 0.001f);
        Band(0, sides);
        Band(sides, 2 * sides);
        Band(2 * sides, 3 * sides);
        var p = positions.ToArray();
        var t = triangles.ToArray();
        QtMesh.WriteTriangles(file, p, Mesher.ComputeNormals(p, t), null, t);
        return ToUrl(file);
    }

    private static string WriteRing(string file)
    {
        const int around = 64, tube = 8;
        const float radius = 0.72f, thickness = 0.022f;
        var positions = new Vector3[around * tube];
        var triangles = new List<int>();
        for (int i = 0; i < around; i++)
        {
            float a = i * MathF.Tau / around;
            var centre = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
            for (int k = 0; k < tube; k++)
            {
                float b = k * MathF.Tau / tube;
                positions[i * tube + k] = centre * (radius + MathF.Cos(b) * thickness) + Vector3.UnitY * (MathF.Sin(b) * thickness);
                int i2 = (i + 1) % around, k2 = (k + 1) % tube;
                triangles.AddRange(new[] { i * tube + k, i * tube + k2, i2 * tube + k, i2 * tube + k, i * tube + k2, i2 * tube + k2 });
            }
        }
        var t = triangles.ToArray();
        QtMesh.WriteTriangles(file, positions, Mesher.ComputeNormals(positions, t), null, t);
        return ToUrl(file);
    }

    private static void RemoveStaleFolders(string root)
    {
        try
        {
            foreach (string dir in Directory.GetDirectories(root, "seamstress-*"))
            {
                string suffix = Path.GetFileName(dir)["seamstress-".Length..];
                if (!int.TryParse(suffix, out int pid))
                    continue;
                bool alive;
                try { alive = !System.Diagnostics.Process.GetProcessById(pid).HasExited; }
                catch (ArgumentException) { alive = false; }
                if (!alive)
                    Directory.Delete(dir, true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left-over viewport files from an earlier run are harmless.
        }
    }

    public static string ToUrl(string path) => new Uri(path).AbsoluteUri;

    private string NextFile(string prefix, Queue<string> history)
    {
        string file = Path.Combine(_folder, $"{prefix}_{++_generation}.mesh");
        history.Enqueue(file);
        // The renderer may still be reading the previous file, so only files a few generations old are removed.
        while (history.Count > 4)
        {
            try { File.Delete(history.Dequeue()); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return file;
    }

    public string AvatarUrl(Avatar? avatar)
    {
        if (ReferenceEquals(avatar, _avatar))
            return _avatarUrl;
        _avatar = avatar;
        if (avatar == null)
            return _avatarUrl = "";
        string file = Path.Combine(_folder, $"avatar_{++_generation}.mesh");
        QtMesh.WriteTriangles(file, avatar.Mesh.Positions, Mesher.ComputeNormals(avatar.Mesh.Positions, avatar.Mesh.Triangles), null, avatar.Mesh.Triangles);
        return _avatarUrl = ToUrl(file);
    }

    /// <summary>Writes whatever changed and returns the list of models the viewport should display, as JSON.</summary>
    public string Sync(Workspace workspace, Avatar? avatar)
    {
        var doc = workspace.Document;
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(2048);
        sb.Append("{\"models\":[");
        bool first = true, anyChanged = false;
        var seen = new HashSet<string>();
        var seamLines = new List<Vector3>();
        var wireLines = ShowWire ? new List<Vector3>() : null;
        var pins = new List<Vector3>();
        var pinSelection = new List<Vector3>();

        foreach (var garment in doc.Garments)
        {
            if (!garment.Visible || garment.Pieces.Count == 0)
                continue;
            var mesh = workspace.MeshFor(garment);
            var layout = UvLayout.For(garment);
            var positions = new Vector3[mesh.Pieces.Count][];
            var normals = new Vector3[mesh.Pieces.Count][];
            for (int i = 0; i < mesh.Pieces.Count; i++)
            {
                var piece = garment.FindPiece(mesh.Pieces[i].PieceId)!;
                positions[i] = Mesher.CurrentPositions(piece, mesh.Pieces[i]);
                normals[i] = Mesher.ComputeNormals(positions[i], mesh.Pieces[i].ShownTriangles(piece.FlipNormals));
            }
            // Shade across closed seams as one surface; draw a thread for stitches that are still apart.
            foreach (var stitch in mesh.Stitches)
            {
                var a = positions[stitch.PieceA][stitch.VertexA];
                var b = positions[stitch.PieceB][stitch.VertexB];
                float distance = Vector3.Distance(a, b);
                if (distance < 0.004f)
                {
                    var na = normals[stitch.PieceA][stitch.VertexA];
                    var nb = normals[stitch.PieceB][stitch.VertexB];
                    if (Vector3.Dot(na, nb) > 0)
                    {
                        var sum = Vector3.Normalize(na + nb);
                        normals[stitch.PieceA][stitch.VertexA] = sum;
                        normals[stitch.PieceB][stitch.VertexB] = sum;
                    }
                }
                else
                {
                    var pa = garment.FindPiece(mesh.Pieces[stitch.PieceA].PieceId)!;
                    var pb = garment.FindPiece(mesh.Pieces[stitch.PieceB].PieceId)!;
                    if (Workspace.IsEffectivelyVisible(garment, pa) && Workspace.IsEffectivelyVisible(garment, pb))
                    {
                        var na = normals[stitch.PieceA][stitch.VertexA];
                        var nb = normals[stitch.PieceB][stitch.VertexB];
                        float ha = garment.ShownThicknessMm * 0.0005f + 0.001f;
                        float hb = garment.ShownThicknessMm * 0.0005f + 0.001f;
                        seamLines.Add(a + na * ha);
                        seamLines.Add(b + nb * hb);
                        seamLines.Add(a - na * ha);
                        seamLines.Add(b - nb * hb);
                    }
                }
            }

            for (int i = 0; i < mesh.Pieces.Count; i++)
            {
                var pieceMesh = mesh.Pieces[i];
                var piece = garment.FindPiece(pieceMesh.PieceId)!;
                if (!Workspace.IsEffectivelyVisible(garment, piece) || pieceMesh.TriangleCount == 0)
                    continue;
                seen.Add(piece.Id);
                float halfThickness = garment.ShownThicknessMm * 0.0005f;
                if (piece.PinKey == pieceMesh.Key)
                {
                    foreach (int index in piece.Pins)
                    {
                        if (index >= 0 && index < positions[i].Length)
                        {
                            pins.Add(positions[i][index] + normals[i][index] * (halfThickness + 0.0015f));
                            pins.Add(positions[i][index] - normals[i][index] * (halfThickness + 0.0015f));
                        }
                    }
                }
                if (workspace.PinSelectionKey(piece.Id) == pieceMesh.Key
                    && workspace.PinSelection.TryGetValue(piece.Id, out var selectedVertices))
                {
                    foreach (int index in selectedVertices)
                    {
                        if (index >= 0 && index < positions[i].Length)
                        {
                            pinSelection.Add(positions[i][index] + normals[i][index] * (halfThickness + 0.0018f));
                            pinSelection.Add(positions[i][index] - normals[i][index] * (halfThickness + 0.0018f));
                        }
                    }
                }
                if (wireLines != null)
                {
                    // Face outlines, lifted a hair off the surface on both sides so they are not hidden by it.
                    var drawn = new HashSet<long>();
                    foreach (var face in pieceMesh.Faces())
                    {
                        for (int k = 0; k < face.Length; k++)
                        {
                            int a = face[k], b = face[(k + 1) % face.Length];
                            if (!drawn.Add(a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a))
                                continue;
                            foreach (float side in new[] { 1f, -1f })
                            {
                                float lift = side * (halfThickness + 0.0008f);
                                wireLines.Add(positions[i][a] + normals[i][a] * lift);
                                wireLines.Add(positions[i][b] + normals[i][b] * lift);
                            }
                        }
                    }
                }
                if (!_entries.TryGetValue(piece.Id, out var entry))
                    _entries[piece.Id] = entry = new Entry();

                var hash = new HashCode();
                hash.Add(piece.Placement.Position);
                hash.Add(piece.Placement.Rotation);
                hash.Add(piece.Placement.Curvature);
                hash.Add(piece.Origin);
                hash.Add(piece.Uv.Offset);
                hash.Add(piece.Uv.Scale);
                hash.Add(piece.Uv.Rotation);
                hash.Add(layout.UnitsPerCm);
                hash.Add(layout.Min);
                hash.Add(piece.FlipNormals);
                hash.Add(halfThickness);
                bool usesDrape = piece.Drape != null && piece.DrapeKey == pieceMesh.Key;
                int signature = hash.ToHashCode();
                if (entry.Url.Length == 0 || entry.Key != pieceMesh.Key || entry.Hash != signature
                    || !ReferenceEquals(entry.Drape, usesDrape ? piece.Drape : null))
                {
                    string file = NextFile("p" + piece.Id, entry.Files);
                    var shell = Mesher.BuildShell(positions[i], normals[i], layout.Map(piece, pieceMesh),
                        pieceMesh.ShownTriangles(piece.FlipNormals), halfThickness * 2f);
                    QtMesh.WriteTriangles(file, shell.Positions, shell.Normals, shell.Uvs, shell.Triangles);
                    entry.Url = ToUrl(file);
                    entry.Key = pieceMesh.Key;
                    entry.Hash = signature;
                    entry.Drape = usesDrape ? piece.Drape : null;
                    anyChanged = true;
                }

                var material = doc.FindMaterial(piece.MaterialId) ?? Seamstress.Core.IO.ExportScene.DefaultMaterial;
                if (!first)
                    sb.Append(',');
                first = false;
                sb.Append("{\"id\":\"").Append(piece.Id).Append("\",\"source\":\"").Append(entry.Url).Append('"');
                sb.Append(",\"color\":\"").Append(material.Color).Append('"');
                string texture = !string.IsNullOrEmpty(material.Texture) && File.Exists(material.Texture) ? ToUrl(material.Texture) : "";
                sb.Append(",\"texture\":").Append(System.Text.Json.JsonSerializer.Serialize(texture));
                sb.Append(inv, $",\"repeat\":{material.Repeat:0.####},\"roughness\":{material.Roughness:0.###},\"metallic\":{material.Metallic:0.###},\"opacity\":{material.Opacity:0.###}");
                sb.Append(",\"selected\":").Append(workspace.SelectedPieces.Contains(piece.Id) ? "true" : "false");
                sb.Append('}');
            }
        }

        foreach (var id in _entries.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            foreach (string file in _entries[id].Files)
            {
                try { File.Delete(file); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            _entries.Remove(id);
            anyChanged = true;
        }

        if (anyChanged || (_seamUrl.Length == 0) != (seamLines.Count == 0))
        {
            if (seamLines.Count == 0)
            {
                _seamUrl = "";
            }
            else
            {
                string file = NextFile("seams", _seamFiles);
                QtMesh.WriteLines(file, seamLines.ToArray());
                _seamUrl = ToUrl(file);
            }
        }

        if (wireLines == null || wireLines.Count == 0)
        {
            _wireUrl = "";
        }
        else if (anyChanged || _wireUrl.Length == 0)
        {
            string file = NextFile("wire", _wireFiles);
            QtMesh.WriteLines(file, wireLines.ToArray());
            _wireUrl = ToUrl(file);
        }

        WriteMarkers("pins", pins, _pinFiles, ref _pinHash, ref _pinUrl);
        WriteMarkers("pin_selection", pinSelection, _pinSelectionFiles, ref _pinSelectionHash, ref _pinSelectionUrl);

        sb.Append("],\"seams\":\"").Append(_seamUrl).Append('"');
        sb.Append(",\"wire\":\"").Append(_wireUrl).Append('"');
        sb.Append(",\"pins\":\"").Append(_pinUrl).Append('"');
        sb.Append(",\"pinSelection\":\"").Append(_pinSelectionUrl).Append('"');
        if (workspace.SelectionCentre() is { } centre)
            sb.Append(inv, $",\"gizmo\":[{centre.X:0.#####},{centre.Y:0.#####},{centre.Z:0.#####}]");
        sb.Append(",\"avatar\":\"").Append(AvatarUrl(avatar)).Append('"');
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>Small octahedra at the given points; the file is only rewritten when the points move.</summary>
    private void WriteMarkers(string prefix, List<Vector3> centres, Queue<string> history, ref int lastHash, ref string url)
    {
        int hash = 17;
        foreach (var centre in centres)
            hash = hash * 31 + centre.GetHashCode();
        if (centres.Count == 0)
        {
            url = "";
            lastHash = hash;
            return;
        }
        if (hash == lastHash && url.Length != 0)
            return;

        const float radius = 0.0042f;
        var directions = new[]
        {
            Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitX, -Vector3.UnitZ, -Vector3.UnitY,
        };
        var positions = new Vector3[centres.Count * 6];
        var triangles = new int[centres.Count * 24];
        for (int i = 0; i < centres.Count; i++)
        {
            int vertex = i * 6;
            for (int k = 0; k < 6; k++)
                positions[vertex + k] = centres[i] + directions[k] * radius;
            int triangle = i * 24;
            foreach (int corner in new[] { 0, 2, 1, 0, 4, 2, 0, 3, 4, 0, 1, 3, 5, 1, 2, 5, 2, 4, 5, 4, 3, 5, 3, 1 })
                triangles[triangle++] = vertex + corner;
        }
        string file = NextFile(prefix, history);
        QtMesh.WriteTriangles(file, positions, Mesher.ComputeNormals(positions, triangles), null, triangles);
        url = ToUrl(file);
        lastHash = hash;
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
