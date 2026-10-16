# Seamstress

A 3D garment creation tool with cloth simulation: draw flat pattern pieces, sew them together, and drape
them on an avatar. Written in C#, with a Qt Quick interface through
[Qt Bridge for C#](https://github.com/qt/qtbridge-csharp).

**[User guide](docs/USER_GUIDE.md)** — a walk-through with screenshots, including notes for people coming from
Marvelous Designer.

## What it does

- **2D pattern view** – draw pieces (polygon, rectangle, ellipse), move them, edit points, curve edges, and
  sew edges together with the Sew tool. Seams are colour-coded, with dots marking the ends that meet. **Cut
  Hole** (right-click menu) cuts one selected piece in the shape of another lying on it, for pocket openings
  and the like; the cut-out becomes a new piece, and the hole's edges can be sewn like any other.
- **3D view** – shows the sewn pieces on an avatar. Orbit, pan, zoom, click to select, and drag the arrows and
  rings on a selected piece to move and rotate it. **Pinch** lets you drag the cloth itself, during or after
  simulation, to tug bunched-up areas into place. **Ortho** switches to an orthographic camera. **View ▸ 3D:
  Normal Direction** colours the front and back of every face, and **Invert Normals** (Properties) turns a
  piece that faces the wrong way.
- **Measuring** – click an edge to read its length, or use the **Ruler** to measure between any two points
  in the pattern or on the cloth and avatar in 3D.
- **Keyboard** – every shortcut can be changed in Preferences, and right-clicking either view opens the Edit
  menu.
- **Languages** – English, Spanish, Brazilian Portuguese, French, Simplified Chinese and Japanese, chosen under
  *Preferences ▸ General* and switched without restarting.
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
| Build for 64-bit ARM | `--arch arm64` | `-Arch arm64` |

### Linux

You need the .NET SDK 8 or newer (9.0.200 or newer is recommended: the solution file is `.slnx`, which older SDKs
cannot read, so the scripts then build the projects one by one), CMake, Ninja, a C++ compiler and Qt 6 with Qt Quick 3D.
(There is no C++ in this repository; the Qt bridge compiles a small native launcher behind the scenes.) On Debian/Ubuntu:

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

### Linux on ARM64 (Raspberry Pi 4/5, RK3399 and other boards)

The ARM64 build needs a 64-bit distribution with glibc 2.38 or newer, because that is what Qt 6.11 for ARM64 is
built against: Raspberry Pi OS based on Debian 13 "Trixie" (64-bit), Ubuntu 24.04 or newer, or Armbian/Debian 13
on an RK3399 board. Raspberry Pi OS "Bookworm" (Debian 12) is too old, and 32-bit systems are not supported.

There are two ways to make it:

- **On the board itself**, exactly as above: `./build.sh --install-qt` downloads Qt for ARM64 into
  `~/Qt/6.11.3/gcc_arm64`, and every command, including `test` and `run`, works there.
- **On an x86_64 Linux PC**, with podman or docker installed (`sudo apt install podman`):

  ```bash
  ./build.sh --arch arm64 appimage --install-qt
  ```

  The first run creates a build container (Ubuntu 24.04 with a cross-compiler and the ARM64 libraries, about
  1.7 GB) and downloads Qt for ARM64 next to the x86_64 Qt, whose tools the build uses. Nothing is emulated, so
  it takes about as long as a normal build. `test` and `run` need the board.

The packages are named for the processor: `Seamstress-linux-arm64/` and its `.tar.gz`,
`Seamstress-aarch64.AppImage` and `Seamstress-aarch64.flatpak`. Cross-building the Flatpak needs no ARM64 runtime
on the PC; on the board, `flatpak install --user Seamstress-aarch64.flatpak` fetches
`org.freedesktop.Platform//24.08` from Flathub as usual.

Seamstress draws with OpenGL, which the Pi's VideoCore GPU and the RK3399's Mali GPU (through Mesa's Panfrost
driver) provide. If the GPU renderer does not start, Seamstress switches to the CPU renderer on the next launch
by itself (see *Renderer*), and `--cpu` forces it. The cloth simulation runs on the CPU either way, so expect
draping to be noticeably slower than on a desktop PC.

### Windows

Install the [.NET SDK 9.0.200 or newer](https://dotnet.microsoft.com/download) (8.x also works, see [Linux](#linux)),
then in PowerShell:

```powershell
.\build.ps1 run
```

On 64-bit Windows the bridge package ships its own Qt, so nothing else is needed. If Visual Studio's C++ tools
are installed the bridge uses them; if not, the script switches to the bridge's prebuilt launcher.
`.\build.ps1 package` produces `dist\Seamstress-win-x64.zip`; the unzipped app needs the .NET 8 runtime installed.
If PowerShell refuses to run the script, start it with `powershell -ExecutionPolicy Bypass -File build.ps1`.

**Windows on ARM** (Snapdragon laptops, Surface Pro X and similar): the bridge does not bring Qt for ARM64, so
add `-InstallQt` to download it into `~\Qt` (or pass `-Qt <prefix>`):

```powershell
.\build.ps1 package -InstallQt                # on the ARM64 PC itself
.\build.ps1 package -Arch arm64 -InstallQt    # on an x64 PC
```

Both produce `dist\Seamstress-win-arm64.zip`, which needs the ARM64 .NET 8 runtime on the target. Cross-building
on an x64 PC uses Qt's *MSVC 2022 ARM64 (cross-compiled)* build together with the x64 MSVC 2022 Qt of the same
version for its tools, and Visual Studio's *MSVC ARM64 build tools* component if Visual Studio is installed
(without it the bridge's prebuilt ARM64 launcher is used). The x64 `Seamstress-win-x64.zip` also runs on Windows
11 on ARM, through its x64 emulation.

> The Windows path, ARM64 included, has not been run yet: it was written against the bridge's Windows packages
> without a Windows machine to try it on. `.github/workflows/build.yml` builds, tests and packages on both Windows
> and Linux, for x64 and ARM64, on every push, which is the quickest way to find out what, if anything, needs
> adjusting.

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
| M | Ruler (2D and 3D) | 5 | Orthographic 3D view on / off |
| 1 / 3 / Ctrl+1 | 3D front / side / back | Right-click | Edit menu |

Every key can be changed under **File ▸ Preferences ▸ Keyboard**. Double-click a piece in the 2D view to select
all of its points.

## MakeHuman

**File ▸ Preferences ▸ MakeHuman** links Seamstress to MakeHuman: its user data folder (the one containing
`data/clothes`) and its base mesh (`data/3dobjs/base.obj`). *Detect Automatically* looks in the usual places.

To make clothes for MakeHuman characters:

1. **Avatar ▸ MakeHuman Base Mesh** — garments are fitted to this body. **MakeHuman Body Shape…** changes its
   gender, age, muscle, weight, height and proportions with MakeHuman's own shape data.
2. Build and simulate the garment.
3. **File ▸ Export ▸ Send to MakeHuman** writes `<name>.mhclo`, `.obj` and `.mhmat` into
   `<user data>/data/clothes/<name>/`. Every vertex is bound to the base mesh, so the garment follows the
   character when its body shape changes. It appears in MakeHuman under *Geometries ▸ Clothes* (restart
   MakeHuman if it is running). A dialog lets you choose the asset name first; the author and license written into
   the asset are set under *Preferences ▸ MakeHuman*.

The built-in mannequin has the base mesh's proportions, so the bundled templates fit both.

## Renderer

The 3D view uses Qt Quick 3D on the GPU. It switches to CPU software rendering when no GPU driver is found,
when the GPU renderer failed to start on the previous launch, or when asked to (`--cpu`, or
*Preferences ▸ Viewport ▸ Renderer*). `--gpu` forces the GPU. The active renderer is shown in the corner of the 3D view.

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
(the edge starting at point *n*, counting from 0) to an edge of another. A piece's optional `holes` are written
like its `points`, run clockwise, and number their points on from the outline's. Anything left out gets a default,
and the optional `drape` block (the simulated shape) can be deleted to return a piece to its flat placement.

## Layout

| Path | Contents |
| --- | --- |
| `src/Seamstress.Core` | Everything that is not UI: document model, meshing, cloth solver, undo history, file formats, exporters, translations. No Qt dependency. |
| `src/Seamstress.App` | The Qt Quick application: QML views and the controller that connects them to the core. |
| `tests/Seamstress.Tests` | Unit tests (`dotnet test`). |
| `samples` | Example projects. |
| `packaging` | Desktop entry, AppImage launcher and Flatpak manifest used by `build.sh`, and the container recipe and CMake toolchains for building on x64 for ARM64. |
| `build.sh`, `build.ps1` | Build, test, run and package. |

`Seamstress --smoke --screenshot <folder>/shot.png` runs an automated pass over the interface (draw, sew,
edit, change topology, move with the 3D handles, simulate, save, export, reopen) and prints the result.

## Translations

Interface texts are written in English in the source: `tr("…")` in QML and `Lang.T("…")` / `Lang.F("…", …)` in
C#. Each language has a catalogue in `src/Seamstress.Core/Localization/<code>.json` that maps every English text
to its translation; a text that has no entry is shown in English. `%1` (QML) and `{0}` (C#) placeholders must be
kept, and a text can be prefixed with a context such as `view|Front` where one English word needs two
translations. `LocalizationTests` collects every marked text from the source and fails if any catalogue is
missing one, has one the source no longer uses, or changes a placeholder. To add a language, add it to
`Lang.Languages` and add its catalogue.

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
- The translations have not yet been checked by native speakers. The file dialogs and colour picker come from Qt
  and the desktop, so they follow the system language rather than the one chosen in Preferences.
