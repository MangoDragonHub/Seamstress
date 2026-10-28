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

    /// <summary>Mesh vertices selected with the 3D pin brush, by piece id; not part of the document.</summary>
    public Dictionary<string, HashSet<int>> PinSelection { get; } = new();
    private readonly Dictionary<string, string> _pinSelectionKeys = new();
    public int PinSelectionCount => PinSelection.Sum(p => p.Value.Count);

    /// <summary>The mesh fingerprint a brush selection was made on, so a rebuilt mesh does not reuse its indices.</summary>
    public string? PinSelectionKey(string pieceId) => _pinSelectionKeys.GetValueOrDefault(pieceId);

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
        PrunePins(garment, mesh);
        return mesh;
    }

    /// <summary>Pins and brush selections only make sense on the mesh they were placed on; drop them when it is rebuilt.</summary>
    private void PrunePins(Garment garment, GarmentMesh mesh)
    {
        foreach (var pieceMesh in mesh.Pieces)
        {
            if (PinSelectionKey(pieceMesh.PieceId) is { } selectionKey && selectionKey != pieceMesh.Key)
            {
                PinSelection.Remove(pieceMesh.PieceId);
                _pinSelectionKeys.Remove(pieceMesh.PieceId);
            }
            var piece = garment.FindPiece(pieceMesh.PieceId);
            if (piece == null || piece.Pins.Count == 0)
                continue;
            if (piece.PinKey != pieceMesh.Key)
                piece.ClearPins();
            else
            {
                piece.Pins.RemoveWhere(index => index < 0 || index >= pieceMesh.VertexCount);
                if (piece.Pins.Count == 0)
                    piece.PinKey = null;
            }
        }
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
            foreach (var (loop, start) in piece.Loops())
                hash.Add(loop.Count);
            foreach (var point in piece.AllPoints())
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
        PinSelection.Clear();
        _pinSelectionKeys.Clear();
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

    /// <summary>Makes a garment the one Simulate works on, without touching the selection.</summary>
    public void SetActiveGarment(string garmentId)
    {
        if (ActiveGarmentId == garmentId || Document.FindGarment(garmentId) == null)
            return;
        ActiveGarmentId = garmentId;
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

    // ---------------------------------------------------------------- pinning

    public void ClearPinSelection()
    {
        if (PinSelection.Count == 0 && _pinSelectionKeys.Count == 0)
            return;
        PinSelection.Clear();
        _pinSelectionKeys.Clear();
        SelectionChanged?.Invoke();
    }

    private string? CurrentMeshKey(string pieceId)
    {
        var garment = Document.GarmentOfPiece(pieceId);
        return garment == null ? null : MeshFor(garment).Find(pieceId)?.Key;
    }

    public void TogglePinVertex(string pieceId, int vertex)
    {
        if (vertex < 0 || Document.FindPiece(pieceId) == null || CurrentMeshKey(pieceId) is not { } key)
            return;
        if (!PinSelection.TryGetValue(pieceId, out var vertices))
            PinSelection[pieceId] = vertices = new HashSet<int>();
        _pinSelectionKeys[pieceId] = key;
        if (!vertices.Remove(vertex))
            vertices.Add(vertex);
        if (vertices.Count == 0)
        {
            PinSelection.Remove(pieceId);
            _pinSelectionKeys.Remove(pieceId);
        }
        SelectionChanged?.Invoke();
    }

    /// <summary>Adds (or replaces) the brush selection on one piece with mesh vertex indices.</summary>
    public void SelectPinVertices(string pieceId, IEnumerable<int> vertices, bool additive)
    {
        if (Document.FindPiece(pieceId) == null || CurrentMeshKey(pieceId) is not { } key)
            return;
        if (!additive)
        {
            PinSelection.Clear();
            _pinSelectionKeys.Clear();
        }
        if (!PinSelection.TryGetValue(pieceId, out var selected))
            PinSelection[pieceId] = selected = new HashSet<int>();
        _pinSelectionKeys[pieceId] = key;
        foreach (int vertex in vertices)
        {
            if (vertex >= 0)
                selected.Add(vertex);
        }
        if (selected.Count == 0)
        {
            PinSelection.Remove(pieceId);
            _pinSelectionKeys.Remove(pieceId);
        }
        SelectionChanged?.Invoke();
    }

    /// <summary>Relates every selected mesh vertex to the current mesh of its piece.</summary>
    private List<(string PieceId, string Key, int[] Vertices)> PinTargets()
    {
        var targets = new List<(string PieceId, string Key, int[] Vertices)>();
        foreach (var (pieceId, selection) in PinSelection.ToArray())
        {
            var garment = Document.GarmentOfPiece(pieceId);
            var mesh = garment == null ? null : MeshFor(garment).Find(pieceId);
            if (mesh == null || PinSelectionKey(pieceId) != mesh.Key)
            {
                PinSelection.Remove(pieceId);
                _pinSelectionKeys.Remove(pieceId);
                continue;
            }
            var vertices = selection.Where(index => index >= 0 && index < mesh.VertexCount).Distinct().OrderBy(i => i).ToArray();
            if (vertices.Length > 0)
                targets.Add((pieceId, mesh.Key, vertices));
        }
        return targets;
    }

    /// <summary>Pins every vertex chosen with the brush. Returns how many were pinned.</summary>
    public int PinSelectedVertices()
    {
        var targets = PinTargets();
        if (targets.Count == 0)
        {
            ClearPinSelection();
            return 0;
        }
        int count = targets.Sum(t => t.Vertices.Length);
        History.Execute(count == 1 ? Lang.T("Pin vertex") : Lang.F("Pin {0} vertices", count), doc =>
        {
            foreach (var (pieceId, key, vertices) in targets)
            {
                var piece = doc.FindPiece(pieceId);
                if (piece == null)
                    continue;
                if (piece.PinKey != key)
                    piece.ClearPins();
                piece.PinKey = key;
                foreach (int vertex in vertices)
                    piece.Pins.Add(vertex);
            }
        });
        PinSelection.Clear();
        SelectionChanged?.Invoke();
        return count;
    }

    /// <summary>Unpins every vertex chosen with the brush. Returns how many were unpinned.</summary>
    public int UnpinSelectedVertices()
    {
        var targets = PinTargets();
        int count = targets.Sum(t =>
        {
            var piece = Document.FindPiece(t.PieceId);
            return piece != null && piece.PinKey == t.Key ? t.Vertices.Count(piece.Pins.Contains) : 0;
        });
        if (count == 0)
        {
            ClearPinSelection();
            return 0;
        }
        History.Execute(count == 1 ? Lang.T("Unpin vertex") : Lang.F("Unpin {0} vertices", count), doc =>
        {
            foreach (var (pieceId, key, vertices) in targets)
            {
                var piece = doc.FindPiece(pieceId);
                if (piece == null)
                    continue;
                if (piece.PinKey != key)
                {
                    piece.ClearPins();
                    continue;
                }
                foreach (int vertex in vertices)
                    piece.Pins.Remove(vertex);
                if (piece.Pins.Count == 0)
                    piece.PinKey = null;
            }
        });
        PinSelection.Clear();
        SelectionChanged?.Invoke();
        return count;
    }

    /// <summary>Garments that Unpin All acts on: those with selected pieces, else the brushed ones, else the active one.</summary>
    public List<Garment> PinTargetGarments()
    {
        var targetIds = SelectedPieceObjects()
            .Select(p => Document.GarmentOfPiece(p.Id)?.Id)
            .OfType<string>()
            .ToHashSet();
        if (targetIds.Count == 0)
        {
            foreach (string pieceId in PinSelection.Keys)
            {
                if (Document.GarmentOfPiece(pieceId) is { } garment)
                    targetIds.Add(garment.Id);
            }
        }
        if (targetIds.Count == 0 && ActiveGarment is { } active)
            targetIds.Add(active.Id);
        return Document.Garments.Where(g => targetIds.Contains(g.Id)).ToList();
    }

    public int PinnedVertexCount(IEnumerable<Garment> garments)
        => garments.Sum(g => g.Pieces.Sum(p => p.Pins.Count));

    /// <summary>Removes every pin from the garments Unpin All acts on. Returns how many were removed.</summary>
    public int UnpinAll()
    {
        var garments = PinTargetGarments();
        var ids = garments.Select(g => g.Id).ToHashSet();
        int count = PinnedVertexCount(garments);
        if (count == 0)
            return 0;
        History.Execute(count == 1 ? Lang.T("Unpin vertex") : Lang.F("Unpin {0} vertices", count), doc =>
        {
            foreach (var garment in doc.Garments.Where(g => ids.Contains(g.Id)))
            {
                foreach (var piece in garment.Pieces)
                    piece.ClearPins();
            }
        });
        foreach (string pieceId in PinSelection.Keys.Where(id => Document.GarmentOfPiece(id) is { } g && ids.Contains(g.Id)).ToList())
        {
            PinSelection.Remove(pieceId);
            _pinSelectionKeys.Remove(pieceId);
        }
        SelectionChanged?.Invoke();
        return count;
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
            if (piece == null || point.Index >= piece.PointCount)
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
        foreach (string id in PinSelection.Keys.Where(id => Document.FindPiece(id) == null).ToList())
        {
            PinSelection.Remove(id);
            _pinSelectionKeys.Remove(id);
            changed = true;
        }
        if (changed)
            SelectionChanged?.Invoke();
    }

    // ---------------------------------------------------------------- garments and pieces

    public Garment AddGarment(string? name = null)
    {
        Garment? added = null;
        History.Execute(Lang.T("Add garment"), doc =>
        {
            added = new Garment { Name = doc.UniqueName(name ?? Lang.T("Garment")) };
            doc.Garments.Add(added);
        });
        SelectLayer(added!.Id);
        return added;
    }

    /// <param name="bodyHeight">Height of the current avatar in metres, so the pieces start at the right level.</param>
    public Garment AddTemplate(string template, float bodyHeight = Templates.ReferenceBodyHeight)
    {
        Garment? added = null;
        History.Execute(Lang.F("Add {0}", Lang.T(template)), doc =>
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
        History.Execute(baseName switch { "Rectangle" => Lang.T("Add rectangle"), "Ellipse" => Lang.T("Add ellipse"), _ => Lang.T("Add piece") }, doc =>
        {
            var garment = doc.FindGarment(ActiveGarmentId);
            if (garment == null)
            {
                garment = new Garment { Name = doc.UniqueName(Lang.T("Garment")) };
                doc.Garments.Add(garment);
                ActiveGarmentId = garment.Id;
            }
            var min = outline.Aggregate(new Vector2(float.MaxValue), Vector2.Min);
            piece = new PatternPiece { Name = doc.UniqueName(Lang.T(baseName)), Origin = min };
            foreach (var p in outline)
                piece.Points.Add(new PatternPoint { Position = p - min });
            Outline.EnsureCounterClockwise(piece);
            var existing = garment.Pieces.FirstOrDefault();
            piece.MaterialId = existing?.MaterialId;
            if (existing != null)
                piece.Physics = existing.Physics.Clone();
            if (piece.MaterialId == null)
            {
                var material = new FabricMaterial { Name = doc.UniqueName(Lang.T("Fabric")) };
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
            History.Execute(Lang.T("Delete seam"), doc =>
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
        History.Execute(ids.Count == 1 ? Lang.T("Delete piece") : Lang.T("Delete pieces"), doc =>
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
            History.Execute(Lang.T("Delete garment"), doc => doc.Garments.RemoveAll(g => g.Id == id));
            return;
        }
        if (Document.FindPiece(id) != null)
        {
            History.Execute(Lang.T("Delete piece"), doc =>
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
        History.Execute(Lang.T("Rename"), doc =>
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
        History.Execute(visible ? Lang.T("Show") : Lang.T("Hide"), doc =>
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
        History.Execute(Lang.T("Add group"), doc =>
        {
            var garment = doc.FindGarment(target);
            if (garment == null)
                return;
            group = new LayerGroup { Name = doc.UniqueName(Lang.T("Group")) };
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
        History.Execute(Lang.T("Group pieces"), doc =>
        {
            var garment = doc.FindGarment(ownerId)!;
            var members = garment.Pieces.Where(p => ids.Contains(p.Id)).ToList();
            // If everything selected already shares a parent group, nest the new group inside it.
            var parents = members.Select(p => p.GroupId).Distinct().ToList();
            group = new LayerGroup { Name = doc.UniqueName(Lang.T("Group")), ParentId = parents.Count == 1 ? parents[0] : null };
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
        History.Execute(Lang.T("Ungroup"), doc =>
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
    public void MoveToGroup(string id, string? groupId) => MoveLayers(new[] { id }, null, groupId);

    /// <summary>Moves one piece or group; see <see cref="MoveLayers"/>.</summary>
    public void MoveLayer(string id, string? garmentId, string? groupId, string? beforeId = null)
        => MoveLayers(new[] { id }, garmentId, groupId, beforeId);

    /// <summary>
    /// The layers that go along when <paramref name="id"/> is dragged or moved from its menu: every selected piece
    /// when it is one of several, otherwise just itself. <paramref name="id"/> comes first.
    /// </summary>
    public List<string> LayersToMove(string id)
        => SelectedLayerId == null && SelectedPieces.Count > 1 && SelectedPieces.Contains(id)
            ? SelectedPieces.Prepend(id).Distinct().ToList()
            : new List<string> { id };

    /// <summary>
    /// Moves pieces and groups, from any garments, into a group or to the top level of a garment (the first layer's
    /// own garment when neither is given), as one undo step. They are placed just before <paramref name="beforeId"/>
    /// when that is one of the destination's children, otherwise after them all, in the order the layers panel showed
    /// them. A group brings its pieces and sub-groups along. Seams between pieces that move together come along; seams
    /// to pieces left behind are dropped. The destination garment becomes the active one.
    /// </summary>
    public void MoveLayers(IReadOnlyList<string> ids, string? garmentId, string? groupId, string? beforeId = null)
    {
        var doc = Document;
        Garment? OwnerOf(string id) => doc.Garments.FirstOrDefault(g => g.FindPiece(id) != null || g.FindGroup(id) != null);
        var requested = ids.Where(id => OwnerOf(id) != null).ToHashSet();
        if (requested.Count == 0)
            return;

        // The group's owner wins over the garment id, so callers only need to pass the group they dropped onto.
        var target = groupId != null
            ? doc.Garments.FirstOrDefault(g => g.FindGroup(groupId) != null)
            : garmentId != null ? doc.FindGarment(garmentId) : OwnerOf(ids.First(requested.Contains));
        if (target == null)
            return;
        if (groupId != null && requested.Any(id => target.FindGroup(id) != null && target.IsGroupWithin(groupId, id)))
            return; // a group cannot go inside itself

        // Layers inside a group that moves too go along with it rather than on their own.
        var moving = new List<string>();
        foreach (var garment in doc.Garments)
        {
            foreach (string id in garment.LayerTree())
            {
                if (requested.Contains(id) && ParentOfLayer(garment, id, out var parent)
                    && !requested.Any(other => other != id && garment.FindGroup(other) != null && garment.IsGroupWithin(parent, other)))
                    moving.Add(id);
            }
        }
        var movingSet = moving.ToHashSet();

        // Only a sibling in the destination can mark the spot. When it is moving itself, the spot is the next sibling
        // that stays, or the end.
        var siblings = target.OrderedLayerIds().Where(l => ParentOfLayer(target, l, out var p) && p == groupId).ToList();
        int at = beforeId != null ? siblings.IndexOf(beforeId) : -1;
        beforeId = at < 0 ? null : siblings.Skip(at).FirstOrDefault(l => !movingSet.Contains(l));

        // Nothing to do when the layers already sit together right there.
        if (moving.All(siblings.Contains))
        {
            var result = siblings.Where(l => !movingSet.Contains(l)).ToList();
            result.InsertRange(beforeId != null ? result.IndexOf(beforeId) : result.Count, moving);
            if (result.SequenceEqual(siblings))
                return;
        }

        string targetId = target.Id;
        History.Execute(Lang.T("Move layer"), d =>
        {
            var to = d.FindGarment(targetId)!;
            var orders = d.Garments.ToDictionary(g => g.Id, g => g.OrderedLayerIds());
            var touched = new HashSet<Garment> { to };
            var movedPieces = new HashSet<string>();
            var changedGarment = new HashSet<string>();
            foreach (string id in moving)
            {
                var from = d.Garments.FirstOrDefault(g => g.FindPiece(id) != null || g.FindGroup(id) != null);
                if (from == null)
                    continue;
                touched.Add(from);
                if (from.FindPiece(id) is { } piece)
                {
                    if (from != to)
                    {
                        from.Pieces.Remove(piece);
                        to.Pieces.Add(piece);
                        movedPieces.Add(piece.Id);
                        changedGarment.Add(piece.Id);
                    }
                    piece.GroupId = groupId;
                    continue;
                }
                if (from != to)
                {
                    foreach (var inner in from.Pieces.Where(p => p.GroupId != null && from.IsGroupWithin(p.GroupId, id)).ToList())
                    {
                        from.Pieces.Remove(inner);
                        to.Pieces.Add(inner);
                        movedPieces.Add(inner.Id);
                        changedGarment.Add(inner.Id);
                    }
                    var groups = from.Groups.Where(g => from.IsGroupWithin(g.Id, id)).ToList();
                    foreach (var moved in groups)
                        from.Groups.Remove(moved);
                    foreach (var moved in groups)
                    {
                        to.Groups.Add(moved);
                        changedGarment.Add(moved.Id);
                    }
                }
                if (to.FindGroup(id) is { } group)
                    group.ParentId = groupId;
            }

            foreach (var from in touched.Where(g => g != to))
            {
                var seams = from.Seams.Where(s => movedPieces.Contains(s.A.PieceId) && movedPieces.Contains(s.B.PieceId)).ToList();
                from.Seams.RemoveAll(s => movedPieces.Contains(s.A.PieceId) || movedPieces.Contains(s.B.PieceId));
                to.Seams.AddRange(seams);
            }

            // Place the layers together before beforeId (or last); what came along with a group keeps its order inside it.
            var order = orders[to.Id].Where(l => !movingSet.Contains(l)).ToList();
            int index = beforeId != null ? order.IndexOf(beforeId) : -1;
            order.InsertRange(index < 0 ? order.Count : index, moving);
            order.AddRange(d.Garments.Where(g => g != to).SelectMany(g => orders[g.Id])
                .Where(l => changedGarment.Contains(l) && !movingSet.Contains(l)));
            orders[to.Id] = order;
            foreach (var g in touched)
                g.LayerOrder = orders[g.Id].Where(l => g.FindGroup(l) != null || g.FindPiece(l) != null).ToList();

            // Make the result visible: open the destination garment and the group that received the layers.
            to.Expanded = true;
            if (groupId != null && to.FindGroup(groupId) is { } parent)
                parent.Expanded = true;
            ActiveGarmentId = to.Id;
        });
    }

    /// <summary>The group a piece or group sits in (null at the top level); false when it is not in the garment.</summary>
    private static bool ParentOfLayer(Garment garment, string id, out string? parentId)
    {
        if (garment.FindPiece(id) is { } piece)
        {
            parentId = piece.GroupId;
            return true;
        }
        parentId = garment.FindGroup(id)?.ParentId;
        return garment.FindGroup(id) != null;
    }

    // ---------------------------------------------------------------- piece editing

    public void DuplicateSelection()
    {
        var ids = SelectedPieces.ToList();
        if (ids.Count == 0)
            return;
        var created = new List<string>();
        History.Execute(Lang.T("Duplicate"), doc =>
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
        History.Execute(ids.Count == 1 ? Lang.T("Invert normals") : Lang.F("Invert normals of {0} pieces", ids.Count), doc =>
        {
            foreach (var piece in doc.AllPieces().Where(p => ids.Contains(p.Id)))
                piece.FlipNormals = !piece.FlipNormals;
        });
    }

    /// <summary>
    /// What Cut Hole would do with the current selection: the piece to cut and the pieces whose shapes the holes take,
    /// or why it cannot. The piece to cut is the one the others all lie inside.
    /// </summary>
    public (PatternPiece Target, List<PatternPiece> Cutters)? PlanCutHole(out string? problem)
    {
        var selected = SelectedPieceObjects().Where(p => p.Points.Count >= 3).ToList();
        problem = null;
        if (selected.Count < 2)
        {
            problem = Lang.T("Select two overlapping pieces to cut a hole: the piece to cut, and a piece inside it in the shape of the hole.");
            return null;
        }
        // Outlines in the target's own coordinates.
        static List<Vector2> Shape(PatternPiece piece, PatternPiece target)
            => Outline.Flatten(piece).Select(p => p + piece.Origin - target.Origin).ToList();
        foreach (var target in selected)
        {
            var cutters = selected.Where(p => p != target).ToList();
            var outer = Outline.Flatten(target);
            var holes = Outline.FlattenHoles(target);
            bool Fits(List<Vector2> shape)
                => shape.All(p => Outline.Contains(outer, p) && !holes.Any(h => Outline.Contains(h, p)))
                   && !Outline.Crosses(shape, outer)
                   && !holes.Any(h => Outline.Crosses(shape, h) || Outline.Contains(shape, h[0]));
            var shapes = cutters.Select(c => Shape(c, target)).ToList();
            if (!shapes.All(Fits))
                continue;
            for (int i = 0; i < shapes.Count; i++)
            {
                for (int j = i + 1; j < shapes.Count; j++)
                {
                    if (Outline.Crosses(shapes[i], shapes[j]) || Outline.Contains(shapes[i], shapes[j][0]) || Outline.Contains(shapes[j], shapes[i][0]))
                    {
                        problem = Lang.T("The pieces that cut holes must not overlap each other.");
                        return null;
                    }
                }
            }
            return (target, cutters);
        }
        problem = Lang.T("To cut a hole, one selected piece must lie entirely inside the other, clear of its edges and of any hole it already has.");
        return null;
    }

    /// <summary>
    /// Cuts a hole in the selected piece that the other selected pieces lie inside, in the shape of each of them, as
    /// one undo step. What is cut out becomes a new piece of the same fabric: laid out beside the cut piece in 2D,
    /// and in 3D placed just behind the hole (draped like it, when the cut piece is draped), ready to be sewn in as a
    /// pocket or facing. The pieces that gave the holes their shapes stay as they are. Returns the new pieces, which
    /// become the selection; nothing is cut when <see cref="PlanCutHole"/> finds a problem.
    /// </summary>
    public List<PatternPiece> CutHole()
    {
        var created = new List<PatternPiece>();
        if (PlanCutHole(out _) is not { } plan)
            return created;
        var (plannedTarget, plannedCutters) = plan;
        string targetId = plannedTarget.Id;
        var cutterIds = plannedCutters.Select(c => c.Id).ToList();
        var garmentBefore = Document.GarmentOfPiece(targetId)!;
        // The draped shape before the cut, to drape what is left of the piece and the cut-outs the same way.
        var oldMesh = MeshFor(garmentBefore).Find(targetId);
        bool draped = oldMesh != null && plannedTarget.Drape != null && plannedTarget.DrapeKey == oldMesh.Key;
        var oldPositions = draped ? Mesher.CurrentPositions(plannedTarget, oldMesh!) : null;

        History.Execute(cutterIds.Count == 1 ? Lang.T("Cut hole") : Lang.F("Cut {0} holes", cutterIds.Count), doc =>
        {
            var garment = doc.GarmentOfPiece(targetId)!;
            var target = garment.FindPiece(targetId)!;
            var offsets = new Dictionary<string, Vector2>();
            foreach (var cutterId in cutterIds)
            {
                var cutter = doc.FindPiece(cutterId)!;
                var shift = cutter.Origin - target.Origin;
                var hole = Outline.Reversed(cutter.Points.Select(p => new PatternPoint
                {
                    Position = p.Position + shift,
                    Curve = p.Curve + shift,
                }).ToList());
                if (Outline.SignedArea(hole.Select(p => p.Position).ToList()) > 0)
                    hole = Outline.Reversed(hole);
                target.Holes.Add(hole);

                var piece = new PatternPiece
                {
                    Name = doc.UniqueName(Lang.F("{0} cut-out", target.Name)),
                    GroupId = target.GroupId,
                    Points = cutter.Points.Select(p => p.Clone()).ToList(),
                    Physics = target.Physics.Clone(),
                    MaterialId = target.MaterialId,
                    ParticleDistance = target.ParticleDistance,
                    FlipNormals = target.FlipNormals,
                };
                Outline.EnsureCounterClockwise(piece);
                // Beside the cut piece in the pattern, level with its hole, in the first gap to the right that is
                // wide enough.
                var (cutMin, cutMax) = Outline.Bounds(piece);
                var size = cutMax - cutMin;
                float x = target.Origin.X + Outline.Bounds(target).Max.X + 8, y = cutter.Origin.Y + cutMin.Y;
                for (bool moved = true; moved;)
                {
                    moved = false;
                    foreach (var other in doc.AllPieces().Where(p => p.Points.Count >= 3))
                    {
                        var (lo, hi) = Outline.Bounds(other);
                        lo += other.Origin;
                        hi += other.Origin;
                        if (x < hi.X + 4 && x + size.X > lo.X - 4 && y < hi.Y + 4 && y + size.Y > lo.Y - 4)
                        {
                            x = hi.X + 8;
                            moved = true;
                        }
                    }
                }
                piece.Origin = new Vector2(x - cutMin.X, cutter.Origin.Y);
                offsets[piece.Id] = shift;

                // In 3D, where the hole is, a centimetre behind the cloth and following its wrap.
                var (targetMin, targetMax) = Outline.Bounds(target);
                var (position, rotation) = Mesher.ArrangedFrame(target, (cutMin + cutMax) / 2 + shift);
                piece.Placement = new Placement
                {
                    Position = position - Vector3.TransformNormal(Vector3.UnitZ, rotation) * 0.01f,
                    Rotation = ToEuler(rotation),
                    Curvature = target.Placement.Curvature * (cutMax.X - cutMin.X) / MathF.Max(targetMax.X - targetMin.X, 1e-3f),
                };
                garment.Pieces.Insert(garment.Pieces.IndexOf(target) + 1 + created.Count, piece);
                created.Add(piece);
            }

            if (oldMesh == null || oldPositions == null)
                return;
            var mesh = Mesher.Build(garment);
            var normals = Mesher.ComputeNormals(oldPositions, oldMesh.Triangles);
            var sampler = new SurfaceSampler(oldMesh, oldPositions, normals);
            void Drape(PatternPiece piece, Vector2 shift, float behind)
            {
                if (mesh.Find(piece.Id) is not { } pieceMesh)
                    return;
                var drape = new float[pieceMesh.VertexCount * 3];
                for (int i = 0; i < pieceMesh.VertexCount; i++)
                {
                    var (p, n) = sampler.At(pieceMesh.Points[i] + shift);
                    p -= n * behind;
                    drape[3 * i] = p.X;
                    drape[3 * i + 1] = p.Y;
                    drape[3 * i + 2] = p.Z;
                }
                piece.Drape = drape;
                piece.DrapeKey = pieceMesh.Key;
            }
            Drape(target, Vector2.Zero, 0);
            foreach (var piece in created)
                Drape(piece, offsets[piece.Id], 0.01f);
        });
        SelectedPieces.Clear();
        SelectedPoint = null;
        SelectedSeamId = null;
        foreach (var piece in created)
            SelectedPieces.Add(piece.Id);
        SelectionChanged?.Invoke();
        return created;
    }

    /// <summary>
    /// Reads positions off a piece's simulated surface by pattern position: the point of the surface that was at a
    /// given place in the flat pattern, and the surface normal there.
    /// </summary>
    private sealed class SurfaceSampler
    {
        private readonly PieceMesh _mesh;
        private readonly Vector3[] _positions;
        private readonly Vector3[] _normals;
        private readonly Dictionary<(int, int), List<int>> _cells = new();
        private readonly Vector2 _min;
        private readonly float _cell;

        public SurfaceSampler(PieceMesh mesh, Vector3[] positions, Vector3[] normals)
        {
            _mesh = mesh;
            _positions = positions;
            _normals = normals;
            var min = new Vector2(float.MaxValue);
            var max = new Vector2(float.MinValue);
            foreach (var p in mesh.Points)
            {
                min = Vector2.Min(min, p);
                max = Vector2.Max(max, p);
            }
            _min = min;
            _cell = MathF.Max(MathF.Max(max.X - min.X, max.Y - min.Y) / MathF.Max(1, MathF.Sqrt(mesh.TriangleCount)), 1e-3f);
            for (int t = 0; t < mesh.TriangleCount; t++)
            {
                var a = mesh.Points[mesh.Triangles[3 * t]];
                var b = mesh.Points[mesh.Triangles[3 * t + 1]];
                var c = mesh.Points[mesh.Triangles[3 * t + 2]];
                var (lo, hi) = (Cell(Vector2.Min(a, Vector2.Min(b, c))), Cell(Vector2.Max(a, Vector2.Max(b, c))));
                for (int x = lo.X; x <= hi.X; x++)
                {
                    for (int y = lo.Y; y <= hi.Y; y++)
                    {
                        if (!_cells.TryGetValue((x, y), out var list))
                            _cells[(x, y)] = list = new List<int>();
                        list.Add(t);
                    }
                }
            }
        }

        private (int X, int Y) Cell(Vector2 p) => ((int)MathF.Floor((p.X - _min.X) / _cell), (int)MathF.Floor((p.Y - _min.Y) / _cell));

        public (Vector3 Position, Vector3 Normal) At(Vector2 at)
        {
            // The triangle the point is in, or the nearest one around it for a point just off the old outline.
            var (cx, cy) = Cell(at);
            int best = -1;
            float bestScore = float.MinValue;
            Vector3 bestWeights = default;
            for (int ring = 0; ring <= 2 && bestScore < -1e-4f; ring++)
            {
                for (int x = cx - ring; x <= cx + ring; x++)
                {
                    for (int y = cy - ring; y <= cy + ring; y++)
                    {
                        if (!_cells.TryGetValue((x, y), out var list))
                            continue;
                        foreach (int t in list)
                        {
                            var w = Weights(t, at);
                            float score = MathF.Min(w.X, MathF.Min(w.Y, w.Z));
                            if (score > bestScore)
                                (best, bestScore, bestWeights) = (t, score, w);
                        }
                    }
                }
            }
            if (best < 0)
            {
                for (int t = 0; t < _mesh.TriangleCount; t++)
                {
                    var w = Weights(t, at);
                    float score = MathF.Min(w.X, MathF.Min(w.Y, w.Z));
                    if (score > bestScore)
                        (best, bestScore, bestWeights) = (t, score, w);
                }
            }
            if (best < 0)
                return (Vector3.Zero, Vector3.UnitZ);
            // Off the surface, use the nearest point of it.
            var weights = Vector3.Max(bestWeights, Vector3.Zero);
            weights /= MathF.Max(weights.X + weights.Y + weights.Z, 1e-9f);
            int i0 = _mesh.Triangles[3 * best], i1 = _mesh.Triangles[3 * best + 1], i2 = _mesh.Triangles[3 * best + 2];
            var position = _positions[i0] * weights.X + _positions[i1] * weights.Y + _positions[i2] * weights.Z;
            var normal = _normals[i0] * weights.X + _normals[i1] * weights.Y + _normals[i2] * weights.Z;
            return (position, normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitZ);
        }

        private Vector3 Weights(int t, Vector2 p)
        {
            var a = _mesh.Points[_mesh.Triangles[3 * t]];
            var b = _mesh.Points[_mesh.Triangles[3 * t + 1]];
            var c = _mesh.Points[_mesh.Triangles[3 * t + 2]];
            float area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            if (MathF.Abs(area) < 1e-12f)
                return new Vector3(-1e9f);
            float wb = ((p.X - a.X) * (c.Y - a.Y) - (p.Y - a.Y) * (c.X - a.X)) / area;
            float wc = ((b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X)) / area;
            return new Vector3(1 - wb - wc, wb, wc);
        }
    }

    public void MirrorSelection()
    {
        var ids = SelectedPieces.ToHashSet();
        if (ids.Count == 0)
            return;
        History.Execute(Lang.T("Mirror"), doc =>
        {
            foreach (var garment in doc.Garments)
            {
                foreach (var piece in garment.Pieces.Where(p => ids.Contains(p.Id)))
                {
                    var (min, max) = Outline.Bounds(piece);
                    float axis = min.X + max.X;
                    var loops = piece.Loops().Select(l => (l.Start, l.Loop.Count)).ToList();
                    // Mirroring flips the winding, so walk the outline and each hole backwards to keep their directions.
                    List<PatternPoint> Mirror(List<PatternPoint> loop) => Outline.Reversed(loop).Select(p => new PatternPoint
                    {
                        Position = new Vector2(axis - p.Position.X, p.Position.Y),
                        Curve = p.Curve is { } c ? new Vector2(axis - c.X, c.Y) : null,
                    }).ToList();
                    piece.Points = Mirror(piece.Points);
                    piece.Holes = piece.Holes.Select(Mirror).ToList();
                    // Edge k of a loop of n (from its point k) became its edge n-1-k and now runs the other way, so a
                    // seam to an edge that was not mirrored has to swap which ends it pairs up.
                    int Mirrored(int edge)
                    {
                        var (start, count) = loops.First(l => edge >= l.Start && edge < l.Start + l.Count);
                        return start + count - 1 - (edge - start);
                    }
                    foreach (var seam in garment.Seams)
                    {
                        bool a = seam.A.PieceId == piece.Id, b = seam.B.PieceId == piece.Id;
                        if (a)
                            seam.A.Edge = Mirrored(seam.A.Edge);
                        if (b)
                            seam.B.Edge = Mirrored(seam.B.Edge);
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
        History.Execute(Lang.T("Rotate"), doc =>
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
                foreach (var point in piece.AllPoints())
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
        History.Execute(Lang.T("Add point"), doc =>
        {
            var garment = doc.GarmentOfPiece(pieceId);
            var piece = garment?.FindPiece(pieceId);
            if (garment == null || piece == null || edge < 0 || edge >= piece.EdgeCount)
                return;
            var (loop, start) = piece.LoopOf(edge);
            loop[edge - start].Curve = null;
            loop.Insert(edge - start + 1, new PatternPoint { Position = localPosition });
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

    /// <summary>
    /// Removes a point; the edges before and after it merge into one. The outline keeps at least three points, and a
    /// hole that would be left with fewer goes altogether.
    /// </summary>
    public void DeletePoint(string pieceId, int index)
    {
        var target = Document.FindPiece(pieceId);
        if (target == null || index < 0 || index >= target.PointCount || (!target.InHole(index) && target.Points.Count <= 3))
            return;
        History.Execute(target.InHole(index) && target.LoopOf(index).Loop.Count <= 3 ? Lang.T("Delete hole") : Lang.T("Delete point"), doc =>
        {
            var garment = doc.GarmentOfPiece(pieceId)!;
            RemovePoints(garment, garment.FindPiece(pieceId)!, new[] { index });
        });
        SelectedPoint = null;
        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// Removes several points as one undo step. The outline of every piece keeps at least three points; a hole goes
    /// altogether when all its points are chosen, or when fewer than three would be left.
    /// </summary>
    public void DeletePoints(IReadOnlyCollection<(string PieceId, int Index)> points)
    {
        if (points.Count == 0)
            return;
        History.Execute(points.Count == 1 ? Lang.T("Delete point") : Lang.T("Delete points"), doc =>
        {
            foreach (var group in points.GroupBy(p => p.PieceId))
            {
                var garment = doc.GarmentOfPiece(group.Key);
                if (garment?.FindPiece(group.Key) is { } piece)
                    RemovePoints(garment, piece, group.Select(p => p.Index).ToList());
            }
        });
        SelectedPoint = null;
        SelectionChanged?.Invoke();
    }

    /// <summary>Removes points of one piece and the seams on the edges that go with them, renumbering the rest.</summary>
    private static void RemovePoints(Garment garment, PatternPiece piece, IReadOnlyCollection<int> indices)
    {
        var chosen = indices.Where(i => i >= 0 && i < piece.PointCount).ToHashSet();
        // Each removal is a run of point numbers; going from the highest down keeps the numbers still to do valid.
        var removals = new List<(int Start, int Count, int Hole)>();
        int holeIndex = -1;
        foreach (var (loop, start) in piece.Loops())
        {
            var mine = Enumerable.Range(start, loop.Count).Where(chosen.Contains).ToList();
            if (holeIndex >= 0 && mine.Count > 0 && loop.Count - mine.Count < 3)
                removals.Add((start, loop.Count, holeIndex));
            else
                removals.AddRange(mine.OrderByDescending(i => i).Take(Math.Max(0, loop.Count - 3)).Select(i => (i, 1, -1)));
            holeIndex++;
        }
        foreach (var (start, count, hole) in removals.OrderByDescending(r => r.Start))
        {
            if (hole >= 0)
            {
                piece.Holes.RemoveAt(hole);
            }
            else
            {
                // The edges before and after the point merge into one straight edge.
                piece.PointAt(piece.PreviousPoint(start)).Curve = null;
                var (loop, first) = piece.LoopOf(start);
                loop.RemoveAt(start - first);
            }
            bool Gone(EdgeRef e) => e.PieceId == piece.Id && e.Edge >= start && e.Edge < start + count;
            garment.Seams.RemoveAll(s => Gone(s.A) || Gone(s.B));
            foreach (var seam in garment.Seams)
            {
                if (seam.A.PieceId == piece.Id && seam.A.Edge >= start + count)
                    seam.A.Edge -= count;
                if (seam.B.PieceId == piece.Id && seam.B.Edge >= start + count)
                    seam.B.Edge -= count;
            }
        }
    }

    public void StraightenEdge(string pieceId, int edge)
    {
        History.Execute(Lang.T("Straighten edge"), doc =>
        {
            var piece = doc.FindPiece(pieceId);
            if (piece != null && edge >= 0 && edge < piece.EdgeCount)
                piece.PointAt(edge).Curve = null;
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
        History.Execute(Lang.T("Sew edges"), doc =>
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
        History.Execute(Lang.T("Reverse seam"), doc =>
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
        History.Execute(Describe(key), doc =>
        {
            foreach (var piece in doc.AllPieces().Where(p => ids.Contains(p.Id)))
                ApplyPieceValue(piece, key, value);
        }, "piece:" + key + ":" + string.Join(",", ids.OrderBy(i => i)));
    }

    /// <summary>The undo name for changing a piece value.</summary>
    private static string Describe(string key) => key switch
    {
        "posX" or "posY" or "posZ" => Lang.T("Change position"),
        "rotX" or "rotY" or "rotZ" => Lang.T("Change rotation"),
        "curvature" => Lang.T("Change wrap"),
        "particleDistance" => Lang.T("Change particle distance"),
        "uvScale" or "uvRotation" or "uvOffsetX" or "uvOffsetY" => Lang.T("Change UV"),
        "density" => Lang.T("Change weight"),
        "stretch" => Lang.T("Change stretch"),
        "shear" => Lang.T("Change shear"),
        "bend" => Lang.T("Change bend"),
        "damping" => Lang.T("Change damping"),
        "friction" => Lang.T("Change friction"),
        "thickness" => Lang.T("Change thickness"),
        _ => Lang.T("Change piece"),
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
        History.Execute(Lang.T("Apply fabric preset"), doc =>
        {
            foreach (var piece in doc.AllPieces().Where(p => ids.Contains(p.Id)))
                piece.Physics = FabricPhysics.FromPreset(preset);
        });
    }

    public void SetGarmentParticleDistance(string garmentId, float value)
    {
        if (float.IsNaN(value))
            return;
        History.Execute(Lang.T("Change particle distance"), doc =>
        {
            if (doc.FindGarment(garmentId) is { } garment)
                garment.ParticleDistance = Math.Clamp(value, 0.3f, 20f);
        }, "garment-pd:" + garmentId);
    }

    /// <summary>Turns the garment-wide thickness override on (seeded from its pieces) or off.</summary>
    public void SetGarmentThicknessEnabled(string garmentId, bool enabled)
    {
        if (Document.FindGarment(garmentId) is not { } garment || (garment.Thickness != null) == enabled)
            return;
        float thickness = garment.Pieces.Count == 0
            ? 3f
            : MathF.Round(Math.Clamp(garment.Pieces.Average(p => p.Physics.Thickness), 1f, 50f));
        History.Execute(Lang.T("Change garment thickness"), doc =>
        {
            if (doc.FindGarment(garmentId) is { } changed)
                changed.Thickness = enabled ? thickness : null;
        });
    }

    /// <summary>Sets the garment-wide collision thickness in millimetres.</summary>
    public void SetGarmentThickness(string garmentId, float millimetres)
    {
        if (float.IsNaN(millimetres) || float.IsInfinity(millimetres))
            return;
        float thickness = Math.Clamp(millimetres, 1f, 50f);
        if (Document.FindGarment(garmentId) is not { } garment
            || garment.Thickness is { } current && MathF.Abs(current - thickness) < 1e-4f)
            return;
        History.Execute(Lang.T("Change garment thickness"), doc =>
        {
            if (doc.FindGarment(garmentId) is { } changed)
                changed.Thickness = thickness;
        }, "garment-thickness:" + garmentId);
    }

    public void SetTopology(string garmentId, MeshTopology topology)
    {
        if (Document.FindGarment(garmentId) is not { } current || current.Topology == topology)
            return;
        History.Execute(Lang.T("Change topology"), doc => doc.FindGarment(garmentId)!.Topology = topology);
    }

    public void SetSimulateWithOthers(string garmentId, bool value)
    {
        History.Execute(Lang.T("Change simulation scope"), doc =>
        {
            if (doc.FindGarment(garmentId) is { } garment)
                garment.SimulateWithOthers = value;
        });
    }

    public void SetMakeHumanZDepth(string garmentId, int value)
    {
        History.Execute(Lang.T("Change MakeHuman layer"), doc =>
        {
            if (doc.FindGarment(garmentId) is { } garment)
                garment.MakeHumanZDepth = Math.Clamp(value, 0, Garment.MaxMakeHumanZDepth);
        });
    }

    public void SetSimulationValue(string key, float value)
    {
        History.Execute(Lang.T("Change simulation settings"), doc =>
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
        History.Execute(Lang.T("Change avatar"), doc =>
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
        History.Execute(Lang.T("Change avatar body"), doc =>
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
        History.Execute(Lang.T("Reset avatar body"), doc =>
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
        History.Execute(Lang.T("Reset arrangement"), doc =>
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
        History.Begin(name ?? (SelectedPieces.Count == 1 ? Lang.T("Move piece in 3D") : Lang.T("Move pieces in 3D")));
        _placementPivot = pivot;
        // With snapping, a single piece lands with its own position on the grid; several pieces with their centre.
        _placementSnapOrigin = SelectedPieces.Count == 1 ? SelectedPieceObjects().First().Placement.Position : pivot;
        _placementMoved = false;
        _placementStart = SelectedPieceObjects().ToDictionary(p => p.Id, p =>
        {
            var (min, max) = Outline.Bounds(p);
            return new PlacementStart(p.Placement.Position, p.Placement.Rotation, p.Drape, (min + max) / 2,
                p.AllPoints().Select(point => (point.Position, point.Curve)).ToArray());
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
            if (!_placementStart.TryGetValue(piece.Id, out var start) || start.Points.Length != piece.PointCount)
                continue;
            var rotation = EulerMatrix(start.Rotation);
            var scale = new Vector2(Along(Vector3.Dot(axis, Vector3.TransformNormal(Vector3.UnitX, rotation))),
                Along(Vector3.Dot(axis, Vector3.TransformNormal(Vector3.UnitY, rotation))));
            int i = 0;
            foreach (var point in piece.AllPoints())
            {
                point.Position = start.Centre + (start.Points[i].Position - start.Centre) * scale;
                point.Curve = start.Points[i].Curve is { } curve ? start.Centre + (curve - start.Centre) * scale : null;
                i++;
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
        History.Execute(Lang.T("Change material"), doc =>
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
        History.Execute(Lang.T("Change colour"), doc => EditableMaterial(doc, ids)!.Color = color, "material:color:" + current.Id);
    }

    public void SetMaterialTexture(string? path)
    {
        var ids = SelectedPieces.ToHashSet();
        if (EditableMaterial(Document, ids) == null)
            return;
        History.Execute(path == null ? Lang.T("Remove texture") : Lang.T("Set texture"), doc => EditableMaterial(doc, ids)!.Texture = path);
    }

    public void RenameMaterial(string name)
    {
        var ids = SelectedPieces.ToHashSet();
        if (EditableMaterial(Document, ids) == null || string.IsNullOrWhiteSpace(name))
            return;
        History.Execute(Lang.T("Rename material"), doc => EditableMaterial(doc, ids)!.Name = name.Trim());
    }

    /// <summary>Gives the selected pieces their own new material (a copy of the first piece's current one).</summary>
    public void NewMaterialForSelection()
    {
        var ids = SelectedPieces.ToHashSet();
        if (ids.Count == 0)
            return;
        History.Execute(Lang.T("New material"), doc =>
        {
            var source = EditableMaterial(doc, ids);
            var material = source?.Clone() ?? new FabricMaterial();
            material.Id = Document.NewId();
            material.Name = doc.UniqueName(source?.Name ?? Lang.T("Fabric"));
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
        History.Execute(Lang.T("Assign material"), doc =>
        {
            foreach (var piece in doc.AllPieces().Where(p => ids.Contains(p.Id)))
                piece.MaterialId = materialId;
        });
    }
}
