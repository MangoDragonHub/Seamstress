# Seamstress

A 3D garment creation tool with cloth simulation: draw flat pattern pieces, sew them together, and drape
them on an avatar. Written in C#, with a Qt Quick interface through
[Qt Bridge for C#](https://github.com/qt/qtbridge-csharp).

**[User guide](docs/USER_GUIDE.md)** — a walk-through with screenshots, including notes for people coming from
Marvelous Designer.

## What it does

- **2D pattern view** – draw pieces (polygon, rectangle, ellipse), move them, edit points, curve edges, and
  sew edges together with the Sew tool. Seams are colour-coded, with dots marking the ends that meet.
- **3D view** – shows the sewn pieces on an avatar. Orbit, pan, zoom, click to select, and drag the arrows and
  rings on a selected piece to move and rotate it. **Pinch** lets you drag the cloth itself, during or after
  simulation, to tug bunched-up areas into place.
- **Simulation** – the Simulate button sews and drapes the *active* garment only. Other garments stay as
  they are unless you link them (⇄ in the layers panel) or tick **All garments**. Cloth collides with the
  avatar, with itself and with the other garments, so a shirt settles over a skirt instead of through it.
- **Undo / redo** for every edit, including a whole simulation run as one step.
- **Properties** – fabric physics per piece (weight, stretch, shear, bend, damping, friction, thickness,
  with presets such as Cotton, Silk, Denim), 3D arrangement, material, UVs.
- **Layers** – garments, nested groups and pieces, with visibility, rename, grouping.
- **Topology** – *Particle distance* sets how many faces a garment has (pieces can override it), and each
  garment can be meshed as triangles or as quads.
- **UVs and materials** – a UV layout view, texel density per piece, a checker preview in 3D, colour and
  texture per material.
- **Files** – projects are readable JSON (`*.garment.json`, see [samples](samples)).
- **Export** – OBJ, glTF 2.0 (`.glb` / `.gltf`), FBX (binary 7.4, written from scratch — no Autodesk SDK),
  and **Send to MakeHuman**.
- **Rendering** – the 3D view is GPU accelerated and falls back to CPU rendering when it has to.

## Building

There is one script per platform; both take the same commands.

| | Linux (and macOS) | Windows |
| --- | --- | --- |
| Build | `./build.sh` | `.\build.ps1` |
| Build and run the tests | `./build.sh test` | `.\build.ps1 test` |
| Build and start the app | `./build.sh run` | `.\build.ps1 run` |
| Package | `./build.sh package` / `appimage` / `flatpak` | `.\build.ps1 package` |
| Remove build output | `./build.sh clean` | `.\build.ps1 clean` |

### Linux

You need the .NET SDK 8 or newer, CMake, Ninja, a C++ compiler and Qt 6 with Qt Quick 3D. (There is no C++
in this repository; the Qt bridge compiles a small native launcher behind the scenes.) On Debian/Ubuntu:

```bash
sudo apt install dotnet-sdk-8.0 cmake ninja-build build-essential libgl1-mesa-dev libxkbcommon-dev libxcb-cursor0
./build.sh --install-qt      # first time: also downloads Qt 6.11.3 into ~/Qt (about 1.7 GB)
./build.sh run
```

`--install-qt` uses [aqtinstall](https://github.com/miurahr/aqtinstall) through `uvx`, `pipx` or a Python virtual
environment, whichever is available. If you already have Qt (for example from the Qt online installer), pass
`--qt <prefix>` or set `QtDir`; the prefix is the folder containing `lib/cmake/Qt6`. Distribution Qt packages
are usually split across folders in a way the bridge cannot use.

Packages, all written to `dist/`:

- `./build.sh package` — `Seamstress-linux-x64/` and a `.tar.gz` of it. Self-contained: Qt and the .NET runtime
  are inside. Start it with `./seamstress.sh`.
- `./build.sh appimage` — `Seamstress-x86_64.AppImage`, a single file to download and run.
- `./build.sh flatpak` — `Seamstress.flatpak`; install with `flatpak install --user dist/Seamstress.flatpak`.
  Needs `flatpak-builder` and the runtimes
  `flatpak install flathub org.freedesktop.Platform//24.08 org.freedesktop.Sdk//24.08`.

An AppImage or tarball runs on distributions at least as new as the one it was built on, so build releases on an
older distribution (the CI workflow uses Ubuntu 22.04).

### Windows

Install the [.NET SDK 8 or newer](https://dotnet.microsoft.com/download), then in PowerShell:

```powershell
.\build.ps1 run
```

On 64-bit Windows the bridge package ships its own Qt, so nothing else is needed. If Visual Studio's C++ tools
are installed the bridge uses them; if not, the script switches to the bridge's prebuilt launcher.
`.\build.ps1 package` produces `dist\Seamstress-win-x64.zip`; the unzipped app needs the .NET 8 runtime installed.
If PowerShell refuses to run the script, start it with `powershell -ExecutionPolicy Bypass -File build.ps1`.

> The Windows path has not been run yet: it was written against the bridge's Windows package without a Windows
> machine to try it on. `.github/workflows/build.yml` builds, tests and packages on both Windows and Linux on
> every push, which is the quickest way to find out what, if anything, needs adjusting.

### Without the scripts

```bash
dotnet build -p:QtDir=$HOME/Qt/6.11.3/gcc_64
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

To layer garments, simulate the inner one first (a skirt), then select and simulate the outer one (a shirt):
the finished garment stays put and the new one drapes over it.

| Key | Action | Key | Action |
| --- | --- | --- | --- |
| V | Select | Space | Start / stop simulation |
| A | Edit points | Ctrl+Z / Ctrl+Shift+Z | Undo / redo |
| S | Sew | Delete | Delete selection |
| P / B / O | Polygon / Rectangle / Ellipse | Ctrl+D | Duplicate |
| U | Toggle UV layout | Ctrl+G | Group selected pieces |
| F | Frame all in 2D | Esc / Enter | Cancel / finish the current tool |
| T | Transform points | Q / W / E / R | Handles: all / move / rotate / scale |

## MakeHuman

**File ▸ Preferences** links Seamstress to MakeHuman: its user data folder (the one containing
`data/clothes`) and its base mesh (`data/3dobjs/base.obj`). *Detect Automatically* looks in the usual places.

To make clothes for MakeHuman characters:

1. **Avatar ▸ MakeHuman Base Mesh** — garments are fitted to this body. **MakeHuman Body Shape…** changes its
   gender, age, muscle, weight, height and proportions with MakeHuman's own shape data.
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
| `packaging` | Desktop entry, AppImage launcher and Flatpak manifest used by `build.sh`. |
| `build.sh`, `build.ps1` | Build, test, run and package. |

`Seamstress --smoke --screenshot <folder>/shot.png` runs an automated pass over the interface (draw, sew,
edit, change topology, move with the 3D handles, simulate, save, export, reopen) and prints the result.

## Known limits

- Cloth collision tests points against faces. That keeps layers and folds apart, but two free edges (hems)
  hanging at the same height can still slip past each other, and it roughly doubles simulation time; it can be
  switched off under *Simulation* in the properties panel.
- Quad topology is quad-dominant, not pure: a grid of quads fills each piece and triangles remain where the
  outline is curved or slanted (typically 3–6 % of the faces). glTF and MakeHuman always receive triangles.
- The 3D handles move and rotate whole pieces; points are still edited in the 2D view only.
- FBX export is binary only and carries geometry, UVs, normals and materials (no skinning or animation).
- Windows: see the note under *Building*. macOS is untried; `build.sh` builds there but does not package.
- The Flatpak cannot start MakeHuman for you after *Send to MakeHuman* (it is sandboxed); the files are still
  written to MakeHuman's folder.
