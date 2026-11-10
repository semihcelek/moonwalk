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
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            if (ct.IsCancellationRequested)
            {
                throw;
            }
            timedOut = true;
        }

        await Task.WhenAll(stdout, stderr);
        return (process.ExitCode, timedOut);
    }

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
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length > 0)
            {
                onLine(line);
            }
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }
}
