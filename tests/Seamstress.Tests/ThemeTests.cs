using Seamstress.Core;
using Xunit;

namespace Seamstress.Tests;

public class ThemeTests
{
    [Theory]
    [InlineData("default")]
    [InlineData("light")]
    [InlineData("hcdark")]
    [InlineData("hclight")]
    public void EveryPresetDefinesEveryRoleExactlyOnce(string key)
    {
        var colors = Theme.Preset(key);
        Assert.Equal(Theme.Roles.OrderBy(r => r, StringComparer.Ordinal), colors.Keys.OrderBy(r => r, StringComparer.Ordinal));
        foreach (var role in Theme.Roles)
            Assert.True(Theme.IsColor(colors[role]), $"{key}: {role} is not a colour: {colors[role]}");
    }

    [Fact]
    public void PresetsLookTheWayTheyShould()
    {
        Assert.Equal("#2b2d31", Theme.Preset("default")["window"]);
        Assert.Equal("#24262b", Theme.Preset("default")["viewerBg"]);
        Assert.Equal("#f0f1f3", Theme.Preset("light")["window"]);
        Assert.Equal("#000000", Theme.Preset("hcdark")["window"]);
        Assert.Equal("#ffffff", Theme.Preset("hclight")["window"]);
        // The custom radio in Preferences needs the preset list to match the keys the settings store.
        Assert.Equal(["default", "light", "hcdark", "hclight"], Theme.Presets);
        Assert.True(Theme.IsPreset("light"));
        Assert.False(Theme.IsPreset("custom"));
        Assert.Equal(Theme.Preset("default"), Theme.Preset("no such theme"));
    }

    [Fact]
    public void ThemeFileRoundTrips()
    {
        var colors = Theme.Preset("light");
        colors["window"] = "#123456";
        string json = Theme.ToJson("My theme", colors);
        Assert.True(Theme.TryParse(json, out string name, out var parsed, out var problem));
        Assert.Equal(Theme.FileProblem.None, problem);
        Assert.Equal("My theme", name);
        Assert.Equal(colors, parsed);
    }

    [Fact]
    public void ThemeFileMayLeaveRolesOut()
    {
        Assert.True(Theme.TryParse("""{"colors": {"window": "#123456", "no such role": "#000000"}}""",
            out string name, out var colors, out _));
        Assert.Equal("", name);
        Assert.Equal("#123456", colors["window"]);
        Assert.Equal(Theme.DefaultColor("windowText"), colors["windowText"]);
        Assert.Equal(Theme.Roles.Length, colors.Count);
    }

    [Fact]
    public void ThemeFileDistinguishesTheProblem()
    {
        Theme.TryParse("not json", out _, out _, out var notJson);
        Theme.TryParse("""{"name": "x"}""", out _, out _, out var noColors);
        Theme.TryParse("""{"colors": {"window": "nope"}}""", out _, out _, out var badColor);
        Theme.TryParse("""{"colors": {"only unknown": "#123456"}}""", out _, out _, out var nothingKnown);
        Theme.TryParse("""{"colors": {"window": 7}}""", out _, out _, out var notAString);
        Assert.Equal(Theme.FileProblem.NotJson, notJson);
        Assert.Equal(Theme.FileProblem.NoColors, noColors);
        Assert.Equal(Theme.FileProblem.BadColor, badColor);
        Assert.Equal(Theme.FileProblem.NoColors, nothingKnown);
        Assert.Equal(Theme.FileProblem.BadColor, notAString);
    }

    [Fact]
    public void SettingsKeepTheTheme()
    {
        string path = Path.Combine(Path.GetTempPath(), $"seamstress-theme-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new AppSettings { Theme = "custom", CustomThemeName = "My theme" };
            settings.CustomTheme["window"] = "#123456";
            settings.Save(path);
            var loaded = AppSettings.Load(path);
            Assert.Equal("custom", loaded.Theme);
            Assert.Equal("My theme", loaded.CustomThemeName);
            Assert.Equal("#123456", loaded.CustomTheme["window"]);
            Assert.Equal("default", new AppSettings().Theme);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
