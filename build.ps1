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
    Full 4-part version override, e.g. "4.1.1.2".
    Default: {VERSION.txt}.{PackagingRevision}.

.PARAMETER PackagingRevision
    Fourth version digit appended to the 3-part source version from VERSION.txt.
    Increment when repackaging without a source rebuild (e.g. .targets fixes).
    Ignored when -Version is specified explicitly.  Default: 0.

.PARAMETER SourceBranch
    Git branch to clone. Default: dev (carries the libbitcoin shim + bridge fixes).

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
    # Source already cloned; repackage with incremented revision
    .\build.ps1 -SkipClone -SkipBuild -Pack -PackagingRevision 1

.EXAMPLE
    # Explicit full version override
    .\build.ps1 -SkipClone -SkipBuild -Pack -Version 4.1.1.0
#>
param(
    [string] $Version           = "",
    [int]    $PackagingRevision = 1,
    [string] $SourceBranch      = "dev",
    [string] $Generator         = "Visual Studio 18 2026",
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
    # $ErrorActionPreference = "Continue" so git's informational stderr output
    # (e.g. "Cloning into...") does not abort the script in PS 5.1.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    if (Test-Path $sourceDir) {
        Write-Host "    Pulling latest..."
        git -C $sourceDir pull --ff-only
    } else {
        git clone --branch $SourceBranch --depth 1 `
            "https://github.com/shrec/UltrafastSecp256k1.git" $sourceDir
    }
    $ErrorActionPreference = $prev
    if ($LASTEXITCODE -ne 0) { throw "git exited with code $LASTEXITCODE" }
}

# ---------------------------------------------------------------------------
# 2. Read version
# ---------------------------------------------------------------------------
if (-not $Version) {
    $versionFile = Join-Path $sourceDir "VERSION.txt"
    if (-not (Test-Path $versionFile)) {
        throw "VERSION.txt not found and no -Version parameter given."
    }
    $sourceVersion = (Get-Content $versionFile -Raw).Trim()
    $Version = "$sourceVersion.$PackagingRevision"
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

    # -------------------------------------------------------------------------
    # Gate the engine's hardcoded MSVC /GL (whole-program optimization) behind a
    # cache flag so we can produce two static variants from the same source:
    #   * static (.static.lib) — /GL OFF: clean, fast consumer link, no
    #     "module compiled with /GL ... restarting link with /LTCG" warning.
    #   * ltcg   (.ltcg.lib)   — /GL ON:  max runtime perf, consumer links /LTCG.
    # Idempotent: only rewrites the line if it is still the unconditional form.
    # -------------------------------------------------------------------------
    $rootCml = Join-Path $sourceDir "CMakeLists.txt"
    $cml = Get-Content $rootCml -Raw
    $glOld = '$<$<AND:$<CONFIG:Release>,$<COMPILE_LANGUAGE:C,CXX>>:/GL>'
    $glNew = '$<$<AND:$<CONFIG:Release>,$<COMPILE_LANGUAGE:C,CXX>,$<STREQUAL:${SECP256K1_MSVC_WHOLE_PROGRAM},ON>>:/GL>'
    if ($cml.Contains($glOld)) {
        $cml = $cml.Replace($glOld, $glNew)
        Set-Content -Path $rootCml -Value $cml -NoNewline -Encoding UTF8
        Write-Host "Patched: gated MSVC /GL behind SECP256K1_MSVC_WHOLE_PROGRAM"
    }

    $archs = @(
        [pscustomobject]@{ CMakeArch = "x64"; Dir = "x64" }
    )

    # Two STATIC variants (libbitcoin links static only; shared/DLL is not
    # shipped). They differ solely by MSVC whole-program optimization (/GL),
    # which selects the .static.lib vs .ltcg.lib package slot.
    $linkTypes = @(
        [pscustomobject]@{ WholeProgram = "OFF"; Dir = "static" }
        [pscustomobject]@{ WholeProgram = "ON";  Dir = "ltcg" }
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
                # Static CRT (/MT /MTd) — required for libbitcoin compatibility.
                # Generator expression: MultiThreaded in Release, MultiThreadedDebug in Debug.
                '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded$<$<CONFIG:Debug>:Debug>',
                # Static libs only (shared/DLL not shipped).
                "-DSECP256K1_BUILD_SHARED=OFF",
                # /GL on for the ltcg variant, off for the static variant.
                ("-DSECP256K1_MSVC_WHOLE_PROGRAM=" + $link.WholeProgram),
                # Components to build
                "-DSECP256K1_BUILD_CPU=ON",
                "-DSECP256K1_BUILD_CABI=ON",
                # Compile the libsecp256k1-compatible shim (secp256k1_* C ABI)
                # directly into fastsecp256k1 so the package can substitute
                # libsecp256k1. With OFF only the shim headers shipped, so
                # consumers (libbitcoin) compiled but failed to link the
                # secp256k1_* symbols.
                "-DSECP256K1_BUILD_SHIM=ON",
                # NOTE: do NOT enable SECP256K1_SHIM_RFC6979_COMPAT. That path
                # appends an "ECDSA" algo16 tag to the RFC6979 nonce, but upstream
                # secp256k1_ecdsa_sign calls the nonce fn with algo16=NULL (no
                # tag). The shim's DEFAULT nonce (rfc6979_nonce, 97-byte HMAC
                # V||0x00||seckey||msg) already matches upstream byte-for-byte —
                # verified against libbitcoin's signature3 vector.
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

                # ufsecp C ABI static lib has no cmake install() rule — copy it
                # from the build tree into this variant's install lib dir so the
                # consolidation step finds it alongside fastsecp256k1.lib.
                $instLib = Join-Path $installPrefix "lib"
                New-Item -ItemType Directory -Force $instLib | Out-Null
                $ufsecpSrc = Join-Path $buildDir ("include\ufsecp\" + $config)
                Get-ChildItem $ufsecpSrc -Filter "ufsecp_s*.lib" -ErrorAction SilentlyContinue |
                    Copy-Item -Destination $instLib -Force
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
        # Static CRT — must match the main library and libbitcoin.
        '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded$<$<CONFIG:Debug>:Debug>',
        # UFSECP_STATIC_LIB removes __declspec(dllimport) from ufsecp.h so the
        # bridge links against the static C ABI (ufsecp_s.lib) rather than
        # expecting DLL import symbols (__imp_ufsecp_*). The macro name must
        # match ufsecp_version.h exactly (UFSECP_STATIC_LIB, not UFSECP_STATIC).
        "-DCMAKE_CXX_FLAGS=/DUFSECP_STATIC_LIB",
        # Point cmake at the installed secp256k1-fast package config so the
        # bridge finds fastsecp256k1 without needing the full source tree.
        ("-DCMAKE_PREFIX_PATH=" + (Join-Path $stagingDir "x64\static\Release")),
        "-DUFSECP_LBTC_BUILD_TESTS=OFF",
        "-DUFSECP_LBTC_BUILD_EXAMPLE=OFF",
        # dev added a throughput bench (default ON) that links the engine; we
        # build only the bridge archive standalone, so disable it too.
        "-DUFSECP_LBTC_BUILD_BENCH=OFF",
        "-DUFSECP_LBTC_WITH_GPU=OFF"
    )

    # Built once (the bridge is a small /GL-free archive); the consolidation
    # step copies it into both the static and ltcg package slots.
    foreach ($config in @("Release", "Debug")) {
        Write-Step ("Build  bridge/" + $config)
        Invoke-Cmake @("--build", $bridgeBuildDir, "--config", $config, "--parallel")
    }

    # -------------------------------------------------------------------------
    # 3b-i. ufsecp C ABI headers
    #
    #     cmake install only copies include/secp256k1/ (the C++ headers).
    #     The ufsecp C ABI headers (ufsecp.h, ufsecp_error.h, ...) live in
    #     include/ufsecp/ in the source tree and must be copied manually.
    #
    #     Kept in the include/ufsecp/ SUBDIR (natural layout). The single
    #     `include\` entry in the .targets resolves ufsecp_libbitcoin.h's
    #     prefixed include ("ufsecp/ufsecp_error.h"), consistent with the
    #     secp256k1/-prefixed engine headers.
    # -------------------------------------------------------------------------
    $ufsecpSrcDir = Join-Path $sourceDir "include\ufsecp"
    $ufsecpDstDir = Join-Path $stagingDir "x64\static\Release\include\ufsecp"
    New-Item -ItemType Directory -Force $ufsecpDstDir | Out-Null
    Get-ChildItem $ufsecpSrcDir -Filter "*.h" | Copy-Item -Destination $ufsecpDstDir -Force
    Write-Host "ufsecp headers copied to staging (include/ufsecp/)."

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

    # -------------------------------------------------------------------------
    # 3d. Flat lib consolidation
    #
    #     All compiled .lib files are renamed to include every build parameter
    #     and copied to a single flat directory (_staging/lib/).  Encoding
    #     arch, toolset, runtime and config in the filename:
    #       - prevents CRT/config mismatches (impossible to pick wrong variant)
    #       - eliminates nested path logic in the MSBuild .targets file
    #
    #     Naming convention (Boost-style):
    #       {lib}-x64-{toolset}-{runtime}-{version}.lib
    #         mt-s   = /MT static lib, Release
    #         mt-sgd = /MTd static lib, Debug   (s=static, g=debug, d=debug-crt)
    # -------------------------------------------------------------------------
    Write-Step "Consolidating libs to flat staging dir"
    $flatLibDir = Join-Path $stagingDir "lib"
    # Wipe stale libs first. The flat dir embeds the version in each filename, so
    # building a new version on top of an existing staging would otherwise leave
    # the previous version's libs behind — the packager globs this dir, so they
    # would be packaged AND listed in .targets (with the old, broken libs first
    # in link order). Clean slate guarantees the package contains only this build.
    if (Test-Path $flatLibDir) { Remove-Item $flatLibDir -Recurse -Force }
    New-Item -ItemType Directory -Force $flatLibDir | Out-Null

    $ver = $Version.Replace(".", "_")

    # Name: {lib}-x64-vc145-{rt}-{ver}.{variant}.lib
    #   rt:      mt-s (Release /MT) | mt-sgd (Debug /MTd)
    #   variant: static (/GL off) | ltcg (/GL on)
    function Copy-Flat {
        param([string]$Src, [string]$Base, [string]$Rt, [string]$Variant)
        if (Test-Path $Src) {
            $dst = Join-Path $flatLibDir ($Base + "-x64-vc145-" + $Rt + "-" + $ver + "." + $Variant + ".lib")
            Copy-Item $Src $dst -Force
            Write-Host ("  " + (Split-Path $dst -Leaf))
        } else {
            Write-Warning ("Not found (skipping): " + $Src)
        }
    }

    # The bridge is built once (/GL-free) and goes into both variant slots.
    $brR = Join-Path $bridgeBuildDir "Release\ufsecp_lbtc_bridge.lib"
    $brD = Join-Path $bridgeBuildDir "Debug\ufsecp_lbtc_bridged.lib"

    foreach ($variant in @("static", "ltcg")) {
        $R = Join-Path $stagingDir ("x64\" + $variant + "\Release\lib")
        $D = Join-Path $stagingDir ("x64\" + $variant + "\Debug\lib")

        Copy-Flat (Join-Path $R "fastsecp256k1.lib")  "fastsecp256k1"      "mt-s"   $variant
        Copy-Flat (Join-Path $R "ufsecp_s.lib")        "ufsecp_s"           "mt-s"   $variant
        Copy-Flat $brR                                  "ufsecp_lbtc_bridge" "mt-s"   $variant
        Copy-Flat (Join-Path $D "fastsecp256k1d.lib") "fastsecp256k1"      "mt-sgd" $variant
        Copy-Flat (Join-Path $D "ufsecp_sd.lib")       "ufsecp_s"           "mt-sgd" $variant
        Copy-Flat $brD                                  "ufsecp_lbtc_bridge" "mt-sgd" $variant
    }
}

# ---------------------------------------------------------------------------
# 4. Build the C# packager and create the .nupkg
# ---------------------------------------------------------------------------
if ($Pack) {
    Assert-Tool "dotnet"
    Assert-Tool "nuget"

    # Wipe the intermediate _package tree first. The builder's header copy only
    # adds/overwrites (never deletes), so a stale layout (e.g. a removed include
    # subdir) would otherwise persist across packs.
    Remove-Item (Join-Path $repoRoot "_package") -Recurse -Force -ErrorAction SilentlyContinue

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
