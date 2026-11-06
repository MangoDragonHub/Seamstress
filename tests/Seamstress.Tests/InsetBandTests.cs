using System.Numerics;
using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;

namespace Seamstress.Tests;

/// <summary>Inset Band: cutting a waistband, collar band or facing off a piece along some of its edges.</summary>
public class InsetBandTests
{
    // A 60 × 50 rectangle from the origin: edge 0 is the hem, 1 the right side (going up), 2 the top (going left),
    // 3 the left side (going down).
    private static readonly Vector2[] Rectangle = { new(0, 0), new(60, 0), new(60, 50), new(0, 50) };

    private static (Workspace Workspace, PatternPiece Piece) One(params Vector2[] outline)
    {
        var ws = new Workspace();
        var piece = ws.AddPiece(outline.Length > 0 ? outline : Rectangle, "Front")!;
        return (ws, ws.Document.FindPiece(piece.Id)!);
    }

    private static PatternPiece Find(Workspace ws, string id) => ws.Document.FindPiece(id)!;

    private static float Area(PatternPiece piece) => Outline.Area(piece);

    private static void Near(float expected, float actual, float tolerance) => Assert.InRange(actual, expected - tolerance, expected + tolerance);

    [Fact]
    public void CutsABandOffAStraightEdge()
    {
        var (ws, piece) = One();
        var bands = ws.InsetBand(new[] { new EdgeRef(piece.Id, 2) }, 4);

        var band = Assert.Single(bands);
        var body = Find(ws, piece.Id);
        Assert.Equal("Front band", band.Name);
        Near(60 * 46, Area(body), 0.05f);
        Near(60 * 4, Area(band), 0.05f);
        Assert.True(Outline.SignedArea(Outline.Flatten(body)) > 0);
        Assert.True(Outline.SignedArea(Outline.Flatten(band)) > 0);
        var (min, max) = Outline.Bounds(band);
        Assert.Equal(new Vector2(0, 46), min);
        Assert.Equal(new Vector2(60, 50), max);
        Assert.Equal(band.Origin, body.Origin);
        Assert.Equal(body.MaterialId, band.MaterialId);
        Assert.Equal(piece.Physics.Preset, band.Physics.Preset);

        // The band is sewn to the piece along the cut, edge to edge and the same length.
        var seam = Assert.Single(ws.Document.Garments[0].Seams);
        var (onBody, onBand) = seam.A.PieceId == body.Id ? (seam.A, seam.B) : (seam.B, seam.A);
        Assert.Equal(band.Id, onBand.PieceId);
        Assert.False(seam.Reversed);
        Near(60, Outline.EdgeLength(body, onBody.Edge), 0.005f);
        Near(60, Outline.EdgeLength(band, onBand.Edge), 0.005f);
        Assert.Equal(Outline.EdgeStart(body, onBody.Edge), Outline.EdgeEnd(band, onBand.Edge));

        Assert.Equal("Inset band", ws.History.UndoName);
        Assert.Equal(new[] { band.Id }, ws.SelectedPieces);
    }

    [Fact]
    public void TheEndsOfTheCutFollowSlantedSides()
    {
        // A trapezoid, wider at the top: the band's ends lie along the slanted sides.
        var (ws, piece) = One(new(10, 0), new(50, 0), new(60, 40), new(0, 40));
        var band = Assert.Single(ws.InsetBand(new[] { new EdgeRef(piece.Id, 2) }, 5));
        var body = Find(ws, piece.Id);

        var corners = Outline.Flatten(band);
        Assert.Contains(corners, p => Vector2.Distance(p, new Vector2(60 - 1.25f, 35)) < 1e-3f);
        Assert.Contains(corners, p => Vector2.Distance(p, new Vector2(1.25f, 35)) < 1e-3f);
        Near(2000, Area(body) + Area(band), 0.05f);
        Assert.Equal(4, body.Points.Count);
    }

    [Fact]
    public void ACurvedEdgeKeepsItsShapeAndTheCutFollowsIt()
    {
        // The top edge bulges upwards through (30, 60).
        var (ws, piece) = One();
        piece.Points[2].Curve = new Vector2(30, 70);
        float topLength = Outline.EdgeLength(piece, 2);
        var original = Outline.FlattenEdge(piece, 2);

        var band = Assert.Single(ws.InsetBand(new[] { new EdgeRef(piece.Id, 2) }, 3));
        var body = Find(ws, piece.Id);

        // The band's outer edge is the same curve.
        int outer = Enumerable.Range(0, band.EdgeCount).Single(e => Outline.IsCurved(band, e) && Outline.EdgeStart(band, e) == new Vector2(60, 50));
        Near(topLength, Outline.EdgeLength(band, outer), 0.0005f);

        // Every point of the cut line is 3 cm from the curve, to within a millimetre, and the cut on a bulge is shorter.
        var garment = ws.Document.Garments[0];
        var cut = garment.Seams.Where(s => s.A.PieceId == body.Id && s.B.PieceId == band.Id).ToList();
        Assert.NotEmpty(cut);
        float cutLength = 0;
        foreach (var seam in cut)
        {
            cutLength += Outline.EdgeLength(body, seam.A.Edge);
            Near(Outline.EdgeLength(body, seam.A.Edge), Outline.EdgeLength(band, seam.B.Edge), 0.0005f);
            // Away from the corners, where the cut runs on to meet the sides.
            foreach (var p in Outline.FlattenEdge(body, seam.A.Edge).Where(p => p.X > 5 && p.X < 55))
                Assert.InRange(Outline.DistanceToPolyline(p, original, false), 2.9f, 3.1f);
        }
        // The cut runs from side to side.
        var ends = cut.SelectMany(c => new[] { Outline.EdgeStart(body, c.A.Edge), Outline.EdgeEnd(body, c.A.Edge) }).ToList();
        Assert.Contains(ends, p => MathF.Abs(p.X - 60) < 1e-3f && p.Y < 50);
        Assert.Contains(ends, p => MathF.Abs(p.X) < 1e-3f && p.Y < 50);
        Assert.InRange(cutLength, 55, topLength + 5);
    }

    [Fact]
    public void AScoopedNecklineGivesALongerCut()
    {
        // A bodice top: shoulders either side of a neckline that dips down into the piece.
        var (ws, piece) = One(new(0, 0), new(60, 0), new(60, 50), new(45, 50), new(15, 50), new(0, 50));
        piece.Points[3].Curve = new Vector2(30, 20);
        float neckLength = Outline.EdgeLength(piece, 3);
        float area = Area(piece);
        var band = Assert.Single(ws.InsetBand(new[] { new EdgeRef(piece.Id, 3) }, 3));
        var body = Find(ws, piece.Id);
        float cutLength = ws.Document.Garments[0].Seams.Sum(s => Outline.EdgeLength(Find(ws, s.A.PieceId), s.A.Edge));
        Assert.True(cutLength > neckLength);
        Near(area, Area(body) + Area(band), 0.5f);
        // The cut ends on the shoulders, which are shortened.
        Assert.True(Outline.EdgeLength(body, 2) < 15);
    }

    [Fact]
    public void ACurveTooTightForTheWidthIsRefused()
    {
        // A sharp bump: the curve bends far tighter than 8 cm.
        var (ws, piece) = One();
        piece.Points[2].Curve = new Vector2(30, 90);
        var before = ws.History.CanUndo;
        ws.PlanInsetBand(new[] { new EdgeRef(piece.Id, 2) }, 25, out var problem);
        Assert.NotNull(problem);
        Assert.Empty(ws.InsetBand(new[] { new EdgeRef(piece.Id, 2) }, 25));
        Assert.Single(ws.Document.Garments[0].Pieces);
        Assert.Equal(before, ws.History.CanUndo);
    }

    [Fact]
    public void ACurvedSideKeepsItsCurveWhereTheCutMeetsIt()
    {
        var (ws, piece) = One();
        piece.Points[1].Curve = new Vector2(70, 25);   // the right side bows outwards
        float sideLength = Outline.EdgeLength(piece, 1);
        var band = Assert.Single(ws.InsetBand(new[] { new EdgeRef(piece.Id, 2) }, 5));
        var body = Find(ws, piece.Id);
        float bodyPart = Outline.EdgeLength(body, 1);
        int bandPart = Enumerable.Range(0, band.EdgeCount).Single(e => Outline.EdgeEnd(band, e) == new Vector2(60, 50));
        Assert.True(Outline.IsCurved(body, 1));
        Assert.True(Outline.IsCurved(band, bandPart));
        Near(sideLength, bodyPart + Outline.EdgeLength(band, bandPart), 0.05f);
    }

    [Fact]
    public void ConsecutiveEdgesMakeOneBandAndSeparateOnesTwo()
    {
        var (ws, piece) = One();
        var corner = Assert.Single(ws.InsetBand(new[] { new EdgeRef(piece.Id, 1), new EdgeRef(piece.Id, 2) }, 4));
        Near(60 * 50 - 56 * 46, Area(corner), 0.05f);
        // The cut turns the corner with a mitre at (56, 46).
        Assert.Contains(Outline.Flatten(corner), p => Vector2.Distance(p, new Vector2(56, 46)) < 1e-3f);

        var (ws2, piece2) = One();
        var two = ws2.InsetBand(new[] { new EdgeRef(piece2.Id, 0), new EdgeRef(piece2.Id, 2) }, 4);
        Assert.Equal(2, two.Count);
        Near(60 * 42, Area(Find(ws2, piece2.Id)), 0.05f);
        Assert.Equal(new[] { "Front band", "Front band 2" }, two.Select(b => b.Name));
    }

    [Fact]
    public void SeamsFollowTheirEdges()
    {
        var (ws, piece) = One();
        var hem = ws.AddPiece(new[] { new Vector2(100, 0), new Vector2(160, 0), new Vector2(160, 10), new Vector2(100, 10) }, "Hem")!;
        var top = ws.AddPiece(new[] { new Vector2(100, 40), new Vector2(160, 40), new Vector2(160, 50), new Vector2(100, 50) }, "Top")!;
        var onHem = ws.AddSeam(new EdgeRef(piece.Id, 0), new EdgeRef(hem.Id, 2))!;
        var onTop = ws.AddSeam(new EdgeRef(piece.Id, 2), new EdgeRef(top.Id, 0))!;

        var band = Assert.Single(ws.InsetBand(new[] { new EdgeRef(piece.Id, 2) }, 4));
        var body = Find(ws, piece.Id);
        var seams = ws.Document.Garments[0].Seams;

        // The seam on the cut edge moved to the band, still sewn the same way round, on the same 60 cm edge.
        var moved = seams.Single(s => s.Id == onTop.Id);
        Assert.Equal(band.Id, moved.A.PieceId);
        Assert.Equal(new Vector2(60, 50), Outline.EdgeStart(band, moved.A.Edge));
        Assert.Equal(new Vector2(0, 50), Outline.EdgeEnd(band, moved.A.Edge));
        // The seam on the hem stays on the hem, wherever it is numbered now.
        var kept = seams.Single(s => s.Id == onHem.Id);
        Assert.Equal(body.Id, kept.A.PieceId);
        Assert.Equal(new Vector2(0, 0), Outline.EdgeStart(body, kept.A.Edge));
        Assert.Equal(new Vector2(60, 0), Outline.EdgeEnd(body, kept.A.Edge));
    }

    [Fact]
    public void SideSeamsAreSplitWhenFrontAndBackAreCutTogether()
    {
        // Front and back of a skirt sewn at both sides; both waists are cut in one go.
        var ws = new Workspace();
        var front = ws.AddPiece(Rectangle, "Front")!;
        var back = ws.AddPiece(Rectangle.Select(p => p + new Vector2(80, 0)).ToArray(), "Back")!;
        ws.AddSeam(new EdgeRef(front.Id, 1), new EdgeRef(back.Id, 3));
        ws.AddSeam(new EdgeRef(front.Id, 3), new EdgeRef(back.Id, 1));

        var bands = ws.InsetBand(new[] { new EdgeRef(front.Id, 2), new EdgeRef(back.Id, 2) }, 5);
        Assert.Equal(2, bands.Count);
        var seams = ws.Document.Garments[0].Seams;
        string F = front.Id, B = back.Id, FB = bands[0].Id, BB = bands[1].Id;

        // Two cut seams, two body-to-body side seams and two band-to-band side seams, each pair the same length.
        Assert.Equal(6, seams.Count);
        bool Joins(Seam s, string a, string b) => (s.A.PieceId == a && s.B.PieceId == b) || (s.A.PieceId == b && s.B.PieceId == a);
        Assert.Single(seams, s => Joins(s, F, FB));
        Assert.Single(seams, s => Joins(s, B, BB));
        Assert.Equal(2, seams.Count(s => Joins(s, F, B)));
        Assert.Equal(2, seams.Count(s => Joins(s, FB, BB)));
        foreach (var seam in seams)
            Near(Outline.EdgeLength(Find(ws, seam.A.PieceId), seam.A.Edge), Outline.EdgeLength(Find(ws, seam.B.PieceId), seam.B.Edge), 0.005f);
        foreach (var seam in seams.Where(s => Joins(s, FB, BB)))
            Near(5, Outline.EdgeLength(Find(ws, seam.A.PieceId), seam.A.Edge), 0.005f);
        Assert.Equal(4, Find(ws, B).Points.Count);
    }

    [Fact]
    public void ASideSewnToAnUncutPieceGetsAPointThere()
    {
        var ws = new Workspace();
        var front = ws.AddPiece(Rectangle, "Front")!;
        var back = ws.AddPiece(Rectangle.Select(p => p + new Vector2(80, 0)).ToArray(), "Back")!;
        var side = ws.AddSeam(new EdgeRef(front.Id, 1), new EdgeRef(back.Id, 3))!;

        var band = Assert.Single(ws.InsetBand(new[] { new EdgeRef(front.Id, 2) }, 5));
        var backNow = Find(ws, back.Id);
        Assert.Equal(5, backNow.Points.Count);
        Assert.Contains(backNow.Points, p => p.Position == new Vector2(0, 45));
        var seams = ws.Document.Garments[0].Seams;
        Assert.Equal(3, seams.Count);
        // The band's 5 cm of side is sewn to the top 5 cm of the back's side, start to end as before.
        var upper = seams.Single(s => s.A.PieceId == band.Id);
        Assert.Equal(back.Id, upper.B.PieceId);
        Assert.Equal(new Vector2(60, 50), Outline.EdgeEnd(band, upper.A.Edge));
        Assert.Equal(new Vector2(0, 50), Outline.EdgeStart(backNow, upper.B.Edge));
        Assert.False(upper.Reversed);
        Assert.Contains(seams, s => s.Id == side.Id && s.A.PieceId == front.Id);
    }

    [Fact]
    public void AWholeHoleGivesARingAroundIt()
    {
        var (ws, piece) = One();
        piece.Holes.Add(new List<PatternPoint> { new(20, 20), new(20, 30), new(40, 30), new(40, 20) });
        var edges = Enumerable.Range(4, 4).Select(e => new EdgeRef(piece.Id, e)).ToList();
        var ring = Assert.Single(ws.InsetBand(edges, 2));
        var body = Find(ws, piece.Id);

        Assert.Single(ring.Holes);
        Near(24 * 14 - 20 * 10, Area(ring), 0.05f);
        Near(60 * 50 - 24 * 14, Area(body), 0.05f);
        Assert.Equal(4, ws.Document.Garments[0].Seams.Count);
    }

    [Fact]
    public void AWholeOutlineGivesARingAndThePieceKeepsTheMiddle()
    {
        var (ws, piece) = One();
        var ring = Assert.Single(ws.InsetBand(Enumerable.Range(0, 4).Select(e => new EdgeRef(piece.Id, e)).ToList(), 5));
        Near(50 * 40, Area(Find(ws, piece.Id)), 0.05f);
        Near(60 * 50 - 50 * 40, Area(ring), 0.05f);
    }

    [Fact]
    public void AWidthThatDoesNotFitChangesNothing()
    {
        var (ws, piece) = One();
        piece.Holes.Add(new List<PatternPoint> { new(20, 40), new(20, 45), new(40, 45), new(40, 40) });
        var edges = new[] { new EdgeRef(piece.Id, 2) };

        ws.PlanInsetBand(edges, 8, out var crossesHole);
        ws.PlanInsetBand(edges, 60, out var tooWide);
        ws.PlanInsetBand(edges, 0, out var zero);
        ws.PlanInsetBand(edges, 3, out var fine);
        Assert.NotNull(crossesHole);
        Assert.NotNull(tooWide);
        Assert.NotNull(zero);
        Assert.Null(fine);
        Assert.Empty(ws.InsetBand(edges, 8));
        Assert.Single(ws.Document.Garments[0].Pieces);
        Assert.Equal(4, Find(ws, piece.Id).Points.Count);
    }

    [Theory]
    [InlineData("T-Shirt", "Front", "Back")]
    [InlineData("Skirt", "Skirt Front", "Skirt Back")]
    public void TheTemplatesNecklinesAndWaistsCanBeCut(string template, string frontName, string backName)
    {
        // The highest long edge of each body piece: the curved neckline between the shoulders, or a waist whose
        // sides lean in.
        var ws = new Workspace();
        var garment = ws.AddTemplate(template);
        int Top(PatternPiece piece)
        {
            var (min, max) = Outline.Bounds(piece);
            return Enumerable.Range(0, piece.Points.Count)
                .Where(e => Outline.EdgeLength(piece, e) >= (max.X - min.X) / 3)
                .MaxBy(e => Outline.SampleEdge(piece, e, 2)[1].Y);
        }
        var front = garment.Pieces.Single(p => p.Name == frontName);
        var back = garment.Pieces.Single(p => p.Name == backName);
        var edges = new[] { new EdgeRef(front.Id, Top(front)), new EdgeRef(back.Id, Top(back)) };
        float area = Area(front) + Area(back);
        int pieces = garment.Pieces.Count, seams = garment.Seams.Count;

        ws.PlanInsetBand(edges, 3, out var problem);
        Assert.Null(problem);
        var bands = ws.InsetBand(edges, 3);
        garment = ws.Document.FindGarment(garment.Id)!;

        Assert.Equal(2, bands.Count);
        Assert.Equal(pieces + 2, garment.Pieces.Count);
        Near(area, Area(Find(ws, front.Id)) + Area(Find(ws, back.Id)) + bands.Sum(Area), 0.01f * area);
        // At least one cut seam per band, and every seam pairs edges of nearly the same length.
        Assert.True(garment.Seams.Count >= seams + 2);
        foreach (var seam in garment.Seams)
        {
            float a = Outline.EdgeLength(Find(ws, seam.A.PieceId), seam.A.Edge), b = Outline.EdgeLength(Find(ws, seam.B.PieceId), seam.B.Edge);
            if (seam.A.PieceId == front.Id && seam.B.PieceId == bands[0].Id)
                Near(a, b, 0.01f);
        }
        // The template's meshes still build, with the bands stitched on.
        var mesh = Mesher.Build(garment);
        Assert.Contains(mesh.Stitches, s => mesh.Pieces[s.PieceA].PieceId == bands[0].Id || mesh.Pieces[s.PieceB].PieceId == bands[0].Id);
    }

    [Fact]
    public void UndoAndRedoRestoreEverything()
    {
        var (ws, piece) = One();
        var band = Assert.Single(ws.InsetBand(new[] { new EdgeRef(piece.Id, 2) }, 4));
        Assert.True(ws.History.Undo());
        Assert.Single(ws.Document.Garments[0].Pieces);
        Assert.Empty(ws.Document.Garments[0].Seams);
        Near(60 * 50, Area(Find(ws, piece.Id)), 0.05f);
        Assert.True(ws.History.Redo());
        Assert.Equal(2, ws.Document.Garments[0].Pieces.Count);
        Assert.NotNull(ws.Document.FindPiece(band.Id));
        Assert.Single(ws.Document.Garments[0].Seams);
    }

    [Fact]
    public void ADrapedPieceStaysDrapedAndTheBandStartsDraped()
    {
        var (ws, piece) = One();
        var garment = ws.Document.Garments[0];
        var mesh = ws.MeshFor(garment).Find(piece.Id)!;
        // A drape that is simply the flat arrangement pushed 10 cm forward.
        var positions = Mesher.Arrange(piece, mesh);
        piece.Drape = positions.SelectMany(p => new[] { p.X, p.Y, p.Z + 0.1f }).ToArray();
        piece.DrapeKey = mesh.Key;

        var band = Assert.Single(ws.InsetBand(new[] { new EdgeRef(piece.Id, 2) }, 4));
        var after = ws.MeshFor(ws.Document.Garments[0]);
        var body = Find(ws, piece.Id);
        Assert.Equal(after.Find(body.Id)!.Key, body.DrapeKey);
        Assert.Equal(after.Find(band.Id)!.Key, band.DrapeKey);
        float z = piece.Placement.Position.Z + 0.1f;
        Assert.All(Enumerable.Range(0, band.Drape!.Length / 3), i => Near(z, band.Drape[3 * i + 2], 0.001f));
    }

    [Fact]
    public void ThePieceStaysWhereItWasIn3D()
    {
        var (ws, piece) = One();
        // Wrapped and turned, then cut down the right side, so the piece gets narrower and its middle moves.
        piece.Placement.Curvature = 90;
        piece.Placement.Rotation = new Vector3(10, 30, 0);
        Vector3 Before(Vector2 at) => Mesher.ArrangedFrame(piece, at).Position;
        var (corner, cutTop, top) = (Before(new(0, 0)), Before(new(50, 50)), Before(new(60, 50)));

        var band = Assert.Single(ws.InsetBand(new[] { new EdgeRef(piece.Id, 1) }, 10));
        var body = Find(ws, piece.Id);
        Assert.True(Vector3.Distance(corner, Mesher.ArrangedFrame(body, new Vector2(0, 0)).Position) < 1e-3f);
        Assert.True(Vector3.Distance(cutTop, Mesher.ArrangedFrame(body, new Vector2(50, 50)).Position) < 1e-3f);
        Assert.True(Vector3.Distance(cutTop, Mesher.ArrangedFrame(band, new Vector2(50, 50)).Position) < 1e-3f);
        Assert.True(Vector3.Distance(top, Mesher.ArrangedFrame(band, new Vector2(60, 50)).Position) < 1e-3f);
    }
}
