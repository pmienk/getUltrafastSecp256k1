using System.Diagnostics;
using System.Xml.Linq;
using builder;

/// <summary>
/// Generates the NuGet readme, the .nuspec manifest, and invokes nuget.exe pack.
/// </summary>
internal static class Nuspec
{
    private static readonly XNamespace Ns =
        "http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd";

    private const string ReadmePath = "docs/readme.md";

    public static void Create(string packageDir)
    {
        WriteReadme(packageDir);

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Ns + "package",
                Metadata(),
                Files(packageDir)));

        string nuspecPath = Path.Combine(packageDir, $"{Config.PackageId}.nuspec");
        doc.Save(nuspecPath);
        Console.WriteLine($"Written: {nuspecPath}");

        Pack(nuspecPath);
    }

    // -----------------------------------------------------------------------
    // Readme
    // -----------------------------------------------------------------------

    private static void WriteReadme(string packageDir)
    {
        string dir  = Path.Combine(packageDir, "docs");
        string path = Path.Combine(dir, "readme.md");
        Directory.CreateDirectory(dir);

        File.WriteAllText(path, $"""
            # UltrafastSecp256k1-{Config.Toolset} {Config.Version}

            Native Windows NuGet package for
            [UltrafastSecp256k1](https://github.com/shrec/UltrafastSecp256k1) —
            ultra high-performance secp256k1 elliptic curve cryptography built
            with **MSVC {Config.Toolset} (Visual Studio 2026)**.

            Packaging pipeline: [eynhaender/getUltrafastSecp256k1](https://github.com/eynhaender/getUltrafastSecp256k1)

            ## Package contents

            | Path | Description |
            |---|---|
            | `build/native/include/` | secp256k1 + ufsecp C/C++ public headers (flat) + `secp256k1/` C++ subdir |
            | `build/native/include/ufsecp_libbitcoin.h` | libbitcoin bridge header |
            | `build/native/bin/*.lib` | All libs with encoded names (Boost-style) |

            ## Platforms

            - **Architecture:** x64 only
              (the library uses x64-only intrinsics; Win32 is not supported)
            - **Configurations:** Release, Debug
            - **Link types:** `static` (/GL-free, default), `ltcg` (/GL, link /LTCG)

            ## Static linking (libbitcoin / default)

            Add a package reference and build — no extra configuration needed.
            The MSBuild `.targets` file wires up include paths and libraries
            automatically.

            `packages.config`:
            ```xml
            <package id="{Config.PackageId}" version="{Config.Version}" targetFramework="native" />
            ```

            ## Shared linking (opt-in)

            Set `UltrafastSecp256k1LinkType` to `Shared` before the NuGet
            targets are imported:

            ```xml
            <PropertyGroup>
              <UltrafastSecp256k1LinkType>Shared</UltrafastSecp256k1LinkType>
            </PropertyGroup>
            ```

            The `.dll` is copied to the output directory automatically.

            ## License

            MIT — see [LICENSE](https://github.com/shrec/UltrafastSecp256k1/blob/dev/LICENSE)
            """);

        Console.WriteLine($"Written: {path}");
    }

    // -----------------------------------------------------------------------
    // <metadata>
    // -----------------------------------------------------------------------

    private static XElement Metadata() =>
        new XElement(Ns + "metadata",
            new XElement(Ns + "id",      Config.PackageId),
            new XElement(Ns + "version", Config.Version),
            new XElement(Ns + "authors", "shrec"),
            new XElement(Ns + "owners",  "eynhaender"),
            new XElement(Ns + "requireLicenseAcceptance", "false"),
            new XElement(Ns + "readme",  ReadmePath),
            new XElement(Ns + "license",
                new XAttribute("type", "expression"), "MIT"),
            new XElement(Ns + "projectUrl",
                "https://github.com/shrec/UltrafastSecp256k1"),
            new XElement(Ns + "repository",
                new XAttribute("type", "git"),
                new XAttribute("url",
                    "https://github.com/eynhaender/getUltrafastSecp256k1")),
            new XElement(Ns + "description",
                $"Native Windows NuGet package for UltrafastSecp256k1 — " +
                $"ultra high-performance secp256k1 elliptic curve cryptography. " +
                $"Built with MSVC {Config.Toolset} (Visual Studio 2026), x64, " +
                $"Release + Debug, static (default) + shared. " +
                $"Includes the libbitcoin bridge (ufsecp_lbtc_bridge)."),
            new XElement(Ns + "tags",
                "secp256k1 ecc bitcoin cryptography native libbitcoin " + Config.Toolset));

    // -----------------------------------------------------------------------
    // <files>
    // -----------------------------------------------------------------------

    private static XElement Files(string packageDir)
    {
        var files = new XElement(Ns + "files");

        // Everything placed under _package/ (headers, .targets, readme)
        foreach (string file in Directory.GetFiles(
            packageDir, "*", SearchOption.AllDirectories))
        {
            if (Path.GetExtension(file).Equals(".nuspec", StringComparison.OrdinalIgnoreCase))
                continue;

            string relative = Path.GetRelativePath(packageDir, file);
            files.Add(FileEntry(file, relative));
        }

        // Flat lib dir: all libs with Boost-style encoded names.
        // build.ps1 consolidates everything here; the C# builder just enumerates.
        if (Directory.Exists(Config.FlatLibDir))
        {
            foreach (string file in Directory.GetFiles(Config.FlatLibDir, "*.lib"))
            {
                // Next to the .targets so $(MSBuildThisFileDirectory)bin\ resolves.
                string target = Path.Combine("build", "native", "bin", Path.GetFileName(file));
                files.Add(FileEntry(file, target));
            }
        }

        return files;
    }

    private static XElement FileEntry(string src, string target) =>
        new XElement(Ns + "file",
            new XAttribute("src",    src),
            new XAttribute("target", target));

    // -----------------------------------------------------------------------
    // nuget.exe pack (and optionally push)
    // -----------------------------------------------------------------------

    private static void Pack(string nuspecPath)
    {
        string outputDir = Config.OutputDir;
        Directory.CreateDirectory(outputDir);

        Console.WriteLine($"Packing {Path.GetFileName(nuspecPath)}...");
        Run(Config.NuGetExe,
            $"pack \"{nuspecPath}\" -OutputDirectory \"{outputDir}\" -NonInteractive");

        if (!Config.LocalOnly)
            Push(outputDir);
    }

    private static void Push(string outputDir)
    {
        Console.Write("Push to nuget.org? [y/N] ");
        if (!string.Equals(Console.ReadLine()?.Trim(), "y",
                StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Push skipped.");
            return;
        }

        string nupkg = Directory
            .GetFiles(outputDir, $"{Config.PackageId}.*.nupkg")
            .OrderByDescending(f => f)
            .FirstOrDefault()
            ?? throw new FileNotFoundException(
                "No .nupkg found to push.", outputDir);

        Run(Config.NuGetExe,
            $"push \"{nupkg}\" " +
            $"-ApiKey \"{Config.NuGetApiKey}\" " +
            $"-Source https://api.nuget.org/v3/index.json " +
            $"-NonInteractive");
    }

    private static void Run(string exe, string arguments)
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {exe}");

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) Console.WriteLine(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) Console.Error.WriteLine(e.Data);
        };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
            throw new Exception($"{exe} exited with code {proc.ExitCode}");
    }
}
