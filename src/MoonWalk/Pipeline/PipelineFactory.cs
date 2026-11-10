using MoonWalk.Pipeline.Stages;
using MoonWalk.Projects;

namespace MoonWalk.Pipeline;

public static class PipelineFactory
{
    public static readonly string[] Known = ["Implement"];

    public static bool IsKnown(string name) => Known.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<IStage> Create(ProjectProfile profile)
    {
        if (profile.IsDemo)
        {
            return DemoPipeline.Create();
        }

        return profile.Stages.Select<string, IStage>(name => name.ToLowerInvariant() switch
        {
            "implement" => new ImplementStage(),
            _ => throw new ArgumentException($"Unknown stage '{name}'."),
        }).ToList();
    }
}
