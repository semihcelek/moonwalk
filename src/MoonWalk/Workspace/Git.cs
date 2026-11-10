using System.Diagnostics;
using System.Text;

namespace MoonWalk.Workspace;

public sealed class WorkspaceException(string message) : Exception(message);

public readonly record struct GitResult(int Exit, string Out, string Err)
{
    public bool Ok => Exit == 0;
}

/// <summary>Thin wrapper over the git CLI. Never prompts, never opens a pager.</summary>
public static class Git
{
    public static async Task<GitResult> RunAsync(string repo, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(repo);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.quotepath=false");
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_PAGER"] = "cat";

        using var process = Process.Start(psi) ?? throw new WorkspaceException("Could not start git.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return new GitResult(process.ExitCode, await stdout, await stderr);
    }

    public static async Task<string> RunOrThrowAsync(string repo, CancellationToken ct, params string[] args)
    {
        var result = await RunAsync(repo, ct, args);
        if (!result.Ok)
        {
            var detail = string.IsNullOrWhiteSpace(result.Err) ? result.Out : result.Err;
            throw new WorkspaceException($"git {string.Join(' ', args.Take(3))} failed: {detail.Trim()}");
        }
        return result.Out;
    }
}
