using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AIUsageTracker
{
    // Claude Code 공식 statusLine 기능으로 전달되는 JSON을 받아 rate_limits만 로컬 파일에 저장한다.
    static class ClaudeStatusLine
    {
        public static int Run()
        {
            L.Load(new Settings());
            string input = "";
            try
            {
                using (var stdin = Console.OpenStandardInput())
                using (var reader = new StreamReader(stdin, new UTF8Encoding(false)))
                    input = reader.ReadToEnd();
            }
            catch { }

            string line = "";
            try
            {
                var root = Json.Parse(input);
                string model = Json.Str(root, "model", "display_name") ?? "Claude";
                var parts = new List<string>();
                parts.Add(model);

                var limits = Json.Obj(root, "rate_limits");
                if (limits != null)
                {
                    SaveSnapshot(model, limits);
                    AddPart(parts, "5h", Json.Obj(limits, "five_hour"));
                    AddPart(parts, "7d", Json.Obj(limits, "seven_day"));
                }
                double? ctx = Json.Num(root, "context_window", "used_percentage");
                if (ctx.HasValue) parts.Add("ctx " + Math.Round(ctx.Value) + "%");
                line = string.Join(" · ", parts);
            }
            catch { }

            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(line);
                using (var stdout = Console.OpenStandardOutput())
                    stdout.Write(bytes, 0, bytes.Length);
            }
            catch { }
            return 0;
        }

        static void AddPart(List<string> parts, string label, IDictionary<string, object> window)
        {
            double? pct = Json.Num(window, "used_percentage");
            if (!pct.HasValue) return;
            string text = label + " " + Math.Round(pct.Value) + "%";
            double? resets = Json.Num(window, "resets_at");
            if (resets.HasValue) text += " (" + Fmt.Until(Fmt.FromUnix(resets.Value)) + ")";
            parts.Add(text);
        }

        static void SaveSnapshot(string model, IDictionary<string, object> limits)
        {
            try
            {
                var snap = new Dictionary<string, object>();
                snap["captured_at"] = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                snap["model"] = model;
                snap["rate_limits"] = limits;
                Paths.WriteAtomic(Paths.ClaudeSnapshot, Json.Stringify(snap, true));
            }
            catch { }
        }
    }

    enum StatusLineState { NotInstalled, Installed, OtherPath, Foreign }

    // ~/.claude/settings.json 의 statusLine 항목 등록/해제
    static class ClaudeIntegration
    {
        static string SettingsPath
        {
            get { return Path.Combine(Paths.ClaudeConfigDir, "settings.json"); }
        }

        static string PreviousPath
        {
            get { return Path.Combine(Paths.DataDir, "previous_statusline.json"); }
        }

        public static string OurCommand
        {
            get { return "\"" + Paths.ExePath.Replace('\\', '/') + "\" --statusline"; }
        }

        static IDictionary<string, object> Load()
        {
            if (!File.Exists(SettingsPath)) return new Dictionary<string, object>();
            var root = Json.Parse(File.ReadAllText(SettingsPath, Encoding.UTF8)) as IDictionary<string, object>;
            if (root == null) throw new InvalidDataException("settings.json 형식을 읽을 수 없습니다.");
            return root;
        }

        static void Save(IDictionary<string, object> root)
        {
            Directory.CreateDirectory(Paths.ClaudeConfigDir);
            if (File.Exists(SettingsPath)) File.Copy(SettingsPath, SettingsPath + ".aiusagetracker.bak", true);
            Paths.WriteAtomic(SettingsPath, Json.Stringify(root, true) + "\n");
        }

        public static StatusLineState Check()
        {
            try
            {
                string cmd = Json.Str(Load(), "statusLine", "command");
                if (string.IsNullOrEmpty(cmd)) return StatusLineState.NotInstalled;
                if (cmd.IndexOf("--statusline", StringComparison.OrdinalIgnoreCase) < 0) return StatusLineState.Foreign;
                if (string.Equals(cmd.Trim(), OurCommand, StringComparison.OrdinalIgnoreCase)) return StatusLineState.Installed;
                // 이전 이름(UsageWidget)으로 등록된 것도 이 앱의 것으로 본다
                if (cmd.IndexOf("AIUsageTracker", StringComparison.OrdinalIgnoreCase) >= 0
                    || cmd.IndexOf("UsageWidget", StringComparison.OrdinalIgnoreCase) >= 0) return StatusLineState.OtherPath;
                return StatusLineState.Foreign;
            }
            catch
            {
                return StatusLineState.NotInstalled;
            }
        }

        // 다른 statusLine이 있으면 force 없이는 덮어쓰지 않는다. 기존 값은 해제 시 복원용으로 보관.
        public static string Install(bool force)
        {
            var root = Load();
            var state = Check();
            if (state == StatusLineState.Foreign)
            {
                if (!force) return "foreign";
                File.WriteAllText(PreviousPath, Json.Stringify(root["statusLine"], true), new UTF8Encoding(false));
            }
            var entry = new Dictionary<string, object>();
            entry["type"] = "command";
            entry["command"] = OurCommand;
            root["statusLine"] = entry;
            Save(root);
            return "ok";
        }

        public static void Uninstall()
        {
            var root = Load();
            var state = Check();
            if (state != StatusLineState.Installed && state != StatusLineState.OtherPath) return;
            object previous = null;
            if (File.Exists(PreviousPath)) previous = Json.Parse(File.ReadAllText(PreviousPath, Encoding.UTF8));
            if (previous != null) root["statusLine"] = previous;
            else root.Remove("statusLine");
            Save(root);
            if (File.Exists(PreviousPath)) File.Delete(PreviousPath);
        }
    }

    // ~/.claude/projects/**/*.jsonl 대화 기록에서 토큰 사용량을 증분 집계
    class ClaudeTokenScanner
    {
        public class DayTotals
        {
            public long Input, Output, CacheCreate, CacheRead;
            public int Requests;
            public long Total { get { return Input + Output + CacheCreate + CacheRead; } }
        }

        class Record
        {
            public DateTime Day;
            public long Input, Output, CacheCreate, CacheRead;
        }

        readonly Dictionary<string, long> offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Record> records = new Dictionary<string, Record>();
        readonly Dictionary<DateTime, DayTotals> days = new Dictionary<DateTime, DayTotals>();

        public DayTotals Day(DateTime day)
        {
            DayTotals t;
            return days.TryGetValue(day.Date, out t) ? t : new DayTotals();
        }

        public void Scan()
        {
            string root = Path.Combine(Paths.ClaudeConfigDir, "projects");
            if (!Directory.Exists(root)) return;
            DateTime cutoff = DateTime.Now.Date.AddDays(-1);
            foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTime < cutoff) continue;
                    long offset;
                    offsets.TryGetValue(file, out offset);
                    if (info.Length < offset) offset = 0;
                    if (info.Length == offset) continue;
                    offsets[file] = ReadFrom(file, offset);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            Prune();
        }

        long ReadFrom(string file, long offset)
        {
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Seek(offset, SeekOrigin.Begin);
                var line = new MemoryStream();
                var buffer = new byte[1 << 20];
                long pos = offset, consumed = offset;
                int n;
                while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    int start = 0;
                    for (int i = 0; i < n; i++)
                    {
                        if (buffer[i] != (byte)'\n') continue;
                        line.Write(buffer, start, i - start);
                        ProcessLine(Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length));
                        line.SetLength(0);
                        start = i + 1;
                        consumed = pos + i + 1;
                    }
                    line.Write(buffer, start, n - start);
                    pos += n;
                }
                return consumed;   // 아직 끝나지 않은 마지막 줄은 다음 스캔에서 다시 읽는다
            }
        }

        void ProcessLine(string line)
        {
            if (line.IndexOf("\"assistant\"", StringComparison.Ordinal) < 0) return;
            if (line.IndexOf("\"usage\"", StringComparison.Ordinal) < 0) return;
            object o;
            try { o = Json.Parse(line); }
            catch { return; }
            if (Json.Str(o, "type") != "assistant") return;
            var usage = Json.Obj(o, "message", "usage");
            if (usage == null) return;
            if (Json.Str(o, "message", "model") == "<synthetic>") return;

            DateTime ts;
            if (!DateTime.TryParse(Json.Str(o, "timestamp"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out ts)) return;

            var rec = new Record();
            rec.Day = ts.ToLocalTime().Date;
            rec.Input = (long)(Json.Num(usage, "input_tokens") ?? 0);
            rec.Output = (long)(Json.Num(usage, "output_tokens") ?? 0);
            rec.CacheCreate = (long)(Json.Num(usage, "cache_creation_input_tokens") ?? 0);
            rec.CacheRead = (long)(Json.Num(usage, "cache_read_input_tokens") ?? 0);

            // 같은 응답이 콘텐츠 블록마다 여러 줄로 기록되므로 message id + requestId로 중복 제거
            string id = (Json.Str(o, "message", "id") ?? "") + ":" + (Json.Str(o, "requestId") ?? "");
            Record old;
            if (id != ":" && records.TryGetValue(id, out old))
            {
                if (rec.Output <= old.Output && rec.Input + rec.CacheCreate + rec.CacheRead <= old.Input + old.CacheCreate + old.CacheRead) return;
                Add(old, -1);
                Add(rec, 0);
                records[id] = rec;
                return;
            }
            if (id != ":") records[id] = rec;
            Add(rec, 1);
        }

        void Add(Record r, int requestDelta)
        {
            DayTotals t;
            if (!days.TryGetValue(r.Day, out t)) days[r.Day] = t = new DayTotals();
            long sign = requestDelta < 0 ? -1 : 1;
            t.Input += sign * r.Input;
            t.Output += sign * r.Output;
            t.CacheCreate += sign * r.CacheCreate;
            t.CacheRead += sign * r.CacheRead;
            if (requestDelta > 0) t.Requests++;
        }

        void Prune()
        {
            DateTime keep = DateTime.Now.Date.AddDays(-7);
            var oldDays = new List<DateTime>();
            foreach (var d in days.Keys) if (d < keep) oldDays.Add(d);
            foreach (var d in oldDays) days.Remove(d);
            var oldIds = new List<string>();
            foreach (var kv in records) if (kv.Value.Day < keep) oldIds.Add(kv.Key);
            foreach (var id in oldIds) records.Remove(id);
        }
    }

    // 한도 %는 두 경로 중 더 최근 값을 쓴다:
    //  1) claude -p "/usage" : 공식 CLI의 로컬 명령. 모델 호출 없이 5분마다 조회
    //  2) statusLine 스냅샷  : 터미널 세션이 돌 때 실시간으로 갱신
    class ClaudeProvider : Provider
    {
        // "{제목}: N% used · resets {날짜}" 형태의 줄을 모두 읽는다. 플랜에 따라 나오는 줄이 다르다:
        //   Current session / Current week (all models)        : 모든 구독
        //   Current week (Sonnet only)                         : Max, Team
        //   Current week (<모델 이름>)                          : 모델 전용 주간 한도가 있는 플랜
        // 아직 모르는 제목이 나와도 그대로 표시한다.
        static readonly Regex UsageLine = new Regex(
            @"^\s*([^:\n]{2,80}?)\s*:\s*(\d+(?:\.\d+)?)%\s*used(?:.*?\bresets\s+(.+?))?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        static readonly Regex WeekScope = new Regex(@"^Current week\s*\(([^)]*)\)$", RegexOptions.IgnoreCase);

        readonly ClaudeTokenScanner scanner = new ClaudeTokenScanner();
        DateTime usageLastRun = DateTime.MinValue;
        bool usageDisabled;
        List<CachedWindow> usageWindows;
        DateTime? usageAt;
        Func<string> usageError;
        bool needsLogin;
        string plan;
        string cliExe;
        bool cliIsScript;

        public ClaudeProvider()
        {
            Name = "Claude Code";
            Accent = Color.FromArgb(217, 119, 87);
            IntervalSec = 20;
        }

        public override ServiceStatus Fetch()
        {
            var s = NewStatus();
            if (Due(ref usageLastRun, LimitIntervalSec) && !usageDisabled) RunUsageCommand();

            if (needsLogin)
            {
                // 로그아웃 상태면 예전 계정의 statusLine 스냅샷도 쓰지 않는다
                s.NeedLogin("claude auth login", cliExe, cliIsScript, "auth login");
            }
            else
            {
                DateTime? snapAt;
                string snapModel;
                var snapWindows = ReadSnapshot(out snapAt, out snapModel);
                s.Badge = plan ?? snapModel;

                // /usage 결과를 바탕으로 하고, statusLine 값이 더 최신이면 같은 한도(5시간/주간)만 덮어쓴다.
                // statusLine에는 모델 전용 한도가 없으므로 그 줄들은 /usage 값을 그대로 둔다.
                var rows = usageWindows != null ? new List<CachedWindow>(usageWindows) : new List<CachedWindow>();
                s.UpdatedAt = usageWindows != null ? usageAt : null;
                if (snapWindows != null && (usageWindows == null || snapAt > usageAt))
                {
                    foreach (var sw in snapWindows)
                    {
                        int i = rows.FindIndex(r => r.Key == sw.Key);
                        if (i >= 0) rows[i] = sw;
                        else rows.Add(sw);
                    }
                    s.UpdatedAt = snapAt;
                }
                foreach (var w in rows) s.Windows.Add(w.ToWindow());
                if (usageError != null) s.Lines.Add(L.F("한도 %: {0}", usageError()));
            }

            try
            {
                scanner.Scan();
                var today = scanner.Day(DateTime.Now);
                s.Lines.Add(L.F("오늘 토큰 {0} · 출력 {1} · 요청 {2}회", Fmt.Tokens(today.Total), Fmt.Tokens(today.Output), today.Requests));
                if (today.Requests > 0) s.Active = true;
            }
            catch (Exception ex)
            {
                s.Error = L.F("기록 읽기 실패: {0}", ex.Message);
            }
            return s;
        }

        void RunUsageCommand()
        {
            bool isScript;
            string exe = Cli.Find("claude", new[]
            {
                Path.Combine(Paths.Home, @".local\bin\claude.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"npm\claude.cmd"),
            }, out isScript);
            needsLogin = false;
            cliExe = exe;
            cliIsScript = isScript;
            if (exe == null)
            {
                usageWindows = null;
                usageError = () => L.F("{0} CLI를 찾을 수 없음", "claude");
                return;
            }

            try
            {
                // 공식 auth status 로 로그인 여부와 플랜을 먼저 확인한다
                string stderr;
                var auth = Cli.ParseJson(Cli.Run(exe, isScript, "auth status --json", 30, out stderr));
                object loggedIn = Json.Get(auth, "loggedIn");
                if (loggedIn is bool && !(bool)loggedIn)
                {
                    needsLogin = true;
                    usageWindows = null;
                    usageError = null;
                    plan = null;
                    return;
                }
                string subscription = Json.Str(auth, "subscriptionType");
                plan = Fmt.PlanName(subscription);
                string method = Json.Str(auth, "authMethod") ?? "";
                if (method.IndexOf("api", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    usageWindows = null;
                    usageError = () => L.T("API 키 사용 중: 구독 한도 없음");
                    return;
                }

                var root = Cli.ParseJson(Cli.Run(exe, isScript, "-p \"/usage\" --output-format json --no-session-persistence", 30, out stderr));
                if (root == null)
                {
                    usageError = () => L.T("/usage 결과를 읽을 수 없음");
                    return;
                }
                // 로컬 명령이 아니라 모델 대화로 처리됐다면(구버전 등) 사용량을 소모하지 않도록 이후 호출을 멈춘다
                double? turns = Json.Num(root, "num_turns");
                if (Json.Str(root, "local_command") != "usage" || (turns.HasValue && turns.Value > 0))
                {
                    usageDisabled = true;
                    usageError = () => L.T("이 버전은 /usage 비대화형 조회를 지원하지 않음");
                    return;
                }

                var windows = new List<CachedWindow>();
                string text = (Json.Str(root, "result") ?? "").Replace("\r", "");
                foreach (Match m in UsageLine.Matches(text))
                {
                    double pct = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    windows.Add(Row(m.Groups[1].Value.Trim(), pct, ParseReset(m.Groups[3].Value)));
                }
                if (windows.Count == 0)
                {
                    usageWindows = null;
                    usageError = () => L.T("구독 한도 정보 없음");
                    return;
                }
                usageWindows = windows;
                usageAt = DateTime.Now;
                usageError = null;
            }
            catch (TimeoutException)
            {
                usageError = () => L.T("/usage 응답 시간 초과");
            }
            catch (Exception ex)
            {
                usageError = () => L.F("/usage 실패: {0}", ex.Message);
            }
        }

        // 키는 statusLine의 five_hour / seven_day 와 맞춘다
        static CachedWindow Row(string title, double pct, DateTime? resets)
        {
            if (title.Equals("Current session", StringComparison.OrdinalIgnoreCase))
                return new CachedWindow("five_hour", null, () => L.T("5시간"), pct, resets);
            if (title.Equals("Current week", StringComparison.OrdinalIgnoreCase))
                return new CachedWindow("seven_day", null, () => L.T("주간"), pct, resets);
            var scope = WeekScope.Match(title);
            if (scope.Success)
            {
                string name = scope.Groups[1].Value.Trim();
                if (name.Equals("all models", StringComparison.OrdinalIgnoreCase))
                    return new CachedWindow("seven_day", null, () => L.T("주간"), pct, resets);
                // "Sonnet only" → "Sonnet", "Opus 4.6" → 그대로
                name = Regex.Replace(name, @"\s+only$", "", RegexOptions.IgnoreCase);
                return new CachedWindow("week:" + name, null, () => L.F("주간 {0}", name), pct, resets);
            }
            return new CachedWindow("other:" + title, null, () => title, pct, resets);
        }

        // "Oct 29, 9:30am (Asia/Seoul)", "Nov 4, 11pm", "9:30am" 형태. 시간대 표기는 PC 로컬 시간대와 같다.
        static DateTime? ParseReset(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            text = Regex.Replace(text, @"\s*\([^)]*\)\s*$", "").Trim();
            text = Regex.Replace(text, @"(am|pm)\b", m => m.Value.ToUpperInvariant(), RegexOptions.IgnoreCase);
            var formats = new[]
            {
                "MMM d, h:mmtt", "MMM d, htt", "MMM d h:mmtt", "MMM d htt", "MMM d",
                "MMMM d, h:mmtt", "MMMM d, htt", "MMMM d",
                "MMM d, yyyy, h:mmtt", "MMM d, yyyy h:mmtt", "MMM d, yyyy",
                "h:mmtt", "htt",
            };
            DateTime dt;
            if (!DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out dt)) return null;
            bool hasDate = Regex.IsMatch(text, @"^[A-Za-z]{3}");
            bool hasYear = Regex.IsMatch(text, @"\d{4}");
            if (!hasDate)
            {
                dt = DateTime.Today + dt.TimeOfDay;
                if (dt < DateTime.Now) dt = dt.AddDays(1);
            }
            else if (!hasYear && dt < DateTime.Now.AddDays(-1))
            {
                dt = dt.AddYears(1);
            }
            return dt;
        }

        static List<CachedWindow> ReadSnapshot(out DateTime? capturedAt, out string model)
        {
            capturedAt = null;
            model = null;
            var state = ClaudeIntegration.Check();
            if (state != StatusLineState.Installed && state != StatusLineState.OtherPath) return null;

            object snap = null;
            try
            {
                if (File.Exists(Paths.ClaudeSnapshot))
                    snap = Json.Parse(File.ReadAllText(Paths.ClaudeSnapshot, Encoding.UTF8));
            }
            catch { }
            if (snap == null) return null;

            double? captured = Json.Num(snap, "captured_at");
            if (captured.HasValue) capturedAt = Fmt.FromUnix(captured.Value);
            model = Json.Str(snap, "model");
            var list = new List<CachedWindow>();
            AddWindow(list, "five_hour", () => L.T("5시간"), Json.Obj(snap, "rate_limits", "five_hour"));
            AddWindow(list, "seven_day", () => L.T("주간"), Json.Obj(snap, "rate_limits", "seven_day"));
            AddWindow(list, "spend_limit", () => L.T("지출"), Json.Obj(snap, "rate_limits", "spend_limit"));
            return list;
        }

        static void AddWindow(List<CachedWindow> list, string key, Func<string> label, IDictionary<string, object> w)
        {
            if (w == null) return;
            double? resets = Json.Num(w, "resets_at");
            list.Add(new CachedWindow(key, null, label, Json.Num(w, "used_percentage"), resets.HasValue ? Fmt.FromUnix(resets.Value) : (DateTime?)null));
        }
    }
}
