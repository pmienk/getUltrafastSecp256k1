namespace builder;

/// <summary>
/// Runtime configuration — populated from command-line arguments in Program.cs.
/// </summary>
internal static class Config
{
    // -----------------------------------------------------------------------
    // Set by command-line arguments
    // -----------------------------------------------------------------------

    /// <summary>Root of the _staging/ tree produced by build.ps1.</summary>
    public static string StagingDir { get; set; } = "";

    /// <summary>Directory where the .nupkg will be written (repo root).</summary>
    public static string OutputDir { get; set; } = "";

    /// <summary>Semver version string, e.g. "4.1.0".</summary>
    public static string Version { get; set; } = "";

    /// <summary>MSVC toolset identifier, e.g. "vc145".</summary>
    public static string Toolset { get; set; } = "vc145";

    /// <summary>
    /// When true (default), the package is only created locally and not
    /// pushed to nuget.org. Flip to false via --push to enable the push prompt.
    /// </summary>
    public static bool LocalOnly { get; set; } = true;

    /// <summary>Path or name of nuget.exe. Must be on PATH or fully qualified.</summary>
    public static string NuGetExe { get; set; } = "nuget.exe";

    /// <summary>API key for nuget.org push (only used when LocalOnly is false).</summary>
    public static string NuGetApiKey { get; set; } = "";

    // -----------------------------------------------------------------------
    // Derived
    // -----------------------------------------------------------------------

    /// <summary>NuGet package identifier, e.g. "UltrafastSecp256k1-vc145".</summary>
    public static string PackageId => $"UltrafastSecp256k1-{Toolset}";

    // -----------------------------------------------------------------------
    // Build matrix — must mirror the values used in build.ps1
    // -----------------------------------------------------------------------

    /// <summary>Architecture directory names inside _staging/.</summary>
    public static readonly string[] Archs = { "x64", "x86" };

    /// <summary>Configuration directory names inside _staging/.</summary>
    public static readonly string[] Configs = { "Release", "Debug" };

    /// <summary>Link-type directory names inside _staging/.</summary>
    public static readonly string[] LinkTypes = { "static", "shared" };

    /// <summary>
    /// Maps our directory name ("x86") to the MSBuild platform name ("Win32").
    /// x64 maps to itself.
    /// </summary>
    public static string MsbuildPlatform(string arch) =>
        arch == "x86" ? "Win32" : arch;
}
