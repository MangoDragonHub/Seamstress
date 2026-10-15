namespace Seamstress.Core;

/// <summary>A command that can be given keyboard shortcuts, with the keys it has until the user changes them.</summary>
public sealed record KeyCommand(string Id, string Group, string Name, string[] Defaults);

/// <summary>
/// Every command with a keyboard shortcut, and the user's changes to them. Keys are written the way Qt reads them,
/// e.g. "Ctrl+Shift+S", "Delete" or "Num+5", with the modifiers in the order Ctrl, Alt, Shift, Meta, Num.
/// </summary>
public static class KeyBindings
{
    public static readonly IReadOnlyList<KeyCommand> Commands = new KeyCommand[]
    {
        new("file.new", "File", "New", new[] { "Ctrl+N" }),
        new("file.open", "File", "Open", new[] { "Ctrl+O" }),
        new("file.save", "File", "Save", new[] { "Ctrl+S" }),
        new("file.saveAs", "File", "Save As", new[] { "Ctrl+Shift+S" }),
        new("file.preferences", "File", "Preferences", new[] { "Ctrl+," }),
        new("file.quit", "File", "Quit", new[] { "Ctrl+Q" }),

        new("edit.undo", "Edit", "Undo", new[] { "Ctrl+Z" }),
        new("edit.redo", "Edit", "Redo", new[] { "Ctrl+Shift+Z", "Ctrl+Y" }),
        new("edit.selectAll", "Edit", "Select All", new[] { "Ctrl+A" }),
        new("edit.duplicate", "Edit", "Duplicate", new[] { "Ctrl+D" }),
        new("edit.delete", "Edit", "Delete", new[] { "Delete", "Backspace" }),
        new("edit.mirror", "Edit", "Mirror Piece", Array.Empty<string>()),
        new("edit.rotateLeft", "Edit", "Rotate 90° Left", Array.Empty<string>()),
        new("edit.rotateRight", "Edit", "Rotate 90° Right", Array.Empty<string>()),
        new("edit.straighten", "Edit", "Straighten Curved Edges", Array.Empty<string>()),
        new("edit.invertNormals", "Edit", "Invert Normals", Array.Empty<string>()),
        new("edit.group", "Edit", "Group Selected Pieces", new[] { "Ctrl+G" }),
        new("edit.reverseSeam", "Edit", "Reverse Seam Direction", Array.Empty<string>()),

        new("tool.select", "Tools", "Select", new[] { "V" }),
        new("tool.editPoints", "Tools", "Edit Points", new[] { "A" }),
        new("tool.transformPoints", "Tools", "Transform Points", new[] { "T" }),
        new("tool.sew", "Tools", "Sew", new[] { "S" }),
        new("tool.polygon", "Tools", "Polygon", new[] { "P" }),
        new("tool.rectangle", "Tools", "Rectangle", new[] { "B" }),
        new("tool.ellipse", "Tools", "Ellipse", new[] { "O" }),
        new("tool.ruler", "Tools", "Ruler", new[] { "M" }),
        new("tool.confirm", "Tools", "Finish (polygon)", new[] { "Return", "Enter" }),
        new("tool.cancel", "Tools", "Cancel", new[] { "Escape" }),

        new("handles.full", "Handles", "Move, Rotate and Scale", new[] { "Q" }),
        new("handles.move", "Handles", "Move", new[] { "W" }),
        new("handles.rotate", "Handles", "Rotate", new[] { "E" }),
        new("handles.scale", "Handles", "Scale", new[] { "R" }),

        new("view.uv", "View", "Pattern / UV Layout", new[] { "U" }),
        new("view.frame", "View", "Frame All in 2D", new[] { "F" }),
        new("view.ortho", "View", "3D: Orthographic", new[] { "5", "Num+5" }),
        new("view.front", "View", "3D: Front", new[] { "1", "Num+1" }),
        new("view.side", "View", "3D: Side", new[] { "3", "Num+3" }),
        new("view.back", "View", "3D: Back", new[] { "Ctrl+1", "Ctrl+Num+1" }),
        new("view.checker", "View", "3D: Checker", Array.Empty<string>()),
        new("view.wireframe", "View", "3D: Wireframe", Array.Empty<string>()),
        new("view.normals", "View", "3D: Normal Direction", Array.Empty<string>()),
        new("view.pinch", "View", "3D: Pinch Cloth", new[] { "G" }),
        new("view.handles", "View", "3D: Show Handles", Array.Empty<string>()),
        new("view.avatar", "View", "3D: Show Avatar", Array.Empty<string>()),

        new("sim.toggle", "Simulation", "Simulate / Stop", new[] { "Space" }),
        new("sim.reset", "Simulation", "Reset Arrangement", Array.Empty<string>()),
    };

    public static KeyCommand? Find(string id) => Commands.FirstOrDefault(c => c.Id == id);

    /// <summary>The keys a command answers to: the user's choice if they made one, otherwise the defaults.</summary>
    public static string[] For(AppSettings settings, string id)
        => settings.Shortcuts.TryGetValue(id, out var keys) ? keys : Find(id)?.Defaults ?? Array.Empty<string>();

    private static readonly string[] ModifierOrder = { "Ctrl", "Alt", "Shift", "Meta", "Num" };

    /// <summary>
    /// Writes a key the same way whatever order or case it was typed in ("shift+ctrl+s" → "Ctrl+Shift+S"), so two
    /// spellings of one key are seen to be the same. Empty for an empty or unreadable key.
    /// </summary>
    public static string Normalize(string key)
    {
        key = key.Trim();
        if (key.Length == 0)
            return "";
        // The last part is the key itself, which may be "+" ("Ctrl++").
        string main;
        string rest;
        if (key.EndsWith("++"))
        {
            main = "+";
            rest = key[..^2];
        }
        else if (key == "+")
        {
            return "+";
        }
        else
        {
            int split = key.LastIndexOf('+');
            main = key[(split + 1)..];
            rest = split < 0 ? "" : key[..split];
        }
        var modifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in rest.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string name = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => "Ctrl",
                "alt" => "Alt",
                "shift" => "Shift",
                "meta" or "super" or "win" => "Meta",
                "num" or "keypad" => "Num",
                _ => "",
            };
            if (name.Length == 0)
                return "";
            modifiers.Add(name);
        }
        main = main.Trim();
        if (main.Length == 0)
            return "";
        main = main.Length == 1 ? main.ToUpperInvariant() : char.ToUpperInvariant(main[0]) + main[1..].ToLowerInvariant();
        main = main switch
        {
            "Esc" => "Escape",
            "Del" => "Delete",
            "Pgup" => "PgUp",
            "Pgdown" => "PgDown",
            _ => main,
        };
        var ordered = ModifierOrder.Where(modifiers.Contains).Append(main);
        return string.Join('+', ordered);
    }

    /// <summary>
    /// Stores a full set of keys, keeping only the commands whose keys differ from their defaults. Unknown commands
    /// and empty keys are dropped.
    /// </summary>
    public static void Save(AppSettings settings, IReadOnlyDictionary<string, string[]> keys)
    {
        settings.Shortcuts.Clear();
        foreach (var command in Commands)
        {
            if (!keys.TryGetValue(command.Id, out var chosen))
                continue;
            var clean = chosen.Select(Normalize).Where(k => k.Length > 0).Distinct().ToArray();
            if (!clean.SequenceEqual(command.Defaults))
                settings.Shortcuts[command.Id] = clean;
        }
    }

    /// <summary>Keys given to more than one command, with the names of those commands.</summary>
    public static IReadOnlyList<(string Key, IReadOnlyList<string> Commands)> Conflicts(IReadOnlyDictionary<string, string[]> keys)
    {
        var owners = new Dictionary<string, List<string>>();
        foreach (var command in Commands)
        {
            if (!keys.TryGetValue(command.Id, out var chosen))
                continue;
            foreach (string key in chosen.Select(Normalize).Where(k => k.Length > 0).Distinct())
            {
                if (!owners.TryGetValue(key, out var list))
                    owners[key] = list = new List<string>();
                list.Add(command.Name);
            }
        }
        return owners.Where(o => o.Value.Count > 1).Select(o => (o.Key, (IReadOnlyList<string>)o.Value)).ToList();
    }
}
