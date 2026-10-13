# Seamstress

A 3D garment creation tool with cloth simulation: draw flat pattern pieces, sew them together, and drape
them on an avatar. Written in C#, with a Qt Quick interface through
[Qt Bridge for C#](https://github.com/qt/qtbridge-csharp).

## What it does

- **2D pattern view** – draw pieces (polygon, rectangle, ellipse), move them, edit points, curve edges, and
  sew edges together with the Sew tool. Seams are colour-coded, with dots marking the ends that meet.
- **3D view** – shows the sewn pieces on an avatar. Orbit, pan, zoom, click to select.
- **Simulation** – the Simulate button sews and drapes the *active* garment only. Other garments stay as
  they are unless you link them (⇄ in the layers panel) or tick **All garments**.
- **Undo / redo** for every edit, including a whole simulation run as one step.
- **Properties** – fabric physics per piece (weight, stretch, shear, bend, damping, friction, thickness,
  with presets such as Cotton, Silk, Denim), 3D arrangement, material, UVs.
- **Layers** – garments, nested groups and pieces, with visibility, rename, grouping.
- **Topology** – *Particle distance* sets how many faces a garment has; pieces can override it.
- **UVs and materials** – a UV layout view, texel density per piece, a checker preview in 3D, colour and
  texture per material.
- **Files** – projects are readable JSON (`*.garment.json`, see [samples](samples)).
- **Export** – OBJ, glTF 2.0 (`.glb` / `.gltf`), FBX (binary 7.4, written from scratch — no Autodesk SDK),
  and **Send to MakeHuman**.
- **Rendering** – the 3D view is GPU accelerated and falls back to CPU rendering when it has to.

## Building

Requirements:

- .NET SDK 8 or newer
- Qt 6 with the Quick 3D, Shader Tools and Quick Timeline modules (developed against Qt 6.11.3)
- CMake, Ninja and a C++ compiler — the Qt bridge uses them behind the scenes; there is no C++ in this
  repository

One way to get Qt without the online installer:

```bash
pipx run --spec aqtinstall aqt install-qt linux desktop 6.11.3 linux_gcc_64 -O ~/Qt -m qtquick3d qtshadertools qtquicktimeline
```

Build and run:

```bash
dotnet build -p:QtDir=$HOME/Qt/6.11.3/gcc_64     # QtDir may be omitted if Qt is at exactly that path
src/Seamstress.App/bin/Debug/net8.0/Seamstress
src/Seamstress.App/bin/Debug/net8.0/Seamstress samples/tshirt-and-skirt.garment.json
```

Qt Bridge for C# is still a beta (0.4), so expect to adjust the package version in
`src/Seamstress.App/Seamstress.App.csproj` as it evolves.

## A first garment

1. **Garment ▸ Add T-Shirt** puts four pieces in the 2D view, already sewn, and arranges them around the avatar.
2. Press **Simulate** (or Space). The pieces are drawn together along their seams, then gravity takes over.
   Press it again to stop. Ctrl+Z undoes the whole run; **Reset** returns the pieces to their flat arrangement.
3. Select a piece and change its fabric under **Fabric Physics**, or drag **Particle distance** to change the
   face count, then simulate again.

To make your own: draw pieces with **Polygon / Rectangle / Ellipse**, pick **Sew** and click two edges, place
the pieces around the body with **3D Arrangement** (position, rotation, and *Wrap* for sleeves and legs), then
simulate. If a seam twists, select it and use **Reverse Direction**.

| Key | Action | Key | Action |
| --- | --- | --- | --- |
| V | Select | Space | Start / stop simulation |
| E | Edit points | Ctrl+Z / Ctrl+Shift+Z | Undo / redo |
| S | Sew | Delete | Delete selection |
| P / R / O | Polygon / Rectangle / Ellipse | Ctrl+D | Duplicate |
| U | Toggle UV layout | Ctrl+G | Group selected pieces |
| F | Frame all in 2D | Esc / Enter | Cancel / finish the current tool |

## MakeHuman

**File ▸ Preferences** links Seamstress to MakeHuman: its user data folder (the one containing
`data/clothes`) and its base mesh (`data/3dobjs/base.obj`). *Detect Automatically* looks in the usual places.

To make clothes for MakeHuman characters:

1. **Avatar ▸ MakeHuman Base Mesh** — garments are fitted to this body.
2. Build and simulate the garment.
3. **File ▸ Export ▸ Send to MakeHuman** writes `<name>.mhclo`, `.obj` and `.mhmat` into
   `<user data>/data/clothes/<name>/`. Every vertex is bound to the base mesh, so the garment follows the
   character when its body shape changes. It appears in MakeHuman under *Geometries ▸ Clothes* (restart
   MakeHuman if it is running).

The built-in mannequin has the base mesh's proportions, so the bundled templates fit both.

## Renderer

The 3D view uses Qt Quick 3D on the GPU. It switches to CPU software rendering when no GPU driver is found,
when the GPU renderer failed to start on the previous launch, or when asked to (`--cpu`, or
*Preferences ▸ Renderer*). `--gpu` forces the GPU. The active renderer is shown in the corner of the 3D view.

## Project file format

Plain JSON, lengths in centimetres for patterns and metres for 3D placement:

```json
{
  "format": "seamstress-garment",
  "garments": [
    { "name": "Scarf",
      "particleDistance": 2,
      "pieces": [
        { "name": "Strip",
          "points": [ { "at": [0, 0] }, { "at": [120, 0] }, { "at": [120, 20] }, { "at": [0, 20], "curve": [-5, 10] } ] }
      ] }
  ]
}
```

`curve` is the control point that bends the edge leaving that point. A seam joins `edge` *n* of one piece
(the edge starting at point *n*, counting from 0) to an edge of another. Anything left out gets a default,
and the optional `drape` block (the simulated shape) can be deleted to return a piece to its flat placement.

## Layout

| Path | Contents |
| --- | --- |
| `src/Seamstress.Core` | Everything that is not UI: document model, meshing, cloth solver, undo history, file formats, exporters. No Qt dependency. |
| `src/Seamstress.App` | The Qt Quick application: QML views and the controller that connects them to the core. |
| `tests/Seamstress.Tests` | Unit tests (`dotnet test`). |
| `samples` | Example projects. |

`Seamstress --smoke --screenshot <folder>/shot.png` runs an automated pass over the interface (draw, sew,
edit, simulate, save, export, reopen) and prints the result.

## Known limits

- Cloth-to-cloth collision is a simple particle-based option (off by default); layered garments can pass
  through each other.
- Meshes are triangles only; there is no quad remeshing.
- Pieces are placed in 3D with numbers in the properties panel — there is no drag gizmo in the 3D view yet.
- FBX export is binary only and carries geometry, UVs, normals and materials (no skinning or animation).
- Built and tested on Linux; Qt Bridge also targets Windows and macOS, but those were not tried.
