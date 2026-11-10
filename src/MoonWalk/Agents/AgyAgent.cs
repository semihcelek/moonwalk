using System.Text;
using System.Text.Json;
using MoonWalk.Core;
using MoonWalk.Projects;

namespace MoonWalk.Agents;

/// <summary>
/// Gemini through the Antigravity CLI (<c>agy</c>). Runs <c>agy -p &lt;prompt&gt; --output-format stream-json --mode accept-edits</c>;
/// the prompt must be an argument because print mode does not read stdin. A read-only request (Spec stage) uses <c>--mode plan</c>.
/// In headless mode agy cannot ask for permission, and a denied shell command ends the session, so by default the prompt tells it to
/// use file tools only; <see cref="AgyOptions.AllowedCommands"/> opts in to shell commands through a temporary settings file.
/// agy has no tool allow/deny lists beyond that, so the stages check the result rather than trusting it.
/// </summary>
public sealed class AgyAgent(AgyOptions options) : IAgent
{
    private const string NoShellNote =
        "\n\nEnvironment note: shell commands are not permitted here, and trying one ends the session. " +
        "Use only your file tools (list, read, search, create and edit files). Do not run any command.";

    public string Name => "agy";

    public async Task<AgentResult> RunAsync(AgentRequest request, EmitEvent emit, CancellationToken ct)
    {
        var allowCommands = options.AllowedCommands.Length > 0;
        var prompt = allowCommands ? request.Prompt : request.Prompt + NoShellNote;

        string? settingsFile = null;
        try
        {
            var args = new List<string>
            {
                "-p", prompt,
                "--output-format", "stream-json",
                "--mode", request.ReadOnly ? "plan" : options.Mode,
                "--print-timeout", $"{options.TimeoutMinutes}m",
            };
            if (allowCommands)
            {
                settingsFile = Path.Combine(Path.GetTempPath(), $"moonwalk-agy-settings-{Guid.NewGuid():N}.json");
                File.WriteAllText(settingsFile,
                    JsonSerializer.Serialize(new { permissions = new { allow = options.AllowedCommands.Select(c => $"command({c})").ToArray() } }),
                    new UTF8Encoding(false));
                args.Add("--settings");
                args.Add(settingsFile);
            }
            if (!string.IsNullOrWhiteSpace(options.Model))
            {
                args.Add("--model");
                args.Add(options.Model);
            }
            if (!string.IsNullOrWhiteSpace(options.Effort))
            {
                args.Add("--effort");
                args.Add(options.Effort);
            }
            args.AddRange(options.ExtraArgs);

            var parser = new AgyStreamParser(request.WorkDir);
            var diagnostics = new StringBuilder();
            var (exitCode, timedOut) = await CliProcess.RunAsync(
                options.Path,
                args,
                request.WorkDir,
                "",
                TimeSpan.FromMinutes(options.TimeoutMinutes + 1),
                line =>
                {
                    foreach (var ev in parser.Feed(line))
                    {
                        emit(ev.Type, ev.Text, ev.Data);
                    }
                },
                line =>
                {
                    lock (diagnostics)
                    {
                        diagnostics.AppendLine(line);
                    }
                    emit(EventTypes.Warning, line.Length > 400 ? line[..400] + "..." : line);
                },
                ct);

            var r = parser.Result;
            return new AgentResult(exitCode, timedOut, r.Text, null, r.IsError, r.NumTurns, r.SessionId, r.Usage, diagnostics.ToString().Trim());
        }
        finally
        {
            if (settingsFile is not null)
            {
                try { File.Delete(settingsFile); } catch (IOException) { }
            }
        }
    }
}
