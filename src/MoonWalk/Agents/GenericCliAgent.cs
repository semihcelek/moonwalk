using System.Text;
using MoonWalk.Core;

namespace MoonWalk.Agents;

/// <summary>An agent defined in agents.json: any command line tool that takes a prompt and edits files in its working folder.</summary>
public sealed class AgentDefinition
{
    public string Name { get; set; } = "";

    public string Command { get; set; } = "";

    /// <summary>
    /// Arguments. <c>{prompt}</c> is replaced by the prompt text, <c>{promptFile}</c> by the path of a temporary file holding it, and
    /// <c>{workdir}</c> by the folder to work in. With neither prompt token the prompt is written to stdin.
    /// </summary>
    public string[] Args { get; set; } = [];

    public int TimeoutMinutes { get; set; } = 45;

    /// <summary>Arguments for the health check; the agent counts as available when this exits with 0.</summary>
    public string[] VersionArgs { get; set; } = ["--version"];
}

/// <summary>
/// Runs a configured CLI agent and shows its output line by line. It cannot see tool calls, only text, so the feed has less detail
/// than for Claude or agy. Read-only requests cannot be enforced for it; the stages verify the result instead.
/// </summary>
public sealed class GenericCliAgent(AgentDefinition definition) : IAgent
{
    public string Name => definition.Name;

    public async Task<AgentResult> RunAsync(AgentRequest request, EmitEvent emit, CancellationToken ct)
    {
        var resolved = CliProcess.Resolve(definition.Command);
        var usesPromptArg = definition.Args.Any(a => a.Contains("{prompt}", StringComparison.Ordinal));
        if (usesPromptArg && CliProcess.IsBatchFile(resolved))
        {
            throw new InvalidOperationException(
                $"Agent '{Name}' runs a batch file ({resolved}); passing the prompt as an argument is unsafe there. Use {{promptFile}} or stdin instead.");
        }

        string? promptFile = null;
        try
        {
            if (definition.Args.Any(a => a.Contains("{promptFile}", StringComparison.Ordinal)))
            {
                promptFile = Path.Combine(Path.GetTempPath(), $"moonwalk-prompt-{Guid.NewGuid():N}.md");
                File.WriteAllText(promptFile, request.Prompt, new UTF8Encoding(false));
            }

            var args = definition.Args
                .Select(a => a.Replace("{prompt}", request.Prompt).Replace("{promptFile}", promptFile ?? "").Replace("{workdir}", request.WorkDir))
                .ToList();

            var text = new StringBuilder();
            var (exitCode, timedOut) = await CliProcess.RunAsync(
                definition.Command,
                args,
                request.WorkDir,
                usesPromptArg || promptFile is not null ? "" : request.Prompt,
                TimeSpan.FromMinutes(definition.TimeoutMinutes),
                line =>
                {
                    lock (text)
                    {
                        text.AppendLine(line);
                    }
                    emit(EventTypes.AgentText, line);
                },
                line => emit(EventTypes.System, "stderr: " + (line.Length > 400 ? line[..400] + "..." : line)),
                ct);

            return new AgentResult(exitCode, timedOut, text.ToString().Trim(), null, false, null, null);
        }
        finally
        {
            if (promptFile is not null)
            {
                try { File.Delete(promptFile); } catch (IOException) { }
            }
        }
    }
}
