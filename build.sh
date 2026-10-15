#!/usr/bin/env bash
# Build script for Linux and macOS. On Windows use build.ps1.
#
#   ./build.sh                 build (Release)
#   ./build.sh test            build and run the unit tests
#   ./build.sh run [args]      build and start Seamstress
#   ./build.sh package         self-contained folder + tarball in dist/        (Linux)
#   ./build.sh appimage        dist/Seamstress-<arch>.AppImage                 (Linux)
#   ./build.sh flatpak         dist/Seamstress.flatpak (x86_64), dist/Seamstress-aarch64.flatpak (ARM64)
#   ./build.sh clean           remove build output
#
# Options (before or after the command):
#   --arch <x64|arm64> CPU to build for; the default is this machine's. On an x86_64 PC, arm64 cross-compiles
#                      inside a container (needs podman or docker) for a Raspberry Pi 4/5 or other 64-bit ARM board
#   --debug            build the Debug configuration
#   --qt <dir>         Qt 6 prefix for the target CPU (the folder containing lib/cmake/Qt6);
#                      otherwise $QtDir, then ~/Qt/<version>/gcc_64 or gcc_arm64
#   --host-qt <dir>    when cross-compiling, the x86_64 Qt whose tools (moc, qmlcachegen...) the build runs;
#                      same version as --qt, by default the gcc_64 folder next to it
#   --install-qt       download Qt into ~/Qt with aqtinstall if no Qt is found
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

APP_ID="io.github.mangodragonhub.Seamstress"
QT_VERSION="6.11.3"
QT_MODULES="qtquick3d qtshadertools qtquicktimeline"
DOTNET_CHANNEL="8.0"
CONFIG="Release"
COMMAND="build"
QT=""
HOST_QT=""
ARCH=""
INSTALL_QT=0
RUN_ARGS=()

while [ $# -gt 0 ]; do
    case "$1" in
        build|test|run|package|appimage|flatpak|clean|stage) COMMAND="$1"; shift
            if [ "$COMMAND" = "run" ]; then RUN_ARGS=("$@"); break; fi ;;
        --arch) ARCH="$2"; shift 2 ;;
        --debug) CONFIG="Debug"; shift ;;
        --release) CONFIG="Release"; shift ;;
        --qt) QT="$2"; shift 2 ;;
        --host-qt) HOST_QT="$2"; shift 2 ;;
        --install-qt) INSTALL_QT=1; shift ;;
        -h|--help) sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown argument: $1 (try --help)" >&2; exit 2 ;;
    esac
done

say()  { printf '\033[1m==> %s\033[0m\n' "$*"; }
fail() { printf '\033[31merror:\033[0m %s\n' "$*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || fail "$1 is required${2:+ ($2)}"; }

case "$(uname -m)" in
    x86_64|amd64) HOST_ARCH=x64 ;;
    aarch64|arm64) HOST_ARCH=arm64 ;;
    *) fail "Unsupported processor '$(uname -m)'; Seamstress builds on x86_64 and 64-bit ARM" ;;
esac
ARCH="${ARCH:-$HOST_ARCH}"
case "$ARCH" in
    x64|x86_64|amd64) ARCH=x64; GNU_ARCH=x86_64 ;;
    arm64|aarch64) ARCH=arm64; GNU_ARCH=aarch64 ;;
    *) fail "Unknown --arch '$ARCH' (use x64 or arm64)" ;;
esac

case "$(uname -s)" in
    Linux) OS=linux; QT_HOST=linux; QT_ARCH=linux_gcc_64; QT_SUBDIR=gcc_64
        if [ "$ARCH" = arm64 ]; then QT_HOST=linux_arm64; QT_ARCH=linux_gcc_arm64; QT_SUBDIR=gcc_arm64; fi ;;
    Darwin) OS=macos; QT_HOST=mac; QT_ARCH=clang_64; QT_SUBDIR=macos ;;
    *) fail "Unsupported system '$(uname -s)'. On Windows run build.ps1 in PowerShell." ;;
esac

# Building for another CPU: on the PC this script starts a container and runs itself inside it, where
# SEAMSTRESS_CROSS_CONTAINER is set. The C++ part is compiled with a cross-compiler, the .NET part is the same on
# every CPU, and nothing has to be emulated.
CROSS=0
if [ "$ARCH" != "$HOST_ARCH" ]; then
    [ "$OS" = linux ] && [ "$ARCH" = arm64 ] || fail "only x86_64 Linux can build for another CPU (--arch arm64)"
    CROSS=1
fi
IN_CONTAINER="${SEAMSTRESS_CROSS_CONTAINER:-0}"
# $QtDir is the Qt for this machine, so it does not apply when building for another CPU.
[ -z "$QT" ] && [ "$CROSS" = 0 ] && QT="${QtDir:-}"

BIN="src/Seamstress.App/bin/$CONFIG/net8.0"
[ "$CROSS" = 1 ] && BIN="$BIN/linux-$ARCH"
DIST="$ROOT/dist"
STAGE="$DIST/Seamstress-linux-$ARCH"
TOOLS="$ROOT/.tools"

# ---------------------------------------------------------------------------------------------- Qt

qt_is_usable() { [ -n "$1" ] && [ -f "$1/lib/cmake/Qt6/Qt6Config.cmake" ] && [ -d "$1/qml/QtQuick3D" ]; }
qt_version() { sed -n 's/^set(PACKAGE_VERSION "\(.*\)")$/\1/p' "$1/lib/cmake/Qt6/Qt6ConfigVersionImpl.cmake" 2>/dev/null; }

install_qt() {   # <aqt host> <aqt arch>
    say "Installing Qt $QT_VERSION ($2) into ~/Qt with aqtinstall"
    local args=(install-qt "$1" desktop "$QT_VERSION" "$2" -O "$HOME/Qt" -m $QT_MODULES)
    if command -v uvx >/dev/null 2>&1; then
        uvx --from aqtinstall aqt "${args[@]}"
    elif command -v pipx >/dev/null 2>&1; then
        pipx run --spec aqtinstall aqt "${args[@]}"
    else
        need python3 "to download Qt; or install Qt yourself and pass --qt <dir>"
        [ -x "$TOOLS/venv/bin/aqt" ] || {
            python3 -m venv "$TOOLS/venv" || fail "python3 -m venv failed; install python3-venv, pipx or uv, or install Qt yourself"
            "$TOOLS/venv/bin/pip" install --quiet aqtinstall
        }
        "$TOOLS/venv/bin/aqt" "${args[@]}"
    fi
}

# Finds the Qt for the target CPU, and when cross-compiling also the x86_64 Qt that provides the build tools.
find_qt() {
    if [ -n "$QT" ]; then
        qt_is_usable "$QT" || fail "'$QT' is not a Qt 6 prefix with Qt Quick 3D (expected lib/cmake/Qt6 and qml/QtQuick3D inside it)"
    else
        local candidate
        # Newest Qt under ~/Qt, as laid out by the Qt online installer and by aqtinstall.
        for candidate in $(ls -d "$HOME"/Qt/6.*/"$QT_SUBDIR" 2>/dev/null | sort -rV) "${QT_ROOT_DIR:-}"; do
            if qt_is_usable "$candidate"; then QT="$candidate"; break; fi
        done
        if [ -z "$QT" ] && [ "$INSTALL_QT" = 1 ]; then
            install_qt "$QT_HOST" "$QT_ARCH"
            QT="$HOME/Qt/$QT_VERSION/$QT_SUBDIR"
            qt_is_usable "$QT" || fail "Qt was downloaded but '$QT' does not look complete"
        fi
        local again="./build.sh"
        [ "$CROSS" = 1 ] && again="$again --arch $ARCH"
        [ -n "$QT" ] || fail "Qt 6 with Qt Quick 3D for $GNU_ARCH was not found.
       Run '$again --install-qt' to download Qt $QT_VERSION into ~/Qt (about 1.7 GB),
       or install it with the Qt online installer and pass '--qt <prefix>'."
    fi
    [ "$CROSS" = 1 ] || return 0

    HOST_QT="${HOST_QT:-$(dirname "$QT")/gcc_64}"
    if ! qt_is_usable "$HOST_QT" && [ "$INSTALL_QT" = 1 ] && [ "$HOST_QT" = "$HOME/Qt/$QT_VERSION/gcc_64" ]; then
        install_qt linux linux_gcc_64
    fi
    qt_is_usable "$HOST_QT" || fail "cross-compiling needs the x86_64 Qt $(qt_version "$QT") as well, for its build tools.
       '$HOST_QT' is not one; pass '--host-qt <prefix>' or run with --install-qt."
    [ "$(qt_version "$QT")" = "$(qt_version "$HOST_QT")" ] \
        || fail "the ARM64 Qt ($(qt_version "$QT")) and the x86_64 Qt ($(qt_version "$HOST_QT")) must be the same version"
}

# ---------------------------------------------------------------------------------------------- cross-compiling

CROSS_IMAGE_FILES=(packaging/linux/cross-arm64.Containerfile packaging/linux/aarch64-linux-gnu.cmake)

# Runs this script with the given command inside the cross-compiling container, creating its image if needed.
run_in_container() {
    local engine user_args=()
    if command -v podman >/dev/null 2>&1; then
        engine=podman; user_args=(--userns=keep-id)
    elif command -v docker >/dev/null 2>&1; then
        engine=docker; user_args=(--user "$(id -u):$(id -g)")
    else
        fail "building for ARM64 on this PC needs podman or docker (package 'podman' on Debian/Ubuntu),
       or run ./build.sh on the ARM64 machine itself"
    fi
    # Tagged with a hash of its recipe so that editing the recipe builds a fresh image.
    local image="seamstress-cross-arm64:$(cat "${CROSS_IMAGE_FILES[@]}" | sha256sum | cut -c1-12)"
    if ! "$engine" image inspect "$image" >/dev/null 2>&1; then
        say "Creating the ARM64 build container ($engine, one-off, about 1.7 GB)"
        "$engine" build -t "$image" -f packaging/linux/cross-arm64.Containerfile packaging/linux
        # Images made from earlier versions of the recipe are no longer used.
        "$engine" image ls --filter reference=seamstress-cross-arm64 --format '{{.Repository}}:{{.Tag}}' \
            | grep -v ":${image#*:}\$" | xargs -r "$engine" rmi >/dev/null 2>&1 || true
    fi
    local nuget="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
    mkdir -p "$nuget" "$TOOLS/cross-home"
    say "Building for ARM64 in the container"
    # Paths are mounted at the same place as outside so that build files written on either side stay valid.
    "$engine" run --rm "${user_args[@]}" --security-opt label=disable \
        -e SEAMSTRESS_CROSS_CONTAINER=1 -e HOME="$TOOLS/cross-home" -e NUGET_PACKAGES="$nuget" \
        -v "$ROOT:$ROOT" -v "$nuget:$nuget" -v "$QT:$QT:ro" -v "$HOST_QT:$HOST_QT:ro" -w "$ROOT" \
        "$image" ./build.sh "$1" --arch "$ARCH" --qt "$QT" --host-qt "$HOST_QT" "--${CONFIG,,}"
}

# ---------------------------------------------------------------------------------------------- build

check_tools() {
    need dotnet "the .NET SDK 8 or newer: https://dotnet.microsoft.com/download"
    need cmake
    need ninja "package 'ninja-build' on Debian/Ubuntu"
    if [ "$CROSS" = 1 ]; then
        need "$GNU_ARCH-linux-gnu-g++"
    else
        command -v c++ >/dev/null 2>&1 || command -v g++ >/dev/null 2>&1 || command -v clang++ >/dev/null 2>&1 \
            || fail "a C++ compiler is required (package 'build-essential' on Debian/Ubuntu); the Qt bridge uses it behind the scenes"
    fi
}

build() {
    check_tools
    find_qt
    if [ "$CROSS" = 1 ]; then
        say "Building Seamstress ($CONFIG) for $GNU_ARCH with Qt at $QT"
        # Only the application project: the tests run on the machine doing the build, not on the target.
        QT_HOST_PATH="$HOST_QT" dotnet build src/Seamstress.App/Seamstress.App.csproj -c "$CONFIG" -r "linux-$ARCH" \
            -p:QtDir="$QT" --nologo -v:minimal
    else
        say "Building Seamstress ($CONFIG) with Qt at $QT"
        dotnet build Seamstress.slnx -c "$CONFIG" -p:QtDir="$QT" --nologo -v:minimal
    fi
    [ -x "$BIN/Seamstress" ] || fail "the build finished but $BIN/Seamstress is missing"
}

# ---------------------------------------------------------------------------------------------- packaging (Linux)

# The shared libraries an ELF file asks for. Unlike ldd, readelf reads files built for any CPU.
needed_libraries() { readelf -d "$1" 2>/dev/null | sed -n 's/.*(NEEDED).*\[\(.*\)\]$/\1/p'; }

# Copies from a folder every library whose name matches the pattern and that anything inside the staged folder
# needs, repeating until nothing is missing.
bundle_libraries() {   # <folder> <name pattern>
    local from="$1" pattern="$2" added=1 file name
    while [ "$added" = 1 ]; do
        added=0
        while IFS= read -r file; do
            while IFS= read -r name; do
                if [[ "$name" =~ $pattern ]] && [ ! -e "$STAGE/lib/$name" ] && [ -e "$from/$name" ]; then
                    cp -L "$from/$name" "$STAGE/lib/$name"
                    added=1
                fi
            done < <(needed_libraries "$file")
        done < <(find "$STAGE" -type f \( -name '*.so' -o -name '*.so.*' -o -name Seamstress \) -not -path "$STAGE/dotnet/*")
    done
}

bundle_dotnet() {
    say "Adding the .NET $DOTNET_CHANNEL runtime for $GNU_ARCH"
    local runtime_dir="$TOOLS/dotnet-runtime-$ARCH"
    mkdir -p "$TOOLS"
    if [ ! -x "$runtime_dir/dotnet" ]; then
        if curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$TOOLS/dotnet-install.sh"; then
            bash "$TOOLS/dotnet-install.sh" --channel "$DOTNET_CHANNEL" --runtime dotnet --architecture "$ARCH" \
                --install-dir "$runtime_dir" --no-path >/dev/null
        fi
    fi
    if [ -x "$runtime_dir/dotnet" ]; then
        cp -a "$runtime_dir" "$STAGE/dotnet"
        return
    fi
    [ "$CROSS" = 0 ] || fail "could not download the .NET runtime for $GNU_ARCH"
    # Offline: fall back to the runtime of the SDK that built the application.
    local sdk runtime
    sdk="$(dirname "$(readlink -f "$(command -v dotnet)")")"
    runtime="$(ls -d "$sdk"/shared/Microsoft.NETCore.App/"${DOTNET_CHANNEL%%.*}".* 2>/dev/null | sort -V | tail -1)"
    [ -n "$runtime" ] || fail "could not download the .NET runtime and no local .NET ${DOTNET_CHANNEL%%.*} runtime was found"
    echo "    (download failed; copying the local runtime from $sdk — it may only run on this distribution)"
    mkdir -p "$STAGE/dotnet/shared/Microsoft.NETCore.App"
    cp -a "$sdk/dotnet" "$sdk/host" "$STAGE/dotnet/"
    cp -a "$runtime" "$STAGE/dotnet/shared/Microsoft.NETCore.App/"
}

stage() {
    [ "$OS" = linux ] || fail "packaging is only implemented for Linux (and for Windows in build.ps1)"
    need readelf "package 'binutils'"
    CONFIG="Release"; BIN="src/Seamstress.App/bin/Release/net8.0"
    [ "$CROSS" = 1 ] && BIN="$BIN/linux-$ARCH"
    build
    say "Assembling a self-contained application in ${STAGE#$ROOT/}"
    rm -rf "$STAGE"
    mkdir -p "$STAGE"
    cp -a "$BIN"/. "$STAGE"/
    rm -f "$STAGE"/*.pdb "$STAGE"/seamstress      # debug symbols; lower-case duplicate of the launcher binary
    rm -rf "$STAGE"/linux-x64 "$STAGE"/linux-arm64  # cross-compiled builds, which land inside the regular output
    mkdir -p "$STAGE/lib" "$STAGE/qml" "$STAGE/translations"

    # QML modules the interface imports (Qt Quick, Controls, Dialogs, Layouts, Quick 3D and what they pull in).
    local module
    for module in QtCore QtQml QtQuick QtQuick3D Qt; do
        [ -d "$QT/qml/$module" ] && cp -a "$QT/qml/$module" "$STAGE/qml/"
    done
    # Parts the application never loads: designer metadata, other platforms' control styles, XR, particles...
    find "$STAGE/qml" -depth -type d \( -name designer -o -name Xr -o -name Particles3D -o -name ParticleEffects \
        -o -name MaterialEditor -o -name lightmapviewer -o -name VirtualKeyboard -o -name Pdf -o -name Scene2D \
        -o -name Scene3D -o -name Material -o -name Universal -o -name Imagine -o -name iOS -o -name macOS \
        -o -name Windows -o -name FluentWinUI3 -o -name NativeStyle -o -name VectorImage -o -name Timeline \
        -o -name Particles -o -name LocalStorage -o -name Physics \) -exec rm -rf {} +
    find "$STAGE/qml" -name '*.debug' -delete

    # Plugins the bridge's own deployment may have skipped (it deploys nothing at all when cross-compiling).
    local group
    for group in platforms xcbglintegrations egldeviceintegrations wayland-shell-integration \
                 wayland-decoration-client wayland-graphics-integration-client imageformats iconengines \
                 platformthemes platforminputcontexts generic networkinformation tls; do
        mkdir -p "$STAGE/plugins/$group"
        cp -an "$QT/plugins/$group"/. "$STAGE/plugins/$group"/ 2>/dev/null || true
    done
    rm -f "$STAGE"/plugins/platforms/libq{eglfs,linuxfb,minimalegl,minimal,vkkhrdisplay,vnc}.so
    find "$QT/translations" -maxdepth 1 -name 'qt_*.qm' -not -name 'qt_help_*' -exec cp -n {} "$STAGE/translations/" \; 2>/dev/null || true

    bundle_libraries "$QT/lib" '.'
    [ -e "$STAGE/lib/libQt6Core.so.6" ] || fail "no Qt libraries were found for the staged application in $QT/lib"
    # A few system libraries Qt needs that a minimal desktop or the Flatpak runtime can lack: the X11 cursor
    # helper for the platform plugin and the Kerberos libraries Qt Network links against.
    local system_lib="" dir
    for dir in "/usr/lib/$GNU_ARCH-linux-gnu" "/lib/$GNU_ARCH-linux-gnu" /usr/lib64 /usr/lib; do
        if [ -e "$dir/libc.so.6" ]; then system_lib="$dir"; break; fi
    done
    if [ -n "$system_lib" ]; then
        [ -e "$system_lib/libxcb-cursor.so.0" ] && cp -L "$system_lib/libxcb-cursor.so.0" "$STAGE/lib/"
        bundle_libraries "$system_lib" '^lib(gssapi_krb5|krb5|k5crypto|krb5support|keyutils|com_err)\.so'
    fi

    # The bridge copies each Qt library twice (libX.so.6 and libX.so.6.x.y); keep one and link the other to it.
    local full short
    for full in "$STAGE"/lib/*.so.*.*.*; do
        short="${full%.*.*}"
        if [ -f "$short" ] && [ ! -L "$short" ] && [ ! -L "$full" ] && cmp -s "$full" "$short"; then
            ln -sf "$(basename "$short")" "$full"
        fi
    done

    printf '[Paths]\nPrefix = .\n' > "$STAGE/qt.conf"
    bundle_dotnet
    install -m755 packaging/linux/seamstress.sh "$STAGE/seamstress.sh"
    install -m644 packaging/linux/"$APP_ID".desktop packaging/linux/"$APP_ID".metainfo.xml "$STAGE"/
    install -m644 src/Seamstress.App/icons/seamstress.svg "$STAGE/$APP_ID.svg"
    cp LICENSE "$STAGE"/ 2>/dev/null || true
    echo "    $(du -sh "$STAGE" | cut -f1) in $(find "$STAGE" -type f | wc -l) files"
}

package() {
    stage
    say "Creating dist/$(basename "$STAGE").tar.gz"
    tar -C "$DIST" -czf "$STAGE.tar.gz" "$(basename "$STAGE")"
    echo "    Unpack anywhere and start it with ./seamstress.sh"
}

appimage() {
    stage
    # The tool runs on the build machine; the AppImage runtime it embeds is for the target CPU.
    local tool_arch
    tool_arch="$(uname -m)"
    local tool="$TOOLS/appimagetool-$tool_arch.AppImage" appdir="$DIST/AppDir-$ARCH" image="$DIST/Seamstress-$GNU_ARCH.AppImage"
    if [ ! -x "$tool" ]; then
        say "Downloading appimagetool"
        need curl
        curl -fL --progress-bar -o "$tool" \
            "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$tool_arch.AppImage"
        chmod +x "$tool"
    fi
    say "Creating dist/$(basename "$image")"
    rm -rf "$appdir"
    mkdir -p "$appdir/usr/lib" "$appdir/usr/share/applications" "$appdir/usr/share/metainfo" \
             "$appdir/usr/share/icons/hicolor/scalable/apps"
    cp -a "$STAGE" "$appdir/usr/lib/seamstress"
    install -m755 packaging/linux/AppRun "$appdir/AppRun"
    install -m644 packaging/linux/"$APP_ID".desktop "$appdir/"
    install -m644 packaging/linux/"$APP_ID".desktop "$appdir/usr/share/applications/"
    install -m644 packaging/linux/"$APP_ID".metainfo.xml "$appdir/usr/share/metainfo/"
    install -m644 src/Seamstress.App/icons/seamstress.svg "$appdir/$APP_ID.svg"
    install -m644 src/Seamstress.App/icons/seamstress.svg "$appdir/usr/share/icons/hicolor/scalable/apps/$APP_ID.svg"
    # --appimage-extract-and-run lets the tool work where FUSE is unavailable (containers, CI).
    # A copy that is still running cannot be written over, but it can be replaced.
    rm -f "$image"
    ARCH="$GNU_ARCH" "$tool" --appimage-extract-and-run --no-appstream "$appdir" "$image"
    rm -rf "$appdir"
    echo "    $(du -h "$image" | cut -f1)  dist/$(basename "$image")"
}

flatpak_bundle() {
    need flatpak
    local work="$DIST/flatpak-$ARCH" bundle="$DIST/Seamstress.flatpak"
    [ "$ARCH" = x64 ] || bundle="$DIST/Seamstress-$GNU_ARCH.flatpak"
    if [ "$CROSS" = 1 ]; then
        # flatpak-builder would have to run the ARM64 runtime's tools, so the bundle is put together directly
        # from the staged folder, with the settings from the manifest. No ARM64 runtime has to be installed here.
        [ -d "$STAGE" ] || fail "the staged ARM64 build is missing"
        local manifest="packaging/flatpak/$APP_ID.yml" runtime version finish_args=()
        runtime="$(sed -n 's/^runtime: *//p' "$manifest")"
        version="$(sed -n "s/^runtime-version: *'\{0,1\}\([^']*\)'\{0,1\}$/\1/p" "$manifest")"
        mapfile -t finish_args < <(sed -n '/^finish-args:/,/^[a-z]/s/^  - //p' "$manifest")
        say "Assembling the $GNU_ARCH Flatpak"
        rm -rf "$work"
        mkdir -p "$work/build/files/lib" "$work/build/var"
        printf '[Application]\nname=%s\nruntime=%s/%s/%s\nsdk=%s/%s/%s\n' "$APP_ID" \
            "$runtime" "$GNU_ARCH" "$version" "$(sed -n 's/^sdk: *//p' "$manifest")" "$GNU_ARCH" "$version" \
            > "$work/build/metadata"
        local files="$work/build/files"
        cp -a "$STAGE" "$files/lib/seamstress"
        printf '#!/bin/sh\nexec /app/lib/seamstress/seamstress.sh "$@"\n' > "$work/launcher"
        install -Dm755 "$work/launcher" "$files/bin/seamstress"
        install -Dm644 "$STAGE/$APP_ID.desktop" "$files/share/applications/$APP_ID.desktop"
        install -Dm644 "$STAGE/$APP_ID.metainfo.xml" "$files/share/metainfo/$APP_ID.metainfo.xml"
        install -Dm644 "$STAGE/$APP_ID.svg" "$files/share/icons/hicolor/scalable/apps/$APP_ID.svg"
        flatpak build-finish --command=seamstress "${finish_args[@]}" "$work/build" >/dev/null
        flatpak build-export --arch="$GNU_ARCH" "$work/repo" "$work/build" >/dev/null
    else
        need flatpak-builder "package 'flatpak-builder'"
        stage
        say "Building the Flatpak"
        rm -rf "$work/build"
        mkdir -p "$work"
        # The manifest names the x86_64 folder; point a copy of it at the one just staged.
        sed "s#path: \.\./\.\./dist/Seamstress-linux-x64#path: $STAGE#" "packaging/flatpak/$APP_ID.yml" > "$work/$APP_ID.yml"
        # Needs the org.freedesktop.Platform and Sdk runtimes named in the manifest:
        #   flatpak install flathub org.freedesktop.Platform//24.08 org.freedesktop.Sdk//24.08
        flatpak-builder --user --force-clean --disable-rofiles-fuse --state-dir="$work/state" --repo="$work/repo" \
            "$work/build" "$work/$APP_ID.yml"
    fi
    flatpak build-bundle --arch="$GNU_ARCH" "$work/repo" "$bundle" "$APP_ID"
    echo "    $(du -h "$bundle" | cut -f1)  dist/$(basename "$bundle")"
    echo "    Install with: flatpak install --user dist/$(basename "$bundle")"
}

# ---------------------------------------------------------------------------------------------- commands

if [ "$CROSS" = 1 ] && [ "$IN_CONTAINER" != 1 ]; then
    case "$COMMAND" in
        test|run) fail "'$COMMAND' needs an ARM64 machine; copy the package over (./build.sh --arch arm64 package) or build there" ;;
        clean) ;;
        flatpak) find_qt; run_in_container stage; flatpak_bundle; say "Done"; exit 0 ;;
        *) find_qt; run_in_container "$COMMAND"; exit 0 ;;
    esac
fi

case "$COMMAND" in
    build)    build ;;
    test)     build; say "Running tests"; dotnet test tests/Seamstress.Tests -c "$CONFIG" --no-build --nologo ;;
    run)      build; exec "$BIN/Seamstress" "${RUN_ARGS[@]}" ;;
    stage)    stage ;;
    package)  package ;;
    appimage) appimage ;;
    flatpak)  flatpak_bundle ;;
    clean)
        say "Removing build output"
        rm -rf "$DIST" src/*/bin src/*/obj tests/*/bin tests/*/obj ;;
esac
say "Done"
