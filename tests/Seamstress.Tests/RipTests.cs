using System.Numerics;
using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;

namespace Seamstress.Tests;

/// <summary>Rip: tearing a piece by dragging across its simulated surface in 3D.</summary>
public class RipTests
{
    private static readonly Vector2[] Rectangle = { new(0, 0), new(60, 0), new(60, 50), new(0, 50) };

    private static (Workspace Workspace, PatternPiece Piece) One()
    {
        var ws = new Workspace();
        var piece = ws.AddPiece(Rectangle, "Front")!;
        return (ws, ws.Document.FindPiece(piece.Id)!);
    }

    private static float Area(PatternPiece piece) => Outline.Area(piece);

    private static void Near(float expected, float actual, float tolerance = 0.05f)
        => Assert.InRange(actual, expected - tolerance, expected + tolerance);

    /// <summary>The point on the arranged surface at a pattern position, for building a 3D stroke.</summary>
    private static Vector3 At(Workspace ws, PatternPiece piece, Vector2 local)
    {
        var mesh = ws.MeshFor(ws.Document.GarmentOfPiece(piece.Id)!).Find(piece.Id)!;
        var positions = Mesher.CurrentPositions(piece, mesh);
        for (int t = 0; t < mesh.Triangles.Length; t += 3)
        {
            int ia = mesh.Triangles[t], ib = mesh.Triangles[t + 1], ic = mesh.Triangles[t + 2];
            var a = mesh.Points[ia];
            var b = mesh.Points[ib];
            var c = mesh.Points[ic];
            float area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            if (MathF.Abs(area) < 1e-9f)
                continue;
            float wb = ((local.X - a.X) * (c.Y - a.Y) - (local.Y - a.Y) * (c.X - a.X)) / area;
            float wc = ((b.X - a.X) * (local.Y - a.Y) - (b.Y - a.Y) * (local.X - a.X)) / area;
            float wa = 1 - wb - wc;
            if (wa < -1e-4f || wb < -1e-4f || wc < -1e-4f)
                continue;
            return positions[ia] * wa + positions[ib] * wb + positions[ic] * wc;
        }
        throw new InvalidOperationException("The point is not on the piece's mesh.");
    }

    [Fact]
    public void RipsAPieceAlongAStroke()
    {
        var (ws, piece) = One();
        var stroke = new[]
        {
            At(ws, piece, new Vector2(30, 12)),
            At(ws, piece, new Vector2(31, 25)),
            At(ws, piece, new Vector2(30, 38)),
        };

        var outcome = ws.RipPiece(piece.Id, stroke);

        Assert.Null(outcome.Problem);
        Assert.Equal(1, outcome.SourcePieces);
        Assert.Equal(2, outcome.Pieces.Count);
        var first = ws.Document.FindPiece(piece.Id)!;
        var second = outcome.Pieces.Single(p => p.Id != piece.Id);
        Near(3000, Area(first) + Area(second));
        Assert.InRange(MathF.Abs(Area(first) - Area(second)), 0, 10);
        Assert.Equal("Rip piece", ws.History.UndoName);
        Assert.Contains(piece.Id, ws.SelectedPieces);
        Assert.Contains(second.Id, ws.SelectedPieces);
    }

    [Fact]
    public void AShortStrokeDoesNothing()
    {
        var (ws, piece) = One();
        var stroke = new[]
        {
            At(ws, piece, new Vector2(30, 25)),
            At(ws, piece, new Vector2(30.1f, 25.1f)),
        };

        var outcome = ws.RipPiece(piece.Id, stroke);

        Assert.NotNull(outcome.Problem);
        Assert.Empty(outcome.Pieces);
        Assert.Single(ws.Document.Garments[0].Pieces);
    }

    [Fact]
    public void UndoRestoresTheOriginalPiece()
    {
        var (ws, piece) = One();
        var stroke = new[]
        {
            At(ws, piece, new Vector2(30, 10)),
            At(ws, piece, new Vector2(30, 40)),
        };
        ws.RipPiece(piece.Id, stroke);
        Assert.Equal(2, ws.Document.Garments[0].Pieces.Count);

        Assert.True(ws.History.Undo());

        Assert.Single(ws.Document.Garments[0].Pieces);
        Near(3000, Area(ws.Document.FindPiece(piece.Id)!));
    }
}
