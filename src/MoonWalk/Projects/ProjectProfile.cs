using System.Text.Json.Serialization;

namespace MoonWalk.Projects;

public enum WorkspaceMode
{
    None,
    Branch,
    Worktree,
}

public enum DirtyPolicy
{
    /// <summary>Refuse to start when the working tree has uncommitted changes.</summary>
    Refuse,

    /// <summary>Remember what is already modified and only commit what the agent changed. Stops if the agent touches a file that was already modified.</summary>
    Baseline,
}

public sealed class GuardRules
{
    /// <summary>When non-empty, every changed path must match one of these globs.</summary>
    public string[] AllowPaths { get; set; } = [];

    public string[] DenyPaths { get; set; } = [".git/**", "**/.env*"];

    public int MaxChangedFiles { get; set; } = 60;

    public int MaxChangedLines { get; set; } = 2000;
}

public sealed class ClaudeOptions
{
    public string Path { get; set; } = "claude";

    public string? Model { get; set; }

    public int MaxTurns { get; set; } = 60;

    public int TimeoutMinutes { get; set; } = 45;

    public string[] AllowedTools { get; set; } =
    [
        "Read", "Edit", "Write", "Grep", "Glob",
        "Bash(git status:*)", "Bash(git diff:*)", "Bash(git log:*)",
    ];

    public string[] DisallowedTools { get; set; } =
    [
        "Bash(git commit:*)", "Bash(git push:*)", "Bash(git add:*)",
    ];

    public string[] ExtraArgs { get; set; } = [];
}

public sealed class ProjectProfile
{
    public string Name { get; set; } = "";

    public string RepoPath { get; set; } = "";

    public string BaseBranch { get; set; } = "main";

    public WorkspaceMode Workspace { get; set; } = WorkspaceMode.Branch;

    public DirtyPolicy DirtyPolicy { get; set; } = DirtyPolicy.Refuse;

    public string Agent { get; set; } = "claude";

    public string[] Stages { get; set; } = ["Implement"];

    /// <summary>Extra bullet points appended to the agent's rules (project conventions, docs to read first, ...).</summary>
    public string[] ExtraInstructions { get; set; } = [];

    public GuardRules Guards { get; set; } = new();

    public ClaudeOptions Claude { get; set; } = new();

    /// <summary>The built-in scripted project that needs no repository or agent.</summary>
    [JsonIgnore]
    public bool IsDemo { get; set; }
}
