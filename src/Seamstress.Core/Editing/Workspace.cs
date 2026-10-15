using System.Numerics;
using Seamstress.Core.Geometry;
using Seamstress.Core.IO;
using Seamstress.Core.Model;

namespace Seamstress.Core.Editing;

/// <summary>
/// The open project together with everything the UI needs around it: selection, undo history, the current file and a
/// cache of generated meshes. Every change to the document goes through here so it is undoable.
/// </summary>
public sealed class Workspace
{
    private readonly Dictionary<string, (string Key, GarmentMesh Mesh)> _meshes = new();

    public History History { get; }
    public Document Document => History.Current;
    public string? FilePath { get; private set; }

    public HashSet<string> SelectedPieces { get; } = new();
    public string? SelectedSeamId { get; private set; }
    public (string PieceId, int Index)? SelectedPoint { get; private set; }

    /// <summary>The garment or group row highlighted in the layers panel, if any.</summary>
    public string? SelectedLayerId { get; private set; }

    public string? ActiveGarmentId { get; private set; }

    public event Action? SelectionChanged;

    public Workspace(Document? document = null)
    {
        History = new History(document ?? new Document());
        History.Changed += Sanitize;
        ActiveGarmentId = Document.Garments.FirstOrDefault()?.Id;
    }

    public Garment? ActiveGarment => Document.FindGarment(ActiveGarmentId) ?? Document.Garments.FirstOrDefault();

    public IEnumerable<PatternPiece> SelectedPieceObjects()
        => Document.AllPieces().Where(p => SelectedPieces.Contains(p.Id));

    public PatternPiece? PrimaryPiece => SelectedPieceObjects().FirstOrDefault();

    // ---------------------------------------------------------------- meshes

    /// <summary>Simulation mesh of a garment, rebuilt only when its outlines, seams or particle distances change.</summary>
    public GarmentMesh MeshFor(Garment garment)
    {
        string key = MeshKey(garment);
        if (_meshes.TryGetValue(garment.Id, out var cached) && cached.Key == key)
        {
            if (!ReferenceEquals(cached.Mesh.Garment, garment))
            {
                // Undo/redo swaps in a different Garment instance with identical geometry; keep the mesh, update the owner.
                cached = (key, new GarmentMesh { Garment = garment, Pieces = cached.Mesh.Pieces, Stitches = cached.Mesh.Stitches });
                _meshes[garment.Id] = cached;
            }
            return cached.Mesh;
        }
        var mesh = Mesher.Build(garment);
        _meshes[garment.Id] = (key, mesh);
        return mesh;
    }

    private static string MeshKey(Garment garment)
    {
        var hash = new HashCode();
        hash.Add(garment.ParticleDistance);
        hash.Add(garment.Topology);
        foreach (var piece in garment.Pieces)
        {
            hash.Add(piece.Id);
            hash.Add(piece.ParticleDistance);
            foreach (var point in piece.Points)
            {
                hash.Add(point.Position);
                hash.Add(point.Curve);
            }
        }
        foreach (var seam in garment.Seams)
        {
            hash.Add(seam.A.PieceId);
            hash.Add(seam.A.Edge);
            hash.Add(seam.B.PieceId);
            hash.Add(seam.B.Edge);
            hash.Add(seam.Reversed);
        }
        return $"{garment.Pieces.Count}:{garment.Seams.Count}:{hash.ToHashCode()}";
    }

    // ---------------------------------------------------------------- files

    public void New()
    {
        FilePath = null;
        _meshes.Clear();
        ClearSelectionState();
        History.Reset(new Document());
        ActiveGarmentId = null;
        SelectionChanged?.Invoke();
    }

    public void Open(string path)
    {
        var document = GarmentFile.Load(path);
        FilePath = path;
        _meshes.Clear();
        ClearSelectionState();
        History.Reset(document);
        ActiveGarmentId = document.Garments.FirstOrDefault()?.Id;
        SelectionChanged?.Invoke();
    }

    public void Save(string path)
    {
        if (Document.Name == "Untitled" || FilePath == null)
        {
            string name = Path.GetFileName(path);
            if (name.EndsWith(GarmentFile.Extension, StringComparison.OrdinalIgnoreCase))
                name = name[..^GarmentFile.Extension.Length];
            else
                name = Path.GetFileNameWithoutExtension(name);
            Document.Name = name;
        }
        GarmentFile.Save(Document, path);
        FilePath = path;
        History.MarkSaved();
    }

    // ---------------------------------------------------------------- selection

    private void ClearSelectionState()
    {
        SelectedPieces.Clear();
        SelectedSeamId = null;
        SelectedPoint = null;
        SelectedLayerId = null;
    }

    public void ClearSelection()
    {
        ClearSelectionState();
        SelectionChanged?.Invoke();
    }

    public void SelectPiece(string pieceId, bool additive = false)
    {
        if (Document.FindPiece(pieceId) == null)
            return;
        SelectedSeamId = null;
        SelectedPoint = null;
        SelectedLayerId = null;
        if (additive)
        {
            if (!SelectedPieces.Remove(pieceId))
                SelectedPieces.Add(pieceId);
        }
        else
        {
            SelectedPieces.Clear();
            SelectedPieces.Add(pieceId);
        }
        ActiveGarmentId = Document.GarmentOfPiece(pieceId)?.Id ?? ActiveGarmentId;
        SelectionChanged?.Invoke();
    }

    public void SelectSeam(string seamId)
    {
        ClearSelectionState();
        SelectedSeamId = seamId;
        var owner = Document.Garments.FirstOrDefault(g => g.Seams.Any(s => s.Id == seamId));
        ActiveGarmentId = owner?.Id ?? ActiveGarmentId;
        SelectionChanged?.Invoke();
    }

    public void SelectPoint(string pieceId, int index)
    {
        ClearSelectionState();
        SelectedPieces.Add(pieceId);
        SelectedPoint = (pieceId, index);
        ActiveGarmentId = Document.GarmentOfPiece(pieceId)?.Id ?? ActiveGarmentId;
        SelectionChanged?.Invoke();
    }

    /// <summary>Selects a row of the layers panel: a garment, a group (with everything inside) or a piece.</summary>
    public void SelectLayer(string id, bool additive = false)
    {
        if (Document.FindPiece(id) != null)
        {
            SelectPiece(id, additive);
            return;
        }
        var garment = Document.FindGarment(id);
        if (garment != null)
        {
            ClearSelectionState();
            SelectedLayerId = id;
            ActiveGarmentId = id;
            foreach (var piece in garment.Pieces)
                SelectedPieces.Add(piece.Id);
            SelectionChanged?.Invoke();
            return;
        }
        foreach (var g in Document.Garments)
        {
            if (g.FindGroup(id) == null)
                continue;
            ClearSelectionState();
            SelectedLayerId = id;
            ActiveGarmentId = g.Id;
            foreach (var piece in g.Pieces.Where(p => p.GroupId != null && g.IsGroupWithin(p.GroupId, id)))
                SelectedPieces.Add(piece.Id);
            SelectionChanged?.Invoke();
            return;
        }
    }

    /// <summary>Drops selection entries that no longer exist (after undo, delete, open...).</summary>
    private void Sanitize()
    {
        bool changed = SelectedPieces.RemoveWhere(id => Document.FindPiece(id) == null) > 0;
        if (SelectedSeamId != null && !Document.Garments.Any(g => g.Seams.Any(s => s.Id == SelectedSeamId)))
        {
            SelectedSeamId = null;
            changed = true;
        }
        if (SelectedPoint is { } point)
        {
            var piece = Document.FindPiece(point.PieceId);
            if (piece == null || point.Index >= piece.Points.Count)
            {
                SelectedPoint = null;
                changed = true;
            }
        }
        if (SelectedLayerId != null && Document.FindGarment(SelectedLayerId) == null
            && !Document.Garments.Any(g => g.FindGroup(SelectedLayerId) != null))
        {
            SelectedLayerId = null;
            changed = true;
        }
        if (Document.FindGarment(ActiveGarmentId) == null)
        {
            ActiveGarmentId = Document.Garments.FirstOrDefault()?.Id;
            changed = true;
        }
        foreach (var id in _meshes.Keys.Where(id => Document.FindGarment(id) == null).ToList())
            _meshes.Remove(id);
        if (changed)
            SelectionChanged?.Invoke();
    }

    // ---------------------------------------------------------------- garments and pieces

    public Garment AddGarment(string? name = null)
    {
        Garment? added = null;
        History.Execute("Add garment", doc =>
        {
            added = new Garment { Name = doc.UniqueName(name ?? "Garment") };
            doc.Garments.Add(added);
        });
        SelectLayer(added!.Id);
        return added;
    }

    /// <param name="bodyHeight">Height of the current avatar in metres, so the pieces start at the right level.</param>
    public Garment AddTemplate(string template, float bodyHeight = Templates.ReferenceBodyHeight)
    {
        Garment? added = null;
        History.Execute($"Add {template}", doc =>
        {
            added = Templates.Create(template, doc, bodyHeight);
            doc.Garments.Add(added);
        });
        SelectLayer(added!.Id);
        return added;
    }

    /// <summary>Creates a piece from an outline given in pattern space (cm) and adds it to the active garment.</summary>
    public PatternPiece? AddPiece(IReadOnlyList<Vector2> outline, string baseName)
    {
        if (outline.Count < 3 || MathF.Abs(Outline.SignedArea(outline)) < 1f)
            return null;
        PatternPiece? piece = null;
        History.Execute($"Add {baseName.ToLowerInvariant()}", doc =>
        {
            var garment = doc.FindGarment(ActiveGarmentId);
            if (garment == null)
            {
                garment = new Garment { Name = doc.UniqueName("Garment") };
                doc.Garments.Add(garment);
                ActiveGarmentId = garment.Id;
            }
            var min = outline.Aggregate(new Vector2(float.MaxValue), Vector2.Min);
            piece = new PatternPiece { Name = doc.UniqueName(baseName), Origin = min };
            foreach (var p in outline)
                piece.Points.Add(new PatternPoint { Position = p - min });
            Outline.EnsureCounterClockwise(piece);
            var existing = garment.Pieces.FirstOrDefault();
            piece.MaterialId = existing?.MaterialId;
            if (existing != null)
                piece.Physics = existing.Physics.Clone();
            if (piece.MaterialId == null)
            {
                var material = new FabricMaterial { Name = doc.UniqueName("Fabric") };
                doc.Materials.Add(material);
                piece.MaterialId = material.Id;
            }
            // New pieces start in front of the avatar at chest height.
            piece.Placement = new Placement { Position = new Vector3(0, 1.10f, 0.30f + 0.02f * garment.Pieces.Count) };
            garment.Pieces.Add(piece);
        });
        if (piece != null)
            SelectPiece(piece.Id);
        return piece;
    }

    public void DeleteSelection()
    {
        if (SelectedSeamId != null)
        {
            string id = SelectedSeamId;
            History.Execute("Delete seam", doc =>
            {
                foreach (var g in doc.Garments)
                    g.Seams.RemoveAll(s => s.Id == id);
            });
            return;
        }
        if (SelectedPoint is { } point)
        {
            DeletePoint(point.PieceId, point.Index);
            return;
        }
        if (SelectedLayerId != null)
        {
            DeleteLayer(SelectedLayerId);
            return;
        }
        if (SelectedPieces.Count == 0)
            return;
        var ids = SelectedPieces.ToHashSet();
        History.Execute(ids.Count == 1 ? "Delete piece" : "Delete pieces", doc =>
        {
            foreach (var g in doc.Garments)
                RemovePieces(g, ids);
        });
    }

    private static void RemovePieces(Garment garment, HashSet<string> ids)
    {
        garment.Pieces.RemoveAll(p => ids.Contains(p.Id));
        garment.Seams.RemoveAll(s => ids.Contains(s.A.PieceId) || ids.Contains(s.B.PieceId));
    }

    /// <summary>Deletes a garment, a group (its contents move to the parent) or a piece by id.</summary>
    public void DeleteLayer(string id)
    {
        if (Document.FindGarment(id) != null)
        {
            History.Execute("Delete garment", doc => doc.Garments.RemoveAll(g => g.Id == id));
            return;
        }
        if (Document.FindPiece(id) != null)
        {
            History.Execute("Delete piece", doc =>
            {
                foreach (var g in doc.Garments)
                    RemovePieces(g, new HashSet<string> { id });
            });
            return;
        }
        Ungroup(id);
    }

    public void Rename(string id, string name)
    {
        name = name.Trim();
        if (name.Length == 0)
            return;
        History.Execute("Rename", doc =>
        {
            if (doc.FindGarment(id) is { } garment)
                garment.Name = name;
            else if (doc.FindPiece(id) is { } piece)
                piece.Name = name;
            else
            {
                foreach (var g in doc.Garments)
                {
                    if (g.FindGroup(id) is { } group)
                        group.Name = name;
                }
            }
        });
    }

    public void SetVisible(string id, bool visible)
    {
        History.Execute(visible ? "Show" : "Hide", doc =>
        {
            if (doc.FindGarment(id) is { } garment)
                garment.Visible = visible;
            else if (doc.FindPiece(id) is { } piece)
                piece.Visible = visible;
            else
            {
                foreach (var g in doc.Garments)
                {
                    if (g.FindGroup(id) is { } group)
                        group.Visible = visible;
                }
            }
        });
    }

    /// <summary>Folding a row open or shut is view state, so it is not recorded as an undo step.</summary>
    public void SetExpanded(string id, bool expanded)
    {
        if (Document.FindGarment(id) is { } garment)
            garment.Expanded = expanded;
        foreach (var g in Document.Garments)
        {
            if (g.FindGroup(id) is { } group)
                group.Expanded = expanded;
        }
        History.NotifyLiveChange();
    }

    /// <summary>True when the piece and all layers above it are visible.</summary>
    public static bool IsEffectivelyVisible(Garment garment, PatternPiece piece)
    {
        if (!garment.Visible || !piece.Visible)
            return false;
        string? groupId = piece.GroupId;
        for (int guard = 0; groupId != null && guard < 64; guard++)
        {
            var group = garment.FindGroup(groupId);
            if (group == null)
                break;
            if (!group.Visible)
                return false;
            groupId = group.ParentId;
        }
        return true;
    }

    // ---------------------------------------------------------------- grouping

    public LayerGroup? AddGroup(string? garmentId = null)
    {
        LayerGroup? group = null;
        string? target = garmentId ?? ActiveGarment?.Id;
        if (target == null)
            return null;
        History.Execute("Add group", doc =>
        {
            var garment = doc.FindGarment(target);
            if (garment == null)
                return;
            group = new LayerGroup { Name = doc.UniqueName("Group") };
            garment.Groups.Add(group);
        });
        return group;
    }

    /// <summary>Puts the selected pieces (which must share a garment) into a new group.</summary>
    public LayerGroup? GroupSelection()
    {
        var ids = SelectedPieces.ToHashSet();
        if (ids.Count == 0)
            return null;
        var owner = Document.GarmentOfPiece(ids.First());
        if (owner == null || !ids.All(id => owner.FindPiece(id) != null))
            return null;
        LayerGroup? group = null;
        string ownerId = owner.Id;
        History.Execute("Group pieces", doc =>
        {
            var garment = doc.FindGarment(ownerId)!;
            var members = garment.Pieces.Where(p => ids.Contains(p.Id)).ToList();
            // If everything selected already shares a parent group, nest the new group inside it.
            var parents = members.Select(p => p.GroupId).Distinct().ToList();
            group = new LayerGroup { Name = doc.UniqueName("Group"), ParentId = parents.Count == 1 ? parents[0] : null };
            garment.Groups.Add(group);
            foreach (var piece in members)
                piece.GroupId = group.Id;
        });
        return group;
    }

    /// <summary>Removes a group; its pieces and sub-groups move up to the group's parent.</summary>
    public void Ungroup(string groupId)
    {
        if (!Document.Garments.Any(g => g.FindGroup(groupId) != null))
            return;
        History.Execute("Ungroup", doc =>
        {
            foreach (var garment in doc.Garments)
            {
                var group = garment.FindGroup(groupId);
                if (group == null)
                    continue;
                foreach (var piece in garment.Pieces.Where(p => p.GroupId == groupId))
                    piece.GroupId = group.ParentId;
                foreach (var child in garment.Groups.Where(g => g.ParentId == groupId))
                    child.ParentId = group.ParentId;
                garment.Groups.Remove(group);
            }
        });
    }

    /// <summary>Moves a piece or group into a group of the same garment, or to the garment's top level when null.</summary>
    public void MoveToGroup(string id, string? groupId)
    {
        History.Execute("Move layer", doc =>
        {
            foreach (var garment in doc.Garments)
            {
                if (groupId != null && garment.FindGroup(groupId) == null)
                    continue;
                if (garment.FindPiece(id) is { } piece)
                    piece.GroupId = groupId;
                else if (garment.FindGroup(id) is { } group && (groupId == null || !garment.IsGroupWithin(groupId, id)))
                    group.ParentId = groupId;
            }
        });
    }

    // ---------------------------------------------------------------- piece editing

    public void DuplicateSelection()
    {
        var ids = SelectedPieces.ToList();
        if (ids.Count == 0)
            return;
        var created = new List<string>();
        History.Execute("Duplicate", doc =>
        {
            foreach (var garment in doc.Garments)
            {
                foreach (var source in garment.Pieces.Where(p => ids.Contains(p.Id)).ToList())
                {
                    var copy = source.Clone();
                    copy.Id = Document.NewId();
                    copy.Name = doc.UniqueName(source.Name);
                    var (min, max) = Outline.Bounds(source);
                    copy.Origin += new Vector2(max.X - min.X + 8, 0);
                    copy.Placement.Position += new Vector3(0, 0, 0.03f);
                    copy.ClearDrape();
                    garment.Pieces.Add(copy);
                    created.Add(copy.Id);
                }
            }
        });
        SelectedPieces.Clear();
        foreach (var id in created)
            SelectedPieces.Add(id);
        SelectionChanged?.Invoke();
    }

    /// <summary>Mirrors the selected pieces left-to-right. Seams stay attached to the same edges.</summary>
    /// <summary>
    /// Turns the selected pieces' normals to face the other side of the cloth, as one undo step. Each piece is
    /// flipped from its own state, so a selection with mixed directions stays mixed. The drape is kept.
    /// </summary>
    public void InvertNormals()
    {
        var ids = SelectedPieces.ToHashSet();
        if (ids.Count == 0)
            return;
        History.Execute(ids.Count == 1 ? "Invert normals" : "Invert normals of " + ids.Count + " pieces", doc =>
        {
            foreach (var piece in doc.AllPieces().Where(p => ids.Contains(p.Id)))
                piece.FlipNormals = !piece.FlipNormals;
        });
    }

    public void MirrorSelection()
    {
        var ids = SelectedPieces.ToHashSet();
        if (ids.Count == 0)
            return;
        History.Execute("Mirror", doc =>
        {
            foreach (var garment in doc.Garments)
            {
                foreach (var piece in garment.Pieces.Where(p => ids.Contains(p.Id)))
                {
                    var (min, max) = Outline.Bounds(piece);
                    float axis = min.X + max.X;
                    int n = piece.Points.Count;
                    // Mirroring flips the winding, so walk the outline backwards to stay counter-clockwise.
                    var mirrored = new List<PatternPoint>(n);
                    for (int i = 0; i < n; i++)
                    {
                        var source = piece.Points[(n - i) % n];
                        var edgeOwner = piece.Points[(n - i - 1 + n) % n];
                        mirrored.Add(new PatternPoint
                        {
                            Position = new Vector2(axis - source.Position.X, source.Position.Y),
                            Curve = edgeOwner.Curve is { } c ? new Vector2(axis - c.X, c.Y) : null,
                        });
                    }
                    piece.Points = mirrored;
                    // Edge k (from point k) became edge n-1-k and now runs the other way, so a seam to an
                    // edge that was not mirrored has to swap which ends it pairs up.
                    foreach (var seam in garment.Seams)
                    {
                        bool a = seam.A.PieceId == piece.Id, b = seam.B.PieceId == piece.Id;
                        if (a)
                            seam.A.Edge = n - 1 - seam.A.Edge;
                        if (b)
                            seam.B.Edge = n - 1 - seam.B.Edge;
                        if (a != b)
                            seam.Reversed = !seam.Reversed;
                    }
                    piece.ClearDrape();
                }
            }
        });
    }

    public void RotateSelection(float degrees)
    {
        var ids = SelectedPieces.ToHashSet();
        if (ids.Count == 0)
            return;
        float r = degrees * MathF.PI / 180f;
        float cos = MathF.Cos(r), sin = MathF.Sin(r);
        History.Execute("Rotate", doc =>
        {
            foreach (var piece in doc.AllPieces().Where(p => ids.Contains(p.Id)))
            {
                var (min, max) = Outline.Bounds(piece);
                var centre = (min + max) / 2;
                Vector2 Turn(Vector2 p)
                {
                    var d = p - centre;
                    return centre + new Vector2(d.X * cos - d.Y * sin, d.X * sin + d.Y * cos);
                }
                foreach (var point in piece.Points)
                {
                    point.Position = Turn(point.Position);
                    if (point.Curve is { } c)
                        point.Curve = Turn(c);
                }
                piece.ClearDrape();
            }
        });
    }

    /// <summary>Splits an edge by inserting a new outline point at the given local position.</summary>
    public void InsertPoint(string pieceId, int edge, Vector2 localPosition)
    {
        History.Execute("Add point", doc =>
        {
            var garment = doc.GarmentOfPiece(pieceId);
            var piece = garment?.FindPiece(pieceId);
            if (garment == null || piece == null || edge < 0 || edge >= piece.Points.Count)
                return;
            piece.Points[edge].Curve = null;
            piece.Points.Insert(edge + 1, new PatternPoint { Position = localPosition });
            foreach (var seam in garment.Seams)
            {
                if (seam.A.PieceId == pieceId && seam.A.Edge > edge)
                    seam.A.Edge++;
                if (seam.B.PieceId == pieceId && seam.B.Edge > edge)
                    seam.B.Edge++;
            }
        });
        SelectPoint(pieceId, edge + 1);
    }

    public void DeletePoint(string pieceId, int index)
    {
        var target = Document.FindPiece(pieceId);
        if (target == null || target.Points.Count <= 3 || index < 0 || index >= target.Points.Count)
            return;
        History.Execute("Delete point", doc =>
        {
            var garment = doc.GarmentOfPiece(pieceId)!;
            var piece = garment.FindPiece(pieceId)!;
            int n = piece.Points.Count;
            int previous = (index - 1 + n) % n;
            // The edges before and after the point merge into one straight edge.
            piece.Points[previous].Curve = null;
            piece.Points.RemoveAt(index);
            garment.Seams.RemoveAll(s => s.A.Matches(pieceId, index) || s.B.Matches(pieceId, index));
            foreach (var seam in garment.Seams)
            {
                if (seam.A.PieceId == pieceId && seam.A.Edge > index)
                    seam.A.Edge--;
                if (seam.B.PieceId == pieceId && seam.B.Edge > index)
                    seam.B.Edge--;
            }
        });
        SelectedPoint = null;
        SelectionChanged?.Invoke();
    }

    /// <summary>Removes several points as one undo step. Every piece keeps at least three points.</summary>
    public void DeletePoints(IReadOnlyCollection<(string PieceId, int Index)> points)
    {
        if (points.Count == 0)
            return;
        History.Execute(points.Count == 1 ? "Delete point" : "Delete points", doc =>
        {
            // Highest index first, so the indices still to be removed stay valid.
            foreach (var (pieceId, index) in points.Distinct().OrderByDescending(p => p.Index))
            {
                var garment = doc.GarmentOfPiece(pieceId);
                var piece = garment?.FindPiece(pieceId);
                if (piece == null || piece.Points.Count <= 3 || index < 0 || index >= piece.Points.Count)
                    continue;
                int n = piece.Points.Count;
                piece.Points[(index - 1 + n) % n].Curve = null;
                piece.Points.RemoveAt(index);
                garment!.Seams.RemoveAll(s => s.A.Matches(pieceId, index) || s.B.Matches(pieceId, index));
                foreach (var seam in garment.Seams)
                {
                    if (seam.A.PieceId == pieceId && seam.A.Edge > index)
                        seam.A.Edge--;
                    if (seam.B.PieceId == pieceId && seam.B.Edge > index)
                        seam.B.Edge--;
                }
            }
        });
        SelectedPoint = null;
        SelectionChanged?.Invoke();
    }

    public void StraightenEdge(string pieceId, int edge)
    {
        History.Execute("Straighten edge", doc =>
        {
            var piece = doc.FindPiece(pieceId);
            if (piece != null && edge >= 0 && edge < piece.Points.Count)
                piece.Points[edge].Curve = null;
        });
    }

    // ---------------------------------------------------------------- seams

    public Seam? AddSeam(EdgeRef a, EdgeRef b)
    {
        var garment = Document.GarmentOfPiece(a.PieceId);
        if (garment == null || garment.FindPiece(b.PieceId) == null)
            return null; // seams only join pieces of the same garment
        if (a.PieceId == b.PieceId && a.Edge == b.Edge)
            return null;
        bool Exists(Seam s) => (s.A.Matches(a.PieceId, a.Edge) && s.B.Matches(b.PieceId, b.Edge))
                            || (s.A.Matches(b.PieceId, b.Edge) && s.B.Matches(a.PieceId, a.Edge));
        if (garment.Seams.Any(Exists))
            return null;
        Seam? seam = null;
        string garmentId = garment.Id;
        History.Execute("Sew edges", doc =>
        {
            seam = new Seam { A = a.Clone(), B = b.Clone() };
            doc.FindGarment(garmentId)!.Seams.Add(seam);
        });
        if (seam != null)
            SelectSeam(seam.Id);
        return seam;
    }

    public void ReverseSeam(string seamId)
    {
        History.Execute("Reverse seam", doc =>
        {
            foreach (var seam in doc.Garments.SelectMany(g => g.Seams).Where(s => s.Id == seamId))
                seam.Reversed = !seam.Reversed;
        });
    }

    // ---------------------------------------------------------------- properties

    /// <summary>Sets a numeric property on every selected piece. Unknown keys are ignored.</summary>
    public void SetPieceValue(string key, float value)
    {
        var ids = SelectedPieces.ToHashSet();
        if (ids.Count == 0 || float.IsNaN(value) || float.IsInfinity(value))
            return;
        History.Execute("Change " + Describe(key), doc =>
        {
            foreach (var piece in doc.AllPieces().Where(p => ids.Contains(p.Id)))
                ApplyPieceValue(piece, key, value);
        }, "piece:" + key + ":" + string.Join(",", ids.OrderBy(i => i)));
    }

    private static string Describe(string key) => key switch
    {
        "posX" or "posY" or "posZ" => "position",
        "rotX" or "rotY" or "rotZ" => "rotation",
        "particleDistance" => "particle distance",
        "uvScale" or "uvRotation" or "uvOffsetX" or "uvOffsetY" => "UV",
        _ => key,
    };

    private static void ApplyPieceValue(PatternPiece piece, string key, float value)
    {
        var placement = piece.Placement;
        var physics = piece.Physics;
        bool isPhysics = false, moved = false;
        switch (key)
        {
            case "posX": placement.Position = placement.Position with { X = value }; moved = true; break;
            case "posY": placement.Position = placement.Position with { Y = value }; moved = true; break;
            case "posZ": placement.Position = placement.Position with { Z = value }; moved = true; break;
            case "rotX": placement.Rotation = placement.Rotation with { X = value }; moved = true; break;
            case "rotY": placement.Rotation = placement.Rotation with { Y = value }; moved = true; break;
            case "rotZ": placement.Rotation = placement.Rotation with { Z = value }; moved = true; break;
            case "curvature": placement.Curvature = Math.Clamp(value, -350, 350); moved = true; break;
            case "particleDistance": piece.ParticleDistance = value <= 0 ? null : Math.Clamp(value, 0.3f, 20f); break;
            case "uvScale": piece.Uv.Scale = Math.Clamp(value, 0.01f, 100f); break;
            case "uvRotation": piece.Uv.Rotation = value; break;
            case "uvOffsetX": piece.Uv.Offset = piece.Uv.Offset with { X = value }; break;
            case "uvOffsetY": piece.Uv.Offset = piece.Uv.Offset with { Y = value }; break;
            case "density": physics.Density = Math.Clamp(value, 5, 5000); isPhysics = true; break;
            case "stretch": physics.Stretch = Math.Clamp(value, 0, 100); isPhysics = true; break;
            case "shear": physics.Shear = Math.Clamp(value, 0, 100); isPhysics = true; break;
            case "bend": physics.Bend = Math.Clamp(value, 0, 100); isPhysics = true; break;
            case "damping": physics.Damping = Math.Clamp(value, 0, 100); isPhysics = true; break;
            case "friction": physics.Friction = Math.Clamp(value, 0, 100); isPhysics = true; break;
            case "thickness": physics.Thickness = Math.Clamp(value, 0.1f, 50); isPhysics = true; break;
        }
        if (isPhysics)
            physics.Preset = "Custom";
        // Re-arranging a piece throws away its simulated shape so the new placement becomes visible.
        if (moved)
            piece.ClearDrape();
    }

    public void SetPhysicsPreset(string preset)
    {
        var ids = SelectedPieces.ToHashSet();
        if (ids.Count == 0)
            return;
        History.Execute("Apply fabric preset", doc =>
        {
            foreach (var piece in doc.AllPieces().Where(p => ids.Contains(p.Id)))
                piece.Physics = FabricPhysics.FromPreset(preset);
        });
    }

    public void SetGarmentParticleDistance(string garmentId, float value)
    {
        if (float.IsNaN(value))
            return;
        History.Execute("Change particle distance", doc =>
        {
            if (doc.FindGarment(garmentId) is { } garment)
                garment.ParticleDistance = Math.Clamp(value, 0.3f, 20f);
        }, "garment-pd:" + garmentId);
    }

    public void SetTopology(string garmentId, MeshTopology topology)
    {
        if (Document.FindGarment(garmentId) is not { } current || current.Topology == topology)
            return;
        History.Execute("Change topology", doc => doc.FindGarment(garmentId)!.Topology = topology);
    }

    public void SetSimulateWithOthers(string garmentId, bool value)
    {
        History.Execute("Change simulation scope", doc =>
        {
            if (doc.FindGarment(garmentId) is { } garment)
                garment.SimulateWithOthers = value;
        });
    }

    public void SetSimulationValue(string key, float value)
    {
        History.Execute("Change simulation settings", doc =>
        {
            var s = doc.Simulation;
            switch (key)
            {
                case "gravity": s.Gravity = Math.Clamp(value, -30, 30); break;
                case "substeps": s.Substeps = Math.Clamp((int)MathF.Round(value), 1, 64); break;
                case "timeScale": s.TimeScale = Math.Clamp(value, 0.05f, 4); break;
                case "selfCollision": s.SelfCollision = value != 0; break;
                case "groundPlane": s.GroundPlane = value != 0; break;
            }
        }, "sim:" + key);
    }

    public void SetAvatar(AvatarKind kind, string? path = null, float scale = 1f)
    {
        History.Execute("Change avatar", doc =>
        {
            doc.Avatar.Kind = kind;
            doc.Avatar.Path = path;
            doc.Avatar.Scale = scale;
        });
    }

    /// <summary>Sets one of the MakeHuman body sliders ("gender", "age", "muscle", "weight", "height", "proportions").</summary>
    public void SetAvatarShape(string key, float value)
    {
        if (float.IsNaN(value))
            return;
        value = Math.Clamp(value, 0, 1);
        History.Execute("Change avatar body", doc =>
        {
            var a = doc.Avatar;
            switch (key)
            {
                case "gender": a.Gender = value; break;
                case "age": a.Age = value; break;
                case "muscle": a.Muscle = value; break;
                case "weight": a.Weight = value; break;
                case "height": a.Height = value; break;
                case "proportions": a.Proportions = value; break;
            }
        }, "avatar-shape:" + key);
    }

    public void ResetAvatarShape()
    {
        History.Execute("Reset avatar body", doc =>
        {
            var a = doc.Avatar;
            a.Gender = a.Age = a.Muscle = a.Weight = a.Height = a.Proportions = 0.5f;
        });
    }

    /// <summary>Returns the selected pieces to their flat placement, discarding the simulation result.</summary>
    public void ResetDrape(bool wholeGarment)
    {
        var ids = wholeGarment
            ? (ActiveGarment?.Pieces.Select(p => p.Id).ToHashSet() ?? new HashSet<string>())
            : SelectedPieces.ToHashSet();
        if (!Document.AllPieces().Any(p => ids.Contains(p.Id) && p.Drape != null))
            return;
        History.Execute("Reset arrangement", doc =>
        {
            foreach (var piece in doc.AllPieces().Where(p => ids.Contains(p.Id)))
                piece.ClearDrape();
        });
    }

    // ---------------------------------------------------------------- moving pieces in 3D

    private sealed record PlacementStart(Vector3 Position, Vector3 Rotation, float[]? Drape, Vector2 Centre, (Vector2 Position, Vector2? Curve)[] Points);
    private Dictionary<string, PlacementStart>? _placementStart;
    private Vector3 _placementPivot;
    private Vector3 _placementSnapOrigin;
    private bool _placementMoved;

    /// <summary>Centre of a piece in 3D: where it has been draped, or else where it is arranged.</summary>
    private static Vector3 CentreOf(PatternPiece piece)
    {
        if (piece.Drape is not { Length: >= 3 } drape)
            return piece.Placement.Position;
        var sum = Vector3.Zero;
        for (int i = 0; i + 2 < drape.Length; i += 3)
            sum += new Vector3(drape[i], drape[i + 1], drape[i + 2]);
        return sum / (drape.Length / 3);
    }

    /// <summary>Where the 3D move/rotate handles sit: the middle of the selected pieces, or null with no selection.</summary>
    public Vector3? SelectionCentre()
    {
        var pieces = SelectedPieceObjects().ToList();
        if (pieces.Count == 0)
            return null;
        return pieces.Aggregate(Vector3.Zero, (sum, p) => sum + CentreOf(p)) / pieces.Count;
    }

    /// <summary>Starts dragging the selected pieces in the 3D view.</summary>
    public bool BeginPlacementEdit(string? name = null)
    {
        if (SelectionCentre() is not { } pivot)
            return false;
        History.Begin(name ?? (SelectedPieces.Count == 1 ? "Move piece in 3D" : "Move pieces in 3D"));
        _placementPivot = pivot;
        // With snapping, a single piece lands with its own position on the grid; several pieces with their centre.
        _placementSnapOrigin = SelectedPieces.Count == 1 ? SelectedPieceObjects().First().Placement.Position : pivot;
        _placementMoved = false;
        _placementStart = SelectedPieceObjects().ToDictionary(p => p.Id, p =>
        {
            var (min, max) = Outline.Bounds(p);
            return new PlacementStart(p.Placement.Position, p.Placement.Rotation, p.Drape, (min + max) / 2,
                p.Points.Select(point => (point.Position, point.Curve)).ToArray());
        });
        return true;
    }

    /// <summary>
    /// Moves the pieces being dragged to "where they started, turned by <paramref name="rotation"/> about the
    /// selection centre, then shifted by <paramref name="translation"/>". A draped piece keeps its draped shape.
    /// With a <paramref name="snapStep"/> (metres), the selection is moved to the nearest multiple of that step
    /// along each axis it is being moved on.
    /// </summary>
    public void ApplyPlacementEdit(Vector3 translation, Quaternion rotation, float snapStep = 0)
    {
        if (_placementStart == null)
            return;
        if (snapStep > 0)
        {
            float Snap(float origin, float offset)
                => offset == 0 ? 0 : MathF.Round((origin + offset) / snapStep) * snapStep - origin;
            translation = new Vector3(Snap(_placementSnapOrigin.X, translation.X), Snap(_placementSnapOrigin.Y, translation.Y),
                Snap(_placementSnapOrigin.Z, translation.Z));
        }
        _placementMoved |= translation != Vector3.Zero || !rotation.IsIdentity;
        var turn = Matrix4x4.CreateFromQuaternion(rotation);
        foreach (var piece in Document.AllPieces())
        {
            if (!_placementStart.TryGetValue(piece.Id, out var start))
                continue;
            piece.Placement.Position = _placementPivot + Vector3.Transform(start.Position - _placementPivot, rotation) + translation;
            piece.Placement.Rotation = rotation.IsIdentity ? start.Rotation : ToEuler(EulerMatrix(start.Rotation) * turn);
            if (start.Drape != null)
            {
                var moved = new float[start.Drape.Length];
                for (int i = 0; i + 2 < moved.Length; i += 3)
                {
                    var p = new Vector3(start.Drape[i], start.Drape[i + 1], start.Drape[i + 2]);
                    p = _placementPivot + Vector3.Transform(p - _placementPivot, rotation) + translation;
                    moved[i] = p.X;
                    moved[i + 1] = p.Y;
                    moved[i + 2] = p.Z;
                }
                piece.Drape = moved;
            }
        }
        History.NotifyLiveChange();
    }

    /// <summary>
    /// Scales the pieces being dragged to <paramref name="factor"/> times the size they started at: their patterns
    /// grow or shrink, and with several pieces selected they also move apart or together about the selection
    /// centre. <paramref name="axis"/> is the world axis to scale along, or zero to scale evenly; a piece is only
    /// changed along an axis as far as it actually lies along it, so the handle that points out of a flat piece
    /// does nothing to it. A scaled piece goes back to its flat arrangement, since its mesh is no longer the same.
    /// </summary>
    public void ApplyPlacementScale(Vector3 axis, float factor)
    {
        if (_placementStart == null)
            return;
        factor = MathF.Max(factor, 0.05f);
        _placementMoved |= factor != 1;
        bool uniform = axis == Vector3.Zero;
        float Along(float share) => uniform ? factor : 1 + (factor - 1) * MathF.Abs(share);
        foreach (var piece in Document.AllPieces())
        {
            if (!_placementStart.TryGetValue(piece.Id, out var start) || start.Points.Length != piece.Points.Count)
                continue;
            var rotation = EulerMatrix(start.Rotation);
            var scale = new Vector2(Along(Vector3.Dot(axis, Vector3.TransformNormal(Vector3.UnitX, rotation))),
                Along(Vector3.Dot(axis, Vector3.TransformNormal(Vector3.UnitY, rotation))));
            for (int i = 0; i < start.Points.Length; i++)
            {
                piece.Points[i].Position = start.Centre + (start.Points[i].Position - start.Centre) * scale;
                piece.Points[i].Curve = start.Points[i].Curve is { } curve ? start.Centre + (curve - start.Centre) * scale : null;
            }
            var spread = new Vector3(Along(axis.X), Along(axis.Y), Along(axis.Z));
            piece.Placement.Position = _placementPivot + (start.Position - _placementPivot) * spread;
            piece.ClearDrape();
        }
        History.NotifyLiveChange();
    }

    public void EndPlacementEdit()
    {
        if (_placementStart == null)
            return;
        _placementStart = null;
        if (_placementMoved)
            History.Commit();
        else
            History.Cancel();
    }

    /// <summary>Rotation matrix for Euler angles in degrees, applied X then Y then Z (as <see cref="Placement"/> stores them).</summary>
    public static Matrix4x4 EulerMatrix(Vector3 degrees)
        => Matrix4x4.CreateRotationX(degrees.X * MathF.PI / 180f)
         * Matrix4x4.CreateRotationY(degrees.Y * MathF.PI / 180f)
         * Matrix4x4.CreateRotationZ(degrees.Z * MathF.PI / 180f);

    /// <summary>Inverse of <see cref="EulerMatrix"/>.</summary>
    public static Vector3 ToEuler(Matrix4x4 m)
    {
        float sinY = Math.Clamp(-m.M13, -1f, 1f);
        float x, y = MathF.Asin(sinY), z;
        if (MathF.Abs(sinY) > 0.9999f)
        {
            // Looking straight along the Y rotation: X and Z turn about the same axis, so put it all in X.
            x = MathF.Atan2(-m.M32, m.M22);
            z = 0;
        }
        else
        {
            x = MathF.Atan2(m.M23, m.M33);
            z = MathF.Atan2(m.M12, m.M11);
        }
        const float toDegrees = 180f / MathF.PI;
        return new Vector3(MathF.Round(x * toDegrees, 2), MathF.Round(y * toDegrees, 2), MathF.Round(z * toDegrees, 2));
    }

    // ---------------------------------------------------------------- materials

    private FabricMaterial? EditableMaterial(Document doc, HashSet<string> pieceIds)
    {
        var piece = doc.AllPieces().FirstOrDefault(p => pieceIds.Contains(p.Id));
        return piece == null ? null : doc.FindMaterial(piece.MaterialId);
    }

    public void SetMaterialValue(string key, float value)
    {
        var ids = SelectedPieces.ToHashSet();
        var current = EditableMaterial(Document, ids);
        if (current == null || float.IsNaN(value))
            return;
        History.Execute("Change material", doc =>
        {
            var m = EditableMaterial(doc, ids)!;
            switch (key)
            {
                case "repeat": m.Repeat = Math.Clamp(value, 0.01f, 1000); break;
                case "roughness": m.Roughness = Math.Clamp(value, 0, 1); break;
                case "metallic": m.Metallic = Math.Clamp(value, 0, 1); break;
                case "opacity": m.Opacity = Math.Clamp(value, 0, 1); break;
            }
        }, "material:" + key + ":" + current.Id);
    }

    public void SetMaterialColor(string color)
    {
        var ids = SelectedPieces.ToHashSet();
        var current = EditableMaterial(Document, ids);
        if (current == null)
            return;
        History.Execute("Change colour", doc => EditableMaterial(doc, ids)!.Color = color, "material:color:" + current.Id);
    }

    public void SetMaterialTexture(string? path)
    {
        var ids = SelectedPieces.ToHashSet();
        if (EditableMaterial(Document, ids) == null)
            return;
        History.Execute(path == null ? "Remove texture" : "Set texture", doc => EditableMaterial(doc, ids)!.Texture = path);
    }

    public void RenameMaterial(string name)
    {
        var ids = SelectedPieces.ToHashSet();
        if (EditableMaterial(Document, ids) == null || string.IsNullOrWhiteSpace(name))
            return;
        History.Execute("Rename material", doc => EditableMaterial(doc, ids)!.Name = name.Trim());
    }

    /// <summary>Gives the selected pieces their own new material (a copy of the first piece's current one).</summary>
    public void NewMaterialForSelection()
    {
        var ids = SelectedPieces.ToHashSet();
        if (ids.Count == 0)
            return;
        History.Execute("New material", doc =>
        {
            var source = EditableMaterial(doc, ids);
            var material = source?.Clone() ?? new FabricMaterial();
            material.Id = Document.NewId();
            material.Name = doc.UniqueName(source?.Name ?? "Fabric");
            doc.Materials.Add(material);
            foreach (var piece in doc.AllPieces().Where(p => ids.Contains(p.Id)))
                piece.MaterialId = material.Id;
        });
    }

    public void AssignMaterial(string materialId)
    {
        var ids = SelectedPieces.ToHashSet();
        if (ids.Count == 0 || Document.FindMaterial(materialId) == null)
            return;
        History.Execute("Assign material", doc =>
        {
            foreach (var piece in doc.AllPieces().Where(p => ids.Contains(p.Id)))
                piece.MaterialId = materialId;
        });
    }
}
