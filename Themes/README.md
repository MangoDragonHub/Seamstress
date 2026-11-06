# Themes

`example-theme.json` is a complete custom theme you can copy and edit. Load it with
**File ▸ Preferences ▸ Themes ▸ Load Theme…**; it is then remembered as the **Custom** theme.

Seamstress puts these two files in its own Themes folder, next to its logs, when it starts and they are
missing (Windows: `Documents\Seamstress\Themes`; Linux: `~/.config/Seamstress/Themes`). It never overwrites
them, but a deleted one comes back, so save your own themes under a different name. **Save Theme…**,
**Load Theme…** and **Open Themes Folder** in Preferences all use that folder.

## File format

```json
{
  "name": "My Theme",
  "colors": { "window": "#2e2a27", "highlight": "#d98a3a" }
}
```

- `name` is shown in Preferences.
- `colors` maps role names (below) to `"#rrggbb"` or `"#rgb"` values.
- Roles you leave out use the Default theme's colour, so a theme can change just a few.
- Unknown names are ignored. A value that is not a colour makes the whole file be rejected.

A quick way to start: pick the preset closest to what you want, press **Save Theme…**, and edit that file.

## Roles

### Interface (Qt palette)

| Role | Used for |
| --- | --- |
| `window` | Background of windows, panels and dialogs |
| `windowText` | Text on `window` |
| `base` | Background of text fields, lists and the layers panel |
| `alternateBase` | Alternating rows in lists |
| `text` | Text on `base` |
| `button` | Button background |
| `buttonText` | Text on buttons |
| `highlight` | Selection, checked tools, slider fill, focus |
| `highlightedText` | Text on `highlight` |
| `mid` | Separators and control borders |
| `dark` | Darker bevels and frames |
| `light` | Lighter bevels and frames |
| `midlight` | Between `mid` and `light` |
| `shadow` | Drop shadows |
| `placeholderText` | Hint text in empty fields |
| `toolTipBase` | Tooltip background |
| `toolTipText` | Tooltip text |
| `brightText` | Text that must stand out on dark or coloured backgrounds |
| `link` | Links |

### 2D and 3D views

| Role | Used for |
| --- | --- |
| `viewerBg` | Background of the 3D view (the 2D view uses a darker shade of `window`) |
| `gridMinor` | Fine grid lines |
| `gridMajor` | The stronger grid line every few squares |
| `gridAxis` | The grid lines through the origin |
| `checkerA`, `checkerB` | The two squares of the checker behind the 2D view's texture (UV) layout |
| `checkerEdge` | Outline of the texture square in that layout |
| `ghostFill`, `ghostStroke` | The avatar silhouette in the 2D view |
| `edgeActive` | Outlines of pieces in the active garment |
| `edgeIdle` | Outlines of other pieces |
| `labelActive` | Piece names in the active garment |
| `labelDim` | Other piece names |
| `pointFill` | Inside of the point handles |
| `overlayText` | The 2D view's scale bar and its label |
| `annotation` | Measurement label outlines, the highlight on a hovered edge, curve handles |
| `annotationDeep` | Dashed lines to the curve handles |
| `annotationFill` | Text of measurement labels |

### Messages

| Role | Used for |
| --- | --- |
| `warningText` | Warnings, e.g. "the avatar is not the MakeHuman base mesh" |
| `dangerText` | Errors, and clashing keys on the Keyboard page |
| `infoText` | The simulation read-out in the status bar |
