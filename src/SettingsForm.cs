using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AIUsageTracker
{
    // 톱니바퀴 버튼으로 여는 설정 창. 값을 바꾸면 바로 저장하고 위젯에 반영한다.
    class SettingsForm : Form
    {
        static readonly int[] Intervals = { 1, 2, 3, 5, 10, 15, 30 };
        static readonly int[] Opacities = { 100, 95, 90, 80, 70, 60 };

        static readonly Color Background = Color.FromArgb(32, 33, 36);
        static readonly Color Field = Color.FromArgb(45, 46, 50);
        static readonly Color Border = Color.FromArgb(80, 84, 88);
        static readonly Color TextColor = Color.FromArgb(232, 234, 237);
        static readonly Color Dim = Color.FromArgb(154, 160, 166);
        static readonly Color Good = Color.FromArgb(129, 201, 149);

        readonly WidgetForm widget;
        readonly Settings settings;
        readonly float scale;
        readonly Font sectionFont = new Font("Segoe UI Semibold", 9f);
        readonly Font smallFont = new Font("Segoe UI", 8.25f);

        public SettingsForm(WidgetForm widget, float scale)
        {
            this.widget = widget;
            this.settings = widget.Config;
            this.scale = scale;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Background;
            ForeColor = TextColor;
            Font = new Font("Segoe UI", 9f);
            Icon = widget.Icon;
            TopMost = widget.TopMost;
            Build();
        }

        int S(float v) { return (int)Math.Round(v * scale); }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 어두운 제목 표시줄 (Windows 10 20H1 이상은 20, 그 이전 1809~ 는 19)
            int on = 1;
            if (DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(Handle, 19, ref on, sizeof(int));
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                Close();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // 언어가 바뀌면 문구를 다시 그리기 위해 컨트롤을 통째로 다시 만든다
        void Build()
        {
            SuspendLayout();
            foreach (Control c in Controls) c.Dispose();
            Controls.Clear();
            Text = L.T("설정");

            int pad = S(16), labelW = S(150), fieldX = pad + labelW, fieldW = S(170);
            int y = S(12);

            y = Section(L.T("일반"), y);

            var lang = AddCombo(L.T("언어"), L.Names, Math.Max(0, Array.IndexOf(L.Codes, L.Lang)), ref y, pad, fieldX, fieldW);
            lang.SelectedIndexChanged += delegate
            {
                settings.Set("Lang", L.Codes[lang.SelectedIndex]);
                Apply();
                BeginInvoke(new Action(Build));
            };

            int current = settings.GetInt("LimitIntervalMin", 5);
            var intervalNames = new string[Intervals.Length];
            int intervalIndex = Array.IndexOf(Intervals, 5);
            for (int i = 0; i < Intervals.Length; i++)
            {
                intervalNames[i] = L.F("{0}분마다", Intervals[i]);
                if (Intervals[i] == current) intervalIndex = i;
            }
            var interval = AddCombo(L.T("한도 조회 간격"), intervalNames, intervalIndex, ref y, pad, fieldX, fieldW);
            interval.SelectedIndexChanged += delegate
            {
                settings.Set("LimitIntervalMin", Intervals[interval.SelectedIndex]);
                Apply();
            };

            int opacityNow = settings.GetInt("Opacity", 100);
            var opacityNames = new string[Opacities.Length];
            int opacityIndex = 0;
            for (int i = 0; i < Opacities.Length; i++)
            {
                opacityNames[i] = Opacities[i] + "%";
                if (Opacities[i] == opacityNow) opacityIndex = i;
            }
            var opacity = AddCombo(L.T("불투명도"), opacityNames, opacityIndex, ref y, pad, fieldX, fieldW);
            opacity.SelectedIndexChanged += delegate
            {
                settings.Set("Opacity", Opacities[opacity.SelectedIndex]);
                Apply();
            };

            var topMost = AddCheck(L.T("항상 위에 표시"), settings.GetBool("TopMost", true), ref y, pad);
            topMost.CheckedChanged += delegate
            {
                settings.Set("TopMost", topMost.Checked);
                Apply();
                TopMost = topMost.Checked;
            };

            var startup = AddCheck(L.T("Windows 시작 시 자동 실행"), WidgetForm.IsStartupEnabled(), ref y, pad);
            startup.CheckedChanged += delegate { WidgetForm.SetStartup(startup.Checked); };

            y += S(6);
            y = Section(L.T("표시할 서비스"), y);
            int x = pad;
            foreach (var p in widget.Providers)
            {
                var provider = p;
                var box = NewCheck(provider.Name, provider.Enabled);
                box.Location = new Point(x, y);
                box.CheckedChanged += delegate
                {
                    settings.Set("Show." + provider.Name, box.Checked);
                    Apply();
                };
                Controls.Add(box);
                x += box.PreferredSize.Width + S(14);
            }
            y += S(30);

            y += S(6);
            y = Section(L.T("Claude statusLine 연동 (선택)"), y);
            var state = ClaudeIntegration.Check();
            string stateText = state == StatusLineState.Installed ? L.T("연동됨")
                : state == StatusLineState.OtherPath ? L.T("exe 위치가 바뀜")
                : state == StatusLineState.Foreign ? L.T("다른 statusLine 사용 중")
                : L.T("연동 안 됨");
            var status = new Label();
            status.Text = stateText;
            status.ForeColor = state == StatusLineState.Installed ? Good : Dim;
            status.AutoSize = true;
            status.Location = new Point(pad, y + S(5));
            Controls.Add(status);
            var link = NewButton(L.T(state == StatusLineState.Installed ? "해제" : "연동"));
            link.Location = new Point(fieldX + fieldW - link.Width, y);
            link.Click += delegate
            {
                if (ClaudeIntegration.Check() == StatusLineState.Installed) widget.UninstallClaude(this);
                else widget.InstallClaude(this);
                Build();
            };
            Controls.Add(link);
            y += link.Height + S(4);
            var hint = new Label();
            hint.Text = L.T("터미널 Claude Code 세션의 한도를 실시간으로 반영합니다.");
            hint.Font = smallFont;
            hint.ForeColor = Dim;
            hint.AutoSize = true;
            hint.Location = new Point(pad, y);
            Controls.Add(hint);
            y += S(30);

            var close = NewButton(L.T("닫기"));
            close.Location = new Point(fieldX + fieldW - close.Width, y);
            close.Click += delegate { Close(); };
            Controls.Add(close);
            y += close.Height + S(14);

            ClientSize = new Size(fieldX + fieldW + pad, y);
            ResumeLayout();
        }

        void Apply()
        {
            settings.Save();
            widget.ApplyOptions();
        }

        int Section(string text, int y)
        {
            var label = new Label();
            label.Text = text;
            label.Font = sectionFont;
            label.ForeColor = Dim;
            label.AutoSize = true;
            label.Location = new Point(S(16), y);
            Controls.Add(label);
            return y + S(26);
        }

        ComboBox AddCombo(string label, string[] items, int selected, ref int y, int pad, int fieldX, int fieldW)
        {
            var text = new Label();
            text.Text = label;
            text.AutoSize = true;
            text.Location = new Point(pad, y + S(4));
            Controls.Add(text);

            var combo = new ComboBox();
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.FlatStyle = FlatStyle.Flat;
            combo.BackColor = Field;
            combo.ForeColor = TextColor;
            combo.Items.AddRange(items);
            combo.SelectedIndex = selected;
            combo.Location = new Point(fieldX, y);
            combo.Width = fieldW;
            Controls.Add(combo);
            y += S(34);
            return combo;
        }

        CheckBox AddCheck(string label, bool value, ref int y, int pad)
        {
            var box = NewCheck(label, value);
            box.Location = new Point(pad, y);
            Controls.Add(box);
            y += S(28);
            return box;
        }

        CheckBox NewCheck(string label, bool value)
        {
            var box = new CheckBox();
            box.Text = label;
            box.Checked = value;
            box.AutoSize = true;
            box.FlatStyle = FlatStyle.Flat;
            box.FlatAppearance.BorderColor = Border;
            box.FlatAppearance.CheckedBackColor = Field;
            box.ForeColor = TextColor;
            return box;
        }

        Button NewButton(string text)
        {
            var button = new Button();
            button.Text = text;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Border;
            button.BackColor = Field;
            button.ForeColor = TextColor;
            button.UseVisualStyleBackColor = false;
            button.Size = new Size(Math.Max(S(84), TextRenderer.MeasureText(text, Font).Width + S(24)), S(28));
            return button;
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    }
}
