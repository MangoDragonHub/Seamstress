using System.Numerics;
using Seamstress.Core.Model;

namespace Seamstress.Core.Editing;

/// <summary>Ready-made garments sized for the built-in mannequin; useful starting points and test subjects.</summary>
public static class Templates
{
    public static readonly string[] Names = { "T-Shirt", "Tank Top", "Skirt", "Dress" };

    /// <summary>Both the mannequin's and the MakeHuman base mesh's torso sit slightly forward of Z = 0.</summary>
    private const float BodyCentreZ = 0.045f;

    public static Garment Create(string template, Document document)
    {
        var garment = template switch
        {
            "T-Shirt" => Top(document, sleeves: true, length: 62),
            "Tank Top" => Top(document, sleeves: false, length: 62),
            "Dress" => Top(document, sleeves: false, length: 100),
            "Skirt" => Skirt(document),
            _ => throw new ArgumentException($"Unknown template '{template}'."),
        };
        garment.Name = document.UniqueName(template);
        return garment;
    }

    private static string MaterialFor(Document document, string name, string color)
    {
        var material = new FabricMaterial { Name = document.UniqueName(name), Color = color };
        document.Materials.Add(material);
        return material.Id;
    }

    /// <summary>Where the next garment's pieces should go in pattern space so they do not overlap existing ones.</summary>
    private static float NextRowY(Document document)
    {
        float top = 0;
        foreach (var piece in document.AllPieces())
        {
            var (_, max) = Geometry.Outline.Bounds(piece);
            top = MathF.Max(top, max.Y + piece.Origin.Y + 15);
        }
        return top;
    }

    private static Garment Top(Document document, bool sleeves, float length)
    {
        string material = MaterialFor(document, sleeves ? "Shirt fabric" : "Top fabric", sleeves ? "#4F8FD9" : "#D9694F");
        float rowY = NextRowY(document);
        float armpit = length - 25;
        float flare = length > 80 ? 12 : (sleeves ? 0 : 2); // widen towards the hem to clear the hips

        // Sleeveless tops are cut closer to the body; a T-shirt needs the extra width for its sleeves.
        float width = sleeves ? 50 : 46;
        PatternPiece Body(string name, float neckDepth)
        {
            var piece = new PatternPiece { Name = name, MaterialId = material };
            piece.Points.Add(new PatternPoint(-flare, 0));                                              // 0 hem
            piece.Points.Add(new PatternPoint(width + flare, 0));                                       // 1 right side seam
            piece.Points.Add(new PatternPoint(width, armpit, width - 9, armpit + 5));                   // 2 right armhole
            piece.Points.Add(new PatternPoint(width - 8, length - 2));                                  // 3 right shoulder
            piece.Points.Add(new PatternPoint(width / 2 + 11, length, width / 2, length - neckDepth * 2)); // 4 neckline
            piece.Points.Add(new PatternPoint(width / 2 - 11, length));                                 // 5 left shoulder
            piece.Points.Add(new PatternPoint(8, length - 2, 9, armpit + 5));                           // 6 left armhole
            piece.Points.Add(new PatternPoint(0, armpit));                                              // 7 left side seam
            return piece;
        }

        var garment = new Garment();
        var front = Body("Front", 9);
        var back = Body("Back", 2.5f);
        front.Origin = new Vector2(0, rowY);
        back.Origin = new Vector2(75 + 2 * flare, rowY);
        float centreY = 1.44f - length * 0.005f;
        front.Placement = new Placement { Position = new Vector3(0, centreY, BodyCentreZ + 0.22f) };
        back.Placement = new Placement { Position = new Vector3(0, centreY, BodyCentreZ - 0.22f), Rotation = new Vector3(0, 180, 0) };
        garment.Pieces.Add(front);
        garment.Pieces.Add(back);

        // The back piece faces away from the viewer, so its left edges meet the front's right edges.
        garment.Seams.Add(new Seam { A = new EdgeRef(front.Id, 1), B = new EdgeRef(back.Id, 7) });
        garment.Seams.Add(new Seam { A = new EdgeRef(front.Id, 7), B = new EdgeRef(back.Id, 1) });
        garment.Seams.Add(new Seam { A = new EdgeRef(front.Id, 3), B = new EdgeRef(back.Id, 5) });
        garment.Seams.Add(new Seam { A = new EdgeRef(front.Id, 5), B = new EdgeRef(back.Id, 3) });

        if (sleeves)
        {
            var group = new LayerGroup { Name = "Sleeves" };
            garment.Groups.Add(group);
            for (int side = 0; side < 2; side++)
            {
                float sign = side == 0 ? 1 : -1; // +X is the wearer's left arm
                var sleeve = new PatternPiece { Name = side == 0 ? "Sleeve L" : "Sleeve R", MaterialId = material, GroupId = group.Id };
                // The cap (top edge) is split at its crown so each half sews to one armhole.
                sleeve.Points.Add(new PatternPoint(3, 0));                    // 0 cuff
                sleeve.Points.Add(new PatternPoint(37, 0));                   // 1 underarm seam
                sleeve.Points.Add(new PatternPoint(42, 16, 34, 26));          // 2 cap, first half
                sleeve.Points.Add(new PatternPoint(21, 28, 8, 26));           // 3 cap, second half
                sleeve.Points.Add(new PatternPoint(0, 16));                   // 4 underarm seam
                sleeve.Origin = new Vector2(150 + 2 * flare + side * 55, rowY + 10);
                // Laid over the arm with the cap towards the shoulder and wrapped so its long edges meet underneath.
                sleeve.Placement = new Placement
                {
                    Position = new Vector3(sign * 0.34f, 1.31f, BodyCentreZ),
                    Rotation = new Vector3(0, sign * 90, sign * 45),
                    Curvature = 150,
                };
                garment.Pieces.Add(sleeve);
                garment.Seams.Add(new Seam { A = new EdgeRef(sleeve.Id, 1), B = new EdgeRef(sleeve.Id, 4) });
                // On the +X arm the pattern's right half ends up at the back; on the -X arm it ends up in front.
                var (first, second) = side == 0 ? (back, front) : (front, back);
                garment.Seams.Add(new Seam { A = new EdgeRef(sleeve.Id, 2), B = new EdgeRef(first.Id, 6) });
                garment.Seams.Add(new Seam { A = new EdgeRef(sleeve.Id, 3), B = new EdgeRef(second.Id, 2) });
            }
        }
        return garment;
    }

    private static Garment Skirt(Document document)
    {
        string material = MaterialFor(document, "Skirt fabric", "#7A5FC2");
        float rowY = NextRowY(document);
        var garment = new Garment();
        PatternPiece Panel(string name)
        {
            var piece = new PatternPiece { Name = name, MaterialId = material };
            piece.Points.Add(new PatternPoint(0, 0));     // 0 hem
            piece.Points.Add(new PatternPoint(70, 0));    // 1 right side seam
            piece.Points.Add(new PatternPoint(55, 50));   // 2 waist
            piece.Points.Add(new PatternPoint(15, 50));   // 3 left side seam
            return piece;
        }
        var front = Panel("Skirt Front");
        var back = Panel("Skirt Back");
        front.Origin = new Vector2(0, rowY);
        back.Origin = new Vector2(85, rowY);
        // A little closer to the body than the tops start, so a top simulated at the same time ends up over the skirt.
        front.Placement = new Placement { Position = new Vector3(0, 0.75f, BodyCentreZ + 0.19f) };
        back.Placement = new Placement { Position = new Vector3(0, 0.75f, BodyCentreZ - 0.19f), Rotation = new Vector3(0, 180, 0) };
        garment.Pieces.Add(front);
        garment.Pieces.Add(back);
        garment.Seams.Add(new Seam { A = new EdgeRef(front.Id, 1), B = new EdgeRef(back.Id, 3) });
        garment.Seams.Add(new Seam { A = new EdgeRef(front.Id, 3), B = new EdgeRef(back.Id, 1) });
        return garment;
    }
}
