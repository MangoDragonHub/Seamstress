using System.Numerics;
using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.IO;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;
using Xunit;

namespace Seamstress.Tests;

public class MeshTests
{
    private static PatternPiece Square(float size = 40)
    {
        var piece = new PatternPiece { Name = "Square" };
        piece.Points.Add(new PatternPoint(0, 0));
        piece.Points.Add(new PatternPoint(size, 0));
        piece.Points.Add(new PatternPoint(size, size));
        piece.Points.Add(new PatternPoint(0, size));
        return piece;
    }

    private static void AssertSound(PatternPiece piece, PieceMesh mesh)
    {
        Assert.True(mesh.TriangleCount > 0);
        var used = new bool[mesh.VertexCount];
        var edges = new HashSet<(int, int)>();
        double area = 0;
        for (int t = 0; t < mesh.Triangles.Length; t += 3)
        {
            int a = mesh.Triangles[t], b = mesh.Triangles[t + 1], c = mesh.Triangles[t + 2];
            used[a] = used[b] = used[c] = true;
            var ab = mesh.Points[b] - mesh.Points[a];
            var ac = mesh.Points[c] - mesh.Points[a];
            double tri = (ab.X * ac.Y - ab.Y * ac.X) * 0.5;
            Assert.True(tri > 0, "triangles must be counter-clockwise with positive area");
            area += tri;
            foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
                edges.Add((Math.Min(u, v), Math.Max(u, v)));
        }
        Assert.All(used, Assert.True);
        Assert.InRange(area, Outline.Area(piece) * 0.97, Outline.Area(piece) * 1.03);
        // Every boundary segment must be a real mesh edge, otherwise seams would stitch unconnected vertices.
        foreach (var chain in mesh.EdgeChains)
        {
            for (int i = 1; i < chain.Length; i++)
                Assert.Contains((Math.Min(chain[i - 1], chain[i]), Math.Max(chain[i - 1], chain[i])), edges);
        }
    }

    [Fact]
    public void SquareMeshCoversThePiece()
    {
        var garment = new Garment { ParticleDistance = 2 };
        garment.Pieces.Add(Square());
        var mesh = Mesher.Build(garment);
        AssertSound(garment.Pieces[0], mesh.Pieces[0]);
        Assert.Equal(4, mesh.Pieces[0].EdgeChains.Length);
        Assert.Equal(21, mesh.Pieces[0].EdgeChains[0].Length);
    }

    [Theory]
    [InlineData("T-Shirt")]
    [InlineData("Tank Top")]
    [InlineData("Skirt")]
    [InlineData("Dress")]
    public void TemplatesMeshCleanly(string template)
    {
        var doc = new Document();
        var garment = Templates.Create(template, doc);
        foreach (float distance in new[] { 4f, 2f, 1f })
        {
            garment.ParticleDistance = distance;
            var mesh = Mesher.Build(garment);
            Assert.Equal(garment.Pieces.Count, mesh.Pieces.Count);
            for (int i = 0; i < mesh.Pieces.Count; i++)
                AssertSound(garment.Pieces[i], mesh.Pieces[i]);
            Assert.NotEmpty(mesh.Stitches);
        }
    }

    [Fact]
    public void SmallerParticleDistanceGivesMoreFaces()
    {
        var doc = new Document();
        var garment = Templates.Create("Tank Top", doc);
        int previous = 0;
        foreach (float distance in new[] { 5f, 3f, 2f, 1f })
        {
            garment.ParticleDistance = distance;
            int faces = Mesher.Build(garment).TriangleCount;
            Assert.True(faces > previous * 1.5, $"{faces} faces at {distance} cm should clearly exceed {previous}");
            previous = faces;
        }
    }

    [Fact]
    public void SewnEdgesGetMatchingVertexCounts()
    {
        var garment = new Garment { ParticleDistance = 2 };
        var a = Square(40);
        var b = Square(25);
        garment.Pieces.Add(a);
        garment.Pieces.Add(b);
        garment.Seams.Add(new Seam { A = new EdgeRef(a.Id, 1), B = new EdgeRef(b.Id, 3) });
        var mesh = Mesher.Build(garment);
        Assert.Equal(mesh.Pieces[0].EdgeChains[1].Length, mesh.Pieces[1].EdgeChains[3].Length);
        Assert.Equal(mesh.Pieces[0].EdgeChains[1].Length, mesh.Stitches.Count);
        // Not reversed: the start of A meets the end of B.
        var first = mesh.Stitches[0];
        Assert.Equal(mesh.Pieces[0].EdgeChains[1][0], first.VertexA);
        Assert.Equal(mesh.Pieces[1].EdgeChains[3][^1], first.VertexB);
        AssertSound(a, mesh.Pieces[0]);
        AssertSound(b, mesh.Pieces[1]);
    }

    [Fact]
    public void ConcaveAndCurvedOutlinesMesh()
    {
        var piece = new PatternPiece { Name = "L" };
        foreach (var (x, y) in new[] { (0f, 0f), (40f, 0f), (40f, 15f), (15f, 15f), (15f, 40f), (0f, 40f) })
            piece.Points.Add(new PatternPoint(x, y));
        piece.Points[1].Curve = new Vector2(48, 7);
        var garment = new Garment { ParticleDistance = 1.5f };
        garment.Pieces.Add(piece);
        AssertSound(piece, Mesher.Build(garment).Pieces[0]);
    }
}

public class QuadTests
{
    [Theory]
    [InlineData("T-Shirt")]
    [InlineData("Skirt")]
    public void QuadTopologyIsMostlyQuadsAndCoversTheSameSurface(string template)
    {
        var doc = new Document();
        var garment = Templates.Create(template, doc);
        garment.Topology = MeshTopology.Quads;
        var mesh = Mesher.Build(garment);
        Assert.True(mesh.QuadCount > mesh.FaceCount * 0.75, $"{mesh.QuadCount} quads of {mesh.FaceCount} faces");
        for (int i = 0; i < mesh.Pieces.Count; i++)
        {
            var piece = mesh.Pieces[i];
            Assert.NotNull(piece.Polygons);
            // Every triangle belongs to exactly one face, so faces and triangles describe the same surface.
            Assert.Equal(piece.TriangleCount, piece.Polygons!.Sum(p => p.Length - 2));
            double area = 0;
            foreach (var face in piece.Polygons!)
            {
                Assert.InRange(face.Length, 3, 4);
                Assert.Equal(face.Length, face.Distinct().Count());
                double a = 0;
                for (int k = 0; k < face.Length; k++)
                {
                    var p = piece.Points[face[k]];
                    var q = piece.Points[face[(k + 1) % face.Length]];
                    a += (double)p.X * q.Y - (double)q.X * p.Y;
                }
                Assert.True(a > 0, "faces are counter-clockwise");
                area += a / 2;
            }
            Assert.InRange(area, Outline.Area(garment.Pieces[i]) * 0.97, Outline.Area(garment.Pieces[i]) * 1.03);
        }
        // Seams still pair up vertex for vertex.
        Assert.NotEmpty(mesh.Stitches);

        garment.Topology = MeshTopology.Triangles;
        Assert.Equal(0, Mesher.Build(garment).QuadCount);
    }

    [Fact]
    public void QuadsReachObjAndFbxButGltfStaysTriangles()
    {
        var ws = new Workspace();
        var garment = ws.AddTemplate("Skirt");
        ws.SetTopology(garment.Id, MeshTopology.Quads);
        garment = ws.Document.Garments[0];
        var mesh = ws.MeshFor(garment);
        var scene = Seamstress.Core.IO.ExportScene.Build(ws.Document, ws.MeshFor, new Seamstress.Core.IO.ExportOptions());
        Assert.Equal(mesh.FaceCount, scene.Meshes[0].FaceCount);
        Assert.Equal(mesh.TriangleCount, scene.Meshes[0].TriangleCount);

        string folder = Path.Combine(Path.GetTempPath(), "seamstress-quads-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Seamstress.Core.IO.ObjExporter.Export(scene, Path.Combine(folder, "q.obj"));
            var faces = File.ReadLines(Path.Combine(folder, "q.obj")).Where(l => l.StartsWith("f ")).ToList();
            Assert.Equal(mesh.FaceCount, faces.Count);
            Assert.Equal(mesh.QuadCount, faces.Count(f => f.Split(' ').Length == 5));
            Seamstress.Core.IO.FbxExporter.Export(scene, Path.Combine(folder, "q.fbx"));
            Seamstress.Core.IO.GltfExporter.Export(scene, Path.Combine(folder, "q.glb"));
            Assert.True(new FileInfo(Path.Combine(folder, "q.fbx")).Length > 1000);
            if (Environment.GetEnvironmentVariable("SEAMSTRESS_DUMP") is { } dump)
            {
                foreach (var f in Directory.GetFiles(folder))
                    File.Copy(f, Path.Combine(dump, Path.GetFileName(f)), true);
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }

        // Undo returns to triangles; the saved file remembers the choice.
        Assert.Contains("\"topology\": \"quads\"", Seamstress.Core.IO.GarmentFile.Serialize(ws.Document));
        ws.History.Undo();
        Assert.Equal(MeshTopology.Triangles, ws.Document.Garments[0].Topology);
    }
}

public class DocumentTests
{
    [Fact]
    public void JsonRoundTripPreservesEverything()
    {
        var ws = new Workspace();
        ws.AddTemplate("T-Shirt");
        ws.AddTemplate("Skirt");
        var doc = ws.Document;
        doc.Garments[0].Pieces[0].Physics = FabricPhysics.FromPreset("Silk");
        doc.Garments[0].Pieces[0].Uv.Scale = 1.5f;
        doc.Garments[0].Pieces[1].ParticleDistance = 1.25f;
        doc.Garments[1].SimulateWithOthers = true;
        doc.Materials[0].Texture = "/tmp/some/fabric.png";
        var mesh = ws.MeshFor(doc.Garments[1]).Pieces[0];
        doc.Garments[1].Pieces[0].Drape = Mesher.Arrange(doc.Garments[1].Pieces[0], mesh).SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray();
        doc.Garments[1].Pieces[0].DrapeKey = mesh.Key;

        string json = GarmentFile.Serialize(doc);
        var loaded = GarmentFile.Deserialize(json);
        Assert.Equal(json, GarmentFile.Serialize(loaded));
        Assert.Equal(2, loaded.Garments.Count);
        Assert.Equal("Silk", loaded.Garments[0].Pieces[0].Physics.Preset);
        Assert.Equal(doc.Garments[0].Seams.Count, loaded.Garments[0].Seams.Count);
        Assert.Equal(doc.Garments[0].Groups.Count, loaded.Garments[0].Groups.Count);
        Assert.NotNull(loaded.Garments[0].Pieces[0].Points[2].Curve);
        Assert.Equal(mesh.Key, loaded.Garments[1].Pieces[0].DrapeKey);
        Assert.Contains("\"at\": [", json);
    }

    [Fact]
    public void HandWrittenMinimalFileLoads()
    {
        const string json = """
        {
          "garments": [
            { "name": "Scarf",
              "pieces": [ { "name": "Strip", "points": [ [0, 0], [120, 0], { "at": [120, 20] }, { "at": [0, 20], "curve": [-5, 10] } ] } ] }
          ]
        }
        """;
        var doc = GarmentFile.Deserialize(json);
        var piece = Assert.Single(Assert.Single(doc.Garments).Pieces);
        Assert.Equal(4, piece.Points.Count);
        Assert.Equal(new Vector2(-5, 10), piece.Points[3].Curve);
        Assert.Equal("Cotton", piece.Physics.Preset);
    }

    [Fact]
    public void RejectsForeignJson()
    {
        Assert.Throws<InvalidDataException>(() => GarmentFile.Deserialize("{\"format\":\"something-else\"}"));
        Assert.Throws<InvalidDataException>(() => GarmentFile.Deserialize("[1,2,3]"));
    }

    [Fact]
    public void UndoRedoRestoresDocumentAndDirtyFlag()
    {
        var ws = new Workspace();
        Assert.False(ws.History.IsDirty);
        ws.AddTemplate("Tank Top");
        var garment = ws.Document.Garments[0];
        string frontId = garment.Pieces[0].Id;
        int seams = garment.Seams.Count;
        ws.SelectPiece(frontId);
        ws.DeleteSelection();
        Assert.Single(ws.Document.Garments[0].Pieces);
        Assert.True(ws.Document.Garments[0].Seams.Count < seams);

        Assert.True(ws.History.Undo());
        Assert.Equal(2, ws.Document.Garments[0].Pieces.Count);
        Assert.Equal(seams, ws.Document.Garments[0].Seams.Count);
        Assert.True(ws.History.Undo());
        Assert.Empty(ws.Document.Garments);
        Assert.False(ws.History.IsDirty);
        Assert.False(ws.History.CanUndo);

        Assert.True(ws.History.Redo());
        Assert.True(ws.History.Redo());
        Assert.Single(ws.Document.Garments[0].Pieces);
        Assert.True(ws.History.IsDirty);
        Assert.Empty(ws.SelectedPieces);
    }

    [Fact]
    public void DraggingIsOneUndoStepAndClicksAreNone()
    {
        var ws = new Workspace();
        ws.AddTemplate("Skirt");
        var editor = new PatternEditor(ws);
        var piece = ws.Document.Garments[0].Pieces[0];
        var start = piece.Origin + new Vector2(30, 20);
        int depthBefore = CountUndo(ws);

        editor.PointerDown(start, 1, false);
        editor.PointerUp(start);
        Assert.Contains(piece.Id, ws.SelectedPieces);
        Assert.Equal(depthBefore, CountUndo(ws));

        var origin = piece.Origin;
        editor.PointerDown(start, 1, false);
        for (int i = 1; i <= 10; i++)
            editor.PointerMove(start + new Vector2(i, i * 0.5f), 1);
        editor.PointerUp(start + new Vector2(10, 5));
        Assert.Equal(origin + new Vector2(10, 5), ws.Document.FindPiece(piece.Id)!.Origin);
        Assert.Equal(depthBefore + 1, CountUndo(ws));
        ws.History.Undo();
        Assert.Equal(origin, ws.Document.FindPiece(piece.Id)!.Origin);
    }

    private static int CountUndo(Workspace ws)
    {
        int n = 0;
        var probe = ws.History;
        while (probe.CanUndo) { probe.Undo(); n++; }
        for (int i = 0; i < n; i++) probe.Redo();
        return n;
    }

    [Fact]
    public void ShiftKeepsDrawnShapesInProportion()
    {
        var ws = new Workspace();
        var editor = new PatternEditor(ws) { Constrain = true };

        // A rectangle dragged 30 × 12 becomes a 30 × 30 square, growing the way the drag goes.
        editor.Tool = PatternTool.Rectangle;
        editor.PointerDown(new Vector2(10, 10), 1, false);
        editor.PointerMove(new Vector2(40, -2), 1);
        editor.PointerUp(new Vector2(40, -2));
        var square = ws.Document.Garments[0].Pieces[0];
        var world = square.Points.Select(p => p.Position + square.Origin).ToList();
        Assert.Equal(10f, world.Min(p => p.X), 0.001f);
        Assert.Equal(40f, world.Max(p => p.X), 0.001f);
        Assert.Equal(-20f, world.Min(p => p.Y), 0.001f);
        Assert.Equal(10f, world.Max(p => p.Y), 0.001f);

        // An ellipse becomes a circle.
        editor.Tool = PatternTool.Ellipse;
        editor.PointerDown(new Vector2(100, 0), 1, false);
        editor.PointerMove(new Vector2(120, 50), 1);
        Assert.Contains("\"draftSize\":[125,25,50,50]", editor.SceneJson(2048));
        editor.PointerUp(new Vector2(120, 50));

        // Each polygon side snaps on its own: angle to 15° steps, length to whole centimetres.
        editor.Tool = PatternTool.Polygon;
        editor.PointerDown(new Vector2(200, 0), 1, false);
        editor.PointerDown(new Vector2(230.4f, 1.5f), 1, false);    // nearly horizontal, 30.4 long -> 30 along X
        editor.PointerDown(new Vector2(231, 20.3f), 1, false);      // nearly vertical -> 20 straight up
        editor.Constrain = false;
        editor.PointerDown(new Vector2(203.3f, 17.7f), 1, false);   // free again
        editor.Confirm();
        var polygon = ws.Document.Garments[0].Pieces[2];
        var corners = polygon.Points.Select(p => p.Position + polygon.Origin).ToList();
        Assert.Equal(4, corners.Count);
        Assert.Contains(corners, c => Vector2.Distance(c, new Vector2(230, 0)) < 1e-3f);
        Assert.Contains(corners, c => Vector2.Distance(c, new Vector2(230, 20)) < 1e-3f);
        Assert.Contains(corners, c => Vector2.Distance(c, new Vector2(203.3f, 17.7f)) < 1e-3f);
    }

    [Fact]
    public void ClickingAnEdgeReportsItsLength()
    {
        var ws = new Workspace();
        var editor = new PatternEditor(ws);
        editor.Tool = PatternTool.Rectangle;
        editor.PointerDown(new Vector2(0, 0), 1, false);
        editor.PointerMove(new Vector2(30, 40), 1);
        Assert.Contains("\"draftMeasures\":[15,0,30,30,20,40]", editor.SceneJson(2048));
        editor.PointerUp(new Vector2(30, 40));

        editor.Tool = PatternTool.Select;
        Assert.Contains("\"measures\":[]", editor.SceneJson(2048));
        editor.PointerDown(new Vector2(30.5f, 20), 1, false);   // just outside the right-hand edge
        editor.PointerUp(new Vector2(30.5f, 20));
        Assert.Contains("\"measures\":[30,20,40,-1,0]", editor.SceneJson(2048));
        editor.PointerDown(new Vector2(15, 20), 1, false);      // the middle of the piece
        editor.PointerUp(new Vector2(15, 20));
        Assert.Contains("\"measures\":[]", editor.SceneJson(2048));

        // Dragging a corner shows the two edges that meet there.
        editor.Tool = PatternTool.EditPoints;
        editor.PointerDown(new Vector2(30, 40), 1, false);
        editor.PointerUp(new Vector2(30, 40));
        Assert.Contains("\"measures\":[30,20,40,-1,0,15,40,30,0,-1]", editor.SceneJson(2048));
    }

    [Fact]
    public void ShiftSnapsEditedPointsToTheGrid()
    {
        var ws = new Workspace();
        var editor = new PatternEditor(ws) { GridStep = 10 };
        editor.Tool = PatternTool.Rectangle;
        editor.PointerDown(new Vector2(0, 0), 1, false);
        editor.PointerMove(new Vector2(30, 40), 1);
        editor.PointerUp(new Vector2(30, 40));
        Vector2 Corner()
        {
            var piece = ws.Document.Garments[0].Pieces[0];     // fetched again because undo restores a copy
            return piece.Points.Select(p => p.Position + piece.Origin).OrderBy(p => Vector2.Distance(p, new Vector2(40, 50))).First();
        }

        // Without Shift the corner follows the pointer; with Shift it lands on the nearest grid line crossing.
        editor.Tool = PatternTool.EditPoints;
        editor.PointerDown(new Vector2(30, 40), 1, false);
        editor.PointerMove(new Vector2(43.2f, 47.9f), 1);
        Assert.True(Vector2.Distance(Corner(), new Vector2(43.2f, 47.9f)) < 1e-3f);
        editor.Constrain = true;                                  // pressing Shift mid-drag snaps at once
        Assert.True(Vector2.Distance(Corner(), new Vector2(40, 50)) < 1e-3f);
        editor.PointerMove(new Vector2(56, 51), 1);
        Assert.True(Vector2.Distance(Corner(), new Vector2(60, 50)) < 1e-3f);
        editor.PointerUp(new Vector2(56, 51));

        // A smaller grid on screen gives finer steps, and the drag is one undo step.
        editor.GridStep = 1;
        editor.PointerDown(new Vector2(60, 50), 1, false);
        editor.PointerMove(new Vector2(52.4f, 44.6f), 1);
        editor.PointerUp(new Vector2(52.4f, 44.6f));
        Assert.True(Vector2.Distance(Corner(), new Vector2(52, 45)) < 1e-3f);
        ws.History.Undo();
        Assert.True(Vector2.Distance(Corner(), new Vector2(60, 50)) < 1e-3f);
    }

    [Fact]
    public void TransformPointsMovesAndRotatesASelectionOfPoints()
    {
        var ws = new Workspace();
        var editor = new PatternEditor(ws);
        editor.Tool = PatternTool.Rectangle;
        editor.PointerDown(new Vector2(0, 0), 1, false);
        editor.PointerMove(new Vector2(40, 20), 1);
        editor.PointerUp(new Vector2(40, 20));
        string id = ws.Document.Garments[0].Pieces[0].Id;
        List<Vector2> Corners()
        {
            var piece = ws.Document.FindPiece(id)!;
            return piece.Points.Select(p => p.Position + piece.Origin).ToList();
        }
        void Drag(Vector2 from, Vector2 to, bool additive = false)
        {
            editor.PointerDown(from, 0.9f, additive);   // tolerance 0.9 = one pixel is 0.1 cm
            editor.PointerMove((from + to) / 2, 0.9f);
            editor.PointerMove(to, 0.9f);
            editor.PointerUp(to);
        }
        void Near(Vector2 expected, Vector2 actual) => Assert.True(Vector2.Distance(expected, actual) < 1e-3f, $"{actual} is not {expected}");

        // A box around the right-hand side selects its two corners: (40, 0) and (40, 20).
        editor.Tool = PatternTool.TransformPoints;
        Drag(new Vector2(35, -5), new Vector2(45, 25));
        Assert.Equal(2, editor.SelectedPoints.Count);
        Assert.Contains("\"pointGizmo\":[40,10,64,88,112,40]", editor.SceneJson(2048));
        int undoBefore = CountUndo(ws);

        // The X arrow moves along X only, however the cursor wanders.
        Drag(new Vector2(43, 10), new Vector2(53, 14));
        Near(new Vector2(50, 0), Corners()[1]);
        Near(new Vector2(50, 20), Corners()[2]);
        Near(new Vector2(0, 0), Corners()[0]);
        Assert.Equal(undoBefore + 1, CountUndo(ws));
        Assert.Equal("Move points", ws.History.UndoName);

        // The ring turns the two points about their middle, (50, 10): a quarter turn lays them flat.
        Drag(new Vector2(50 + 8.8f, 10), new Vector2(50, 10 + 8.8f));
        Near(new Vector2(60, 10), Corners()[1]);
        Near(new Vector2(40, 10), Corners()[2]);
        Assert.Equal("Rotate points", ws.History.UndoName);
        ws.History.Undo();
        ws.History.Undo();
        Near(new Vector2(40, 0), Corners()[1]);

        // Ctrl+click adds a third point; a click on empty space clears the selection.
        editor.PointerDown(new Vector2(0, 20), 0.9f, true);
        editor.PointerUp(new Vector2(0, 20));
        Assert.Equal(3, editor.SelectedPoints.Count);
        editor.PointerDown(new Vector2(-30, -30), 0.9f, false);
        editor.PointerUp(new Vector2(-30, -30));
        Assert.Empty(editor.SelectedPoints);

        // This tool never bends an edge: dragging one starts a selection box instead.
        Drag(new Vector2(20, 0), new Vector2(20, 8));
        Assert.All(ws.Document.FindPiece(id)!.Points, p => Assert.Null(p.Curve));
    }

    [Fact]
    public void PiecesScaleInBothViews()
    {
        var ws = new Workspace();
        var editor = new PatternEditor(ws);
        editor.Tool = PatternTool.Rectangle;
        editor.PointerDown(new Vector2(0, 0), 1, false);
        editor.PointerMove(new Vector2(40, 20), 1);
        editor.PointerUp(new Vector2(40, 20));
        string id = ws.Document.Garments[0].Pieces[0].Id;
        (float Width, float Height) Size()
        {
            var (min, max) = Outline.Bounds(ws.Document.FindPiece(id)!);
            return (max.X - min.X, max.Y - min.Y);
        }
        void Drag(Vector2 from, Vector2 to)
        {
            editor.PointerDown(from, 0.9f, false);   // tolerance 0.9 = one pixel is 0.1 cm
            editor.PointerMove((from + to) / 2, 0.9f);
            editor.PointerMove(to, 0.9f);
            editor.PointerUp(to);
        }

        // 2D, Select tool: the piece is selected after drawing, with its handles at (20, 10).
        editor.Tool = PatternTool.Select;
        ws.SelectPiece(id);
        editor.GizmoMode = GizmoMode.Scale;
        Assert.Contains("\"pointGizmo\":[20,10,0,0,64,0]", editor.SceneJson(2048));
        // The X box is 64 pixels (6.4 cm) out; pulling it to twice that doubles the width only.
        Drag(new Vector2(26.4f, 10), new Vector2(32.8f, 13));
        Assert.Equal(80f, Size().Width, 0.01f);
        Assert.Equal(20f, Size().Height, 0.01f);
        Assert.Equal("Scale pieces", ws.History.UndoName);
        ws.History.Undo();
        Assert.Equal(40f, Size().Width, 0.01f);

        // With the move handles, the X arrow slides the piece sideways only.
        editor.GizmoMode = GizmoMode.Move;
        Assert.Contains("\"pointGizmo\":[20,10,64,0,0,0]", editor.SceneJson(2048));
        var origin = ws.Document.FindPiece(id)!.Origin;
        Drag(new Vector2(23, 10), new Vector2(33, 15));
        Assert.Equal(origin + new Vector2(10, 0), ws.Document.FindPiece(id)!.Origin);
        ws.History.Undo();

        // 3D: scaling evenly by 1.5, then along world X only. The piece faces forward, so X is its width.
        Assert.True(ws.BeginPlacementEdit("Scale piece"));
        ws.ApplyPlacementScale(Vector3.Zero, 1.2f);
        ws.ApplyPlacementScale(Vector3.Zero, 1.5f);
        ws.EndPlacementEdit();
        Assert.Equal(60f, Size().Width, 0.01f);
        Assert.Equal(30f, Size().Height, 0.01f);
        Assert.Equal("Scale piece", ws.History.UndoName);
        var rotation = ws.Document.FindPiece(id)!.Placement.Rotation;
        Assert.True(ws.BeginPlacementEdit("Scale piece"));
        ws.ApplyPlacementScale(Vector3.UnitX, 2f);
        ws.EndPlacementEdit();
        Assert.Equal(rotation == Vector3.Zero ? 120f : Size().Width, Size().Width, 0.01f);
        Assert.Equal(30f, Size().Height, 0.01f);
        // The axis pointing out of the flat piece leaves it alone.
        Assert.True(ws.BeginPlacementEdit("Scale piece"));
        ws.ApplyPlacementScale(Vector3.UnitZ, 3f);
        ws.EndPlacementEdit();
        if (rotation == Vector3.Zero)
            Assert.Equal(120f, Size().Width, 0.01f);
        Assert.Equal(30f, Size().Height, 0.01f);
    }

    [Fact]
    public void SewToolCreatesSeamBetweenClickedEdges()
    {
        var ws = new Workspace();
        var editor = new PatternEditor(ws);
        editor.Tool = PatternTool.Rectangle;
        editor.PointerDown(new Vector2(0, 0), 1, false);
        editor.PointerMove(new Vector2(30, 40), 1);
        editor.PointerUp(new Vector2(30, 40));
        editor.PointerDown(new Vector2(50, 0), 1, false);
        editor.PointerMove(new Vector2(80, 40), 1);
        editor.PointerUp(new Vector2(80, 40));
        var garment = Assert.Single(ws.Document.Garments);
        Assert.Equal(2, garment.Pieces.Count);

        editor.Tool = PatternTool.Sew;
        editor.PointerDown(new Vector2(30, 20), 1, false);
        Assert.NotNull(editor.PendingEdge);
        editor.PointerDown(new Vector2(50, 20), 1, false);
        var seam = Assert.Single(garment.Seams);
        Assert.Equal(garment.Pieces[0].Id, seam.A.PieceId);
        Assert.Equal(1, seam.A.Edge);
        Assert.Equal(garment.Pieces[1].Id, seam.B.PieceId);
        Assert.Equal(3, seam.B.Edge);
        Assert.Equal(seam.Id, ws.SelectedSeamId);

        // Sewing the same pair again must not create a duplicate.
        editor.PointerDown(new Vector2(50, 20), 1, false);
        editor.PointerDown(new Vector2(30, 20), 1, false);
        Assert.Single(garment.Seams);
        Assert.Contains("\"seams\":[{", editor.SceneJson(2048));
    }

    [Fact]
    public void PointEditsKeepSeamsOnTheRightEdges()
    {
        var ws = new Workspace();
        ws.AddTemplate("Skirt");
        var garment = ws.Document.Garments[0];
        var front = garment.Pieces[0];
        // Seams use front edges 1 and 3. Splitting edge 0 shifts both by one.
        ws.InsertPoint(front.Id, 0, new Vector2(32, 0));
        garment = ws.Document.Garments[0];
        Assert.Equal(5, garment.Pieces[0].Points.Count);
        Assert.Contains(garment.Seams, s => s.A.Matches(front.Id, 2));
        Assert.Contains(garment.Seams, s => s.A.Matches(front.Id, 4));
        ws.DeletePoint(front.Id, 1);
        garment = ws.Document.Garments[0];
        Assert.Contains(garment.Seams, s => s.A.Matches(front.Id, 1));
        Assert.Contains(garment.Seams, s => s.A.Matches(front.Id, 3));
    }

    [Fact]
    public void GroupingBuildsHierarchy()
    {
        var ws = new Workspace();
        ws.AddTemplate("T-Shirt");
        var garment = ws.Document.Garments[0];
        ws.SelectPiece(garment.Pieces[0].Id);
        ws.SelectPiece(garment.Pieces[1].Id, additive: true);
        var body = ws.GroupSelection();
        Assert.NotNull(body);
        garment = ws.Document.Garments[0];
        Assert.Equal(2, garment.Groups.Count);
        Assert.All(garment.Pieces.Take(2), p => Assert.Equal(body!.Id, p.GroupId));

        string sleeves = garment.Groups[0].Id;
        ws.MoveToGroup(sleeves, body!.Id);
        Assert.Equal(body.Id, ws.Document.Garments[0].FindGroup(sleeves)!.ParentId);
        // A group can never be moved inside itself.
        ws.MoveToGroup(body.Id, sleeves);
        Assert.Null(ws.Document.Garments[0].FindGroup(body.Id)!.ParentId);

        ws.SelectLayer(body.Id);
        Assert.Equal(4, ws.SelectedPieces.Count);
        ws.Ungroup(body.Id);
        Assert.Null(ws.Document.Garments[0].FindGroup(sleeves)!.ParentId);
        Assert.All(ws.Document.Garments[0].Pieces.Take(2), p => Assert.Null(p.GroupId));
    }

    [Fact]
    public void SliderDragMergesIntoOneUndoStep()
    {
        var ws = new Workspace();
        ws.AddTemplate("Skirt");
        ws.SelectPiece(ws.Document.Garments[0].Pieces[0].Id);
        for (int i = 0; i < 20; i++)
            ws.SetPieceValue("bend", i * 5);
        Assert.Equal(95, ws.PrimaryPiece!.Physics.Bend);
        Assert.Equal("Custom", ws.PrimaryPiece!.Physics.Preset);
        ws.History.Undo();
        Assert.Equal(30, ws.PrimaryPiece!.Physics.Bend);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(30, -45, 120)]
    [InlineData(-170, 60, -15)]
    [InlineData(0, 180, 0)]
    [InlineData(10, 90, 0)]
    public void EulerAnglesSurviveARoundTrip(float x, float y, float z)
    {
        var matrix = Workspace.EulerMatrix(new Vector3(x, y, z));
        var again = Workspace.EulerMatrix(Workspace.ToEuler(matrix));
        foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            Assert.True(Vector3.Distance(Vector3.Transform(axis, matrix), Vector3.Transform(axis, again)) < 1e-3f);
    }

    [Fact]
    public void DraggingInThe3DViewMovesArrangementAndDrapeAsOneUndoStep()
    {
        var ws = new Workspace();
        ws.AddTemplate("Skirt");
        var garment = ws.Document.Garments[0];
        var front = garment.Pieces[0];
        var mesh = ws.MeshFor(garment).Pieces[0];
        var flat = Mesher.Arrange(front, mesh);
        front.Drape = flat.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray();
        front.DrapeKey = mesh.Key;
        ws.SelectPiece(front.Id);
        var centre = ws.SelectionCentre()!.Value;
        var startPosition = front.Placement.Position;

        // A click on a handle without dragging leaves no trace.
        Assert.True(ws.BeginPlacementEdit());
        ws.EndPlacementEdit();
        Assert.Equal("Add Skirt", ws.History.UndoName);

        Assert.True(ws.BeginPlacementEdit());
        ws.ApplyPlacementEdit(new Vector3(0.05f, 0, 0), Quaternion.Identity);
        ws.ApplyPlacementEdit(new Vector3(0.10f, 0.02f, 0), Quaternion.Identity);
        ws.EndPlacementEdit();
        front = ws.Document.FindPiece(front.Id)!;
        Assert.Equal(startPosition + new Vector3(0.10f, 0.02f, 0), front.Placement.Position);
        Assert.True(Vector3.Distance(ws.SelectionCentre()!.Value, centre + new Vector3(0.10f, 0.02f, 0)) < 1e-4f);
        // The draped shape moved with it and is still valid for the mesh.
        var draped = Mesher.CurrentPositions(front, mesh);
        Assert.True(Vector3.Distance(draped[0], flat[0] + new Vector3(0.10f, 0.02f, 0)) < 1e-5f);

        // With a snap step the piece lands on a multiple of it along the dragged axis only.
        var beforeSnap = front.Placement.Position;
        Assert.True(ws.BeginPlacementEdit());
        ws.ApplyPlacementEdit(new Vector3(0.133f, 0, 0), Quaternion.Identity, 0.1f);
        ws.EndPlacementEdit();
        front = ws.Document.FindPiece(front.Id)!;
        float snapped = front.Placement.Position.X;
        Assert.Equal(MathF.Round(snapped / 0.1f) * 0.1f, snapped, 1e-5f);
        Assert.InRange(MathF.Abs(snapped - (beforeSnap.X + 0.133f)), 0, 0.0501f);
        Assert.Equal(beforeSnap.Y, front.Placement.Position.Y);
        Assert.Equal(beforeSnap.Z, front.Placement.Position.Z);
        ws.History.Undo();
        front = ws.Document.FindPiece(front.Id)!;

        // Turning 90 degrees about the vertical axis: the arrangement and the drape agree afterwards.
        Assert.True(ws.BeginPlacementEdit());
        ws.ApplyPlacementEdit(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2));
        ws.EndPlacementEdit();
        front = ws.Document.FindPiece(front.Id)!;
        Assert.InRange(front.Placement.Rotation.Y, 89.9f, 90.1f);
        var turned = Mesher.CurrentPositions(front, mesh);
        var rearranged = Mesher.Arrange(front, mesh);
        for (int i = 0; i < turned.Length; i += 37)
            Assert.True(Vector3.Distance(turned[i], rearranged[i]) < 2e-3f, $"vertex {i}: {turned[i]} vs {rearranged[i]}");

        Assert.Equal("Move piece in 3D", ws.History.UndoName);
        ws.History.Undo();
        ws.History.Undo();
        Assert.Equal(startPosition, ws.Document.FindPiece(front.Id)!.Placement.Position);
    }
}
