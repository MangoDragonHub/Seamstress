using System.Globalization;
using System.Text;
using System.Text.Json;
using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;

namespace Seamstress.App;

public partial class AppController
{
    private static string Q(string? s) => JsonSerializer.Serialize(s ?? "");
    private static string B(bool b) => b ? "true" : "false";
    private static string N(float v) => (MathF.Round(v, 4) + 0f).ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Rows of the layers panel, already flattened in display order with their nesting depth.</summary>
    public string Layers()
    {
        var doc = _workspace.Document;
        var sb = new StringBuilder("[");
        bool first = true;
        void Row(string id, string kind, string name, int depth, bool visible, bool expanded, bool selected, bool hasChildren, string extra)
        {
            if (!first)
                sb.Append(',');
            first = false;
            sb.Append("{\"id\":").Append(Q(id)).Append(",\"kind\":\"").Append(kind).Append("\",\"name\":").Append(Q(name));
            sb.Append(",\"depth\":").Append(depth).Append(",\"visible\":").Append(B(visible)).Append(",\"expanded\":").Append(B(expanded));
            sb.Append(",\"selected\":").Append(B(selected)).Append(",\"hasChildren\":").Append(B(hasChildren)).Append(extra).Append('}');
        }

        foreach (var garment in doc.Garments)
        {
            bool active = garment.Id == _workspace.ActiveGarmentId;
            bool simulated = _simulation.SimulatedGarments.Contains(garment.Id);
            string extra = $",\"active\":{B(active)},\"simulateWithOthers\":{B(garment.SimulateWithOthers)},\"simulating\":{B(simulated)}"
                         + $",\"faces\":{(garment.Pieces.Count > 0 ? _workspace.MeshFor(garment).FaceCount : 0)}";
            Row(garment.Id, "garment", garment.Name, 0, garment.Visible, garment.Expanded,
                _workspace.SelectedLayerId == garment.Id, garment.Pieces.Count + garment.Groups.Count > 0, extra);
            if (!garment.Expanded)
                continue;

            void Children(string? parent, int depth)
            {
                foreach (var group in garment.Groups.Where(g => g.ParentId == parent))
                {
                    bool any = garment.Groups.Any(g => g.ParentId == group.Id) || garment.Pieces.Any(p => p.GroupId == group.Id);
                    Row(group.Id, "group", group.Name, depth, group.Visible, group.Expanded, _workspace.SelectedLayerId == group.Id, any,
                        $",\"garment\":{Q(garment.Id)}");
                    if (group.Expanded)
                        Children(group.Id, depth + 1);
                }
                foreach (var piece in garment.Pieces.Where(p => p.GroupId == parent))
                {
                    var material = doc.FindMaterial(piece.MaterialId);
                    Row(piece.Id, "piece", piece.Name, depth, piece.Visible, false, _workspace.SelectedPieces.Contains(piece.Id), false,
                        $",\"garment\":{Q(garment.Id)},\"color\":{Q(material?.Color ?? "#C8CED8")}");
                }
            }
            Children(null, 1);
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>Groups of the active garment, for the "Move to" menu.</summary>
    public string GroupChoices()
    {
        var garment = _workspace.ActiveGarment;
        var sb = new StringBuilder("[");
        if (garment != null)
        {
            for (int i = 0; i < garment.Groups.Count; i++)
            {
                if (i > 0)
                    sb.Append(',');
                sb.Append("{\"id\":").Append(Q(garment.Groups[i].Id)).Append(",\"name\":").Append(Q(garment.Groups[i].Name)).Append('}');
            }
        }
        return sb.Append(']').ToString();
    }

    /// <summary>Everything the properties panel shows for the current selection.</summary>
    public string Selection()
    {
        var doc = _workspace.Document;
        var sb = new StringBuilder("{");
        var pieces = _workspace.SelectedPieceObjects().ToList();
        var garment = _workspace.ActiveGarment;
        var seam = doc.Garments.SelectMany(g => g.Seams).FirstOrDefault(s => s.Id == _workspace.SelectedSeamId);
        string kind = seam != null ? "seam" : pieces.Count > 0 ? "pieces" : garment != null ? "garment" : "none";
        sb.Append("\"kind\":\"").Append(kind).Append("\",\"count\":").Append(pieces.Count);

        if (garment != null)
        {
            var mesh = garment.Pieces.Count > 0 ? _workspace.MeshFor(garment) : null;
            sb.Append(",\"garment\":{\"id\":").Append(Q(garment.Id)).Append(",\"name\":").Append(Q(garment.Name));
            sb.Append(",\"particleDistance\":").Append(N(garment.ParticleDistance));
            sb.Append(",\"faces\":").Append(mesh?.FaceCount ?? 0).Append(",\"vertices\":").Append(mesh?.VertexCount ?? 0);
            sb.Append(",\"quads\":").Append(mesh?.QuadCount ?? 0).Append(",\"topology\":\"").Append(garment.Topology).Append('"');
            sb.Append(",\"pieces\":").Append(garment.Pieces.Count).Append(",\"seams\":").Append(garment.Seams.Count);
            sb.Append(",\"simulateWithOthers\":").Append(B(garment.SimulateWithOthers)).Append('}');
        }

        if (pieces.Count > 0)
        {
            var piece = pieces[0];
            var owner = doc.GarmentOfPiece(piece.Id)!;
            var pieceMesh = _workspace.MeshFor(owner).Find(piece.Id);
            var material = doc.FindMaterial(piece.MaterialId);
            var layout = UvLayout.For(owner);
            sb.Append(",\"piece\":{\"id\":").Append(Q(piece.Id)).Append(",\"name\":").Append(Q(pieces.Count == 1 ? piece.Name : $"{pieces.Count} pieces"));
            sb.Append(",\"posX\":").Append(N(piece.Placement.Position.X)).Append(",\"posY\":").Append(N(piece.Placement.Position.Y)).Append(",\"posZ\":").Append(N(piece.Placement.Position.Z));
            sb.Append(",\"rotX\":").Append(N(piece.Placement.Rotation.X)).Append(",\"rotY\":").Append(N(piece.Placement.Rotation.Y)).Append(",\"rotZ\":").Append(N(piece.Placement.Rotation.Z));
            sb.Append(",\"curvature\":").Append(N(piece.Placement.Curvature));
            // How many of the selected pieces have their normals inverted.
            sb.Append(",\"flippedNormals\":").Append(pieces.Count(p => p.FlipNormals)).Append(",\"count\":").Append(pieces.Count);
            sb.Append(",\"particleDistance\":").Append(N(piece.ParticleDistance ?? 0));
            sb.Append(",\"area\":").Append(N(pieces.Sum(Outline.Area)));
            sb.Append(",\"faces\":").Append(pieces.Sum(p => _workspace.MeshFor(doc.GarmentOfPiece(p.Id)!).Find(p.Id)?.FaceCount ?? 0));
            sb.Append(",\"vertices\":").Append(pieceMesh?.VertexCount ?? 0);
            sb.Append(",\"draped\":").Append(B(pieceMesh != null && piece.Drape != null && piece.DrapeKey == pieceMesh.Key));
            sb.Append(",\"uvScale\":").Append(N(piece.Uv.Scale)).Append(",\"uvRotation\":").Append(N(piece.Uv.Rotation));
            sb.Append(",\"texelDensity\":").Append(N(layout.TexelDensity(piece, material, _settings.TextureResolution)));
            sb.Append(",\"textureResolution\":").Append(_settings.TextureResolution).Append('}');

            var ph = piece.Physics;
            sb.Append(",\"physics\":{\"preset\":").Append(Q(ph.Preset));
            sb.Append(",\"density\":").Append(N(ph.Density)).Append(",\"stretch\":").Append(N(ph.Stretch)).Append(",\"shear\":").Append(N(ph.Shear));
            sb.Append(",\"bend\":").Append(N(ph.Bend)).Append(",\"damping\":").Append(N(ph.Damping)).Append(",\"friction\":").Append(N(ph.Friction));
            sb.Append(",\"thickness\":").Append(N(ph.Thickness)).Append('}');

            if (material != null)
            {
                sb.Append(",\"material\":{\"id\":").Append(Q(material.Id)).Append(",\"name\":").Append(Q(material.Name));
                sb.Append(",\"color\":").Append(Q(material.Color)).Append(",\"texture\":").Append(Q(material.Texture));
                sb.Append(",\"textureName\":").Append(Q(material.Texture == null ? "" : Path.GetFileName(material.Texture)));
                sb.Append(",\"textureMissing\":").Append(B(material.Texture != null && !File.Exists(material.Texture)));
                sb.Append(",\"repeat\":").Append(N(material.Repeat)).Append(",\"roughness\":").Append(N(material.Roughness));
                sb.Append(",\"metallic\":").Append(N(material.Metallic)).Append(",\"opacity\":").Append(N(material.Opacity));
                sb.Append(",\"users\":").Append(doc.AllPieces().Count(p => p.MaterialId == material.Id)).Append('}');
            }
        }

        if (seam != null)
        {
            var owner = doc.Garments.First(g => g.Seams.Contains(seam));
            var a = owner.FindPiece(seam.A.PieceId);
            var b = owner.FindPiece(seam.B.PieceId);
            sb.Append(",\"seam\":{\"id\":").Append(Q(seam.Id)).Append(",\"reversed\":").Append(B(seam.Reversed));
            sb.Append(",\"a\":").Append(Q(a == null ? "?" : $"{a.Name} · edge {seam.A.Edge + 1}"));
            sb.Append(",\"b\":").Append(Q(b == null ? "?" : $"{b.Name} · edge {seam.B.Edge + 1}"));
            sb.Append(",\"lengthA\":").Append(N(a == null ? 0 : Outline.EdgeLength(a, seam.A.Edge)));
            sb.Append(",\"lengthB\":").Append(N(b == null ? 0 : Outline.EdgeLength(b, seam.B.Edge))).Append('}');
        }

        sb.Append(",\"presets\":[").Append(string.Join(",", FabricPhysics.Presets.Select(p => Q(p.Preset)))).Append(']');
        sb.Append(",\"materials\":[").Append(string.Join(",", doc.Materials.Select(m => $"{{\"id\":{Q(m.Id)},\"name\":{Q(m.Name)},\"color\":{Q(m.Color)}}}"))).Append(']');
        var sim = doc.Simulation;
        sb.Append(",\"simulation\":{\"gravity\":").Append(N(sim.Gravity)).Append(",\"substeps\":").Append(sim.Substeps);
        sb.Append(",\"timeScale\":").Append(N(sim.TimeScale)).Append(",\"selfCollision\":").Append(B(sim.SelfCollision));
        sb.Append(",\"groundPlane\":").Append(B(sim.GroundPlane)).Append('}');
        sb.Append(",\"totalFaces\":").Append(doc.Garments.Where(g => g.Pieces.Count > 0).Sum(g => _workspace.MeshFor(g).FaceCount));
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>Applies command-line options: a file to open and the switches used for automated screenshots.</summary>
    private void ApplyStartupOptions()
    {
        var options = Startup;
        if (options.File != null)
            Open(options.File);
        if (options.Gender is { } gender)
        {
            _workspace.Document.Avatar.Kind = AvatarKind.MakeHuman;
            _workspace.Document.Avatar.Gender = gender;
        }
        if (options.Avatar != null && Enum.TryParse<AvatarKind>(options.Avatar, true, out var kind))
            _workspace.Document.Avatar.Kind = kind;
        if (options.SimulateFrames > 0)
        {
            // Startup simulation runs before the window exists, so the avatar is built right here.
            _avatar = _workspace.Document.Avatar.Kind == AvatarKind.MakeHuman
                ? Seamstress.Core.Simulation.Avatar.FromMakeHumanBase(_settings.MakeHumanBaseMesh, _workspace.Document.Avatar)
                : Seamstress.Core.Simulation.Avatar.CreateMannequin();
            _avatarKey = AvatarKeyFor(_workspace.Document.Avatar);
        }
        foreach (string template in options.Templates)
            _workspace.AddTemplate(template, _avatar?.Mesh.Bounds().Max.Y ?? Templates.ReferenceBodyHeight);
        if (options.Quads)
        {
            foreach (var garment in _workspace.Document.Garments)
                garment.Topology = MeshTopology.Quads;
        }
        if (options.Templates.Count > 0)
            _workspace.History.MarkSaved();
        if (options.SimulateFrames > 0 && _simulation.Start(true) == null)
        {
            _simulation.RunFrames(options.SimulateFrames);
            _simulation.Stop();
            if (options.View.Contains("pinch") && _workspace.Document.Garments.FirstOrDefault() is { Pieces.Count: > 0 } first
                && _simulation.Start(true) == null)
            {
                // For documentation screenshots: hold the front hem pulled out, as if caught mid-pinch.
                var mesh = _workspace.MeshFor(first).Pieces[0];
                var hem = mesh.EdgeChains[0];
                var point = Seamstress.Core.Geometry.Mesher.CurrentPositions(first.Pieces[0], mesh)[hem[hem.Length / 2]];
                _simulation.BeginPinch(point);
                _simulation.MovePinch(point + new System.Numerics.Vector3(0.05f, 0.06f, 0.16f));
                _simulation.RunFrames(45);
                _simulation.Stop();
            }
        }
        if (options.Tool != null && Enum.TryParse<PatternTool>(options.Tool, true, out var tool))
            _editor.Tool = tool;
        _editor.UvMode = options.UvMode;
        if (options.SelectFirst && _workspace.Document.AllPieces().FirstOrDefault() is { } firstPiece)
            _workspace.SelectPiece(firstPiece.Id);
        if (options.View.Contains("measure") && _workspace.Document.AllPieces().FirstOrDefault() is { } measured)
        {
            // For documentation screenshots: click the middle of the first piece's first edge.
            var middle = Seamstress.Core.Geometry.Outline.SampleEdge(measured, 0, 2)[1] + measured.Origin;
            _editor.PointerDown(middle, 0.5f, false);
            _editor.PointerUp(middle);
        }
        if (options.View.Contains("points") && _workspace.Document.AllPieces().FirstOrDefault() is { } shaped)
        {
            // For documentation screenshots: the Transform Points tool with the first piece's first two points chosen.
            _editor.Tool = PatternTool.TransformPoints;
            foreach (var point in shaped.Points.Take(2))
            {
                _editor.PointerDown(point.Position + shaped.Origin, 0.5f, true);
                _editor.PointerUp(point.Position + shaped.Origin);
            }
        }
        if (options.View.Contains("ruler") && _workspace.Document.AllPieces().FirstOrDefault() is { Points.Count: >= 3 } ruled)
        {
            // For documentation screenshots: the Ruler from the first piece's first point to its third.
            _editor.Tool = PatternTool.Ruler;
            var from = ruled.Points[0].Position + ruled.Origin;
            var to = ruled.Points[2].Position + ruled.Origin;
            _editor.PointerDown(from, 0.5f, false);
            _editor.PointerMove((from + to) / 2, 0.5f);
            _editor.PointerMove(to, 0.5f);
            _editor.PointerUp(to);
        }
        // For documentation screenshots: "--view gizmoscale" (or gizmomove, gizmorotate) shows only those handles.
        foreach (var mode in Enum.GetValues<GizmoMode>())
        {
            if (options.View.Contains("gizmo" + mode.ToString().ToLowerInvariant()))
                _editor.GizmoMode = mode;
        }
        if (options.SelectPiece != null)
        {
            // A piece, group or garment by name; "seam" selects the first seam.
            var doc = _workspace.Document;
            if (options.SelectPiece == "seam" && doc.Garments.SelectMany(g => g.Seams).FirstOrDefault() is { } seam)
                _workspace.SelectSeam(seam.Id);
            else if (doc.AllPieces().FirstOrDefault(p => p.Name == options.SelectPiece) is { } named)
                _workspace.SelectPiece(named.Id);
            else if (doc.Garments.FirstOrDefault(g => g.Name == options.SelectPiece) is { } garment)
                _workspace.SelectLayer(garment.Id);
        }
        // For documentation screenshots: "--view invert" turns the selected pieces' normals the other way.
        if (options.View.Contains("invert"))
            _workspace.InvertNormals();
    }
}
