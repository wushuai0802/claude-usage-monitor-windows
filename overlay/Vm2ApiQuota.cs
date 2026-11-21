using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

// Opt-in account usage for Claude Desktop sessions routed through CC Switch.
internal sealed class Vm2ApiQuota
{
    internal sealed class Reading
    {
        internal Limit FiveHour, SevenDay;
        internal DateTime At;
    }

    private readonly Uri _origin;
    private readonly string _database, _configLibrary;
    private string _session, _pendingSession;
    private Task<Reading> _pending;
    private Reading _last;
    private DateTime _next;

    internal Vm2ApiQuota(string state, string profile, string localAppData)
    {
        _origin = BaseUrl(JsonData.Get(ReadObject(Path.Combine(state, "vm2api.json")), "baseUrl"));
        _database = Path.Combine(profile, ".cc-switch", "cc-switch.db");
        _configLibrary = Path.Combine(localAppData, "Claude-3p", "configLibrary");
    }

    internal void Apply(Metrics metrics)
    {
        if (_origin == null) return;
        Guid sessionGuid;
        var session = metrics.ThirdParty && Guid.TryParseExact(metrics.SessionId, "D", out sessionGuid) ? metrics.SessionId : null;
        if (!string.Equals(_session, session, StringComparison.Ordinal)) {
            _session = session; _last = null; _next = DateTime.MinValue;
        }
        if (_pending != null && _pending.IsCompleted) {
            try {
                var reading = _pending.GetAwaiter().GetResult();
                if (_pendingSession == session) _last = reading;
            } catch (Exception ex) {
                if (_pendingSession == session) _last = null;
                Program.Note("vm2api: " + ex.GetType().Name);
            } finally {
                _pending = null;
            }
        }
        if (session == null) return;
        if (_pending == null && metrics.At >= _next) {
            _pendingSession = session;
            _next = metrics.At.AddMinutes(1);
            _pending = Task.Run(() => Fetch(session));
        }
        if (_last == null || metrics.At >= _last.At.AddSeconds(90)) return;

        metrics.LimitsAt = _last.At; metrics.LimitSource = "vm2api";
        metrics.FiveHour = CurrentWindow(_last.FiveHour, metrics.At);
        metrics.SevenDay = CurrentWindow(_last.SevenDay, metrics.At);
    }

    private Reading Fetch(string session)
    {
        try {
            var port = LocalProxyPort();
            if (port == null || !File.Exists(_database)) return null;
            var settings = CcSwitchRoute.Read(_database, session, port.Value);
            var env = JsonData.Map(JsonData.Get(settings, "env"));
            var upstream = BaseUrl(JsonData.Get(env, "ANTHROPIC_BASE_URL"));
            if (upstream == null || !_origin.Equals(upstream)) return null;
            var key = (JsonData.Get(env, "ANTHROPIC_AUTH_TOKEN") ?? JsonData.Get(env, "ANTHROPIC_API_KEY")) as string;
            if (string.IsNullOrEmpty(key) || !key.StartsWith("sk-vm-", StringComparison.Ordinal) || key.Length > 512) return null;

            var request = (HttpWebRequest)WebRequest.Create(new Uri(_origin, "/v1/usage?platform=claude"));
            request.Method = "GET";
            request.Proxy = null;
            request.AllowAutoRedirect = false;
            request.Timeout = 4000;
            request.ReadWriteTimeout = 4000;
            request.Accept = "application/json";
            request.Headers[HttpRequestHeader.Authorization] = "Bearer " + key;
            request.Headers["x-claude-code-session-id"] = session;
            using (var response = (HttpWebResponse)request.GetResponse()) {
                if (response.StatusCode != HttpStatusCode.OK) return null;
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8)) {
                    var buffer = new char[8193];
                    int size = reader.ReadBlock(buffer, 0, buffer.Length);
                    if (size == buffer.Length) return null;
                    return Parse(new string(buffer, 0, size), DateTime.Now);
                }
            }
        } catch (WebException ex) {
            if (ex.Response != null) ex.Response.Dispose();
        } catch (Exception ex) {
            if (!(ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidOperationException
                || ex is DllNotFoundException || ex is EntryPointNotFoundException)) throw;
        }
        return null;
    }

    private int? LocalProxyPort()
    {
        Guid id;
        var applied = JsonData.Get(ReadObject(Path.Combine(_configLibrary, "_meta.json")), "appliedId") as string;
        if (!Guid.TryParseExact(applied, "D", out id)) return null;
        var profile = ReadObject(Path.Combine(_configLibrary, id.ToString("D") + ".json"));
        Uri gateway;
        if (!Equals(JsonData.Get(profile, "inferenceProvider"), "gateway")
            || !Uri.TryCreate(JsonData.Get(profile, "inferenceGatewayBaseUrl") as string, UriKind.Absolute, out gateway)
            || (gateway.Scheme != Uri.UriSchemeHttp && gateway.Scheme != Uri.UriSchemeHttps)
            || !gateway.IsLoopback || gateway.AbsolutePath.TrimEnd('/') != "/claude-desktop") return null;
        return gateway.Port;
    }

    private static Dictionary<string, object> ReadObject(string path)
    {
        try {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= 65536 ? JsonData.Parse(File.ReadAllText(path)) : null;
        } catch (Exception ex) {
            if (!(ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidOperationException)) throw;
            return null;
        }
    }

    internal static Uri BaseUrl(object value)
    {
        Uri origin;
        if (!Uri.TryCreate(value as string, UriKind.Absolute, out origin) || origin.UserInfo.Length != 0
            || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0) return null;
        if (origin.Scheme == Uri.UriSchemeHttps || (origin.Scheme == Uri.UriSchemeHttp && origin.IsLoopback)) return origin;
        // Plain HTTP is supported only for a private IPv4 address or Tailnet.
        IPAddress address;
        if (origin.Scheme != Uri.UriSchemeHttp || !IPAddress.TryParse(origin.Host, out address)) return null;
        var ip = address.GetAddressBytes();
        if (ip.Length != 4) return null;
        return ip[0] == 10 || (ip[0] == 172 && ip[1] >= 16 && ip[1] <= 31) || (ip[0] == 192 && ip[1] == 168)
            || (ip[0] == 100 && ip[1] >= 64 && ip[1] <= 127) ? origin : null;
    }

    internal static Reading Parse(string payload, DateTime at)
    {
        var data = JsonData.Parse(payload);
        if (!Equals(JsonData.Get(data, "unit"), "percent_used") || !Equals(JsonData.Get(data, "platform"), "anthropic")) return null;
        var five = ReadWindow(JsonData.Get(data, "five_hour"));
        var seven = ReadWindow(JsonData.Get(data, "seven_day"));
        return five == null && seven == null ? null : new Reading { At = at, FiveHour = five, SevenDay = seven };
    }

    private static Limit ReadWindow(object value)
    {
        var window = JsonData.Map(value);
        var used = JsonData.Percent(JsonData.Get(window, "utilization"));
        if (used == null) return null;
        var reset = JsonData.Get(window, "resets_at");
        long seconds;
        DateTime? at = null;
        if (long.TryParse(Convert.ToString(reset, CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out seconds)) {
            if (seconds > 0 && seconds <= 253402300799L)
                at = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds).ToLocalTime();
        } else {
            at = JsonData.Time(reset);
        }
        return new Limit { Used = used, ResetsAt = at };
    }

    internal static Limit CurrentWindow(Limit limit, DateTime now)
    {
        if (limit == null || limit.ResetsAt == null || limit.ResetsAt > now) return limit;
        return new Limit { Used = null, ResetsAt = limit.ResetsAt, Expired = true };
    }
}

internal static class CcSwitchRoute
{
    // Require the last successful request for this transcript and the selected route to agree.
    private const string Query = @"
        SELECT p.settings_config
        FROM providers AS p
        WHERE p.app_type='claude-desktop' AND p.is_current=1
          AND EXISTS (SELECT 1 FROM proxy_config WHERE app_type='claude' AND listen_port=?2)
          AND p.id=(
            SELECT r.provider_id FROM proxy_request_logs AS r
            WHERE r.app_type=p.app_type AND r.session_id=?1 AND r.status_code BETWEEN 200 AND 299
            ORDER BY r.created_at DESC, r.rowid DESC LIMIT 1)";

    private const string Sqlite = "winsqlite3.dll";
    private const int OpenReadOnly = 1, Row = 100, Done = 101;
    [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
    [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, string sql, int bytes, out IntPtr statement, IntPtr tail);
    [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_text(IntPtr statement, int index, string text, int bytes, IntPtr destructor);
    [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_int(IntPtr statement, int index, int value);
    [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text16(IntPtr statement, int column);
    [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_bytes16(IntPtr statement, int column);
    [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);

    internal static Dictionary<string, object> Read(string path, string session, int port)
    {
        IntPtr db = IntPtr.Zero, statement = IntPtr.Zero;
        try {
            if (sqlite3_open_v2(Encoding.UTF8.GetBytes(path + "\0"), out db, OpenReadOnly, IntPtr.Zero) != 0) return null;
            if (sqlite3_busy_timeout(db, 400) != 0 || sqlite3_prepare_v2(db, Query, -1, out statement, IntPtr.Zero) != 0
                || sqlite3_bind_text(statement, 1, session, -1, new IntPtr(-1)) != 0 || sqlite3_bind_int(statement, 2, port) != 0
                || sqlite3_step(statement) != Row) return null;
            var value = sqlite3_column_text16(statement, 0);
            int bytes = sqlite3_column_bytes16(statement, 0);
            if (value == IntPtr.Zero || bytes <= 0 || bytes > 131072) return null;
            var text = Marshal.PtrToStringUni(value, bytes / 2);
            return sqlite3_step(statement) == Done ? JsonData.Parse(text) : null;
        } finally {
            if (statement != IntPtr.Zero) sqlite3_finalize(statement);
            if (db != IntPtr.Zero) sqlite3_close(db);
        }
    }
}
