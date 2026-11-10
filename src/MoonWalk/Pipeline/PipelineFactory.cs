using MoonWalk.Pipeline.Stages;
using MoonWalk.Projects;

namespace MoonWalk.Pipeline;

public static class PipelineFactory
{
    public static readonly string[] Known = ["Spec", "Implement", "Verify", "Handover"];

    public static bool IsKnown(string name) => Known.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<IStage> Create(ProjectProfile profile)
    {
        if (profile.IsDemo)
        {
            return DemoPipeline.Create();
        }

        return profile.Stages.Select<string, IStage>(name => name.ToLowerInvariant() switch
        {
            "spec" => new SpecStage(),
            "implement" => new ImplementStage(),
            "verify" => new VerifyStage(),
            "handover" => new HandoverStage(),
            _ => throw new ArgumentException($"Unknown stage '{name}'."),
        }).ToList();
    }
}
