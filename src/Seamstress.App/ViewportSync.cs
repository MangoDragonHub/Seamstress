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

    public ViewportSync()
    {
        string root = OperatingSystem.IsLinux() && Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath();
        RemoveStaleFolders(root);
        _folder = Path.Combine(root, $"seamstress-{Environment.ProcessId}");
        Directory.CreateDirectory(_folder);
        string checker = Path.Combine(_folder, "checker.png");
        Seamstress.Core.IO.PngWriter.WriteChecker(checker);
        CheckerUrl = ToUrl(checker);
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
            catch (IOException) { }
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
                normals[i] = Mesher.ComputeNormals(positions[i], mesh.Pieces[i].Triangles);
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
                        seamLines.Add(a);
                        seamLines.Add(b);
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
                bool usesDrape = piece.Drape != null && piece.DrapeKey == pieceMesh.Key;
                int signature = hash.ToHashCode();
                if (entry.Url.Length == 0 || entry.Key != pieceMesh.Key || entry.Hash != signature
                    || !ReferenceEquals(entry.Drape, usesDrape ? piece.Drape : null))
                {
                    string file = NextFile("p" + piece.Id, entry.Files);
                    QtMesh.WriteTriangles(file, positions[i], normals[i], layout.Map(piece, pieceMesh), pieceMesh.Triangles);
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
                catch (IOException) { }
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

        sb.Append("],\"seams\":\"").Append(_seamUrl).Append('"');
        sb.Append(",\"avatar\":\"").Append(AvatarUrl(avatar)).Append('"');
        sb.Append('}');
        return sb.ToString();
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
