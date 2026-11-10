using MoonWalk.Projects;

namespace MoonWalk.Agents;

public static class AgentFactory
{
    public static readonly string[] Known = ["claude"];

    public static bool IsKnown(string name) => Known.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static IAgent Create(string name, ProjectProfile profile) => name.ToLowerInvariant() switch
    {
        "claude" => new ClaudeAgent(profile.Claude),
        _ => throw new ArgumentException($"Unknown agent '{name}'."),
    };
}
