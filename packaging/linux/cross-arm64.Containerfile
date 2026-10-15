# Build environment for "./build.sh --arch arm64 ..." on an x86_64 machine. build.sh creates the image the first
# time it is needed (podman or docker) and runs itself inside it with the repository, ~/Qt and the NuGet cache
# mounted. Everything runs at native speed: the C++ part is cross-compiled with GCC for aarch64 against the ARM64
# Qt, using the x86_64 Qt's tools (moc, qmlcachegen...), and the .NET part is architecture-neutral.
#
# Ubuntu 24.04 matches what Qt 6.11 for ARM64 is built on (it needs glibc 2.38 on the target anyway).
FROM docker.io/library/ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive
# The amd64 archive does not carry arm64 packages; those come from ports.ubuntu.com.
RUN sed -i 's/^Types: deb$/Types: deb\nArchitectures: amd64/' /etc/apt/sources.list.d/ubuntu.sources \
 && printf 'Types: deb\nURIs: http://ports.ubuntu.com/ubuntu-ports\nSuites: noble noble-updates noble-security\nComponents: main universe\nArchitectures: arm64\nSigned-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg\n' \
      > /etc/apt/sources.list.d/ubuntu-ports.sources \
 && dpkg --add-architecture arm64 \
 && apt-get update \
 && apt-get install -y --no-install-recommends \
      ca-certificates curl file binutils cmake ninja-build g++-aarch64-linux-gnu binutils-aarch64-linux-gnu libicu74 \
      libglib2.0-0t64 libgl1 libegl1 libfontconfig1 libfreetype6 libx11-6 libxkbcommon0 libbrotli1 libzstd1 \
      libgssapi-krb5-2 \
      libgl-dev:arm64 libegl-dev:arm64 libvulkan-dev:arm64 libdrm-dev:arm64 libgbm-dev:arm64 \
      libfontconfig-dev:arm64 libfreetype-dev:arm64 libglib2.0-0t64:arm64 libdbus-1-3:arm64 \
      libx11-dev:arm64 libx11-xcb1:arm64 libsm6:arm64 libice6:arm64 libxkbcommon-dev:arm64 libxkbcommon-x11-0:arm64 \
      libxcb1:arm64 libxcb-cursor0:arm64 libxcb-icccm4:arm64 libxcb-image0:arm64 libxcb-keysyms1:arm64 \
      libxcb-randr0:arm64 libxcb-render0:arm64 libxcb-render-util0:arm64 libxcb-shape0:arm64 libxcb-shm0:arm64 \
      libxcb-sync1:arm64 libxcb-xfixes0:arm64 libxcb-xkb1:arm64 libwayland-client0:arm64 libwayland-cursor0:arm64 \
      libbrotli1:arm64 zlib1g:arm64 libzstd1:arm64 libkrb5-3:arm64 libgssapi-krb5-2:arm64 \
 && rm -rf /var/lib/apt/lists/*

# The .NET SDK for the build machine. The solution file is .slnx, which needs SDK 9.0.200 or newer.
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
 && bash /tmp/dotnet-install.sh --channel 10.0 --install-dir /opt/dotnet --no-path \
 && rm /tmp/dotnet-install.sh
ENV PATH=/opt/dotnet:$PATH DOTNET_ROOT=/opt/dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# Read by CMake (3.21+) and by Qt's CMake files when the Qt bridge configures its native code.
COPY aarch64-linux-gnu.cmake /opt/aarch64-linux-gnu.cmake
ENV CMAKE_TOOLCHAIN_FILE=/opt/aarch64-linux-gnu.cmake QT_REQUIRE_HOST_PATH_CHECK=ON
