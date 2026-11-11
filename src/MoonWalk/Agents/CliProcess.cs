using System.Diagnostics;
using System.Text;

namespace MoonWalk.Agents;

/// <summary>Runs a CLI agent: prompt on stdin, stdout and stderr streamed line by line, whole process tree killed on cancel or timeout.</summary>
public static class CliProcess
{
    public static async Task<(int ExitCode, bool TimedOut)> RunAsync(
        string exe,
        IReadOnlyList<string> args,
        string workDir,
        string stdin,
        TimeSpan timeout,
        Action<string> onStdout,
        Action<string> onStderr,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Resolve(exe))
        {
            WorkingDirectory = workDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        process.Start();
        ProcessJob.TryAssign(process); // dies with MoonWalk, even if MoonWalk is killed

        try
        {
            await process.StandardInput.WriteAsync(stdin.AsMemory(), ct);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The process exited before reading its input; the exit code will say why.
        }

        var stdout = PumpAsync(process.StandardOutput, onStdout);
        var stderr = PumpAsync(process.StandardError, onStderr);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            var killed = Kill(process, onStderr);
            // Never wait without a bound: a child we cannot kill (for example a sandbox account's process) can keep the
            // redirected pipes open, and WaitForExitAsync only returns once they close.
            using var grace = new CancellationTokenSource(KillGrace);
            try
            {
                await process.WaitForExitAsync(grace.Token);
            }
            catch (OperationCanceledException)
            {
                onStderr($"MoonWalk: the agent process did not stop within {KillGrace.TotalSeconds:0}s" + (killed ? "." : " and could not be killed (another user's process?)."));
            }
            if (ct.IsCancellationRequested)
            {
                throw;
            }
            timedOut = true;
        }

        // Pipes may stay open if a surviving grandchild holds them; do not wait for them forever.
        var pumps = Task.WhenAll(stdout, stderr);
        if (await Task.WhenAny(pumps, Task.Delay(KillGrace)) != pumps)
        {
            onStderr("MoonWalk: stopped reading agent output (a leftover process still holds the pipes).");
        }
        return (process.HasExited ? process.ExitCode : -1, timedOut);
    }

    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Finds a command the way a shell would (PATH plus PATHEXT), so "muse" finds muse.cmd. CreateProcess alone only appends ".exe".
    /// A command with a folder part, or one that is not found, is returned unchanged.
    /// </summary>
    public static string Resolve(string command)
    {
        if (command.IndexOfAny(['\\', '/']) >= 0 || !OperatingSystem.IsWindows())
        {
            return command;
        }

        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        var names = Path.HasExtension(command) ? [command] : extensions.Select(e => command + e).Prepend(command).ToArray();

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim('"'), name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry: skip it.
                }
            }
        }
        return command;
    }

    public static bool IsBatchFile(string resolved) =>
        resolved.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || resolved.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Length > 0)
                {
                    onLine(line);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The process was disposed while we stopped waiting for a stuck pipe.
        }
    }

    /// <summary>Kills the tree. Returns false when part of it could not be killed (access denied for another user's process).</summary>
    private static bool Kill(Process process, Action<string> log)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (InvalidOperationException)
        {
            return true; // Already exited.
        }
        catch (Exception ex)
        {
            log($"MoonWalk: could not kill the whole process tree: {ex.Message}");
            try { process.Kill(); } catch { }
            return false;
        }
    }
}
