using System.Collections.Concurrent;
using System.Text.Json;

namespace MoonWalk.Core;

/// <summary>
/// Append-only per-run event log (runs/&lt;id&gt;/events.jsonl) with live fan-out.
/// The log file is the source of truth; the UI replays it and then tails new events.
/// </summary>
public sealed class EventHub(string runsRoot)
{
    private readonly ConcurrentDictionary<string, RunLog> _logs = new();

    public RunEvent Append(string runId, string type, string? stage, string? text, Dictionary<string, object?>? data = null) =>
        Log(runId).Append(type, stage, text, data);

    /// <summary>Atomically subscribes and reads the backlog after <paramref name="afterSeq"/>, so no event is lost or duplicated.</summary>
    public (IReadOnlyList<RunEvent> Backlog, Fanout<RunEvent>.Subscription Subscription) OpenStream(string runId, long afterSeq) =>
        Log(runId).OpenStream(afterSeq);

    /// <summary>Releases the open log file of a run, so its folder can be deleted.</summary>
    public void Close(string runId)
    {
        if (_logs.TryRemove(runId, out var log))
        {
            log.Dispose();
        }
    }

    private RunLog Log(string runId) =>
        _logs.GetOrAdd(runId, id => new RunLog(id, Path.Combine(runsRoot, id)));

    private sealed class RunLog : IDisposable
    {
        public void Dispose()
        {
            lock (_lock)
            {
                _writer.Dispose();
            }
        }

        private readonly object _lock = new();
        private readonly string _runId;
        private readonly string _path;
        private readonly StreamWriter _writer;
        private readonly Fanout<RunEvent> _fanout = new();
        private long _seq;

        public RunLog(string runId, string dir)
        {
            _runId = runId;
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "events.jsonl");
            if (File.Exists(_path))
            {
                _seq = File.ReadLines(_path).LongCount(line => !string.IsNullOrWhiteSpace(line));
            }
            var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _writer = new StreamWriter(stream) { AutoFlush = true };
        }

        public RunEvent Append(string type, string? stage, string? text, Dictionary<string, object?>? data)
        {
            lock (_lock)
            {
                var ev = new RunEvent(++_seq, DateTimeOffset.Now, _runId, type, stage, text, data);
                _writer.WriteLine(JsonSerializer.Serialize(ev, Json.Options));
                _fanout.Publish(ev);
                return ev;
            }
        }

        public (IReadOnlyList<RunEvent>, Fanout<RunEvent>.Subscription) OpenStream(long afterSeq)
        {
            lock (_lock)
            {
                var subscription = _fanout.Subscribe();
                return (ReadFrom(afterSeq), subscription);
            }
        }

        private List<RunEvent> ReadFrom(long afterSeq)
        {
            var result = new List<RunEvent>();
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                var ev = JsonSerializer.Deserialize<RunEvent>(line, Json.Options);
                if (ev is not null && ev.Seq > afterSeq)
                {
                    result.Add(ev);
                }
            }
            return result;
        }
    }
}
