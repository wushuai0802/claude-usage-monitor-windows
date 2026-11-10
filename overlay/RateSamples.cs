using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;

// Consumes only timing/count snapshots written by the optional streaming observer.
internal sealed class RateSnapshots
{
    private sealed class Frame
    {
        internal string SessionId, Phase;
        internal DateTime At, TurnStartedAt;
        internal DateTime? SampleAt;
        internal long Sequence, OutputTokens, DurationMs;
        internal double Average;
        internal bool Cached;
    }
    private readonly string _folder;
    private readonly SnapshotReader _reader;
    private string[] _files = new string[0];
    private DateTime _nextList;
    internal RateSnapshots(string root) { _folder = Path.Combine(root, "rates"); _reader = new SnapshotReader(root, 1); }
    private Frame Latest(DateTime now)
    {
        if (now >= _nextList) {
            _nextList = now.AddSeconds(2);
            try { _files = Directory.Exists(_folder) ? Directory.GetFiles(_folder, "*.json") : new string[0]; }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Frame latest = null;
        foreach (var path in _files) {
            bool failed;
            var d = _reader.Read(path, out failed);
            if (!Equals(JsonData.Get(d, "kind"), "modelRate")) continue;
            var id = JsonData.Get(d, "sessionId") as string;
            var at = JsonData.Time(JsonData.Get(d, "updatedAt"));
            var started = JsonData.Time(JsonData.Get(d, "turnStartedAt"));
            var phase = JsonData.Get(d, "phase") as string;
            if (string.IsNullOrEmpty(id) || !Path.GetFileName(path).StartsWith(id + ".slot", StringComparison.OrdinalIgnoreCase)
                || at == null || started == null || at < started || at > now.AddSeconds(2)
                || !new[] { "waiting", "generating", "tools", "completed", "interrupted", "error" }.Contains(phase)) continue;
            var frame = new Frame { SessionId = id, At = at.Value, TurnStartedAt = started.Value, Phase = phase, Sequence = JsonData.Number(d, "sequence"), Cached = failed };
            var sample = JsonData.Map(JsonData.Get(d, "sample"));
            var sampleAt = JsonData.Time(JsonData.Get(sample, "completedAt"));
            long output, duration = JsonData.Number(sample, "durationMs");
            bool counted = long.TryParse(Convert.ToString(JsonData.Get(sample, "outputTokens"), CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out output);
            if (sampleAt != null && sampleAt >= started && sampleAt <= at && counted && output >= 0 && output <= int.MaxValue && duration >= 50 && duration <= 86400000) {
                frame.SampleAt = sampleAt; frame.OutputTokens = output; frame.DurationMs = duration; frame.Average = output * 1000.0 / duration;
            }
            if (latest == null || frame.At > latest.At || (frame.At == latest.At && frame.Sequence > latest.Sequence)) latest = frame;
        }
        return latest;
    }
    internal void Apply(Metrics m, DateTime now)
    {
        var f = Latest(now); if (f == null) return;
        bool terminal = f.Phase == "completed" || f.Phase == "interrupted" || f.Phase == "error";
        // Heartbeats exist only while a main turn is active. Never use an abandoned
        // observer to override newer transcript activity or a newly submitted prompt.
        if ((!terminal && (now - f.At).TotalSeconds > 15) || (m.SessionId != f.SessionId && m.ActivityAt != null && f.At < m.ActivityAt)
            || (m.SessionId == f.SessionId && m.HumanPromptAt != null && f.TurnStartedAt < m.HumanPromptAt)
            || (terminal && m.ActivityAt != null && f.At < m.ActivityAt.Value.AddSeconds(-2))) return;
        bool same = m.SessionId == f.SessionId;
        m.SessionId = f.SessionId; m.ActivityAt = f.At;
        if (!same) m.ContextTokens = null;
        m.RateState = f.Phase; m.RateSource = "turn.step"; m.LastRate = f.Average;
        m.RateAt = f.SampleAt; m.RateOutputTokens = f.OutputTokens; m.RateDurationMs = f.DurationMs; m.SnapshotError |= f.Cached;
        bool valid = f.SampleAt != null && f.Phase != "interrupted" && f.Phase != "error";
        m.Rate = valid && (!terminal || (now - f.SampleAt.Value).TotalSeconds <= 5) ? f.Average : 0;
        if (f.Phase == "completed" && m.Rate == 0) m.RateState = "idle";
    }
}
