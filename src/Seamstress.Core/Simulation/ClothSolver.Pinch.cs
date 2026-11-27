using System.Numerics;

namespace Seamstress.Core.Simulation;

// Pinching: the user takes hold of the cloth at a point and drags it. The particles around that point follow the
// hand, most strongly at its centre, while the rest of the simulation (seams, stretch, collisions) keeps running,
// so the cloth can be tugged into place but not pulled through the body.
public sealed partial class ClothSolver
{
    private const float PinchMaxSpeed = 1.5f;
    private const float PinchPickDistance = 0.10f;

    private readonly object _pinchGate = new();
    private Vector3? _pinchRequest;
    private float _pinchRadius;
    private Vector3 _pinchTarget;
    private bool _pinchRelease;
    private bool _pinchRecapture;
    private int _pinchGarment = -1;

    private int[] _pinched = Array.Empty<int>();
    private float[] _pinchStrength = Array.Empty<float>();
    private Vector3[] _pinchOffset = Array.Empty<Vector3>();
    private Vector3 _pinchHand;

    /// <summary>True while a patch of cloth is being held.</summary>
    public bool IsPinching => _pinched.Length > 0 || _pinchRequest != null;

    public int PinchedParticleCount => _pinched.Length;

    /// <summary>Takes hold of the simulated cloth nearest to <paramref name="point"/>. Safe to call from any thread.</summary>
    public void BeginPinch(Vector3 point, float radius = 0.05f)
    {
        lock (_pinchGate)
        {
            _pinchRequest = point;
            _pinchRadius = Math.Clamp(radius, 0.005f, 0.2f);
            _pinchTarget = point;
            _pinchRelease = false;
        }
    }

    public void SetPinchRadius(float radius)
    {
        lock (_pinchGate)
        {
            _pinchRadius = Math.Clamp(radius, 0.005f, 0.2f);
            _pinchRecapture = true;
        }
    }

    /// <summary>Moves the hand; the held cloth follows at a limited speed.</summary>
    public void MovePinch(Vector3 target)
    {
        lock (_pinchGate)
            _pinchTarget = target;
    }

    public void EndPinch()
    {
        lock (_pinchGate)
        {
            _pinchRelease = true;
            _pinchRequest = null;
        }
    }

    /// <summary>Start of a frame: act on what the user did since the last one.</summary>
    private void UpdatePinch()
    {
        Vector3? request;
        float radius;
        bool recapture;
        lock (_pinchGate)
        {
            if (_pinchRelease)
            {
                _pinched = Array.Empty<int>();
                _pinchRelease = false;
                _pinchRecapture = false;
                _pinchGarment = -1;
            }
            request = _pinchRequest;
            radius = _pinchRadius;
            recapture = _pinchRecapture;
            _pinchRequest = null;
            _pinchRecapture = false;
        }
        if (request is { } point)
        {
            CapturePinch(point, radius, snapToCloth: true);
            return;
        }
        if (recapture && _pinched.Length > 0)
            CapturePinch(_pinchHand, radius, snapToCloth: false);
    }

    private void CapturePinch(Vector3 point, float radius, bool snapToCloth)
    {
        var x = Positions;
        int garment = snapToCloth ? -1 : _pinchGarment;
        int nearest = -1;
        float best = PinchPickDistance * PinchPickDistance;
        for (int i = 0; i < x.Length; i++)
        {
            if (_invMass[i] == 0)
                continue;
            if (garment >= 0 && _garmentOfPiece[_pieceOf[i]] != garment)
                continue;
            float d = Vector3.DistanceSquared(x[i], point);
            if (d < best)
            {
                best = d;
                nearest = i;
            }
        }
        if (nearest < 0)
            return;
        if (garment < 0)
            garment = _pinchGarment = _garmentOfPiece[_pieceOf[nearest]];

        // Everything within the radius comes along, fading out towards the rim so the cloth is not creased there.
        var centre = snapToCloth ? x[nearest] : _pinchHand;
        var particles = new List<int>();
        var strength = new List<float>();
        var offsets = new List<Vector3>();
        for (int i = 0; i < x.Length; i++)
        {
            if (_invMass[i] == 0 || _garmentOfPiece[_pieceOf[i]] != garment)
                continue;
            float d = Vector3.Distance(x[i], centre);
            if (d >= radius && i != nearest)
                continue;
            float falloff = MathF.Max(0f, 1f - d / radius);
            particles.Add(i);
            strength.Add(i == nearest ? 1f : falloff * falloff);
            offsets.Add(x[i] - centre);
        }
        if (snapToCloth)
            _pinchHand = centre;
        _pinched = particles.ToArray();
        _pinchStrength = strength.ToArray();
        _pinchOffset = offsets.ToArray();
    }

    /// <summary>Every substep: draw the held particles towards the hand.</summary>
    private void ApplyPinch(float h)
    {
        if (_pinched.Length == 0)
            return;
        Vector3 target;
        lock (_pinchGate)
            target = _pinchTarget;
        var toTarget = target - _pinchHand;
        float distance = toTarget.Length();
        float step = PinchMaxSpeed * h;
        _pinchHand = distance <= step ? target : _pinchHand + toTarget * (step / distance);

        var x = Positions;
        for (int k = 0; k < _pinched.Length; k++)
        {
            int i = _pinched[k];
            x[i] += (_pinchHand + _pinchOffset[k] - x[i]) * _pinchStrength[k];
        }
    }
}
