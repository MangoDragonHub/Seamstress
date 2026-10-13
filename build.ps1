<#
.SYNOPSIS
    Build script for Windows. On Linux and macOS use build.sh.

.DESCRIPTION
    .\build.ps1                 build (Release)
    .\build.ps1 test            build and run the unit tests
    .\build.ps1 run [args]      build and start Seamstress
    .\build.ps1 package         dist\Seamstress-win-x64 and a .zip of it
    .\build.ps1 clean           remove build output

    On 64-bit Windows the Qt bridge package brings its own copy of Qt, so only the .NET SDK is required.
    Pass -Qt <dir> to build against another Qt 6 installation (required on Windows on ARM).

.PARAMETER Configuration
    Release (default) or Debug.

.PARAMETER Qt
    Qt 6 prefix to build against, for example C:\Qt\6.11.3\msvc2022_64.

.PARAMETER NoCompiler
    Build without a C++ compiler even if Visual Studio is installed (uses the bridge's prebuilt host).
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('build', 'test', 'run', 'package', 'clean')]
    [string]$Command = 'build',

    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [string]$Qt = $env:QtDir,

    [switch]$NoCompiler,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$AppArguments
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Say($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Fail($text) { Write-Host "error: $text" -ForegroundColor Red; exit 1 }

$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$bin = "src\Seamstress.App\bin\$Configuration\net8.0"
$dist = Join-Path $PSScriptRoot 'dist'
$stage = Join-Path $dist "Seamstress-win-$arch"

function Test-CppToolchain {
    # The bridge can compile native glue with Visual Studio's C++ tools; without them it falls back to a prebuilt host.
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { return $false }
    $found = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    return [bool]$found
}

function Invoke-Build {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Fail 'The .NET SDK 8 or newer is required: https://dotnet.microsoft.com/download'
    }
    $arguments = @('build', 'Seamstress.slnx', '-c', $Configuration, '--nologo', '-v:minimal')
    if ($Qt) {
        if (-not (Test-Path (Join-Path $Qt 'lib\cmake\Qt6\Qt6Config.cmake'))) {
            Fail "'$Qt' is not a Qt 6 prefix (expected lib\cmake\Qt6\Qt6Config.cmake inside it)"
        }
        $arguments += "-p:QtDir=$Qt"
    }
    elseif ($arch -eq 'arm64') {
        Fail 'On Windows on ARM the bridge does not bundle Qt. Install Qt 6 for ARM64 and pass -Qt <prefix>.'
    }
    if ($NoCompiler -or -not (Test-CppToolchain)) {
        Say 'No Visual Studio C++ tools in use: building with the bridge''s prebuilt host'
        $arguments += '-p:QtExportAs=metadata'
    }
    Say "Building Seamstress ($Configuration)"
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { Fail 'the build failed' }
    if (-not (Test-Path "$bin\Seamstress.exe")) { Fail "the build finished but $bin\Seamstress.exe is missing" }
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
        Invoke-Build
        Say "Assembling $stage"
        if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
        New-Item $stage -ItemType Directory -Force | Out-Null
        # The build output already contains Qt (deployed by the bridge); only debug symbols are left out.
        Copy-Item "$bin\*" $stage -Recurse -Exclude '*.pdb'
        $zip = "$stage.zip"
        if (Test-Path $zip) { Remove-Item $zip -Force }
        Compress-Archive -Path $stage -DestinationPath $zip
        Say "Created $zip"
        Write-Host '    Unzip anywhere and run Seamstress.exe. It needs the .NET 8 runtime (or newer) installed:'
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
