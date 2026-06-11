using System.Xml.Linq;
using builder;

/// <summary>
/// Generates the MSBuild .targets file that NuGet auto-imports into consumer
/// projects.
///
/// Design (analogous to libbitcoin/secp256k1 packaging/nuget/package.targets):
///
///   - package.xml drives a "Linkage" drop-down in the VS project properties
///     UI.  The resulting MSBuild property is $(Linkage-ultrafast).
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

        // General block ClCompile: WITH_ULTRAFAST + flat include\, and (for any
        // non-dynamic linkage) UFSECP_STATIC_LIB so ufsecp.h drops the
        // __declspec(dllimport) — the macro must match ufsecp_version.h exactly.
        var generalCl = new XElement(Ns + "ClCompile",
            new XElement(Ns + "PreprocessorDefinitions",
                "WITH_ULTRAFAST;%(PreprocessorDefinitions)"),
            new XElement(Ns + "AdditionalIncludeDirectories",
                @"$(MSBuildThisFileDirectory)include\;%(AdditionalIncludeDirectories)"));

        var generalClStatic = new XElement(Ns + "ClCompile",
            new XAttribute("Condition", "'$(Linkage-ultrafast)' != 'dynamic'"),
            new XElement(Ns + "PreprocessorDefinitions",
                "UFSECP_STATIC_LIB;%(PreprocessorDefinitions)"));

        var project = new XElement(Ns + "Project",

            // Load the property-page schema so VS shows the Linkage drop-down
            new XElement(Ns + "ItemGroup",
                new XElement(Ns + "PropertyPageSchema",
                    new XAttribute("Include",
                        @"$(MSBuildThisFileDirectory)package.xml"))),

            // General: any non-empty linkage on x64. Libs live next to the
            // .targets in bin\.
            new XElement(Ns + "ItemDefinitionGroup",
                new XAttribute("Condition",
                    "'$(Platform)' == 'x64' And '$(Linkage-ultrafast)' != ''"),
                generalCl,
                generalClStatic,
                new XElement(Ns + "Link",
                    new XElement(Ns + "AdditionalLibraryDirectories",
                        @"$(MSBuildThisFileDirectory)bin\;%(AdditionalLibraryDirectories)"))),

            // static (*.static.lib) — /GL-free, clean consumer link.
            LibGroup("static", "static.lib", "Release"),
            LibGroup("static", "static.lib", "Debug"),

            // ltcg (*.ltcg.lib) — /GL, consumer links with /LTCG.
            LibGroup("ltcg", "ltcg.lib", "Release"),
            LibGroup("ltcg", "ltcg.lib", "Debug"),

            // dynamic (*.lib) — placeholder; DLLs not shipped.
            LibGroup("dynamic", "lib", "Release"),
            LibGroup("dynamic", "lib", "Debug")
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
    // Lib groups — one per (linkage, config).
    //
    // Flat lib naming (set by build.ps1):
    //   {lib}-x64-vc145-{mt-s|mt-sgd}-{ver}.{static|ltcg}.lib
    //     mt-s   → Release (/MT)   mt-sgd → Debug (/MTd)
    //     static → /GL-free        ltcg   → /GL (consumer links /LTCG)
    //
    // The canonical lib set is derived from the static variant (always built)
    // and reformatted with each linkage's extension. This keeps the three
    // linkage groups identical in membership/order, and lets the dynamic group
    // list names for consistency even though DLLs are not shipped.
    //
    // Using Contains("-mt-s-") never matches "-mt-sgd-" (no trailing '-').
    // -----------------------------------------------------------------------

    private static XElement LibGroup(string linkage, string ext, string config)
    {
        string rt = config == "Release" ? "-mt-s-" : "-mt-sgd-";

        var deps = new List<string>();
        if (Directory.Exists(Config.FlatLibDir))
        {
            const string staticExt = ".static.lib";
            foreach (string f in Directory.GetFiles(Config.FlatLibDir, "*" + staticExt))
            {
                string name = Path.GetFileName(f);
                if (!name.Contains(rt)) continue;
                string stem = name[..^staticExt.Length];   // drop ".static.lib"
                deps.Add($"{stem}.{ext}");
            }
            deps.Sort(StringComparer.Ordinal);              // fast…, bridge, ufsecp_s
        }

        if (deps.Count == 0)
            Console.WriteLine($"WARNING: no libs for linkage={linkage} config={config} (rt={rt})");

        string list = string.Join(";", deps) + ";%(AdditionalDependencies)";

        return new XElement(Ns + "ItemDefinitionGroup",
            new XAttribute("Condition",
                $"{PlatformCond} And '$(Linkage-ultrafast)' == '{linkage}' And $(Configuration.IndexOf('{config}')) != -1"),
            new XElement(Ns + "Link",
                new XElement(Ns + "AdditionalDependencies", list)));
    }

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
