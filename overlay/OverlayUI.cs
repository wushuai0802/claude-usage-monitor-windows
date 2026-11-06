using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal sealed class PassiveWindow : Window
{
    internal IntPtr Handle;
    private Rect _bounds = Rect.Empty;
    private IntPtr _owner;
    internal PassiveWindow(string title)
    {
        Title = title; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; AllowsTransparency = true;
        Background = Brushes.Transparent; Focusable = false; SizeToContent = SizeToContent.Manual;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        UseLayoutRounding = true;
        SourceInitialized += (s, e) => {
            Handle = new WindowInteropHelper(this).Handle;
            Native.SetLong(Handle, -20, new IntPtr(Native.GetLong(Handle, -20).ToInt64() | 0x08000080));
            HwndSource.FromHwnd(Handle).AddHook((IntPtr w, int msg, IntPtr a, IntPtr b, ref bool handled) => {
                if (msg == 0x21) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE
                return IntPtr.Zero;
            });
        };
    }
    internal void Place(IntPtr owner, Rect r)
    {
        if (Handle == IntPtr.Zero) new WindowInteropHelper(this).EnsureHandle();
        if (_owner != owner) { Native.SetLong(Handle, -8, owner); _owner = owner; }
        if (r == _bounds) return;
        _bounds = r;
        Native.SetWindowPos(Handle, IntPtr.Zero, (int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Ceiling(r.Width), (int)Math.Ceiling(r.Height), 0x14);
    }
    internal void Reveal()
    {
        if (IsVisible) return;
        Show();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(100)));
    }
}
internal static class MonitorUI
{
    internal static readonly FontFamily Font = new FontFamily("Segoe UI, Microsoft YaHei UI");
    internal static Brush Brush(string hex) { return (Brush)new BrushConverter().ConvertFromString(hex); }
    internal static TextBlock Text(string text, double size, Brush ink)
    {
        return new TextBlock { Text = text, FontFamily = Font, FontSize = size, Foreground = ink, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    }
    internal static Button Button(string text, Action action, Brush ink)
    {
        var button = new Button { Content = text, FontFamily = Font, FontSize = 12, Foreground = ink, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(7, 3, 7, 3), Cursor = Cursors.Hand, Focusable = false, VerticalAlignment = VerticalAlignment.Center };
        button.Click += (s, e) => action(); AutomationProperties.SetName(button, text); return button;
    }
    internal static FrameworkElement Detail(Metrics m, DisplaySettings settings, bool light, bool showSettings, Action close, Action toggleSettings, Action changed)
    {
        var ink = Brush(light ? "#34322E" : "#E5E2DC");
        var muted = Brush(light ? "#77736D" : "#ABA69D");
        var panel = new StackPanel();
        var header = new DockPanel { LastChildFill = true };
        var closeButton = Button("关闭", close, muted); DockPanel.SetDock(closeButton, Dock.Right); header.Children.Add(closeButton);
        var title = Text("用量明细", 15, ink); title.FontWeight = FontWeights.SemiBold; header.Children.Add(title); panel.Children.Add(header);
        panel.Children.Add(new Border { Height = 8 });
        panel.Children.Add(Text("最近活跃会话 · " + MetricText.Age(m.ActivityAt, DateTime.Now), 11, muted));
        panel.Children.Add(Text("此处不代表正在查看的页面", 10.5, muted));
        panel.Children.Add(new Border { Height = 10 });
        Action<string, string> row = (label, value) => {
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(Text(label, 12, muted));
            var number = Text(value, 12, ink); number.FontWeight = FontWeights.SemiBold; Grid.SetColumn(number, 1); grid.Children.Add(number); panel.Children.Add(grid);
        };
        row("输出速率 · 估算", MetricText.Value("rate", m));
        var state = m.RateState == "tools" ? "工具执行中" : m.RateState == "waiting" ? "等待响应" : m.RateState == "generating" ? "最近一次生成采样" : m.RateState == "completed" ? "最近完成的响应" : "空闲";
        panel.Children.Add(Text(state + "；耗时包含首 Token 等待", 10.5, muted));
        row("上下文", MetricText.ContextText(m));
        panel.Children.Add(Text("上下文读数：" + MetricText.Age(m.ContextAt, DateTime.Now) + (m.ContextPercent == null ? " · ~ 表示请求 Token 数" : ""), 10.5, muted));
        panel.Children.Add(new Border { Height = 1, Background = Brush(light ? "#E9E6E1" : "#48443E"), Margin = new Thickness(0, 12, 0, 10) });
        row("今日 Token", m.Today.Total.ToString("N0", CultureInfo.InvariantCulture));
        row("输入 / 写缓存", MetricText.Tokens(m.Today.Input) + " / " + MetricText.Tokens(m.Today.CacheWrite));
        row("读缓存 / 输出", MetricText.Tokens(m.Today.CacheRead) + " / " + MetricText.Tokens(m.Today.Output));
        row("缓存命中", MetricText.CacheRate(m.Today));
        panel.Children.Add(Text("含子代理与缓存读取，不代表计费用量", 10.5, muted));
        if (m.Incomplete) panel.Children.Add(Text("仍在补读，或有记录未能计入", 10.5, Brush(light ? "#9C5C22" : "#E3B781")));
        panel.Children.Add(new Border { Height = 1, Background = Brush(light ? "#E9E6E1" : "#48443E"), Margin = new Thickness(0, 12, 0, 10) });
        foreach (var item in new[] { new { Label = "5 小时已用", Limit = m.FiveHour }, new { Label = "7 天已用", Limit = m.SevenDay } }) {
            row(item.Label, MetricText.Percent(item.Limit));
            if (item.Limit != null && item.Limit.ResetsAt != null) panel.Children.Add(Text(item.Limit.Expired ? "已到重置时间，等待新响应确认" : item.Limit.ResetsAt.Value.ToString("MM-dd HH:mm") + " 重置", 10.5, muted));
        }
        panel.Children.Add(Text("额度读数：" + MetricText.Age(m.LimitsAt, DateTime.Now), 10.5, muted));
        if (m.SnapshotError) panel.Children.Add(Text("有快照暂不可读，保留最后有效读数", 10.5, muted));
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var settingsButton = Button(showSettings ? "收起设置" : "显示设置", toggleSettings, ink); DockPanel.SetDock(settingsButton, Dock.Right); footer.Children.Add(settingsButton);
        footer.Children.Add(Text("非官方 · v" + Program.Version, 10.5, muted)); panel.Children.Add(footer);
        if (showSettings) {
            panel.Children.Add(Text("勾选显示项 · 上方项目优先保留", 11, muted));
            foreach (var id in settings.Priority.ToArray()) {
                var key = id; var r = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
                var up = Button("↑", () => { int index = settings.Priority.IndexOf(key); if (index > 0) { settings.Priority.RemoveAt(index); settings.Priority.Insert(index - 1, key); changed(); } }, muted);
                AutomationProperties.SetName(up, "提高" + MetricText.Label(key) + "保留优先级"); DockPanel.SetDock(up, Dock.Right); r.Children.Add(up);
                var down = Button("↓", () => { int index = settings.Priority.IndexOf(key); if (index < settings.Priority.Count - 1) { settings.Priority.RemoveAt(index); settings.Priority.Insert(index + 1, key); changed(); } }, muted);
                AutomationProperties.SetName(down, "降低" + MetricText.Label(key) + "保留优先级"); DockPanel.SetDock(down, Dock.Right); r.Children.Add(down);
                var box = new CheckBox { Content = MetricText.Label(key), IsChecked = settings.Selected.Contains(key), Focusable = false, FontFamily = Font, FontSize = 12, Foreground = ink, VerticalAlignment = VerticalAlignment.Center };
                box.Click += (s, e) => { if (box.IsChecked == true) { if (!settings.Selected.Contains(key)) settings.Selected.Add(key); } else settings.Selected.Remove(key); changed(); };
                r.Children.Add(box); panel.Children.Add(r);
            }
            panel.Children.Add(Button("配色：" + (settings.Theme == "auto" ? "自动" : settings.Theme == "light" ? "浅色" : "深色"), () => { settings.Theme = settings.Theme == "auto" ? "light" : settings.Theme == "light" ? "dark" : "auto"; changed(); }, ink));
            panel.Children.Add(Text("鼠标操作；不会夺走 Claude 输入焦点", 10.5, muted));
        }
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        return new Border {
            Width = 350, Padding = new Thickness(18, 14, 18, 12), CornerRadius = new CornerRadius(12),
            Background = Brush(light ? "#FFFCFA" : "#292723"), BorderBrush = Brush(light ? "#DDD9D2" : "#514C44"), BorderThickness = new Thickness(1),
            Child = scroll, Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.16, Color = Colors.Black }
        };
    }
    internal static void RenderPreview(string directory)
    {
        Directory.CreateDirectory(directory);
        var data = new Metrics { Rate = 42.6, RateState = "completed", Today = new TokenUsage { Input = 123456, CacheWrite = 40000, CacheRead = 2345678, Output = 54321 }, ContextPercent = 26,
            LimitsAt = DateTime.Now.AddMinutes(-3), ContextAt = DateTime.Now, ActivityAt = DateTime.Now, FiveHour = new Limit { Used = 24, ResetsAt = DateTime.Now.AddHours(2) }, SevenDay = new Limit { Used = 38, ResetsAt = DateTime.Now.AddDays(3) } };
        foreach (bool light in new[] { true, false }) {
            var card = Detail(data, new DisplaySettings(), light, false, () => {}, () => {}, () => {});
            card.Measure(new Size(350, 1000)); card.Arrange(new Rect(new Point(), card.DesiredSize)); card.UpdateLayout();
            var bitmap = new RenderTargetBitmap(350 * 2, (int)Math.Ceiling(card.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32); bitmap.Render(card);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(directory, light ? "detail-light.png" : "detail-dark.png"))) encoder.Save(file);
        }
    }
}
internal sealed class OverlayController
{
    private readonly string _state;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly DisplaySettings _settings;
    private PassiveWindow _strip, _detail;
    private Border _stripBorder;
    private volatile Anchor _anchor;
    private volatile Metrics _metrics;
    private bool _detailsOpen, _showSettings, _light = true, _dirty = true, _dismissed;
    private Native.RECT _lastFrame;
    private IntPtr _owner;
    private long _motionAt;
    private DateTime _nextColor, _nextReport, _lastDetail, _pausedUntil, _nextRender;
    private string _signature = "", _renderSignature = "", _reason = "starting";
    internal bool IsMoving { get { return (DateTime.UtcNow.Ticks - Interlocked.Read(ref _motionAt)) / TimeSpan.TicksPerMillisecond < 200; } }
    internal OverlayController(string state)
    {
        _state = state; _dispatcher = Dispatcher.CurrentDispatcher; _settings = DisplaySettings.Load(Path.Combine(state, "ui-settings.json"));
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) }; _timer.Tick += (s, e) => Tick(); _timer.Start();
    }
    internal void PostMetrics(Metrics m) { _metrics = m; _dispatcher.BeginInvoke((Action)(() => { _dirty = true; })); }
    internal void PostAnchor(Anchor a) { _dispatcher.BeginInvoke((Action)(() => { _anchor = a; _dirty = true; })); }
    internal void HostState(string signature)
    {
        _dispatcher.BeginInvoke((Action)(() => {
            if (signature != _signature) { _signature = signature; _dismissed = false; }
            if (signature.Length == 0) { _anchor = null; _metrics = null; Destroy(); _reason = "standby"; }
        }));
    }
    private void EnsureWindows()
    {
        if (_strip != null) return;
        _strip = new PassiveWindow("Claude 用量状态条"); _detail = new PassiveWindow("Claude 用量明细");
        _stripBorder = new Border { Background = Brushes.Transparent, CornerRadius = new CornerRadius(6), Padding = new Thickness(5, 2, 5, 2), Cursor = Cursors.Hand };
        _stripBorder.MouseEnter += (s, e) => _stripBorder.Background = MonitorUI.Brush(_light ? "#09000000" : "#10FFFFFF");
        _stripBorder.MouseLeave += (s, e) => _stripBorder.Background = Brushes.Transparent;
        _stripBorder.MouseLeftButtonUp += (s, e) => { _detailsOpen = !_detailsOpen; _dirty = true; if (!_detailsOpen) _detail.Hide(); };
        _strip.Content = _stripBorder;
        var menu = new ContextMenu();
        Action<string, Action> add = (title, action) => { var item = new MenuItem { Header = title }; item.Click += (s, e) => action(); menu.Items.Add(item); };
        add("展开 / 收起明细", () => { _detailsOpen = !_detailsOpen; _dirty = true; });
        add("暂时隐藏 5 分钟", () => { _pausedUntil = DateTime.UtcNow.AddMinutes(5); Hide("paused"); });
        add("隐藏本次（下次启动 Claude 恢复）", () => { _dismissed = true; Hide("dismissed"); });
        add("退出用量小窗", () => Application.Current.Shutdown());
        _stripBorder.ContextMenu = menu;
    }
    private void SaveSettings()
    {
        try { _settings.Save(Path.Combine(_state, "ui-settings.json")); }
        catch (Exception ex) { Program.Note("settings save failed: " + ex.GetType().Name); }
        _dirty = true; _renderSignature = "";
    }
    private void Hide(string reason)
    {
        _reason = reason;
        if (_strip != null && _strip.IsVisible) _strip.Hide();
        if (_detail != null && _detail.IsVisible) _detail.Hide();
    }
    private void Destroy()
    {
        if (_strip != null) _strip.Close(); if (_detail != null) _detail.Close();
        _strip = _detail = null; _stripBorder = null; _owner = IntPtr.Zero; _detailsOpen = false; _renderSignature = "";
    }
    private void Tick()
    {
        var now = DateTime.UtcNow; var a = _anchor; var m = _metrics;
        if (a == null || !Native.Live(a.Window)) { Hide(_signature.Length == 0 ? "standby" : "host-hidden"); SetInterval(400); Report(); return; }
        Native.RECT frame; Native.GetWindowRect(a.Window, out frame);
        if (_owner != a.Window) { Hide("owner-changed"); _owner = a.Window; _lastFrame = frame; _nextColor = DateTime.MinValue; _detailsOpen = false; }
        if (!_lastFrame.Same(frame)) { _lastFrame = frame; Interlocked.Exchange(ref _motionAt, now.Ticks); Hide("moving"); }
        if (IsMoving) { SetInterval(30); Report(); return; }
        if (a.MenuOpen) { Hide("menu-open"); SetInterval(100); Report(); return; }
        if (_dismissed || now < _pausedUntil) { Hide(_dismissed ? "dismissed" : "paused"); SetInterval(400); Report(); return; }
        if (frame.Width != a.Frame.Width || frame.Height != a.Frame.Height) { Hide("waiting-for-layout"); SetInterval(100); Report(); return; }
        if (m == null || (DateTime.Now - m.At).TotalSeconds > 20 || (now - a.At).TotalSeconds > 4) { Hide("waiting-for-data"); SetInterval(100); Report(); return; }
        EnsureWindows(); SetInterval(30);
        if (!_dirty && now < _nextRender) { Report(); return; }
        _nextRender = now.AddMilliseconds(250);
        double sx = frame.Left - a.Frame.Left, sy = frame.Top - a.Frame.Top;
        if (now >= _nextColor) { _nextColor = now.AddSeconds(3); bool light = _settings.Theme == "light" || (_settings.Theme != "dark" && Native.IsLight(a.Window, (int)(a.Model.Left + sx - 3), (int)(a.Model.Top + sy + 3), _light)); if (light != _light) { _light = light; _dirty = true; _renderSignature = ""; } }
        if (_settings.Theme == "light" && !_light || _settings.Theme == "dark" && _light) { _light = _settings.Theme == "light"; _dirty = true; _renderSignature = ""; }
        double available = (a.Right - a.Left) / a.Scale;
        Func<string, double> measure = keys => {
            var text = string.Join("  |  ", keys.Split(',').Where(k => k.Length > 0).Select(k => MetricText.Value(k, m)));
            return new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(MonitorUI.Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 12, Brushes.Gray, a.Scale).WidthIncludingTrailingWhitespace + 10;
        };
        var shown = _settings.Fit(measure, available);
        // An all-unchecked selection remains reachable for restoring preferences.
        bool settingsOnly = _settings.Selected.Count == 0;
        if (shown.Count == 0 && !settingsOnly) { Hide("no-room"); Report(); return; }
        var textValue = settingsOnly ? "用量设置" : string.Join("  |  ", shown.Select(k => MetricText.Value(k, m)));
        var signature = textValue + _light;
        if (signature != _renderSignature) {
            _renderSignature = signature;
            _stripBorder.Child = MonitorUI.Text(textValue, 12, MonitorUI.Brush(_light ? "#6E6A63" : "#BDB8AE"));
            _stripBorder.ToolTip = "点击查看明细与显示设置\n速率及上下文：最近活跃会话（不代表当前页面）\n额度读数：" + MetricText.Age(m.LimitsAt, DateTime.Now) + (m.Incomplete ? "\n≈ 表示统计正在补读或存在无法计入的记录" : "");
            _stripBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }
        var size = _stripBorder.DesiredSize;
        var stripBounds = OverlayLayout.Place(a.Left + sx, a.Right + sx, a.Model.Top + sy + (a.Model.Height - size.Height * a.Scale) / 2, size.Width, size.Height, a.Scale);
        if (stripBounds.Left < frame.Left || stripBounds.Right > frame.Right || stripBounds.Bottom > frame.Bottom) { Hide("outside-host"); Report(); return; }
        _strip.Width = size.Width; _strip.Height = size.Height; _strip.Place(a.Window, stripBounds); _strip.Reveal(); _reason = "shown";
        if (_detailsOpen) {
            if (_dirty || (now - _lastDetail).TotalSeconds >= 2) {
                _lastDetail = now; _dirty = false;
                var card = MonitorUI.Detail(m, _settings, _light, _showSettings, () => { _detailsOpen = false; _detail.Hide(); },
                    () => { _showSettings = !_showSettings; _dirty = true; }, SaveSettings);
                var area = System.Windows.Forms.Screen.FromHandle(a.Window).WorkingArea;
                card.MaxHeight = Math.Max(180, (Math.Min(area.Bottom, frame.Bottom) - Math.Max(area.Top, frame.Top) - 50) / a.Scale);
                _detail.Content = new Border { Padding = new Thickness(10), Child = card };
                ((FrameworkElement)_detail.Content).Measure(new Size(370, double.PositiveInfinity));
            }
            var content = (FrameworkElement)_detail.Content; var ds = content.DesiredSize;
            var work = System.Windows.Forms.Screen.FromHandle(a.Window).WorkingArea;
            double x = Math.Max(work.Left, Math.Min(stripBounds.Right - ds.Width * a.Scale, work.Right - ds.Width * a.Scale));
            double y = Math.Max(work.Top, stripBounds.Top - ds.Height * a.Scale - 3);
            _detail.Width = ds.Width; _detail.Height = ds.Height;
            _detail.Place(a.Window, new Rect(x, y, ds.Width * a.Scale, ds.Height * a.Scale)); _detail.Reveal();
        } else { _dirty = false; if (_detail.IsVisible) _detail.Hide(); }
        Report();
    }
    private void SetInterval(int ms) { if (_timer.Interval.TotalMilliseconds != ms) _timer.Interval = TimeSpan.FromMilliseconds(ms); }
    private void Report()
    {
        if (DateTime.UtcNow < _nextReport) return; _nextReport = DateTime.UtcNow.AddSeconds(5);
        try { JsonData.WriteAtomic(Path.Combine(_state, "status.json"), new { version = Program.Version, state = _reason, visible = _strip != null && _strip.IsVisible, details = _detail != null && _detail.IsVisible, owner = _owner.ToInt64(), theme = _light ? "light" : "dark", pluginVersion = _metrics == null ? null : _metrics.PluginVersion, sessionBinding = "latest-active", at = DateTime.UtcNow.ToString("o") }); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
