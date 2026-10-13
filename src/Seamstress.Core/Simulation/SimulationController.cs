using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;

namespace Seamstress.Core.Simulation;

/// <summary>
/// Starts and stops cloth simulation for a workspace. Only the active garment is simulated unless other garments opt
/// in ("simulate with others") or the caller asks for all of them; a whole run becomes a single undo step.
/// </summary>
public sealed class SimulationController : IDisposable
{
    private readonly Workspace _workspace;
    private readonly Func<Avatar?> _avatar;
    private SimulationRunner? _runner;
    private bool _stopping;
    private readonly HashSet<string> _garments = new();

    public SimulationController(Workspace workspace, Func<Avatar?> avatar)
    {
        _workspace = workspace;
        _avatar = avatar;
        // Any other edit (including undo) ends the run first, so the solver never works on a stale document.
        _workspace.History.BeforeChange += () =>
        {
            if (IsRunning && !_stopping)
                Stop();
        };
    }

    public bool IsRunning => _runner != null;
    public IReadOnlyCollection<string> SimulatedGarments => _garments;
    public int ParticleCount => _runner?.Solver.ParticleCount ?? 0;
    public double StepMilliseconds => _runner?.StepMilliseconds ?? 0;
    public long Frame => _runner?.Frame ?? 0;

    /// <summary>The garments a run started now would simulate.</summary>
    public List<Garment> GarmentsToSimulate(bool all)
    {
        var doc = _workspace.Document;
        var active = _workspace.ActiveGarment;
        return doc.Garments.Where(g => g.Visible && g.Pieces.Count > 0 && (all || g == active || g.SimulateWithOthers)).ToList();
    }

    /// <summary>Starts simulating. Returns a message when there is nothing to simulate.</summary>
    public string? Start(bool allGarments)
    {
        if (IsRunning)
            return null;
        var doc = _workspace.Document;
        var simulated = GarmentsToSimulate(allGarments);
        if (simulated.Count == 0)
            return "There is no visible garment to simulate. Add a garment or select one in the layers panel.";

        _stopping = true; // Begin raises BeforeChange; this run has not started yet
        _workspace.History.Begin(simulated.Count == 1 ? $"Simulate {simulated[0].Name}" : "Simulate garments");
        _stopping = false;

        var active = simulated.Select(g => _workspace.MeshFor(g)).ToList();
        var obstacles = doc.Garments.Where(g => g.Visible && !simulated.Contains(g)).Select(g => _workspace.MeshFor(g)).ToList();
        var solver = ClothSolver.Create(active, obstacles, _avatar()?.Sdf, doc.Simulation.Clone());
        _garments.Clear();
        foreach (var g in simulated)
            _garments.Add(g.Id);
        _runner = new SimulationRunner(solver);
        _runner.Start();
        return null;
    }

    /// <summary>Copies the newest simulated frame into the document. Returns true when something changed.</summary>
    public bool Pump(bool force = false)
    {
        var runner = _runner;
        if (runner == null)
            return false;
        var frame = runner.TakeFrame(force);
        if (frame == null)
            return false;
        foreach (var cloth in runner.Solver.Pieces)
        {
            if (cloth.Simulated && frame.TryGetValue(cloth.Piece.Id, out var drape))
            {
                cloth.Piece.Drape = drape;
                cloth.Piece.DrapeKey = cloth.Mesh.Key;
            }
        }
        return true;
    }

    /// <summary>Advances a started run by exactly <paramref name="frames"/> frames without real-time pacing.</summary>
    public void RunFrames(int frames) => _runner?.RunFrames(frames);

    public string? Stop()
    {
        var runner = _runner;
        if (runner == null)
            return null;
        _stopping = true;
        try
        {
            runner.Stop();
            Pump(force: true);
            _runner = null;
            _garments.Clear();
            _workspace.History.Commit();
            return runner.Error;
        }
        finally
        {
            _stopping = false;
        }
    }

    public void Dispose() => _runner?.Dispose();
}
