// Claude Usage Overlay: a small window owned by the Claude desktop window, placed
// left of the composer's model button. Reads local transcripts and a limits snapshot;
// it never injects into Claude, opens a port, reads credentials or sends anything out.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;

internal static class Native
{
    public const int GWLP_HWNDPARENT = -8;
    public const uint PW_RENDERFULLCONTENT = 2;
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetOwner(IntPtr h, int index, IntPtr owner);
}

internal sealed class TokenUsage { public long Input, CacheWrite, CacheRead, Output; public long Total { get { return Input + CacheWrite + CacheRead + Output; } } }

internal sealed class FileCursor
{
    public string SessionId;
    public long Offset;
    public DateTime LastTurnRecord = DateTime.MinValue;
    public string LastMessageId;
    public DateTime CurrentStart = DateTime.MinValue;
    public double Rate;
    public DateTime LastActivity = DateTime.MinValue;
    public bool Finished = true;
}

internal sealed class Limit { public double? Used; public DateTime? ResetsAt; }

internal sealed class Metrics
{
    public double Rate; public TokenUsage Today = new TokenUsage(); public Limit FiveHour, SevenDay; public DateTime? LimitsAt; public double? ContextPercent;
}

// Incremental, read-only scan of today's transcript records.
internal sealed class Collector
{
    private static readonly Regex TimestampPattern = new Regex("\"timestamp\":\"([^\"]+)\"", RegexOptions.Compiled);
    private readonly string _projects, _limitsPath, _contextDir;
    private readonly Dictionary<string, FileCursor> _files = new Dictionary<string, FileCursor>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TokenUsage> _messages = new Dictionary<string, TokenUsage>();
    private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    private DateTime _day = DateTime.Today;

    public Collector(string projects, string limitsPath)
    {
        _projects = projects; _limitsPath = limitsPath;
        _contextDir = Path.Combine(Path.GetDirectoryName(limitsPath), "context");
        RemoveOldContextFiles();
    }

    private void RemoveOldContextFiles()
    {
        if (!Directory.Exists(_contextDir)) return;
        foreach (var path in Directory.EnumerateFiles(_contextDir, "*.json"))
        {
            try { if (File.GetLastWriteTime(path) < DateTime.Now.AddDays(-7)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // A subagent's transcript sits under <session>\subagents; its context is its session's.
    private static string SessionIdOf(string path)
    {
        var directory = Path.GetDirectoryName(path);
        return string.Equals(Path.GetFileName(directory), "subagents", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(Path.GetDirectoryName(directory))
            : Path.GetFileNameWithoutExtension(path);
    }

    public Metrics Poll()
    {
        if (DateTime.Today != _day) { _day = DateTime.Today; _files.Clear(); _messages.Clear(); }
        foreach (var path in Directory.EnumerateFiles(_projects, "*.jsonl", SearchOption.AllDirectories))
        {
            try
            {
                if (File.GetLastWriteTime(path) < _day) continue;
                FileCursor cursor;
                if (!_files.TryGetValue(path, out cursor)) { cursor = new FileCursor { SessionId = SessionIdOf(path) }; _files[path] = cursor; }
                ReadNew(path, cursor);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var metrics = new Metrics();
        foreach (var u in _messages.Values)
        {
            metrics.Today.Input += u.Input; metrics.Today.CacheWrite += u.CacheWrite;
            metrics.Today.CacheRead += u.CacheRead; metrics.Today.Output += u.Output;
        }
        var active = _files.Values.OrderByDescending(f => f.LastActivity).FirstOrDefault();
        if (active != null)
        {
            var age = (DateTime.Now - active.LastActivity).TotalSeconds;
            metrics.Rate = (active.Finished ? age <= 5 : age <= 300) ? active.Rate : 0;
            metrics.ContextPercent = ReadContext(active.SessionId);
        }
        ReadLimits(metrics);
        return metrics;
    }

    private void ReadNew(string path, FileCursor cursor)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (stream.Length < cursor.Offset) cursor.Offset = 0;
            if (stream.Length == cursor.Offset) return;
            stream.Position = cursor.Offset;
            var buffer = new byte[stream.Length - cursor.Offset];
            var read = 0;
            while (read < buffer.Length) { var n = stream.Read(buffer, read, buffer.Length - read); if (n <= 0) break; read += n; }
            var lineStart = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                ProcessLine(Encoding.UTF8.GetString(buffer, lineStart, i - lineStart), cursor);
                lineStart = i + 1;
            }
            cursor.Offset += lineStart;
        }
    }

    private void ProcessLine(string line, FileCursor cursor)
    {
        if (line.Contains("\"type\":\"assistant\"") && line.Contains("\"usage\"")) { ProcessAssistant(line, cursor); return; }
        if (!line.Contains("\"type\":\"user\"")) return;
        var matches = TimestampPattern.Matches(line);
        if (matches.Count == 0) return;
        var at = ParseTime(matches[matches.Count - 1].Groups[1].Value);
        if (at == null) return;
        cursor.LastTurnRecord = at.Value;
        cursor.LastActivity = at.Value;
        cursor.Finished = false;
        cursor.LastMessageId = null;
    }

    private void ProcessAssistant(string line, FileCursor cursor)
    {
        Dictionary<string, object> record;
        try { record = _json.Deserialize<Dictionary<string, object>>(line); } catch (ArgumentException) { return; } catch (InvalidOperationException) { return; }
        object type; if (!record.TryGetValue("type", out type) || (type as string) != "assistant") return;
        var message = Child(record, "message"); var usage = message == null ? null : Child(message, "usage");
        object tsValue; if (usage == null || !record.TryGetValue("timestamp", out tsValue)) return;
        var at = ParseTime(tsValue as string); if (at == null) return;
        object idValue; message.TryGetValue("id", out idValue); var id = idValue as string; if (string.IsNullOrEmpty(id)) return;
        object model; message.TryGetValue("model", out model); if ((model as string) == "<synthetic>") return;

        var tokens = new TokenUsage { Input = Num(usage, "input_tokens"), CacheWrite = Num(usage, "cache_creation_input_tokens"), CacheRead = Num(usage, "cache_read_input_tokens"), Output = Num(usage, "output_tokens") };
        // One response spans several records sharing an id; the latest figures replace earlier ones.
        if (at.Value.Date == _day) _messages[id] = tokens;

        if (cursor.LastMessageId != id) { cursor.LastMessageId = id; cursor.CurrentStart = cursor.LastTurnRecord; }
        var seconds = (at.Value - cursor.CurrentStart).TotalSeconds;
        if (cursor.CurrentStart != DateTime.MinValue && seconds >= 0.5 && tokens.Output > 0) cursor.Rate = tokens.Output / seconds;
        cursor.LastTurnRecord = at.Value;
        cursor.LastActivity = at.Value;
        object stop; message.TryGetValue("stop_reason", out stop);
        cursor.Finished = (stop as string) == "end_turn";
    }

    private double? ReadContext(string sessionId)
    {
        try
        {
            var path = Path.Combine(_contextDir, sessionId + ".json");
            if (!File.Exists(path)) return null;
            string text;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8)) text = reader.ReadToEnd();
            object percent;
            return _json.Deserialize<Dictionary<string, object>>(text).TryGetValue("percent", out percent) && percent != null
                ? Convert.ToDouble(percent, CultureInfo.InvariantCulture)
                : (double?)null;
        }
        catch (IOException) { return null; }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private void ReadLimits(Metrics metrics)
    {
        try
        {
            if (!File.Exists(_limitsPath)) return;
            string text;
            using (var stream = new FileStream(_limitsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8)) text = reader.ReadToEnd();
            var root = _json.Deserialize<Dictionary<string, object>>(text);
            object at; if (root.TryGetValue("updatedAt", out at)) metrics.LimitsAt = ParseTime(at as string);
            object list; if (!root.TryGetValue("rateLimits", out list)) return;
            foreach (var item in ((System.Collections.ArrayList)list).OfType<Dictionary<string, object>>())
            {
                var limit = new Limit();
                object used; if (item.TryGetValue("percentUsed", out used)) limit.Used = Convert.ToDouble(used, CultureInfo.InvariantCulture);
                object reset; if (item.TryGetValue("resetsAt", out reset)) limit.ResetsAt = ParseTime(reset as string);
                // A reading from before its window reset says nothing about the new window.
                if (limit.ResetsAt != null && limit.ResetsAt.Value <= DateTime.Now) limit.Used = null;
                var kind = item["kind"] as string;
                if (kind == "five_hour") metrics.FiveHour = limit; else if (kind == "seven_day") metrics.SevenDay = limit;
            }
        }
        catch (IOException) { } catch (ArgumentException) { } catch (InvalidOperationException) { } catch (InvalidCastException) { } catch (KeyNotFoundException) { }
    }

    private static Dictionary<string, object> Child(Dictionary<string, object> parent, string key)
    { object v; return parent.TryGetValue(key, out v) ? v as Dictionary<string, object> : null; }

    private static long Num(Dictionary<string, object> parent, string key)
    { object v; return parent.TryGetValue(key, out v) && v != null ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : 0; }

    private static DateTime? ParseTime(string text)
    {
        DateTimeOffset value;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value) ? value.LocalDateTime : (DateTime?)null;
    }
}

internal sealed class Anchor { public IntPtr Window; public Rectangle Model; public int LeftBound; }

// Finds the composer's model button through UI Automation; the app's own controls move
// with the sidebar and panes, so no offset from the window edge is assumed.
internal sealed class AnchorFinder
{
    private AutomationElement _button; private int _leftBound; private IntPtr _window; private DateTime _nextSearch = DateTime.MinValue;

    public Anchor Find()
    {
        var window = ClaudeWindow();
        if (window == IntPtr.Zero) { _button = null; return null; }
        if (window != _window || DateTime.UtcNow >= _nextSearch) { _window = window; Search(window); }
        if (_button == null) return null;
        try
        {
            if (_button.Current.IsOffscreen) { _button = null; _nextSearch = DateTime.MinValue; return null; }
            var r = _button.Current.BoundingRectangle;
            if (r.IsEmpty || r.Width <= 0) { _button = null; _nextSearch = DateTime.MinValue; return null; }
            return new Anchor { Window = window, Model = new Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height), LeftBound = _leftBound };
        }
        catch (ElementNotAvailableException) { _button = null; _nextSearch = DateTime.MinValue; return null; }
    }

    private void Search(IntPtr window)
    {
        _nextSearch = DateTime.UtcNow.AddSeconds(5);
        var root = AutomationElement.FromHandle(window);
        var buttons = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
        AutomationElement best = null; var bestBottom = double.MinValue;
        foreach (AutomationElement b in buttons)
        {
            var name = b.Current.Name ?? "";
            if (!name.StartsWith("模型：") && !name.StartsWith("Model:", StringComparison.OrdinalIgnoreCase)) continue;
            var r = b.Current.BoundingRectangle;
            if (r.IsEmpty || b.Current.IsOffscreen) continue;
            if (r.Bottom > bestBottom) { bestBottom = r.Bottom; best = b; }
        }
        _button = best;
        if (best == null) { _nextSearch = DateTime.UtcNow.AddSeconds(1); return; }
        var model = best.Current.BoundingRectangle; var centre = model.Y + model.Height / 2; var bound = 0.0;
        // Only the button's own row counts: transcript content scrolls behind the same height.
        var row = best;
        for (var depth = 0; depth < 8; depth++)
        {
            var parent = TreeWalker.ControlViewWalker.GetParent(row);
            if (parent == null) break;
            var box = parent.Current.BoundingRectangle;
            if (box.IsEmpty || box.Height > model.Height * 2) break;
            row = parent;
        }
        var neighbours = row.FindAll(TreeScope.Descendants, new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox)));
        foreach (AutomationElement e in neighbours)
        {
            var r = e.Current.BoundingRectangle;
            if (r.IsEmpty || r.Top > centre || r.Bottom < centre || r.Height > model.Height * 2) continue;
            if (r.Right <= model.Left && r.Right > bound) bound = r.Right;
        }
        _leftBound = (int)bound;
    }

    private static IntPtr ClaudeWindow()
    {
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("Claude"))
        {
            using (p)
            {
                var h = p.MainWindowHandle;
                if (h != IntPtr.Zero && Native.IsWindowVisible(h) && !Native.IsIconic(h)) return h;
            }
        }
        return IntPtr.Zero;
    }
}

internal sealed class OverlayForm : Form
{
    private const int GapToModel = 12, GapToLeft = 8, MissesBeforeHide = 4;
    private const int SampleSeconds = 3;
    private int _misses; private string _state; private DateTime _nextSample = DateTime.MinValue;
    private readonly ToolTip _tip = new ToolTip { InitialDelay = 300, AutoPopDelay = 20000, ReshowDelay = 100 };
    private readonly string[] _none = new string[0];
    private Metrics _metrics; private Anchor _anchor; private IntPtr _owner = IntPtr.Zero; private string _text = ""; private Color _ink = Color.Gray; private string _tipText = "";

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true; Visible = false; Size = new Size(1, 1);
        var menu = new ContextMenuStrip(); menu.Items.Add("退出用量小窗", null, (s, e) => Application.Exit()); ContextMenuStrip = menu;
        // A closed window must take the overlay with it at once, whatever UI Automation is
        // doing: its calls can stall on a window going away, and misses wait out a grace period.
        var watch = new System.Windows.Forms.Timer { Interval = 150 };
        watch.Tick += (s, e) => { if (Visible) Apply(); };
        watch.Start();
    }

    private static bool IsOnScreen(IntPtr window)
    {
        return Native.IsWindow(window) && Native.IsWindowVisible(window) && !Native.IsIconic(window);
    }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x00000080 /*TOOLWINDOW*/ | 0x08000000 /*NOACTIVATE*/; return p; }
    }

    public void Update(Metrics metrics) { _metrics = metrics; Apply(); }
    public void Update(Anchor anchor)
    {
        // The button can drop out for a moment while the app redraws; don't blink for that.
        _misses = anchor == null ? _misses + 1 : 0;
        if (anchor != null || _misses >= MissesBeforeHide) _anchor = anchor;
        Apply();
    }

    private void Report(string state)
    {
        if (state == _state) return;
        _state = state;
        Program.Note("overlay " + state);
    }

    internal static string Tokens(long n)
    {
        if (n >= 100000000) return (n / 100000000.0).ToString("0.00", CultureInfo.InvariantCulture) + "亿";
        if (n >= 10000) return (n / 10000) + "万";
        return n.ToString(CultureInfo.InvariantCulture);
    }
    private static string Percent(Limit l) { return l == null || l.Used == null ? "--" : Math.Round(l.Used.Value).ToString(CultureInfo.InvariantCulture) + "%"; }
    internal static string CacheRate(TokenUsage t)
    {
        var denominator = t.Input + t.CacheWrite + t.CacheRead;
        return denominator == 0 ? "--" : (100.0 * t.CacheRead / denominator).ToString("0.0", CultureInfo.InvariantCulture) + "%";
    }
    private static string LimitLine(string label, Limit l)
    {
        var text = label + "已用 " + Percent(l);
        return l != null && l.ResetsAt != null ? text + "，" + l.ResetsAt.Value.ToString("MM-dd HH:mm") + " 重置" : text;
    }

    private void Apply()
    {
        if (_anchor != null && !IsOnScreen(_anchor.Window)) _anchor = null;
        if (_anchor == null || _metrics == null) { Report(_anchor == null ? "hidden: model button not found" : "hidden: no metrics yet"); if (Visible) Hide(); return; }
        var m = _metrics;
        var rate = m.Rate.ToString("0.0", CultureInfo.InvariantCulture) + " tok/s";
        var limits = "5h " + Percent(m.FiveHour) + " · 7d " + Percent(m.SevenDay);
        var context = "上下文 " + (m.ContextPercent == null ? "--" : Math.Round(m.ContextPercent.Value).ToString(CultureInfo.InvariantCulture) + "%");
        var candidates = new[] {
            string.Join(" | ", new[] { rate, Tokens(m.Today.Total), CacheRate(m.Today), limits, context }),
            string.Join(" | ", new[] { rate, Tokens(m.Today.Total), limits, context }),
            string.Join(" | ", new[] { rate, limits, context }),
            string.Join(" | ", new[] { limits, context }),
            limits
        };
        var model = _anchor.Model;
        using (var font = new Font("Microsoft YaHei UI", Math.Max(11f, model.Height * 0.52f), GraphicsUnit.Pixel))
        {
            if (Font.Size != font.Size || Font.Name != font.Name) Font = new Font(font, font.Style);
        }
        var available = model.Left - GapToModel - (_anchor.LeftBound + GapToLeft);
        string chosen = null; var width = 0;
        foreach (var c in candidates)
        {
            width = TextRenderer.MeasureText(c, Font, Size.Empty, TextFormatFlags.NoPadding).Width + 2;
            if (width <= available) { chosen = c; break; }
        }
        if (chosen == null) { Report("hidden: no room, available " + available + "px, left bound " + _anchor.LeftBound); if (Visible) Hide(); return; }
        Report("shown");

        if (_owner != _anchor.Window) { Native.SetOwner(Handle, Native.GWLP_HWNDPARENT, _anchor.Window); _owner = _anchor.Window; }
        var bg = SampleBackground(_anchor.Window, model.Left - 4, model.Top + 2);
        var luminance = 0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B;
        var ink = luminance > 128 ? Color.FromArgb(0x6E, 0x6C, 0x66) : Color.FromArgb(0xB4, 0xB2, 0xAA);
        var changed = chosen != _text || ink != _ink || bg != BackColor;
        _text = chosen; _ink = ink; BackColor = bg;
        var bounds = new Rectangle(model.Left - GapToModel - width, model.Top, width, model.Height);
        if (Bounds != bounds) Bounds = bounds;
        if (!Visible) Show();
        if (changed) Invalidate();
        var tip = string.Join("\n", new[] {
            "今日 Token：" + m.Today.Total.ToString("N0", CultureInfo.InvariantCulture),
            "  输入 " + m.Today.Input.ToString("N0", CultureInfo.InvariantCulture) + " / 写缓存 " + m.Today.CacheWrite.ToString("N0", CultureInfo.InvariantCulture),
            "  读缓存 " + m.Today.CacheRead.ToString("N0", CultureInfo.InvariantCulture) + " / 输出 " + m.Today.Output.ToString("N0", CultureInfo.InvariantCulture),
            "今日缓存命中：" + CacheRate(m.Today),
            "tok/s：最近活跃会话的估算值（输出 ÷ 该次响应耗时）",
            LimitLine("5 小时窗口", m.FiveHour),
            LimitLine("7 天窗口", m.SevenDay),
            context + "（最近活跃会话）",
            m.LimitsAt == null ? "额度快照：暂无" : "额度快照：" + m.LimitsAt.Value.ToString("HH:mm:ss")
        });
        if (tip != _tipText) { _tipText = tip; _tip.SetToolTip(this, tip); }
    }

    // Reads the Claude window's own rendering: a screen pixel would pick up whatever lies
    // on top of it, such as an input method's candidate bar, and invert the colors.
    private Color SampleBackground(IntPtr window, int x, int y)
    {
        if (DateTime.UtcNow < _nextSample) return BackColor;
        _nextSample = DateTime.UtcNow.AddSeconds(SampleSeconds);
        Native.RECT rect;
        if (!Native.GetWindowRect(window, out rect)) return BackColor;
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top, px = x - rect.Left, py = y - rect.Top;
        if (px < 0 || py < 0 || px >= width || py >= height) return BackColor;
        using (var bmp = new Bitmap(width, height))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                try { if (!Native.PrintWindow(window, hdc, Native.PW_RENDERFULLCONTENT)) return BackColor; }
                finally { g.ReleaseHdc(hdc); }
            }
            return bmp.GetPixel(px, py);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        TextRenderer.DrawText(e.Graphics, _text, Font, ClientRectangle, _ink, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }
}

internal static class Program
{
    private static string _logPath;
    private static void Log(Exception ex) { Note(ex.GetType().Name + ": " + ex.Message); }

    public static void Note(string text)
    {
        try { File.AppendAllText(_logPath, DateTime.Now.ToString("s") + " " + text + Environment.NewLine); } catch (IOException) { }
    }

    private static void Loop(int delayMs, Action body)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try { body(); } catch (Exception ex) { Log(ex); }
                Thread.Sleep(delayMs);
            }
        });
        thread.IsBackground = true; thread.Start();
    }

    [STAThread]
    private static void Main()
    {
        bool created;
        using (new Mutex(true, "Local\\ClaudeUsageOverlay", out created))
        {
            if (!created) return;
            Native.SetProcessDPIAware();
            var state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsageMonitor");
            Directory.CreateDirectory(state);
            _logPath = Path.Combine(state, "overlay.log");
            var projects = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
            Application.EnableVisualStyles();
            var form = new OverlayForm();
            var handle = form.Handle;
            var collector = new Collector(projects, Path.Combine(state, "limits.json"));
            var finder = new AnchorFinder();
            Action<Action> post = a => { if (form.IsHandleCreated && !form.IsDisposed) form.BeginInvoke(a); };
            Loop(2000, () => { var m = collector.Poll(); post(() => form.Update(m)); });
            Loop(500, () => { var a = finder.Find(); post(() => form.Update(a)); });
            Application.Run();
        }
    }
}
