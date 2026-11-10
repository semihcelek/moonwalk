namespace MoonWalk.Core;

/// <summary>
/// The three folders and files MoonWalk reads and writes. Relative settings resolve against the MoonWalk repository root (the folder
/// holding MoonWalk.slnx, found by walking up from the app), so <c>dotnet run --project src/MoonWalk</c> behaves the same from any
/// folder. Without a solution file above it (a published copy) they resolve against the current folder.
/// </summary>
public sealed record AppPaths(string RunsRoot, string ProjectsDir, string AgentsFile)
{
    public static AppPaths From(IConfiguration configuration, string contentRoot)
    {
        var root = FindRoot(contentRoot);
        return new AppPaths(
            Resolve(root, configuration["MoonWalk:DataDir"], "runs"),
            Resolve(root, configuration["MoonWalk:ProjectsDir"], "projects"),
            Resolve(root, configuration["MoonWalk:AgentsFile"], "agents.json"));
    }

    private static string Resolve(string root, string? configured, string fallback) =>
        Path.GetFullPath(Path.Combine(root, string.IsNullOrWhiteSpace(configured) ? fallback : configured));

    private static string FindRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MoonWalk.slnx")))
            {
                return dir.FullName;
            }
        }
        return Directory.GetCurrentDirectory();
    }
}
