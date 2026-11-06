using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;

internal static class Native
{
    private sealed class KnownProcess { internal long Started; internal bool Desktop; }
    private static readonly Dictionary<int, KnownProcess> Processes = new Dictionary<int, KnownProcess>();
    [StructLayout(LayoutKind.Sequential)] internal struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
        public bool Same(RECT other) { return Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom; }
    }
    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
    internal delegate bool EnumProc(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback, IntPtr state);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] internal static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern IntPtr SetLong(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern IntPtr GetLong(IntPtr window, int index);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    internal static bool Live(IntPtr w) { return w != IntPtr.Zero && IsWindow(w) && IsWindowVisible(w) && !IsIconic(w); }
    internal static double Scale(IntPtr w) { try { return Math.Max(1, GetDpiForWindow(w) / 96.0); } catch (EntryPointNotFoundException) { return 1; } }
    internal static HashSet<int> DesktopPids()
    {
        var ids = new HashSet<int>();
        var seen = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName("Claude")) using (p) {
            try {
                seen.Add(p.Id); long started = p.StartTime.ToUniversalTime().Ticks; KnownProcess known;
                if (!Processes.TryGetValue(p.Id, out known) || known.Started != started) {
                    known = new KnownProcess { Started = started, Desktop = p.MainModule.FileName.IndexOf("claude-code", StringComparison.OrdinalIgnoreCase) < 0 };
                    Processes[p.Id] = known;
                }
                if (known.Desktop) ids.Add(p.Id);
            } catch (System.ComponentModel.Win32Exception) { } catch (InvalidOperationException) { }
        }
        foreach (var key in Processes.Keys.Where(k => !seen.Contains(k)).ToArray()) Processes.Remove(key);
        return ids;
    }
    internal static string DesktopIdentity(HashSet<int> ids)
    {
        // A renderer process restarting must not undo the person's "hide this run" choice.
        int first = ids.OrderBy(id => Processes[id].Started).FirstOrDefault();
        return first == 0 ? "" : first + ":" + Processes[first].Started;
    }
    internal static IntPtr ChooseWindow(HashSet<int> pids, IntPtr last)
    {
        var windows = new List<IntPtr>();
        EnumWindows((w, unused) => {
            uint pid; GetWindowThreadProcessId(w, out pid); RECT r;
            if (pids.Contains((int)pid) && Live(w) && GetWindowRect(w, out r) && r.Width >= 300 && r.Height >= 250) windows.Add(w);
            return true;
        }, IntPtr.Zero);
        var foreground = GetAncestor(GetForegroundWindow(), 2);
        if (windows.Contains(foreground)) return foreground;
        if (windows.Contains(last)) return last;
        return windows.OrderByDescending(w => { RECT r; GetWindowRect(w, out r); return (long)r.Width * r.Height; }).FirstOrDefault();
    }
    // Sample only uncovered Claude pixels. IME/menu/other-app pixels are ignored.
    // The transparent strip no longer needs a full-window PrintWindow bitmap.
    internal static bool IsLight(IntPtr owner, int x, int y, bool fallback)
    {
        if (GetAncestor(WindowFromPoint(new POINT(x, y)), 2) != owner) return fallback;
        var dc = GetDC(IntPtr.Zero); if (dc == IntPtr.Zero) return fallback;
        try {
            var c = GetPixel(dc, x, y); if (c == 0xffffffff) return fallback;
            return 0.299 * (c & 255) + 0.587 * ((c >> 8) & 255) + 0.114 * ((c >> 16) & 255) > 128;
        } finally { ReleaseDC(IntPtr.Zero, dc); }
    }
}
internal sealed class Anchor
{
    internal IntPtr Window;
    internal Native.RECT Frame;
    internal Rect Model;
    internal double Left, Right, Scale;
    internal bool MenuOpen;
    internal DateTime At;
}
internal static class OverlayLayout
{
    internal static bool CanPlace(double left, double right, double width) { return width > 0 && right - left >= width; }
    internal static Rect Place(double left, double right, double top, double widthDip, double heightDip, double scale)
    {
        var width = Math.Ceiling(widthDip * scale); var height = Math.Ceiling(heightDip * scale);
        return new Rect(Math.Max(left, right - width), top, width, height);
    }
}
internal sealed class AnchorFinder
{
    private AutomationElement _root, _model, _portal;
    private readonly List<AutomationElement> _row = new List<AutomationElement>();
    private IntPtr _window;
    private DateTime _nextSearch;
    private double _leftGap;
    internal string Stage = "waiting";
    internal void Invalidate() { _nextSearch = DateTime.MinValue; }
    internal Anchor Find(IntPtr window)
    {
        if (!Native.Live(window)) { _model = null; Stage = "host-hidden"; return null; }
        try {
            if (window != _window || _model == null || DateTime.UtcNow >= _nextSearch) { _window = window; Search(window); }
            if (_model == null) { Stage = "model-not-found"; return null; }
            if (_model.Current.IsOffscreen) { Invalidate(); Stage = "offscreen"; return null; }
            var box = _model.Current.BoundingRectangle;
            Native.RECT frame; Native.GetWindowRect(window, out frame);
            if (box.IsEmpty || box.Width <= 0 || box.Left < frame.Left || box.Right > frame.Right || box.Top < frame.Top || box.Bottom > frame.Bottom) { Invalidate(); return null; }
            bool menu = _row.Any(Expanded);
            if (!menu && _portal != null) {
                var popup = _portal.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu));
                menu = popup != null && !popup.Current.IsOffscreen && !popup.Current.BoundingRectangle.IsEmpty;
            }
            double scale = Native.Scale(window);
            Stage = menu ? "menu-open" : "located";
            return new Anchor { Window = window, Frame = frame, Model = box, Left = box.Left - _leftGap + 8 * scale, Right = box.Left - 12 * scale, Scale = scale, MenuOpen = menu, At = DateTime.UtcNow };
        } catch (ElementNotAvailableException) { _model = null; Invalidate(); Stage = "element-rebuilt"; return null; }
        catch (InvalidOperationException) { _model = null; Invalidate(); Stage = "uia-unavailable"; return null; }
    }
    private static bool Expanded(AutomationElement e)
    {
        try { object p; return e.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out p) && ((ExpandCollapsePattern)p).Current.ExpandCollapseState == ExpandCollapseState.Expanded; }
        catch (ElementNotAvailableException) { return false; }
    }
    private static bool ModelName(string name)
    {
        // Stable model family + composer geometry is a fallback for localized button labels.
        return name.StartsWith("模型", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Model", StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(name, @"\b(Opus|Sonnet|Haiku)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
    private void Search(IntPtr window)
    {
        _nextSearch = DateTime.UtcNow.AddSeconds(5); _model = null; _row.Clear();
        _root = AutomationElement.FromHandle(window);
        // Cache the popup container; do not traverse the conversation tree on every tick.
        _portal = _root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "portal-root"));
        var edits = _root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        var editBoxes = new List<Rect>();
        foreach (AutomationElement e in edits) if (!e.Current.IsOffscreen) {
            var r = e.Current.BoundingRectangle; if (!r.IsEmpty && r.Width >= 160) editBoxes.Add(r);
        }
        var buttons = _root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
        var bestBottom = double.MinValue; var scale = Native.Scale(window);
        foreach (AutomationElement b in buttons) {
            if (b.Current.IsOffscreen || !ModelName(b.Current.Name ?? "")) continue;
            var r = b.Current.BoundingRectangle;
            if (r.IsEmpty || !editBoxes.Any(e => r.Top >= e.Bottom - 20 * scale && r.Top <= e.Bottom + 100 * scale && r.Left >= e.Left && r.Right <= e.Right + 30 * scale)) continue;
            if (r.Bottom > bestBottom) { bestBottom = r.Bottom; _model = b; }
        }
        if (_model == null) { _nextSearch = DateTime.UtcNow.AddSeconds(1); return; }
        var model = _model.Current.BoundingRectangle; var center = model.Top + model.Height / 2;
        var row = _model;
        for (int depth = 0; depth < 8; depth++) {
            var parent = TreeWalker.ControlViewWalker.GetParent(row); if (parent == null) break;
            var r = parent.Current.BoundingRectangle; if (r.IsEmpty || r.Height > model.Height * 2.5) break; row = parent;
        }
        double left = editBoxes.Where(e => e.Left <= model.Left && e.Bottom <= model.Bottom).OrderByDescending(e => e.Bottom).Select(e => e.Left).DefaultIfEmpty(model.Left).First();
        var neighbors = row.FindAll(TreeScope.Descendants, new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)));
        foreach (AutomationElement e in neighbors) {
            var r = e.Current.BoundingRectangle;
            if (e.Current.IsOffscreen || r.IsEmpty || r.Top > center || r.Bottom < center || r.Height > model.Height * 2.5) continue;
            _row.Add(e);
            if (r.Right <= model.Left && r.Right > left) left = r.Right;
        }
        _leftGap = model.Left - left;
    }
}
