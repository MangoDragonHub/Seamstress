using System.Numerics;
using Seamstress.Core.Model;

namespace Seamstress.Core.Simulation;

public sealed partial class ClothSolver
{
    private sealed class CollisionFormState
    {
        public required CollisionForm Form { get; init; }
        public required int Garment { get; init; }
        public required Matrix4x4 Rotation { get; init; }
        public required Matrix4x4 InverseRotation { get; init; }
        public required Vector3 Axis { get; init; }
        public required float Friction { get; init; }
    }

    private readonly List<CollisionFormState> _collisionForms = new();

    private void SetCollisionForms(IReadOnlyList<CollisionForm>? forms, IReadOnlyDictionary<string, int> garmentIds)
    {
        _collisionForms.Clear();
        if (forms == null)
            return;
        foreach (var form in forms)
        {
            if (!form.Collides)
                continue;
            int garment = -1;
            if (!string.IsNullOrEmpty(form.GarmentId))
            {
                if (!garmentIds.TryGetValue(form.GarmentId, out garment))
                    continue;
            }
            var rotation = CollisionForm.RotationMatrix(form.Rotation);
            _collisionForms.Add(new CollisionFormState
            {
                Form = form,
                Garment = garment,
                Rotation = rotation,
                InverseRotation = Matrix4x4.Transpose(rotation),
                Axis = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, rotation)),
                Friction = Math.Clamp(form.Friction, 0, 100) / 100f * 1.5f,
            });
        }
    }

    private bool SolveCollisionForms()
    {
        if (_collisionForms.Count == 0)
            return false;
        var x = Positions;
        bool moved = false;
        for (int i = 0; i < x.Length; i++)
        {
            if (_invMass[i] == 0)
                continue;
            int garment = _garmentOfPiece[_pieceOf[i]];
            foreach (var form in _collisionForms)
            {
                if (form.Garment >= 0 && form.Garment != garment)
                    continue;
                if (!PushOut(form, ref x[i], _thickness[i], out var normal, out float depth))
                    continue;
                ApplyFriction(i, normal, depth, form.Friction);
                moved = true;
            }
        }
        return moved;
    }

    private static bool PushOut(CollisionFormState state, ref Vector3 point, float thickness, out Vector3 normal, out float depth)
    {
        var form = state.Form;
        switch (form.Shape)
        {
            case CollisionShape.Capsule:
                return PushOutCapsule(state, ref point, thickness, out normal, out depth);
            case CollisionShape.Box:
                return PushOutBox(state, ref point, thickness, out normal, out depth);
            default:
                return PushOutSphere(state, ref point, thickness, out normal, out depth);
        }
    }

    private static bool PushOutSphere(CollisionFormState state, ref Vector3 point, float thickness, out Vector3 normal, out float depth)
    {
        var form = state.Form;
        var delta = point - form.Position;
        float distance = delta.Length();
        float target = form.Size.X * 0.5f + thickness;
        if (distance >= target)
        {
            normal = Vector3.Zero;
            depth = 0;
            return false;
        }
        normal = distance > 1e-6f ? delta / distance : Vector3.UnitY;
        depth = target - distance;
        point = form.Position + normal * target;
        return true;
    }

    private static bool PushOutCapsule(CollisionFormState state, ref Vector3 point, float thickness, out Vector3 normal, out float depth)
    {
        var form = state.Form;
        float radius = form.Size.X * 0.5f;
        float half = MathF.Max(0f, form.Size.Y * 0.5f - radius);
        var start = form.Position - state.Axis * half;
        var end = form.Position + state.Axis * half;
        var closest = ClosestPointOnSegment(point, start, end);
        var delta = point - closest;
        float distance = delta.Length();
        float target = radius + thickness;
        if (distance >= target)
        {
            normal = Vector3.Zero;
            depth = 0;
            return false;
        }
        if (distance > 1e-6f)
        {
            normal = delta / distance;
        }
        else
        {
            var side = MathF.Abs(state.Axis.X) < 0.9f ? Vector3.UnitX : Vector3.UnitZ;
            normal = Vector3.Normalize(Vector3.Cross(state.Axis, side));
        }
        depth = target - distance;
        point = closest + normal * target;
        return true;
    }

    private static bool PushOutBox(CollisionFormState state, ref Vector3 point, float thickness, out Vector3 normal, out float depth)
    {
        var form = state.Form;
        var half = form.Size * 0.5f;
        var local = Vector3.Transform(point - form.Position, state.InverseRotation);
        var closest = Vector3.Clamp(local, -half, half);
        var delta = local - closest;
        if (delta.LengthSquared() > 1e-12f)
        {
            float distance = delta.Length();
            if (distance >= thickness)
            {
                normal = Vector3.Zero;
                depth = 0;
                return false;
            }
            var localNormal = delta / distance;
            local = closest + localNormal * thickness;
            depth = thickness - distance;
            normal = Vector3.TransformNormal(localNormal, state.Rotation);
        }
        else
        {
            var toFace = half - Vector3.Abs(local);
            int axis = toFace.X <= toFace.Y && toFace.X <= toFace.Z ? 0 : toFace.Y <= toFace.Z ? 1 : 2;
            var localNormal = axis switch
            {
                0 => new Vector3(local.X < 0 ? -1 : 1, 0, 0),
                1 => new Vector3(0, local.Y < 0 ? -1 : 1, 0),
                _ => new Vector3(0, 0, local.Z < 0 ? -1 : 1),
            };
            local[axis] = localNormal[axis] * (half[axis] + thickness);
            depth = toFace[axis] + thickness;
            normal = Vector3.TransformNormal(localNormal, state.Rotation);
        }
        point = form.Position + Vector3.Transform(local, state.Rotation);
        return true;
    }

    private static Vector3 ClosestPointOnSegment(Vector3 point, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float lengthSquared = ab.LengthSquared();
        if (lengthSquared < 1e-12f)
            return a;
        float t = Math.Clamp(Vector3.Dot(point - a, ab) / lengthSquared, 0f, 1f);
        return a + ab * t;
    }
}
