using System.Text;
using MoonWalk.Core;
using MoonWalk.Projects;

namespace MoonWalk.Agents;

/// <summary>
/// Muse Code (Meta) through <c>muse exec --json --prompt-file &lt;file&gt; --workspace &lt;dir&gt;</c>, streamed as live tool calls and results.
/// A read-only request (Spec stage) adds <c>--disable-write --disable-shell</c>. Shell execution is off by default for implementing too
/// (opt in with <see cref="MuseOptions.AllowShell"/>); Muse's own approval and sandbox stay on. The prompt goes through a temporary file,
/// never the command line, because on Windows <c>muse</c> is a batch shim.
/// </summary>
public sealed class MuseAgent(MuseOptions options) : IAgent
{
    private static readonly char[] UnsafeForBatch = ['&', '|', '<', '>', '^', '%', '!', '"'];

    public string Name => "muse";

    public async Task<AgentResult> RunAsync(AgentRequest request, EmitEvent emit, CancellationToken ct)
    {
        var resolved = CliProcess.Resolve(options.Path);
        if (CliProcess.IsBatchFile(resolved) && request.WorkDir.IndexOfAny(UnsafeForBatch) >= 0)
        {
            throw new InvalidOperationException(
                $"The folder '{request.WorkDir}' contains a character that is unsafe to pass through the muse batch shim ({string.Join(' ', UnsafeForBatch)}). Use a path without it.");
        }

        var promptFile = Path.Combine(Path.GetTempPath(), $"moonwalk-muse-{Guid.NewGuid():N}.md");
        try
        {
            File.WriteAllText(promptFile, request.Prompt, new UTF8Encoding(false));

            var args = new List<string> { "exec", "--json", "--prompt-file", promptFile, "--workspace", request.WorkDir };
            if (!string.IsNullOrWhiteSpace(options.Model))
            {
                args.Add("--model");
                args.Add(options.Model);
            }
            if (!string.IsNullOrWhiteSpace(options.Effort))
            {
                args.Add("--reasoning-effort");
                args.Add(options.Effort);
            }
            if (request.ReadOnly)
            {
                args.Add("--disable-write");
            }
            if (request.ReadOnly || !options.AllowShell)
            {
                args.Add("--disable-shell");
            }
            if (options.TrustWorkspace)
            {
                args.Add("--trust-workspace");
            }
            args.AddRange(options.ExtraArgs);

            var parser = new MuseStreamParser();
            var diagnostics = new StringBuilder();
            var (exitCode, timedOut) = await CliProcess.RunAsync(
                options.Path,
                args,
                request.WorkDir,
                "",
                TimeSpan.FromMinutes(options.TimeoutMinutes),
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
                    emit(EventTypes.System, "muse: " + (line.StartsWith("muse:") ? line[5..].Trim() : line));
                },
                ct);

            var r = parser.Result;
            return new AgentResult(exitCode, timedOut, r.Text, null, r.IsError || (exitCode == 0 && !r.Seen), null, r.SessionId, r.Usage, diagnostics.ToString().Trim());
        }
        finally
        {
            try { File.Delete(promptFile); } catch (IOException) { }
        }
    }
}
