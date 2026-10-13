using System.Numerics;
using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.IO;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace Seamstress.Tests;

public class AvatarFixture
{
    public Avatar Mannequin { get; } = Avatar.CreateMannequin();
}

public class SimulationTests : IClassFixture<AvatarFixture>
{
    private readonly Avatar _avatar;
    private readonly ITestOutputHelper _output;

    public SimulationTests(AvatarFixture fixture, ITestOutputHelper output)
    {
        _avatar = fixture.Mannequin;
        _output = output;
    }

    /// <summary>Set SEAMSTRESS_DUMP to a folder to keep the simulated results for visual inspection.</summary>
    private static string? DumpFolder => Environment.GetEnvironmentVariable("SEAMSTRESS_DUMP");

    [Fact]
    public void MannequinFieldMatchesItsSurface()
    {
        var sdf = _avatar.Sdf;
        Assert.True(sdf.Sample(new Vector3(0, 1.2f, 0.045f), out _) < -0.03f, "chest centre is inside");
        Assert.True(sdf.Sample(new Vector3(0, 1.2f, 0.28f), out var front) > 0.08f, "in front of the chest is outside");
        Assert.True(front.Z > 0.8f, "the field points away from the body");
        Assert.True(sdf.Sample(new Vector3(0.6f, 1.6f, 0), out _) > 0.1f);

        // Surface triangles face outwards and lie on the zero level of the field.
        var mesh = _avatar.Mesh;
        Assert.True(mesh.Triangles.Length > 3000);
        int outward = 0, total = 0;
        for (int t = 0; t < mesh.Triangles.Length; t += 3 * 17)
        {
            var a = mesh.Positions[mesh.Triangles[t]];
            var b = mesh.Positions[mesh.Triangles[t + 1]];
            var c = mesh.Positions[mesh.Triangles[t + 2]];
            var n = Vector3.Cross(b - a, c - a);
            if (n.Length() < 1e-10f)
                continue;
            var centre = (a + b + c) / 3;
            float d = sdf.Sample(centre, out var g);
            Assert.InRange(d, -0.006f, 0.006f);
            total++;
            if (Vector3.Dot(Vector3.Normalize(n), g) > 0.3f)
                outward++;
        }
        Assert.True(outward > total * 0.97, $"{outward}/{total} triangles face outwards");
    }

    private (Workspace, SimulationController) Setup(params string[] templates)
    {
        var ws = new Workspace();
        foreach (var t in templates)
            ws.AddTemplate(t);
        return (ws, new SimulationController(ws, () => _avatar));
    }

    // Fixed frame counts keep the tests independent of wall-clock time.
    private static void Run(SimulationController sim, int frames) => sim.RunFrames(frames);

    private void Check(Workspace ws, Garment garment, float minY)
    {
        var mesh = ws.MeshFor(garment);
        var positions = mesh.Pieces.Select(m => Mesher.CurrentPositions(garment.FindPiece(m.PieceId)!, m)).ToList();
        float lowest = float.MaxValue, deepest = float.MaxValue;
        var gaps = mesh.Stitches.Select(s => Vector3.Distance(positions[s.PieceA][s.VertexA], positions[s.PieceB][s.VertexB])).OrderBy(g => g).ToList();
        foreach (var piece in positions)
        {
            foreach (var p in piece)
            {
                Assert.False(float.IsNaN(p.X + p.Y + p.Z), "simulation produced NaN");
                lowest = MathF.Min(lowest, p.Y);
                deepest = MathF.Min(deepest, _avatar.Sdf.Sample(p, out _));
            }
        }
        // A stitch whose two ends rest on opposite sides of a body ridge (the shoulder line) can stay a little open,
        // so judge the seams by almost all stitches rather than the single worst one.
        float typical = gaps[(int)(gaps.Count * 0.9f)], worst = gaps[^1];
        _output.WriteLine($"{garment.Name}: seam gap 90% {typical * 100:0.00} cm, worst {worst * 100:0.00} cm, lowest {lowest:0.00} m, deepest penetration {deepest * 1000:0.0} mm");
        Assert.True(typical < 0.005f, $"seams should close (90% of stitches within {typical * 100:0.0} cm)");
        Assert.True(worst < 0.03f, $"no stitch should stay wide open ({worst * 100:0.0} cm)");
        Assert.True(lowest > minY, $"garment should stay on the body (lowest point {lowest:0.00} m)");
        Assert.True(deepest > -0.004f, $"cloth should not sink into the body ({deepest * 1000:0.0} mm)");
    }

    private void Dump(Workspace ws, string name)
    {
        if (DumpFolder is not { } folder)
            return;
        Directory.CreateDirectory(folder);
        var scene = ExportScene.Build(ws.Document, ws.MeshFor, new ExportOptions());
        ObjExporter.Export(scene, Path.Combine(folder, name + ".obj"));
        var body = new System.Text.StringBuilder();
        foreach (var p in _avatar.Mesh.Positions)
            body.Append(System.Globalization.CultureInfo.InvariantCulture, $"v {p.X} {p.Y} {p.Z}\n");
        for (int t = 0; t < _avatar.Mesh.Triangles.Length; t += 3)
            body.Append($"f {_avatar.Mesh.Triangles[t] + 1} {_avatar.Mesh.Triangles[t + 1] + 1} {_avatar.Mesh.Triangles[t + 2] + 1}\n");
        File.WriteAllText(Path.Combine(folder, "mannequin.obj"), body.ToString());
    }

    [Theory]
    [InlineData("Tank Top", 1.0f)]
    [InlineData("T-Shirt", 1.0f)]
    [InlineData("Skirt", 0.25f)]
    [InlineData("Dress", 0.25f)]
    public void TemplateDrapesOnMannequin(string template, float minY)
    {
        var (ws, sim) = Setup(template);
        Assert.Null(sim.Start(false));
        Run(sim, 360);
        sim.Stop();
        Dump(ws, template.Replace(" ", "").Replace("-", ""));
        Check(ws, ws.Document.Garments[0], minY - 0.25f);
        Assert.Equal("Simulate " + ws.Document.Garments[0].Name, ws.History.UndoName);
    }

    [Fact]
    public void OnlyTheActiveGarmentIsSimulated()
    {
        var (ws, sim) = Setup("Tank Top", "Skirt");
        var top = ws.Document.Garments[0];
        var skirt = ws.Document.Garments[1];
        ws.SelectLayer(skirt.Id);
        Assert.Equal(new[] { skirt }, sim.GarmentsToSimulate(false));
        sim.Start(false);
        Run(sim, 30);
        sim.Stop();
        Assert.All(top.Pieces, p => Assert.Null(p.Drape));
        Assert.All(skirt.Pieces, p => Assert.NotNull(p.Drape));

        // Opting the top in, or asking for everything, widens the scope.
        ws.SetSimulateWithOthers(top.Id, true);
        Assert.Equal(2, sim.GarmentsToSimulate(false).Count);
        ws.SetSimulateWithOthers(top.Id, false);
        Assert.Equal(2, sim.GarmentsToSimulate(true).Count);

        // Undoing the run restores the flat arrangement in one step.
        ws.History.Undo(); ws.History.Undo(); ws.History.Undo();
        Assert.All(ws.Document.Garments[1].Pieces, p => Assert.Null(p.Drape));
    }

    [Fact]
    public void EditingStopsARunningSimulation()
    {
        var (ws, sim) = Setup("Skirt");
        sim.Start(false);
        Assert.True(sim.IsRunning);
        ws.SetPieceValue("bend", 80);
        Assert.False(sim.IsRunning);
        Assert.Equal(80, ws.Document.Garments[0].Pieces[0].Physics.Bend);
    }

    [Fact]
    public void StifferFabricSagsLess()
    {
        float Sag(float bend)
        {
            // A strip held along one edge by a seam to a tiny fixed anchor would need pins; instead compare how far
            // an unsupported skirt hem flares away from the legs: stiff cloth stands out more than limp cloth.
            var (ws, sim) = Setup("Skirt");
            foreach (var p in ws.Document.Garments[0].Pieces)
                p.Physics.Bend = bend;
            sim.Start(false);
            Run(sim, 240);
            sim.Stop();
            var garment = ws.Document.Garments[0];
            var mesh = ws.MeshFor(garment);
            float radius = 0;
            int count = 0;
            for (int i = 0; i < mesh.Pieces.Count; i++)
            {
                var pos = Mesher.CurrentPositions(garment.Pieces[i], mesh.Pieces[i]);
                foreach (int v in mesh.Pieces[i].EdgeChains[0])
                {
                    radius += new Vector2(pos[v].X, pos[v].Z).Length();
                    count++;
                }
            }
            return radius / count;
        }
        float limp = Sag(2), stiff = Sag(98);
        _output.WriteLine($"hem radius limp {limp:0.000} m, stiff {stiff:0.000} m");
        Assert.True(stiff > limp + 0.005f);
    }

    [Fact]
    public void GarmentsDrapeOnTheMakeHumanBaseMesh()
    {
        var settings = new Seamstress.Core.AppSettings();
        settings.DetectMakeHuman();
        if (!File.Exists(settings.MakeHumanBaseMesh))
            return; // MakeHuman is not installed on this machine
        var avatar = Avatar.FromMakeHumanBase(settings.MakeHumanBaseMesh);
        var (min, max) = avatar.Mesh.Bounds();
        _output.WriteLine($"MakeHuman body bounds {min} .. {max}, {avatar.Mesh.Triangles.Length / 3} triangles");
        Assert.InRange(min.Y, -0.001f, 0.001f);
        float chest = max.Y * 0.72f;
        Assert.True(avatar.Sdf.Sample(new Vector3(0, chest, 0), out _) < -0.02f, "inside the chest");
        Assert.True(avatar.Sdf.Sample(new Vector3(0, chest, 0.4f), out _) > 0.05f, "in front of the body");
        Assert.True(avatar.Sdf.Sample(new Vector3(0.3f, max.Y * 0.3f, 0.2f), out _) > 0.05f, "beside the legs");

        foreach (string template in new[] { "Skirt", "T-Shirt" })
        {
            var ws = new Workspace();
            ws.AddTemplate(template);
            var sim = new SimulationController(ws, () => avatar);
            sim.Start(false);
            sim.RunFrames(360);
            sim.Stop();
            if (DumpFolder is { } folder)
            {
                ObjExporter.Export(ExportScene.Build(ws.Document, ws.MeshFor, new ExportOptions()), Path.Combine(folder, "mh_" + template.Replace("-", "") + ".obj"));
                var body = new System.Text.StringBuilder();
                foreach (var p in avatar.Mesh.Positions)
                    body.Append(System.Globalization.CultureInfo.InvariantCulture, $"v {p.X} {p.Y} {p.Z}\n");
                for (int t = 0; t < avatar.Mesh.Triangles.Length; t += 3)
                    body.Append($"f {avatar.Mesh.Triangles[t] + 1} {avatar.Mesh.Triangles[t + 1] + 1} {avatar.Mesh.Triangles[t + 2] + 1}\n");
                File.WriteAllText(Path.Combine(folder, "mh_body.obj"), body.ToString());
            }
            var garment = ws.Document.Garments[0];
            var mesh = ws.MeshFor(garment);
            float deepest = float.MaxValue, lowest = float.MaxValue;
            foreach (var m in mesh.Pieces)
            {
                foreach (var p in Mesher.CurrentPositions(garment.FindPiece(m.PieceId)!, m))
                {
                    deepest = MathF.Min(deepest, avatar.Sdf.Sample(p, out _));
                    lowest = MathF.Min(lowest, p.Y);
                }
            }
            _output.WriteLine($"{template} on MakeHuman: lowest {lowest:0.00} m, deepest {deepest * 1000:0.0} mm");
            Assert.True(deepest > -0.004f);
            Assert.True(lowest > 0.2f, "the garment should stay on the body");
        }
    }
}
