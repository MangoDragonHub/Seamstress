# CMake toolchain for cross-compiling the Qt bridge's native code to Windows on ARM from an x64 PC, used by
# "build.ps1 -Arch arm64". The compiler itself comes from Visual Studio's vcvarsamd64_arm64.bat environment; naming
# the target system here is what makes CMake (and Qt) treat the build as a cross build, so that the x64 Qt's tools
# (moc, rcc, qmlcachegen...) run instead of the ARM64 ones.
set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_SYSTEM_PROCESSOR ARM64)
