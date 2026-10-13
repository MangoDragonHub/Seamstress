using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using Qt.Quick;
using Seamstress.Core;
using Seamstress.Core.Editing;
using Seamstress.Core.IO;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;

namespace Seamstress.App;

/// <summary>
/// The single object the QML user interface talks to (as the "App" singleton). It forwards user actions to the
/// workspace and tells the UI when to refresh; structured data crosses the boundary as small JSON strings.
/// </summary>
[QmlElement(Name = "App", Singleton = true)]
public partial class AppController : INotifyPropertyChanged
{
    internal static StartupOptions Startup { get; set; } = new();

    private readonly Workspace _workspace = new();
    private readonly PatternEditor _editor;
    private readonly SimulationController _simulation;
    private readonly ViewportSync _viewport = new();
    private readonly AppSettings _settings = AppSettings.Load();

    private Avatar? _avatar;
    private string _avatarKey = "";
    private Task<Avatar?>? _avatarTask;
    private string _avatarTaskKey = "";

    private int _sceneRevision, _layersRevision, _selectionRevision, _viewportRevision, _errorRevision;
    private string _status = "Ready";
    private string _simInfo = "";
    private string _lastError = "";
    private bool _simulateAll;
    private bool _refreshQueued;
    private bool _startWhenAvatarReady;

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppController()
    {
        _editor = new PatternEditor(_workspace);
        _simulation = new SimulationController(_workspace, () => _avatar);
        _workspace.History.Changed += OnDocumentChanged;
        _workspace.SelectionChanged += OnDocumentChanged;
        _editor.Changed += () => SceneRevision++;
        if (_settings.DetectMakeHuman())
            TrySaveSettings();
        ApplyStartupOptions();
        EnsureAvatar();
    }

    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void OnDocumentChanged()
    {
        // Several model events often arrive for one user action; the UI re-reads everything once.
        if (_refreshQueued)
            return;
        _refreshQueued = true;
        try
        {
            SceneRevision++;
            LayersRevision++;
            SelectionRevision++;
            ViewportRevision++;
            Notify(nameof(WindowTitle));
            Notify(nameof(CanUndo));
            Notify(nameof(CanRedo));
            Notify(nameof(UndoText));
            Notify(nameof(RedoText));
            Notify(nameof(Dirty));
            Notify(nameof(Simulating));
            Notify(nameof(HasFile));
            Notify(nameof(AvatarType));
            EnsureAvatar();
        }
        finally
        {
            _refreshQueued = false;
        }
    }

    // ---------------------------------------------------------------- observable state

    public int SceneRevision { get => _sceneRevision; private set { _sceneRevision = value; Notify(); } }
    public int LayersRevision { get => _layersRevision; private set { _layersRevision = value; Notify(); } }
    public int SelectionRevision { get => _selectionRevision; private set { _selectionRevision = value; Notify(); } }
    public int ViewportRevision { get => _viewportRevision; private set { _viewportRevision = value; Notify(); } }
    public int ErrorRevision { get => _errorRevision; private set { _errorRevision = value; Notify(); } }

    public string WindowTitle
    {
        get
        {
            string name = _workspace.FilePath != null ? Path.GetFileName(_workspace.FilePath) : "Untitled";
            return (_workspace.History.IsDirty ? "● " : "") + name + " — Seamstress";
        }
    }

    public bool Dirty => _workspace.History.IsDirty;
    public bool HasFile => _workspace.FilePath != null;
    public bool CanUndo => _workspace.History.CanUndo || _simulation.IsRunning;
    public bool CanRedo => _workspace.History.CanRedo;
    public string UndoText => _workspace.History.CanUndo ? "Undo " + _workspace.History.UndoName : "Undo";
    public string RedoText => _workspace.History.CanRedo ? "Redo " + _workspace.History.RedoName : "Redo";
    public bool Simulating => _simulation.IsRunning;

    public string StatusText { get => _status; private set { _status = value; Notify(); } }
    public string SimInfo { get => _simInfo; private set { if (_simInfo != value) { _simInfo = value; Notify(); } } }
    public string LastError { get => _lastError; private set { _lastError = value; Notify(); } }

    public string Tool
    {
        get => _editor.Tool.ToString();
        set
        {
            if (Enum.TryParse<PatternTool>(value, out var tool) && tool != _editor.Tool)
            {
                _editor.Tool = tool;
                Notify();
                StatusText = _editor.Hint;
            }
        }
    }

    public bool UvMode
    {
        get => _editor.UvMode;
        set
        {
            if (_editor.UvMode == value)
                return;
            _editor.UvMode = value;
            Notify();
            StatusText = _editor.Hint;
        }
    }

    /// <summary>When set, the Simulate button drives every visible garment instead of only the active one.</summary>
    public bool SimulateAll
    {
        get => _simulateAll;
        set { if (_simulateAll != value) { _simulateAll = value; Notify(); } }
    }

    public string CheckerTexture => _viewport.CheckerUrl;
    public string GizmoArrow => _viewport.ArrowUrl;
    public string GizmoRing => _viewport.RingUrl;

    /// <summary>Topology overlay in the 3D view: draws the outline of every face.</summary>
    public bool ShowWire
    {
        get => _viewport.ShowWire;
        set
        {
            if (_viewport.ShowWire == value)
                return;
            _viewport.ShowWire = value;
            Notify();
            ViewportRevision++;
        }
    }
    public string Renderer => Startup.RendererDescription;

    /// <summary>The application version from Directory.Build.props, without the source revision the SDK appends.</summary>
    public string Version { get; } = (System.Reflection.CustomAttributeExtensions
        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(AppController).Assembly)?.InformationalVersion ?? "").Split('+')[0];
    public string StartupScreenshot => Startup.Screenshot ?? "";
    public string StartupView => Startup.View;
    public bool StartupSmoke => Startup.Smoke;
    public int StartupWidth => Startup.Width;
    public int StartupHeight => Startup.Height;

    /// <summary>Writes a line to standard error; used by the automated UI check.</summary>
    public void Log(string message) => Console.Error.WriteLine(message);

    private void Fail(string action, Exception e)
    {
        LastError = $"{action}\n\n{e.Message}";
        StatusText = action;
        ErrorRevision++;
    }

    private void Fail(string message)
    {
        LastError = message;
        ErrorRevision++;
    }

    private void TrySaveSettings()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusText = "Could not save preferences: " + e.Message;
        }
    }

    // ---------------------------------------------------------------- 2D viewport

    public string PatternScene() => _editor.SceneJson(_settings.TextureResolution);

    /// <summary>Shift is held in the 2D view: the drawing tools keep their proportions (see PatternEditor.Constrain).</summary>
    public bool Constrain
    {
        get => _editor.Constrain;
        set
        {
            if (_editor.Constrain == value)
                return;
            // Constrained polygon sides come out as round numbers in the units being shown: 1 cm or 1/4 in.
            _editor.LengthStep = Imperial ? 0.635f : 1f;
            _editor.Constrain = value;
            Notify();
        }
    }

    public void PointerDown(double x, double y, double tolerance, bool additive)
        => _editor.PointerDown(new Vector2((float)x, (float)y), (float)tolerance, additive);

    public void PointerMove(double x, double y, double tolerance)
        => _editor.PointerMove(new Vector2((float)x, (float)y), (float)tolerance);

    public void PointerUp(double x, double y) => _editor.PointerUp(new Vector2((float)x, (float)y));

    public void DoubleClick(double x, double y, double tolerance)
        => _editor.DoubleClick(new Vector2((float)x, (float)y), (float)tolerance);

    public void CancelTool()
    {
        _editor.Cancel();
        StatusText = _editor.Hint;
    }

    public void ConfirmTool() => _editor.Confirm();

    // ---------------------------------------------------------------- 3D viewport

    public string ViewportModels() => _viewport.Sync(_workspace, _avatar);

    public void SelectIn3D(string pieceId, bool additive)
    {
        if (string.IsNullOrEmpty(pieceId))
            _workspace.ClearSelection();
        else
            _workspace.SelectPiece(pieceId, additive);
    }

    // Move/rotate handles in the 3D view. Offsets and angles are totals since the drag began.
    public bool GizmoBegin()
    {
        if (_simulation.IsRunning)
            StopSimulation();
        return _workspace.BeginPlacementEdit();
    }

    public void GizmoMove(double x, double y, double z, bool snap)
        => _workspace.ApplyPlacementEdit(new Vector3((float)x, (float)y, (float)z), Quaternion.Identity, snap ? (float)(SnapStepCm / 100) : 0);

    public void GizmoRotate(string axis, double degrees)
    {
        var direction = axis == "x" ? Vector3.UnitX : axis == "y" ? Vector3.UnitY : Vector3.UnitZ;
        _workspace.ApplyPlacementEdit(Vector3.Zero, Quaternion.CreateFromAxisAngle(direction, (float)(degrees * Math.PI / 180)));
    }

    public void GizmoEnd() => _workspace.EndPlacementEdit();

    // Pinching cloth in the 3D view. If no simulation is running, one is started for the garment that was grabbed
    // and stopped again shortly after the cloth is let go, so the whole pinch is a single undo step.
    private bool _pinchStartedSimulation;
    private bool _pinching;
    private long _pinchStopFrame = -1;
    private const int PinchSettleFrames = 45;

    public bool PinchBegin(string pieceId, double x, double y, double z)
    {
        var garment = _workspace.Document.GarmentOfPiece(pieceId);
        if (garment == null)
            return false;
        if (!_simulation.IsRunning)
        {
            if (_avatarTask != null)
            {
                StatusText = "The avatar is still being prepared — try again in a moment.";
                return false;
            }
            if (_workspace.ActiveGarmentId != garment.Id)
                _workspace.SelectPiece(pieceId);
            string? problem = _simulation.Start(_simulateAll, "Pinch cloth");
            if (problem != null)
            {
                StatusText = problem;
                return false;
            }
            _pinchStartedSimulation = true;
            Notify(nameof(Simulating));
            Notify(nameof(CanUndo));
        }
        else if (!_simulation.SimulatedGarments.Contains(garment.Id))
        {
            StatusText = $"“{garment.Name}” is not part of the running simulation, so it cannot be pinched right now.";
            return false;
        }
        _pinching = true;
        _pinchStopFrame = -1;
        _simulation.BeginPinch(new Vector3((float)x, (float)y, (float)z));
        StatusText = "Pinching — drag to pull the cloth, release to let it settle.";
        return true;
    }

    public void PinchMove(double x, double y, double z)
    {
        if (_pinching)
            _simulation.MovePinch(new Vector3((float)x, (float)y, (float)z));
    }

    public void PinchEnd()
    {
        if (!_pinching)
            return;
        _pinching = false;
        _simulation.EndPinch();
        if (_pinchStartedSimulation)
            _pinchStopFrame = _simulation.Frame + PinchSettleFrames;
        else
            StatusText = "Simulating — press Space to stop.";
    }

    /// <summary>Called by a UI timer: applies the newest simulation frame and finishes background avatar loading.</summary>
    public void Tick()
    {
        if (_avatarTask is { IsCompleted: true } task)
        {
            _avatarTask = null;
            if (task.IsCompletedSuccessfully)
            {
                _avatar = task.Result;
                _avatarKey = _avatarTaskKey;
                StatusText = _avatar == null ? "No avatar"
                    : _avatar.MakeHuman is { Shaped: false }
                        ? "Avatar ready: MakeHuman base mesh as it is on disk (MakeHuman's targets folder was not found next to it, so body shape cannot be changed)"
                        : $"Avatar ready: {_avatar.Name}";
            }
            else
            {
                _avatar = null;
                _avatarKey = _avatarTaskKey;
                Fail("Could not load the avatar.", task.Exception?.GetBaseException() ?? new Exception("Unknown error"));
            }
            ViewportRevision++;
            EnsureAvatar();
            if (_startWhenAvatarReady && _avatarTask == null)
            {
                _startWhenAvatarReady = false;
                StartSimulation();
            }
        }

        if (!_simulation.IsRunning)
        {
            _pinchStartedSimulation = false;
            _pinchStopFrame = -1;
            return;
        }
        if (_pinchStartedSimulation && !_pinching && _pinchStopFrame >= 0 && _simulation.Frame >= _pinchStopFrame)
        {
            // The cloth has had a moment to settle after the pinch that started this run.
            _simulation.Pump();
            StopSimulation();
            StatusText = "Pinch applied. Ctrl+Z undoes it.";
            return;
        }
        if (_simulation.Pump())
        {
            ViewportRevision++;
            SimInfo = $"Simulating · {_simulation.ParticleCount:N0} particles · {_simulation.StepMilliseconds:0.0} ms/step";
        }
    }

    // ---------------------------------------------------------------- avatar

    private string AvatarKeyFor(AvatarSettings a) => a.Kind switch
    {
        AvatarKind.None => "none",
        AvatarKind.Mannequin => "mannequin",
        AvatarKind.MakeHuman => $"makehuman|{_settings.MakeHumanBaseMesh}|{a.Gender}|{a.Age}|{a.Muscle}|{a.Weight}|{a.Height}|{a.Proportions}",
        _ => $"obj|{a.Path}|{a.Scale}",
    };

    /// <summary>Starts loading the document's avatar in the background if it is not the one currently shown.</summary>
    private void EnsureAvatar()
    {
        var wanted = _workspace.Document.Avatar.Clone();
        string key = AvatarKeyFor(wanted);
        if (key == _avatarKey || _avatarTask != null)
            return;
        _avatarTaskKey = key;
        string baseMesh = _settings.MakeHumanBaseMesh;
        if (wanted.Kind != AvatarKind.None)
            StatusText = "Preparing avatar…";
        _avatarTask = Task.Run(() => wanted.Kind switch
        {
            AvatarKind.None => null,
            AvatarKind.Mannequin => Avatar.CreateMannequin(),
            AvatarKind.MakeHuman => File.Exists(baseMesh)
                ? Avatar.FromMakeHumanBase(baseMesh, wanted)
                : throw new FileNotFoundException("The MakeHuman base mesh was not found. Set its location in Preferences."),
            _ => Avatar.FromObj(wanted.Path ?? throw new FileNotFoundException("No avatar file is set."), wanted.Scale),
        });
    }

    public string AvatarType => _workspace.Document.Avatar.Kind.ToString();

    /// <summary>The MakeHuman body sliders (0-1 each) as JSON, plus the resulting height once the avatar is loaded.</summary>
    public string AvatarShape()
    {
        var a = _workspace.Document.Avatar;
        float height = _avatar?.MakeHuman != null ? _avatar.Mesh.Bounds().Max.Y * 100f : 0;
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            gender = a.Gender, age = a.Age, muscle = a.Muscle, weight = a.Weight, height = a.Height, proportions = a.Proportions,
            heightCm = MathF.Round(height, 1),
            shaped = _avatar?.MakeHuman?.Shaped ?? true,
            loading = _avatarTask != null,
        });
    }

    public void SetAvatarShape(string key, double value) => _workspace.SetAvatarShape(key, (float)value);
    public void ResetAvatarShape() => _workspace.ResetAvatarShape();

    public void SetAvatar(string kind, string fileUrl, double scale)
    {
        if (!Enum.TryParse<AvatarKind>(kind, out var parsed))
            return;
        if (parsed == AvatarKind.MakeHuman && !File.Exists(_settings.MakeHumanBaseMesh))
        {
            Fail("The MakeHuman base mesh is not linked yet.\n\nOpen File ▸ Preferences and point Seamstress at MakeHuman's base.obj (inside its data/3dobjs folder).");
            return;
        }
        _workspace.SetAvatar(parsed, string.IsNullOrEmpty(fileUrl) ? null : LocalPath(fileUrl), scale <= 0 ? 1f : (float)scale);
        Notify(nameof(AvatarType));
    }

    // ---------------------------------------------------------------- editing commands

    public void Undo()
    {
        if (_simulation.IsRunning)
        {
            StopSimulation();
            return;
        }
        if (_workspace.History.Undo())
            StatusText = "Undone";
    }

    public void Redo()
    {
        if (_workspace.History.Redo())
            StatusText = "Redone";
    }

    public void DeleteSelection() => _workspace.DeleteSelection();
    public void Duplicate() => _workspace.DuplicateSelection();
    public void Mirror() => _workspace.MirrorSelection();
    public void Rotate(double degrees) => _workspace.RotateSelection((float)degrees);
    public void SelectAll()
    {
        if (_workspace.ActiveGarment is { } garment)
            _workspace.SelectLayer(garment.Id);
    }

    public void AddGarment() => _workspace.AddGarment();

    public void AddTemplate(string name)
    {
        _workspace.AddTemplate(name, _avatar?.Mesh.Bounds().Max.Y ?? Templates.ReferenceBodyHeight);
        StatusText = $"Added {name}. Press Simulate to sew and drape it.";
    }

    public string TemplateNames() => string.Join("|", Templates.Names);

    public void AddGroup() => _workspace.AddGroup();
    public void GroupSelection()
    {
        if (_workspace.GroupSelection() == null)
            StatusText = "Select one or more pieces of the same garment to group them.";
    }

    public void Ungroup(string groupId) => _workspace.Ungroup(groupId);
    public void MoveToGroup(string id, string groupId) => _workspace.MoveToGroup(id, string.IsNullOrEmpty(groupId) ? null : groupId);
    public void Rename(string id, string name) => _workspace.Rename(id, name);
    public void SetVisible(string id, bool visible) => _workspace.SetVisible(id, visible);
    public void SetExpanded(string id, bool expanded) => _workspace.SetExpanded(id, expanded);
    public void SelectLayer(string id, bool additive) => _workspace.SelectLayer(id, additive);
    public void DeleteLayer(string id) => _workspace.DeleteLayer(id);

    public void ReverseSeam()
    {
        if (_workspace.SelectedSeamId is { } id)
            _workspace.ReverseSeam(id);
    }

    public void StraightenEdges()
    {
        foreach (var piece in _workspace.SelectedPieceObjects().ToList())
        {
            for (int e = 0; e < piece.Points.Count; e++)
            {
                if (piece.Points[e].Curve != null)
                    _workspace.StraightenEdge(piece.Id, e);
            }
        }
    }

    // ---------------------------------------------------------------- properties

    public void SetPieceValue(string key, double value) => _workspace.SetPieceValue(key, (float)value);
    public void SetPreset(string preset) => _workspace.SetPhysicsPreset(preset);

    public void SetParticleDistance(double value)
    {
        if (_workspace.ActiveGarment is { } garment)
            _workspace.SetGarmentParticleDistance(garment.Id, (float)value);
    }

    public void SetSimulateWithOthers(string garmentId, bool value) => _workspace.SetSimulateWithOthers(garmentId, value);

    public void SetTopology(string topology)
    {
        if (_workspace.ActiveGarment is { } garment)
            _workspace.SetTopology(garment.Id, topology == "Quads" ? MeshTopology.Quads : MeshTopology.Triangles);
    }
    public void SetSimulationValue(string key, double value) => _workspace.SetSimulationValue(key, (float)value);
    public void SetMaterialValue(string key, double value) => _workspace.SetMaterialValue(key, (float)value);
    public void SetMaterialColor(string color) => _workspace.SetMaterialColor(color.Length > 7 ? "#" + color[^6..] : color);
    public void SetMaterialTexture(string fileUrl) => _workspace.SetMaterialTexture(string.IsNullOrEmpty(fileUrl) ? null : LocalPath(fileUrl));
    public void RenameMaterial(string name) => _workspace.RenameMaterial(name);
    public void NewMaterial() => _workspace.NewMaterialForSelection();
    public void AssignMaterial(string id) => _workspace.AssignMaterial(id);

    // ---------------------------------------------------------------- simulation

    public void ToggleSimulation()
    {
        if (_startWhenAvatarReady)
        {
            _startWhenAvatarReady = false;
            StatusText = "Simulation cancelled.";
            return;
        }
        if (_simulation.IsRunning)
            StopSimulation();
        else
            StartSimulation();
    }

    private void StartSimulation()
    {
        if (_avatarTask != null)
        {
            // The avatar's collision field is still being built; start as soon as it is ready.
            _startWhenAvatarReady = true;
            StatusText = "Preparing the avatar — the simulation starts as soon as it is ready.";
            return;
        }
        string? problem = _simulation.Start(_simulateAll);
        if (problem != null)
        {
            StatusText = problem;
            return;
        }
        var names = _workspace.Document.Garments.Where(g => _simulation.SimulatedGarments.Contains(g.Id)).Select(g => g.Name);
        StatusText = "Simulating " + string.Join(", ", names) + " — press Space to stop.";
        Notify(nameof(Simulating));
        Notify(nameof(CanUndo));
    }

    private void StopSimulation()
    {
        _pinching = false;
        _pinchStartedSimulation = false;
        _pinchStopFrame = -1;
        string? error = _simulation.Stop();
        SimInfo = "";
        Notify(nameof(Simulating));
        if (error != null)
            Fail("The simulation stopped because of an error.", new Exception(error));
        else
            StatusText = "Simulation stopped.";
    }

    public void ResetArrangement()
    {
        if (_simulation.IsRunning)
            StopSimulation();
        _workspace.ResetDrape(wholeGarment: _workspace.SelectedPieces.Count == 0 || _workspace.SelectedLayerId != null);
        StatusText = "Pieces returned to their flat arrangement.";
    }

    // ---------------------------------------------------------------- files

    internal static string LocalPath(string urlOrPath)
        => urlOrPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? new Uri(urlOrPath).LocalPath : urlOrPath;

    public void NewDocument()
    {
        _workspace.New();
        StatusText = "New project";
    }

    public bool Open(string fileUrl)
    {
        string path = LocalPath(fileUrl);
        try
        {
            _workspace.Open(path);
            _settings.AddRecent(path);
            TrySaveSettings();
            StatusText = "Opened " + Path.GetFileName(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Fail($"Could not open {Path.GetFileName(path)}.", e);
            return false;
        }
    }

    public bool Save() => _workspace.FilePath != null && SaveAs(_workspace.FilePath);

    public bool SaveAs(string fileUrl)
    {
        string path = LocalPath(fileUrl);
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            path += GarmentFile.Extension;
        try
        {
            if (_simulation.IsRunning)
                StopSimulation();
            _workspace.Save(path);
            _settings.AddRecent(path);
            TrySaveSettings();
            StatusText = "Saved " + Path.GetFileName(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Fail($"Could not save {Path.GetFileName(path)}.", e);
            return false;
        }
    }

    public string SuggestedFileName(string extension)
    {
        string name = _workspace.FilePath != null
            ? Path.GetFileName(_workspace.FilePath).Replace(GarmentFile.Extension, "", StringComparison.OrdinalIgnoreCase)
            : (_workspace.ActiveGarment?.Name ?? "garment");
        return ExportScene.SafeName(name) + extension;
    }

    private ExportScene BuildExport(bool weld, bool activeOnly)
    {
        if (_simulation.IsRunning)
            StopSimulation();
        var options = new ExportOptions { WeldSeams = weld };
        if (activeOnly && _workspace.ActiveGarment is { } active)
            options.GarmentIds = new[] { active.Id };
        var scene = ExportScene.Build(_workspace.Document, _workspace.MeshFor, options);
        if (scene.Meshes.Count == 0)
            throw new InvalidOperationException("There is nothing to export: no visible garment has any pieces.");
        return scene;
    }

    /// <summary>Exports to "obj", "gltf" (.glb or .gltf chosen by the file name) or "fbx".</summary>
    public bool ExportTo(string format, string fileUrl, bool weld, bool activeOnly)
    {
        string path = LocalPath(fileUrl);
        try
        {
            var scene = BuildExport(weld, activeOnly);
            switch (format)
            {
                case "obj":
                    path = WithExtension(path, ".obj");
                    ObjExporter.Export(scene, path);
                    break;
                case "gltf":
                    path = WithExtension(path, ".glb", ".gltf");
                    GltfExporter.Export(scene, path);
                    break;
                case "fbx":
                    path = WithExtension(path, ".fbx");
                    FbxExporter.Export(scene, path);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown export format '{format}'.");
            }
            int faces = scene.Meshes.Sum(m => format == "gltf" ? m.TriangleCount : m.FaceCount);
            StatusText = $"Exported {scene.Meshes.Count} garment(s), {faces:N0} faces to {path}";
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Fail("Export failed.", e);
            return false;
        }
    }

    private static string WithExtension(string path, params string[] allowed)
        => allowed.Any(a => path.EndsWith(a, StringComparison.OrdinalIgnoreCase)) ? path : path + allowed[0];

    // ---------------------------------------------------------------- MakeHuman

    public bool MakeHumanLinked => Directory.Exists(_settings.MakeHumanUserFolder) && File.Exists(_settings.MakeHumanBaseMesh);

    /// <summary>Writes the active garment into MakeHuman's clothes library, bound to the base mesh.</summary>
    public bool SendToMakeHuman()
    {
        try
        {
            if (_settings.MakeHumanClothesFolder is not { } clothes || !Directory.Exists(_settings.MakeHumanUserFolder))
                throw new InvalidOperationException("MakeHuman is not linked yet. Open File ▸ Preferences and choose MakeHuman's user data folder.");
            if (_workspace.ActiveGarment is not { } garment)
                throw new InvalidOperationException("Select the garment to send first.");
            if (_avatar?.MakeHuman is not { } baseMesh)
                throw new InvalidOperationException("Garments are fitted to MakeHuman's base mesh.\n\nChoose Avatar ▸ MakeHuman Base Mesh, simulate the garment on it, then send it again.");
            var scene = BuildExport(weld: true, activeOnly: true);
            var result = MakeHumanExporter.Export(scene.Meshes[0], baseMesh, clothes, _settings.Author);
            string launched = "";
            if (!string.IsNullOrWhiteSpace(_settings.MakeHumanCommand) && Startup.AllowLaunch)
                launched = LaunchMakeHuman() ? " MakeHuman is starting." : "";
            StatusText = $"Sent “{garment.Name}” to MakeHuman ({result.VertexCount:N0} vertices): {result.Folder}.{launched}";
            LastMakeHumanFolder = result.Folder;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Fail("Could not send the garment to MakeHuman.", e);
            return false;
        }
    }

    public string LastMakeHumanFolder { get; private set; } = "";

    private bool LaunchMakeHuman()
    {
        try
        {
            if (System.Diagnostics.Process.GetProcesses().Any(p => SafeName(p).Contains("makehuman", StringComparison.OrdinalIgnoreCase)))
                return false; // already running; it will pick the garment up on its next start or asset reload
            var info = OperatingSystem.IsWindows()
                ? new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c " + _settings.MakeHumanCommand)
                : new System.Diagnostics.ProcessStartInfo("/bin/sh", new[] { "-c", _settings.MakeHumanCommand });
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            System.Diagnostics.Process.Start(info);
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusText = "Could not start MakeHuman: " + e.Message;
            return false;
        }
    }

    private static string SafeName(System.Diagnostics.Process p)
    {
        try { return p.ProcessName; }
        catch (InvalidOperationException) { return ""; }
    }

    // ---------------------------------------------------------------- preferences

    public string SettingMakeHumanUserFolder => _settings.MakeHumanUserFolder;
    public string SettingMakeHumanBaseMesh => _settings.MakeHumanBaseMesh;
    public string SettingMakeHumanCommand => _settings.MakeHumanCommand;
    public string SettingRenderer => _settings.Renderer;
    public int SettingTextureResolution => _settings.TextureResolution;
    public string SettingAuthor => _settings.Author;
    public string SettingUnits => _settings.Units;

    /// <summary>True when lengths are shown in inches. "--units" overrides the saved preference for one run.</summary>
    public bool Imperial => (Startup.Units ?? _settings.Units) == "imperial";

    /// <summary>Spacing of the 3D ground grid, in centimetres.</summary>
    public double GridSizeCm => _settings.GridSize > 0 ? _settings.GridSize : AppSettings.DefaultGridStep(Imperial);

    /// <summary>Step that Shift-dragging a move handle snaps to, in centimetres.</summary>
    public double SnapStepCm => _settings.SnapStep > 0 ? _settings.SnapStep : AppSettings.DefaultGridStep(Imperial);

    public void SavePreferences(string userFolder, string baseMesh, string command, string renderer, int textureResolution, string author, string units,
        double gridSizeCm, double snapStepCm)
    {
        bool baseChanged = _settings.MakeHumanBaseMesh != LocalPath(baseMesh.Trim());
        _settings.MakeHumanUserFolder = LocalPath(userFolder.Trim());
        _settings.MakeHumanBaseMesh = LocalPath(baseMesh.Trim());
        _settings.MakeHumanCommand = command.Trim();
        _settings.Renderer = renderer is "gpu" or "cpu" ? renderer : "auto";
        _settings.TextureResolution = Math.Clamp(textureResolution, 64, 16384);
        _settings.Author = author.Trim();
        _settings.Units = units == "imperial" ? "imperial" : "metric";
        // A value equal to the default is stored as "default", so it follows the units if they change later.
        double standard = AppSettings.DefaultGridStep(_settings.Units == "imperial");
        double Chosen(double cm) => !(cm > 0) || Math.Abs(cm - standard) < 0.01 ? 0 : Math.Clamp(cm, 0.1, 1000);
        _settings.GridSize = Chosen(gridSizeCm);
        _settings.SnapStep = Chosen(snapStepCm);
        Startup.Units = null;
        TrySaveSettings();
        NotifySettings();
        if (baseChanged)
            EnsureAvatar();
        SelectionRevision++;
        SceneRevision++;
        StatusText = "Preferences saved.";
    }

    /// <summary>Searches the usual locations for MakeHuman and fills in the preferences that are still empty.</summary>
    public bool DetectMakeHuman()
    {
        string user = _settings.MakeHumanUserFolder, mesh = _settings.MakeHumanBaseMesh;
        _settings.MakeHumanUserFolder = "";
        _settings.MakeHumanBaseMesh = "";
        bool found = _settings.DetectMakeHuman();
        if (string.IsNullOrEmpty(_settings.MakeHumanUserFolder))
            _settings.MakeHumanUserFolder = user;
        if (string.IsNullOrEmpty(_settings.MakeHumanBaseMesh))
            _settings.MakeHumanBaseMesh = mesh;
        NotifySettings();
        return found;
    }

    private void NotifySettings()
    {
        Notify(nameof(SettingMakeHumanUserFolder));
        Notify(nameof(SettingMakeHumanBaseMesh));
        Notify(nameof(SettingMakeHumanCommand));
        Notify(nameof(SettingRenderer));
        Notify(nameof(SettingTextureResolution));
        Notify(nameof(SettingAuthor));
        Notify(nameof(SettingUnits));
        Notify(nameof(Imperial));
        Notify(nameof(GridSizeCm));
        Notify(nameof(SnapStepCm));
        Notify(nameof(MakeHumanLinked));
    }

    public string RecentFiles() => string.Join("|", _settings.RecentFiles.Where(File.Exists));

    /// <summary>File name without folders, whichever path separator the platform uses.</summary>
    public string FileName(string path) => Path.GetFileName(LocalPath(path));

    /// <summary>Converts a file URL from a dialog into an ordinary path.</summary>
    public string PathFromUrl(string url) => LocalPath(url);

    /// <summary>Folder the automated check writes its files into.</summary>
    public string StartupFolderUrl => Startup.Screenshot == null ? "" : ViewportSync.ToUrl(Path.GetDirectoryName(Startup.Screenshot)!);

    /// <summary>Called when the window closes.</summary>
    public void Shutdown()
    {
        _simulation.Dispose();
        _viewport.Dispose();
    }

    /// <summary>The viewport rendered its first frame, so the chosen renderer works on this machine.</summary>
    public void ViewportReady() => Startup.ClearCrashMarker();
}
