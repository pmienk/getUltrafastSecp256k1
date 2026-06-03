using builder;

// ---------------------------------------------------------------------------
// Parse command-line arguments
// ---------------------------------------------------------------------------
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--staging":  Config.StagingDir  = args[++i]; break;
        case "--output":   Config.OutputDir   = args[++i]; break;
        case "--version":  Config.Version     = args[++i]; break;
        case "--toolset":  Config.Toolset     = args[++i]; break;
        case "--push":     Config.LocalOnly   = false;     break;
        case "--nuget":    Config.NuGetExe    = args[++i]; break;
        case "--apikey":   Config.NuGetApiKey = args[++i]; break;
        default:
            Die($"Unknown argument: {args[i]}");
            break;
    }
}

if (string.IsNullOrEmpty(Config.StagingDir)) Die("--staging <path> is required");
if (string.IsNullOrEmpty(Config.OutputDir))  Die("--output <path> is required");
if (string.IsNullOrEmpty(Config.Version))    Die("--version <x.y.z> is required");
if (!Directory.Exists(Config.StagingDir))    Die($"Staging directory not found: {Config.StagingDir}");

// The intermediate _package/ directory holds the NuGet layout before packing
string packageDir = Path.Combine(Config.OutputDir, "_package");
Directory.CreateDirectory(packageDir);

// 1. Generate the MSBuild .targets file and copy headers into _package/
Targets.Generate(packageDir);

// 2. Generate the .nuspec and call nuget.exe pack
Nuspec.Create(packageDir);

static void Die(string message)
{
    Console.Error.WriteLine($"ERROR: {message}");
    Environment.Exit(1);
}
