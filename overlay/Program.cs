using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;

[assembly: AssemblyVersion("1.1.5.0")]
[assembly: AssemblyFileVersion("1.1.5.0")]
[assembly: AssemblyTitle("Claude Usage Monitor (unofficial)")]

internal static class Program
{
    internal const string Version = "1.1.5";
    private static string _state;
    private static readonly ManualResetEvent Stop = new ManualResetEvent(false);
    private static volatile bool _desktopAlive;
    private static IntPtr _window;
    private static readonly object LogLock = new object();
    internal static void Note(string text)
    {
        lock (LogLock) try {
            var path = Path.Combine(_state, "overlay.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) {
                var previous = path + ".1"; if (File.Exists(previous)) File.Delete(previous); File.Move(path, previous);
            }
            File.AppendAllText(path, DateTime.Now.ToString("s") + " " + text + Environment.NewLine);
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private static void Worker(Action run)
    {
        var thread = new Thread(() => { try { run(); } catch (Exception ex) { Note("worker stopped: " + ex.GetType().Name); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.MTA); thread.Start();
    }
    [STAThread]
    private static void Main(string[] args)
    {
        _state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsageMonitor");
        int stateArg = Array.IndexOf(args, "--state-dir"); if (stateArg >= 0 && stateArg + 1 < args.Length) _state = Path.GetFullPath(args[stateArg + 1]);
        Directory.CreateDirectory(_state);
        int preview = Array.IndexOf(args, "--render-preview");
        if (preview >= 0 && preview + 1 < args.Length) { MonitorUI.RenderPreview(Path.GetFullPath(args[preview + 1])); return; }
        bool created;
        using (var mutex = new Mutex(true, "Local\\ClaudeUsageOverlay", out created)) {
            if (!created) return;
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { Native.SetProcessDPIAware(); }
            AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges", false);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (s, e) => { Note("UI error: " + e.Exception.GetType().Name); e.Handled = true; };
            app.Exit += (s, e) => Stop.Set();
            var controller = new OverlayController(_state);
            Worker(() => {
                var signature = "";
                while (!Stop.WaitOne(0)) {
                    try {
                        var pids = Native.DesktopPids(); _desktopAlive = pids.Count != 0;
                        var next = Native.DesktopIdentity(pids);
                        if (next != signature) { signature = next; controller.HostState(signature); }
                        Interlocked.Exchange(ref _window, Native.ChooseWindow(pids, Interlocked.CompareExchange(ref _window, IntPtr.Zero, IntPtr.Zero)));
                    } catch (Exception ex) { Note("host check: " + ex.GetType().Name); }
                    Stop.WaitOne(1500);
                }
            });
            Worker(() => {
                var finder = new AnchorFinder();
                while (!Stop.WaitOne(0)) {
                    try {
                        var window = Interlocked.CompareExchange(ref _window, IntPtr.Zero, IntPtr.Zero);
                        if (!controller.IsMoving) controller.PostAnchor(window == IntPtr.Zero ? null : finder.Find(window));
                    } catch (Exception ex) { Note("locating: " + ex.GetType().Name); }
                    Stop.WaitOne(_desktopAlive ? 400 : 2000);
                }
            });
            Worker(() => {
                Collector collector = null;
                try {
                    while (!Stop.WaitOne(0)) {
                        if (!_desktopAlive) { if (collector != null) { collector.Dispose(); collector = null; } Stop.WaitOne(2000); continue; }
                        try {
                            if (collector == null) collector = new Collector(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects"), Path.Combine(_state, "limits.json"));
                            controller.PostMetrics(collector.Poll());
                        } catch (Exception ex) { Note("collecting: " + ex.GetType().Name); }
                        Stop.WaitOne(1000);
                    }
                } finally { if (collector != null) collector.Dispose(); }
            });
            Note("started v" + Version); app.Run(); Note("stopped");
        }
    }
}
