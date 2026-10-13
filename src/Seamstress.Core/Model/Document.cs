using System.Numerics;

namespace Seamstress.Core.Model;

/// <summary>
/// Root of a Seamstress project. Pattern space is centimetres with Y up; 3D space is metres with Y up.
/// </summary>
public sealed class Document
{
    public string Name { get; set; } = "Untitled";
    public List<Garment> Garments { get; set; } = new();
    public List<FabricMaterial> Materials { get; set; } = new();
    public SimulationSettings Simulation { get; set; } = new();
    public AvatarSettings Avatar { get; set; } = new();

    public Document Clone() => new()
    {
        Name = Name,
        Garments = Garments.Select(g => g.Clone()).ToList(),
        Materials = Materials.Select(m => m.Clone()).ToList(),
        Simulation = Simulation.Clone(),
        Avatar = Avatar.Clone(),
    };

    public Garment? FindGarment(string? id) => Garments.FirstOrDefault(g => g.Id == id);

    public PatternPiece? FindPiece(string? id)
    {
        foreach (var g in Garments)
            foreach (var p in g.Pieces)
                if (p.Id == id)
                    return p;
        return null;
    }

    public Garment? GarmentOfPiece(string? pieceId)
        => Garments.FirstOrDefault(g => g.Pieces.Any(p => p.Id == pieceId));

    public FabricMaterial? FindMaterial(string? id) => Materials.FirstOrDefault(m => m.Id == id);

    public IEnumerable<PatternPiece> AllPieces() => Garments.SelectMany(g => g.Pieces);

    /// <summary>Returns a name not yet used by any garment, group or piece.</summary>
    public string UniqueName(string baseName)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in Garments)
        {
            used.Add(g.Name);
            foreach (var gr in g.Groups) used.Add(gr.Name);
            foreach (var p in g.Pieces) used.Add(p.Name);
        }
        if (!used.Contains(baseName))
            return baseName;
        for (int i = 2; ; i++)
        {
            var candidate = $"{baseName} {i}";
            if (!used.Contains(candidate))
                return candidate;
        }
    }

    public static string NewId() => Guid.NewGuid().ToString("N")[..10];
}

/// <summary>A garment is an independent set of pattern pieces and seams that is simulated as one unit.</summary>
public sealed class Garment
{
    public string Id { get; set; } = Document.NewId();
    public string Name { get; set; } = "Garment";
    public bool Visible { get; set; } = true;
    public bool Expanded { get; set; } = true;

    /// <summary>When set, this garment is also simulated while another garment is the active one.</summary>
    public bool SimulateWithOthers { get; set; }

    /// <summary>Target edge length of the simulation mesh in centimetres. Smaller means more faces.</summary>
    public float ParticleDistance { get; set; } = 2.0f;

    public List<LayerGroup> Groups { get; set; } = new();
    public List<PatternPiece> Pieces { get; set; } = new();
    public List<Seam> Seams { get; set; } = new();

    public Garment Clone() => new()
    {
        Id = Id,
        Name = Name,
        Visible = Visible,
        Expanded = Expanded,
        SimulateWithOthers = SimulateWithOthers,
        ParticleDistance = ParticleDistance,
        Groups = Groups.Select(g => g.Clone()).ToList(),
        Pieces = Pieces.Select(p => p.Clone()).ToList(),
        Seams = Seams.Select(s => s.Clone()).ToList(),
    };

    public PatternPiece? FindPiece(string? id) => Pieces.FirstOrDefault(p => p.Id == id);
    public LayerGroup? FindGroup(string? id) => Groups.FirstOrDefault(g => g.Id == id);

    public float EffectiveParticleDistance(PatternPiece piece)
        => Math.Clamp(piece.ParticleDistance ?? ParticleDistance, 0.3f, 20f);

    /// <summary>True when <paramref name="groupId"/> is <paramref name="ancestorId"/> or nested inside it.</summary>
    public bool IsGroupWithin(string? groupId, string ancestorId)
    {
        for (int guard = 0; groupId != null && guard < 64; guard++)
        {
            if (groupId == ancestorId)
                return true;
            groupId = FindGroup(groupId)?.ParentId;
        }
        return false;
    }
}

/// <summary>A folder in the layers panel. Groups nest through <see cref="ParentId"/>.</summary>
public sealed class LayerGroup
{
    public string Id { get; set; } = Document.NewId();
    public string Name { get; set; } = "Group";
    public string? ParentId { get; set; }
    public bool Visible { get; set; } = true;
    public bool Expanded { get; set; } = true;

    public LayerGroup Clone() => (LayerGroup)MemberwiseClone();
}

/// <summary>An outline vertex. <see cref="Curve"/> bends the edge that starts at this point.</summary>
public sealed class PatternPoint
{
    public Vector2 Position { get; set; }

    /// <summary>Quadratic Bezier control point for the edge from this point to the next, or null for a straight edge.</summary>
    public Vector2? Curve { get; set; }

    public PatternPoint() { }
    public PatternPoint(float x, float y) => Position = new Vector2(x, y);
    public PatternPoint(float x, float y, float cx, float cy) : this(x, y) => Curve = new Vector2(cx, cy);

    public PatternPoint Clone() => (PatternPoint)MemberwiseClone();
}

/// <summary>Where a flat piece is placed around the avatar before simulation.</summary>
public sealed class Placement
{
    /// <summary>Position of the piece centre in metres.</summary>
    public Vector3 Position { get; set; } = new(0, 1.1f, 0.3f);

    /// <summary>Euler rotation in degrees, applied X then Y then Z.</summary>
    public Vector3 Rotation { get; set; }

    /// <summary>Degrees of arc the piece is wrapped through around its vertical axis (0 = flat).</summary>
    public float Curvature { get; set; }

    public Placement Clone() => (Placement)MemberwiseClone();
}

/// <summary>Per-piece UV adjustment on top of the automatic layout derived from the 2D pattern.</summary>
public sealed class UvAdjust
{
    public Vector2 Offset { get; set; }
    public float Scale { get; set; } = 1f;
    public float Rotation { get; set; }

    public UvAdjust Clone() => (UvAdjust)MemberwiseClone();
}

public sealed class PatternPiece
{
    public string Id { get; set; } = Document.NewId();
    public string Name { get; set; } = "Piece";
    public string? GroupId { get; set; }
    public bool Visible { get; set; } = true;

    /// <summary>Position of the piece's local origin in pattern space (cm).</summary>
    public Vector2 Origin { get; set; }

    /// <summary>Closed outline in local centimetres, counter-clockwise.</summary>
    public List<PatternPoint> Points { get; set; } = new();

    public Placement Placement { get; set; } = new();
    public FabricPhysics Physics { get; set; } = new();
    public string? MaterialId { get; set; }

    /// <summary>Overrides the garment's particle distance (cm) for this piece.</summary>
    public float? ParticleDistance { get; set; }

    public UvAdjust Uv { get; set; } = new();

    /// <summary>Simulated vertex positions (x,y,z triples in metres), valid only for the mesh identified by <see cref="DrapeKey"/>.</summary>
    public float[]? Drape { get; set; }

    /// <summary>Fingerprint of the mesh topology the drape belongs to.</summary>
    public string? DrapeKey { get; set; }

    public PatternPiece Clone() => new()
    {
        Id = Id,
        Name = Name,
        GroupId = GroupId,
        Visible = Visible,
        Origin = Origin,
        Points = Points.Select(p => p.Clone()).ToList(),
        Placement = Placement.Clone(),
        Physics = Physics.Clone(),
        MaterialId = MaterialId,
        ParticleDistance = ParticleDistance,
        Uv = Uv.Clone(),
        Drape = Drape, // drape arrays are replaced, never mutated in place, so sharing is safe
        DrapeKey = DrapeKey,
    };

    public int EdgeCount => Points.Count;

    public void ClearDrape()
    {
        Drape = null;
        DrapeKey = null;
    }
}

/// <summary>Identifies the outline edge that starts at point <see cref="Edge"/> of a piece.</summary>
public sealed class EdgeRef
{
    public string PieceId { get; set; } = "";
    public int Edge { get; set; }

    public EdgeRef() { }
    public EdgeRef(string pieceId, int edge)
    {
        PieceId = pieceId;
        Edge = edge;
    }

    public EdgeRef Clone() => new(PieceId, Edge);
    public bool Matches(string pieceId, int edge) => PieceId == pieceId && Edge == edge;
}

/// <summary>Sews edge <see cref="A"/> to edge <see cref="B"/>.</summary>
public sealed class Seam
{
    public string Id { get; set; } = Document.NewId();
    public EdgeRef A { get; set; } = new();
    public EdgeRef B { get; set; } = new();

    /// <summary>
    /// False sews start-to-end (the natural match for two counter-clockwise outlines facing each other);
    /// true sews start-to-start.
    /// </summary>
    public bool Reversed { get; set; }

    public Seam Clone() => new() { Id = Id, A = A.Clone(), B = B.Clone(), Reversed = Reversed };
}

public sealed class FabricPhysics
{
    public string Preset { get; set; } = "Cotton";

    /// <summary>Fabric weight in grams per square metre.</summary>
    public float Density { get; set; } = 150f;

    /// <summary>0-100. Resistance to stretching along the weave.</summary>
    public float Stretch { get; set; } = 80f;

    /// <summary>0-100. Resistance to shearing (diagonal distortion).</summary>
    public float Shear { get; set; } = 60f;

    /// <summary>0-100. Resistance to folding.</summary>
    public float Bend { get; set; } = 30f;

    /// <summary>0-100. How quickly motion dies out.</summary>
    public float Damping { get; set; } = 20f;

    /// <summary>0-100. Grip against the avatar.</summary>
    public float Friction { get; set; } = 40f;

    /// <summary>Collision thickness in millimetres.</summary>
    public float Thickness { get; set; } = 3f;

    public FabricPhysics Clone() => (FabricPhysics)MemberwiseClone();

    public static readonly IReadOnlyList<FabricPhysics> Presets = new[]
    {
        new FabricPhysics { Preset = "Cotton", Density = 150, Stretch = 80, Shear = 60, Bend = 30, Damping = 20, Friction = 40, Thickness = 3 },
        new FabricPhysics { Preset = "Silk", Density = 70, Stretch = 75, Shear = 35, Bend = 5, Damping = 10, Friction = 15, Thickness = 2 },
        new FabricPhysics { Preset = "Jersey Knit", Density = 180, Stretch = 35, Shear = 25, Bend = 12, Damping = 25, Friction = 45, Thickness = 3 },
        new FabricPhysics { Preset = "Denim", Density = 400, Stretch = 95, Shear = 85, Bend = 75, Damping = 35, Friction = 55, Thickness = 4 },
        new FabricPhysics { Preset = "Leather", Density = 700, Stretch = 98, Shear = 92, Bend = 92, Damping = 45, Friction = 65, Thickness = 5 },
        new FabricPhysics { Preset = "Wool", Density = 300, Stretch = 70, Shear = 55, Bend = 50, Damping = 40, Friction = 60, Thickness = 5 },
        new FabricPhysics { Preset = "Chiffon", Density = 40, Stretch = 65, Shear = 20, Bend = 2, Damping = 8, Friction = 20, Thickness = 2 },
    };

    public static FabricPhysics FromPreset(string name)
        => (Presets.FirstOrDefault(p => p.Preset.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? Presets[0]).Clone();
}

public sealed class FabricMaterial
{
    public string Id { get; set; } = Document.NewId();
    public string Name { get; set; } = "Fabric";

    /// <summary>Base colour as #RRGGBB.</summary>
    public string Color { get; set; } = "#C8CED8";

    /// <summary>Absolute path of the base colour texture, or null.</summary>
    public string? Texture { get; set; }

    /// <summary>How many times the texture repeats across the UV square.</summary>
    public float Repeat { get; set; } = 1f;

    public float Roughness { get; set; } = 0.8f;
    public float Metallic { get; set; }
    public float Opacity { get; set; } = 1f;

    public FabricMaterial Clone() => (FabricMaterial)MemberwiseClone();
}

public sealed class SimulationSettings
{
    public float Gravity { get; set; } = -9.81f;
    public int Substeps { get; set; } = 12;
    public float TimeScale { get; set; } = 1f;

    /// <summary>Keeps cloth layers from passing through each other (slower).</summary>
    public bool SelfCollision { get; set; }

    public bool GroundPlane { get; set; } = true;

    public SimulationSettings Clone() => (SimulationSettings)MemberwiseClone();
}

public enum AvatarKind
{
    None,
    Mannequin,
    MakeHuman,
    CustomObj,
}

public sealed class AvatarSettings
{
    public AvatarKind Kind { get; set; } = AvatarKind.Mannequin;

    /// <summary>OBJ file used when <see cref="Kind"/> is <see cref="AvatarKind.CustomObj"/>.</summary>
    public string? Path { get; set; }

    /// <summary>Multiplier that converts custom OBJ units to metres.</summary>
    public float Scale { get; set; } = 1f;

    public AvatarSettings Clone() => (AvatarSettings)MemberwiseClone();
}
