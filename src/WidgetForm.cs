using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AIUsageTracker
{
    class WidgetForm : Form
    {
        const int LogicalWidth = 320;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "AIUsageTracker";
        const string OldRunValue = "UsageWidget";   // 이전 이름

        readonly List<Provider> providers = new List<Provider>();
        readonly Settings settings = new Settings();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly EventWaitHandle showSignal;
        NotifyIcon tray;
        ContextMenuStrip menu;
        SettingsForm settingsForm;
        Icon appIcon;
        float scale = 1f;
        DateTime lastRepaint = DateTime.Now;

        Rectangle settingsButton, refreshButton, closeButton;
        int hoverButton;   // 0 없음, 1 새로고침, 2 닫기, 3 설정
        readonly List<Tuple<Rectangle, Provider>> loginLinks = new List<Tuple<Rectangle, Provider>>();
        Provider hoverLogin;

        readonly Font titleFont = new Font("Segoe UI Semibold", 10f);
        readonly Font nameFont = new Font("Segoe UI Semibold", 9.5f);
        readonly Font bodyFont = new Font("Segoe UI", 9f);
        readonly Font smallFont = new Font("Segoe UI", 8.25f);
        readonly Font iconFont = new Font("Segoe MDL2 Assets", 8f);
        readonly Font linkHoverFont = new Font("Segoe UI", 8.25f, FontStyle.Underline);

        static readonly Color Background = Color.FromArgb(32, 33, 36);
        static readonly Color Border = Color.FromArgb(60, 64, 67);
        static readonly Color TextColor = Color.FromArgb(232, 234, 237);
        static readonly Color Dim = Color.FromArgb(154, 160, 166);
        static readonly Color Track = Color.FromArgb(60, 63, 68);
        static readonly Color Hover = Color.FromArgb(55, 58, 62);
        static readonly Color Warn = Color.FromArgb(242, 179, 61);
        static readonly Color Danger = Color.FromArgb(239, 83, 80);
        static readonly Color Link = Color.FromArgb(138, 180, 248);

        public WidgetForm(EventWaitHandle showSignal)
        {
            this.showSignal = showSignal;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Background;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            appIcon = IconFactory.Create(32);
            Icon = appIcon;

            providers.Add(new ClaudeProvider());
            providers.Add(new CodexProvider());
            providers.Add(new AntigravityProvider());

            MigrateStartup();
            BuildTray();
            ApplyOptions();

            ClientSize = new Size(S(LogicalWidth), MeasureHeight());
            PlaceOnScreen();

            timer.Interval = 1000;
            timer.Tick += delegate { Tick(); };
            timer.Start();

            var waiter = new Thread(WaitForShowSignal);
            waiter.IsBackground = true;
            waiter.Start();
        }

        public Settings Config { get { return settings; } }
        public IList<Provider> Providers { get { return providers; } }

        int S(float v) { return (int)Math.Round(v * scale); }

        // 저장된 설정을 화면과 조회 주기에 반영한다. 설정 창에서 값이 바뀔 때마다 호출된다.
        public void ApplyOptions()
        {
            string oldLang = L.Lang;
            L.Load(settings);
            Text = L.T("AI 사용량");

            Provider.LimitIntervalSec = Math.Max(1, settings.GetInt("LimitIntervalMin", 5)) * 60;
            foreach (var p in providers)
            {
                p.Enabled = settings.GetBool("Show." + p.Name, true);
                if (p is CodexProvider)
                {
                    p.IntervalSec = Provider.LimitIntervalSec;
                    var due = DateTime.Now.AddSeconds(p.IntervalSec);
                    if (p.NextDue > due) p.NextDue = due;
                }
            }

            if (IsHandleCreated) ApplyTopMost(settings.GetBool("TopMost", true));
            else TopMost = settings.GetBool("TopMost", true);
            Opacity = settings.GetInt("Opacity", 100) / 100.0;

            BuildMenu();
            if (oldLang != L.Lang && IsHandleCreated)
            {
                // 카드 문구를 새 언어로 다시 만든다. 느린 한도 조회(CLI)는 다시 돌리지 않고 저장된 값을 쓴다.
                foreach (var p in providers) p.NextDue = DateTime.MinValue;
                Tick();
                UpdateLayoutAndPaint();
            }
            else if (IsHandleCreated) UpdateLayoutAndPaint();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80;          // WS_EX_TOOLWINDOW: 작업 표시줄/Alt+Tab 제외
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Windows 11은 DWM으로 둥근 모서리, Windows 10은 Region으로 처리
            int pref = 2;
            roundedByDwm = DwmSetWindowAttribute(Handle, 33, ref pref, sizeof(int)) == 0 && Environment.OSVersion.Version.Build >= 22000;
            if (roundedByDwm) Region = null;
            else OnSizeChanged(EventArgs.Empty);
            Tick();
        }

        bool roundedByDwm;

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ApplyTopMost(TopMost);
        }

        // 테두리 없는 창은 처음 표시될 때 WS_EX_TOPMOST가 빠지는 경우가 있다. 끄고 다시 켜야 적용된다.
        void ApplyTopMost(bool on)
        {
            TopMost = false;
            if (on) TopMost = true;
        }

        bool IsActuallyTopMost
        {
            get { return (GetWindowLong(Handle, -20) & 0x8) != 0; }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (roundedByDwm || Width == 0) return;
            using (var path = RoundRect(new Rectangle(0, 0, Width, Height), S(8)))
                Region = new Region(path);
        }

        void PlaceOnScreen()
        {
            int x = settings.GetInt("X", int.MinValue), y = settings.GetInt("Y", int.MinValue);
            var rect = new Rectangle(x, y, Width, Height);
            bool visible = false;
            foreach (var screen in Screen.AllScreens)
                if (screen.WorkingArea.IntersectsWith(rect)) visible = true;
            if (x == int.MinValue || !visible)
            {
                var wa = Screen.PrimaryScreen.WorkingArea;
                x = wa.Right - Width - S(16);
                y = wa.Top + S(16);
            }
            Location = new Point(x, y);
        }

        void WaitForShowSignal()
        {
            while (true)
            {
                showSignal.WaitOne();
                try { BeginInvoke(new Action(ShowWidget)); }
                catch (InvalidOperationException) { return; }
            }
        }

        // ---------- 갱신 ----------

        void Tick()
        {
            foreach (var p in providers)
                if (p.Enabled && !p.Busy && DateTime.Now >= p.NextDue) Start(p);
            if ((DateTime.Now - lastRepaint).TotalSeconds >= 15) UpdateLayoutAndPaint();
            if (TopMost && Visible && !IsActuallyTopMost) ApplyTopMost(true);
        }

        void Start(Provider p)
        {
            p.Busy = true;
            Task.Factory.StartNew(() => p.Fetch()).ContinueWith(t =>
            {
                ServiceStatus result;
                if (t.IsFaulted)
                {
                    result = new ServiceStatus();
                    result.Name = p.Name;
                    result.Accent = p.Accent;
                    result.Error = t.Exception.GetBaseException().Message;
                }
                else result = t.Result;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        p.Last = result;
                        p.NextDue = DateTime.Now.AddSeconds(p.IntervalSec);
                        if (p.LoginWatchUntil > DateTime.Now)
                        {
                            if (result.LoginCommand == null) p.LoginWatchUntil = DateTime.MinValue;   // 로그인 완료
                            else
                            {
                                p.NextDue = DateTime.Now.AddSeconds(15);
                                p.ForceRefresh = true;
                            }
                        }
                        p.Busy = false;
                        UpdateLayoutAndPaint();
                    }));
                }
                catch (InvalidOperationException) { }
            });
        }

        void RefreshAll()
        {
            foreach (var p in providers)
            {
                p.NextDue = DateTime.MinValue;
                p.ForceRefresh = true;
            }
            Tick();
            UpdateLayoutAndPaint();
        }

        void UpdateLayoutAndPaint()
        {
            lastRepaint = DateTime.Now;
            int h = MeasureHeight();
            if (ClientSize.Height != h) ClientSize = new Size(S(LogicalWidth), h);
            UpdateTrayText();
            Invalidate();
        }

        void UpdateTrayText()
        {
            if (tray == null) return;
            var parts = new List<string>();
            foreach (var p in providers)
            {
                if (!p.Enabled || p.Last == null || p.Last.Windows.Count == 0) continue;
                var w = p.Last.Windows[0];
                if (w.Percent.HasValue) parts.Add(p.Name + " " + Math.Round(w.Percent.Value) + "%");
            }
            string text = parts.Count > 0 ? string.Join(" · ", parts) : L.T("AI 사용량");
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        // ---------- 그리기 ----------

        int MeasureHeight()
        {
            return Render(null);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Render(e.Graphics);
            if (!roundedByDwm)
            {
                using (var pen = new Pen(Border))
                using (var path = RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), S(8)))
                    e.Graphics.DrawPath(pen, path);
            }
            else
            {
                using (var pen = new Pen(Border))
                    e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
        }

        // g가 null이면 높이만 계산한다
        int Render(Graphics g)
        {
            loginLinks.Clear();
            int w = S(LogicalWidth), pad = S(14);
            int y = S(10);

            // 헤더
            int btn = S(24);
            closeButton = new Rectangle(w - pad - btn + S(6), y, btn, btn);
            refreshButton = new Rectangle(closeButton.Left - btn, y, btn, btn);
            settingsButton = new Rectangle(refreshButton.Left - btn, y, btn, btn);
            if (g != null)
            {
                DrawText(g, L.T("AI 사용량"), titleFont, TextColor, new Rectangle(pad, y, w / 2, btn), TextFormatFlags.Left);
                DrawIconButton(g, settingsButton, "", hoverButton == 3);
                DrawIconButton(g, refreshButton, "", hoverButton == 1);
                DrawIconButton(g, closeButton, "", hoverButton == 2);
            }
            y += btn + S(4);

            foreach (var p in providers)
            {
                if (!p.Enabled) continue;
                y += S(6);
                if (g != null)
                    using (var pen = new Pen(Track)) g.DrawLine(pen, pad, y, w - pad, y);
                y += S(8);
                y = RenderService(g, p, p.Last, y, w, pad);
            }
            return y + S(8);
        }

        int RenderService(Graphics g, Provider p, ServiceStatus s, int y, int w, int pad)
        {
            int row = S(22);
            if (g != null)
            {
                int dot = S(8);
                var dotRect = new Rectangle(pad, y + (row - dot) / 2, dot, dot);
                bool active = s != null && s.Active;
                using (var brush = new SolidBrush(active ? p.Accent : Track)) g.FillEllipse(brush, dotRect);
                DrawText(g, p.Name, nameFont, TextColor, new Rectangle(pad + S(14), y, w / 2, row), TextFormatFlags.Left);

                var right = new List<string>();
                if (s == null) right.Add(p.Busy ? L.T("불러오는 중…") : "");
                else if (s.LoginCommand != null) right.Add(L.T("로그인 필요"));
                else
                {
                    if (!string.IsNullOrEmpty(s.Badge)) right.Add(s.Badge);
                    if (s.UpdatedAt.HasValue) right.Add(Fmt.Ago(s.UpdatedAt.Value));
                }
                DrawText(g, string.Join(" · ", right), smallFont, s != null && s.LoginCommand != null ? Warn : Dim,
                    new Rectangle(w / 2, y, w / 2 - pad, row), TextFormatFlags.Right);
            }
            y += row;
            if (s == null) return y;

            int labelW = S(42), pctW = S(38), resetW = S(70), gap = S(6), lineH = S(20);
            foreach (var win in s.Windows)
                labelW = Math.Max(labelW, TextRenderer.MeasureText(win.Label, smallFont, Size.Empty, TextFormatFlags.NoPadding).Width + S(8));
            labelW = Math.Min(labelW, S(110));   // 긴 모델 이름은 말줄임
            int barX = pad + labelW;
            int barW = w - pad - resetW - gap - pctW - gap - barX;
            string group = null;
            foreach (var win in s.Windows)
            {
                // 모델 전용 한도 묶음은 소제목을 한 번 그리고 그 아래에 줄을 둔다
                if (win.Group != group)
                {
                    group = win.Group;
                    if (group != null)
                    {
                        if (g != null) DrawText(g, group, smallFont, TextColor, new Rectangle(pad, y + S(2), w - 2 * pad, S(16)), TextFormatFlags.Left);
                        y += S(18);
                    }
                }
                if (g != null)
                {
                    DrawText(g, win.Label, smallFont, Dim, new Rectangle(pad, y, labelW, lineH), TextFormatFlags.Left);
                    int barH = S(6);
                    var track = new Rectangle(barX, y + (lineH - barH) / 2, barW, barH);
                    using (var path = RoundRect(track, barH / 2))
                    using (var brush = new SolidBrush(Track)) g.FillPath(brush, path);
                    if (win.Percent.HasValue && win.Percent.Value > 0)
                    {
                        double pct = Math.Min(100, Math.Max(0, win.Percent.Value));
                        var fill = new Rectangle(track.X, track.Y, Math.Max(barH, (int)(track.Width * pct / 100)), barH);
                        Color c = pct >= 90 ? Danger : pct >= 75 ? Warn : p.Accent;
                        using (var path = RoundRect(fill, barH / 2))
                        using (var brush = new SolidBrush(c)) g.FillPath(brush, path);
                    }
                    string pctText = win.Percent.HasValue ? Math.Round(win.Percent.Value) + "%" : "–";
                    DrawText(g, pctText, bodyFont, TextColor, new Rectangle(barX + barW + gap, y, pctW, lineH), TextFormatFlags.Right);
                    string reset = win.Note ?? (win.ResetsAt.HasValue ? Fmt.Until(win.ResetsAt.Value) : "");
                    DrawText(g, reset, smallFont, Dim, new Rectangle(w - pad - resetW, y, resetW, lineH), TextFormatFlags.Right);
                }
                y += lineH;
            }

            int textH = S(18);
            if (s.LoginCommand != null)
            {
                // CLI 경로를 알면 클릭해서 공식 로그인 명령을 터미널로 연다
                string text = s.LoginExe != null ? L.F("클릭하여 로그인: {0}", s.LoginCommand) : L.F("로그인: 터미널에서 {0} 실행", s.LoginCommand);
                int textW = TextRenderer.MeasureText(text, smallFont, Size.Empty, TextFormatFlags.NoPadding).Width;
                var rect = new Rectangle(pad, y, Math.Min(textW + S(4), w - 2 * pad), textH);
                if (s.LoginExe != null) loginLinks.Add(Tuple.Create(rect, p));
                if (g != null)
                {
                    bool hover = hoverLogin == p;
                    DrawText(g, text, hover ? linkHoverFont : smallFont, s.LoginExe != null ? Link : TextColor, rect, TextFormatFlags.Left);
                }
                y += textH;
            }
            foreach (var line in s.Lines)
            {
                if (g != null) DrawText(g, line, smallFont, Dim, new Rectangle(pad, y, w - 2 * pad, textH), TextFormatFlags.Left);
                y += textH;
            }
            if (!string.IsNullOrEmpty(s.Error))
            {
                if (g != null) DrawText(g, s.Error, smallFont, Danger, new Rectangle(pad, y, w - 2 * pad, textH), TextFormatFlags.Left);
                y += textH;
            }
            return y;
        }

        void DrawIconButton(Graphics g, Rectangle r, string glyph, bool hover)
        {
            if (hover)
                using (var path = RoundRect(r, S(4)))
                using (var brush = new SolidBrush(Hover)) g.FillPath(brush, path);
            DrawText(g, glyph, iconFont, hover ? TextColor : Dim, r, TextFormatFlags.HorizontalCenter);
        }

        static void DrawText(Graphics g, string text, Font font, Color color, Rectangle r, TextFormatFlags align)
        {
            TextRenderer.DrawText(g, text, font, r, color,
                align | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // ---------- 마우스 ----------

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = refreshButton.Contains(e.Location) ? 1 : closeButton.Contains(e.Location) ? 2 : settingsButton.Contains(e.Location) ? 3 : 0;
            Provider link = LoginLinkAt(e.Location);
            if (h != hoverButton || link != hoverLogin)
            {
                hoverButton = h;
                hoverLogin = link;
                Cursor = link != null ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverButton != 0 || hoverLogin != null)
            {
                hoverButton = 0;
                hoverLogin = null;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        Provider LoginLinkAt(Point pt)
        {
            foreach (var link in loginLinks)
                if (link.Item1.Contains(pt)) return link.Item2;
            return null;
        }

        // 공식 로그인 명령을 새 콘솔 창에서 실행한다. 로그인은 그 창에서 사용자가 직접 진행한다.
        void OpenLogin(Provider p)
        {
            var s = p.Last;
            if (s == null || s.LoginExe == null) return;
            try
            {
                string command = "\"" + s.LoginExe + "\"" + (s.LoginArgs.Length > 0 ? " " + s.LoginArgs : "");
                var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/k \"" + command + "\"");
                psi.UseShellExecute = true;
                psi.WorkingDirectory = Paths.Home;
                Process.Start(psi);
                // 로그인이 끝나면 바로 보이도록 10분 동안 15초마다 다시 확인한다
                p.LoginWatchUntil = DateTime.Now.AddMinutes(10);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L.F("로그인 창을 열 수 없음: {0}", ex.Message), L.T("AI 사용량"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (settingsButton.Contains(e.Location)) { OpenSettings(); return; }
            if (refreshButton.Contains(e.Location)) { RefreshAll(); return; }
            if (closeButton.Contains(e.Location)) { HideToTray(); return; }
            var login = LoginLinkAt(e.Location);
            if (login != null) { OpenLogin(login); return; }
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);   // WM_NCLBUTTONDOWN + HTCAPTION: 창 끌기
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            settings.Set("X", Left);
            settings.Set("Y", Top);
            settings.Save();
        }

        // ---------- 설정 창 ----------

        void OpenSettings()
        {
            if (settingsForm != null && !settingsForm.IsDisposed)
            {
                settingsForm.Activate();
                return;
            }
            settingsForm = new SettingsForm(this, scale);
            // 위젯 옆에 붙여서 띄우고, 화면 밖으로 나가지 않게 한다
            var wa = Screen.FromControl(this).WorkingArea;
            int x = Left - settingsForm.Width - S(8);
            if (x < wa.Left) x = Right + S(8);
            if (x + settingsForm.Width > wa.Right) x = wa.Right - settingsForm.Width;
            int y = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - settingsForm.Height));
            settingsForm.Location = new Point(x, y);
            settingsForm.Show(this);
        }

        // ---------- 메뉴 / 트레이 ----------

        void BuildMenu()
        {
            var old = menu;
            menu = new ContextMenuStrip();
            menu.Items.Add(L.T("지금 새로고침"), null, delegate { RefreshAll(); });
            menu.Items.Add(L.T("설정…"), null, delegate { ShowWidget(); OpenSettings(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(L.T("데이터 폴더 열기"), null, delegate { Process.Start("explorer.exe", "\"" + Paths.DataDir + "\""); });
            menu.Items.Add(L.T("종료"), null, delegate { ExitApp(); });
            ContextMenuStrip = menu;
            if (tray != null) tray.ContextMenuStrip = menu;
            if (old != null) old.Dispose();
        }

        void BuildTray()
        {
            tray = new NotifyIcon();
            tray.Icon = appIcon;
            tray.Text = L.T("AI 사용량");
            tray.MouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                if (Visible) HideToTray();
                else ShowWidget();
            };
            tray.Visible = true;
        }

        void ShowWidget()
        {
            if (Visible) return;
            Show();
            ApplyTopMost(TopMost);
            Activate();
            RefreshAll();
        }

        void HideToTray()
        {
            Hide();
            if (!settings.GetBool("TrayTipShown", false))
            {
                tray.ShowBalloonTip(3000, L.T("AI 사용량"), L.T("트레이에서 계속 실행 중입니다. 아이콘을 클릭하면 다시 표시됩니다."), ToolTipIcon.Info);
                settings.Set("TrayTipShown", true);
                settings.Save();
            }
        }

        void ExitApp()
        {
            tray.Visible = false;
            Application.Exit();
        }

        public void InstallClaude(IWin32Window owner)
        {
            try
            {
                string result = ClaudeIntegration.Install(false);
                if (result == "foreign")
                {
                    var answer = MessageBox.Show(owner,
                        L.T("Claude Code에 이미 다른 statusLine이 설정되어 있습니다.\n이 위젯용으로 바꿀까요? (기존 설정은 '해제' 시 복원됩니다)"),
                        L.T("AI 사용량"), MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (answer != DialogResult.Yes) return;
                    ClaudeIntegration.Install(true);
                }
                MessageBox.Show(owner, L.T("등록했습니다. 터미널에서 Claude Code를 쓰면 한도가 실시간으로 갱신됩니다.\n(settings.json 백업: settings.json.aiusagetracker.bak)"),
                    L.T("AI 사용량"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, L.F("등록 실패: {0}", ex.Message), L.T("AI 사용량"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            RefreshAll();
        }

        public void UninstallClaude(IWin32Window owner)
        {
            try { ClaudeIntegration.Uninstall(); }
            catch (Exception ex) { MessageBox.Show(owner, ex.Message, L.T("AI 사용량"), MessageBoxButtons.OK, MessageBoxIcon.Error); }
            RefreshAll();
        }

        // 이전 이름으로 등록된 자동 실행 항목이 있으면 새 이름과 현재 exe 경로로 옮긴다
        static void MigrateStartup()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null || key.GetValue(OldRunValue) == null) return;
                    key.DeleteValue(OldRunValue, false);
                    key.SetValue(RunValue, "\"" + Paths.ExePath + "\"");
                }
            }
            catch { }
        }

        public static bool IsStartupEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key != null && key.GetValue(RunValue) != null;
        }

        public static void SetStartup(bool enable)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enable) key.SetValue(RunValue, "\"" + Paths.ExePath + "\"");
                else key.DeleteValue(RunValue, false);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            tray.Visible = false;
            base.OnFormClosing(e);
        }

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    }
}
