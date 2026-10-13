#!/usr/bin/env bash
# Build script for Linux and macOS. On Windows use build.ps1.
#
#   ./build.sh                 build (Release)
#   ./build.sh test            build and run the unit tests
#   ./build.sh run [args]      build and start Seamstress
#   ./build.sh package         self-contained folder + tarball in dist/        (Linux)
#   ./build.sh appimage        dist/Seamstress-x86_64.AppImage                 (Linux)
#   ./build.sh flatpak         dist/Seamstress.flatpak                         (Linux)
#   ./build.sh clean           remove build output
#
# Options (before or after the command):
#   --debug            build the Debug configuration
#   --qt <dir>         Qt 6 prefix (the folder containing lib/cmake/Qt6); otherwise $QtDir, then ~/Qt/<version>/*
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
QT="${QtDir:-}"
INSTALL_QT=0
RUN_ARGS=()

while [ $# -gt 0 ]; do
    case "$1" in
        build|test|run|package|appimage|flatpak|clean) COMMAND="$1"; shift
            if [ "$COMMAND" = "run" ]; then RUN_ARGS=("$@"); break; fi ;;
        --debug) CONFIG="Debug"; shift ;;
        --release) CONFIG="Release"; shift ;;
        --qt) QT="$2"; shift 2 ;;
        --install-qt) INSTALL_QT=1; shift ;;
        -h|--help) sed -n '2,17p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown argument: $1 (try --help)" >&2; exit 2 ;;
    esac
done

say()  { printf '\033[1m==> %s\033[0m\n' "$*"; }
fail() { printf '\033[31merror:\033[0m %s\n' "$*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || fail "$1 is required${2:+ ($2)}"; }

case "$(uname -s)" in
    Linux) OS=linux; QT_HOST=linux; QT_ARCH=linux_gcc_64; QT_SUBDIR=gcc_64 ;;
    Darwin) OS=macos; QT_HOST=mac; QT_ARCH=clang_64; QT_SUBDIR=macos ;;
    *) fail "Unsupported system '$(uname -s)'. On Windows run build.ps1 in PowerShell." ;;
esac

BIN="src/Seamstress.App/bin/$CONFIG/net8.0"
DIST="$ROOT/dist"
STAGE="$DIST/Seamstress-linux-x64"
TOOLS="$ROOT/.tools"

# ---------------------------------------------------------------------------------------------- Qt

qt_is_usable() { [ -n "$1" ] && [ -f "$1/lib/cmake/Qt6/Qt6Config.cmake" ] && [ -d "$1/qml/QtQuick3D" ]; }

install_qt() {
    say "Installing Qt $QT_VERSION into ~/Qt with aqtinstall"
    local args=(install-qt "$QT_HOST" desktop "$QT_VERSION" "$QT_ARCH" -O "$HOME/Qt" -m $QT_MODULES)
    if command -v uvx >/dev/null 2>&1; then
        uvx --from aqtinstall aqt "${args[@]}"
    elif command -v pipx >/dev/null 2>&1; then
        pipx run --spec aqtinstall aqt "${args[@]}"
    else
        need python3 "to download Qt; or install Qt yourself and pass --qt <dir>"
        python3 -m venv "$TOOLS/venv" || fail "python3 -m venv failed; install python3-venv, pipx or uv, or install Qt yourself"
        "$TOOLS/venv/bin/pip" install --quiet aqtinstall
        "$TOOLS/venv/bin/aqt" "${args[@]}"
    fi
}

find_qt() {
    if [ -n "$QT" ]; then
        qt_is_usable "$QT" || fail "'$QT' is not a Qt 6 prefix with Qt Quick 3D (expected lib/cmake/Qt6 and qml/QtQuick3D inside it)"
        return
    fi
    local candidate
    # Newest Qt under ~/Qt, as laid out by the Qt online installer and by aqtinstall.
    for candidate in $(ls -d "$HOME"/Qt/6.*/"$QT_SUBDIR" 2>/dev/null | sort -rV) "${QT_ROOT_DIR:-}"; do
        if qt_is_usable "$candidate"; then QT="$candidate"; return; fi
    done
    if [ "$INSTALL_QT" = 1 ]; then
        install_qt
        QT="$HOME/Qt/$QT_VERSION/$QT_SUBDIR"
        qt_is_usable "$QT" || fail "Qt was downloaded but '$QT' does not look complete"
        return
    fi
    fail "Qt 6 with Qt Quick 3D was not found.
       Run './build.sh --install-qt' to download Qt $QT_VERSION into ~/Qt (about 1.7 GB),
       or install it with the Qt online installer and pass '--qt <prefix>'."
}

# ---------------------------------------------------------------------------------------------- build

check_tools() {
    need dotnet "the .NET SDK 8 or newer: https://dotnet.microsoft.com/download"
    need cmake
    need ninja "package 'ninja-build' on Debian/Ubuntu"
    command -v c++ >/dev/null 2>&1 || command -v g++ >/dev/null 2>&1 || command -v clang++ >/dev/null 2>&1 \
        || fail "a C++ compiler is required (package 'build-essential' on Debian/Ubuntu); the Qt bridge uses it behind the scenes"
}

build() {
    check_tools
    find_qt
    say "Building Seamstress ($CONFIG) with Qt at $QT"
    dotnet build Seamstress.slnx -c "$CONFIG" -p:QtDir="$QT" --nologo -v:minimal
    [ -x "$BIN/Seamstress" ] || fail "the build finished but $BIN/Seamstress is missing"
}

# ---------------------------------------------------------------------------------------------- packaging (Linux)

# Copies every Qt library that anything inside the staged folder links against, repeating until nothing is missing.
bundle_qt_libraries() {
    local added=1 file dependency name
    while [ "$added" = 1 ]; do
        added=0
        while IFS= read -r file; do
            while IFS= read -r dependency; do
                name="$(basename "$dependency")"
                if [ ! -e "$STAGE/lib/$name" ]; then
                    cp -L "$dependency" "$STAGE/lib/$name"
                    added=1
                fi
            done < <(LD_LIBRARY_PATH="$STAGE/lib:$QT/lib" ldd "$file" 2>/dev/null | awk -v qt="$QT/lib/" 'index($3, qt) == 1 { print $3 }')
        done < <(find "$STAGE" -type f \( -name '*.so' -o -name '*.so.*' -o -name Seamstress \) -not -path "$STAGE/dotnet/*")
    done
}

bundle_dotnet() {
    say "Adding the .NET $DOTNET_CHANNEL runtime"
    mkdir -p "$TOOLS"
    if [ ! -x "$TOOLS/dotnet-runtime/dotnet" ]; then
        if curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$TOOLS/dotnet-install.sh"; then
            bash "$TOOLS/dotnet-install.sh" --channel "$DOTNET_CHANNEL" --runtime dotnet --install-dir "$TOOLS/dotnet-runtime" --no-path >/dev/null
        fi
    fi
    if [ -x "$TOOLS/dotnet-runtime/dotnet" ]; then
        cp -a "$TOOLS/dotnet-runtime" "$STAGE/dotnet"
        return
    fi
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
    CONFIG="Release"; BIN="src/Seamstress.App/bin/Release/net8.0"
    build
    say "Assembling a self-contained application in ${STAGE#$ROOT/}"
    rm -rf "$STAGE"
    mkdir -p "$STAGE"
    cp -a "$BIN"/. "$STAGE"/
    rm -f "$STAGE"/*.pdb "$STAGE"/seamstress      # debug symbols; lower-case duplicate of the launcher binary
    mkdir -p "$STAGE/lib" "$STAGE/qml"

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

    # Plugins the bridge's own deployment may have skipped.
    local group
    for group in platforms xcbglintegrations imageformats iconengines platformthemes platforminputcontexts tls; do
        mkdir -p "$STAGE/plugins/$group"
        cp -an "$QT/plugins/$group"/. "$STAGE/plugins/$group"/ 2>/dev/null || true
    done
    rm -f "$STAGE"/plugins/platforms/libq{eglfs,linuxfb,minimalegl,minimal,vkkhrdisplay,vnc}.so

    bundle_qt_libraries
    # A few system libraries Qt needs that a minimal desktop or the Flatpak runtime can lack: the X11 cursor
    # helper for the platform plugin and the Kerberos libraries Qt Network links against.
    local cursor system
    for cursor in /usr/lib/x86_64-linux-gnu/libxcb-cursor.so.0 /usr/lib64/libxcb-cursor.so.0 /usr/lib/libxcb-cursor.so.0; do
        if [ -e "$cursor" ]; then cp -L "$cursor" "$STAGE/lib/"; break; fi
    done
    while IFS= read -r system; do
        cp -Ln "$system" "$STAGE/lib/"
    done < <(find "$STAGE/lib" "$STAGE/plugins" -name '*.so*' -type f -exec env LD_LIBRARY_PATH="$STAGE/lib" ldd {} + 2>/dev/null \
             | awk '$3 ~ /\/(libgssapi_krb5|libkrb5|libk5crypto|libkrb5support|libkeyutils|libcom_err)\.so/ { print $3 }' | sort -u)

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
    say "Creating dist/Seamstress-linux-x64.tar.gz"
    tar -C "$DIST" -czf "$DIST/Seamstress-linux-x64.tar.gz" "$(basename "$STAGE")"
    echo "    Unpack anywhere and start it with ./seamstress.sh"
}

appimage() {
    [ "$(uname -m)" = x86_64 ] || fail "AppImage packaging is set up for x86_64 only"
    stage
    local tool="$TOOLS/appimagetool-x86_64.AppImage" appdir="$DIST/AppDir"
    if [ ! -x "$tool" ]; then
        say "Downloading appimagetool"
        need curl
        curl -fL --progress-bar -o "$tool" \
            https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
        chmod +x "$tool"
    fi
    say "Creating dist/Seamstress-x86_64.AppImage"
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
    rm -f "$DIST/Seamstress-x86_64.AppImage"
    ARCH=x86_64 "$tool" --appimage-extract-and-run --no-appstream "$appdir" "$DIST/Seamstress-x86_64.AppImage"
    rm -rf "$appdir"
    echo "    $(du -h "$DIST/Seamstress-x86_64.AppImage" | cut -f1)  dist/Seamstress-x86_64.AppImage"
}

flatpak_bundle() {
    need flatpak
    need flatpak-builder "package 'flatpak-builder'"
    stage
    say "Building the Flatpak"
    local work="$DIST/flatpak"
    rm -rf "$work/build"
    mkdir -p "$work"
    # Needs the org.freedesktop.Platform and Sdk runtimes named in the manifest:
    #   flatpak install flathub org.freedesktop.Platform//24.08 org.freedesktop.Sdk//24.08
    flatpak-builder --user --force-clean --disable-rofiles-fuse --state-dir="$work/state" --repo="$work/repo" \
        "$work/build" "packaging/flatpak/$APP_ID.yml"
    flatpak build-bundle "$work/repo" "$DIST/Seamstress.flatpak" "$APP_ID"
    echo "    $(du -h "$DIST/Seamstress.flatpak" | cut -f1)  dist/Seamstress.flatpak"
    echo "    Install with: flatpak install --user dist/Seamstress.flatpak"
}

# ---------------------------------------------------------------------------------------------- commands

case "$COMMAND" in
    build)    build ;;
    test)     build; say "Running tests"; dotnet test tests/Seamstress.Tests -c "$CONFIG" --no-build --nologo ;;
    run)      build; exec "$BIN/Seamstress" "${RUN_ARGS[@]}" ;;
    package)  package ;;
    appimage) appimage ;;
    flatpak)  flatpak_bundle ;;
    clean)
        say "Removing build output"
        rm -rf "$DIST" src/*/bin src/*/obj tests/*/bin tests/*/obj ;;
esac
say "Done"
