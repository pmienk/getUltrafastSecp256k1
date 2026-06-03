#Requires -Version 5.1
<#
.SYNOPSIS
    Build UltrafastSecp256k1 and package it as a NuGet package.

.DESCRIPTION
    Clones shrec/UltrafastSecp256k1 (branch: dev), builds with CMake and the
    Visual Studio 2026 generator (vc145) for x64 in Release/Debug x
    static/shared, installs to _staging/, then invokes the C# builder to
    produce the .nupkg.

    Note: UltrafastSecp256k1 uses x64-only intrinsics and cannot be
    compiled for Win32/x86.

.PARAMETER Version
    Version override. Default: read from source VERSION.txt.

.PARAMETER SourceBranch
    Git branch to clone. Default: dev.

.PARAMETER Generator
    CMake generator name. Default: "Visual Studio 18 2026".

.PARAMETER SkipClone
    Skip the git clone/pull step when the source tree is already present.

.PARAMETER SkipBuild
    Skip CMake configure/build/install (useful when re-running only the pack step).

.PARAMETER Pack
    After staging, build the C# builder and produce the .nupkg.

.EXAMPLE
    # Full run: clone, build, package
    .\build.ps1 -Pack

.EXAMPLE
    # Source already cloned; rebuild and repack
    .\build.ps1 -SkipClone -Pack

.EXAMPLE
    # Skip everything except packaging (staging already complete)
    .\build.ps1 -SkipClone -SkipBuild -Pack -Version 4.1.0
#>
param(
    [string] $Version      = "",
    [string] $SourceBranch = "dev",
    [string] $Generator    = "Visual Studio 18 2026",
    [switch] $SkipClone,
    [switch] $SkipBuild,
    [switch] $Pack
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# ---------------------------------------------------------------------------
# Paths -- all relative to the repository root
# ---------------------------------------------------------------------------
$repoRoot   = $PSScriptRoot
$sourceDir  = Join-Path $repoRoot "_source\UltrafastSecp256k1"
$buildRoot  = Join-Path $repoRoot "_build"
$stagingDir = Join-Path $repoRoot "_staging"

# ---------------------------------------------------------------------------
# cmake discovery
# If cmake is not on PATH (common when not running inside a VS Developer
# shell), locate it via vswhere in the Visual Studio installation.
# ---------------------------------------------------------------------------
if (-not (Get-Command cmake -ErrorAction SilentlyContinue)) {
    $vswhere = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $vsRoot   = & $vswhere -latest -property installationPath 2>$null
        $cmakeDir = Join-Path $vsRoot "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin"
        if (Test-Path $cmakeDir) {
            $env:PATH = $cmakeDir + ";" + $env:PATH
            Write-Host "cmake added from: $cmakeDir"
        }
    }
    if (-not (Get-Command cmake -ErrorAction SilentlyContinue)) {
        throw "cmake not found. Install CMake or run from a VS Developer shell."
    }
}

# ---------------------------------------------------------------------------
# nuget.exe discovery / auto-download
# Checks (in order): PATH -> tools/nuget.exe -> download from dist.nuget.org
# ---------------------------------------------------------------------------
if (-not (Get-Command nuget -ErrorAction SilentlyContinue)) {
    $toolsDir  = Join-Path $repoRoot "tools"
    $nugetTool = Join-Path $toolsDir "nuget.exe"
    if (-not (Test-Path $nugetTool)) {
        Write-Host "nuget.exe not found -- downloading to tools/nuget.exe ..."
        New-Item -ItemType Directory -Force $toolsDir | Out-Null
        $prev = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        Invoke-WebRequest `
            -Uri "https://dist.nuget.org/win-x86-commandline/latest/nuget.exe" `
            -OutFile $nugetTool -UseBasicParsing
        $ErrorActionPreference = $prev
    }
    $env:PATH = $toolsDir + ";" + $env:PATH
    Write-Host "nuget added from: $toolsDir"
}

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
function Invoke-Cmake {
    param([string[]] $Arguments)
    # PS 5.1 wraps native-process stderr as NativeCommandError objects.
    # Temporarily use "Continue" so cmake's CMake Warning messages do not
    # trigger $ErrorActionPreference = "Stop" and kill the script.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    & cmake @Arguments
    $ErrorActionPreference = $prev
    if ($LASTEXITCODE -ne 0) { throw "cmake exited with code $LASTEXITCODE" }
}

function Write-Step([string] $Message) {
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Assert-Tool([string] $Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "'$Name' not found on PATH. See readme.md for prerequisites."
    }
}

# ---------------------------------------------------------------------------
# Prerequisite check
# ---------------------------------------------------------------------------
Assert-Tool "cmake"
Assert-Tool "git"

# ---------------------------------------------------------------------------
# 1. Clone / update source
# ---------------------------------------------------------------------------
if (-not $SkipClone) {
    Write-Step "Source: UltrafastSecp256k1 (branch: $SourceBranch)"
    if (Test-Path $sourceDir) {
        Write-Host "    Pulling latest..."
        git -C $sourceDir pull --ff-only
        if ($LASTEXITCODE -ne 0) { throw "git pull failed" }
    } else {
        git clone --branch $SourceBranch --depth 1 `
            "https://github.com/shrec/UltrafastSecp256k1.git" $sourceDir
        if ($LASTEXITCODE -ne 0) { throw "git clone failed" }
    }
}

# ---------------------------------------------------------------------------
# 2. Read version
# ---------------------------------------------------------------------------
if (-not $Version) {
    $versionFile = Join-Path $sourceDir "VERSION.txt"
    if (-not (Test-Path $versionFile)) {
        throw "VERSION.txt not found and no -Version parameter given."
    }
    $Version = (Get-Content $versionFile -Raw).Trim()
}
Write-Host "Version: $Version"

# ---------------------------------------------------------------------------
# 3. Build matrix: arch x link-type -> configure once, install twice
#
#    Each (arch, linktype) pair is configured once with the VS multi-config
#    generator, then built and installed separately for Release and Debug.
#    The install prefix encodes (arch, linktype, config) so the builder can
#    locate every variant unambiguously.
#
#    x64 only: UltrafastSecp256k1 uses x64-only intrinsics (_umul128,
#    _mulx_u64, etc.) and cannot be compiled for Win32/x86.
# ---------------------------------------------------------------------------
if (-not $SkipBuild) {

    $archs = @(
        [pscustomobject]@{ CMakeArch = "x64"; Dir = "x64" }
    )

    # SECP256K1_BUILD_SHARED controls shared vs static in this project
    $linkTypes = @(
        [pscustomobject]@{ BuildShared = "OFF"; Dir = "static" }
        [pscustomobject]@{ BuildShared = "ON";  Dir = "shared" }
    )

    foreach ($arch in $archs) {
        foreach ($link in $linkTypes) {

            $buildDir = Join-Path $buildRoot ($arch.Dir + "_" + $link.Dir)

            # ---- Configure ------------------------------------------------
            Write-Step ("Configure  arch=" + $arch.Dir + "  link=" + $link.Dir)
            Invoke-Cmake @(
                "-S", $sourceDir,
                "-B", $buildDir,
                "-G", $Generator,
                "-A", $arch.CMakeArch,
                # Debug libs get a 'd' suffix so Release and Debug can coexist
                "-DCMAKE_DEBUG_POSTFIX=d",
                # Shared vs static
                ("-DSECP256K1_BUILD_SHARED=" + $link.BuildShared),
                # Components to build
                "-DSECP256K1_BUILD_CPU=ON",
                "-DSECP256K1_BUILD_CABI=ON",
                "-DSECP256K1_BUILD_SHIM=OFF",
                # Disable everything not needed in a redistributable package
                "-DSECP256K1_BUILD_TESTS=OFF",
                "-DSECP256K1_BUILD_BENCH=OFF",
                "-DSECP256K1_BUILD_EXAMPLES=OFF",
                "-DSECP256K1_BUILD_JAVA=OFF",
                "-DSECP256K1_BUILD_CUDA=OFF",
                "-DSECP256K1_BUILD_ROCM=OFF",
                "-DSECP256K1_BUILD_OPENCL=OFF",
                "-DSECP256K1_BUILD_METAL=OFF",
                "-DSECP256K1_INSTALL=ON",
                "-DSECP256K1_INSTALL_PKGCONFIG=OFF"
            )

            # ---- Build + Install per config -------------------------------
            foreach ($config in @("Release", "Debug")) {

                $installPrefix = Join-Path $stagingDir ($arch.Dir + "\" + $link.Dir + "\" + $config)

                Write-Step ("Build+Install  " + $arch.Dir + "/" + $link.Dir + "/" + $config)

                Invoke-Cmake @("--build", $buildDir, "--config", $config, "--parallel")

                Invoke-Cmake @("--install", $buildDir, "--config", $config, "--prefix", $installPrefix)
            }
        }
    }

    # -------------------------------------------------------------------------
    # 3b. libbitcoin bridge (ufsecp_lbtc_bridge)
    #
    #     compat/libbitcoin_bridge/ has no install() rules, so we build it
    #     separately and copy the outputs into the existing static staging dirs.
    #     This makes the C# builder pick them up automatically alongside
    #     fastsecp256k1.lib without any extra configuration.
    # -------------------------------------------------------------------------
    $bridgeSrcDir  = Join-Path $sourceDir "compat\libbitcoin_bridge"
    $bridgeBuildDir = Join-Path $buildRoot "x64_bridge"

    Write-Step "Configure  bridge (libbitcoin)"
    Invoke-Cmake @(
        "-S", $bridgeSrcDir,
        "-B", $bridgeBuildDir,
        "-G", $Generator,
        "-A", "x64",
        "-DCMAKE_DEBUG_POSTFIX=d",
        # Point cmake at the installed secp256k1-fast package config so the
        # bridge finds fastsecp256k1 without needing the full source tree.
        ("-DCMAKE_PREFIX_PATH=" + (Join-Path $stagingDir "x64\static\Release")),
        "-DUFSECP_LBTC_BUILD_TESTS=OFF",
        "-DUFSECP_LBTC_BUILD_EXAMPLE=OFF",
        "-DUFSECP_LBTC_WITH_GPU=OFF"
    )

    foreach ($config in @("Release", "Debug")) {
        Write-Step ("Build+Copy  bridge/" + $config)
        Invoke-Cmake @("--build", $bridgeBuildDir, "--config", $config, "--parallel")

        # Copy bridge lib into the matching static staging lib directory so the
        # C# builder finds it alongside fastsecp256k1.lib.
        $libSrc = Join-Path $bridgeBuildDir $config
        $libDst = Join-Path $stagingDir ("x64\static\" + $config + "\lib")
        New-Item -ItemType Directory -Force $libDst | Out-Null
        Get-ChildItem $libSrc -Filter "ufsecp_lbtc_bridge*.lib" -ErrorAction SilentlyContinue |
            Copy-Item -Destination $libDst -Force
    }

    # -------------------------------------------------------------------------
    # 3b-i. ufsecp C ABI headers
    #
    #     cmake install only copies include/secp256k1/ (the C++ headers).
    #     The ufsecp C ABI headers (ufsecp.h, ufsecp_error.h, ...) live in
    #     include/ufsecp/ in the source tree and must be copied manually.
    #     They are needed at runtime by ufsecp_libbitcoin.h via relative
    #     includes such as "ufsecp_error.h".
    # -------------------------------------------------------------------------
    $ufsecpSrcDir = Join-Path $sourceDir "include\ufsecp"
    $ufsecpDstDir = Join-Path $stagingDir "x64\static\Release\include\ufsecp"
    New-Item -ItemType Directory -Force $ufsecpDstDir | Out-Null
    Get-ChildItem $ufsecpSrcDir -Filter "*.h" | Copy-Item -Destination $ufsecpDstDir -Force
    Write-Host "ufsecp headers copied to staging."

    # Copy the bridge public header into the canonical include tree so Targets.cs
    # includes it when it copies headers to the package.
    $bridgeHeader = Join-Path $bridgeSrcDir "include\ufsecp_libbitcoin.h"
    $headerDst    = Join-Path $stagingDir "x64\static\Release\include\ufsecp_libbitcoin.h"
    Copy-Item $bridgeHeader $headerDst -Force
    Write-Host "Bridge header copied to staging."

    # -------------------------------------------------------------------------
    # 3c. libsecp256k1 shim headers
    #
    #     compat/libsecp256k1_shim/include/ contains the standard secp256k1.h
    #     drop-in headers so consumers can compile code written against the
    #     original libsecp256k1 API without changes.  They go into the flat
    #     include/ root (same level as ufsecp_libbitcoin.h) so that both
    #     #include <secp256k1.h> and the relative include inside
    #     ufsecp_libbitcoin.h ("ufsecp_error.h") resolve correctly once
    #     include\ufsecp\ is on the compiler path.
    # -------------------------------------------------------------------------
    $shimSrcDir = Join-Path $sourceDir "compat\libsecp256k1_shim\include"
    $shimDstDir = Join-Path $stagingDir "x64\static\Release\include"
    Get-ChildItem $shimSrcDir -Filter "*.h" | Copy-Item -Destination $shimDstDir -Force
    Write-Host "Shim headers copied to staging."
}

# ---------------------------------------------------------------------------
# 4. Build the C# packager and create the .nupkg
# ---------------------------------------------------------------------------
if ($Pack) {
    Assert-Tool "dotnet"
    Assert-Tool "nuget"

    Write-Step "Build C# packager"
    $builderSln = Join-Path $repoRoot "builder\builder.sln"
    & dotnet build $builderSln --configuration Release --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

    Write-Step "Generate .targets + .nuspec + pack"
    $builderExe = Join-Path $repoRoot "builder\builder\bin\Release\net9.0-windows\builder.exe"
    & $builderExe `
        --staging $stagingDir `
        --output  $repoRoot   `
        --version $Version    `
        --toolset "vc145"
    if ($LASTEXITCODE -ne 0) { throw "builder.exe failed" }

    Write-Host ""
    Write-Host "Done. Package is in: $repoRoot" -ForegroundColor Green
}
