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

    /// <summary>
    /// Muse's own shape for MCP servers: <c>{"command","args"}</c> for stdio and <c>{"url"}</c> for http (no <c>type</c> key; verified against muse 1.4.14, which refuses
    /// unknown keys). Its <c>env</c> values must be <c>${VAR}</c> references, so a server's env is not passed and a note says so.
    /// </summary>
    private static (List<string> Args, List<string> Notes) BuildMcpArgs(string configFile)
    {
        var args = new List<string>();
        var notes = new List<string>();
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configFile));
        if (!doc.RootElement.TryGetProperty("mcpServers", out var servers))
        {
            return (args, notes);
        }
        foreach (var server in servers.EnumerateObject())
        {
            var entry = new Dictionary<string, object?>();
            if (server.Value.TryGetProperty("url", out var url) && url.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                entry["url"] = url.GetString();
            }
            else
            {
                entry["command"] = server.Value.TryGetProperty("command", out var command) ? command.GetString() : null;
                entry["args"] = server.Value.TryGetProperty("args", out var a) ? a.EnumerateArray().Select(x => x.GetString()).ToArray() : Array.Empty<string?>();
            }
            if (server.Value.TryGetProperty("env", out var env) && env.ValueKind == System.Text.Json.JsonValueKind.Object && env.EnumerateObject().Any())
            {
                notes.Add($"MCP server '{server.Name}': its env is not passed to muse (muse only accepts ${{VAR}} references there).");
            }
            args.Add("-c");
            args.Add($"mcpServers.{server.Name}=" + System.Text.Json.JsonSerializer.Serialize(entry));
        }
        return (args, notes);
    }

    /// <summary>The binary the muse.cmd shim starts (<c>muse-bin-&lt;version&gt;.exe</c> beside it, version in <c>.muse-version</c>), or null when it is not where expected.</summary>
    private static string? FindRealBinary(string shim)
    {
        try
        {
            var dir = Path.GetDirectoryName(shim);
            var versionFile = dir is null ? null : Path.Combine(dir, ".muse-version");
            if (dir is null || versionFile is null || !File.Exists(versionFile))
            {
                return null;
            }
            var binary = Path.Combine(dir, $"muse-bin-{File.ReadAllText(versionFile).Trim()}.exe");
            return File.Exists(binary) ? binary : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

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
            if (options.MaxSteps is > 0 and var steps)
            {
                args.Add("--max-model-steps");
                args.Add(steps.ToString());
            }
            if (options.TrustWorkspace)
            {
                args.Add("--trust-workspace");
            }

            // MCP servers for this stage: `-c mcpServers.<name>=<json>` per server. The JSON has double quotes, which the Windows batch shim
            // mangles, so when MCP is wanted the real binary next to the shim is started directly (arguments are then passed exactly).
            var executable = options.Path;
            if (!string.IsNullOrWhiteSpace(request.McpConfigFile))
            {
                var (mcpArgs, notes) = BuildMcpArgs(request.McpConfigFile);
                foreach (var note in notes)
                {
                    emit(EventTypes.Warning, note);
                }
                if (mcpArgs.Count > 0)
                {
                    var direct = CliProcess.IsBatchFile(resolved) ? FindRealBinary(resolved) : resolved;
                    if (direct is null)
                    {
                        emit(EventTypes.Warning, "MCP servers were not passed to muse: the real muse binary was not found next to the muse.cmd shim (the shim cannot pass JSON). Muse runs without them.");
                    }
                    else
                    {
                        executable = direct;
                        args.AddRange(mcpArgs);
                    }
                }
            }
            args.AddRange(options.ExtraArgs);

            var parser = new MuseStreamParser();
            var diagnostics = new StringBuilder();
            var (exitCode, timedOut) = await CliProcess.RunAsync(
                executable,
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
            return new AgentResult(exitCode, timedOut, r.Text, null, r.IsError || (exitCode == 0 && !r.Seen), null, r.SessionId, r.Usage, diagnostics.ToString().Trim(), r.Tokens, r.Model ?? options.Model);
        }
        finally
        {
            try { File.Delete(promptFile); } catch (IOException) { }
        }
    }
}
