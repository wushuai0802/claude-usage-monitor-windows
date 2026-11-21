using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Collections.Generic;

internal static class RegressionTests
{
    private static int _checks;
    private static void Equal(string label, object expected, object actual) { _checks++; if (!Equals(expected, actual)) throw new Exception(label + ": expected " + expected + ", got " + actual); }
    private static string Json(object value) { return new JavaScriptSerializer().Serialize(value); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static string Assistant(string id, DateTime at, long output, string stop = "end_turn", string model = "claude-test")
    {
        return Json(new { type = "assistant", timestamp = at.ToUniversalTime().ToString("o"), message = new { id = id, model = model, stop_reason = stop, usage = new { input_tokens = 10, cache_creation_input_tokens = 20, cache_read_input_tokens = 70, output_tokens = output } } }) + "\n";
    }
    private static string User(DateTime at) { return Json(new { type = "user", timestamp = at.ToUniversalTime().ToString("o"), message = new { content = "UTF-8 测试 · mentions usage but is not an assistant response" } }) + "\n"; }
    private static object Snapshot(string session, DateTime at, object context, double used, int sequence = 1)
    {
        return new { schemaVersion = 2, pluginVersion = Program.Version, sessionId = session, sequence = sequence, updatedAt = at.ToUniversalTime().ToString("o"), contextAt = at.ToUniversalTime().ToString("o"),
            rateLimitsAt = at.ToUniversalTime().ToString("o"), context = context, rateLimits = new[] { new { kind = "five_hour", percentUsed = used, resetsAt = at.AddHours(5).ToUniversalTime().ToString("o") } }, complete = true };
    }
    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "claude-monitor-test-" + Guid.NewGuid().ToString("N"));
        try {
            var projects = Path.Combine(root, "projects"); var state = Path.Combine(root, "state");
            Directory.CreateDirectory(projects); Directory.CreateDirectory(Path.Combine(state, "context")); Directory.CreateDirectory(Path.Combine(state, "measurements"));
            // Keep all initial records in the same local day even when CI runs at midnight.
            DateTime now = DateTime.Today.AddHours(12);
            var path = Path.Combine(projects, "parent.jsonl");
            File.WriteAllText(path, User(now.AddSeconds(-4)) + Assistant("m1", now.AddSeconds(-2), 40) + Assistant("synthetic", now, 999, "end_turn", "<synthetic>"));
            var children = Path.Combine(projects, "parent", "subagents"); Directory.CreateDirectory(children);
            File.WriteAllText(Path.Combine(children, "child.jsonl"), Assistant("m1", now.AddSeconds(-3), 10) + Assistant("child", now.AddSeconds(-1), 5));
            var context = Path.Combine(state, "context", "parent.json"); File.WriteAllText(context, "{\"percent\":26}"); File.SetLastWriteTime(context, now.AddSeconds(-3));
            var limits = Path.Combine(state, "limits.json");
            File.WriteAllText(limits, Json(new { updatedAt = now.ToUniversalTime().ToString("o"), rateLimits = new[] { new { kind = "five_hour", percentUsed = 90, resetsAt = now.AddHours(-1).ToUniversalTime().ToString("o") } } }));
            using (var collector = new Collector(projects, limits, () => now, false)) {
                var m = collector.Poll();
                Equal("deduplicate across files, ignore synthetic", 45L, m.Today.Output);
                Equal("out-of-order duplicate cannot replace newer usage", 20L, m.Today.Input);
                Equal("child agent cannot steal response rate", 20.0, m.Rate);
                Equal("parent session provenance", "parent", m.SessionId);
                Equal("expired limit is unknown", null, m.FiveHour.Used);
                Equal("expired reset label", "已重置", MetricText.Percent(m.FiveHour));
                Equal("legacy context retained", 26.0, m.ContextPercent);
                Equal("cache denominator includes writes", "70.0%", MetricText.CacheRate(m.Today));
                long bytes = collector.BytesRead;
                for (int i = 0; i < 20; i++) collector.Poll();
                Equal("idle does not reread unchanged log", bytes, collector.BytesRead);
                Equal("idle does not enumerate directory tree", 1, collector.DirectoryScans);
                File.AppendAllText(path, User(now)); collector.Changed(path);
                Equal("new request clears previous rate", 0.0, collector.Poll().Rate);
                now = now.AddSeconds(2);
                File.AppendAllText(path, Assistant("tool", now, 30, "tool_use")); collector.Changed(path);
                m = collector.Poll(); Equal("tool-bound response preserves its request average", 15.0, m.Rate); Equal("tool call state", "tools", m.RateState);
                now = now.AddSeconds(2);
                var line = Encoding.UTF8.GetBytes(User(now) + Assistant("split", now.AddSeconds(1), 9));
                using (var f = new FileStream(path, FileMode.Append)) f.Write(line, 0, line.Length - 2);
                collector.Changed(path);
                Equal("incomplete JSONL record not counted", 75L, collector.Poll().Today.Output);
                using (var f = new FileStream(path, FileMode.Append)) f.Write(line, line.Length - 2, 2);
                collector.Changed(path); now = now.AddSeconds(2);
                Equal("completed split record counted once", 84L, collector.Poll().Today.Output);
                now = now.AddSeconds(6); Equal("completed speed expires", 0.0, collector.Poll().Rate);
                File.WriteAllText(path, Assistant("m1", now.AddSeconds(-30), 1)); collector.Changed(path);
                Equal("truncate/replay does not double count", 84L, collector.Poll().Today.Output);
                var slot = Path.Combine(state, "measurements", "parent.slot0.json");
                var snapshotAt = now;
                File.WriteAllText(slot, Json(Snapshot("parent", snapshotAt, new { percent = 33, tokens = 33000 }, 44)));
                now = now.AddSeconds(6); m = collector.Poll();
                Equal("v2 current context", 33.0, m.ContextPercent); Equal("v2 current quota", 44.0, m.FiveHour.Used);
                Equal("plugin version reported", Program.Version, m.PluginVersion);
                var slot1 = Path.Combine(state, "measurements", "parent.slot1.json");
                File.WriteAllText(slot1, Json(Snapshot("parent", snapshotAt, new { percent = 35, tokens = 35000 }, 46, 2)));
                now = now.AddSeconds(6); m = collector.Poll();
                Equal("same-millisecond context uses newer sequence", 35.0, m.ContextPercent);
                Equal("same-millisecond quota uses newer sequence", 46.0, m.FiveHour.Used);
                File.WriteAllText(slot, "{\"schemaVersion\":2,"); m = collector.Poll();
                Equal("torn snapshot keeps last valid context", 35.0, m.ContextPercent); Equal("torn snapshot reports stale read", true, m.SnapshotError);
                File.WriteAllText(slot1, Json(Snapshot("parent", now, new { percent = (double?)null, tokens = (long?)null }, 45)));
                now = now.AddSeconds(6); m = collector.Poll();
                Equal("compaction invalidates old percentage", null, m.ContextPercent);
                Equal("compaction invalidates old prompt count", null, m.ContextTokens);
                Equal("one valid slot recovers quota", 45.0, m.FiveHour.Used);
                File.WriteAllText(slot1, Json(Snapshot("parent", now, new { percent = 101 }, -1))); m = collector.Poll();
                Equal("invalid context range is not displayed", null, m.ContextPercent);
                Equal("invalid quota is not fabricated", null, m.FiveHour.Used);
                now = now.Date.AddDays(1).AddHours(12);
                File.WriteAllText(path, Assistant("tomorrow", now.AddSeconds(-1), 7)); File.SetLastWriteTime(path, now);
                m = collector.Poll(); Equal("day rollover resets total", 7L, m.Today.Output);
            }
            var profiles = Path.Combine(root, "local-agent-mode-sessions");
            var profile = Path.Combine(profiles, "account", "organization", "profile");
            var thirdPartyProjects = Path.Combine(profile, ".claude", "projects", "session");
            var thirdPartyState = Path.Combine(root, "third-party-state");
            var thirdPartyMeasurements = Path.Combine(thirdPartyState, "measurements");
            Directory.CreateDirectory(thirdPartyProjects);
            Directory.CreateDirectory(thirdPartyMeasurements);
            var transcript = Path.Combine(thirdPartyProjects, "third-party.jsonl");
            var stray = Path.Combine(profile, "diagnostics.jsonl");
            File.WriteAllText(stray, Assistant("unrelated", now, 1000));
            File.WriteAllText(transcript, User(now.AddSeconds(-2)) + Assistant("tomorrow", now, 80));
            File.SetLastWriteTime(transcript, now);
            File.WriteAllText(Path.Combine(thirdPartyMeasurements, "other.slot0.json"), Json(Snapshot("other", now, new { percent = 25, tokens = 90000 }, 82)));
            using (var collector = new Collector(projects, Path.Combine(thirdPartyState, "limits.json"), () => now, false, profiles)) {
                collector.Changed(stray);
                var m = collector.Poll();
                Equal("third-party transcript discovered in its profile", "third-party", m.SessionId);
                Equal("third-party source retained", true, m.ThirdParty);
                Equal("transcripts from both locations deduplicate", 80L, m.Today.Output);
                Equal("third-party context falls back to prompt tokens", 100L, m.ContextTokens);
                Equal("third-party rate falls back to transcript timing", 40.0, m.Rate);
                Equal("unrelated subscription quota is not used for third-party session", null, m.FiveHour);
                File.WriteAllText(Path.Combine(thirdPartyMeasurements, "third-party.slot0.json"), Json(Snapshot("third-party", now, new { percent = 1, tokens = 100 }, 12)));
                now = now.AddSeconds(6);
                m = collector.Poll();
                Equal("matching third-party session can provide quota", 12.0, m.FiveHour.Used);
                Equal("matching third-party session can provide context", 1.0, m.ContextPercent);
                File.AppendAllText(transcript, User(now) + Assistant("new-third-party", now.AddSeconds(1), 13));
                collector.Changed(transcript); now = now.AddSeconds(2);
                Equal("third-party append is picked up without directory rescan", 93L, collector.Poll().Today.Output);
            }
            var reset = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToLocalTime();
            var quotaBody = Json(new { unit = "percent_used", platform = "anthropic",
                five_hour = new { utilization = 18.5, resets_at = "1893456000" },
                seven_day = new { utilization = 0, resets_at = "2030-01-01T00:00:00Z" } });
            var quota = Vm2ApiQuota.Parse(quotaBody, reset.AddMinutes(-1));
            Equal("vm2api uses the returned percent without changing units", 18.5, quota.FiveHour.Used);
            Equal("vm2api parses a Unix reset", reset, quota.FiveHour.ResetsAt);
            Equal("vm2api parses an ISO reset", reset, quota.SevenDay.ResetsAt);
            Equal("vm2api accepts a confirmed zero-percent window", 0.0, quota.SevenDay.Used);
            Equal("vm2api never shows pre-reset usage after the reset", "已重置", MetricText.Percent(Vm2ApiQuota.CurrentWindow(quota.FiveHour, reset)));
            Equal("vm2api rejects the other platform", null, Vm2ApiQuota.Parse(quotaBody.Replace("anthropic", "openai"), now));
            Equal("vm2api rejects unrecognized units", null, Vm2ApiQuota.Parse(quotaBody.Replace("percent_used", "percent_remaining"), now));
            Equal("vm2api does not invent a window", null, Vm2ApiQuota.Parse("{\"platform\":\"anthropic\",\"unit\":\"percent_used\",\"five_hour\":null}", now));
            Equal("vm2api accepts an explicitly configured Tailnet endpoint", true, Vm2ApiQuota.BaseUrl("http://100.64.0.8:8787") != null);
            Equal("vm2api does not send an issued key over public HTTP", null, Vm2ApiQuota.BaseUrl("http://198.51.100.1:8787"));
            Equal("vm2api endpoint cannot carry credentials in the URL", null, Vm2ApiQuota.BaseUrl("https://test@example.invalid"));
            var ratesDir = Path.Combine(state, "rates"); Directory.CreateDirectory(ratesDir);
            var rateSlot = Path.Combine(ratesDir, "parent.slot0.json");
            var rateStart = now;
            var rateSampleAt = rateStart.AddSeconds(4);
            long rateSequence = 0;
            Action<string, long> writeRate = (phase, duration) => {
                rateSequence++;
                File.WriteAllText(rateSlot, Json(new { schemaVersion = 1, kind = "modelRate", sessionId = "parent", pluginVersion = Program.Version,
                    updatedAt = now.ToUniversalTime().ToString("o"), turnStartedAt = rateStart.ToUniversalTime().ToString("o"), turnId = "fixture", sequence = rateSequence,
                    phase = phase, sample = new { outputTokens = 400, durationMs = duration, completedAt = rateSampleAt.ToUniversalTime().ToString("o") }, complete = true }));
                File.SetLastWriteTime(rateSlot, now.AddMilliseconds(rateSequence));
            };
            now = rateStart.AddSeconds(10); writeRate("tools", 4000);
            var rateReader = new RateSnapshots(state);
            Func<Metrics> baseRate = () => new Metrics { SessionId = "parent", HumanPromptAt = rateStart, ActivityAt = rateStart, At = now };
            var rateMetrics = baseRate(); rateReader.Apply(rateMetrics, now);
            Equal("stream request average survives tool phase", 100.0, rateMetrics.Rate);
            Equal("stream observer source is explicit", "turn.step", rateMetrics.RateSource);
            Equal("stream output count is retained", 400L, rateMetrics.RateOutputTokens);
            Equal("stream duration is retained", 4000L, rateMetrics.RateDurationMs);
            now = now.AddSeconds(2); writeRate("waiting", 4000); rateMetrics = baseRate(); rateReader.Apply(rateMetrics, now);
            Equal("next request wait retains the current turn sample", 100.0, rateMetrics.Rate);
            now = now.AddSeconds(2); writeRate("generating", 4000); rateMetrics = baseRate(); rateReader.Apply(rateMetrics, now);
            Equal("generation displays the previous completed request average", 100.0, rateMetrics.Rate);
            now = now.AddSeconds(2); writeRate("completed", 4000); rateMetrics = baseRate(); rateReader.Apply(rateMetrics, now);
            Equal("terminal main value expires after five seconds", 0.0, rateMetrics.Rate);
            Equal("expired sample remains available in details", 100.0, rateMetrics.LastRate);
            var endedNow = now;
            now = now.AddSeconds(18); rateMetrics = baseRate(); rateReader.Apply(rateMetrics, now);
            Equal("completed sample stays exact after heartbeat cancellation", 100.0, rateMetrics.LastRate);
            Equal("completed historical sample does not become a live rate", 0.0, rateMetrics.Rate);
            Equal("completed historical sample keeps its timing source", "turn.step", rateMetrics.RateSource);
            now = endedNow;
            now = now.AddSeconds(1); rateMetrics = baseRate(); rateMetrics.HumanPromptAt = now; rateReader.Apply(rateMetrics, now);
            Equal("old turn cannot override a newly submitted prompt", 0.0, rateMetrics.Rate);
            writeRate("waiting", 4000); rateMetrics = baseRate(); rateReader.Apply(rateMetrics, now);
            now = now.AddSeconds(19); rateMetrics = baseRate(); rateMetrics.Rate = 37; rateReader.Apply(rateMetrics, now);
            Equal("abandoned heartbeat cannot override transcript fallback", 37.0, rateMetrics.Rate);
            writeRate("tools", 4000); rateMetrics = baseRate(); rateReader.Apply(rateMetrics, now);
            Equal("fresh heartbeat can retain an older valid request sample", 100.0, rateMetrics.Rate);
            File.WriteAllText(rateSlot, "{"); File.SetLastWriteTime(rateSlot, now.AddSeconds(1));
            rateMetrics = baseRate(); rateReader.Apply(rateMetrics, now);
            Equal("partial rate write preserves last complete sample", 100.0, rateMetrics.Rate);
            Equal("partial rate write is reported", true, rateMetrics.SnapshotError);
            now = now.AddSeconds(2); writeRate("waiting", 0); rateMetrics = baseRate(); rateReader.Apply(rateMetrics, now);
            Equal("invalid request duration does not create a speed", 0.0, rateMetrics.Rate);
            now = now.AddSeconds(2); writeRate("interrupted", 4000); rateMetrics = baseRate(); rateReader.Apply(rateMetrics, now);
            Equal("interrupted turn clears the main value", 0.0, rateMetrics.Rate);
            Equal("interrupted turn retains a labeled historical sample", 100.0, rateMetrics.LastRate);
            var settingsPath = Path.Combine(state, "ui-settings.json");
            var settings = new DisplaySettings(); settings.Selected.Remove("today"); settings.Save(settingsPath);
            settings.Theme = "dark"; settings.Save(settingsPath);
            Equal("settings persist", "dark", DisplaySettings.Load(settingsPath).Theme);
            Equal("selection persists", false, DisplaySettings.Load(settingsPath).Selected.Contains("today"));
            File.WriteAllText(settingsPath, "{"); Equal("invalid settings recover backup", "auto", DisplaySettings.Load(settingsPath).Theme);
            settings = new DisplaySettings();
            var fit = settings.Fit(keys => keys.Split(',').Length * 50, 100);
            Equal("narrow layout retains limits", true, fit.Contains("limits")); Equal("narrow layout retains context", true, fit.Contains("context"));
            fit = settings.Fit(keys => keys.Split(',').Length * 50, 100, false);
            Equal("unknown third-party quota gives its space to rate", true, fit.Contains("rate") && !fit.Contains("limits"));
            Equal("no room hides all metrics", 0, settings.Fit(keys => 50, 20).Count);
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 }) {
                var placed = OverlayLayout.Place(100 * scale, 500 * scale, 60 * scale, 200, 24, scale);
                Equal("scaled right edge", 500 * scale, placed.Right); Equal("scaled width", 200 * scale, placed.Width);
            }
            // Geometry captured from Claude 4.23118; all coordinates are relative to its toolbar.
            var toolbar = new Rect(0, 0, 769, 24);
            var modelButton = new Rect(534, 0, 102, 24);
            var nativeControls = new[] { new Rect(10, 0, 92, 24), new Rect(105, 0, 79, 24),
                new Rect(193, 6, 13, 12), new Rect(213, 4, 40, 16), modelButton, new Rect(639, 0, 91, 24) };
            var free = OverlayLayout.FreeRange(toolbar, modelButton, nativeControls, 1);
            Equal("real toolbar uses all available middle space", 269.0, free.Width);
            Equal("worktree label keeps six-pixel clearance", 259.0, free.Left);
            Equal("model button keeps six-pixel clearance", 528.0, free.Right);
            var extra = nativeControls.Concat(new[] { new Rect(480, 0, 24, 24) });
            Equal("an extra right-hand control remains uncovered", 474.0, OverlayLayout.FreeRange(toolbar, modelButton, extra, 1).Right);
            var wideLabel = nativeControls.Concat(new[] { new Rect(213, 4, 100, 16) });
            Equal("growing native label reduces the free range", 209.0, OverlayLayout.FreeRange(toolbar, modelButton, wideLabel, 1).Width);
            Equal("empty left side uses toolbar boundary", 522.0, OverlayLayout.FreeRange(toolbar, modelButton, new Rect[0], 1).Width);
            Equal("occupied toolbar has no safe space", true, OverlayLayout.FreeRange(toolbar, modelButton, new[] { new Rect(0, 0, 534, 24) }, 1).IsEmpty);
            var thirdPartyComposer = new Rect(0, 0, 430, 80);
            var thirdPartyInput = new Rect(10, 10, 410, 20);
            var thirdPartyModel = new Rect(191, 42, 194, 28);
            var thirdPartyRow = OverlayLayout.ComposerRow(thirdPartyComposer, thirdPartyInput, thirdPartyModel, 1);
            Equal("picker-only toolbar uses the compact composer bounds", new Rect(0, 42, 430, 28), thirdPartyRow);
            var thirdPartyGap = OverlayLayout.FreeRange(thirdPartyRow, thirdPartyModel,
                new[] { new Rect(10, 42, 28, 28), new Rect(44, 42, 28, 28), thirdPartyModel }, 1);
            Equal("third-party strip clears the two native buttons", new Rect(78, 42, 107, 28), thirdPartyGap);
            Equal("whole conversation is not a composer", true, OverlayLayout.ComposerRow(new Rect(0, 0, 430, 300), thirdPartyInput, thirdPartyModel, 1).IsEmpty);
            Equal("off-row content does not steal toolbar space", free, OverlayLayout.FreeRange(toolbar, modelButton, nativeControls.Concat(new[] { new Rect(253, -40, 281, 16) }), 1));
            Equal("oversized overlay is not placed over controls", true, OverlayLayout.Place(100, 199, 0, 100, 20, 1).IsEmpty);
            Equal("fractional boundary rounding stays inside gap", true, OverlayLayout.Place(100.4, 199.8, 0, 100, 20, 1).IsEmpty);
            Equal("exact safe pixel width is accepted", 101.0, OverlayLayout.Place(100.4, 199.8, 0, 98, 20, 1).Left);
            Equal("detail explanatory text still wraps", TextWrapping.Wrap, MonitorUI.Text("explanation", 12, Brushes.Gray).TextWrapping);
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 }) {
                foreach (var value in new[] { "0.0 tok/s  |  5h 24% · 7d 22%  |  上下文 21%", "0.0 tok/s  |  5h 已重置 · 7d 100%  |  上下文 21%" }) {
                    var measured = MonitorUI.MeasureStrip(value, scale);
                    var tb = MonitorUI.StripText(value, Brushes.Gray);
                    var border = new Border { Padding = new Thickness(5, 2, 5, 2), Child = tb, ClipToBounds = true, UseLayoutRounding = true };
                    TextOptions.SetTextFormattingMode(border, TextFormattingMode.Ideal);
                    double constrained = measured.Width - 1 / scale;
                    border.Measure(new Size(constrained, measured.Height));
                    border.Arrange(new Rect(0, 0, constrained, measured.Height)); border.UpdateLayout();
                    Equal("one physical pixel does not cause a second line", true, tb.ContentStart.GetLineStartPosition(1) == null);
                    var glyph = tb.TranslatePoint(tb.ContentStart.GetCharacterRect(LogicalDirection.Forward).TopLeft, border);
                    Equal("first text line is not cropped vertically", true, glyph.Y >= 0);
                    Equal("measurement ignores previous window constraints", measured, MonitorUI.MeasureStrip(value, scale));
                    Equal("measured width includes rounding reserve", true, measured.Width > tb.DesiredSize.Width + 10);
                }
                var sample = new Metrics { Rate = 123.4, Today = new TokenUsage { Input = 9999999999, CacheRead = 9999999999, Output = 9999999999 },
                    FiveHour = new Limit { Expired = true }, SevenDay = new Limit { Used = 100 }, ContextPercent = 99 };
                foreach (double pixels in new[] { 120.0, 180.0, 269.0, 400.0, 700.0 }) {
                    Func<string, double> width = keys => MonitorUI.MeasureStrip(string.Join("  |  ", keys.Split(',').Select(k => MetricText.Value(k, sample))), scale).Width;
                    var selected = new DisplaySettings().Fit(width, pixels / scale);
                    Equal("long/reset metrics fit their physical range", true, selected.Count == 0 || width(string.Join(",", selected)) * scale <= pixels);
                }
            }
            Equal("unknown context remains unknown", "--", MetricText.ContextText(new Metrics()));
            Equal("unknown window shows approximate token count", "~12.0万", MetricText.ContextText(new Metrics { ContextTokens = 120000 }));
            Equal("empty cache denominator", "--", MetricText.CacheRate(new TokenUsage()));
            Equal("partial totals are marked", "≈50", MetricText.Value("today", new Metrics { Today = new TokenUsage { Output = 50 }, Incomplete = true }));
            int edits = 0, closes = 0;
            settings = new DisplaySettings();
            var card = MonitorUI.Detail(new Metrics(), settings, true, true, () => closes++, () => {}, () => edits++);
            card.Measure(new Size(350, 1200)); card.Arrange(new Rect(new Point(), card.DesiredSize)); card.UpdateLayout();
            var controls = Descendants(card).ToArray();
            var cache = controls.OfType<CheckBox>().Single(c => Equals(c.Content, "缓存命中"));
            cache.IsChecked = false; cache.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Equal("checkbox changes selected metrics", false, settings.Selected.Contains("cache"));
            Equal("checkbox invokes settings persistence callback", 1, edits);
            var up = controls.OfType<Button>().Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "提高缓存命中保留优先级");
            int oldIndex = settings.Priority.IndexOf("cache"); up.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Equal("priority button moves the intended metric", oldIndex - 1, settings.Priority.IndexOf("cache"));
            controls.OfType<Button>().Single(b => Equals(b.Content, "关闭")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Equal("detail close control works", 1, closes);
            var passive = new PassiveWindow("test");
            var handle = new WindowInteropHelper(passive).EnsureHandle();
            Equal("passive window keeps NOACTIVATE", true, (Native.GetLong(handle, -20).ToInt64() & 0x08000000) != 0);
            Equal("passive window does not enter taskbar", false, passive.ShowInTaskbar);
            passive.Close();
            Console.WriteLine("PASS: " + _checks + " collector, snapshot, settings and DPI regression checks.");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
