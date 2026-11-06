using System.Numerics;
using Seamstress.Core.Editing;
using Seamstress.Core.Model;

namespace Seamstress.Tests;

/// <summary>Sewing several edges at once, including the box selection in the Sew tool.</summary>
public class SewSelectionTests
{
    private static (Workspace Workspace, PatternPiece Left, PatternPiece Right) Pair()
    {
        var ws = new Workspace();
        var left = ws.AddPiece(new[] { new Vector2(0, 0), new Vector2(20, 0), new Vector2(20, 40), new Vector2(0, 40) }, "Left")!;
        var right = ws.AddPiece(new[] { new Vector2(20, 0), new Vector2(40, 0), new Vector2(40, 40), new Vector2(20, 40) }, "Right")!;
        return (ws, ws.Document.FindPiece(left.Id)!, ws.Document.FindPiece(right.Id)!);
    }

    [Fact]
    public void BoxSelectionSewsTheNearestPair()
    {
        var (ws, left, right) = Pair();
        var editor = new PatternEditor(ws) { Tool = PatternTool.Sew };
        int made = -1;
        editor.SewMade += count => made = count;

        editor.PointerDown(new Vector2(15, 5), 0.5f, false);
        editor.PointerMove(new Vector2(25, 35), 0.5f);
        Assert.Contains("\"marquee\":", editor.SceneJson(2048));
        editor.PointerUp(new Vector2(25, 35));

        Assert.Equal(2, editor.SewSelection.Count);
        Assert.Contains("\"sewSelection\":", editor.SceneJson(2048));

        editor.Confirm();

        var seam = Assert.Single(ws.Document.Garments[0].Seams);
        Assert.Equal(1, made);
        if (seam.A.PieceId == left.Id)
        {
            Assert.Equal(1, seam.A.Edge);
            Assert.Equal(right.Id, seam.B.PieceId);
            Assert.Equal(3, seam.B.Edge);
        }
        else
        {
            Assert.Equal(right.Id, seam.A.PieceId);
            Assert.Equal(3, seam.A.Edge);
            Assert.Equal(left.Id, seam.B.PieceId);
            Assert.Equal(1, seam.B.Edge);
        }
        Assert.Empty(editor.SewSelection);
    }

    [Fact]
    public void SeveralPairsAreOneUndoStep()
    {
        var (ws, left, right) = Pair();

        var made = ws.AddSeams(new[]
        {
            (new EdgeRef(left.Id, 1), new EdgeRef(right.Id, 3), false),
            (new EdgeRef(left.Id, 2), new EdgeRef(right.Id, 2), false),
        });

        Assert.Equal(2, made.Count);
        Assert.Equal(2, ws.Document.Garments[0].Seams.Count);
        Assert.Equal("Sew edges", ws.History.UndoName);
        Assert.True(ws.History.Undo());
        Assert.Empty(ws.Document.Garments[0].Seams);
    }

    [Fact]
    public void ABadPairIsSkippedAndDuplicatesAreNotAdded()
    {
        var (ws, left, right) = Pair();

        var made = ws.AddSeams(new[]
        {
            (new EdgeRef(left.Id, 1), new EdgeRef(right.Id, 3), false),
            (new EdgeRef(left.Id, 1), new EdgeRef(right.Id, 3), true),
            (new EdgeRef(left.Id, 9), new EdgeRef(right.Id, 3), false),
        });

        Assert.Single(made);
        Assert.Single(ws.Document.Garments[0].Seams);
    }
}
