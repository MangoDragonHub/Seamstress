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
}
