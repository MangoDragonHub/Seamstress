#!/bin/sh
# Starts the self-contained Seamstress build: uses the Qt libraries and the .NET runtime shipped next to this
# script instead of whatever is (or is not) installed on the system.
HERE="$(dirname "$(readlink -f "$0")")"
# The application host looks for "dotnet" on PATH to locate the runtime.
export PATH="$HERE/dotnet:$PATH"
export DOTNET_ROOT="$HERE/dotnet"
export LD_LIBRARY_PATH="$HERE/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
export QT_PLUGIN_PATH="$HERE/plugins"
export QML_IMPORT_PATH="$HERE/qml"
exec "$HERE/Seamstress" "$@"
