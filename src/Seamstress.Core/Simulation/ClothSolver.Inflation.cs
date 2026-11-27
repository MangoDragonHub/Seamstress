using System.Numerics;

namespace Seamstress.Core.Simulation;

public sealed partial class ClothSolver
{
    private const float MaxInflationPressure = 20f;

    private void ApplyInflation(float h)
    {
        if (!_hasInflation)
            return;
        var x = Positions;
        var centres = new Vector3[_inflation.Length];
        var counts = new int[_inflation.Length];
        foreach (var piece in Pieces)
        {
            if (!piece.Simulated || _inflation[piece.Garment] <= 0)
                continue;
            int offset = piece.Offset;
            for (int i = 0; i < piece.Mesh.VertexCount; i++)
                centres[piece.Garment] += x[offset + i];
            counts[piece.Garment] += piece.Mesh.VertexCount;
        }
        for (int g = 0; g < centres.Length; g++)
            if (counts[g] > 0)
                centres[g] /= counts[g];

        foreach (var piece in Pieces)
        {
            if (!piece.Simulated)
                continue;
            int garment = piece.Garment;
            float inflation = _inflation[garment];
            if (inflation <= 0 || _sewing[garment] > 0)
                continue;
            float pressure = inflation / 100f * MaxInflationPressure;
            int offset = piece.Offset;
            var triangles = piece.Mesh.Triangles;
            for (int t = 0; t < triangles.Length; t += 3)
            {
                int a = offset + triangles[t];
                int b = offset + triangles[t + 1];
                int c = offset + triangles[t + 2];
                var ab = x[b] - x[a];
                var ac = x[c] - x[a];
                var normal = Vector3.Cross(ab, ac);
                float length = normal.Length();
                if (length < 1e-12f)
                    continue;
                normal /= length;
                float area = length * 0.5f;
                var centroid = (x[a] + x[b] + x[c]) / 3f;
                if (Vector3.Dot(normal, centroid - centres[garment]) < 0)
                    normal = -normal;
                var impulse = normal * (pressure * area * h / 3f);
                if (_invMass[a] != 0)
                    _velocity[a] += impulse * _invMass[a];
                if (_invMass[b] != 0)
                    _velocity[b] += impulse * _invMass[b];
                if (_invMass[c] != 0)
                    _velocity[c] += impulse * _invMass[c];
            }
        }
    }
}
