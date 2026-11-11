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

    /// <summary>Passed as --effort (low, medium, high, xhigh, max). Empty: the CLI's own default.</summary>
    public string? Effort { get; set; }

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

/// <summary>One command the Verify stage runs in the repository (no shell: command plus arguments). Steps run in order and stop at the first failure.</summary>
public sealed class AgyOptions
{
    public string Path { get; set; } = "agy";

    public string? Model { get; set; }

    /// <summary>Execution mode for implementing: accept-edits (edits without asking) or auto. Read-only requests always use plan.</summary>
    public string Mode { get; set; } = "accept-edits";

    public string? Effort { get; set; }

    public int TimeoutMinutes { get; set; } = 45;

    /// <summary>
    /// Shell commands agy may run. In headless mode agy cannot ask, so a command that is not allowed is denied and ends the session.
    /// By default none are allowed and the prompt tells the agent to use its file tools only. Entries are agy permission targets:
    /// an exact command line, or <c>*</c> for every command (which also lets it run git commit and git push; only use it if you accept that).
    /// </summary>
    public string[] AllowedCommands { get; set; } = [];

    public string[] ExtraArgs { get; set; } = [];
}

public sealed class MuseOptions
{
    public string Path { get; set; } = "muse";

    public string? Model { get; set; }

    /// <summary>Passed as --reasoning-effort (none ... ultra). Empty: Muse's own default.</summary>
    public string? Effort { get; set; }

    public int TimeoutMinutes { get; set; } = 45;

    /// <summary>Let Muse run shell commands (inside its own sandbox). Off by default, so it cannot run git commit or push; its file tools still work.</summary>
    public bool AllowShell { get; set; }

    /// <summary>Load the workspace's own Muse skills and rules (<c>--trust-workspace</c>). Off by default.</summary>
    public bool TrustWorkspace { get; set; }

    public string[] ExtraArgs { get; set; } = [];
}

public sealed class VerifyStep
{
    public string Name { get; set; } = "";

    public string Command { get; set; } = "";

    public string[] Args { get; set; } = [];

    public int TimeoutMinutes { get; set; } = 15;
}

public sealed class ProjectProfile
{
    public string Name { get; set; } = "";

    public string RepoPath { get; set; } = "";

    public string BaseBranch { get; set; } = "main";

    public WorkspaceMode Workspace { get; set; } = WorkspaceMode.Branch;

    public DirtyPolicy DirtyPolicy { get; set; } = DirtyPolicy.Refuse;

    /// <summary>
    /// Globs of files MoonWalk pretends do not exist: never counted as dirty, never part of what the agent changed, never committed.
    /// For files a tool rewrites on its own while a run is going (for Unity, some settings assets), which would otherwise look like
    /// the agent touched a file that was already modified.
    /// </summary>
    public string[] IgnorePaths { get; set; } = [];

    public string Agent { get; set; } = "claude";

    public string[] Stages { get; set; } = ["Implement"];

    /// <summary>Stages after which the run pauses until you approve, ask for a redo, or stop it.</summary>
    public string[] Gates { get; set; } = [];

    public VerifyStep[] Verify { get; set; } = [];

    /// <summary>Extra bullet points appended to the agent's rules (project conventions, docs to read first, ...).</summary>
    public string[] ExtraInstructions { get; set; } = [];

    public GuardRules Guards { get; set; } = new();

    public ClaudeOptions Claude { get; set; } = new();

    public AgyOptions Agy { get; set; } = new();

    public MuseOptions Muse { get; set; } = new();

    /// <summary>Worktree mode only: the folder that holds the per-run worktrees. Default: <c>wt-moonwalk</c> beside the repository.</summary>
    public string? WorktreeRoot { get; set; }

    /// <summary>The built-in scripted project that needs no repository or agent.</summary>
    [JsonIgnore]
    public bool IsDemo { get; set; }
}
