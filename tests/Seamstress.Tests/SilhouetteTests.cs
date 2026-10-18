using System.Numerics;
using Seamstress.Core.Simulation;
using Xunit;

namespace Seamstress.Tests;

public class SilhouetteTests
{
    private static TriMesh Mesh(Vector2[] points, params int[] triangles) => new()
    {
        Positions = points.Select(p => new Vector3(p, 0)).ToArray(),
        Triangles = triangles,
    };

    private static float Area(Vector2[] loop)
    {
        float sum = 0;
        for (int i = 0; i < loop.Length; i++)
        {
            var a = loop[i];
            var b = loop[(i + 1) % loop.Length];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return MathF.Abs(sum / 2);
    }

    private static (Vector2 Min, Vector2 Max) Bounds(Vector2[] loop)
    {
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var p in loop)
        {
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p);
        }
        return (min, max);
    }

    [Fact]
    public void FlatSquareComesOutAsItsFace()
    {
        // 10 x 20 cm; mesh coordinates are metres.
        var mesh = Mesh(
            new[] { new Vector2(0, 0), new Vector2(0.1f, 0), new Vector2(0.1f, 0.2f), new Vector2(0, 0.2f) },
            0, 1, 2, 0, 2, 3);
        var loop = Assert.Single(Silhouette.FrontOutline(mesh));
        var (min, max) = Bounds(loop);
        Assert.InRange(min.X, -0.75f, 0.75f);
        Assert.InRange(min.Y, -0.75f, 0.75f);
        Assert.InRange(max.X, 9.25f, 10.75f);
        Assert.InRange(max.Y, 19.25f, 20.75f);
        Assert.InRange(Area(loop), 190f, 210f);
    }

    [Fact]
    public void RingLeavesItsGapOpen()
    {
        // A 20 cm square with a 10 cm square cut from the middle, as a ring of eight triangles.
        var mesh = Mesh(
            new[]
            {
                new Vector2(0, 0), new Vector2(0.2f, 0), new Vector2(0.2f, 0.2f), new Vector2(0, 0.2f),
                new Vector2(0.05f, 0.05f), new Vector2(0.15f, 0.05f), new Vector2(0.15f, 0.15f), new Vector2(0.05f, 0.15f),
            },
            0, 1, 5, 0, 5, 4,
            1, 2, 6, 1, 6, 5,
            2, 3, 7, 2, 7, 6,
            3, 0, 4, 3, 4, 7);
        var loops = Silhouette.FrontOutline(mesh);
        Assert.Equal(2, loops.Count);
        Assert.InRange(Area(loops[0]), 380f, 420f);
        Assert.InRange(Area(loops[1]), 90f, 110f);
    }

    [Fact]
    public void DisconnectedPiecesComeOutSeparately()
    {
        var mesh = Mesh(
            new[]
            {
                new Vector2(0, 0), new Vector2(0.1f, 0), new Vector2(0.1f, 0.1f), new Vector2(0, 0.1f),
                new Vector2(0.3f, 0), new Vector2(0.4f, 0), new Vector2(0.4f, 0.1f), new Vector2(0.3f, 0.1f),
            },
            0, 1, 2, 0, 2, 3,
            4, 5, 6, 4, 6, 7);
        var loops = Silhouette.FrontOutline(mesh);
        Assert.Equal(2, loops.Count);
        foreach (var loop in loops)
            Assert.InRange(Area(loop), 90f, 110f);
    }

    [Fact]
    public void MannequinStandsOnTheGroundAndFacesItsHeight()
    {
        var avatar = Avatar.CreateMannequin();
        var loop = Assert.Single(Silhouette.FrontOutline(avatar.Mesh));
        var (min, max) = Bounds(loop);
        Assert.InRange(max.Y, 164f, 170f);
        Assert.InRange(min.Y, -1f, 1f);
        Assert.InRange(max.X - min.X, 100f, 140f);
        Assert.InRange(MathF.Abs(min.X + max.X), 0f, 3f);
    }

    [Fact]
    public void CoarseGridKeepsTheShape()
    {
        var mesh = Mesh(new[] { new Vector2(0, 0), new Vector2(0.4f, 0), new Vector2(0.2f, 0.4f) }, 0, 1, 2);
        foreach (float cell in new[] { 0.25f, 1f, 4f })
        {
            var loop = Assert.Single(Silhouette.FrontOutline(mesh, cell));
            var (min, max) = Bounds(loop);
            Assert.InRange(max.X - min.X, 39f - cell, 40.5f + cell);
            Assert.InRange(max.Y, 40f - cell, 40.5f + cell);
            // A triangle: half of 40 x 40, with the edge tolerances above.
            Assert.InRange(Area(loop), 800f * 0.9f, 800f * 1.1f);
        }
    }
}
