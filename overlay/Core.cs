// Read-only local usage collection. No credentials, network or conversation text in state files.
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

internal sealed class TokenUsage
{
    public long Input, CacheWrite, CacheRead, Output;
    public long Total { get { return Input + CacheWrite + CacheRead + Output; } }
    public void Add(TokenUsage u, int sign) { Input += sign * u.Input; CacheWrite += sign * u.CacheWrite; CacheRead += sign * u.CacheRead; Output += sign * u.Output; }
    public TokenUsage Copy() { return (TokenUsage)MemberwiseClone(); }
}
internal sealed class Limit { public double? Used; public DateTime? ResetsAt; public bool Expired; }
internal sealed class Metrics
{
    public double Rate, DisplayRate;
    public TokenUsage Today = new TokenUsage();
    public Limit FiveHour, SevenDay;
    public DateTime? LimitsAt, ContextAt, ActivityAt;
    public double? ContextPercent;
    public long? ContextTokens;
    public string SessionId, RateState = "idle", PluginVersion;
    public string RateSource = "jsonl-estimate";
    public double LastRate;
    public DateTime? RateAt, HumanPromptAt;
    public long RateOutputTokens, RateDurationMs;
    public bool SnapshotError, Incomplete;
    public DateTime At;
}
internal static class JsonData
{
    internal static Dictionary<string, object> Parse(string text) { return new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(text.TrimStart('\uFEFF')); }
    internal static object Get(Dictionary<string, object> d, string key) { object v; return d != null && d.TryGetValue(key, out v) ? v : null; }
    internal static Dictionary<string, object> Map(object o) { return o as Dictionary<string, object>; }
    internal static long Number(Dictionary<string, object> d, string key) { long n; return long.TryParse(Convert.ToString(Get(d, key), CultureInfo.InvariantCulture), out n) && n >= 0 ? n : 0; }
    internal static double? Percent(object o)
    {
        double n; return o != null && double.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out n)
            && !double.IsNaN(n) && !double.IsInfinity(n) && n >= 0 && n <= 100 ? n : (double?)null;
    }
    internal static DateTime? Time(object o)
    {
        DateTimeOffset n; return DateTimeOffset.TryParse(o as string, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out n) ? n.LocalDateTime : (DateTime?)null;
    }
    internal static void WriteAtomic(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            File.WriteAllText(temp, new JavaScriptSerializer().Serialize(value), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
        } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
internal sealed class SnapshotReader
{
    private sealed class Entry { public long Length; public DateTime Write; public Dictionary<string, object> Data; public bool Error; }
    private readonly string _root;
    private readonly int _schemaVersion;
    private readonly Dictionary<string, Entry> _cache = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
    private string[] _paths = new string[0];
    private DateTime _nextList;
    internal SnapshotReader(string root, int schemaVersion = 2) { _root = root; _schemaVersion = schemaVersion; }
    internal Dictionary<string, object> Read(string path, out bool failed)
    {
        Entry entry;
        if (!_cache.TryGetValue(path, out entry)) { entry = new Entry(); _cache[path] = entry; }
        try {
            var info = new FileInfo(path);
            if (!info.Exists) { failed = entry.Data != null; return entry.Data; }
            if (entry.Data != null && entry.Write == info.LastWriteTimeUtc && entry.Length == info.Length) { failed = entry.Error; return entry.Data; }
            if (info.Length > 65536) throw new InvalidDataException();
            Dictionary<string, object> data;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8)) data = JsonData.Parse(reader.ReadToEnd());
            if (data == null) throw new InvalidDataException();
            if (path.EndsWith(".slot0.json") || path.EndsWith(".slot1.json")) {
                if (JsonData.Number(data, "schemaVersion") != _schemaVersion || !Equals(JsonData.Get(data, "complete"), true)
                    || JsonData.Time(JsonData.Get(data, "updatedAt")) == null) throw new InvalidDataException();
            }
            entry.Data = data; entry.Write = info.LastWriteTimeUtc; entry.Length = info.Length; entry.Error = false;
        } catch (Exception ex) {
            if (!(ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidOperationException)) throw;
            entry.Error = true;
        }
        failed = entry.Error; return entry.Data;
    }
    internal void Apply(Metrics m, DateTime now)
    {
        if (now >= _nextList) {
            _nextList = now.AddSeconds(5);
            var folder = Path.Combine(_root, "measurements");
            try { _paths = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json") : new string[0]; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        bool failed;
        var legacy = Read(Path.Combine(_root, "limits.json"), out failed);
        m.SnapshotError |= failed;
        var limitsAt = JsonData.Time(JsonData.Get(legacy, "updatedAt"));
        long limitsSequence = -1, contextSequence = -1;
        string limitsSource = null;
        object limits = JsonData.Get(legacy, "rateLimits");
        Dictionary<string, object> context = null;
        DateTime? contextAt = null;
        if (!string.IsNullOrEmpty(m.SessionId)) {
            var path = Path.Combine(_root, "context", m.SessionId + ".json");
            context = Read(path, out failed); m.SnapshotError |= failed;
            contextAt = JsonData.Time(JsonData.Get(context, "updatedAt"));
            if (context != null && contextAt == null && File.Exists(path)) contextAt = File.GetLastWriteTime(path);
        }
        foreach (var path in _paths) {
            var snapshot = Read(path, out failed); m.SnapshotError |= failed;
            if (snapshot == null || JsonData.Number(snapshot, "schemaVersion") != 2) continue;
            long sequence = JsonData.Number(snapshot, "sequence");
            var source = JsonData.Get(snapshot, "sessionId") as string;
            var at = JsonData.Time(JsonData.Get(snapshot, "rateLimitsAt"));
            if (at != null && (limitsAt == null || at > limitsAt || (at == limitsAt && source == limitsSource && sequence > limitsSequence)) && at <= now.AddMinutes(2)) {
                limitsAt = at; limitsSource = source; limitsSequence = sequence; limits = JsonData.Get(snapshot, "rateLimits");
            }
            at = JsonData.Time(JsonData.Get(snapshot, "contextAt"));
            if (string.Equals(JsonData.Get(snapshot, "sessionId") as string, m.SessionId, StringComparison.OrdinalIgnoreCase)
                && at != null && (contextAt == null || at > contextAt || (at == contextAt && sequence > contextSequence)) && at <= now.AddMinutes(2)) {
                contextAt = at; contextSequence = sequence; context = JsonData.Map(JsonData.Get(snapshot, "context"));
                m.PluginVersion = JsonData.Get(snapshot, "pluginVersion") as string;
            }
        }
        m.LimitsAt = limitsAt; m.ContextAt = contextAt;
        m.ContextPercent = JsonData.Percent(JsonData.Get(context, "percent"));
        if (context != null && context.ContainsKey("tokens")) m.ContextTokens = JsonData.Get(context, "tokens") == null ? (long?)null : JsonData.Number(context, "tokens");
        var list = limits as System.Collections.IEnumerable;
        if (list == null) return;
        foreach (var obj in list) {
            var item = JsonData.Map(obj); if (item == null) continue;
            var limit = new Limit { Used = JsonData.Percent(JsonData.Get(item, "percentUsed")), ResetsAt = JsonData.Time(JsonData.Get(item, "resetsAt")) };
            if (limit.ResetsAt != null && limit.ResetsAt <= now) { limit.Used = null; limit.Expired = true; }
            switch (JsonData.Get(item, "kind") as string) { case "five_hour": m.FiveHour = limit; break; case "seven_day": m.SevenDay = limit; break; }
        }
    }
}
internal sealed class FileCursor : IDisposable
{
    public string SessionId;
    public bool IsSubagent, SkipLine;
    public long Offset, PromptTokens;
    public DateTime LastActivity, LastTurnRecord, CurrentStart;
    public DateTime LastHumanPrompt, RateAt;
    public long RateOutputTokens, RateDurationMs, ResponseOutput;
    private readonly HashSet<string> _responses = new HashSet<string>();
    private readonly Queue<string> _responseOrder = new Queue<string>();
    public string LastMessageId, State = "idle";
    public double Rate;
    public readonly MemoryStream Partial = new MemoryStream();
    public bool Seen(string id) { return _responses.Contains(id); }
    public void Remember(string id) { if (_responses.Add(id)) _responseOrder.Enqueue(id); while (_responseOrder.Count > 256) _responses.Remove(_responseOrder.Dequeue()); }
    public void Reset() { Offset = 0; Partial.SetLength(0); SkipLine = false; Rate = 0; State = "idle"; LastMessageId = null; LastActivity = LastTurnRecord = CurrentStart = LastHumanPrompt = RateAt = DateTime.MinValue; RateOutputTokens = RateDurationMs = ResponseOutput = 0; _responses.Clear(); _responseOrder.Clear(); }
    public void Dispose() { Partial.Dispose(); }
}
internal sealed class Collector : IDisposable
{
    private sealed class Counted { internal DateTime At; internal TokenUsage Usage; }
    private readonly string _projects;
    private readonly Func<DateTime> _clock;
    private readonly SnapshotReader _snapshots;
    private readonly RateSnapshots _rates;
    private double _heldRate;
    private readonly Dictionary<string, FileCursor> _files = new Dictionary<string, FileCursor>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Counted> _messages = new Dictionary<string, Counted>();
    private readonly ConcurrentDictionary<string, byte> _changed = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _backlog = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly TokenUsage _total = new TokenUsage();
    private FileSystemWatcher _watcher;
    private DateTime _day, _nextScan, _nextFallback;
    private volatile bool _rescan = true;
    private bool _incomplete;
    internal int DirectoryScans { get; private set; }
    internal long BytesRead { get; private set; }
    internal Collector(string projects, string limitsPath) : this(projects, limitsPath, () => DateTime.Now, true) { }
    internal Collector(string projects, string limitsPath, Func<DateTime> clock, bool watch)
    {
        _projects = Path.GetFullPath(projects).TrimEnd(Path.DirectorySeparatorChar);
        _clock = clock; _day = clock().Date; _snapshots = new SnapshotReader(Path.GetDirectoryName(limitsPath));
        _rates = new RateSnapshots(Path.GetDirectoryName(limitsPath));
        if (watch && Directory.Exists(_projects)) {
            try {
                _watcher = new FileSystemWatcher(_projects, "*.jsonl") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                _watcher.Changed += (s, e) => Changed(e.FullPath);
                _watcher.Created += (s, e) => Changed(e.FullPath);
                _watcher.Renamed += (s, e) => { Changed(e.FullPath); _rescan = true; };
                _watcher.Error += (s, e) => _rescan = true;
                _watcher.EnableRaisingEvents = true;
            } catch (IOException) { if (_watcher != null) _watcher.Dispose(); _watcher = null; }
        }
    }
    internal void Changed(string path)
    {
        if (_changed.Count > 10000) { _rescan = true; return; }
        if (path.StartsWith(_projects + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) _changed[path] = 0;
    }
    private void Discover()
    {
        DirectoryScans++;
        if (!Directory.Exists(_projects)) return;
        var stack = new Stack<string>(); stack.Push(_projects);
        while (stack.Count > 0) {
            var dir = stack.Pop();
            try {
                foreach (var path in Directory.GetFiles(dir, "*.jsonl")) if (File.GetLastWriteTime(path) >= _day) Changed(path);
                foreach (var sub in Directory.GetDirectories(dir)) if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0) stack.Push(sub);
            } catch (IOException) { _incomplete = true; } catch (UnauthorizedAccessException) { _incomplete = true; }
        }
    }
    internal Metrics Poll()
    {
        var now = _clock();
        if (now.Date != _day) {
            _day = now.Date; foreach (var f in _files.Values) f.Dispose(); _files.Clear(); _messages.Clear(); _backlog.Clear();
            _total.Input = _total.CacheWrite = _total.CacheRead = _total.Output = 0; _incomplete = false; _rescan = true;
        }
        if (_rescan || now >= _nextScan) { _rescan = false; _nextScan = now.AddSeconds(60); Discover(); }
        var paths = new HashSet<string>(_backlog, StringComparer.OrdinalIgnoreCase); _backlog.Clear();
        foreach (var path in _changed.Keys) { byte ignored; if (_changed.TryRemove(path, out ignored)) paths.Add(path); }
        if (now >= _nextFallback) { foreach (var path in _files.Keys) paths.Add(path); _nextFallback = now.AddSeconds(5); }
        foreach (var path in paths) {
            try {
                FileCursor cursor;
                if (!_files.TryGetValue(path, out cursor)) {
                    bool child = string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "subagents", StringComparison.OrdinalIgnoreCase);
                    cursor = new FileCursor { IsSubagent = child, SessionId = child ? Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path))) : Path.GetFileNameWithoutExtension(path) };
                    _files[path] = cursor;
                }
                ReadNew(path, cursor);
            } catch (IOException) { } catch (UnauthorizedAccessException) { _incomplete = true; }
        }
        var m = new Metrics { At = now, Today = _total.Copy(), Incomplete = _incomplete || _backlog.Count != 0 };
        var active = _files.Values.Where(f => !f.IsSubagent && f.LastActivity != DateTime.MinValue).OrderByDescending(f => f.LastActivity).FirstOrDefault();
        if (active != null) {
            m.SessionId = active.SessionId; m.ActivityAt = active.LastActivity; m.ContextTokens = active.PromptTokens > 0 ? active.PromptTokens : (long?)null;
            var age = (now - active.LastActivity).TotalSeconds; m.RateState = active.State;
            m.LastRate = active.Rate; m.RateAt = active.RateAt == DateTime.MinValue ? (DateTime?)null : active.RateAt;
            m.RateOutputTokens = active.RateOutputTokens; m.RateDurationMs = active.RateDurationMs;
            m.HumanPromptAt = active.LastHumanPrompt == DateTime.MinValue ? (DateTime?)null : active.LastHumanPrompt;
            // This is the latest request's average, retained during the same turn.
            // The log-only fallback has no heartbeat, so stop retaining after two minutes.
            m.Rate = age >= 0 && age <= (active.State == "completed" ? 5 : 120) ? active.Rate : 0;
            if (age > 5 && active.State == "completed") m.RateState = "idle";
        }
        _rates.Apply(m, now);
        _snapshots.Apply(m, now);
        // The strip keeps the latest valid request average instead of dropping to 0 between turns.
        if (m.Rate > 0) _heldRate = m.Rate;
        m.DisplayRate = _heldRate; return m;
    }
    private void ReadNew(string path, FileCursor cursor)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
            if (stream.Length < cursor.Offset) cursor.Reset();
            stream.Position = cursor.Offset; var buffer = new byte[65536]; int budget = 4 * 1024 * 1024;
            while (budget > 0) {
                int n = stream.Read(buffer, 0, Math.Min(buffer.Length, budget)); if (n == 0) break;
                BytesRead += n; budget -= n; cursor.Offset += n; int start = 0;
                for (int i = 0; i < n; i++) if (buffer[i] == (byte)'\n') {
                    Append(cursor, buffer, start, i - start);
                    if (!cursor.SkipLine) ProcessLine(Encoding.UTF8.GetString(cursor.Partial.GetBuffer(), 0, (int)cursor.Partial.Length), cursor);
                    cursor.Partial.SetLength(0); cursor.SkipLine = false; start = i + 1;
                }
                Append(cursor, buffer, start, n - start);
            }
            if (stream.Position < stream.Length) _backlog.Add(path);
        }
    }
    private void Append(FileCursor cursor, byte[] bytes, int offset, int count)
    {
        if (cursor.SkipLine) return;
        if (cursor.Partial.Length + count > 16 * 1024 * 1024) { cursor.Partial.SetLength(0); cursor.SkipLine = true; _incomplete = true; return; }
        cursor.Partial.Write(bytes, offset, count);
    }
    private void ProcessLine(string line, FileCursor cursor)
    {
        if (!line.Contains("\"assistant\"") && !line.Contains("\"user\"")) return;
        Dictionary<string, object> record;
        try { record = JsonData.Parse(line); } catch (ArgumentException) { _incomplete = true; return; } catch (InvalidOperationException) { _incomplete = true; return; }
        var at = JsonData.Time(JsonData.Get(record, "timestamp")); if (at == null) return;
        var type = JsonData.Get(record, "type") as string;
        if (type == "user") {
            if (at < cursor.LastActivity) return;
            if (Equals(JsonData.Get(record, "isMeta"), true)) return;
            cursor.LastTurnRecord = cursor.LastActivity = at.Value; cursor.State = "waiting";
            if (!IsToolResult(record)) {
                cursor.LastHumanPrompt = at.Value; cursor.Rate = 0; cursor.RateAt = DateTime.MinValue;
                cursor.RateOutputTokens = cursor.RateDurationMs = 0;
            }
            return;
        }
        if (type != "assistant") return;
        var message = JsonData.Map(JsonData.Get(record, "message")); var usage = JsonData.Map(JsonData.Get(message, "usage")); var id = JsonData.Get(message, "id") as string;
        if (usage == null || string.IsNullOrEmpty(id) || Equals(JsonData.Get(message, "model"), "<synthetic>")) return;
        var tokens = new TokenUsage { Input = JsonData.Number(usage, "input_tokens"), CacheWrite = JsonData.Number(usage, "cache_creation_input_tokens"), CacheRead = JsonData.Number(usage, "cache_read_input_tokens"), Output = JsonData.Number(usage, "output_tokens") };
        Counted previous = null;
        if (at.Value.Date == _day && (!_messages.TryGetValue(id, out previous) || at > previous.At || (at == previous.At && tokens.Output > previous.Usage.Output))) {
            if (previous != null) _total.Add(previous.Usage, -1);
            _messages[id] = new Counted { At = at.Value, Usage = tokens }; _total.Add(tokens, 1);
        }
        bool seen = cursor.Seen(id);
        // The same response can be recorded again after a tool result. It must not
        // become a new request or use that tool-result timestamp as its start.
        if (seen && (cursor.LastMessageId != id || cursor.State != "generating" || tokens.Output <= cursor.ResponseOutput)) return;
        if (!seen) {
            cursor.Remember(id);
            if (at < cursor.LastActivity) return;
            cursor.LastMessageId = id; cursor.CurrentStart = cursor.LastTurnRecord; cursor.ResponseOutput = 0;
        }
        if (at < cursor.LastActivity) return;
        cursor.PromptTokens = tokens.Input + tokens.CacheWrite + tokens.CacheRead;
        double seconds = (at.Value - cursor.CurrentStart).TotalSeconds;
        if (cursor.CurrentStart != DateTime.MinValue && seconds >= 0.5 && tokens.Output > 0) {
            cursor.Rate = tokens.Output / seconds; cursor.RateAt = at.Value;
            cursor.RateOutputTokens = tokens.Output; cursor.RateDurationMs = (long)Math.Round(seconds * 1000);
        }
        cursor.ResponseOutput = tokens.Output; cursor.LastActivity = at.Value;
        var stop = JsonData.Get(message, "stop_reason") as string;
        cursor.State = stop == "tool_use" ? "tools" : stop != null ? "completed" : "generating";
    }
    private static bool IsToolResult(Dictionary<string, object> record)
    {
        var message = JsonData.Map(JsonData.Get(record, "message"));
        var content = JsonData.Get(message, "content") as System.Collections.IEnumerable;
        if (content == null || content is string) return false;
        foreach (var block in content) if (Equals(JsonData.Get(JsonData.Map(block), "type"), "tool_result")) return true;
        return false;
    }
    public void Dispose() { if (_watcher != null) _watcher.Dispose(); foreach (var f in _files.Values) f.Dispose(); }
}
internal sealed class DisplaySettings
{
    internal static readonly string[] Keys = { "rate", "today", "cache", "limits", "context" };
    public List<string> Selected = new List<string>(Keys);
    public List<string> Priority = new List<string>(new[] { "limits", "context", "rate", "today", "cache" });
    public string Theme = "auto";
    internal static DisplaySettings Load(string path)
    {
        foreach (var candidate in new[] { path, path + ".bak" }) {
            try {
                if (!File.Exists(candidate)) continue;
                var d = JsonData.Parse(File.ReadAllText(candidate)); var result = new DisplaySettings();
                var selected = JsonData.Get(d, "selected") as System.Collections.IEnumerable;
                if (selected != null) result.Selected = selected.Cast<object>().OfType<string>().Where(Keys.Contains).Distinct().ToList();
                var order = JsonData.Get(d, "priority") as System.Collections.IEnumerable;
                if (order != null) result.Priority = order.Cast<object>().OfType<string>().Where(Keys.Contains).Concat(result.Priority).Distinct().ToList();
                var theme = JsonData.Get(d, "theme") as string;
                if (theme == "auto" || theme == "light" || theme == "dark") result.Theme = theme;
                return result;
            } catch (Exception ex) { if (!(ex is IOException || ex is ArgumentException || ex is InvalidOperationException || ex is UnauthorizedAccessException)) throw; }
        }
        return new DisplaySettings();
    }
    internal void Save(string path) { JsonData.WriteAtomic(path, new { schemaVersion = 1, selected = Selected, priority = Priority, theme = Theme }); }
    internal List<string> Fit(Func<string, double> measure, double available)
    {
        var keys = Keys.Where(Selected.Contains).ToList();
        while (keys.Count > 0 && measure(string.Join(",", keys)) > available) { var last = Priority.LastOrDefault(keys.Contains) ?? keys.Last(); keys.Remove(last); }
        return keys;
    }
}
internal static class MetricText
{
    internal static string Tokens(long n) { return n >= 100000000 ? (n / 100000000.0).ToString("0.00", CultureInfo.InvariantCulture) + "亿" : n >= 10000 ? (n / 10000.0).ToString("0.0", CultureInfo.InvariantCulture) + "万" : n.ToString(CultureInfo.InvariantCulture); }
    internal static string Percent(Limit l) { return l != null && l.Expired ? "已重置" : l == null || l.Used == null ? "--" : Math.Round(l.Used.Value).ToString(CultureInfo.InvariantCulture) + "%"; }
    internal static string CacheRate(TokenUsage t) { long n = t.Input + t.CacheRead + t.CacheWrite; return n == 0 ? "--" : (100.0 * t.CacheRead / n).ToString("0.0", CultureInfo.InvariantCulture) + "%"; }
    internal static string ContextText(Metrics m) { return m.ContextPercent != null ? Math.Round(m.ContextPercent.Value).ToString(CultureInfo.InvariantCulture) + "%" : m.ContextTokens != null ? "~" + Tokens(m.ContextTokens.Value) : "--"; }
    internal static string Value(string key, Metrics m)
    {
        switch (key) {
            case "rate": return m.DisplayRate.ToString("0.0", CultureInfo.InvariantCulture) + " tok/s";
            case "today": return (m.Incomplete ? "≈" : "") + Tokens(m.Today.Total);
            case "cache": return CacheRate(m.Today);
            case "limits": return "5h " + Percent(m.FiveHour) + " · 7d " + Percent(m.SevenDay);
            default: return "上下文 " + ContextText(m);
        }
    }
    internal static string Label(string key) { switch (key) { case "rate": return "输出速率"; case "today": return "今日 Token"; case "cache": return "缓存命中"; case "limits": return "5h / 7d 已用"; default: return "上下文"; } }
    internal static string Age(DateTime? at, DateTime now) { if (at == null) return "暂无读数"; var sec = Math.Max(0, (now - at.Value).TotalSeconds); return sec < 60 ? "刚刚" : sec < 3600 ? (int)(sec / 60) + " 分钟前" : sec < 86400 ? (int)(sec / 3600) + " 小时前" : (int)(sec / 86400) + " 天前"; }
}
