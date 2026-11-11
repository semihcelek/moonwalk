using MoonWalk.Core;
using MoonWalk.Projects;

namespace MoonWalk.Agents;

public sealed class ClaudeAgent(ClaudeOptions options) : IAgent
{
    public string Name => "claude";

    public async Task<AgentResult> RunAsync(AgentRequest request, EmitEvent emit, CancellationToken ct)
    {
        var args = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            "--max-turns", options.MaxTurns.ToString(),
        };
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
        if (request.MaxBudgetUsd is > 0 and var budget)
        {
            args.Add("--max-budget-usd");
            args.Add(budget.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrWhiteSpace(request.McpConfigFile))
        {
            // Only the servers MoonWalk wrote for this stage; the project's own .mcp.json and the user's servers stay out of this run.
            args.Add("--mcp-config");
            args.Add(request.McpConfigFile);
            args.Add("--strict-mcp-config");
        }
        var allowed = request.AllowedTools ?? options.AllowedTools;
        var disallowed = request.DisallowedTools ?? options.DisallowedTools;
        if (allowed.Length > 0)
        {
            args.Add("--allowedTools");
            args.AddRange(allowed);
        }
        if (disallowed.Length > 0)
        {
            args.Add("--disallowedTools");
            args.AddRange(disallowed);
        }
        args.AddRange(options.ExtraArgs);

        var parser = new ClaudeStreamParser(request.WorkDir);
        var (exitCode, timedOut) = await CliProcess.RunAsync(
            options.Path,
            args,
            request.WorkDir,
            request.Prompt,
            TimeSpan.FromMinutes(options.TimeoutMinutes),
            line =>
            {
                foreach (var ev in parser.Feed(line))
                {
                    emit(ev.Type, ev.Text, ev.Data);
                }
            },
            line => emit(EventTypes.Warning, line.Length > 400 ? line[..400] + "..." : line),
            ct);

        var r = parser.Result;
        return new AgentResult(exitCode, timedOut, r.Text, r.CostUsd, r.IsError, r.NumTurns, r.SessionId,
            Usage: r.Tokens is { } tokens ? $"{tokens:N0} tokens" : null, Diagnostics: r.Errors, Tokens: r.Tokens, Model: r.Model ?? options.Model);
    }
}
