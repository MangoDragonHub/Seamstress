using System.Numerics;
using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.IO;
using Seamstress.Core.Model;

namespace Seamstress.Tests;

/// <summary>Holes in pattern pieces, and Cut Hole, which makes them from two overlapping pieces.</summary>
public class HoleTests
{
    /// <summary>A workspace with a 60 × 50 cm piece and a 20 × 10 cm piece lying inside it, both selected.</summary>
    private static (Workspace Workspace, PatternPiece Target, PatternPiece Cutter) Overlaid(Vector2? cutterCorner = null)
    {
        var ws = new Workspace();
        var target = ws.AddPiece(new[] { new Vector2(0, 0), new Vector2(60, 0), new Vector2(60, 50), new Vector2(0, 50) }, "Rectangle")!;
        var corner = cutterCorner ?? new Vector2(20, 25);
        var cutter = ws.AddPiece(new[] { corner, corner + new Vector2(20, 0), corner + new Vector2(20, 10), corner + new Vector2(0, 10) }, "Rectangle")!;
        ws.SelectPiece(target.Id);
        ws.SelectPiece(cutter.Id, additive: true);
        return (ws, ws.Document.FindPiece(target.Id)!, ws.Document.FindPiece(cutter.Id)!);
    }

    private static Vector2 Centroid(PieceMesh mesh, int t)
        => (mesh.Points[mesh.Triangles[t]] + mesh.Points[mesh.Triangles[t + 1]] + mesh.Points[mesh.Triangles[t + 2]]) / 3;

    [Fact]
    public void APieceWithAHoleMeshesAroundIt()
    {
        var piece = new PatternPiece();
        piece.Points.AddRange(new[] { new PatternPoint(0, 0), new PatternPoint(40, 0), new PatternPoint(40, 40), new PatternPoint(0, 40) });
        // A curved, clockwise hole: its edges are numbered 4 to 7.
        piece.Holes.Add(new List<PatternPoint> { new(10, 10, 4, 18), new(10, 25), new(25, 25), new(25, 10) });
        var garment = new Garment { ParticleDistance = 1.5f };
        garment.Pieces.Add(piece);
        var mesh = Mesher.Build(garment).Pieces[0];

        Assert.Equal(8, piece.EdgeCount);
        Assert.Equal(8, mesh.EdgeChains.Length);
        var hole = Outline.FlattenHoles(piece)[0];
        double area = 0;
        var edges = new HashSet<(int, int)>();
        for (int t = 0; t < mesh.Triangles.Length; t += 3)
        {
            Assert.False(Outline.Contains(hole, Centroid(mesh, t)), "no cloth inside the hole");
            int a = mesh.Triangles[t], b = mesh.Triangles[t + 1], c = mesh.Triangles[t + 2];
            var ab = mesh.Points[b] - mesh.Points[a];
            var ac = mesh.Points[c] - mesh.Points[a];
            area += (ab.X * ac.Y - ab.Y * ac.X) * 0.5;
            foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
                edges.Add((Math.Min(u, v), Math.Max(u, v)));
        }
        Assert.InRange(area, Outline.Area(piece) * 0.97, Outline.Area(piece) * 1.03);
        Assert.InRange(Outline.Area(piece), 40 * 40 - 300, 40 * 40 - 15 * 15 - 20);
        // The hole's edges are mesh edges, and each ends where the next one starts.
        foreach (var chain in mesh.EdgeChains)
        {
            for (int i = 1; i < chain.Length; i++)
                Assert.Contains((Math.Min(chain[i - 1], chain[i]), Math.Max(chain[i - 1], chain[i])), edges);
        }
        Assert.Equal(mesh.EdgeChains[7][^1], mesh.EdgeChains[4][0]);
        Assert.Equal(mesh.EdgeChains[3][^1], mesh.EdgeChains[0][0]);
    }

    [Theory]
    [InlineData(false, 3f)]
    [InlineData(false, 1f)]
    [InlineData(false, 0.6f)]
    [InlineData(true, 3f)]
    [InlineData(true, 2f)]
    [InlineData(true, 1f)]
    [InlineData(true, 0.6f)]
    public void CutPiecesMeshCleanly(bool ellipse, float distance)
    {
        var ws = new Workspace();
        var target = ws.AddPiece(new[] { new Vector2(0, 0), new Vector2(60, 0), new Vector2(60, 50), new Vector2(0, 50) }, "Rectangle")!;
        // Drawn the way the Ellipse tool draws them: sixteen straight sides.
        var shape = ellipse
            ? Enumerable.Range(0, 16).Select(i => new Vector2(30 + 12 * MathF.Cos(i * MathF.Tau / 16), 30 + 7 * MathF.Sin(i * MathF.Tau / 16))).ToArray()
            : new[] { new Vector2(13, 21), new Vector2(31, 21), new Vector2(31, 38), new Vector2(13, 38) };
        var cutter = ws.AddPiece(shape, "Piece")!;
        ws.SelectPiece(target.Id);
        ws.SelectPiece(cutter.Id, additive: true);
        var created = Assert.Single(ws.CutHole());
        var garment = ws.Document.Garments[0];
        garment.ParticleDistance = distance;
        var mesh = Mesher.Build(garment);
        foreach (var id in new[] { target.Id, created.Id })
        {
            var piece = garment.FindPiece(id)!;
            var pieceMesh = mesh.Find(id)!;
            var edges = new HashSet<(int, int)>();
            double area = 0;
            var holes = Outline.FlattenHoles(piece);
            for (int t = 0; t < pieceMesh.Triangles.Length; t += 3)
            {
                int a = pieceMesh.Triangles[t], b = pieceMesh.Triangles[t + 1], c = pieceMesh.Triangles[t + 2];
                var ab = pieceMesh.Points[b] - pieceMesh.Points[a];
                var ac = pieceMesh.Points[c] - pieceMesh.Points[a];
                area += (ab.X * ac.Y - ab.Y * ac.X) * 0.5;
                Assert.DoesNotContain(holes, h => Outline.Contains(h, Centroid(pieceMesh, t)));
                foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
                    edges.Add((Math.Min(u, v), Math.Max(u, v)));
            }
            Assert.InRange(area, Outline.Area(piece) * 0.99, Outline.Area(piece) * 1.01);
            foreach (var chain in pieceMesh.EdgeChains)
            {
                for (int i = 1; i < chain.Length; i++)
                    Assert.Contains((Math.Min(chain[i - 1], chain[i]), Math.Max(chain[i - 1], chain[i])), edges);
            }
        }
    }

    [Fact]
    public void CutHoleMakesAHoleAndACutOutPiece()
    {
        var (ws, target, cutter) = Overlaid();
        var garment = ws.Document.Garments[0];
        Assert.NotNull(ws.PlanCutHole(out var problem));
        Assert.Null(problem);

        var created = Assert.Single(ws.CutHole());
        target = ws.Document.FindPiece(target.Id)!;
        var hole = Assert.Single(target.Holes);
        Assert.Equal(4, hole.Count);
        Assert.True(Outline.SignedArea(hole.Select(p => p.Position).ToList()) < 0, "holes run clockwise");
        // The hole sits where the cutter is, in the target's own coordinates.
        var expected = cutter.Points.Select(p => p.Position + cutter.Origin - target.Origin).ToHashSet();
        Assert.All(hole, p => Assert.Contains(p.Position, expected));
        Assert.Equal(60 * 50 - 20 * 10, Outline.Area(target), 2f);

        // The cut-out is a new piece of the same fabric in the hole's shape; the cutter stays as it was.
        Assert.Equal(3, garment.Pieces.Count);
        Assert.Equal(target.MaterialId, created.MaterialId);
        Assert.Equal(20 * 10, Outline.Area(created), 2f);
        Assert.Equal(cutter.Points.Count, ws.Document.FindPiece(cutter.Id)!.Points.Count);
        Assert.Contains("cut-out", created.Name);
        Assert.Equal(new[] { created.Id }, ws.SelectedPieces);
        Assert.Equal("Cut hole", ws.History.UndoName);
        // Laid out beside the cut piece, and placed in 3D just behind the hole.
        Assert.True(Outline.Bounds(created).Min.X + created.Origin.X >= target.Origin.X + 60);
        Assert.True(created.Placement.Position.Z < target.Placement.Position.Z);

        ws.History.Undo();
        Assert.Empty(ws.Document.FindPiece(target.Id)!.Holes);
        Assert.Equal(2, ws.Document.Garments[0].Pieces.Count);
    }

    [Fact]
    public void TheCutOutIsLaidOutClearOfOtherPieces()
    {
        // The skirt's back piece sits right beside the front, where the cut-out would go first.
        var ws = new Workspace();
        ws.AddTemplate("Skirt");
        var front = ws.Document.Garments[0].Pieces[0];
        var corner = front.Origin + new Vector2(40, 22);
        var cutter = ws.AddPiece(new[] { corner, corner + new Vector2(14, 0), corner + new Vector2(14, 16), corner + new Vector2(0, 16) }, "Rectangle")!;
        ws.SelectPiece(front.Id);
        ws.SelectPiece(cutter.Id, additive: true);
        var created = Assert.Single(ws.CutHole());
        static (Vector2 Min, Vector2 Max) Box(PatternPiece p) => (Outline.Bounds(p).Min + p.Origin, Outline.Bounds(p).Max + p.Origin);
        var (lo, hi) = Box(created);
        foreach (var other in ws.Document.AllPieces().Where(p => p.Id != created.Id))
        {
            var (olo, ohi) = Box(other);
            Assert.False(lo.X < ohi.X && hi.X > olo.X && lo.Y < ohi.Y && hi.Y > olo.Y, $"the cut-out overlaps {other.Name}");
        }
        Assert.Equal(cutter.Origin.Y, created.Origin.Y);
    }

    [Fact]
    public void CutHoleRefusesShapesThatDoNotFit()
    {
        // Only one piece selected.
        var (ws, target, _) = Overlaid();
        ws.SelectPiece(target.Id);
        Assert.Null(ws.PlanCutHole(out var problem));
        Assert.NotNull(problem);
        Assert.Empty(ws.CutHole());

        // Overlapping the edge of the other piece.
        (ws, target, _) = Overlaid(new Vector2(50, 20));
        Assert.Null(ws.PlanCutHole(out problem));
        Assert.NotNull(problem);
        Assert.Empty(ws.CutHole());
        Assert.Empty(ws.Document.FindPiece(target.Id)!.Holes);
        Assert.False(ws.History.UndoName.Contains("hole", StringComparison.OrdinalIgnoreCase));

        // Overlapping a hole the piece already has.
        (ws, target, var cutter) = Overlaid();
        ws.CutHole();
        ws.SelectPiece(target.Id);
        ws.SelectPiece(cutter.Id, additive: true);
        Assert.Null(ws.PlanCutHole(out problem));
    }

    [Fact]
    public void TheHoleCanBeSewnToTheCutOut()
    {
        var (ws, target, cutter) = Overlaid();
        var created = ws.CutHole()[0];
        // The hole runs the other way round the cutter's shape: its first edge is the cut-out's last.
        int holeEdge = target.Points.Count;
        Assert.NotNull(ws.AddSeam(new EdgeRef(target.Id, holeEdge), new EdgeRef(created.Id, 3)));
        var garment = ws.Document.Garments[0];
        var mesh = ws.MeshFor(garment);
        int a = garment.Pieces.FindIndex(p => p.Id == target.Id);
        int b = mesh.Pieces.FindIndex(p => p.PieceId == created.Id);
        Assert.Equal(mesh.Pieces[a].EdgeChains[holeEdge].Length, mesh.Pieces[b].EdgeChains[3].Length);
        Assert.Equal(mesh.Pieces[a].EdgeChains[holeEdge].Length, mesh.Stitches.Count);
        // So a plain seam pairs matching corners: the start of the hole edge with the end of the cut-out's edge.
        var hole = ws.Document.FindPiece(target.Id)!;
        var shift = cutter.Origin - target.Origin;
        Assert.Equal(Outline.EdgeStart(hole, holeEdge) - shift, Outline.EdgeEnd(created, 3));
        Assert.Equal(Outline.EdgeEnd(hole, holeEdge) - shift, Outline.EdgeStart(created, 3));
    }

    [Fact]
    public void HolesSurviveSavingAndLoading()
    {
        var (ws, target, _) = Overlaid();
        var created = ws.CutHole()[0];
        ws.AddSeam(new EdgeRef(target.Id, 5), new EdgeRef(created.Id, 1));
        var json = GarmentFile.Serialize(ws.Document);
        Assert.Contains("\"holes\"", json);
        var loaded = GarmentFile.Deserialize(json);
        var piece = loaded.FindPiece(target.Id)!;
        var original = ws.Document.FindPiece(target.Id)!;
        Assert.Equal(original.Holes.Select(h => h.Select(p => p.Position)).SelectMany(p => p), piece.Holes.SelectMany(h => h.Select(p => p.Position)));
        Assert.Contains(loaded.Garments[0].Seams, s => s.A.Matches(target.Id, 5));
    }

    [Fact]
    public void PointEditsKeepHoleSeamsOnTheRightEdges()
    {
        var (ws, target, _) = Overlaid();
        var created = ws.CutHole()[0];
        ws.AddSeam(new EdgeRef(target.Id, 6), new EdgeRef(created.Id, 2));
        // A point added to the outline renumbers the hole's edges after it.
        ws.InsertPoint(target.Id, 0, new Vector2(30, 0));
        Assert.Contains(ws.Document.Garments[0].Seams, s => s.A.Matches(target.Id, 7));
        // Points can be added to and dragged on the hole too.
        ws.InsertPoint(target.Id, 5, new Vector2(20, 30));
        var piece = ws.Document.FindPiece(target.Id)!;
        Assert.Equal(5, piece.Holes[0].Count);
        Assert.Contains(ws.Document.Garments[0].Seams, s => s.A.Matches(target.Id, 8));
        // Deleting a hole point when only three would be left removes the whole hole, and its seam.
        ws.DeletePoint(target.Id, 6);
        ws.DeletePoint(target.Id, 6);
        Assert.Single(ws.Document.FindPiece(target.Id)!.Holes);
        ws.DeletePoint(target.Id, 6);
        Assert.Empty(ws.Document.FindPiece(target.Id)!.Holes);
        Assert.Equal("Delete hole", ws.History.UndoName);
        Assert.DoesNotContain(ws.Document.Garments[0].Seams, s => s.A.PieceId == target.Id || s.B.PieceId == target.Id);
    }

    [Fact]
    public void SelectingAllOfAHolesPointsAndDeletingRemovesIt()
    {
        var (ws, target, _) = Overlaid();
        ws.CutHole();
        int first = target.Points.Count;
        ws.DeletePoints(Enumerable.Range(first, 4).Select(i => (target.Id, i)).Append((target.Id, 0)).ToList());
        var piece = ws.Document.FindPiece(target.Id)!;
        Assert.Empty(piece.Holes);
        Assert.Equal(3, piece.Points.Count);
    }

    [Fact]
    public void MirroringKeepsTheHoleAndItsSeams()
    {
        var (ws, target, _) = Overlaid(new Vector2(5, 5));
        var created = ws.CutHole()[0];
        int holeEdge = target.Points.Count + 1;
        ws.AddSeam(new EdgeRef(target.Id, holeEdge), new EdgeRef(created.Id, 0));
        var before = ws.Document.FindPiece(target.Id)!;
        var middle = Outline.SampleEdge(before, holeEdge, 2)[1];
        float axis = 60;
        ws.SelectPiece(target.Id);
        ws.MirrorSelection();
        var piece = ws.Document.FindPiece(target.Id)!;
        var seam = Assert.Single(ws.Document.Garments[0].Seams);
        Assert.True(piece.InHole(seam.A.Edge));
        // The seam is still on the same stretch of the hole, now on the other side of the piece.
        var mirrored = Outline.SampleEdge(piece, seam.A.Edge, 2)[1];
        Assert.Equal(axis - middle.X, mirrored.X, 1e-3f);
        Assert.Equal(middle.Y, mirrored.Y, 1e-3f);
        Assert.True(Outline.SignedArea(Outline.FlattenHoles(piece)[0]) < 0);
        Assert.True(seam.Reversed);
    }

    [Fact]
    public void ClicksInAHoleGoThroughToWhatIsUnderneath()
    {
        var (ws, target, cutter) = Overlaid();
        ws.CutHole();
        var editor = new PatternEditor(ws);
        // The cutter is under the hole; the cut-out was laid out elsewhere.
        var inHole = cutter.Origin + new Vector2(10, 5);
        Assert.Equal(cutter.Id, editor.HitPiece(inHole)?.Id);
        Assert.Equal(target.Id, editor.HitPiece(target.Origin + new Vector2(5, 5))?.Id);
        Assert.Contains("\"holes\":[[", editor.SceneJson(2048));
        // Hidden cutter: the hole shows nothing.
        ws.SetVisible(cutter.Id, false);
        Assert.Null(editor.HitPiece(inHole));
    }

    [Fact]
    public void ADrapedPieceStaysDrapedWhenCut()
    {
        var (ws, target, _) = Overlaid();
        var garment = ws.Document.Garments[0];
        // Drape the target into a gentle bowl.
        var mesh = ws.MeshFor(garment).Find(target.Id)!;
        static Vector3 Bowl(Vector2 p) => new(p.X * 0.01f, 1 + p.Y * 0.01f, 0.3f - 0.0004f * ((p.X - 30) * (p.X - 30) + (p.Y - 25) * (p.Y - 25)) * 0.01f);
        target.Drape = mesh.Points.SelectMany(p => { var v = Bowl(p); return new[] { v.X, v.Y, v.Z }; }).ToArray();
        target.DrapeKey = mesh.Key;

        var created = ws.CutHole()[0];
        garment = ws.Document.Garments[0];
        var after = ws.MeshFor(garment);
        var cut = ws.Document.FindPiece(target.Id)!;
        var cutMesh = after.Find(target.Id)!;
        Assert.Equal(cutMesh.Key, cut.DrapeKey);
        var positions = Mesher.CurrentPositions(cut, cutMesh);
        for (int i = 0; i < cutMesh.VertexCount; i++)
            Assert.True(Vector3.Distance(Bowl(cutMesh.Points[i]), positions[i]) < 0.002f, $"vertex {i} kept its draped position");
        // The cut-out is draped where the hole was, a centimetre behind it.
        var outMesh = after.Find(created.Id)!;
        Assert.Equal(outMesh.Key, created.DrapeKey);
        var outPositions = Mesher.CurrentPositions(created, outMesh);
        var centre = outPositions.Aggregate(Vector3.Zero, (s, p) => s + p) / outPositions.Length;
        Assert.InRange(centre.X, 0.25f, 0.35f);
        Assert.InRange(centre.Y, 1.25f, 1.35f);
        Assert.True(centre.Z < Bowl(new Vector2(30, 30)).Z - 0.005f);
    }
}
