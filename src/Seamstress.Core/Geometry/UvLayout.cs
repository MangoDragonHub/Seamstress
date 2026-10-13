using System.Numerics;
using Seamstress.Core.Model;

namespace Seamstress.Core.Geometry;

/// <summary>
/// Maps pattern space to texture space. By default the 2D pattern layout of a garment is scaled uniformly into the
/// unit square, so every piece gets the same texel density; each piece can then be adjusted individually.
/// </summary>
public sealed class UvLayout
{
    private const float Margin = 0.02f;

    /// <summary>UV units per centimetre before per-piece adjustment.</summary>
    public float UnitsPerCm { get; private init; } = 0.01f;

    /// <summary>Pattern-space position (cm) that maps to UV (Margin, Margin).</summary>
    public Vector2 Min { get; private init; }

    public static UvLayout For(Garment garment)
    {
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        bool any = false;
        foreach (var piece in garment.Pieces)
        {
            if (piece.Points.Count < 3)
                continue;
            var (lo, hi) = Outline.Bounds(piece);
            min = Vector2.Min(min, lo + piece.Origin);
            max = Vector2.Max(max, hi + piece.Origin);
            any = true;
        }
        if (!any)
            return new UvLayout();
        float extent = MathF.Max(MathF.Max(max.X - min.X, max.Y - min.Y), 1e-3f);
        return new UvLayout { UnitsPerCm = (1f - 2 * Margin) / extent, Min = min };
    }

    /// <summary>UV of the piece's bounding-box centre; per-piece scale and rotation pivot here.</summary>
    public Vector2 PieceCentre(PatternPiece piece)
    {
        var (lo, hi) = Outline.Bounds(piece);
        return BaseMap((lo + hi) / 2 + piece.Origin);
    }

    private Vector2 BaseMap(Vector2 world) => (world - Min) * UnitsPerCm + new Vector2(Margin);

    /// <summary>UV (V up) of a point given in the piece's local pattern coordinates.</summary>
    public Vector2 Map(PatternPiece piece, Vector2 local) => Map(piece, local, PieceCentre(piece));

    public Vector2 Map(PatternPiece piece, Vector2 local, Vector2 pieceCentre)
    {
        var uv = BaseMap(local + piece.Origin);
        var adjust = piece.Uv;
        var d = (uv - pieceCentre) * adjust.Scale;
        if (adjust.Rotation != 0)
        {
            float r = adjust.Rotation * MathF.PI / 180f;
            float c = MathF.Cos(r), s = MathF.Sin(r);
            d = new Vector2(d.X * c - d.Y * s, d.X * s + d.Y * c);
        }
        return pieceCentre + d + adjust.Offset;
    }

    public Vector2[] Map(PatternPiece piece, PieceMesh mesh)
    {
        var centre = PieceCentre(piece);
        var result = new Vector2[mesh.Points.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = Map(piece, mesh.Points[i], centre);
        return result;
    }

    /// <summary>Texture pixels per centimetre of fabric for a piece.</summary>
    public float TexelDensity(PatternPiece piece, FabricMaterial? material, int textureResolution)
        => UnitsPerCm * piece.Uv.Scale * textureResolution * MathF.Max(material?.Repeat ?? 1f, 1e-3f);
}
