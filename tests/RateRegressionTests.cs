using System;
using System.IO;
using System.Web.Script.Serialization;

// Uses only the existing Collector public surface so this can also demonstrate
// the failure against the unchanged 1.1.1 sources.
internal static class RateRegressionTests
{
    private static int _checks;
    private static void Equal(string label, object expected, object actual) { _checks++; if (!Equals(expected, actual)) throw new Exception(label + ": expected " + expected + ", got " + actual); }
    private static string Json(object value) { return new JavaScriptSerializer().Serialize(value) + "\n"; }
    private static string Human(DateTime at) { return Json(new { type = "user", timestamp = at.ToUniversalTime().ToString("o"), message = new { content = "fixture" } }); }
    private static string Tool(DateTime at) { return Json(new { type = "user", timestamp = at.ToUniversalTime().ToString("o"), message = new { content = new[] { new { type = "tool_result", tool_use_id = "fixture", content = "fixture" } } } }); }
    private static string Assistant(string id, DateTime at, int output, string stop)
    {
        return Json(new { type = "assistant", timestamp = at.ToUniversalTime().ToString("o"), message = new { id = id, model = "fixture", stop_reason = stop, usage = new { input_tokens = 1, output_tokens = output } } });
    }
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "claude-rate-test-" + Guid.NewGuid().ToString("N"));
        try {
            var projects = Path.Combine(root, "projects"); Directory.CreateDirectory(projects);
            DateTime start = DateTime.Today.AddHours(12), now = start.AddSeconds(2);
            var path = Path.Combine(projects, "parent.jsonl");
            File.WriteAllText(path, Human(start) + Assistant("request-1", now, 30, "tool_use"));
            using (var collector = new Collector(projects, Path.Combine(root, "state", "limits.json"), () => now, false)) {
                Equal("tool-bound response retains its valid request average", 15.0, collector.Poll().Rate);
                now = start.AddSeconds(12); File.AppendAllText(path, Tool(now)); collector.Changed(path);
                Equal("tool result retains the previous model request average", 15.0, collector.Poll().Rate);
                now = start.AddSeconds(28); File.AppendAllText(path, Assistant("request-1", now, 30, "tool_use")); collector.Changed(path);
                var m = collector.Poll();
                Equal("late duplicate cannot time a new model request", 15.0, m.Rate);
                Equal("late duplicate cannot overwrite waiting phase", "waiting", m.RateState);
                now = start.AddSeconds(30); File.AppendAllText(path, Tool(now)); collector.Changed(path); collector.Poll();
                now = start.AddSeconds(34); File.AppendAllText(path, Assistant("request-2", now, 80, "end_turn")); collector.Changed(path);
                m = collector.Poll();
                Equal("next response excludes the preceding tool wait", 20.0, m.Rate);
                Equal("duplicate response contributes tokens once", 110L, m.Today.Output);
                now = start.AddSeconds(40); m = collector.Poll(); Equal("finished turn expires as a live rate", 0.0, m.Rate); Equal("idle strip shows no rate", true, m.DisplayRate == null); Equal("idle strip text", "-- tok/s", MetricText.Value("rate", m));
                now = start.AddSeconds(41); File.AppendAllText(path, Human(now)); collector.Changed(path);
                m = collector.Poll(); Equal("new human turn clears the live sample", 0.0, m.Rate); Equal("strip keeps the last average while a turn waits", 20.0, m.DisplayRate.Value);
                now = start.AddSeconds(44); File.AppendAllText(path, Assistant("request-3", now, 0, null)); collector.Changed(path); collector.Poll();
                now = start.AddSeconds(46); File.AppendAllText(path, Assistant("request-3", now, 50, "end_turn")); collector.Changed(path);
                Equal("partial response can acquire its final output count", 10.0, collector.Poll().Rate);
            }
            Console.WriteLine("PASS: " + _checks + " rate sampling regression checks."); return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
