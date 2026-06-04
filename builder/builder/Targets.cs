using System.Xml.Linq;
using builder;

/// <summary>
/// Generates the MSBuild .targets file that NuGet auto-imports into consumer
/// projects.
///
/// Design (analogous to libbitcoin/secp256k1 packaging/nuget/package.targets):
///
///   - package.xml drives a "Linkage" drop-down in the VS project properties
///     UI.  The resulting MSBuild property is $(Linkage-UltrafastSecp256k1).
///
///   - Include paths and lib dirs are only injected when a linkage mode is
///     selected (i.e. NOT "Not linked").
///
///   - Declared properties (UltrafastSecp256k1_IncludeDir, _StaticLibDir,
///     _SharedLibDir) keep the per-variant ItemDefinitionGroups concise.
///
///   - Both include\ and include\ufsecp\ are added to AdditionalIncludeDirectories
///     so that relative includes inside ufsecp_libbitcoin.h (e.g. "ufsecp_error.h")
///     resolve correctly.
/// </summary>
internal static class Targets
{
    private static readonly XNamespace Ns =
        "http://schemas.microsoft.com/developer/msbuild/2003";

    // Restricts all lib and include conditions to our supported target:
    // x64 architecture, vc145 toolset only.  Prevents the targets from
    // firing on ARM, ARM64, or older/newer toolsets where no libs exist.
    private const string PlatformCond =
        "'$(Platform)' == 'x64' And '$(PlatformToolset)' == 'v145'";

    public static void Generate(string packageDir)
    {
        string targetsDir = Path.Combine(packageDir, "build", "native");
        Directory.CreateDirectory(targetsDir);

        // Headers: copy from canonical staging location (x64/static/Release).
        // Covers secp256k1/, ufsecp/, ufsecp_libbitcoin.h, secp256k1_shim *.h
        string headerSrc = Path.Combine(
            Config.StagingDir, "x64", "static", "Release", "include");
        string headerDst = Path.Combine(targetsDir, "include");
        CopyDirectory(headerSrc, headerDst);

        var project = new XElement(Ns + "Project",

            // Load the property-page schema so VS shows the Linkage drop-down
            new XElement(Ns + "ItemGroup",
                new XElement(Ns + "PropertyPageSchema",
                    new XAttribute("Include",
                        @"$(MSBuildThisFileDirectory)package.xml"))),

            // General: fired for any non-empty linkage (static or dynamic).
            // Both include\ and include\ufsecp\ are needed so that relative
            // includes inside ufsecp_libbitcoin.h (e.g. "ufsecp_error.h") resolve.
            // AdditionalLibraryDirectories points at the flat lib dir so that
            // AdditionalDependencies can use bare filenames with no path prefix.
            // WITH_ULTRAFAST lets consumer code conditionally compile the
            // UltrafastSecp256k1 code path.
            new XElement(Ns + "ItemDefinitionGroup",
                new XAttribute("Condition",
                    $"'$(Linkage-UltrafastSecp256k1)' != '' And {PlatformCond}"),
                new XElement(Ns + "ClCompile",
                    new XElement(Ns + "AdditionalIncludeDirectories",
                        @"$(MSBuildThisFileDirectory)include\;%(AdditionalIncludeDirectories)"),
                    new XElement(Ns + "AdditionalIncludeDirectories",
                        @"$(MSBuildThisFileDirectory)include\ufsecp\;%(AdditionalIncludeDirectories)"),
                    new XElement(Ns + "PreprocessorDefinitions",
                        "WITH_ULTRAFAST;%(PreprocessorDefinitions)")),
                new XElement(Ns + "Link",
                    new XElement(Ns + "AdditionalLibraryDirectories",
                        @"$(MSBuildThisFileDirectory)..\..\lib\native\;%(AdditionalLibraryDirectories)"))),

            // Static: preprocessor define.
            new XElement(Ns + "ItemDefinitionGroup",
                new XAttribute("Condition",
                    $"'$(Linkage-UltrafastSecp256k1)' == 'static' And {PlatformCond}"),
                new XElement(Ns + "ClCompile",
                    new XElement(Ns + "PreprocessorDefinitions",
                        "UFSECP_STATIC;%(PreprocessorDefinitions)"))),

            StaticLibGroup("Release"),
            StaticLibGroup("Debug"),

            // Dynamic: placeholder — no DLLs packaged yet.
            SharedLibGroup("Release"),
            SharedLibGroup("Debug")
        );

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            project);

        string targetsFile = Path.Combine(
            targetsDir, $"{Config.PackageId}.targets");
        doc.Save(targetsFile);
        Console.WriteLine($"Written: {targetsFile}");
    }

    // -----------------------------------------------------------------------
    // Static lib groups — scanned from the flat staging lib dir.
    //
    // Flat lib naming (Boost-style, set by build.ps1):
    //   mt-s   in the filename → Release (/MT)
    //   mt-sgd in the filename → Debug   (/MTd)
    //
    // Using Contains() rather than a glob so "-mt-s-" never matches "-mt-sgd-".
    // -----------------------------------------------------------------------

    private static XElement StaticLibGroup(string config)
    {
        string suffix = config == "Release" ? "-mt-s-" : "-mt-sgd-";

        var paths = new List<string>();
        if (Directory.Exists(Config.FlatLibDir))
        {
            // Bare filenames only — AdditionalLibraryDirectories in the general
            // group already points at lib\native\, so no path prefix is needed.
            paths = Directory.GetFiles(Config.FlatLibDir, "*.lib")
                .Where(f => Path.GetFileName(f).Contains(suffix))
                .Select(f => Path.GetFileName(f))
                .ToList();
        }

        if (paths.Count == 0)
            Console.WriteLine($"WARNING: no flat libs found for config={config} (suffix={suffix})");

        string deps = string.Join(";", paths) + ";%(AdditionalDependencies)";

        return new XElement(Ns + "ItemDefinitionGroup",
            new XAttribute("Condition",
                $"'$(Linkage-UltrafastSecp256k1)' == 'static' And '$(Configuration)' == '{config}' And {PlatformCond}"),
            new XElement(Ns + "Link",
                new XElement(Ns + "AdditionalDependencies", deps)));
    }

    // -----------------------------------------------------------------------
    // Shared groups — placeholder; shared libs not yet in flat staging.
    // -----------------------------------------------------------------------

    private static XElement SharedLibGroup(string config) =>
        new XElement(Ns + "ItemDefinitionGroup",
            new XAttribute("Condition",
                $"'$(Linkage-UltrafastSecp256k1)' == 'dynamic' And '$(Configuration)' == '{config}' And {PlatformCond}"),
            new XElement(Ns + "Link",
                new XElement(Ns + "AdditionalDependencies", "%(AdditionalDependencies)")));

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------


    private static void CopyDirectory(string src, string dst)
    {
        if (!Directory.Exists(src))
        {
            Console.WriteLine($"WARNING: Header source not found: {src}");
            return;
        }

        Directory.CreateDirectory(dst);
        foreach (string file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(src, file);
            string target   = Path.Combine(dst, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
