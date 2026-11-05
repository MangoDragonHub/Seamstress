using System.Numerics;
using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;

namespace Seamstress.Tests;

/// <summary>Cut: drawing a line across pieces and splitting them into separate pieces.</summary>
public class CutTests
{
    private static readonly Vector2[] Rectangle = { new(0, 0), new(60, 0), new(60, 50), new(0, 50) };

    private static (Workspace Workspace, PatternPiece Piece) One(params Vector2[] outline)
    {
        var ws = new Workspace();
        var piece = ws.AddPiece(outline.Length > 0 ? outline : Rectangle, "Front")!;
        return (ws, ws.Document.FindPiece(piece.Id)!);
    }

    private static PatternPiece Find(Workspace ws, string id) => ws.Document.FindPiece(id)!;

    private static float Area(PatternPiece piece) => Outline.Area(piece);

    private static void Near(float expected, float actual, float tolerance = 0.05f)
        => Assert.InRange(actual, expected - tolerance, expected + tolerance);

    [Fact]
    public void CutsARectangleIntoTwoPieces()
    {
        var (ws, piece) = One();
        var outcome = ws.CutPieces(new Vector2(30, -20), new Vector2(30, 70));

        Assert.Null(outcome.Problem);
        Assert.Equal(1, outcome.SourcePieces);
        Assert.Equal(2, outcome.Pieces.Count);
        var left = Find(ws, piece.Id);
        var right = outcome.Pieces.Single(p => p.Id != piece.Id);
        Near(1500, Area(left));
        Near(1500, Area(right));
        Near(3000, Area(left) + Area(right));
        Assert.Equal(left.Origin, right.Origin);
        Assert.Equal(left.MaterialId, right.MaterialId);
        Assert.Equal(left.Physics.Preset, right.Physics.Preset);
        Assert.Equal("Front", left.Name);
        Assert.Equal("Front 2", right.Name);
        Assert.Equal(new[] { left.Id, right.Id }.OrderBy(id => id), ws.SelectedPieces.OrderBy(id => id));
        Assert.Equal("Cut piece", ws.History.UndoName);
    }

    [Fact]
    public void CuttingAcrossSeveralPiecesCutsEachOfThem()
    {
        var ws = new Workspace();
        var a = ws.AddPiece(new[] { new Vector2(0, 0), new Vector2(20, 0), new Vector2(20, 40), new Vector2(0, 40) }, "A")!;
        var b = ws.AddPiece(new[] { new Vector2(30, 0), new Vector2(50, 0), new Vector2(50, 40), new Vector2(30, 40) }, "B")!;

        var outcome = ws.CutPieces(new Vector2(-10, 20), new Vector2(70, 20));

        Assert.Null(outcome.Problem);
        Assert.Equal(2, outcome.SourcePieces);
        Assert.Equal(4, outcome.Pieces.Count);
        Near(400 + 400, Area(Find(ws, a.Id)) + Area(outcome.Pieces.Single(p => p.Name == "A 2")));
        Near(400 + 400, Area(Find(ws, b.Id)) + Area(outcome.Pieces.Single(p => p.Name == "B 2")));
        Assert.Equal(4, ws.Document.Garments[0].Pieces.Count);
    }

    [Fact]
    public void CuttingThroughAHoleStillSplitsThePiece()
    {
        var (ws, piece) = One();
        piece.Holes.Add(new List<PatternPoint>
        {
            new(20, 15), new(20, 35), new(40, 35), new(40, 15),
        });

        var outcome = ws.CutPieces(new Vector2(30, -20), new Vector2(30, 70));

        Assert.Null(outcome.Problem);
        Assert.Equal(2, outcome.Pieces.Count);
        Near(1300, Area(Find(ws, piece.Id)));
        Near(1300, Area(outcome.Pieces.Single(p => p.Id != piece.Id)));
        Assert.Empty(Find(ws, piece.Id).Holes);
        Assert.Empty(outcome.Pieces.Single(p => p.Id != piece.Id).Holes);
    }

    [Fact]
    public void AHoleAwayFromTheCutStaysInOnePiece()
    {
        var (ws, piece) = One();
        piece.Holes.Add(new List<PatternPoint>
        {
            new(10, 20), new(10, 30), new(20, 30), new(20, 20),
        });

        var outcome = ws.CutPieces(new Vector2(40, -20), new Vector2(40, 70));

        Assert.Null(outcome.Problem);
        Assert.Equal(2, outcome.Pieces.Count);
        var left = outcome.Pieces.Single(p => p.Id == piece.Id);
        var right = outcome.Pieces.Single(p => p.Id != piece.Id);
        Near(1900, Area(left));
        Near(1000, Area(right));
        Assert.Single(left.Holes);
        Assert.Empty(right.Holes);
    }

    [Fact]
    public void CurvesAreKeptOnThePieces()
    {
        var (ws, piece) = One();
        piece.Points[2].Curve = new Vector2(30, 70);
        float before = Outline.Area(piece);

        var outcome = ws.CutPieces(new Vector2(30, -20), new Vector2(30, 90));

        Assert.Null(outcome.Problem);
        Assert.Equal(2, outcome.Pieces.Count);
        Assert.InRange(outcome.Pieces.Sum(Area), before * 0.99f, before * 1.01f);
        Assert.Contains(outcome.Pieces, p => Enumerable.Range(0, p.EdgeCount).Any(e => Outline.IsCurved(p, e)));
    }

    [Fact]
    public void SeamsOnEdgesTheCutLeavesWholeStay()
    {
        var ws = new Workspace();
        var left = ws.AddPiece(new[] { new Vector2(0, 0), new Vector2(20, 0), new Vector2(20, 40), new Vector2(0, 40) }, "Left")!;
        var right = ws.AddPiece(new[] { new Vector2(20, 0), new Vector2(40, 0), new Vector2(40, 40), new Vector2(20, 40) }, "Right")!;
        ws.AddSeam(new EdgeRef(left.Id, 2), new EdgeRef(right.Id, 2));

        var outcome = ws.CutPieces(new Vector2(-10, 20), new Vector2(25, 20));

        Assert.Null(outcome.Problem);
        var seam = Assert.Single(ws.Document.Garments[0].Seams);
        var leftSide = seam.A.PieceId == right.Id ? seam.B : seam.A;
        Assert.NotEqual(left.Id, leftSide.PieceId);
        Assert.Equal(right.Id, seam.A.PieceId == right.Id ? seam.A.PieceId : seam.B.PieceId);
        Near(20, Outline.EdgeLength(Find(ws, leftSide.PieceId), leftSide.Edge));
        Near(20, Outline.EdgeLength(Find(ws, right.Id), seam.A.PieceId == right.Id ? seam.A.Edge : seam.B.Edge));
    }

    [Fact]
    public void SeamsOnEdgesTheCutDividesAreDropped()
    {
        var ws = new Workspace();
        var left = ws.AddPiece(new[] { new Vector2(0, 0), new Vector2(20, 0), new Vector2(20, 40), new Vector2(0, 40) }, "Left")!;
        var right = ws.AddPiece(new[] { new Vector2(20, 0), new Vector2(40, 0), new Vector2(40, 40), new Vector2(20, 40) }, "Right")!;
        ws.AddSeam(new EdgeRef(left.Id, 3), new EdgeRef(right.Id, 0));

        var outcome = ws.CutPieces(new Vector2(-10, 20), new Vector2(25, 20));

        Assert.Null(outcome.Problem);
        Assert.Empty(ws.Document.Garments[0].Seams);
    }

    [Fact]
    public void ARadialCutThatOnlyOpensThePieceIsRejected()
    {
        var (ws, piece) = One();
        piece.Holes.Add(new List<PatternPoint>
        {
            new(20, 15), new(20, 35), new(40, 35), new(40, 15),
        });

        var outcome = ws.CutPieces(new Vector2(30, 20), new Vector2(30, 70));

        Assert.NotNull(outcome.Problem);
        Assert.Empty(outcome.Pieces);
        Assert.Single(ws.Document.Garments[0].Pieces);
    }

    [Fact]
    public void TheCutToolDrawsTheLineAndCutsOnRelease()
    {
        var (ws, piece) = One();
        var editor = new PatternEditor(ws) { Tool = PatternTool.Cut };
        Workspace.CutOutcome? result = null;
        editor.CutMade += outcome => result = outcome;

        editor.PointerDown(new Vector2(30, -20), 0.5f, false);
        editor.PointerMove(new Vector2(32, 25), 0.5f);
        Assert.Contains("\"cut\":", editor.SceneJson(2048));
        editor.PointerMove(new Vector2(30, 70), 0.5f);
        editor.PointerUp(new Vector2(30, 70));

        Assert.NotNull(result);
        Assert.Equal(2, result.Pieces.Count);
        Assert.DoesNotContain("\"cut\":", editor.SceneJson(2048));
    }

    [Fact]
    public void UndoRestoresTheUncutPiece()
    {
        var (ws, piece) = One();
        ws.CutPieces(new Vector2(30, -20), new Vector2(30, 70));
        Assert.Equal(2, ws.Document.Garments[0].Pieces.Count);

        Assert.True(ws.History.Undo());

        Assert.Single(ws.Document.Garments[0].Pieces);
        Near(3000, Area(Find(ws, piece.Id)));
    }
}
