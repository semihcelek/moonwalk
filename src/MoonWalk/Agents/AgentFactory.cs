using MoonWalk.Projects;

namespace MoonWalk.Agents;

public static class AgentFactory
{
    public static readonly string[] Known = ["claude"];

    public static bool IsKnown(string name) => Known.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static IAgent Create(ProjectProfile profile) => profile.Agent.ToLowerInvariant() switch
    {
        "claude" => new ClaudeAgent(profile.Claude),
        _ => throw new ArgumentException($"Unknown agent '{profile.Agent}'."),
    };
}
