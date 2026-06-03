# getUltrafastSecp256k1

NuGet packaging pipeline for [UltrafastSecp256k1](https://github.com/shrec/UltrafastSecp256k1) —
ultra high-performance secp256k1 elliptic curve cryptography library.

Analogous to [eynhaender/getboost](https://github.com/eynhaender/getboost).

## Prerequisites

| Tool | Purpose | Check |
|---|---|---|
| Visual Studio 2026 with "Desktop development with C++" | vc145 compiler | VS Installer |
| CMake ≥ 3.25 | Build system | `cmake --version` |
| Git | Clone source | `git --version` |
| NuGet CLI (`nuget.exe` on PATH) | Create package | `nuget help` |
| .NET 8 SDK | Build C# packager | `dotnet --version` |

## Usage

```powershell
# Full run: clone source, build, stage, pack
.\build.ps1 -Pack

# Source already cloned; rebuild and repack
.\build.ps1 -SkipClone -Pack

# Staging already complete; repack only
.\build.ps1 -SkipClone -SkipBuild -Pack -Version 4.1.0

# Override version (e.g. for a pre-release tag)
.\build.ps1 -Version 4.1.0-rc1 -Pack
```

## What the pipeline does

1. **build.ps1** clones `shrec/UltrafastSecp256k1` (branch `dev`) and runs CMake
   with the Visual Studio 2026 generator for every combination of:
   - Architecture: x64, x86
   - Link type: static, shared
   - Configuration: Release, Debug

   Each variant is installed to `_staging/{arch}/{linktype}/{config}/`.

2. **builder/** (C# .NET 8) scans the staging tree, generates a `.targets` file
   for MSBuild integration and a `.nuspec`, then calls `nuget.exe pack`.

## Package contents

`UltrafastSecp256k1-vc145.<version>.nupkg` contains:

```
build/native/
    UltrafastSecp256k1-vc145.targets   ← auto-imported by NuGet restore
    include/secp256k1/                 ← public headers
    include/ufsecp/
lib/native/
    x64/{Release,Debug}/{static,shared}/
    x86/{Release,Debug}/{static,shared}/
```

## Consuming the package

Add a local feed and restore:

```powershell
nuget sources add -Name local -Source C:\path\to\getUltrafastSecp256k1
nuget install UltrafastSecp256k1-vc145 -Version 4.1.0
```

Or add a `nuget.config` to your solution pointing at the local directory.

**Link type** is `Static` by default (required by libbitcoin). To opt into shared
linking, set this property before importing the NuGet targets:

```xml
<!-- in your .vcxproj or a .props file that it imports -->
<PropertyGroup>
  <UltrafastSecp256k1LinkType>Shared</UltrafastSecp256k1LinkType>
</PropertyGroup>
```

## Testing the package

Open `test/test.sln` in Visual Studio 2026, restore NuGet packages, and build.
The project links a minimal program against the installed package.
