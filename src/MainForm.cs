using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DshLauncher
{
    class MainForm : Form
    {
        // 布局网格(逻辑像素, 4px 基准)
        const int PadL = Tokens.Sp6;      // 24 外边距
        const int WinW = 780, WinH = 542;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        Rectangle rcStatus, rcLog, rcFooter, rcConsole;
        Label lblTitle, lblVer, lblStatusMain, lblStatusInfo, lblLogTitle, lblFooter, lblLogEmpty;
        LinkLabel lnkOpen;
        StatusDot dot;
        UiButton btnStart, btnOpen, btnRestart, btnExit, btnCheck, btnUpdate, btnLog, btnCopy;
        RichTextBox logBox;
        ToolTip tip;

        System.Windows.Forms.Timer tick;
        int frames;
        float pulsePhase;

        volatile bool backendReady = true;   // 后端探测是否就绪
        string backendDetail = "";

        volatile bool busy;
        volatile bool logHold;      // 更新过程中冻结日志框(不让 web 日志覆盖流式输出)
        string busyText = "";

        bool running;
        int wslPid = -1;
        string shownLog = "";
        int refreshLock;

        public MainForm()
        {
            Text = "DSH 启动器 " + Program.AppVersion;

            try
            {
                using (var screen = Graphics.FromHwnd(IntPtr.Zero)) Program.UiScale = screen.DpiX / 96f;
            }
            catch { Program.UiScale = 1f; }
            if (Program.UiScale < 1f) Program.UiScale = 1f;

            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Program.S(WinW), Program.S(WinH));
            Program.Log("[GUI] DPI 缩放=" + Program.UiScale.ToString("0.##") + "x, 窗口客户区="
                + ClientSize.Width + "x" + ClientSize.Height);

            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            DoubleBuffered = true;
            BackColor = Tokens.Bg;
            ForeColor = Tokens.TextPrimary;
            Font = new Font("Microsoft YaHei UI", Tokens.FsBody);

            try { Icon = new Icon(Path.Combine(Program.BaseDir, "dsh.ico")); } catch { }

            bool smoke = Environment.GetEnvironmentVariable("DSH_SMOKE") == "1";
            if (smoke)
            {
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-30000, -30000);
                ShowInTaskbar = false;
            }

            tip = new ToolTip();
            tip.AutoPopDelay = 8000;
            tip.InitialDelay = 400;

            // ---- 布局网格 ----
            int contentW = Program.S(WinW) - 2 * Program.S(PadL);
            int y = Program.S(Tokens.Sp5);                       // 20
            int headerH = Program.S(28);
            rcStatus = new Rectangle(Program.S(PadL), y + headerH + Program.S(Tokens.Sp3), contentW, Program.S(84));
            int rowA = rcStatus.Bottom + Program.S(Tokens.Sp4);
            int rowB = rowA + Program.S(48) + Program.S(Tokens.Sp3);
            rcLog = new Rectangle(Program.S(PadL), rowB + Program.S(44) + Program.S(Tokens.Sp4), contentW, Program.S(190));
            rcFooter = new Rectangle(Program.S(PadL), rcLog.Bottom + Program.S(Tokens.Sp4), contentW, Program.S(32));

            // ---- 页头: 标题 + 版本信息 ----
            lblTitle = MakeLabel("lblTitle", "DSH 启动器", rcStatus.X, y,
                new Font("Microsoft YaHei UI", Tokens.FsTitle, FontStyle.Bold), Tokens.TextPrimary, ContentAlignment.MiddleLeft);
            lblTitle.Size = new Size(Program.S(320), headerH);

            lblVer = MakeLabel("lblVer", Program.AppVersion + "   ·   " + Program.Backend.Label + "   ·   端口 " + Program.Port,
                rcStatus.Right - Program.S(420), y + Program.S(4),
                new Font("Microsoft YaHei UI", Tokens.FsCaption), Tokens.TextTertiary, ContentAlignment.MiddleRight);
            lblVer.Size = new Size(Program.S(420), Program.S(20));

            // ---- 状态卡片 ----
            dot = new StatusDot();
            dot.Name = "dot";
            dot.Location = new Point(rcStatus.X + Program.S(Tokens.Sp5), rcStatus.Y + Program.S(Tokens.Sp5));
            Controls.Add(dot);

            lblStatusMain = MakeLabel("lblStatusMain", "", rcStatus.X + Program.S(48), rcStatus.Y + Program.S(Tokens.Sp4),
                new Font("Microsoft YaHei UI", Tokens.FsHead, FontStyle.Bold), Tokens.TextPrimary, ContentAlignment.MiddleLeft);
            lblStatusMain.Size = new Size(Program.S(400), Program.S(24));
            lblStatusMain.BackColor = Tokens.Surface;

            lblStatusInfo = MakeLabel("lblStatusInfo", "", rcStatus.X + Program.S(48), rcStatus.Y + Program.S(46),
                new Font("Microsoft YaHei UI", Tokens.FsCaption), Tokens.TextSecondary, ContentAlignment.MiddleLeft);
            lblStatusInfo.Size = new Size(Program.S(660), Program.S(20));
            lblStatusInfo.BackColor = Tokens.Surface;

            lnkOpen = new LinkLabel();
            lnkOpen.Name = "lnkOpen";
            lnkOpen.Text = "在浏览器打开";
            lnkOpen.AutoSize = false;
            lnkOpen.Size = new Size(Program.S(150), Program.S(24));
            lnkOpen.Location = new Point(rcStatus.Right - Program.S(Tokens.Sp5) - Program.S(150), rcStatus.Y + Program.S(Tokens.Sp4));
            lnkOpen.TextAlign = ContentAlignment.MiddleRight;
            lnkOpen.LinkColor = Tokens.AccentText;
            lnkOpen.ActiveLinkColor = Color.White;
            lnkOpen.LinkBehavior = LinkBehavior.HoverUnderline;
            lnkOpen.Font = new Font("Microsoft YaHei UI", Tokens.FsCaption);
            lnkOpen.BackColor = Tokens.Surface;
            lnkOpen.TabStop = false;
            lnkOpen.Click += (s, e) => OpenWebUi();
            Controls.Add(lnkOpen);

            // ---- 主行动行: 启动 / 打开 Web UI(同一时刻只有一个 accent) ----
            int pairW = (contentW - Program.S(Tokens.Sp3)) / 2;
            btnStart = MakeBtn("btnStart", "启 动 DSH", IconKind.Play, BtnVariant.Primary, BtnStart_Click,
                new Point(rcStatus.X, rowA), pairW, Program.S(48), "启动 WSL 内的 DSH 服务");
            btnOpen = MakeBtn("btnOpen", "打开 Web UI", IconKind.Globe, BtnVariant.Secondary, BtnOpen_Click,
                new Point(rcStatus.X + pairW + Program.S(Tokens.Sp3), rowA), pairW, Program.S(48),
                "在浏览器打开 Web UI(未运行则自动启动)");

            // ---- 次行动行 ----
            int thirdW = (contentW - 2 * Program.S(Tokens.Sp3)) / 3;
            btnRestart = MakeBtn("btnRestart", "一键重启", IconKind.Refresh, BtnVariant.Secondary, BtnRestart_Click,
                new Point(rcStatus.X, rowB), thirdW, Program.S(44), "停止并重新启动 DSH");
            btnCheck = MakeBtn("btnCheck", "检查更新", IconKind.Search, BtnVariant.Secondary, BtnCheck_Click,
                new Point(rcStatus.X + thirdW + Program.S(Tokens.Sp3), rowB), thirdW, Program.S(44),
                "联网检查 DSH 是否有新版本");
            btnUpdate = MakeBtn("btnUpdate", "更新 DSH", IconKind.Download, BtnVariant.Secondary, BtnUpdate_Click,
                new Point(rcStatus.X + 2 * (thirdW + Program.S(Tokens.Sp3)), rowB), thirdW, Program.S(44),
                "停止服务 → 安装最新版 → 自动重启");

            // ---- 日志卡片 ----
            lblLogTitle = MakeLabel("lblLogTitle", "运行日志", rcLog.X + Program.S(Tokens.Sp4), rcLog.Y + Program.S(Tokens.Sp3),
                new Font("Microsoft YaHei UI", Tokens.FsBody, FontStyle.Bold), Tokens.TextSecondary, ContentAlignment.MiddleLeft);
            lblLogTitle.Size = new Size(Program.S(200), Program.S(22));
            lblLogTitle.BackColor = Tokens.Surface;

            btnCopy = MakeBtn("btnCopy", "复制", IconKind.Copy, BtnVariant.Ghost, BtnCopy_Click,
                new Point(rcLog.Right - Program.S(Tokens.Sp4) - Program.S(72), rcLog.Y + Program.S(Tokens.Sp2)),
                Program.S(72), Program.S(28), "复制日志内容到剪贴板");
            btnCopy.Font = new Font("Microsoft YaHei UI", Tokens.FsCaption);
            btnCopy.BackColor = Tokens.Surface;   // 幽灵按钮透出的底色要跟所在卡片一致, 否则会出现深色方块

            logBox = new RichTextBox();
            logBox.Name = "logBox";
            logBox.BorderStyle = BorderStyle.None;
            logBox.ReadOnly = true;
            logBox.TabStop = false;
            logBox.BackColor = Tokens.Console;
            logBox.ForeColor = Color.FromArgb(0xB9, 0xC2, 0xCF);
            logBox.Font = new Font("Consolas", Tokens.FsMono);
            logBox.ScrollBars = RichTextBoxScrollBars.Vertical;
            logBox.HideSelection = false;
            rcConsole = new Rectangle(rcLog.X + Program.S(Tokens.Sp4), rcLog.Y + Program.S(44),
                rcLog.Width - Program.S(2 * Tokens.Sp4), rcLog.Height - Program.S(44) - Program.S(Tokens.Sp4));
            logBox.Location = new Point(rcConsole.X + Program.S(Tokens.Sp3), rcConsole.Y + Program.S(Tokens.Sp3));
            logBox.Size = new Size(rcConsole.Width - Program.S(2 * Tokens.Sp3), rcConsole.Height - Program.S(2 * Tokens.Sp3));
            Controls.Add(logBox);

            // 空状态: 日志无内容时给出可读的占位, 而不是一片黑
            lblLogEmpty = MakeLabel("lblLogEmpty", "暂无日志输出 · 服务启动后这里会显示 ~/.dsh-web.log 的尾部",
                rcConsole.X, rcConsole.Y + rcConsole.Height / 2 - Program.S(Tokens.Sp3),
                new Font("Microsoft YaHei UI", Tokens.FsCaption), Tokens.TextTertiary, ContentAlignment.TopCenter);
            lblLogEmpty.Size = new Size(rcConsole.Width, Program.S(20));
            lblLogEmpty.BackColor = Tokens.Console;
            lblLogEmpty.Visible = true;
            lblLogEmpty.BringToFront();

            // ---- 页脚: 低频操作 + 最近一次动作 ----
            lblFooter = MakeLabel("lblFooter", "就绪", rcFooter.X, rcFooter.Y,
                new Font("Microsoft YaHei UI", Tokens.FsCaption), Tokens.TextTertiary, ContentAlignment.MiddleLeft);
            lblFooter.Size = new Size(Program.S(360), rcFooter.Height);

            btnLog = MakeBtn("btnLog", "打开日志目录", IconKind.Folder, BtnVariant.Ghost,
                (s, e) => { try { Process.Start("explorer.exe", Program.LogDir); } catch { } },
                new Point(rcFooter.Right - Program.S(228), rcFooter.Y), Program.S(132), rcFooter.Height,
                "打开日志所在目录");
            btnLog.Font = new Font("Microsoft YaHei UI", Tokens.FsCaption);
            btnExit = MakeBtn("btnExit", "退 出", IconKind.Close, BtnVariant.Ghost, (s, e) => Close(),
                new Point(rcFooter.Right - Program.S(88), rcFooter.Y), Program.S(88), rcFooter.Height,
                "关闭窗口 (不停止 DSH)");
            btnExit.Font = new Font("Microsoft YaHei UI", Tokens.FsCaption);

            tick = new System.Windows.Forms.Timer();
            tick.Interval = 50;
            tick.Tick += OnTick;
            tick.Start();

            Shown += (s, e) =>
            {
                Program.Log("[GUI] 启动器已打开 (环境=" + Program.Backend.Label + ")");
                ProbeBackendAsync();
                RequestRefresh();
                LoadVersionAsync();
                if (smoke)
                {
                    var st = new System.Windows.Forms.Timer();
                    st.Interval = 2500;
                    st.Tick += (s2, e2) =>
                    {
                        st.Stop(); st.Dispose();
                        DumpUi();
                        try { File.WriteAllText(Path.Combine(Program.LogDir, "smoke-ok.txt"),
                            "ok " + DateTime.Now.ToString("HH:mm:ss")); } catch { }
                        Close();
                    };
                    st.Start();
                }
            };
            FormClosed += (s, e) => { tick.Stop(); tick.Dispose(); };
        }

        // 深色标题栏(纯视觉; 失败静默, 不影响任何功能)
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(Handle, 19, ref on, sizeof(int));
            }
            catch { }
        }

        Label MakeLabel(string name, string text, int x, int y, Font f, Color c, ContentAlignment align)
        {
            var l = new Label();
            l.Name = name;
            l.Text = text;
            l.Location = new Point(x, y);
            l.Font = f;
            l.ForeColor = c;
            l.BackColor = Tokens.Bg;
            l.AutoSize = false;
            l.TextAlign = align;
            l.TabStop = false;
            Controls.Add(l);
            return l;
        }

        UiButton MakeBtn(string name, string text, IconKind icon, BtnVariant variant, EventHandler onClick,
            Point loc, int w, int h, string hint)
        {
            var b = new UiButton(name, text, icon, variant, onClick);
            b.Location = loc;
            b.Size = new Size(w, h);
            if (!string.IsNullOrEmpty(hint)) tip.SetToolTip(b, hint);
            Controls.Add(b);
            return b;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawCard(g, rcStatus);
            DrawCard(g, rcLog);
            // 日志控制台容器: 让黑色区域读起来是"终端", 而不是一块空洞
            using (var p = Tokens.Round(rcConsole, Program.S(Tokens.RSm)))
            {
                using (var b = new SolidBrush(Tokens.Console)) g.FillPath(b, p);
                using (var pen = new Pen(Tokens.Divider, Math.Max(1f, Program.UiScale))) g.DrawPath(pen, p);
            }
            // 日志卡片内的分隔线, 让标题区与内容区分离
            using (var pen = new Pen(Tokens.Divider, Math.Max(1f, Program.UiScale)))
            {
                int ly = rcLog.Y + Program.S(40);
                g.DrawLine(pen, rcLog.X + 1, ly, rcLog.Right - 1, ly);
            }
        }

        void DrawCard(Graphics g, Rectangle r)
        {
            using (var p = Tokens.Round(r, Program.S(Tokens.RLg)))
            {
                using (var b = new SolidBrush(Tokens.Surface)) g.FillPath(b, p);
                using (var pen = new Pen(Tokens.Border, Math.Max(1f, Program.UiScale))) g.DrawPath(pen, p);
            }
        }

        void OnTick(object s, EventArgs e)
        {
            frames++;
            if (busy)
            {
                pulsePhase += 0.09f;
                if (pulsePhase > 1f) pulsePhase -= 1f;
                dot.Phase = pulsePhase;
                dot.Invalidate();
            }
            if (frames % 60 == 0) RequestRefresh();
        }

        void ProbeBackendAsync()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                BackendProbe p;
                try { p = Program.Backend.Probe(); }
                catch (Exception ex) { p = new BackendProbe(); p.Detail = ex.Message; }
                try
                {
                    if (IsHandleCreated) BeginInvoke((MethodInvoker)delegate
                    {
                        backendReady = p.Available;
                        backendDetail = p.Detail;
                        Program.Log("[PROBE] " + (p.Available ? "就绪" : "不可用") + " — " + p.Detail);
                        RenderStatus();
                    });
                }
                catch { }
            });
        }

        void LoadVersionAsync()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (backendReady) Program.GetInstalledVersion(true);
                    if (IsHandleCreated) BeginInvoke((MethodInvoker)delegate { RenderStatus(); });
                }
                catch { }
            });
        }

        void RequestRefresh()
        {
            if (!IsHandleCreated) return;
            if (Interlocked.CompareExchange(ref refreshLock, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    bool up = Proc.PortUp(Program.Port);
                    int pid = -1;
                    if (up) pid = Program.Backend.GetListenerPid();
                    Program.Backend.SyncLog();
                    string tail = Proc.Tail(Program.LogCopyPath, 80);
                    BeginInvoke((MethodInvoker)delegate { ApplyState(up, pid, tail); });
                }
                finally { Interlocked.Exchange(ref refreshLock, 0); }
            });
        }

        void ApplyState(bool up, int pid, string tail)
        {
            running = up;
            wslPid = pid;
            if (!logHold && tail != shownLog)
            {
                shownLog = tail;
                SetLog(tail ?? "");
            }
            RenderStatus();
        }

        void RenderStatus()
        {
            if (busy)
            {
                dot.DotColor = Tokens.Warning;
                dot.Busy = true;
                lblStatusMain.Text = busyText;
                lblStatusMain.ForeColor = Tokens.Warning;
                lblStatusInfo.Text = "操作进行中, 请稍候…";
                lnkOpen.Visible = false;
                SetActionsEnabled(false);
            }
            else if (!backendReady)
            {
                dot.DotColor = Tokens.Warning;
                dot.Busy = false;
                lblStatusMain.Text = "DSH 未就绪";
                lblStatusMain.ForeColor = Tokens.Warning;
                lblStatusInfo.Text = backendDetail;
                lnkOpen.Visible = false;
                SetActionsEnabled(true);
                btnStart.Enabled = false;
                btnOpen.Enabled = false;
                btnRestart.Enabled = false;
                btnStart.Variant = BtnVariant.Secondary;
                btnOpen.Variant = BtnVariant.Secondary;
            }
            else if (running)
            {
                dot.DotColor = Tokens.Success;
                dot.Busy = false;
                lblStatusMain.Text = "运行中";
                lblStatusMain.ForeColor = Tokens.SuccessText;
                string info = (wslPid > 0 ? "PID " + wslPid + "    ·    " : "") + Program.DshUrl;
                if (Program.InstalledVersion.Length > 0) info += "    ·    DSH " + Program.InstalledVersion;
                if (Program.UpdateAvailable()) info += "    ·    可更新到 " + Program.LatestVersion;
                lblStatusInfo.Text = info;
                lnkOpen.Visible = true;
                SetActionsEnabled(true);
                btnStart.Enabled = false;
                // 运行时「打开 Web UI」是主行动, 「启动」让位
                btnOpen.Variant = BtnVariant.Primary;
                btnStart.Variant = BtnVariant.Secondary;
            }
            else
            {
                dot.DotColor = Tokens.Danger;
                dot.Busy = false;
                lblStatusMain.Text = "未运行";
                lblStatusMain.ForeColor = Tokens.Danger;
                string info = "点「启 动 DSH」启动服务, 或点「打开 Web UI」自动启动    ·    " + Program.Backend.Label;
                if (Program.InstalledVersion.Length > 0) info += "    ·    DSH " + Program.InstalledVersion;
                lblStatusInfo.Text = info;
                lnkOpen.Visible = false;
                SetActionsEnabled(true);
                btnStart.Enabled = true;
                // 停服时「启 动 DSH」是主行动
                btnStart.Variant = BtnVariant.Primary;
                btnOpen.Variant = BtnVariant.Secondary;
            }
            dot.Invalidate();
        }

        void SetActionsEnabled(bool on)
        {
            btnStart.Enabled = on;
            btnOpen.Enabled = on;
            btnRestart.Enabled = on;
            btnCheck.Enabled = on;
            btnUpdate.Enabled = on;
            btnLog.Enabled = on;
            btnExit.Enabled = on;
        }

        void SetBusy(bool b, string text)
        {
            busy = b;
            busyText = text ?? "";
            RenderStatus();
        }

        void SetFooter(string text)
        {
            lblFooter.Text = text + "    ·    " + DateTime.Now.ToString("HH:mm:ss");
        }

        void SetLog(string text)
        {
            logBox.Text = text;
            int idx = 0;
            foreach (string line in logBox.Lines)
            {
                if (IsErrorLine(line))
                {
                    logBox.Select(idx, line.Length);
                    logBox.SelectionColor = Color.FromArgb(242, 116, 124);
                }
                idx += line.Length + 1;
            }
            logBox.SelectionStart = logBox.TextLength;
            logBox.ScrollToCaret();
            if (lblLogEmpty != null) lblLogEmpty.Visible = logBox.TextLength == 0;
        }

        void AppendLog(string line)
        {
            logBox.AppendText(line + "\r\n");
            logBox.SelectionStart = logBox.TextLength;
            logBox.ScrollToCaret();
            if (lblLogEmpty != null) lblLogEmpty.Visible = logBox.TextLength == 0;
        }

        void DumpUi()
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("{\"form\":{\"w\":" + ClientSize.Width + ",\"h\":" + ClientSize.Height
                    + ",\"version\":\"" + Program.AppVersion + "\"},\"controls\":[");
                bool first = true;
                foreach (Control c in Controls)
                {
                    if (!first) sb.Append(",");
                    first = false;
                    string txt = Program.Esc(c.Text);
                    sb.Append("{\"name\":\"" + c.Name + "\",\"type\":\"" + c.GetType().Name
                        + "\",\"text\":\"" + txt + "\",\"x\":" + c.Left + ",\"y\":" + c.Top
                        + ",\"w\":" + c.Width + ",\"h\":" + c.Height
                        + ",\"enabled\":" + (c.Enabled ? "true" : "false")
                        + ",\"visible\":" + (c.Visible ? "true" : "false") + "}");
                }
                sb.Append("]}");
                File.WriteAllText(Path.Combine(Program.LogDir, "ui-dump.json"), sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Program.Log("[SMOKE] ui dump 失败: " + ex.Message); }
        }

        static bool IsErrorLine(string l)
        {
            string low = l.ToLowerInvariant();
            return low.Contains("error") || low.Contains("fail") || low.Contains("exception")
                || l.Contains("错误") || l.Contains("失败");
        }

        void Msg(string text, string title, MessageBoxIcon icon)
        {
            MessageBox.Show(this, text, title, MessageBoxButtons.OK, icon);
        }

        // 打开 Web UI: 已在跑就直接开浏览器, 没跑就先启动再开
        void OpenWebUi()
        {
            if (busy) return;
            if (Proc.PortUp(Program.Port))
            {
                Program.Log("[OPEN] 打开 " + Program.DshUrl);
                Program.OpenInChrome(Program.ResolveOpenUrl());
                SetFooter("已打开浏览器");
                return;
            }
            DoStart("启动并打开 Web UI");
        }

        void DoStart(string title)
        {
            if (busy) return;
            if (Proc.PortUp(Program.Port)) { Program.OpenInChrome(Program.ResolveOpenUrl()); return; }
            SetBusy(true, "启动中…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                string r = Program.StartDsh();
                BeginInvoke((MethodInvoker)delegate
                {
                    SetBusy(false, null);
                    if (r == "ok")
                    {
                        Program.OpenInChrome(Program.ResolveOpenUrl());
                        SetFooter("已启动");
                        Msg(title + " 成功!\n地址: " + Program.DshUrl + "\n已自动在浏览器打开", "成功", MessageBoxIcon.Information);
                    }
                    else
                    {
                        SetFooter("启动失败");
                        Msg(title + " 失败!\n\nDSH 日志已写入:\n" + Program.LogCopyPath +
                            "\n\n可点「打开日志目录」查看 dsh-web.log。", "失败", MessageBoxIcon.Error);
                    }
                    RequestRefresh();
                });
            });
        }

        void BtnStart_Click(object sender, EventArgs e)
        {
            if (busy) return;
            if (Proc.PortUp(Program.Port))
            {
                Msg("DSH 已在运行!\n\n点「打开 Web UI」可直接打开浏览器。", "提示", MessageBoxIcon.Information);
                return;
            }
            DoStart("DSH 启动");
        }

        void BtnOpen_Click(object sender, EventArgs e)
        {
            OpenWebUi();
        }

        void BtnRestart_Click(object sender, EventArgs e)
        {
            if (busy) return;
            SetBusy(true, "重启中…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                Program.Log("[RESTART] ============ 一键重启 ============");
                Program.StopDsh();
                Thread.Sleep(1000);
                string r = Program.StartDsh();
                BeginInvoke((MethodInvoker)delegate
                {
                    SetBusy(false, null);
                    if (r == "ok")
                    {
                        Program.OpenInChrome(Program.ResolveOpenUrl());
                        SetFooter("已重启");
                        Msg("DSH 重启成功!\n地址: " + Program.DshUrl + "\n已自动在浏览器打开", "成功", MessageBoxIcon.Information);
                    }
                    else
                    {
                        SetFooter("重启失败");
                        Msg("DSH 重启失败!\n\nDSH 日志已写入:\n" + Program.LogCopyPath +
                            "\n\n可点「打开日志目录」查看 dsh-web.log。", "重启失败", MessageBoxIcon.Error);
                    }
                    RequestRefresh();
                });
            });
        }

        void BtnCheck_Click(object sender, EventArgs e)
        {
            if (busy) return;
            SetBusy(true, "检查更新中…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                Program.Log("[CHECK] ============ 检查更新 ============");
                string inst, latest, err;
                int st = Program.CheckUpdate(out inst, out latest, out err, null);
                BeginInvoke((MethodInvoker)delegate
                {
                    SetBusy(false, null);
                    RequestRefresh();
                    if (st == 0)
                    {
                        SetFooter("检查更新失败");
                        Msg("检查更新失败\n\n" + err + "\n\n可点「打开日志目录」查看日志。", "检查更新", MessageBoxIcon.Warning);
                    }
                    else if (st == 1)
                    {
                        SetFooter("发现新版本 " + latest);
                        Msg("发现新版本!\n\n当前版本: " + inst + "\n最新版本: " + latest
                            + "\n\n点「更新 DSH」即可一键升级(会自动停止并重启服务)。", "有更新", MessageBoxIcon.Information);
                    }
                    else
                    {
                        SetFooter("已是最新版本 " + inst);
                        Msg("已是最新版本\n\n当前版本: " + inst + "\n最新版本: " + latest, "已最新", MessageBoxIcon.Information);
                    }
                });
            });
        }

        void BtnUpdate_Click(object sender, EventArgs e)
        {
            if (busy) return;
            var dr = MessageBox.Show(this,
                "更新 DSH 会:\n\n  1. 停止当前 DSH 服务\n  2. 安装最新版 @deepseek-ai/dsh\n  3. 自动重新启动并打开 Web UI\n\n"
                + "期间 Web UI 会短暂不可用(当前页面会断开)。确定继续吗?",
                "确认更新", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (dr != DialogResult.OK) return;

            SetBusy(true, "更新中…");
            logHold = true;
            SetLog("=== 开始更新 DSH ===\r\n");
            ThreadPool.QueueUserWorkItem(delegate
            {
                Program.Log("[UPDATE] ============ 一键更新 DSH ============");
                string before = Program.GetInstalledVersion(true);
                Program.StopDsh();
                Thread.Sleep(800);
                string r = Program.RunUpdate(delegate(string line)
                {
                    try { if (IsHandleCreated) BeginInvoke((MethodInvoker)delegate { AppendLog(line); }); }
                    catch { }
                });
                string after = Program.InstalledVersion;
                string startRes = "skipped";
                if (r == "ok") startRes = Program.StartDsh();
                BeginInvoke((MethodInvoker)delegate
                {
                    AppendLog("=== 更新结束: " + r + " ===");
                    logHold = false;
                    shownLog = "";
                    SetBusy(false, null);
                    if (r == "ok")
                    {
                        if (startRes == "ok") Program.OpenInChrome(Program.ResolveOpenUrl());
                        SetFooter("已更新到 " + after);
                        Msg("更新成功!\n\n" + (before.Length > 0 ? before : "?") + "  →  " + (after.Length > 0 ? after : "?")
                            + "\n\n服务状态: " + (startRes == "ok" ? "已重启, 已打开 Web UI" : "重启失败, 请点「一键重启」"),
                            "更新成功", MessageBoxIcon.Information);
                    }
                    else
                    {
                        SetFooter("更新失败");
                        Msg("更新失败!\n\n详细输出见日志框, 也可点「打开日志目录」。\n\n当前服务状态: "
                            + (Proc.PortUp(Program.Port) ? "运行中" : "未运行 (可点「启 动 DSH」)"),
                            "更新失败", MessageBoxIcon.Error);
                    }
                    RequestRefresh();
                });
            });
        }

        void BtnCopy_Click(object sender, EventArgs e)
        {
            try
            {
                Clipboard.SetText(logBox.Text);
                btnCopy.Text = "已复制";
                var t = new System.Windows.Forms.Timer();
                t.Interval = 1200;
                t.Tick += (s2, e2) => { t.Stop(); t.Dispose(); btnCopy.Text = "复制"; };
                t.Start();
            }
            catch { }
        }
    }

}
