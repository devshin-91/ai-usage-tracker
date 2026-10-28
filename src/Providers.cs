using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AIUsageTracker
{
    // 설치된 Codex CLI의 공식 app-server(JSON-RPC, stdio)에 account/rateLimits/read 를 요청한다.
    // 인증 토큰은 Codex가 직접 다루며 이 앱은 읽지 않는다.
    class CodexProvider : Provider
    {
        public CodexProvider()
        {
            Name = "Codex";
            Accent = Color.FromArgb(16, 163, 127);
            IntervalSec = LimitIntervalSec;
        }

        // npm 설치본은 node 래퍼를 거치지 않도록 안에 든 네이티브 codex.exe를 우선 사용
        static IEnumerable<string> NpmCandidates()
        {
            string npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai");
            foreach (var arch in new[] { "x86_64-pc-windows-msvc", "aarch64-pc-windows-msvc" })
                foreach (var pkg in new[] { @"codex\node_modules\@openai\codex-win32-x64", @"codex\node_modules\@openai\codex-win32-arm64", "codex-win32-x64", "codex-win32-arm64" })
                {
                    yield return Path.Combine(npm, pkg, "vendor", arch, "bin", "codex.exe");
                    yield return Path.Combine(npm, pkg, "vendor", arch, "codex", "codex.exe");
                }
        }

        public override ServiceStatus Fetch()
        {
            var s = NewStatus();
            ForceRefresh = false;
            bool isScript;
            string exe = Cli.Find("codex", NpmCandidates(), out isScript);
            if (exe == null)
            {
                s.Error = L.T("Codex CLI를 찾을 수 없음 (npm i -g @openai/codex)");
                return s;
            }

            var psi = Cli.StartInfo(exe, isScript, "app-server");
            if (exe.IndexOf("node_modules", StringComparison.OrdinalIgnoreCase) >= 0) psi.EnvironmentVariables["CODEX_MANAGED_BY_NPM"] = "1";

            Process p = null;
            try
            {
                p = Process.Start(psi);
                p.ErrorDataReceived += delegate { };
                p.BeginErrorReadLine();
                var input = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false));
                input.NewLine = "\n";
                input.AutoFlush = true;
                DateTime deadline = DateTime.Now.AddSeconds(30);

                input.WriteLine("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"ai_usage_tracker\",\"title\":\"AI Usage Tracker\",\"version\":\"1.0.0\"}}}");
                var init = WaitFor(p, 1, deadline);
                if (Json.Get(init, "error") != null)
                {
                    s.Error = L.F("초기화 실패: {0}", Json.Str(init, "error", "message"));
                    return s;
                }
                input.WriteLine("{\"method\":\"initialized\"}");

                // 로그아웃 상태면 account가 null로 온다
                input.WriteLine("{\"id\":2,\"method\":\"account/read\",\"params\":{}}");
                var account = WaitFor(p, 2, deadline);
                if (Json.Obj(account, "result") != null && Json.Get(account, "result", "account") == null)
                {
                    input.Close();
                    s.NeedLogin("codex login", exe, isScript, "login");
                    return s;
                }

                input.WriteLine("{\"id\":3,\"method\":\"account/rateLimits/read\"}");
                var resp = WaitFor(p, 3, deadline);
                input.Close();

                if (Json.Get(resp, "error") != null)
                {
                    string msg = Json.Str(resp, "error", "message") ?? L.T("알 수 없는 오류");
                    if (Cli.LooksLoggedOut(msg) || msg.IndexOf("auth", StringComparison.OrdinalIgnoreCase) >= 0) s.NeedLogin("codex login", exe, isScript, "login");
                    else s.Error = msg;
                    return s;
                }
                Parse(s, Json.Obj(resp, "result"));
                s.UpdatedAt = DateTime.Now;
            }
            catch (TimeoutException)
            {
                s.Error = L.T("Codex 응답 시간 초과");
            }
            catch (Exception ex)
            {
                s.Error = L.F("Codex 조회 실패: {0}", ex.Message);
            }
            finally
            {
                if (p != null)
                {
                    try
                    {
                        if (!p.WaitForExit(3000)) Cli.KillTree(p);
                    }
                    catch { }
                    p.Dispose();
                }
            }
            return s;
        }

        static object WaitFor(Process p, int id, DateTime deadline)
        {
            while (true)
            {
                var remaining = deadline - DateTime.Now;
                if (remaining.TotalMilliseconds <= 0) throw new TimeoutException();
                Task<string> read = p.StandardOutput.ReadLineAsync();
                if (!read.Wait(remaining)) throw new TimeoutException();
                if (read.Result == null) throw new IOException("app-server가 종료됨");
                object msg;
                try { msg = Json.Parse(read.Result); }
                catch { continue; }
                double? got = Json.Num(msg, "id");
                if (got.HasValue && (int)got.Value == id && Json.Get(msg, "method") == null) return msg;
            }
        }

        static void Parse(ServiceStatus s, IDictionary<string, object> result)
        {
            var limits = Json.Obj(result, "rateLimits");
            if (limits == null)
            {
                s.Error = L.T("사용량 정보 없음");
                return;
            }
            s.Badge = Fmt.PlanName(Json.Str(limits, "planType"));

            // 상위 플랜은 기본 버킷(codex) 외에 모델 전용 버킷이 rateLimitsByLimitId에 더 온다.
            // 기본 버킷을 먼저, 나머지는 이름을 소제목으로 붙여 아래에 표시한다.
            string mainId = Json.Str(limits, "limitId");
            var buckets = new List<IDictionary<string, object>>();
            buckets.Add(limits);
            var byId = Json.Obj(result, "rateLimitsByLimitId");
            if (byId != null)
                foreach (var kv in byId)
                {
                    var bucket = kv.Value as IDictionary<string, object>;
                    if (bucket != null && kv.Key != mainId) buckets.Add(bucket);
                }

            var reachedTypes = new List<string>();
            for (int i = 0; i < buckets.Count; i++)
            {
                string group = i == 0 ? null : BucketName(buckets[i]);
                AddWindow(s, group, Json.Obj(buckets[i], "primary"));
                AddWindow(s, group, Json.Obj(buckets[i], "secondary"));
                AddSpendLimit(s, group, Json.Obj(buckets[i], "individualLimit"));
                string reachedType = Json.Str(buckets[i], "rateLimitReachedType");
                if (!string.IsNullOrEmpty(reachedType)) reachedTypes.Add(group == null ? reachedType : group + ": " + reachedType);
            }
            foreach (var w in s.Windows)
                if (w.Percent.HasValue && w.Percent.Value > 0) s.Active = true;

            foreach (var reached in reachedTypes) s.Lines.Add(L.F("한도 도달 ({0})", reached));

            var credits = Json.Obj(limits, "credits");
            if (credits != null && Json.Get(credits, "hasCredits") is bool && (bool)Json.Get(credits, "hasCredits"))
            {
                if (Json.Get(credits, "unlimited") is bool && (bool)Json.Get(credits, "unlimited"))
                    s.Lines.Add(L.T("크레딧 무제한"));
                else
                {
                    double? balance = Json.Num(credits, "balance");
                    if (balance.HasValue) s.Lines.Add(L.F("크레딧 잔액 {0}", balance.Value.ToString("0.##", CultureInfo.InvariantCulture)));
                }
            }
            double? resets = Json.Num(result, "rateLimitResetCredits", "availableCount");
            if (resets.HasValue && resets.Value > 0) s.Lines.Add(L.F("한도 리셋권 {0}개 보유", resets.Value));
        }

        // limitName → 모델 slug → limitId 순으로 사람이 읽을 이름을 고른다
        static string BucketName(IDictionary<string, object> bucket)
        {
            string name = Json.Str(bucket, "limitName");
            if (!string.IsNullOrEmpty(name)) return name;
            string slug = Json.Str(bucket, "normalModelSlug");
            if (!string.IsNullOrEmpty(slug)) return slug;
            return Json.Str(bucket, "limitId") ?? "?";
        }

        static void AddWindow(ServiceStatus s, string group, IDictionary<string, object> w)
        {
            if (w == null) return;
            double? mins = Json.Num(w, "windowDurationMins");
            double? resets = Json.Num(w, "resetsAt");
            var win = UsageWindow.Make(mins.HasValue ? Fmt.WindowLabel(mins.Value) : L.T("한도"),
                Json.Num(w, "usedPercent"), resets.HasValue ? Fmt.FromUnix(resets.Value) : (DateTime?)null);
            win.Group = group;
            s.Windows.Add(win);
        }

        // 워크스페이스 관리자가 정한 개인 지출 한도 (남은 % 로 온다)
        static void AddSpendLimit(ServiceStatus s, string group, IDictionary<string, object> limit)
        {
            if (limit == null) return;
            double? remaining = Json.Num(limit, "remainingPercent");
            double? resets = Json.Num(limit, "resetsAt");
            var win = UsageWindow.Make(L.T("지출"), remaining.HasValue ? (double?)(100 - remaining.Value) : null,
                resets.HasValue ? Fmt.FromUnix(resets.Value) : (DateTime?)null);
            win.Group = group;
            s.Windows.Add(win);
        }
    }

    // 한도 %는 Antigravity CLI의 공식 print 모드 명령 agy -p "/usage" (에이전트 턴·할당량 소모 없음)로,
    // 오늘 활동은 로컬 대화 파일과 프로세스로 표시한다.
    class AntigravityProvider : Provider
    {
        DateTime usageLastRun = DateTime.MinValue;
        bool usageDisabled;
        List<CachedWindow> usageWindows;
        DateTime? usageAt;
        Func<string> usageError;
        bool needsLogin;
        string cliExe;
        bool cliIsScript;

        public volatile bool QueryCli;   // 설정 'agy 한도 조회' (기본 꺼짐)

        public AntigravityProvider()
        {
            Name = "Antigravity";
            Accent = Color.FromArgb(66, 133, 244);
            IntervalSec = 30;
        }

        void RunUsageCommand()
        {
            bool isScript;
            string exe = Cli.Find("agy", new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"agy\bin\agy.exe"),
            }, out isScript);
            needsLogin = false;
            cliExe = exe;
            cliIsScript = isScript;
            if (exe == null)
            {
                usageWindows = null;
                usageError = () => L.F("{0} CLI를 찾을 수 없음", "agy");
                return;
            }

            try
            {
                // print 모드에서 로그아웃 상태면 브라우저를 열지 않고 stderr에 "not logged in" 류의 오류를 낸다
                string stderr;
                string stdout = Cli.Run(exe, isScript, "-p \"/usage\" --output-format json", 30, out stderr);
                var root = Cli.ParseJson(stdout);
                if (Cli.LooksLoggedOut(stderr) || (root == null || Json.Str(root, "status") != "SUCCESS") && Cli.LooksLoggedOut(stdout))
                {
                    needsLogin = true;
                    usageWindows = null;
                    usageError = null;
                    return;
                }
                if (root == null)
                {
                    usageError = () => FirstLine(stderr) ?? L.T("/usage 결과를 읽을 수 없음");
                    return;
                }
                // 슬래시 명령으로 처리되지 않고 에이전트 턴이 돌았다면 할당량 보호를 위해 이후 호출을 멈춘다
                double? turns = Json.Num(root, "num_turns");
                if (Json.Str(root, "command", "name") != "usage" || (turns.HasValue && turns.Value > 0))
                {
                    if (Json.Str(root, "status") != "SUCCESS")
                    {
                        usageError = () => Json.Str(root, "error") ?? Json.Str(root, "response") ?? L.T("조회 실패");
                        return;
                    }
                    usageDisabled = true;
                    usageError = () => L.T("이 버전은 /usage 비대화형 조회를 지원하지 않음");
                    return;
                }

                var windows = new List<CachedWindow>();
                var groups = Json.Get(root, "command", "data", "groups") as object[];
                if (groups != null)
                {
                    foreach (var g in groups)
                    {
                        var buckets = Json.Get(g, "buckets") as object[];
                        if (buckets == null) continue;
                        string group = ShortGroup(Json.Str(g, "name") ?? "");
                        foreach (var b in buckets)
                        {
                            double? remaining = Json.Num(b, "remaining_fraction");
                            DateTime reset;
                            DateTime? resetAt = null;
                            if (DateTime.TryParse(Json.Str(b, "reset_time"), CultureInfo.InvariantCulture,
                                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out reset))
                                resetAt = reset.ToLocalTime();
                            string window = Json.Str(b, "window");
                            // 그룹에 한도가 하나면 그룹 이름이 곧 줄 이름, 여러 개면(상위 플랜의 일간+주간 등) 그룹을 소제목으로
                            bool several = buckets.Length > 1;
                            string key = Json.Str(b, "id") ?? group + ":" + window;
                            Func<string> heading = several ? (Func<string>)(() => group) : null;
                            Func<string> label = () => several ? WindowName(window) : group;
                            // 다른 서비스와 맞춰 '사용한 %'로 표시
                            windows.Add(new CachedWindow(key, heading, label,
                                remaining.HasValue ? (double?)Math.Round((1 - remaining.Value) * 100, 1) : null, resetAt));
                        }
                    }
                }
                if (windows.Count == 0)
                {
                    usageError = () => L.T("한도 정보 없음");
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

        static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            foreach (var line in text.Split('\n'))
                if (line.Trim().Length > 0) return line.Trim();
            return null;
        }

        // "Gemini Models" → "Gemini", "Claude and GPT models" → "Claude·GPT"
        static string ShortGroup(string name)
        {
            name = Regex.Replace(name, @"\s+models?$", "", RegexOptions.IgnoreCase);
            return Regex.Replace(name, @"\s+and\s+", "·", RegexOptions.IgnoreCase).Trim();
        }

        static string WindowName(string window)
        {
            switch ((window ?? "").ToLowerInvariant())
            {
                case "weekly": return L.T("주간");
                case "daily": return L.T("일간");
                case "monthly": return L.T("월간");
                case "five_hour":
                case "5h": return L.T("5시간");
                default: return window ?? L.T("한도");
            }
        }

        public override ServiceStatus Fetch()
        {
            var s = NewStatus();
            // Google은 소비자 플랜에서 다른 앱이 agy를 감싸 실행하는 것을 지원하지 않는다고 안내한다.
            // 그래서 agy 조회는 사용자가 설정에서 직접 켠 경우에만 하고, 기본은 로컬 활동만 표시한다.
            if (!QueryCli)
            {
                usageWindows = null;
                usageError = null;
                needsLogin = false;
                usageLastRun = DateTime.MinValue;
                ForceRefresh = false;
                s.Lines.Add(L.T("한도 %: 설정에서 'Antigravity 한도 조회'를 켜면 표시"));
            }
            else if (Due(ref usageLastRun, LimitIntervalSec) && !usageDisabled) RunUsageCommand();
            if (needsLogin) s.NeedLogin("agy", cliExe, cliIsScript, "");
            if (usageWindows != null)
            {
                foreach (var w in usageWindows) s.Windows.Add(w.ToWindow());
                s.UpdatedAt = usageAt;
            }
            if (usageError != null) s.Lines.Add(L.F("한도 %: {0}", usageError()));

            string gemini = Path.Combine(Paths.Home, ".gemini");
            var roots = new[] { Path.Combine(gemini, "antigravity"), Path.Combine(gemini, "antigravity-cli") };

            var lastByConversation = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            bool found = false;
            foreach (var root in roots)
            {
                string dir = Path.Combine(root, "conversations");
                if (!Directory.Exists(dir)) continue;
                found = true;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir))
                    {
                        string name = Path.GetFileName(file);
                        int dot = name.IndexOf('.');
                        string id = dot > 0 ? name.Substring(0, dot) : name;
                        DateTime t = File.GetLastWriteTime(file);
                        DateTime prev;
                        if (!lastByConversation.TryGetValue(id, out prev) || t > prev) lastByConversation[id] = t;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            bool ide = IsRunning("Antigravity");
            bool cli = IsRunning("agy");
            s.Active = ide || cli;
            s.Badge = L.T(ide && cli ? "IDE·CLI 실행 중" : ide ? "IDE 실행 중" : cli ? "CLI 실행 중" : "꺼짐");

            if (!found)
            {
                s.Lines.Add(L.T("로컬 기록 없음 (~/.gemini/antigravity)"));
                return s;
            }

            DateTime today = DateTime.Now.Date;
            int todayCount = 0;
            DateTime last = DateTime.MinValue;
            foreach (var t in lastByConversation.Values)
            {
                if (t >= today) todayCount++;
                if (t > last) last = t;
            }
            string line = L.F("오늘 대화 {0}개", todayCount);
            if (last > DateTime.MinValue) line += L.F(" · 마지막 활동 {0}", Fmt.Ago(last));
            s.Lines.Add(line);
            return s;
        }

        static bool IsRunning(string name)
        {
            var list = Process.GetProcessesByName(name);
            foreach (var p in list) p.Dispose();
            return list.Length > 0;
        }
    }
}
