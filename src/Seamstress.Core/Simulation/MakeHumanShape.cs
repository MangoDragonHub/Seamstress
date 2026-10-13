using System.Globalization;
using System.Numerics;
using Seamstress.Core.Model;

namespace Seamstress.Core.Simulation;

/// <summary>
/// Reshapes the MakeHuman base mesh with MakeHuman's own "macro" targets, the files behind the Gender, Age, Muscle,
/// Weight, Height and Proportions sliders on its Main modelling tab. The base mesh file on its own is MakeHuman's
/// average young woman; every other body, including the neutral default, is that mesh plus a weighted mix of targets.
/// </summary>
public static class MakeHumanShape
{
    /// <summary>The targets folder that belongs to a base mesh (data/3dobjs/base.obj -> data/targets), if present.</summary>
    public static string? TargetsFolderFor(string baseObjPath)
    {
        string? data = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(baseObjPath)));
        if (data == null)
            return null;
        string folder = Path.Combine(data, "targets", "macrodetails");
        return Directory.Exists(folder) ? folder : null;
    }

    /// <summary>
    /// How strongly each named component of a target applies, following MakeHuman's formulas. A target file such as
    /// "universal-male-young-averagemuscle-maxweight" is weighted by the product of its components' values.
    /// </summary>
    internal static Dictionary<string, float> Factors(AvatarSettings shape)
    {
        float gender = Math.Clamp(shape.Gender, 0, 1), age = Math.Clamp(shape.Age, 0, 1);
        float muscle = Math.Clamp(shape.Muscle, 0, 1), weight = Math.Clamp(shape.Weight, 0, 1);
        float height = Math.Clamp(shape.Height, 0, 1), proportions = Math.Clamp(shape.Proportions, 0, 1);

        float baby, child, young, old;
        if (age < 0.5f)
        {
            old = 0;
            baby = MathF.Max(0, 1 - age * 5.333f);
            young = MathF.Max(0, (age - 0.1875f) * 3.2f);
            child = MathF.Max(0, MathF.Min(1, 5.333f * age) - young);
        }
        else
        {
            baby = child = 0;
            old = MathF.Max(0, age * 2 - 1);
            young = 1 - old;
        }

        static (float Min, float Average, float Max) Split(float v)
        {
            float max = MathF.Max(0, v * 2 - 1), min = MathF.Max(0, 1 - v * 2);
            return (min, 1 - (max + min), max);
        }
        var m = Split(muscle);
        var w = Split(weight);
        var h = Split(height);
        var p = Split(proportions);

        return new Dictionary<string, float>
        {
            ["male"] = gender, ["female"] = 1 - gender,
            ["baby"] = baby, ["child"] = child, ["young"] = young, ["old"] = old,
            ["minmuscle"] = m.Min, ["averagemuscle"] = m.Average, ["maxmuscle"] = m.Max,
            ["minweight"] = w.Min, ["averageweight"] = w.Average, ["maxweight"] = w.Max,
            ["minheight"] = h.Min, ["maxheight"] = h.Max,
            ["uncommonproportions"] = p.Min, ["idealproportions"] = p.Max,
            // MakeHuman's default character is an even mix of its three ethnic base shapes.
            ["african"] = 1f / 3, ["asian"] = 1f / 3, ["caucasian"] = 1f / 3,
        };
    }

    /// <summary>
    /// Moves the base mesh vertices to the requested body shape. Returns how many target files contributed;
    /// zero means the targets folder held nothing usable and the mesh is unchanged.
    /// </summary>
    public static int Apply(Vector3[] vertices, string macroTargetsFolder, AvatarSettings shape)
    {
        var factors = Factors(shape);
        int applied = 0;
        foreach (string folder in new[] { macroTargetsFolder, Path.Combine(macroTargetsFolder, "height"), Path.Combine(macroTargetsFolder, "proportions") })
        {
            if (!Directory.Exists(folder))
                continue;
            foreach (string file in Directory.EnumerateFiles(folder, "*.target"))
            {
                float weight = 1;
                bool known = false;
                foreach (string part in Path.GetFileNameWithoutExtension(file).Split('-'))
                {
                    if (factors.TryGetValue(part, out float factor))
                    {
                        weight *= factor;
                        known = true;
                    }
                    else if (part != "universal")
                    {
                        // A component this code does not model: leave the target out rather than guess.
                        weight = 0;
                    }
                }
                if (!known || weight < 1e-4f)
                    continue;

                bool any = false;
                foreach (string line in File.ReadLines(file))
                {
                    if (line.Length == 0 || line[0] == '#')
                        continue;
                    var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length < 4 || !int.TryParse(fields[0], out int index) || index < 0 || index >= vertices.Length)
                        continue;
                    if (float.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float dx)
                        && float.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float dy)
                        && float.TryParse(fields[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float dz))
                    {
                        vertices[index] += new Vector3(dx, dy, dz) * weight;
                        any = true;
                    }
                }
                if (any)
                    applied++;
            }
        }
        return applied;
    }
}
