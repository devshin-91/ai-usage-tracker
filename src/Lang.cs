using System;
using System.Collections.Generic;
using System.Globalization;

namespace AIUsageTracker
{
    // 한국어 문자열을 키로 쓰고, 다른 언어일 때만 번역을 찾는다. 번역이 없으면 원문을 그대로 쓴다.
    static class L
    {
        public static readonly string[] Codes = { "ko", "en", "ja", "zh" };
        public static readonly string[] Names = { "한국어", "English", "日本語", "中文(简体)" };

        public static string Lang = "ko";

        public static string DefaultLang
        {
            get
            {
                string code = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                return Array.IndexOf(Codes, code) >= 0 ? code : "en";
            }
        }

        public static void Load(Settings settings)
        {
            string code = settings.GetString("Lang", DefaultLang);
            Lang = Array.IndexOf(Codes, code) >= 0 ? code : "en";
        }

        public static string T(string ko)
        {
            int column = Array.IndexOf(Codes, Lang) - 1;   // en=0, ja=1, zh=2
            string[] row;
            if (column >= 0 && Table.TryGetValue(ko, out row) && row[column] != null) return row[column];
            return ko;
        }

        public static string F(string ko, params object[] args)
        {
            return string.Format(CultureInfo.InvariantCulture, T(ko), args);
        }

        static void Add(string ko, string en, string ja, string zh)
        {
            Table[ko] = new[] { en, ja, zh };
        }

        static readonly Dictionary<string, string[]> Table = new Dictionary<string, string[]>();

        static L()
        {
            // 시간 표시
            Add("곧 리셋", "soon", "まもなく", "即将重置");
            Add("{0}분", "{0}m", "{0}分", "{0}分钟");
            Add("{0}시간 {1}분", "{0}h {1}m", "{0}時間{1}分", "{0}小时{1}分");
            Add("{0}일 {1}시간", "{0}d {1}h", "{0}日{1}時間", "{0}天{1}小时");
            Add("방금", "just now", "たった今", "刚刚");
            Add("{0}분 전", "{0}m ago", "{0}分前", "{0}分钟前");
            Add("{0}시간 전", "{0}h ago", "{0}時間前", "{0}小时前");
            Add("{0}일 전", "{0}d ago", "{0}日前", "{0}天前");
            Add("{0}시간", "{0}h", "{0}時間", "{0}小时");
            Add("{0}일", "{0}d", "{0}日", "{0}天");
            Add("리셋됨", "reset", "リセット済み", "已重置");

            // 한도 이름
            Add("5시간", "5h", "5時間", "5小时");
            Add("주간", "Weekly", "週間", "每周");
            Add("일간", "Daily", "日間", "每日");
            Add("주간 {0}", "Weekly {0}", "週間 {0}", "每周 {0}");
            Add("지출", "Spend", "支出", "支出");
            Add("한도", "Limit", "上限", "限额");

            // 서비스 카드
            Add("한도 %: {0}", "Limits: {0}", "上限: {0}", "限额: {0}");
            Add("오늘 토큰 {0} · 출력 {1} · 요청 {2}회", "Today {0} tokens · out {1} · {2} req", "今日 {0} トークン · 出力 {1} · {2} 回", "今日 {0} tokens · 输出 {1} · {2} 次请求");
            Add("기록 읽기 실패: {0}", "Failed to read logs: {0}", "ログの読み込みに失敗: {0}", "读取记录失败: {0}");
            Add("{0} CLI를 찾을 수 없음", "{0} CLI not found", "{0} CLI が見つかりません", "未找到 {0} CLI");
            Add("/usage 결과를 읽을 수 없음", "Couldn't read /usage output", "/usage の結果を読み取れません", "无法读取 /usage 结果");
            Add("이 버전은 /usage 비대화형 조회를 지원하지 않음", "This version can't run /usage non-interactively", "このバージョンは /usage の非対話実行に未対応", "此版本不支持非交互式 /usage");
            Add("API 키 사용 중: 구독 한도 없음", "Using an API key: no plan limits", "APIキー使用中: プラン上限なし", "正在使用 API 密钥: 无订阅限额");
            Add("구독 한도 정보 없음", "No plan limit data", "プラン上限の情報なし", "无订阅限额信息");
            Add("/usage 응답 시간 초과", "/usage timed out", "/usage がタイムアウト", "/usage 超时");
            Add("/usage 실패: {0}", "/usage failed: {0}", "/usage 失敗: {0}", "/usage 失败: {0}");
            Add("Codex CLI를 찾을 수 없음 (npm i -g @openai/codex)", "Codex CLI not found (npm i -g @openai/codex)", "Codex CLI が見つかりません (npm i -g @openai/codex)", "未找到 Codex CLI (npm i -g @openai/codex)");
            Add("초기화 실패: {0}", "Init failed: {0}", "初期化失敗: {0}", "初始化失败: {0}");
            Add("알 수 없는 오류", "Unknown error", "不明なエラー", "未知错误");
            Add("Codex 응답 시간 초과", "Codex timed out", "Codex がタイムアウト", "Codex 超时");
            Add("Codex 조회 실패: {0}", "Codex query failed: {0}", "Codex の取得に失敗: {0}", "Codex 查询失败: {0}");
            Add("사용량 정보 없음", "No usage data", "使用量データなし", "无用量数据");
            Add("한도 도달 ({0})", "Limit reached ({0})", "上限に到達 ({0})", "已达限额 ({0})");
            Add("크레딧 무제한", "Unlimited credits", "クレジット無制限", "无限额度");
            Add("크레딧 잔액 {0}", "Credits {0}", "クレジット残高 {0}", "额度余额 {0}");
            Add("한도 리셋권 {0}개 보유", "{0} limit resets available", "上限リセット {0} 回分あり", "可用限额重置 {0} 次");
            Add("한도 정보 없음", "No limit data", "上限データなし", "无限额数据");
            Add("IDE·CLI 실행 중", "IDE·CLI running", "IDE·CLI 実行中", "IDE·CLI 运行中");
            Add("IDE 실행 중", "IDE running", "IDE 実行中", "IDE 运行中");
            Add("CLI 실행 중", "CLI running", "CLI 実行中", "CLI 运行中");
            Add("꺼짐", "not running", "停止中", "未运行");
            Add("한도 %: 약관상 외부 조회 불가 (앱에서 확인)", "Limits: check in Antigravity (terms disallow apps)", "上限: 規約上外部取得不可 (アプリで確認)", "限额: 条款不允许外部查询 (请在应用中查看)");
            Add("로컬 기록 없음 (~/.gemini/antigravity)", "No local data (~/.gemini/antigravity)", "ローカル記録なし (~/.gemini/antigravity)", "无本地记录 (~/.gemini/antigravity)");
            Add("오늘 대화 {0}개", "{0} conversations today", "今日の会話 {0} 件", "今日对话 {0} 个");
            Add(" · 마지막 활동 {0}", " · last active {0}", " · 最終アクティビティ {0}", " · 最近活动 {0}");
            Add("로그인 필요", "Sign-in needed", "ログインが必要", "需要登录");
            Add("클릭하여 로그인: {0}", "Click to sign in: {0}", "クリックしてログイン: {0}", "点击登录: {0}");
            Add("사용 크레딧 확인 (claude.ai)", "Check usage credits (claude.ai)", "使用クレジットを確認 (claude.ai)", "查看使用积分 (claude.ai)");
            Add("페이지를 열 수 없음: {0}", "Can't open the page: {0}", "ページを開けません: {0}", "无法打开页面: {0}");
            Add("로그인 창을 열 수 없음: {0}", "Couldn't open the sign-in window: {0}", "ログイン画面を開けません: {0}", "无法打开登录窗口: {0}");
            Add("로그인: 터미널에서 {0} 실행","To sign in, run {0} in a terminal", "ログイン: ターミナルで {0} を実行", "登录: 在终端运行 {0}");

            // 위젯
            Add("AI 사용량", "AI Usage", "AI 使用量", "AI 用量");
            Add("불러오는 중…", "Loading…", "読み込み中…", "加载中…");
            Add("지금 새로고침", "Refresh now", "今すぐ更新", "立即刷新");
            Add("설정…", "Settings…", "設定…", "设置…");
            Add("데이터 폴더 열기", "Open data folder", "データフォルダーを開く", "打开数据文件夹");
            Add("종료", "Exit", "終了", "退出");
            Add("트레이에서 계속 실행 중입니다. 아이콘을 클릭하면 다시 표시됩니다.",
                "Still running in the tray. Click the icon to show it again.",
                "トレイで実行中です。アイコンをクリックすると再表示します。",
                "仍在托盘中运行。点击图标即可重新显示。");
            Add("Claude Code에 이미 다른 statusLine이 설정되어 있습니다.\n이 위젯용으로 바꿀까요? (기존 설정은 '해제' 시 복원됩니다)",
                "Claude Code already has a different statusLine.\nReplace it with this widget's? (The old one is restored when you disconnect.)",
                "Claude Code には別の statusLine が設定されています。\nこのウィジェット用に置き換えますか？（元の設定は「解除」で復元されます）",
                "Claude Code 已设置了其他 statusLine。\n要替换为此小组件的吗？（点击“断开”可恢复原设置）");
            Add("등록했습니다. 터미널에서 Claude Code를 쓰면 한도가 실시간으로 갱신됩니다.\n(settings.json 백업: settings.json.aiusagetracker.bak)",
                "Connected. Limits now update live while you use Claude Code in a terminal.\n(settings.json backup: settings.json.aiusagetracker.bak)",
                "連携しました。ターミナルで Claude Code を使うと上限がリアルタイムで更新されます。\n(settings.json のバックアップ: settings.json.aiusagetracker.bak)",
                "已连接。在终端使用 Claude Code 时限额会实时更新。\n(settings.json 备份: settings.json.aiusagetracker.bak)");
            Add("등록 실패: {0}", "Setup failed: {0}", "連携に失敗: {0}", "连接失败: {0}");

            // 설정 창
            Add("설정", "Settings", "設定", "设置");
            Add("일반", "General", "一般", "常规");
            Add("언어", "Language", "言語", "语言");
            Add("한도 조회 간격", "Limit check interval", "上限の確認間隔", "限额检查间隔");
            Add("{0}분마다", "Every {0} min", "{0} 分ごと", "每 {0} 分钟");
            Add("불투명도", "Opacity", "不透明度", "不透明度");
            Add("항상 위에 표시", "Always on top", "常に手前に表示", "始终置顶");
            Add("Windows 시작 시 자동 실행", "Start with Windows", "Windows 起動時に自動実行", "开机自动启动");
            Add("표시할 서비스", "Services", "表示するサービス", "显示的服务");
            Add("Claude statusLine 연동 (선택)", "Claude statusLine (optional)", "Claude statusLine 連携 (任意)", "Claude statusLine 连接 (可选)");
            Add("터미널 Claude Code 세션의 한도를 실시간으로 반영합니다.",
                "Shows live limits from terminal Claude Code sessions.",
                "ターミナルの Claude Code セッションの上限をリアルタイムで反映します。",
                "实时反映终端 Claude Code 会话的限额。");
            Add("연동됨", "Connected", "連携中", "已连接");
            Add("연동 안 됨", "Not connected", "未連携", "未连接");
            Add("exe 위치가 바뀜", "exe was moved", "exe の場所が変更されました", "exe 位置已变更");
            Add("다른 statusLine 사용 중", "Another statusLine is set", "別の statusLine を使用中", "正在使用其他 statusLine");
            Add("연동", "Connect", "連携", "连接");
            Add("해제", "Disconnect", "解除", "断开");
            Add("닫기", "Close", "閉じる", "关闭");
        }
    }
}
