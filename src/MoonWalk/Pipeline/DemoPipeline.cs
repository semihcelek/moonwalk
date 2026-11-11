using MoonWalk.Pipeline.Stages;

namespace MoonWalk.Pipeline;

public static class DemoPipeline
{
    public static IReadOnlyList<IStage> Create() => new IStage[]
    {
        new DemoStage("Spec", "Reading the brief and turning it into acceptance criteria.", artifact: "spec.md"),
        new DemoStage("Implement", "Starting the implementation on the run branch.", screenshot: true),
        new DemoStage("Verify", "Checking that the change compiles and the guards pass.", DemoBehavior.FailFirstAttemptOnFlag, "evidence/verify.log"),
        new DemoStage("Handover", "Writing the handover note.", DemoBehavior.NeedsHuman, "handover.md"),
    };
}
