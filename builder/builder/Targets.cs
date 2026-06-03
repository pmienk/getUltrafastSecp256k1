using System.Xml.Linq;
using builder;

/// <summary>
/// Generates the MSBuild .targets file that NuGet auto-imports into consumer
/// projects.  The file sets up include paths and, conditionally on Platform,
/// Configuration, and UltrafastSecp256k1LinkType, wires in the correct .lib
/// files and copies .dll files to the output directory.
/// </summary>
internal static class Targets
{
    private static readonly XNamespace Ns =
        "http://schemas.microsoft.com/developer/msbuild/2003";

    public static void Generate(string packageDir)
    {
        string targetsDir = Path.Combine(packageDir, "build", "native");
        Directory.CreateDirectory(targetsDir);

        // Headers are the same for all variants; take them from the canonical
        // location (x64/static/Release) which is always built first.
        string headerSrc = Path.Combine(
            Config.StagingDir, "x64", "static", "Release", "include");
        string headerDst = Path.Combine(targetsDir, "include");
        CopyDirectory(headerSrc, headerDst);

        // Build the XML document
        var project = new XElement(Ns + "Project",
            DefaultLinkTypeProperty(),
            UnconditionalIncludeGroup());

        foreach (string arch    in Config.Archs)
        foreach (string config  in Config.Configs)
        foreach (string linkType in Config.LinkTypes)
        {
            project.Add(LibItemDefinitionGroup(arch, config, linkType));

            if (linkType == "shared")
                project.Add(DllCopyItemGroup(arch, config));
        }

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            project);

        string targetsFile = Path.Combine(targetsDir, $"{Config.PackageId}.targets");
        doc.Save(targetsFile);
        Console.WriteLine($"Written: {targetsFile}");
    }

    // -----------------------------------------------------------------------
    // XML helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Default value for the consumer-facing UltrafastSecp256k1LinkType property.
    /// Static is the default because libbitcoin only links statically.
    /// </summary>
    private static XElement DefaultLinkTypeProperty() =>
        new XElement(Ns + "PropertyGroup",
            new XAttribute("Condition", "'$(UltrafastSecp256k1LinkType)' == ''"),
            new XElement(Ns + "UltrafastSecp256k1LinkType", "Static"));

    /// <summary>
    /// Adds the header directory to AdditionalIncludeDirectories for every
    /// project that imports this .targets file, regardless of platform or config.
    /// </summary>
    private static XElement UnconditionalIncludeGroup() =>
        new XElement(Ns + "ItemDefinitionGroup",
            new XElement(Ns + "ClCompile",
                new XElement(Ns + "AdditionalIncludeDirectories",
                    @"$(MSBuildThisFileDirectory)include;%(AdditionalIncludeDirectories)")));

    /// <summary>
    /// One ItemDefinitionGroup that links the correct .lib files for a given
    /// (arch, config, linkType) combination.  The .lib file names are discovered
    /// by scanning the staging directory so the targets file stays correct even
    /// if CMake renames an output.
    /// </summary>
    private static XElement LibItemDefinitionGroup(
        string arch, string config, string linkType)
    {
        string platform  = Config.MsbuildPlatform(arch);
        string linkCond  = linkType == "static"
            ? "'$(UltrafastSecp256k1LinkType)' != 'Shared'"
            : "'$(UltrafastSecp256k1LinkType)' == 'Shared'";

        string condition =
            $"'$(Platform)' == '{platform}' And " +
            $"'$(Configuration)' == '{config}' And " +
            $"{linkCond}";

        // Scan staging for the actual .lib names produced by CMake
        string libStagingDir = Path.Combine(
            Config.StagingDir, arch, linkType, config, "lib");

        var libPaths = new List<string>();
        if (Directory.Exists(libStagingDir))
        {
            libPaths = Directory
                .GetFiles(libStagingDir, "*.lib")
                .Select(f =>
                    // Path relative to $(MSBuildThisFileDirectory) = build/native/
                    $@"$(MSBuildThisFileDirectory)..\..\lib\native\{arch}\{config}\{linkType}\{Path.GetFileName(f)}")
                .ToList();
        }

        if (libPaths.Count == 0)
            Console.WriteLine(
                $"WARNING: No .lib files found for {arch}/{linkType}/{config}");

        string deps = string.Join(";", libPaths) + ";%(AdditionalDependencies)";

        return new XElement(Ns + "ItemDefinitionGroup",
            new XAttribute("Condition", condition),
            new XElement(Ns + "Link",
                new XElement(Ns + "AdditionalDependencies", deps)));
    }

    /// <summary>
    /// For shared builds, instructs MSBuild to copy the .dll to the output
    /// directory so the executable can find it at runtime.
    /// </summary>
    private static XElement DllCopyItemGroup(string arch, string config)
    {
        string platform  = Config.MsbuildPlatform(arch);
        string condition =
            $"'$(Platform)' == '{platform}' And " +
            $"'$(Configuration)' == '{config}' And " +
            $"'$(UltrafastSecp256k1LinkType)' == 'Shared'";

        var group = new XElement(Ns + "ItemGroup",
            new XAttribute("Condition", condition));

        string dllStagingDir = Path.Combine(
            Config.StagingDir, arch, "shared", config, "lib");

        if (Directory.Exists(dllStagingDir))
        {
            foreach (string dll in Directory.GetFiles(dllStagingDir, "*.dll"))
            {
                string pkgPath =
                    $@"$(MSBuildThisFileDirectory)..\..\lib\native\{arch}\{config}\shared\{Path.GetFileName(dll)}";

                group.Add(new XElement(Ns + "None",
                    new XAttribute("Include", pkgPath),
                    new XElement(Ns + "CopyToOutputDirectory", "PreserveNewest"),
                    new XElement(Ns + "DeploymentContent", "true")));
            }
        }

        return group;
    }

    // -----------------------------------------------------------------------
    // File utilities
    // -----------------------------------------------------------------------

    private static void CopyDirectory(string src, string dst)
    {
        if (!Directory.Exists(src))
        {
            Console.WriteLine($"WARNING: Header source directory not found: {src}");
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
