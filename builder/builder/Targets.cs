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

            // Base directories — evaluated lazily by MSBuild at build time
            new XElement(Ns + "PropertyGroup",
                new XElement(Ns + "UltrafastSecp256k1_IncludeDir",
                    @"$(MSBuildThisFileDirectory)include\"),
                new XElement(Ns + "UltrafastSecp256k1_StaticLibDir",
                    @"$(MSBuildThisFileDirectory)..\..\lib\native\x64\$(Configuration)\static\"),
                new XElement(Ns + "UltrafastSecp256k1_SharedLibDir",
                    @"$(MSBuildThisFileDirectory)..\..\lib\native\x64\$(Configuration)\shared\")),

            // Include paths — only when linked and on the supported target.
            // Add both include\ and include\ufsecp\ so that headers inside
            // ufsecp/ that use relative includes (e.g. "ufsecp_error.h") work.
            new XElement(Ns + "ItemDefinitionGroup",
                new XAttribute("Condition",
                    $"'$(Linkage-UltrafastSecp256k1)' != '' And {PlatformCond}"),
                new XElement(Ns + "ClCompile",
                    new XElement(Ns + "AdditionalIncludeDirectories",
                        @"$(UltrafastSecp256k1_IncludeDir);$(UltrafastSecp256k1_IncludeDir)ufsecp\;%(AdditionalIncludeDirectories)"))),

            // Static: preprocessor define + lib directory
            new XElement(Ns + "ItemDefinitionGroup",
                new XAttribute("Condition",
                    $"'$(Linkage-UltrafastSecp256k1)' == 'static' And {PlatformCond}"),
                new XElement(Ns + "ClCompile",
                    new XElement(Ns + "PreprocessorDefinitions",
                        "UFSECP_STATIC;%(PreprocessorDefinitions)")),
                new XElement(Ns + "Link",
                    new XElement(Ns + "AdditionalLibraryDirectories",
                        @"$(UltrafastSecp256k1_StaticLibDir);%(AdditionalLibraryDirectories)"))),

            StaticLibGroup("Release"),
            StaticLibGroup("Debug"),

            // Dynamic: lib directory
            new XElement(Ns + "ItemDefinitionGroup",
                new XAttribute("Condition",
                    $"'$(Linkage-UltrafastSecp256k1)' == 'dynamic' And {PlatformCond}"),
                new XElement(Ns + "Link",
                    new XElement(Ns + "AdditionalLibraryDirectories",
                        @"$(UltrafastSecp256k1_SharedLibDir);%(AdditionalLibraryDirectories)"))),

            SharedLibGroup("Release"),
            SharedLibGroup("Debug"),

            DllCopyGroup("Release"),
            DllCopyGroup("Debug")
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
    // Static lib filenames — scanned from staging so the file stays correct
    // even if CMake output names change in a future version.
    // -----------------------------------------------------------------------

    private static XElement StaticLibGroup(string config)
    {
        string libDir = Path.Combine(
            Config.StagingDir, "x64", "static", config, "lib");

        var names = ScanLibNames(libDir);
        if (names.Count == 0)
            Console.WriteLine($"WARNING: no .lib found for static/{config}");

        string deps = string.Join(";", names) + ";%(AdditionalDependencies)";

        return new XElement(Ns + "ItemDefinitionGroup",
            new XAttribute("Condition",
                $"'$(Linkage-UltrafastSecp256k1)' == 'static' And '$(Configuration)' == '{config}' And {PlatformCond}"),
            new XElement(Ns + "Link",
                new XElement(Ns + "AdditionalDependencies", deps)));
    }

    // -----------------------------------------------------------------------
    // Shared (import lib) filenames
    // -----------------------------------------------------------------------

    private static XElement SharedLibGroup(string config)
    {
        string libDir = Path.Combine(
            Config.StagingDir, "x64", "shared", config, "lib");

        // Only include .lib files (import libs), not .dll
        var names = ScanLibNames(libDir);

        string deps = string.Join(";", names) + ";%(AdditionalDependencies)";

        return new XElement(Ns + "ItemDefinitionGroup",
            new XAttribute("Condition",
                $"'$(Linkage-UltrafastSecp256k1)' == 'dynamic' And '$(Configuration)' == '{config}' And {PlatformCond}"),
            new XElement(Ns + "Link",
                new XElement(Ns + "AdditionalDependencies", deps)));
    }

    // -----------------------------------------------------------------------
    // DLL copy — copies shared library to the build output directory
    // -----------------------------------------------------------------------

    private static XElement DllCopyGroup(string config)
    {
        string condition =
            $"'$(Linkage-UltrafastSecp256k1)' == 'dynamic' And '$(Configuration)' == '{config}' And {PlatformCond}";

        var group = new XElement(Ns + "ItemGroup",
            new XAttribute("Condition", condition));

        string dllDir = Path.Combine(
            Config.StagingDir, "x64", "shared", config, "lib");

        if (Directory.Exists(dllDir))
        {
            foreach (string dll in Directory.GetFiles(dllDir, "*.dll"))
            {
                string pkgPath =
                    $@"$(UltrafastSecp256k1_SharedLibDir){Path.GetFileName(dll)}";
                group.Add(new XElement(Ns + "None",
                    new XAttribute("Include", pkgPath),
                    new XElement(Ns + "CopyToOutputDirectory", "PreserveNewest"),
                    new XElement(Ns + "DeploymentContent", "true")));
            }
        }

        return group;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>Returns just the filenames (no paths) of all .lib files.</summary>
    private static List<string> ScanLibNames(string dir)
    {
        if (!Directory.Exists(dir))
            return new List<string>();

        return Directory.GetFiles(dir, "*.lib")
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToList();
    }

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
