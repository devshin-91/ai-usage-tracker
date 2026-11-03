using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("AI Usage Tracker")]
[assembly: AssemblyDescription("Floating usage widget for Claude Code, Codex and Antigravity")]
[assembly: AssemblyProduct("AI Usage Tracker")]
[assembly: AssemblyVersion("1.0.2.0")]
[assembly: AssemblyFileVersion("1.0.2.0")]

namespace AIUsageTracker
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0)
            {
                switch (args[0])
                {
                    case "--statusline":
                        return ClaudeStatusLine.Run();
                    case "--install-statusline":
                        return Report(ClaudeIntegration.Install(Array.IndexOf(args, "--force") >= 0));
                    case "--uninstall-statusline":
                        ClaudeIntegration.Uninstall();
                        return Report("ok");
                    case "--diagnose":
                        return Diagnose(args);
                    case "--write-icon":
                        IconFactory.WriteIco(args.Length > 1 ? args[1] : "app.ico");
                        return 0;
                }
            }

            bool created;
            using (var mutex = new Mutex(true, @"Local\AIUsageTracker.Instance", out created))
            using (var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AIUsageTracker.Show"))
            {
                if (!created)
                {
                    // 이미 실행 중이면 기존 창을 띄우고 종료
                    showSignal.Set();
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new WidgetForm(showSignal));
            }
            return 0;
        }

        // 위젯과 같은 코드로 각 서비스를 한 번 조회해 결과를 글로 출력한다 (문제 확인용)
        static int Diagnose(string[] args)
        {
            L.Load(new Settings());
            int langIndex = Array.IndexOf(args, "--lang");
            if (langIndex >= 0 && langIndex + 1 < args.Length) L.Lang = args[langIndex + 1];

            var sb = new System.Text.StringBuilder();
            foreach (Provider p in new Provider[] { new ClaudeProvider(), new CodexProvider(), new AntigravityProvider() })
            {
                ServiceStatus s;
                try { s = p.Fetch(); }
                catch (Exception ex)
                {
                    sb.AppendLine("[" + p.Name + "] EXCEPTION " + ex.Message);
                    continue;
                }
                sb.Append("[" + s.Name + "]");
                if (s.LoginCommand != null) sb.Append(" " + L.T("로그인 필요"));
                else
                {
                    if (!string.IsNullOrEmpty(s.Badge)) sb.Append(" " + s.Badge);
                    if (s.UpdatedAt.HasValue) sb.Append(" · " + Fmt.Ago(s.UpdatedAt.Value));
                }
                sb.AppendLine();
                string group = null;
                foreach (var w in s.Windows)
                {
                    if (w.Group != group)
                    {
                        group = w.Group;
                        if (group != null) sb.AppendLine("  [" + group + "]");
                    }
                    sb.AppendLine((group != null ? "    " : "  ") + w.Label + "  " + (w.Percent.HasValue ? Math.Round(w.Percent.Value) + "%" : "–") + "  "
                        + (w.Note ?? (w.ResetsAt.HasValue ? Fmt.Until(w.ResetsAt.Value) : "")));
                }
                if (s.LoginCommand != null) sb.AppendLine("  " + L.F("로그인: 터미널에서 {0} 실행", s.LoginCommand));
                foreach (var line in s.Lines) sb.AppendLine("  " + line);
                if (s.Error != null) sb.AppendLine("  ! " + s.Error);
            }
            try
            {
                var bytes = new System.Text.UTF8Encoding(false).GetBytes(sb.ToString());
                using (var stdout = Console.OpenStandardOutput()) stdout.Write(bytes, 0, bytes.Length);
            }
            catch { }
            return 0;
        }

        static int Report(string result)
        {
            try
            {
                using (var stdout = Console.OpenStandardOutput())
                using (var w = new StreamWriter(stdout))
                    w.WriteLine(result == "foreign" ? "다른 statusLine이 이미 있습니다. 교체하려면 --force" : result);
            }
            catch { }
            return result == "ok" ? 0 : 1;
        }
    }

    static class IconFactory
    {
        public static Bitmap Render(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float r = size * 0.22f, m = size * 0.04f, s = size - 2 * m;
                using (var path = new GraphicsPath())
                {
                    path.AddArc(m, m, 2 * r, 2 * r, 180, 90);
                    path.AddArc(m + s - 2 * r, m, 2 * r, 2 * r, 270, 90);
                    path.AddArc(m + s - 2 * r, m + s - 2 * r, 2 * r, 2 * r, 0, 90);
                    path.AddArc(m, m + s - 2 * r, 2 * r, 2 * r, 90, 90);
                    path.CloseFigure();
                    using (var bg = new SolidBrush(Color.FromArgb(32, 33, 36))) g.FillPath(bg, path);
                }
                var colors = new[] { Color.FromArgb(217, 119, 87), Color.FromArgb(16, 163, 127), Color.FromArgb(66, 133, 244) };
                var lengths = new[] { 0.78f, 0.55f, 0.35f };
                float barH = size * 0.13f, gap = size * 0.08f, x = size * 0.2f, maxW = size * 0.6f;
                float y = (size - (3 * barH + 2 * gap)) / 2;
                for (int i = 0; i < 3; i++)
                {
                    using (var b = new SolidBrush(colors[i]))
                        g.FillRectangle(b, x, y, Math.Max(1, maxW * lengths[i]), barH);
                    y += barH + gap;
                }
            }
            return bmp;
        }

        public static Icon Create(int size)
        {
            using (var bmp = Render(size)) return Icon.FromHandle(bmp.GetHicon());
        }

        // PNG 프레임을 담은 .ico 작성 (Vista 이상 지원)
        public static void WriteIco(string path)
        {
            var sizes = new[] { 16, 24, 32, 48, 64, 256 };
            var images = new List<byte[]>();
            foreach (int size in sizes)
                using (var bmp = Render(size))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    images.Add(ms.ToArray());
                }
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write((short)0);
                w.Write((short)1);
                w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0);
                    w.Write((byte)0);
                    w.Write((short)1);
                    w.Write((short)32);
                    w.Write(images[i].Length);
                    w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (var img in images) w.Write(img);
            }
        }
    }
}
