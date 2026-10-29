using Seamstress.Core;

namespace Seamstress.App;

/// <summary>
/// Theming: the colour roles the interface binds to ("theme" + the role name, e.g. <see cref="ThemeWindow"/>),
/// the four presets, and custom themes loaded from or saved to a file. Preferences previews a theme live and the
/// choice is stored in the settings; <see cref="RevertTheme"/> puts back the saved one when the dialog is closed
/// without saving.
/// </summary>
public partial class AppController
{
    private Dictionary<string, string> _theme = Theme.Preset("default");
    private string _themeChoice = "default";
    private Dictionary<string, string> _customTheme = new();
    private string _customThemeName = "";

    /// <summary>Called from the constructor once the settings are loaded.</summary>
    private void InitTheme()
    {
        RestoreFromSettings();
        try
        {
            foreach (string path in Theme.InstallExamples(ThemesFolder))
                AppLog.Write($"Wrote {path}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Could not write the example theme to {ThemesFolder}: {e.Message}");
        }
    }

    /// <summary>The Themes folder beside the logs, holding the example theme; theme files are loaded from and saved to it.</summary>
    public string ThemesFolder => AppSettings.ThemesFolder;

    public string ThemesFolderUrl => ViewportSync.ToUrl(ThemesFolder);

    /// <summary>Shows the Themes folder in the system's file manager.</summary>
    public void OpenThemesFolder()
    {
        try
        {
            Directory.CreateDirectory(ThemesFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ThemesFolder) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Fail(Lang.T("Could not open the themes folder."), e);
        }
    }

    /// <summary>The preset the interface shows ("default", "light", "hcdark", "hclight" or "custom").</summary>
    public string ThemeChoice => _themeChoice;

    /// <summary>Whether a custom theme has been loaded, so Preferences can offer it.</summary>
    public bool HasCustomTheme => _customTheme.Count > 0;

    /// <summary>Name of the loaded custom theme, from its file.</summary>
    public string CustomThemeName => _customThemeName;

    /// <summary>Shows a preset, or the loaded custom theme, without storing the choice.</summary>
    public void PreviewTheme(string key)
    {
        if (key == "custom")
        {
            if (!HasCustomTheme)
                return;
            _themeChoice = "custom";
            ApplyTheme(_customTheme);
            return;
        }
        _themeChoice = Theme.IsPreset(key) ? key : "default";
        ApplyTheme(Theme.Preset(_themeChoice));
    }

    /// <summary>Stores the previewed theme. Called when Preferences is accepted.</summary>
    public void SaveTheme(string key)
    {
        PreviewTheme(key);
        _settings.Theme = _themeChoice;
        if (_themeChoice == "custom")
        {
            _settings.CustomTheme = new Dictionary<string, string>(_customTheme);
            _settings.CustomThemeName = _customThemeName;
        }
        TrySaveSettings();
    }

    /// <summary>Puts back the saved theme. Called when Preferences is closed without saving.</summary>
    public void RevertTheme() => RestoreFromSettings();

    /// <summary>Loads a custom theme from a file and shows it right away; the file's roles may be partial.</summary>
    public void LoadThemeFile(string path)
    {
        try
        {
            if (!Theme.TryParse(File.ReadAllText(PathFromUrl(path)), out string name, out var colors, out var problem))
            {
                Fail(problem switch
                {
                    Theme.FileProblem.NotJson => Lang.T("This is not a Seamstress theme file."),
                    Theme.FileProblem.BadColor => Lang.T("A colour in this theme file is not written as #rrggbb."),
                    _ => Lang.T("This theme file has no colours in it."),
                });
                return;
            }
            _customTheme = colors;
            _customThemeName = name;
            Notify(nameof(HasCustomTheme));
            Notify(nameof(CustomThemeName));
            PreviewTheme("custom");
            StatusText = Lang.F("Theme “{0}” loaded.", _customThemeName == "" ? FileName(path) : _customThemeName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Fail(Lang.T("Could not read the theme file."), e);
        }
    }

    /// <summary>Writes the shown theme to a file, to keep it or to share it.</summary>
    public void SaveThemeFile(string path)
    {
        string name = _themeChoice == "custom" && _customThemeName != ""
            ? _customThemeName
            : Lang.T(PresetNames[_themeChoice is "custom" ? "default" : _themeChoice]);
        try
        {
            File.WriteAllText(PathFromUrl(path), Theme.ToJson(name, _theme));
            StatusText = Lang.F("Theme saved to {0}.", FileName(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Fail(Lang.T("Could not write the theme file."), e);
        }
    }

    private static readonly Dictionary<string, string> PresetNames = new()
    {
        ["default"] = "Default",
        ["light"] = "Light",
        ["hcdark"] = "High Contrast Dark",
        ["hclight"] = "High Contrast Light",
    };

    private void RestoreFromSettings()
    {
        var custom = new Dictionary<string, string>();
        foreach (string role in Theme.Roles)
            custom[role] = Theme.ColorOr(_settings.CustomTheme, role, Theme.DefaultColor(role));
        _customTheme = _settings.CustomTheme.Any(c => Theme.Roles.Contains(c.Key) && Theme.IsColor(c.Value)) ? custom : new();
        _customThemeName = _settings.CustomThemeName;
        if (_settings.Theme == "custom" && HasCustomTheme)
            _themeChoice = "custom";
        else
            _themeChoice = Theme.IsPreset(_settings.Theme) ? _settings.Theme : "default";
        ApplyTheme(_themeChoice == "custom" ? _customTheme : Theme.Preset(_themeChoice));
        Notify(nameof(ThemeChoice));
        Notify(nameof(HasCustomTheme));
        Notify(nameof(CustomThemeName));
    }

    private void ApplyTheme(Dictionary<string, string> colors)
    {
        _theme = colors;
        Notify(nameof(ThemeChoice));
        Notify(nameof(ThemeWindow));
        Notify(nameof(ThemeWindowText));
        Notify(nameof(ThemeBase));
        Notify(nameof(ThemeAlternateBase));
        Notify(nameof(ThemeText));
        Notify(nameof(ThemeButton));
        Notify(nameof(ThemeButtonText));
        Notify(nameof(ThemeHighlight));
        Notify(nameof(ThemeHighlightedText));
        Notify(nameof(ThemeMid));
        Notify(nameof(ThemeDark));
        Notify(nameof(ThemeLight));
        Notify(nameof(ThemeMidlight));
        Notify(nameof(ThemeShadow));
        Notify(nameof(ThemePlaceholderText));
        Notify(nameof(ThemeToolTipBase));
        Notify(nameof(ThemeToolTipText));
        Notify(nameof(ThemeBrightText));
        Notify(nameof(ThemeLink));
        Notify(nameof(ThemeViewerBg));
        Notify(nameof(ThemeGridMinor));
        Notify(nameof(ThemeGridMajor));
        Notify(nameof(ThemeGridAxis));
        Notify(nameof(ThemeCheckerA));
        Notify(nameof(ThemeCheckerB));
        Notify(nameof(ThemeCheckerEdge));
        Notify(nameof(ThemeGhostFill));
        Notify(nameof(ThemeGhostStroke));
        Notify(nameof(ThemeEdgeActive));
        Notify(nameof(ThemeEdgeIdle));
        Notify(nameof(ThemeLabelActive));
        Notify(nameof(ThemeLabelDim));
        Notify(nameof(ThemePointFill));
        Notify(nameof(ThemeOverlayText));
        Notify(nameof(ThemeAnnotation));
        Notify(nameof(ThemeAnnotationDeep));
        Notify(nameof(ThemeAnnotationFill));
        Notify(nameof(ThemeWarningText));
        Notify(nameof(ThemeDangerText));
        Notify(nameof(ThemeInfoText));
    }

    private string Role(string role) => _theme.TryGetValue(role, out var value) ? value : Theme.DefaultColor(role);

    // The Qt palette roles, read by Main.qml into the window palette.
    public string ThemeWindow => Role("window");
    public string ThemeWindowText => Role("windowText");
    public string ThemeBase => Role("base");
    public string ThemeAlternateBase => Role("alternateBase");
    public string ThemeText => Role("text");
    public string ThemeButton => Role("button");
    public string ThemeButtonText => Role("buttonText");
    public string ThemeHighlight => Role("highlight");
    public string ThemeHighlightedText => Role("highlightedText");
    public string ThemeMid => Role("mid");
    public string ThemeDark => Role("dark");
    public string ThemeLight => Role("light");
    public string ThemeMidlight => Role("midlight");
    public string ThemeShadow => Role("shadow");
    public string ThemePlaceholderText => Role("placeholderText");
    public string ThemeToolTipBase => Role("toolTipBase");
    public string ThemeToolTipText => Role("toolTipText");
    public string ThemeBrightText => Role("brightText");
    public string ThemeLink => Role("link");

    // The view colours, used directly by the two canvases and the panels.
    public string ThemeViewerBg => Role("viewerBg");
    public string ThemeGridMinor => Role("gridMinor");
    public string ThemeGridMajor => Role("gridMajor");
    public string ThemeGridAxis => Role("gridAxis");
    public string ThemeCheckerA => Role("checkerA");
    public string ThemeCheckerB => Role("checkerB");
    public string ThemeCheckerEdge => Role("checkerEdge");
    public string ThemeGhostFill => Role("ghostFill");
    public string ThemeGhostStroke => Role("ghostStroke");
    public string ThemeEdgeActive => Role("edgeActive");
    public string ThemeEdgeIdle => Role("edgeIdle");
    public string ThemeLabelActive => Role("labelActive");
    public string ThemeLabelDim => Role("labelDim");
    public string ThemePointFill => Role("pointFill");
    public string ThemeOverlayText => Role("overlayText");
    public string ThemeAnnotation => Role("annotation");
    public string ThemeAnnotationDeep => Role("annotationDeep");
    public string ThemeAnnotationFill => Role("annotationFill");
    public string ThemeWarningText => Role("warningText");
    public string ThemeDangerText => Role("dangerText");
    public string ThemeInfoText => Role("infoText");
}
