<#
.SYNOPSIS
    Build script for Windows. On Linux and macOS use build.sh.

.DESCRIPTION
    .\build.ps1                 build (Release)
    .\build.ps1 test            build and run the unit tests
    .\build.ps1 run [args]      build and start Seamstress
    .\build.ps1 package         dist\Seamstress-win-<arch> and a .zip of it
    .\build.ps1 -AllX64         build and package everything for x64 in one run
    .\build.ps1 -AllArm64       the same for ARM64
    .\build.ps1 clean           remove build output

    The 3D viewport is built with Qt Quick 3D, which the minimal Qt runtime in the bridge's Windows package does
    not contain, so a full Qt 6 of the same version is needed: -Qt <dir> points at one, -InstallQt downloads it
    into ~\Qt. Without either the build stops with an explanation, instead of making an app that shows no window.

    ARM64 (Windows on ARM: Snapdragon laptops, Surface Pro X...) needs a Qt for ARM64, which the bridge does not
    bring. -InstallQt downloads it into ~\Qt; -Qt or C:\Qt\<version>\msvc2022_arm64 are used otherwise.
      On an ARM64 PC:   .\build.ps1 package -InstallQt
      On an x64 PC:     .\build.ps1 package -Arch arm64 -InstallQt
    Cross-compiling from x64 uses Qt's "MSVC 2022 ARM64 (cross-compiled)" build plus the x64 MSVC 2022 Qt of the
    same version for its tools, and Visual Studio's "MSVC ARM64 build tools" component (or no compiler at all, in
    which case the bridge's prebuilt ARM64 host is used).

.PARAMETER Arch
    x64 or arm64: the processor to build for. The default is this PC's.

.PARAMETER Configuration
    Release (default) or Debug.

.PARAMETER Qt
    Qt 6 prefix to build against, for example C:\Qt\6.11.3\msvc2022_64 or C:\Qt\6.11.3\msvc2022_arm64. On 64-bit
    Windows it must be the version the bridge package was built with and include Qt Quick 3D.

.PARAMETER HostQt
    When cross-compiling to ARM64, the x64 Qt of the same version whose tools the build runs. By default the
    msvc2022_64 folder next to the ARM64 Qt.

.PARAMETER InstallQt
    Download the Qt needed into ~\Qt with aqtinstall when it is not found: a full desktop Qt on 64-bit Windows,
    or the ARM64 Qt when building for ARM64.

.PARAMETER AllX64
    Build (Release) and package everything for x64 in one run: the same as "package", the zip being the only
    Windows distribution format.

.PARAMETER AllArm64
    Build (Release) and package everything for ARM64 in one run; on an x64 PC the build is cross-compiled.
    The two -All* switches are one at a time.

.PARAMETER NoCompiler
    Build without a C++ compiler even if Visual Studio is installed (uses the bridge's prebuilt host).
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('build', 'test', 'run', 'package', 'clean')]
    [string]$Command = 'build',

    [ValidateSet('x64', 'arm64')]
    [string]$Arch,

    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [string]$Qt = $env:QtDir,

    [string]$HostQt,

    [switch]$InstallQt,

    [switch]$AllX64,

    [switch]$AllArm64,

    [switch]$NoCompiler,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$AppArguments
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$QtVersion = '6.11.3'
$QtModules = @('qtquick3d', 'qtshadertools', 'qtquicktimeline')

function Say($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Fail($text) { Write-Host "error: $text" -ForegroundColor Red; exit 1 }

# -AllX64 / -AllArm64 are shortcuts for one CPU: a Release build and then every package Windows has.
if ($AllX64 -or $AllArm64) {
    if ($AllX64 -and $AllArm64) { Fail 'Pass one of -AllX64 or -AllArm64, not both.' }
    if ($Command -ne 'build' -and $Command -ne 'package') { Fail "-AllX64 and -AllArm64 package the application; they do not combine with '$Command'." }
    $allArch = if ($AllX64) { 'x64' } else { 'arm64' }
    if ($PSBoundParameters.ContainsKey('Arch') -and $Arch -ne $allArch) {
        Fail "-Arch $Arch conflicts with $(if ($AllX64) { '-AllX64' } else { '-AllArm64' })."
    }
    $Arch = $allArch
    $Command = 'package'
}

# The operating system's processor, also when this PowerShell itself runs as x64 under emulation on ARM.
$hostArch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
if (-not $Arch) { $Arch = $hostArch }
$cross = $Arch -ne $hostArch
if ($cross -and $Arch -ne 'arm64') { Fail 'An ARM64 PC builds for ARM64 only; build the x64 version on an x64 PC.' }
# $QtDir is the Qt for this PC, so it does not apply when building for another processor.
if ($cross -and $Qt -eq $env:QtDir -and -not $PSBoundParameters.ContainsKey('Qt')) { $Qt = $null }

$bin = "src\Seamstress.App\bin\$Configuration\net8.0"
if ($cross) { $bin = "$bin\win-$Arch" }
$dist = Join-Path $PSScriptRoot 'dist'
$stage = Join-Path $dist "Seamstress-win-$Arch"
$tools = Join-Path $PSScriptRoot '.tools'

# ---------------------------------------------------------------------------------------------- Qt

function Test-QtPrefix($dir) { return $dir -and (Test-Path (Join-Path $dir 'lib\cmake\Qt6\Qt6Config.cmake')) }
# A Qt cross-compiled for ARM64 has a target_qt.conf next to its x64 tools' settings; a native ARM64 Qt has not.
function Test-CrossQt($dir) { return Test-Path (Join-Path $dir 'bin\target_qt.conf') }

function Invoke-Aqt([string[]]$arguments) {
    if (Get-Command uvx -ErrorAction SilentlyContinue) { & uvx --from aqtinstall aqt @arguments }
    elseif (Get-Command pipx -ErrorAction SilentlyContinue) { & pipx run --spec aqtinstall aqt @arguments }
    else {
        $venvAqt = Join-Path $tools 'venv\Scripts\aqt.exe'
        if (-not (Test-Path $venvAqt)) {
            $python = Get-Command py, python -ErrorAction SilentlyContinue | Select-Object -First 1
            if (-not $python) { Fail 'Python, pipx or uv is needed to download Qt; or install Qt yourself and pass -Qt <dir>.' }
            & $python.Source -m venv (Join-Path $tools 'venv')
            & (Join-Path $tools 'venv\Scripts\pip.exe') install --quiet aqtinstall
        }
        & $venvAqt @arguments
    }
    if ($LASTEXITCODE -ne 0) { Fail 'downloading Qt failed' }
}

function Install-Qt($aqtHost, $aqtArch, $version) {
    Say "Installing Qt $version ($aqtArch) into ~\Qt with aqtinstall"
    Invoke-Aqt (@('install-qt', $aqtHost, 'desktop', $version, $aqtArch, '-O', (Join-Path $HOME 'Qt'), '-m') + $QtModules)
}

# Newest <root>\<version>\<subdir> under C:\Qt (Qt online installer) and ~\Qt (aqtinstall) that passes the test.
function Find-Qt($subdir, [scriptblock]$accept) {
    foreach ($root in @('C:\Qt', (Join-Path $HOME 'Qt'))) {
        $found = Get-ChildItem (Join-Path $root "6.*\$subdir") -Directory -ErrorAction SilentlyContinue |
            Sort-Object { try { [version]$_.Parent.Name } catch { [version]'0.0' } } -Descending |
            Where-Object { (Test-QtPrefix $_.FullName) -and (& $accept $_.FullName) } | Select-Object -First 1
        if ($found) { return $found.FullName }
    }
    return $null
}

# The bridge package for this PC. On Windows it carries the Qt it deploys: a minimal Qt Quick runtime that runs
# QML but has none of the modules a full Qt has, Qt Quick 3D among them.
function Find-BridgePackage {
    $root = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    Get-ChildItem (Join-Path $root 'qtgroup.qt.bridge.csharp.win-*') -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Get-ChildItem $_.FullName -Directory -ErrorAction SilentlyContinue } |
        Sort-Object { try { [version]($_.Name -replace '-.*$', '') } catch { [version]'0.0' } } -Descending |
        Where-Object { Test-Path (Join-Path $_.FullName 'tools\qt\bin\windeployqt.exe') } |
        Select-Object -First 1
}

# The bridge's prebuilt host is built against one Qt version, and Qt is only binary compatible within a version,
# so the full Qt that replaces the bundled one has to have exactly that version.
function Get-BridgeQtVersion {
    $package = Find-BridgePackage
    if (-not $package) {
        # The package only reaches the NuGet cache with the restore, and -InstallQt runs before the build.
        & dotnet restore 'src\Seamstress.App\Seamstress.App.csproj' --nologo -v:quiet | Out-Null
        $package = Find-BridgePackage
    }
    if (-not $package) { return $null }
    $core = Join-Path $package.FullName 'tools\qt\bin\Qt6Core.dll'
    if (-not (Test-Path $core)) { return $null }
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($core).FileVersion
    if ($version -match '^\d+\.\d+\.\d+') { return $Matches[0] }
    return $null
}

# 64-bit Windows: the bridge's Qt has no Qt Quick 3D, so -InstallQt fetches a full Qt of the bridge's version,
# or reuses one of that version that is already installed.
function Resolve-DesktopQt {
    $version = Get-BridgeQtVersion
    if (-not $version) {
        Fail 'Cannot tell which Qt version the bridge package uses; install Qt yourself and pass -Qt <dir>.'
    }
    foreach ($root in @('C:\Qt', (Join-Path $HOME 'Qt'))) {
        $dir = Join-Path $root "$version\msvc2022_64"
        if ((Test-QtPrefix $dir) -and (Test-Path (Join-Path $dir 'qml\QtQuick3D'))) {
            Say "Using the Qt $version at $dir"
            $script:Qt = $dir
            return
        }
    }
    Install-Qt 'windows' 'win64_msvc2022_64' $version
    $dir = Join-Path $HOME "Qt\$version\msvc2022_64"
    if (-not (Test-QtPrefix $dir) -or -not (Test-Path (Join-Path $dir 'qml\QtQuick3D'))) {
        Fail "The Qt downloaded to '$dir' has no Qt Quick 3D; pass -Qt <dir> with a full Qt $version."
    }
    $script:Qt = $dir
}

# windeployqt leaves Qt DLLs that are already next to the app alone, so changing the Qt (from the bridge's bundled
# one to -Qt, or between two -Qt prefixes) would mix two runtimes. Start over when the deployed Qt is not this one.
function Reset-IfQtChanged {
    if (-not $Qt -or $cross) { return }
    $deployed = Join-Path $bin 'Qt6Core.dll'
    $wanted = Join-Path $Qt 'bin\Qt6Core.dll'
    if (-not (Test-Path $deployed) -or -not (Test-Path $wanted)) { return }
    if ((Get-FileHash $deployed).Hash -eq (Get-FileHash $wanted).Hash) { return }
    Say "The app was deployed with another Qt; removing $bin to deploy this one"
    Remove-Item $bin -Recurse -Force
    Remove-Item "src\Seamstress.App\obj\$Configuration" -Recurse -Force -ErrorAction SilentlyContinue
}

# Qt Quick 3D is not part of the minimal Qt runtime in the bridge's Windows package, but the 3D viewport imports
# it, and a missing QML module fails no build step: the app would start and never show a window.
function Assert-QtQuick3D {
    if (Test-Path (Join-Path $bin 'qml\QtQuick3D')) { return }
    Fail (@"
the Qt deployed with the app has no Qt Quick 3D, which the 3D viewport needs.
The Qt in the bridge's Windows package is a minimal Qt Quick runtime and does not include it.
Use a full Qt of the same version, or let the script download one:

    .\build.ps1 -InstallQt $Command

"@)
}

function Resolve-ArmQt {
    if ($Qt) {
        if (-not (Test-QtPrefix $Qt)) { Fail "'$Qt' is not a Qt 6 prefix (expected lib\cmake\Qt6\Qt6Config.cmake inside it)" }
    }
    else {
        $script:Qt = Find-Qt 'msvc2022_arm64' { param($d) (Test-CrossQt $d) -eq $cross }
        if (-not $Qt -and $InstallQt) {
            if ($cross) {
                # The x64 Qt first, with the same modules: the cross-compiled Qt runs its tools.
                if (-not (Test-QtPrefix (Join-Path $HOME "Qt\$QtVersion\msvc2022_64"))) { Install-Qt 'windows' 'win64_msvc2022_64' $QtVersion }
                Install-Qt 'windows' 'win64_msvc2022_arm64_cross_compiled' $QtVersion
            }
            else { Install-Qt 'windows_arm64' 'win64_msvc2022_arm64' $QtVersion }
            $script:Qt = Join-Path $HOME "Qt\$QtVersion\msvc2022_arm64"
        }
        if (-not (Test-QtPrefix $Qt)) {
            $kind = if ($cross) { 'the "MSVC 2022 ARM64 (cross-compiled)" Qt' } else { 'Qt for MSVC 2022 ARM64' }
            Fail "The bridge does not bring Qt for ARM64 and $kind was not found. Run with -InstallQt to download Qt $QtVersion into ~\Qt, or pass -Qt <prefix>."
        }
    }
    if (-not $cross) { return }
    if (-not (Test-CrossQt $Qt)) {
        Fail "'$Qt' is a Qt that runs on ARM64; an x64 PC needs the cross-compiled one (aqt arch win64_msvc2022_arm64_cross_compiled)."
    }
    if (-not $HostQt) { $script:HostQt = Join-Path (Split-Path $Qt -Parent) 'msvc2022_64' }
    if (-not (Test-Path (Join-Path $HostQt 'bin\windeployqt.exe'))) {
        Fail "Cross-compiling needs the x64 MSVC 2022 Qt of the same version for its tools; '$HostQt' is not one. Pass -HostQt <prefix>."
    }
}

# ---------------------------------------------------------------------------------------------- build

function Test-CppToolchain {
    # The bridge can compile native glue with Visual Studio's C++ tools; without them it falls back to a prebuilt host.
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { return $false }
    $component = if ($Arch -eq 'arm64') { 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' } else { 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' }
    $found = & $vswhere -latest -products * -requires $component -property installationPath
    return [bool]$found
}

# The bridge's Qt tools (windeployqt) and the Qt DLLs they copy import the MSVC runtime
# (vcruntime140.dll and friends), which the bridge package does not ship. Without it windeployqt.exe cannot
# even start, and the build fails at the deployment step with the bare "Error deploying Qt binaries!".
function Test-MsvcRuntime {
    $folders = @(Join-Path $env:SystemRoot 'System32')
    if (Test-Path (Join-Path $env:SystemRoot 'Sysnative')) { $folders += (Join-Path $env:SystemRoot 'Sysnative') }
    foreach ($dll in @('vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll')) {
        if (-not ($folders | Where-Object { Test-Path (Join-Path $_ $dll) })) { return $false }
    }
    return $true
}

# The bridge runs windeployqt with its output discarded, so a failed deployment says only "Error deploying Qt
# binaries!". Running the same command again, with its output, shows what actually went wrong.
function Show-DeployError {
    # A successful deployment leaves Qt's DLLs next to the app; without them the deployment is what failed.
    if (Test-Path (Join-Path $bin 'Qt6Core.dll')) { return }
    $package = Find-BridgePackage
    if (-not $package) { return }
    $exe = Join-Path $bin 'Seamstress.exe'
    if (-not (Test-Path $exe)) { return }
    Say 'Running windeployqt again, with its output, to show why the deployment failed'
    & (Join-Path $package.FullName 'tools\qt\bin\windeployqt.exe') --qmldir (Resolve-Path 'src\Seamstress.App').Path `
        --no-translations --release --no-compiler-runtime $exe
    Write-Host "    windeployqt exited with $LASTEXITCODE"
}

# Deploys the ARM64 Qt next to a cross-compiled build. The bridge would run the ARM64 Qt's own windeployqt, which an
# x64 PC cannot start, so the x64 Qt's windeployqt is pointed at the ARM64 Qt instead.
function Invoke-CrossDeploy {
    Say 'Deploying the ARM64 Qt with the x64 windeployqt'
    New-Item $tools -ItemType Directory -Force | Out-Null
    $qtpaths = Join-Path $tools 'qtpaths-arm64.bat'
    Set-Content $qtpaths -Encoding Oem -Value @(
        '@echo off',
        "`"$HostQt\bin\qtpaths6.exe`" -qtconf `"$Qt\bin\target_qt.conf`" %*")
    $mode = if ($Configuration -eq 'Debug' -and (Test-Path "$Qt\bin\Qt6Cored.dll")) { '--debug' } else { '--release' }
    & "$HostQt\bin\windeployqt.exe" --qtpaths $qtpaths --qmldir (Resolve-Path 'src\Seamstress.App').Path `
        --no-translations $mode --no-compiler-runtime "$bin\Seamstress.exe" | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail 'deploying the ARM64 Qt failed' }
}

# The solution file is the XML format .slnx, which needs MSBuild 17.13 -- the first version in the .NET SDK 9.0.200.
# Older SDKs do not recognize it, so they build the projects themselves, in the order the solution lists them.
function Get-BuildTarget {
    $sdk = $null
    $text = (& dotnet --version | Select-Object -Last 1)
    if ($text -and [version]::TryParse($text.Trim(), [ref]$sdk) -and
        ($sdk.Major -gt 9 -or ($sdk.Major -eq 9 -and $sdk -ge [version]'9.0.200'))) {
        return 'Seamstress.slnx'
    }
    Say "The .NET SDK $(if ($sdk) { $sdk } else { 'in use' }) cannot read Seamstress.slnx (9.0.200 or newer can); building the projects directly"
    return @('src\Seamstress.App\Seamstress.App.csproj', 'tests\Seamstress.Tests\Seamstress.Tests.csproj')
}

function Invoke-Build {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Fail 'The .NET SDK 8 or newer is required: https://dotnet.microsoft.com/download'
    }
    if (-not (Test-MsvcRuntime)) {
        Write-Host 'warning: the Microsoft Visual C++ 2015-2022 Redistributable (x64) is not installed.' -ForegroundColor Yellow
        Write-Host '    The bridge''s Qt tools and the application itself need it; without it the build fails at the' -ForegroundColor Yellow
        Write-Host '    deployment step with "Error deploying Qt binaries!", and the app does not start.' -ForegroundColor Yellow
        Write-Host '    Install it from https://aka.ms/vs/17/release/vc_redist.x64.exe' -ForegroundColor Yellow
    }
    $compiler = -not $NoCompiler -and (Test-CppToolchain)
    if ($cross) {
        Resolve-ArmQt
        # Only the application project: the tests run on the PC doing the build, not on the target.
        $targets = @('src\Seamstress.App\Seamstress.App.csproj')
        $options = @('-c', $Configuration, '-r', "win-$Arch", "-p:QtDir=$Qt", '-p:QtArch=arm64',
            '-p:VcVarsScript=vcvars64.bat', '-p:DeployCmd=rem', '--nologo', '-v:minimal')
        if ($compiler) {
            # Visual Studio's x64-hosted ARM64 compiler; CMake and Qt take the rest from these variables.
            $options += '-p:QtVcVarsScript=vcvarsamd64_arm64.bat'
            $env:CMAKE_TOOLCHAIN_FILE = Join-Path $PSScriptRoot 'packaging\windows\arm64-msvc.cmake'
            $env:QT_HOST_PATH = $HostQt
            $env:QT_REQUIRE_HOST_PATH_CHECK = 'ON'
        }
    }
    else {
        $targets = @(Get-BuildTarget)
        $options = @('-c', $Configuration, '--nologo', '-v:minimal')
        if ($Arch -eq 'arm64') { Resolve-ArmQt }
        elseif ($InstallQt -and -not $Qt) { Resolve-DesktopQt }
        if ($Qt) {
            if (-not (Test-QtPrefix $Qt)) { Fail "'$Qt' is not a Qt 6 prefix (expected lib\cmake\Qt6\Qt6Config.cmake inside it)" }
            $options += "-p:QtDir=$Qt"
        }
    }
    if (-not $compiler) {
        Say 'No Visual Studio C++ tools in use: building with the bridge''s prebuilt host'
        $options += '-p:QtExportAs=metadata'
    }
    Say "Building Seamstress ($Configuration, $Arch)"
    Reset-IfQtChanged
    foreach ($target in $targets) {
        & dotnet build $target @options
        if ($LASTEXITCODE -ne 0) {
            Show-DeployError
            Fail 'the build failed'
        }
    }
    if (-not (Test-Path "$bin\Seamstress.exe")) { Fail "the build finished but $bin\Seamstress.exe is missing" }
    if ($cross) { Invoke-CrossDeploy }
    Assert-QtQuick3D
}

if ($cross -and $Command -in @('test', 'run')) {
    Fail "'$Command' needs an ARM64 PC; copy the package over (.\build.ps1 package -Arch arm64) or build there."
}

switch ($Command) {
    'build' { Invoke-Build }
    'test' {
        Invoke-Build
        Say 'Running tests'
        & dotnet test tests\Seamstress.Tests -c $Configuration --no-build --nologo
        if ($LASTEXITCODE -ne 0) { Fail 'tests failed' }
    }
    'run' {
        Invoke-Build
        & "$bin\Seamstress.exe" @AppArguments
    }
    'package' {
        $Configuration = 'Release'
        $bin = 'src\Seamstress.App\bin\Release\net8.0'
        if ($cross) { $bin = "$bin\win-$Arch" }
        Invoke-Build
        Say "Assembling $stage"
        if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
        New-Item $stage -ItemType Directory -Force | Out-Null
        # The build output already contains Qt (deployed by the bridge); debug symbols are left out, and so are
        # cross-compiled builds, which land inside the regular output folder.
        Copy-Item "$bin\*" $stage -Recurse -Exclude '*.pdb', 'win-x64', 'win-arm64'
        # The licences of what the package contains: MIT for Seamstress, LGPLv3 for Qt and the Qt Bridge, GPLv3 for
        # Qt Quick 3D, which the 3D viewport makes part of the whole (see NOTICE).
        Copy-Item LICENSE, NOTICE -Destination $stage -Force
        Copy-Item LICENSES -Destination $stage -Recurse -Force
        # The Qt version is the bridge's on Windows and another one on Linux, so each package names its own.
        $qtVersion = ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $bin 'Qt6Core.dll')).FileVersion -split '\.')[0..2] -join '.'
        Add-Content (Join-Path $stage 'NOTICE') "`nBuilt with Qt $qtVersion."
        $zip = "$stage.zip"
        if (Test-Path $zip) { Remove-Item $zip -Force }
        Compress-Archive -Path $stage -DestinationPath $zip
        Say "Created $zip"
        Write-Host "    Unzip anywhere and run Seamstress.exe. It needs the .NET 8 runtime (or newer) for $Arch installed:"
        Write-Host '    https://dotnet.microsoft.com/download/dotnet/8.0'
    }
    'clean' {
        Say 'Removing build output'
        foreach ($path in @($dist) + (Get-ChildItem src, tests -Directory | ForEach-Object { "$($_.FullName)\bin"; "$($_.FullName)\obj" })) {
            if (Test-Path $path) { Remove-Item $path -Recurse -Force }
        }
    }
}
Say 'Done'
