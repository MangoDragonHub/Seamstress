# CMake toolchain for cross-compiling the Qt bridge's native code to 64-bit ARM Linux, used inside the container
# from cross-arm64.Containerfile. The ARM64 libraries are installed there through Debian multiarch, so no sysroot
# is needed: they live in /usr/lib/aarch64-linux-gnu next to the build machine's own.
set(CMAKE_SYSTEM_NAME Linux)
set(CMAKE_SYSTEM_PROCESSOR aarch64)
set(CMAKE_LIBRARY_ARCHITECTURE aarch64-linux-gnu)
set(CMAKE_C_COMPILER aarch64-linux-gnu-gcc)
set(CMAKE_CXX_COMPILER aarch64-linux-gnu-g++)
