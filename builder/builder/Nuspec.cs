using System.Diagnostics;
using System.Xml.Linq;
using builder;

/// <summary>
/// Generates the .nuspec manifest and invokes nuget.exe to pack the .nupkg.
/// </summary>
internal static class Nuspec
{
    private static readonly XNamespace Ns =
        "http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd";

    public static void Create(string packageDir)
    {
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
    // <metadata>
    // -----------------------------------------------------------------------

    private static XElement Metadata() =>
        new XElement(Ns + "metadata",
            new XElement(Ns + "id",      Config.PackageId),
            new XElement(Ns + "version", Config.Version),
            new XElement(Ns + "authors", "shrec"),
            new XElement(Ns + "owners",  "eynhaender"),
            new XElement(Ns + "requireLicenseAcceptance", "false"),
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
                $"Built with MSVC {Config.Toolset} (x64 + x86, " +
                $"Release + Debug, static [default] + shared)."),
            new XElement(Ns + "tags",
                "secp256k1 ecc bitcoin cryptography native " + Config.Toolset));

    // -----------------------------------------------------------------------
    // <files>
    // -----------------------------------------------------------------------

    private static XElement Files(string packageDir)
    {
        var files = new XElement(Ns + "files");

        // Everything already placed under _package/ (headers + .targets)
        foreach (string file in Directory.GetFiles(
            packageDir, "*", SearchOption.AllDirectories))
        {
            if (Path.GetExtension(file).Equals(".nuspec", StringComparison.OrdinalIgnoreCase))
                continue;

            string relative = Path.GetRelativePath(packageDir, file);
            files.Add(FileEntry(file, relative));
        }

        // Lib / dll / pdb files live in _staging/ — add them with explicit
        // target paths that match what the .targets file references.
        foreach (string arch     in Config.Archs)
        foreach (string config   in Config.Configs)
        foreach (string linkType in Config.LinkTypes)
        {
            string libDir = Path.Combine(
                Config.StagingDir, arch, linkType, config, "lib");
            if (!Directory.Exists(libDir))
                continue;

            foreach (string file in Directory.GetFiles(libDir))
            {
                string target = Path.Combine(
                    "lib", "native", arch, config, linkType,
                    Path.GetFileName(file));
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
        if (!Console.ReadLine()?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true)
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

        // Print output while the process runs
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
