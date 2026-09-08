using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace DshLauncher
{
    /// <summary>
    /// 入口。CLI 模式(--probe/--start/... 写 logs\selftest.json)与 GUI 模式共用同一套后端。
    /// 运行环境(WSL 发行版 / Windows 原生)由 LauncherConfig + 自动探测决定。
    /// </summary>
    static class Program
    {
        /// <summary>exe 所在目录。</summary>
        public static string BaseDir;
        /// <summary>日志目录(exe 同级的 logs)。</summary>
        public static string LogDir;
        /// <summary>启动器自身日志。</summary>
        public static string LauncherLog;
        /// <summary>界面显示的 DSH 日志副本。</summary>
        public static string LogCopyPath;
        /// <summary>解析后的配置。</summary>
        public static LauncherConfig Config;
        /// <summary>选中的运行环境后端。</summary>
        public static IBackend Backend;

        public static int Port { get { return Config.Port; } }
        public static string DshUrl { get { return "http://127.0.0.1:" + Port; } }

        // ---- DPI 缩放 ----
        // 字体以磅为单位, 在高 DPI 屏上会自动放大; 手工排版的像素盒子必须同比放大,
        // 否则文字会被盒子裁掉下半截(200% 缩放下表现为整屏文字只剩上半截)。
        public static float UiScale = 1f;
        public static int S(float v) { return (int)Math.Round(v * UiScale); }

        // ---- 版本状态 ----
        public static string InstalledVersion = "";
        public static string LatestVersion = "";
        static DateTime installedAt = DateTime.MinValue;
        static readonly object verLock = new object();

        [STAThread]
        static void Main(string[] args)
        {
            BaseDir = AppDomain.CurrentDomain.BaseDirectory;
            LogDir = Path.Combine(BaseDir, "logs");
            LauncherLog = Path.Combine(LogDir, "launcher.log");
            LogCopyPath = Path.Combine(LogDir, "dsh-web.log");
            Directory.CreateDirectory(LogDir);

            Config = LauncherConfig.Load(BaseDir);

            string mode = args.Length > 0 ? args[0] : "";
            if (mode.StartsWith("--") || mode == "-h" || mode == "/?")
            {
                try { Console.OutputEncoding = Encoding.UTF8; } catch { }   // 控制台输出用 UTF-8, 避免中文乱码
            }
            if (mode == "--help" || mode == "-h" || mode == "/?")
            {
                Console.WriteLine(HelpText);
                return;
            }
            if (mode == "--detect")
            {
                string note;
                IBackend b = BackendFactory.Create(Config, out note);
                BackendProbe p = b.Probe();
                Console.WriteLine("环境: " + b.Label);
                Console.WriteLine("探测: " + (p.Available ? "就绪" : "不可用") + " — " + p.Detail);
                Console.WriteLine(note);
                return;
            }

            string detectNote;
            Backend = BackendFactory.Create(Config, out detectNote);
            Log("[INIT] 版本 " + AppVersion + " · 环境 " + Backend.Label + " · 端口 " + Port
                + " · 配置 " + (File.Exists(Config.ConfigPath) ? Config.ConfigPath : "(无 launcher.json)"));
            Log("[INIT] " + detectNote);

            if (mode == "--selftest") { SelfTest(); return; }
            if (mode == "--probe")
            {
                WriteJson("{\"action\":\"probe\",\"env\":\"" + Esc(Backend.Label) + "\",\"port\":" + Port
                    + ",\"running\":" + Bool(Proc.PortUp(Port)) + ",\"url\":\"" + DshUrl
                    + "\",\"openUrl\":\"" + Esc(ResolveOpenUrl()) + "\",\"pid\":" + Backend.GetListenerPid() + "}");
                return;
            }
            if (mode == "--start") { WriteSimpleResult("start", StartDsh()); return; }
            if (mode == "--stop") { WriteSimpleResult("stop", StopDsh() ? "ok" : "failed"); return; }
            if (mode == "--restart")
            {
                Log("[RESTART] ============ 一键重启 ============");
                StopDsh();
                Thread.Sleep(1000);
                WriteSimpleResult("restart", StartDsh());
                return;
            }
            if (mode == "--open")
            {
                Log("[OPEN] ============ 打开 Web UI ============");
                string r = Proc.PortUp(Port) ? "ok" : StartDsh();
                if (r == "ok") OpenInChrome(ResolveOpenUrl());
                WriteSimpleResult("open", r);
                return;
            }
            if (mode == "--check-update")
            {
                Log("[CHECK] ============ 检查更新 ============");
                string inst, latest, err;
                int st = CheckUpdate(out inst, out latest, out err, null);
                Log("[CHECK] 当前=" + inst + " 最新=" + latest + " 状态=" + st + (err.Length > 0 ? " err=" + err : ""));
                WriteJson("{\"action\":\"check-update\",\"env\":\"" + Esc(Backend.Label) + "\",\"installed\":\"" + Esc(inst)
                    + "\",\"latest\":\"" + Esc(latest) + "\",\"state\":" + st + ",\"update\":" + Bool(st == 1)
                    + ",\"error\":\"" + Esc(err) + "\"}");
                return;
            }
            if (mode == "--update")
            {
                Log("[UPDATE] ============ 一键更新 ============");
                StopDsh();
                Thread.Sleep(1000);
                string r = RunUpdate(delegate(string l) { Log("[UPDATE] " + l); });
                string sr = "skipped";
                if (r == "ok") sr = StartDsh();
                WriteJson("{\"action\":\"update\",\"env\":\"" + Esc(Backend.Label) + "\",\"result\":\"" + r
                    + "\",\"start\":\"" + sr + "\",\"installed\":\"" + Esc(InstalledVersion)
                    + "\",\"port\":" + Port + ",\"running\":" + Bool(Proc.PortUp(Port)) + "}");
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        /// <summary>当前版本号。</summary>
        public const string AppVersion = "v4.0";

        /// <summary>DSH 的 npm 包名。</summary>
        public const string DshPackage = "@deepseek-ai/dsh";

        const string HelpText = @"DSH 启动器 —— 管理 WSL 或 Windows 原生的 DSH Web 服务

用法: DSH-Launcher.exe [选项]

  (无参数)          打开图形界面
  --probe           输出当前状态 JSON 到 logs\selftest.json
  --detect          打印探测到的运行环境并退出
  --start           启动 DSH(等待端口就绪, 最多 120 秒)
  --stop            停止 DSH
  --restart         停止后重新启动
  --open            打开 Web UI(未运行则先启动)
  --check-update    检查 DSH 是否有新版本
  --update          更新 DSH 到最新版(会先停止服务)
  --selftest        全量自测(启动/重启/打开/检查更新/失败注入)
  -h, --help        显示本帮助

配置: exe 同目录的 launcher.json, 或环境变量
  DSH_LAUNCHER_MODE   auto | wsl | windows
  DSH_PORT            端口(默认 3080)
  DSH_WSL_DISTRO      WSL 发行版(默认自动选第一个)
  DSH_WSL_START       自定义 WSL 启动脚本路径
  DSH_HOME            DSH_HOME 目录
";

        /// <summary>写启动器日志。</summary>
        public static void Log(string msg)
        {
            try
            {
                File.AppendAllText(LauncherLog,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>清掉代理环境变量(死代理会让 API 请求挂起)。</summary>
        public static void ClearProxy()
        {
            string[] keys = { "http_proxy", "https_proxy", "all_proxy", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "no_proxy" };
            foreach (var k in keys) Environment.SetEnvironmentVariable(k, null, EnvironmentVariableTarget.Process);
        }

        /// <summary>JSON 字符串转义。</summary>
        public static string Esc(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");
        }

        static string Bool(bool b) { return b ? "true" : "false"; }

        static void WriteJson(string json)
        {
            try { File.WriteAllText(Path.Combine(LogDir, "selftest.json"), json, new UTF8Encoding(false)); }
            catch { }
        }

        // ---- 启停 ----

        /// <summary>启动 DSH 并等待端口就绪, 返回 "ok" / "failed"。</summary>
        public static string StartDsh()
        {
            Log("[START] 启动 DSH (" + Backend.Label + ", 端口 " + Port + ") ...");
            try
            {
                if (Backend.Start() != "spawned") { Backend.SyncLog(); return "failed"; }
            }
            catch (Exception ex)
            {
                Log("[START] 创建进程失败: " + ex.Message);
                Backend.SyncLog();
                return "failed";
            }
            for (int i = 0; i < 120; i++)
            {
                Thread.Sleep(1000);
                if (Proc.PortUp(Port))
                {
                    Log("[START] 启动成功! -> " + DshUrl + " (PID=" + Backend.GetListenerPid() + ")");
                    Backend.SyncLog();
                    return "ok";
                }
            }
            Backend.SyncLog();
            Log("[START] 启动失败! 日志副本: " + LogCopyPath);
            Log("-------- 日志尾部 --------");
            Log(Tail(File.Exists(LogCopyPath) ? File.ReadAllText(LogCopyPath) : "", 60));
            return "failed";
        }

        /// <summary>停止 DSH, 返回端口是否已释放。</summary>
        public static bool StopDsh()
        {
            Log("[STOP] 开始停止 DSH (" + Backend.Label + ") ...");
            int pid = Backend.GetListenerPid();
            if (pid > 0) Log("[STOP] 端口 " + Port + " 监听进程 PID=" + pid);
            bool ok = Backend.Stop();
            Log(ok ? "[STOP] 已停止" : "[STOP] 端口未能释放!");
            return ok;
        }

        // ---- 版本 / 更新 ----

        /// <summary>已安装版本(带 10 分钟缓存)。</summary>
        public static string GetInstalledVersion(bool force)
        {
            lock (verLock)
            {
                if (!force && InstalledVersion.Length > 0 && (DateTime.Now - installedAt).TotalMinutes < 10)
                    return InstalledVersion;
            }
            string inst = Backend.GetInstalledVersion(force);
            lock (verLock)
            {
                if (inst.Length > 0) { InstalledVersion = inst; installedAt = DateTime.Now; }
                return InstalledVersion;
            }
        }

        /// <summary>是否可更新。</summary>
        public static bool UpdateAvailable()
        {
            return InstalledVersion.Length > 0 && LatestVersion.Length > 0
                && Proc.CompareVersions(LatestVersion, InstalledVersion) > 0;
        }

        /// <summary>检查更新: 0=失败/未知, 1=有更新, 2=已最新。</summary>
        public static int CheckUpdate(out string inst, out string latest, out string err, Action<string> onLine)
        {
            int st = Backend.CheckUpdate(out inst, out latest, out err, onLine);
            lock (verLock)
            {
                if (inst.Length > 0) { InstalledVersion = inst; installedAt = DateTime.Now; }
                if (latest.Length > 0) LatestVersion = latest;
            }
            return st;
        }

        /// <summary>执行更新。</summary>
        public static string RunUpdate(Action<string> onLine)
        {
            string r = Backend.RunUpdate(onLine);
            GetInstalledVersion(true);
            return r;
        }

        // ---- 浏览器 ----

        static string FindChrome()
        {
            string[] paths = {
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe")
            };
            foreach (var p in paths) if (File.Exists(p)) return p;
            return null;
        }

        /// <summary>
        /// 打开浏览器时使用的地址。新版 DSH(0.1.2+) 会在日志里打印带 token 的 URL,
        /// 不带 token 直接访问会过不了浏览器信任校验, 所以优先用日志里那条。
        /// </summary>
        public static string ResolveOpenUrl()
        {
            try
            {
                string log = Proc.Tail(LogCopyPath, 60);
                var ms = Regex.Matches(log, "http://127\\.0\\.0\\.1:" + Port + "/?(\\?[^\\s\"']*)?");
                for (int i = ms.Count - 1; i >= 0; i--)
                {
                    string u = ms[i].Value.TrimEnd('\r', '\n', ' ', '"', '\'');
                    if (u.IndexOf("token=", StringComparison.OrdinalIgnoreCase) >= 0) return u;
                }
            }
            catch { }
            return DshUrl;
        }

        /// <summary>用 Chrome 打开(没有则用默认浏览器)。</summary>
        public static void OpenInChrome(string url)
        {
            string chrome = FindChrome();
            if (chrome != null)
            {
                try { Process.Start(chrome, url); return; }
                catch (Exception ex) { Log("[CHROME] 打开失败: " + ex.Message); }
            }
            try { Process.Start(url); }
            catch (Exception ex) { Log("[CHROME] 系统浏览器打开失败: " + ex.Message); }
        }

        static string Tail(string s, int lines)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var arr = s.Replace("\r\n", "\n").Split('\n');
            int start = Math.Max(0, arr.Length - lines);
            var sb = new StringBuilder();
            for (int i = start; i < arr.Length; i++) sb.AppendLine(arr[i]);
            return sb.ToString();
        }

        static void WriteSimpleResult(string action, string result)
        {
            WriteJson("{\"action\":\"" + action + "\",\"result\":\"" + result + "\",\"env\":\"" + Esc(Backend.Label)
                + "\",\"port\":" + Port + ",\"url\":\"" + DshUrl + "\",\"running\":" + Bool(Proc.PortUp(Port))
                + ",\"pid\":" + Backend.GetListenerPid() + "}");
        }

        // ---- 自测 ----

        static void SelfTest()
        {
            Log("[SELFTEST] ========== 全量自测开始 (env=" + Backend.Label + ", port=" + Port + ") ==========");
            string rStart = "skipped", rRestart = "skipped", rFail = "skipped", rOpen = "skipped";
            string cInst = "", cLatest = "";
            int cState = 0;

            Log("[SELFTEST] --- 阶段 1/4: 启动 ---");
            if (Proc.PortUp(Port)) { StopDsh(); Thread.Sleep(1000); }
            rStart = StartDsh();
            if (rStart == "ok") OpenInChrome(ResolveOpenUrl());

            Log("[SELFTEST] --- 阶段 2/4: 重启 ---");
            if (rStart == "ok")
            {
                Log("[RESTART] ============ 一键重启 ============");
                StopDsh();
                Thread.Sleep(1000);
                rRestart = StartDsh();
                if (rRestart == "ok") OpenInChrome(ResolveOpenUrl());
            }
            else Log("[SELFTEST] 阶段 1 失败, 跳过阶段 2");

            Log("[SELFTEST] --- 阶段 3/4: 打开 Web UI ---");
            if (Proc.PortUp(Port)) { rOpen = "ok"; OpenInChrome(ResolveOpenUrl()); }
            else { rOpen = "port-down"; Log("[SELFTEST] 端口未就绪, 跳过打开"); }

            Log("[SELFTEST] --- 阶段 4/4: 检查更新 ---");
            string cErr;
            cState = CheckUpdate(out cInst, out cLatest, out cErr, null);
            Log("[SELFTEST] 检查更新: 当前=" + cInst + " 最新=" + cLatest + " state=" + cState);

            Log("[SELFTEST] --- 附加: 失败注入 ---");
            if (rStart == "ok") { StopDsh(); Thread.Sleep(1000); }
            string keep = Config.FakeStart;
            Config.FakeStart = "exit 1";
            rFail = StartDsh();
            Config.FakeStart = keep;

            Log("[SELFTEST] 结果: start=" + rStart + " restart=" + rRestart + " open=" + rOpen
                + " check=" + cState + " fail=" + rFail);
            WriteJson("{\"env\":\"" + Esc(Backend.Label) + "\",\"port\":" + Port + ",\"url\":\"" + DshUrl
                + "\",\"start\":\"" + rStart + "\",\"restart\":\"" + rRestart + "\",\"open\":\"" + rOpen
                + "\",\"fail\":\"" + rFail + "\",\"installed\":\"" + Esc(cInst) + "\",\"latest\":\"" + Esc(cLatest)
                + "\",\"checkState\":" + cState + ",\"chrome\":\"" + Esc(FindChrome() ?? "") + "\"}");
            Log("[SELFTEST] 全量自测完成, 结果已写入 selftest.json");
        }
    }
}
