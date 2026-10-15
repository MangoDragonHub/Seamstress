using System.Text.RegularExpressions;
using Seamstress.Core;
using Seamstress.Core.Editing;
using Seamstress.Core.Model;

namespace Seamstress.Tests;

/// <summary>
/// The translation catalogues against the source: every text the interface marks for translation (tr("…") in QML,
/// Lang.T/F/N("…") in C#, and the names that are stored in English) must be translated in every language, with the
/// same placeholders, and a catalogue must not keep texts the source no longer uses.
/// </summary>
public class LocalizationTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Seamstress.Core")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    // A double-quoted string literal; QML (JavaScript) and C# escape the same few characters here.
    private const string Literal = "\"((?:[^\"\\\\]|\\\\.)*)\"";

    private static string Unescape(string s) => Regex.Replace(s, @"\\(.)", m => m.Groups[1].Value switch
    {
        "n" => "\n",
        "t" => "\t",
        var c => c,
    });

    /// <summary>Every English text the interface asks to have translated.</summary>
    internal static SortedSet<string> SourceTexts()
    {
        string root = RepoRoot();
        var texts = new SortedSet<string>(StringComparer.Ordinal);
        var qml = new Regex(@"\btr\(" + Literal + @"\)");
        foreach (string file in Directory.GetFiles(Path.Combine(root, "src", "Seamstress.App"), "*.qml"))
        {
            if (Path.GetFileName(file) == "SmokeTest.qml")
                continue;
            foreach (Match m in qml.Matches(File.ReadAllText(file)))
                texts.Add(Unescape(m.Groups[1].Value));
        }
        var cs = new Regex(@"\bLang\.[TFN]\(\s*" + Literal);
        foreach (string file in Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;
            foreach (Match m in cs.Matches(File.ReadAllText(file)))
                texts.Add(Unescape(m.Groups[1].Value));
        }
        // Stored in English and translated where they are shown.
        foreach (var command in KeyBindings.Commands)
        {
            texts.Add(command.Name);
            texts.Add(command.Group);
        }
        foreach (var preset in FabricPhysics.Presets)
            texts.Add(preset.Preset);
        texts.Add("Custom");
        foreach (string template in Templates.Names)
            texts.Add(template);
        return texts;
    }

    private static IEnumerable<string> Placeholders(string text)
        => Regex.Matches(text, @"%\d|\{\d+(?::[^}]*)?\}").Select(m => m.Value).OrderBy(p => p, StringComparer.Ordinal);

    public static IEnumerable<object[]> Translations() => Lang.Languages.Where(l => l.Code != Lang.English).Select(l => new object[] { l.Code });

    [Theory]
    [MemberData(nameof(Translations))]
    public void EveryTextIsTranslatedWithTheSamePlaceholders(string code)
    {
        var catalog = Lang.Catalog(code);
        var source = SourceTexts();
        var missing = source.Where(t => !catalog.TryGetValue(t, out var v) || string.IsNullOrWhiteSpace(v)).ToList();
        Assert.True(missing.Count == 0, $"{code}: {missing.Count} texts not translated:\n" + string.Join("\n", missing));
        var unused = catalog.Keys.Where(k => !source.Contains(k)).ToList();
        Assert.True(unused.Count == 0, $"{code}: {unused.Count} texts no longer in the source:\n" + string.Join("\n", unused));
        var wrong = source.Where(t => !Placeholders(t).SequenceEqual(Placeholders(catalog[t]))).ToList();
        Assert.True(wrong.Count == 0, $"{code}: placeholders differ in:\n" + string.Join("\n", wrong.Select(t => t + "  →  " + catalog[t])));
    }

    [Fact]
    public void LooksUpTextsInTheChosenLanguage()
    {
        Assert.Equal("Simular", Lang.In("es", "Simulate"));
        Assert.Equal("シミュレート", Lang.In("ja", "Simulate"));
        Assert.Equal("Simulate", Lang.In(Lang.English, "Simulate"));
        // A text without a translation is shown as it is.
        Assert.Equal("No such text", Lang.In("fr", "No such text"));
        // A context before "|" tells apart texts that read the same in English, and is dropped when untranslated.
        Assert.Equal("Front", Lang.In(Lang.English, "view|Front"));
        Assert.NotEqual(Lang.In("ja", "Front"), Lang.In("ja", "view|Front"));
        Assert.Equal(6, Lang.Languages.Count);
        Assert.Equal(Lang.Languages.Select(l => l.Code).Distinct().Count(), Lang.Languages.Count);
    }

    [Fact]
    public void SettingsKeepTheLanguage()
    {
        string path = Path.Combine(Path.GetTempPath(), $"seamstress-lang-{Guid.NewGuid():N}.json");
        try
        {
            Assert.Equal(Lang.English, new AppSettings().Language);
            new AppSettings { Language = "pt" }.Save(path);
            Assert.Equal("pt", AppSettings.Load(path).Language);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
