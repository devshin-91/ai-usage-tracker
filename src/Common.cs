using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace AIUsageTracker
{
    // 한 개의 사용량 창(5시간, 주간 등)
    class UsageWindow
    {
        public string Group;  // 모델 전용 한도처럼 묶음이 있을 때 카드 안에 소제목으로 표시
        public string Label;
        public double? Percent;
        public DateTime? ResetsAt;
        public string Note;   // 리셋 열에 시간 대신 표시할 문구

        // 리셋 시각이 지났으면 예전 %는 의미가 없으므로 비운다
        public static UsageWindow Make(string label, double? percent, DateTime? resetsAt)
        {
            var w = new UsageWindow();
            w.Label = label;
            w.Percent = percent;
            w.ResetsAt = resetsAt;
            if (resetsAt.HasValue && resetsAt.Value <= DateTime.Now)
            {
                w.Percent = null;
                w.Note = L.T("리셋됨");
            }
            return w;
        }
    }

    // CLI 조회 결과를 다음 조회 때까지 보관한다. 이름은 그릴 때 현재 언어로 만든다.
    class CachedWindow
    {
        public string Key;            // 같은 한도를 다른 출처(statusLine)와 맞춰 보기 위한 키
        public Func<string> Group;
        public Func<string> Label;
        public double? Percent;
        public DateTime? ResetsAt;

        public CachedWindow(string key, Func<string> group, Func<string> label, double? percent, DateTime? resetsAt)
        {
            Key = key;
            Group = group;
            Label = label;
            Percent = percent;
            ResetsAt = resetsAt;
        }

        public UsageWindow ToWindow()
        {
            var w = UsageWindow.Make(Label(), Percent, ResetsAt);
            w.Group = Group == null ? null : Group();
            return w;
        }
    }

    // 서비스 한 개의 카드에 그릴 내용
    class ServiceStatus
    {
        public string Name;
        public Color Accent;
        public bool Active;
        public string Badge;
        public DateTime? UpdatedAt;
        public List<UsageWindow> Windows = new List<UsageWindow>();
        public List<string> Lines = new List<string>();
        public string Error;
        public string LoginCommand;   // 로그아웃 상태일 때 사용자가 터미널에서 실행할 공식 로그인 명령
        public string LoginExe;       // 위 명령을 실제로 실행할 CLI 경로와 인자
        public bool LoginIsScript;
        public string LoginArgs = "";

        public void NeedLogin(string display, string exe, bool isScript, string args)
        {
            LoginCommand = display;
            LoginExe = exe;
            LoginIsScript = isScript;
            LoginArgs = args;
        }
    }

    abstract class Provider
    {
        public string Name;
        public Color Accent;
        public int IntervalSec = 60;
        public DateTime NextDue = DateTime.MinValue;
        public volatile bool Busy;
        public volatile bool ForceRefresh;   // 수동 새로고침: 느린 CLI 조회도 바로 다시 실행
        public bool Enabled = true;
        public ServiceStatus Last;
        public DateTime LoginWatchUntil = DateTime.MinValue;   // 로그인 창을 연 뒤 이 시각까지 자주 다시 확인

        // 설정의 '한도 조회 간격'. CLI를 실행하는 조회는 모두 이 간격을 따른다
        public static int LimitIntervalSec = 300;

        // 느린 CLI 조회는 IntervalSec보다 드물게 돌리기 위한 판단
        protected bool Due(ref DateTime lastRun, int seconds)
        {
            bool force = ForceRefresh;
            ForceRefresh = false;
            if (!force && (DateTime.Now - lastRun).TotalSeconds < seconds) return false;
            lastRun = DateTime.Now;
            return true;
        }

        public abstract ServiceStatus Fetch();

        protected ServiceStatus NewStatus()
        {
            var s = new ServiceStatus();
            s.Name = Name;
            s.Accent = Accent;
            return s;
        }
    }

    static class Json
    {
        static JavaScriptSerializer Serializer()
        {
            var s = new JavaScriptSerializer();
            s.MaxJsonLength = int.MaxValue;
            s.RecursionLimit = 512;
            return s;
        }

        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            return Serializer().DeserializeObject(text);
        }

        public static object Get(object o, params string[] path)
        {
            foreach (var key in path)
            {
                var d = o as IDictionary<string, object>;
                if (d == null) return null;
                object v;
                if (!d.TryGetValue(key, out v)) return null;
                o = v;
            }
            return o;
        }

        public static IDictionary<string, object> Obj(object o, params string[] path)
        {
            return Get(o, path) as IDictionary<string, object>;
        }

        public static string Str(object o, params string[] path)
        {
            return Get(o, path) as string;
        }

        public static double? Num(object o, params string[] path)
        {
            o = Get(o, path);
            if (o == null) return null;
            if (o is int) return (int)o;
            if (o is long) return (long)o;
            if (o is decimal) return (double)(decimal)o;
            if (o is double) return (double)o;
            var s = o as string;
            double d;
            if (s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return null;
        }

        public static string Stringify(object o, bool pretty)
        {
            var sb = new StringBuilder();
            Write(sb, o, 0, pretty);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object o, int depth, bool pretty)
        {
            if (o == null) { sb.Append("null"); return; }
            if (o is string) { WriteString(sb, (string)o); return; }
            if (o is bool) { sb.Append((bool)o ? "true" : "false"); return; }
            if (o is int || o is long || o is decimal || o is double || o is float)
            {
                sb.Append(Convert.ToString(o, CultureInfo.InvariantCulture));
                return;
            }
            var dict = o as IDictionary<string, object>;
            if (dict != null)
            {
                if (dict.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                bool first = true;
                foreach (var kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(sb, depth + 1, pretty);
                    WriteString(sb, kv.Key);
                    sb.Append(pretty ? ": " : ":");
                    Write(sb, kv.Value, depth + 1, pretty);
                }
                NewLine(sb, depth, pretty);
                sb.Append('}');
                return;
            }
            var list = o as IEnumerable;
            if (list != null)
            {
                var items = new List<object>();
                foreach (var item in list) items.Add(item);
                if (items.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[');
                for (int i = 0; i < items.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    NewLine(sb, depth + 1, pretty);
                    Write(sb, items[i], depth + 1, pretty);
                }
                NewLine(sb, depth, pretty);
                sb.Append(']');
                return;
            }
            WriteString(sb, o.ToString());
        }

        static void NewLine(StringBuilder sb, int depth, bool pretty)
        {
            if (!pretty) return;
            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }

    // 사용자가 설치한 공식 CLI를 셸 없이 직접 실행한다 (Git Bash처럼 "/usage" 인자를 경로로 바꾸는 일이 없음)
    static class Cli
    {
        // AI_USAGE_TRACKER_CLAUDE_PATH 같은 환경 변수로 CLI 위치를 직접 지정할 수 있다 (비표준 설치, 테스트용)
        public static string Find(string baseName, IEnumerable<string> candidates, out bool isScript)
        {
            isScript = false;
            string custom = Environment.GetEnvironmentVariable("AI_USAGE_TRACKER_" + baseName.ToUpperInvariant() + "_PATH");
            if (!string.IsNullOrEmpty(custom) && File.Exists(custom))
            {
                isScript = custom.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || custom.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
                return custom;
            }
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var ext in new[] { ".exe", ".cmd" })
            {
                foreach (var dir in path.Split(';'))
                {
                    try
                    {
                        if (dir.Trim().Length == 0) continue;
                        string candidate = Path.Combine(dir.Trim(), baseName + ext);
                        if (!File.Exists(candidate)) continue;
                        isScript = ext == ".cmd";
                        return candidate;
                    }
                    catch (ArgumentException) { }
                }
            }
            return null;
        }

        public static ProcessStartInfo StartInfo(string exe, bool isScript, string args)
        {
            var psi = new ProcessStartInfo();
            if (isScript)
            {
                psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                psi.Arguments = "/d /s /c \"\"" + exe + "\" " + args + "\"";
            }
            else
            {
                psi.FileName = exe;
                psi.Arguments = args;
            }
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            return psi;
        }

        // 표준 입력을 바로 닫고 출력 전체를 받는다. 오류 메시지는 stderr로 오는 CLI가 많아 함께 돌려준다.
        public static string Run(string exe, bool isScript, string args, int timeoutSec, out string stderr)
        {
            using (var p = Process.Start(StartInfo(exe, isScript, args)))
            {
                p.StandardInput.Close();
                var stdout = p.StandardOutput.ReadToEndAsync();
                var errors = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutSec * 1000))
                {
                    KillTree(p);
                    throw new TimeoutException();
                }
                stdout.Wait(5000);
                errors.Wait(5000);
                stderr = errors.IsCompleted ? errors.Result : "";
                return stdout.Result;
            }
        }

        // 로그아웃 상태에서 CLI들이 내는 문구
        public static bool LooksLoggedOut(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var key in new[] { "not logged in", "not signed in", "authentication required", "please sign in", "please log in", "log in, then retry", "login required" })
                if (text.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public static void KillTree(Process p)
        {
            try
            {
                var psi = new ProcessStartInfo("taskkill", "/T /F /PID " + p.Id);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                using (var k = Process.Start(psi)) k.WaitForExit(3000);
            }
            catch
            {
                try { p.Kill(); } catch { }
            }
        }

        // 출력 앞뒤에 다른 줄이 섞여도 JSON 객체만 골라 읽는다
        public static object ParseJson(string output)
        {
            if (string.IsNullOrEmpty(output)) return null;
            try { return Json.Parse(output); }
            catch { }
            int start = output.IndexOf('{'), end = output.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            try { return Json.Parse(output.Substring(start, end - start + 1)); }
            catch { return null; }
        }
    }

    static class Paths
    {
        static string dataDir;

        public static string ExePath
        {
            get { return Assembly.GetExecutingAssembly().Location; }
        }

        public static string ExeDir
        {
            get { return Path.GetDirectoryName(ExePath); }
        }

        public static string Home
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
        }

        // 포터블: exe 옆 data 폴더를 쓰고, 쓰기 불가하면 %LOCALAPPDATA%\AIUsageTracker
        public static string DataDir
        {
            get
            {
                if (dataDir != null) return dataDir;
                string local = Path.Combine(ExeDir, "data");
                try
                {
                    Directory.CreateDirectory(local);
                    string probe = Path.Combine(local, ".write-test");
                    File.WriteAllText(probe, "");
                    File.Delete(probe);
                    dataDir = local;
                }
                catch
                {
                    dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIUsageTracker");
                    Directory.CreateDirectory(dataDir);
                }
                return dataDir;
            }
        }

        public static string ClaudeConfigDir
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                if (!string.IsNullOrEmpty(env)) return env;
                return Path.Combine(Home, ".claude");
            }
        }

        public static string ClaudeSnapshot
        {
            get { return Path.Combine(DataDir, "claude_status.json"); }
        }

        public static void WriteAtomic(string path, string content)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
    }

    class Settings
    {
        readonly Dictionary<string, string> values = new Dictionary<string, string>();
        readonly string path;

        public Settings()
        {
            path = Path.Combine(Paths.DataDir, "settings.ini");
            try
            {
                if (!File.Exists(path)) return;
                foreach (var line in File.ReadAllLines(path))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) values[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
            }
            catch { }
        }

        public int GetInt(string key, int def)
        {
            string v;
            int n;
            if (values.TryGetValue(key, out v) && int.TryParse(v, out n)) return n;
            return def;
        }

        public string GetString(string key, string def)
        {
            string v;
            return values.TryGetValue(key, out v) && v.Length > 0 ? v : def;
        }

        public bool GetBool(string key, bool def)
        {
            return GetInt(key, def ? 1 : 0) != 0;
        }

        public void Set(string key, object value)
        {
            values[key] = value is bool ? ((bool)value ? "1" : "0") : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public void Save()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var kv in values) sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    static class Fmt
    {
        public static DateTime FromUnix(double seconds)
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds).ToLocalTime();
        }

        public static string Until(DateTime when)
        {
            var d = when - DateTime.Now;
            if (d.TotalSeconds <= 0) return L.T("곧 리셋");
            if (d.TotalMinutes < 60) return L.F("{0}분", Math.Max(1, (int)d.TotalMinutes));
            if (d.TotalHours < 24) return L.F("{0}시간 {1}분", (int)d.TotalHours, d.Minutes);
            return L.F("{0}일 {1}시간", (int)d.TotalDays, d.Hours);
        }

        public static string Ago(DateTime when)
        {
            var d = DateTime.Now - when;
            if (d.TotalSeconds < 60) return L.T("방금");
            if (d.TotalMinutes < 60) return L.F("{0}분 전", (int)d.TotalMinutes);
            if (d.TotalHours < 24) return L.F("{0}시간 전", (int)d.TotalHours);
            return L.F("{0}일 전", (int)d.TotalDays);
        }

        public static string Tokens(long n)
        {
            if (n >= 1000000000) return (n / 1e9).ToString("0.##", CultureInfo.InvariantCulture) + "B";
            if (n >= 1000000) return (n / 1e6).ToString("0.#", CultureInfo.InvariantCulture) + "M";
            if (n >= 1000) return (n / 1e3).ToString("0.#", CultureInfo.InvariantCulture) + "K";
            return n.ToString(CultureInfo.InvariantCulture);
        }

        // "prolite" → "Pro Lite", "edu_plus" → "Edu Plus"
        public static string PlanName(string plan)
        {
            if (string.IsNullOrEmpty(plan) || plan == "unknown") return null;
            plan = plan.Replace("prolite", "pro_lite").Replace("promax", "pro_max").Replace('_', ' ');
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(plan);
        }

        public static string WindowLabel(double minutes)
        {
            if (minutes == 10080) return L.T("주간");
            if (minutes == 1440) return L.T("일간");
            if (minutes < 1440) return L.F("{0}시간", Math.Round(minutes / 60));
            return L.F("{0}일", Math.Round(minutes / 1440));
        }
    }
}
