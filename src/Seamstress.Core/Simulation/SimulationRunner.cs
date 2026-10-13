using System.Diagnostics;
using System.Numerics;

namespace Seamstress.Core.Simulation;

/// <summary>Runs a <see cref="ClothSolver"/> on a background thread and hands finished frames to the UI thread.</summary>
public sealed class SimulationRunner : IDisposable
{
    private readonly ClothSolver _solver;
    private readonly object _gate = new();
    private readonly Vector3[] _front;
    private Thread? _thread;
    private volatile bool _running;
    private long _frame;
    private long _taken;

    public SimulationRunner(ClothSolver solver)
    {
        _solver = solver;
        _front = (Vector3[])solver.Positions.Clone();
    }

    public ClothSolver Solver => _solver;
    public bool IsRunning => _running;

    /// <summary>Simulation frames completed so far.</summary>
    public long Frame => Interlocked.Read(ref _frame);

    /// <summary>Milliseconds the most recent solver step took.</summary>
    public double StepMilliseconds { get; private set; }

    public string? Error { get; private set; }

    public void Start()
    {
        if (_running)
            return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Seamstress simulation" };
        _thread.Start();
    }

    private void Loop()
    {
        var clock = Stopwatch.StartNew();
        const double framePeriod = 1000.0 / 60.0;
        try
        {
            while (_running)
            {
                double started = clock.Elapsed.TotalMilliseconds;
                _solver.Step();
                lock (_gate)
                {
                    Array.Copy(_solver.Positions, _front, _front.Length);
                    _frame++;
                }
                double elapsed = clock.Elapsed.TotalMilliseconds - started;
                StepMilliseconds = elapsed;
                if (elapsed < framePeriod)
                    Thread.Sleep((int)(framePeriod - elapsed));
            }
        }
        catch (Exception e)
        {
            Error = e.Message;
            _running = false;
        }
    }

    /// <summary>Runs a fixed number of frames on the calling thread (headless use and tests). Stops the thread first.</summary>
    public void RunFrames(int frames)
    {
        Stop();
        for (int i = 0; i < frames; i++)
            _solver.Step();
        lock (_gate)
        {
            Array.Copy(_solver.Positions, _front, _front.Length);
            _frame += frames;
        }
    }

    /// <summary>Stops the simulation thread and waits for it to finish its current step.</summary>
    public void Stop()
    {
        _running = false;
        _thread?.Join();
        _thread = null;
    }

    /// <summary>
    /// Returns per-piece drape arrays for the newest frame, or null when nothing changed since the last call.
    /// Pass <paramref name="force"/> to get the current state regardless.
    /// </summary>
    public Dictionary<string, float[]>? TakeFrame(bool force = false)
    {
        lock (_gate)
        {
            if (!force && _taken == _frame)
                return null;
            _taken = _frame;
            var result = new Dictionary<string, float[]>();
            foreach (var piece in _solver.Pieces)
            {
                if (!piece.Simulated)
                    continue;
                var drape = new float[piece.Mesh.VertexCount * 3];
                for (int i = 0; i < piece.Mesh.VertexCount; i++)
                {
                    var p = _front[piece.Offset + i];
                    drape[3 * i] = p.X;
                    drape[3 * i + 1] = p.Y;
                    drape[3 * i + 2] = p.Z;
                }
                result[piece.Piece.Id] = drape;
            }
            return result;
        }
    }

    public void Dispose() => Stop();
}
