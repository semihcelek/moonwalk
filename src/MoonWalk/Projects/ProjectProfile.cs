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

    /// <summary>
    /// Globs the Integrate stage may change even though <see cref="DenyPaths"/> (or <see cref="AllowPaths"/>) would refuse them, for the files an
    /// Editor-side step has to save (scenes, prefabs). Applies to that stage only; Implement keeps the strict rules.
    /// </summary>
    public string[] IntegrateAllowPaths { get; set; } = [];
}

/// <summary>An MCP server an agent may be given. <c>stdio</c> starts <see cref="Command"/>; <c>http</c> connects to <see cref="Url"/>.</summary>
public sealed class McpServer
{
    public string Type { get; set; } = "stdio";

    public string? Command { get; set; }

    public string[] Args { get; set; } = [];

    public string? Url { get; set; }

    public Dictionary<string, string> Env { get; set; } = new();
}

/// <summary>
/// MCP servers by name, and which agent stages may use which. Only the claude agent can be given them (it takes a per-run config file and a tool
/// allow-list); a stage that lists servers for another agent runs without them and says so. When this section is empty nothing changes: agents see
/// whatever their own configuration gives them, and Claude's tool allow-list decides what it may call.
/// </summary>
public sealed class McpOptions
{
    public Dictionary<string, McpServer> Servers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stage name (Spec, Implement, Integrate) to the server names that stage may use. A stage not listed gets none.</summary>
    public Dictionary<string, string[]> Stages { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Configured => Servers.Count > 0 || Stages.Count > 0;
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

    /// <summary>Caps the model steps of one Muse run (<c>--max-model-steps</c>). Muse has no token or dollar cap.</summary>
    public int? MaxSteps { get; set; }

    public string[] ExtraArgs { get; set; } = [];
}

public sealed class VerifyStep
{
    public string Name { get; set; } = "";

    public string Command { get; set; } = "";

    public string[] Args { get; set; } = [];

    public int TimeoutMinutes { get; set; } = 15;

    /// <summary>
    /// Instead of a command: ask the open Unity Editor (its unity-mcp HTTP server, for example http://127.0.0.1:8080/mcp) to compile the scripts
    /// and fail on any compile error in the Console. Costs no agent quota; needs the Editor open on the project folder.
    /// </summary>
    public string? UnityMcp { get; set; }

    /// <summary>With <see cref="UnityMcp"/>: when the Editor cannot be reached, skip the step with a warning instead of failing the stage.</summary>
    public bool Optional { get; set; }
}

/// <summary>
/// Spending limits for one run. Checked between agent stages: when a limit is passed the run waits for you (continue anyway, retry, or
/// stop). Claude is additionally given the remaining dollars as <c>--max-budget-usd</c>, so a single stage cannot overshoot; Muse and agy
/// have no equivalent, so a single stage of theirs can still pass a limit before the check.
/// </summary>
public sealed class BudgetLimits
{
    public double? MaxCostUsd { get; set; }

    public long? MaxTokens { get; set; }
}

public sealed class ProjectProfile
{
    public string Name { get; set; } = "";

    public string RepoPath { get; set; } = "";

    public string BaseBranch { get; set; } = "main";

    /// <summary>
    /// The branch finished runs can be merged into, so several results can be tested together with one checkout. Created from the base
    /// branch on the first merge. Never a <c>moonwalk/</c> name and never the base branch itself.
    /// </summary>
    public string ReviewBranch { get; set; } = "waiting-review";

    /// <summary>Facts for whoever writes briefs for this project (a person or a main agent): shown in the delegation guide, not sent to run agents.</summary>
    public string[] BriefingNotes { get; set; } = [];

    public WorkspaceMode Workspace { get; set; } = WorkspaceMode.Branch;

    /// <summary>
    /// "run" (default): every run gets its own branch from the base branch. "feature": the briefs of a queue share one feature branch and
    /// build on each other, unless a brief opts out. A single run is not affected; name a feature on it to share a branch.
    /// </summary>
    public string Branching { get; set; } = "run";

    public DirtyPolicy DirtyPolicy { get; set; } = DirtyPolicy.Refuse;

    /// <summary>
    /// Globs of files MoonWalk pretends do not exist: never counted as dirty, never part of what the agent changed, never committed.
    /// For files a tool rewrites on its own while a run is going (for Unity, some settings assets), which would otherwise look like
    /// the agent touched a file that was already modified.
    /// </summary>
    public string[] IgnorePaths { get; set; } = [];

    /// <summary>
    /// Before Handover, commit the Unity .meta files that appeared during the run (their assets are already committed). Null: on when the
    /// folder looks like a Unity project (Assets and ProjectSettings). Set false to switch off.
    /// </summary>
    public bool? CommitMetaFiles { get; set; }

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

    public BudgetLimits Budget { get; set; } = new();

    public McpOptions Mcp { get; set; } = new();

    /// <summary>The default automation for runs of this project; the New run dialog can change it per run.</summary>
    public MoonWalk.Core.AutoMode Auto { get; set; } = new();

    public MuseOptions Muse { get; set; } = new();

    /// <summary>Worktree mode only: the folder that holds the per-run worktrees. Default: <c>wt-moonwalk</c> beside the repository.</summary>
    public string? WorktreeRoot { get; set; }

    /// <summary>The built-in scripted project that needs no repository or agent.</summary>
    [JsonIgnore]
    public bool IsDemo { get; set; }
}
