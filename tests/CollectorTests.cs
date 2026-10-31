using System;
using System.Globalization;
using System.IO;

internal static class CollectorTests
{
    private static int _failures;

    private static void Equal(string name, object expected, object actual)
    {
        if (Equals(expected, actual)) { Console.WriteLine("PASS " + name); return; }
        _failures++;
        Console.WriteLine("FAIL " + name + ": expected " + expected + ", got " + actual);
    }

    private static string Iso(DateTime utc) { return utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture); }

    private static string Assistant(string id, DateTime at, int input, int write, int read, int output, string model = "claude-test", string stop = "null")
    {
        return "{\"message\":{\"model\":\"" + model + "\",\"id\":\"" + id + "\",\"stop_reason\":" + stop + ",\"usage\":{\"input_tokens\":" + input + ",\"cache_creation_input_tokens\":" + write + ",\"cache_read_input_tokens\":" + read + ",\"output_tokens\":" + output + "}},\"type\":\"assistant\",\"timestamp\":\"" + Iso(at) + "\"}";
    }

    private static string User(DateTime at, string text)
    {
        return "{\"message\":{\"role\":\"user\",\"content\":\"" + text + "\"},\"type\":\"user\",\"timestamp\":\"" + Iso(at) + "\"}";
    }

    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "usage-overlay-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var now = DateTime.UtcNow.AddSeconds(-20);
            var projects = Path.Combine(root, "projects", "project");
            var state = Path.Combine(root, "state");
            var subagents = Path.Combine(projects, "session-a", "subagents");
            Directory.CreateDirectory(subagents);
            Directory.CreateDirectory(Path.Combine(state, "context"));

            File.WriteAllLines(Path.Combine(projects, "session-a.jsonl"), new[] {
                User(now, "text mentioning \\\"usage\\\" must not count"),
                // One response across two records: the later figures replace the earlier.
                Assistant("m1", now.AddSeconds(1), 1, 2, 3, 5),
                Assistant("m1", now.AddSeconds(2), 1, 2, 3, 10),
                Assistant("synthetic", now.AddSeconds(2), 1000, 0, 0, 1000, "<synthetic>"),
                Assistant("old", now.AddDays(-2), 5000, 0, 0, 5000),
                "{ not json \"type\":\"assistant\" \"usage\"",
            });
            // The same response replayed in a subagent's transcript counts once. It is the newest
            // activity, so the context read must fall back to the parent session's id.
            File.WriteAllLines(Path.Combine(subagents, "agent-1.jsonl"), new[] {
                User(now.AddSeconds(3), "go"),
                Assistant("m1", now.AddSeconds(5), 1, 2, 3, 10),
                Assistant("m2", now.AddSeconds(7), 4, 0, 6, 20),
            });
            File.WriteAllText(Path.Combine(state, "context", "session-a.json"), "{\"percent\":18}");
            File.WriteAllText(Path.Combine(state, "limits.json"), "{\"updatedAt\":\"" + Iso(now) + "\",\"rateLimits\":[" +
                "{\"kind\":\"five_hour\",\"percentUsed\":90,\"resetsAt\":\"" + Iso(now.AddHours(-1)) + "\"}," +
                "{\"kind\":\"seven_day\",\"percentUsed\":22.5,\"resetsAt\":\"" + Iso(now.AddDays(3)) + "\"}]}");

            var collector = new Collector(Path.Combine(root, "projects"), Path.Combine(state, "limits.json"));
            var m = collector.Poll();
            Equal("input excludes duplicates, synthetic and old", 5L, m.Today.Input);
            Equal("cache write", 2L, m.Today.CacheWrite);
            Equal("cache read", 9L, m.Today.CacheRead);
            Equal("output uses latest figures", 30L, m.Today.Output);
            Equal("total", 46L, m.Today.Total);
            Equal("cache rate over input plus cache", "56.3%", OverlayForm.CacheRate(m.Today));
            Equal("no input shows dashes", "--", OverlayForm.CacheRate(new TokenUsage()));
            Equal("context from parent session", 18.0, m.ContextPercent);
            Equal("expired window has no reading", null, m.FiveHour.Used);
            Equal("expired window says so", "已重置", OverlayForm.Percent(m.FiveHour));
            Equal("live window reading", 22.5, m.SevenDay.Used);
            Equal("live window shows percent", "23%", OverlayForm.Percent(new Limit { Used = 22.6 }));
            Equal("no limit reading shows dashes", "--", OverlayForm.Percent(null));
            Equal("plugin reading wins over tokens", "18%", OverlayForm.ContextText(m));
            Equal("session tokens ignore the subagent's", 5000L, m.ContextTokens);
            Equal("no plugin reading shows tokens", "~12万", OverlayForm.ContextText(new Metrics { ContextTokens = 120000 }));
            Equal("nothing known shows dashes", "--", OverlayForm.ContextText(new Metrics()));
            Equal("rate is output over response time", 10.0, Math.Round(m.Rate, 1));

            File.AppendAllText(Path.Combine(subagents, "agent-1.jsonl"), Assistant("m3", now.AddSeconds(6), 1, 0, 0, 1) + "\n");
            Equal("appended records are picked up once", 31L, collector.Poll().Today.Output);
            Equal("repeat poll adds nothing", 31L, collector.Poll().Today.Output);

            Equal("under ten thousand in full", "9999", OverlayForm.Tokens(9999));
            Equal("ten thousands", "1万", OverlayForm.Tokens(10000));
            Equal("hundred millions", "1.50亿", OverlayForm.Tokens(150000000));
        }
        finally { Directory.Delete(root, true); }

        Console.WriteLine(_failures == 0 ? "All tests passed." : _failures + " test(s) failed.");
        return _failures == 0 ? 0 : 1;
    }
}
