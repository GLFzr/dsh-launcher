using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DshLauncher
{
    /// <summary>某个运行环境的探测结果。</summary>
    class BackendProbe
    {
        /// <summary>该环境里 dsh 是否就绪。</summary>
        public bool Available;
        /// <summary>面向用户的一句话说明(就绪原因 / 缺失原因 + 修复命令)。</summary>
        public string Detail = "";
    }

    /// <summary>
    /// 一种 DSH 承载环境(WSL 发行版 / Windows 原生)。
    /// 所有方法都是同步阻塞的, 由调用方放到后台线程执行。
    /// </summary>
    interface IBackend
    {
        /// <summary>展示用名称, 如 "WSL · Ubuntu"。</summary>
        string Label { get; }
        /// <summary>本地日志副本路径(界面显示的日志内容)。</summary>
        string LogCopyPath { get; }
        /// <summary>探测 dsh 是否可用。</summary>
        BackendProbe Probe();
        /// <summary>启动 DSH, 返回 "ok" / "failed"。</summary>
        string Start();
        /// <summary>停止 DSH, 返回是否已释放端口。</summary>
        bool Stop();
        /// <summary>监听端口对应的进程 PID, 拿不到返回 -1。</summary>
        int GetListenerPid();
        /// <summary>把远端日志同步到 LogCopyPath。</summary>
        void SyncLog();
        /// <summary>读取已安装版本。</summary>
        string GetInstalledVersion(bool force);
        /// <summary>检查更新: 0=失败, 1=有更新, 2=已最新。</summary>
        int CheckUpdate(out string inst, out string latest, out string err, Action<string> onLine);
        /// <summary>执行更新, 返回 "ok" / "failed"。</summary>
        string RunUpdate(Action<string> onLine);
    }

    /// <summary>进程/端口/文本工具。</summary>
    static class Proc
    {
        public static string Clean(string s) { return s == null ? "" : s.Replace("\0", ""); }

        /// <summary>TCP 探测端口是否可连接。</summary>
        public static bool PortUp(int port)
        {
            try
            {
                var c = new TcpClient();
                var ar = c.BeginConnect("127.0.0.1", port, null, null);
                bool ok = ar.AsyncWaitHandle.WaitOne(1500, false) && c.Connected;
                c.Close();
                return ok;
            }
            catch { return false; }
        }

        /// <summary>执行命令并收集 stdout(超时强杀)。</summary>
        public static string Run(string exe, string args, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using (var p = Process.Start(psi))
                {
                    var sb = new StringBuilder();
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(Clean(e.Data)); };
                    p.BeginOutputReadLine();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } p.WaitForExit(3000); }
                    return sb.ToString();
                }
            }
            catch (Exception ex) { Program.Log("[" + exe + "] 调用失败: " + ex.Message); return ""; }
        }

        /// <summary>流式执行命令, 逐行回调, 返回退出码(-1 = 超时/异常)。</summary>
        public static int RunStream(string exe, string args, int timeoutMs, Action<string> onLine)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using (var p = Process.Start(psi))
                {
                    p.OutputDataReceived += (s, e) => { if (e.Data != null && onLine != null) onLine(Clean(e.Data)); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null && onLine != null) onLine(Clean(e.Data)); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } p.WaitForExit(3000); return -1; }
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch (Exception ex) { Program.Log("[" + exe + "] 流式调用失败: " + ex.Message); return -1; }
        }

        /// <summary>取 key=value 形式的字段值。</summary>
        public static string Field(string output, string key)
        {
            if (string.IsNullOrEmpty(output)) return "";
            foreach (string raw in output.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith(key + "=")) return line.Substring(key.Length + 1).Trim();
            }
            return "";
        }

        /// <summary>文件尾部若干行。</summary>
        public static string Tail(string path, int lines)
        {
            try
            {
                string[] arr;
                // FileShare.ReadWrite: 目标文件可能正被 DSH 进程写入(cmd 重定向),
                // File.ReadAllLines 的共享模式会直接抛异常, 导致日志框空白。
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                    arr = sr.ReadToEnd().Replace("\r\n", "\n").Split('\n');
                int start = Math.Max(0, arr.Length - lines);
                return string.Join("\r\n", arr, start, arr.Length - start);
            }
            catch { return ""; }
        }

        /// <summary>语义化版本比较: a 比 b 新返回正数。</summary>
        public static int CompareVersions(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return string.IsNullOrEmpty(b) ? 0 : -1;
            if (string.IsNullOrEmpty(b)) return 1;
            string pa = "", pb = "";
            int i = a.IndexOf('+'); if (i >= 0) a = a.Substring(0, i);
            i = b.IndexOf('+'); if (i >= 0) b = b.Substring(0, i);
            i = a.IndexOf('-'); if (i >= 0) { pa = a.Substring(i + 1); a = a.Substring(0, i); }
            i = b.IndexOf('-'); if (i >= 0) { pb = b.Substring(i + 1); b = b.Substring(0, i); }

            string[] na = a.Split('.'), nb = b.Split('.');
            int n = Math.Max(na.Length, nb.Length);
            for (int k = 0; k < n; k++)
            {
                int x = Num(k < na.Length ? na[k] : "0");
                int y = Num(k < nb.Length ? nb[k] : "0");
                if (x != y) return x > y ? 1 : -1;
            }
            if (pa.Length == 0 && pb.Length == 0) return 0;
            if (pa.Length == 0) return 1;      // 正式版 > 预发布版
            if (pb.Length == 0) return -1;
            string[] sa = pa.Split('.'), sb = pb.Split('.');
            int m = Math.Max(sa.Length, sb.Length);
            for (int k = 0; k < m; k++)
            {
                string u = k < sa.Length ? sa[k] : "";
                string v = k < sb.Length ? sb[k] : "";
                if (u == v) continue;
                if (u.Length == 0) return -1;
                if (v.Length == 0) return 1;
                bool un = Regex.IsMatch(u, "^[0-9]+$"), vn = Regex.IsMatch(v, "^[0-9]+$");
                if (un && vn) { int x = Num(u), y = Num(v); if (x != y) return x > y ? 1 : -1; }
                else if (un) return -1;        // 数字标识符 < 字母标识符
                else if (vn) return 1;
                else { int c = string.Compare(u, v, StringComparison.OrdinalIgnoreCase); if (c != 0) return c > 0 ? 1 : -1; }
            }
            return 0;
        }

        static int Num(string s) { int v = 0; int.TryParse(s, out v); return v; }
    }

    // ==================== WSL 后端 ====================

    /// <summary>通过 wsl.exe 管理发行版里的 DSH。</summary>
    class WslBackend : IBackend
    {
        readonly LauncherConfig cfg;
        string distro;

        public WslBackend(LauncherConfig cfg) { this.cfg = cfg; distro = cfg.WslDistro; }

        public string Label { get { return "WSL · " + ResolveDistro(); } }
        public string LogCopyPath { get { return Program.LogCopyPath; } }

        /// <summary>发行版名: 配置优先, 否则取 wsl -l -q 的第一个。</summary>
        public string ResolveDistro()
        {
            if (!string.IsNullOrEmpty(distro)) return distro;
            string outp = Proc.Run("wsl.exe", "-l -q", 15000);
            foreach (string raw in outp.Replace("\0", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length > 0) { distro = line; break; }
            }
            if (string.IsNullOrEmpty(distro)) distro = "Ubuntu";
            return distro;
        }

        string Wsl(string bashArgs, int timeoutMs)
        {
            return Proc.Run("wsl.exe", "-d " + ResolveDistro() + " -- " + bashArgs, timeoutMs);
        }

        /// <summary>把 bash 脚本 base64 后送进 WSL 执行, 彻底规避多层引号转义。</summary>
        static string ScriptCmd(string script, string args)
        {
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
            return "bash -c \"echo " + b64 + " | base64 -d | bash -s -- " + (args == null ? "" : args) + "\"";
        }

        public BackendProbe Probe()
        {
            var r = new BackendProbe();
            string outp = Wsl("bash -lc \"command -v dsh 2>/dev/null; echo __END__\"", 30000);
            if (outp.IndexOf("__END__", StringComparison.Ordinal) < 0)
            {
                r.Detail = "WSL 发行版 " + ResolveDistro() + " 不可用(未安装 WSL 或发行版名不对)";
                return r;
            }
            string path = outp.Substring(0, outp.IndexOf("__END__", StringComparison.Ordinal)).Trim();
            if (path.Length == 0)
            {
                r.Detail = "WSL(" + ResolveDistro() + ") 里找不到 dsh, 请先安装: npm i -g @deepseek-ai/dsh";
                return r;
            }
            r.Available = true;
            r.Detail = "dsh 位于 " + path;
            return r;
        }

        public string Start()
        {
            if (!string.IsNullOrEmpty(cfg.FakeStart))
            {
                Program.Log("[START] 使用注入命令: " + cfg.FakeStart);
                Process.Start(new ProcessStartInfo("wsl.exe",
                    "-d " + ResolveDistro() + " -- " + cfg.FakeStart)
                { UseShellExecute = false, CreateNoWindow = true });
                return "failed";
            }
            if (!string.IsNullOrEmpty(cfg.WslStartScript))
            {
                // 兼容自定义启动脚本(老用户)
                Program.Log("[START] 使用自定义脚本: " + cfg.WslStartScript);
                Process.Start(new ProcessStartInfo("wsl.exe",
                    "-d " + ResolveDistro() + " -- bash " + cfg.WslStartScript)
                { UseShellExecute = false, CreateNoWindow = true });
            }
            else
            {
                string script = BuiltinStartScript;
                Program.Log("[START] 使用内置脚本 (port=" + cfg.Port + ")");
                Process.Start(new ProcessStartInfo("wsl.exe",
                    "-d " + ResolveDistro() + " -- " + ScriptCmd(script, cfg.Port + " " + cfg.DshHome))
                { UseShellExecute = false, CreateNoWindow = true });
            }
            return "spawned";
        }

        /// <summary>内置启动脚本: 清代理、补 PATH、设置 DSH_HOME、exec dsh web 并落日志。</summary>
        const string BuiltinStartScript = @"
PORT=${1:-3080}
HOME_DIR=${2:-}
export PATH=$HOME/.local/bin:$PATH
unset http_proxy https_proxy all_proxy HTTP_PROXY HTTPS_PROXY ALL_PROXY no_proxy NO_PROXY
if [ -n ""$HOME_DIR"" ]; then export DSH_HOME=""$HOME_DIR""; elif [ -z ""$DSH_HOME"" ]; then export DSH_HOME=$HOME/.dsh; fi
LOG=${DSH_LAUNCHER_LOG:-$HOME/.dsh-web.log}
cd ""$HOME"" 2>/dev/null || cd /
exec dsh web --no-open --port ""$PORT"" > ""$LOG"" 2>&1
";

        public bool Stop()
        {
            string script = "pkill -f '[d]sh web'; exit 0";
            if (!string.IsNullOrEmpty(cfg.WslStartScript))
            {
                string name = cfg.WslStartScript.Substring(cfg.WslStartScript.LastIndexOf('/') + 1);
                script = "pkill -f '[d]sh web'; pkill -f '[" + name.Substring(0, 1) + "]" + name.Substring(1) + "'; exit 0";
            }
            Wsl("bash -c \"" + script + "\"", 15000);
            for (int i = 0; i < 40 && Proc.PortUp(cfg.Port); i++) Thread.Sleep(500);
            if (Proc.PortUp(cfg.Port))
            {
                Wsl("bash -c \"pkill -9 -f '[d]sh web'; exit 0\"", 15000);
                for (int i = 0; i < 20 && Proc.PortUp(cfg.Port); i++) Thread.Sleep(500);
            }
            return !Proc.PortUp(cfg.Port);
        }

        public int GetListenerPid()
        {
            string outp = Wsl("bash -c \"ss -tlnp 2>/dev/null | grep ':" + cfg.Port + " ' || true\"", 15000);
            var m = Regex.Match(outp, @"pid=(\d+)");
            if (m.Success) { int pid; if (int.TryParse(m.Groups[1].Value, out pid)) return pid; }
            return -1;
        }

        public void SyncLog()
        {
            // 哨兵行判定 wsl 调用是否成功: 失败时 wsl.exe 会把错误以非 UTF-8 文本吐到 stdout
            string outp = Wsl("bash -c \"tail -n 200 ~/.dsh-web.log 2>/dev/null; echo __DSH_TAIL_END__\"", 15000);
            int end = outp.IndexOf("__DSH_TAIL_END__", StringComparison.Ordinal);
            if (end < 0) return;
            File.WriteAllText(LogCopyPath, outp.Substring(0, end).TrimEnd('\r', '\n'), new UTF8Encoding(false));
        }

        const string VersionScript = @"
export PATH=$HOME/.local/bin:$PATH
unset http_proxy https_proxy all_proxy HTTP_PROXY HTTPS_PROXY ALL_PROXY no_proxy NO_PROXY
ROOT=$(npm root -g 2>/dev/null)
INST=
if [ -n ""$ROOT"" ] && [ -f ""$ROOT/@deepseek-ai/dsh/package.json"" ]; then
  INST=$(node -e 'console.log(require(process.argv[1]).version)' ""$ROOT/@deepseek-ai/dsh/package.json"" 2>/dev/null)
fi
LATEST=
if [ ""$1"" = ""net"" ]; then
  LATEST=$(npm view @deepseek-ai/dsh version --fetch-retries=1 --fetch-timeout=30000 2>/dev/null | tail -1 | tr -d '\r\n ')
fi
echo ""__INST__=$INST""
echo ""__LATEST__=$LATEST""
";

        const string UpdateScript = @"
export PATH=$HOME/.local/bin:$PATH
unset http_proxy https_proxy all_proxy HTTP_PROXY HTTPS_PROXY ALL_PROXY no_proxy NO_PROXY
ROOT=$(npm root -g 2>/dev/null)
BEFORE=$(node -e 'console.log(require(process.argv[1]).version)' ""$ROOT/@deepseek-ai/dsh/package.json"" 2>/dev/null)
echo ""[update] 当前版本: $BEFORE""
npm install -g @deepseek-ai/dsh@latest --no-fund --no-audit --fetch-retries=2 --fetch-timeout=60000
RC=$?
ROOT=$(npm root -g 2>/dev/null)
INST=$(node -e 'console.log(require(process.argv[1]).version)' ""$ROOT/@deepseek-ai/dsh/package.json"" 2>/dev/null)
echo ""[update] 安装后版本: $INST (npm exit=$RC)""
echo ""__RC__=$RC""
echo ""__INST__=$INST""
";

        public string GetInstalledVersion(bool force)
        {
            string outp = Wsl(ScriptCmd(VersionScript, ""), 25000);
            return Proc.Field(outp, "__INST__");
        }

        public int CheckUpdate(out string inst, out string latest, out string err, Action<string> onLine)
        {
            inst = ""; latest = ""; err = "";
            var sb = new StringBuilder();
            int rc = Proc.RunStream("wsl.exe", "-d " + ResolveDistro() + " -- " + ScriptCmd(VersionScript, "net"), 60000,
                delegate(string l) { sb.AppendLine(l); if (onLine != null) onLine(l); });
            string outp = sb.ToString();
            if (rc != 0 || outp.Trim().Length == 0) { err = "WSL 无响应或命令执行失败 (exit=" + rc + ")"; return 0; }
            inst = Proc.Field(outp, "__INST__");
            latest = Proc.Field(outp, "__LATEST__");
            if (inst.Length == 0) { err = "读不到已安装版本(npm root -g / node 是否可用?)"; return 0; }
            if (latest.Length == 0) { err = "查不到最新版本(网络或 npm registry 不通)"; return 0; }
            return Proc.CompareVersions(latest, inst) > 0 ? 1 : 2;
        }

        public string RunUpdate(Action<string> onLine)
        {
            var sb = new StringBuilder();
            int rc = Proc.RunStream("wsl.exe", "-d " + ResolveDistro() + " -- " + ScriptCmd(UpdateScript, ""), 20 * 60 * 1000,
                delegate(string l) { sb.AppendLine(l); if (onLine != null) onLine(l); });
            string outp = sb.ToString();
            string npmRc = Proc.Field(outp, "__RC__");
            string inst = Proc.Field(outp, "__INST__");
            if (rc != 0) { Program.Log("[UPDATE] wsl 退出码=" + rc); return "failed"; }
            if (npmRc != "0") { Program.Log("[UPDATE] npm 退出码=" + npmRc); return "failed"; }
            if (inst.Length == 0) { Program.Log("[UPDATE] 无法确认更新后的版本"); return "failed"; }
            return "ok";
        }
    }

    // ==================== Windows 原生后端 ====================

    /// <summary>直接管理 Windows 上的 DSH(node + npm 全局包)。</summary>
    class WindowsBackend : IBackend
    {
        readonly LauncherConfig cfg;
        string nodeExe, npmRoot, dshBin;
        Process proc;

        public WindowsBackend(LauncherConfig cfg) { this.cfg = cfg; }

        public string Label { get { return "Windows 原生"; } }
        public string LogCopyPath { get { return Program.LogCopyPath; } }

        string Node()
        {
            if (nodeExe != null) return nodeExe;
            string outp = Proc.Run("cmd.exe", "/c where node", 20000);
            foreach (string raw in outp.Replace("\0", "").Split('\n'))
            {
                string line = raw.Trim();
                // cmd 在 UNC 工作目录下会先打一行告警, 用文件存在性过滤掉
                if (line.EndsWith("node.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(line)) { nodeExe = line; break; }
            }
            return nodeExe;
        }

        string NpmRoot()
        {
            if (npmRoot != null) return npmRoot;
            string outp = Proc.Run("cmd.exe", "/c npm root -g", 30000);
            string best = null;
            foreach (string raw in outp.Replace("\0", "").Split('\n'))
            {
                string line = raw.Trim();
                // 取最后一个真实存在的路径, 顺带跳过 cmd 的 UNC 告警
                if (line.Length > 3 && line.IndexOf(':') > 0 && Directory.Exists(line)) best = line;
            }
            npmRoot = best;
            return npmRoot;
        }

        string DshBin()
        {
            if (dshBin != null) return dshBin;
            string root = NpmRoot();
            if (string.IsNullOrEmpty(root)) return null;
            string p = Path.Combine(root, "@deepseek-ai", "dsh", "lib", "bin.js");
            if (File.Exists(p)) dshBin = p;
            return dshBin;
        }

        public BackendProbe Probe()
        {
            var r = new BackendProbe();
            if (string.IsNullOrEmpty(Node()))
            {
                r.Detail = "找不到 node.exe, 请先安装 Node.js (https://nodejs.org)";
                return r;
            }
            if (string.IsNullOrEmpty(DshBin()))
            {
                r.Detail = "Windows 上找不到 dsh, 请先安装: npm i -g @deepseek-ai/dsh";
                return r;
            }
            r.Available = true;
            r.Detail = "dsh 位于 " + DshBin();
            return r;
        }

        public string Start()
        {
            if (!string.IsNullOrEmpty(cfg.FakeStart))
            {
                Program.Log("[START] 使用注入命令: " + cfg.FakeStart);
                Proc.Run("cmd.exe", "/c " + cfg.FakeStart, 20000);
                return "failed";
            }
            string bin = DshBin();
            if (string.IsNullOrEmpty(bin)) return "failed";

            Program.ClearProxy();
            // 用 cmd 重定向到文件, 而不是 .NET 的管道: 管道随启动器进程结束而关闭,
            // 子进程写 stdout 会失败并退出 —— 那样关掉启动器窗口就把 DSH 一起杀了。
            string inner = "\"" + Node() + "\" \"" + bin + "\" web --no-open --port " + cfg.Port
                + " > \"" + LogCopyPath + "\" 2>&1";
            var psi = new ProcessStartInfo("cmd.exe", "/c \"" + inner + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            if (!string.IsNullOrEmpty(cfg.DshHome)) psi.EnvironmentVariables["DSH_HOME"] = cfg.DshHome;

            proc = Process.Start(psi);
            Program.Log("[START] 已创建 cmd 进程 PID=" + proc.Id + " (node 由它托管, 启动器退出不影响)");
            return "spawned";
        }

        public bool Stop()
        {
            try
            {
                if (proc != null)
                {
                    proc.Refresh();
                    if (!proc.HasExited) Proc.Run("taskkill.exe", "/PID " + proc.Id + " /T /F", 20000);
                }
            }
            catch { }
            for (int i = 0; i < 20 && Proc.PortUp(cfg.Port); i++) Thread.Sleep(500);
            if (Proc.PortUp(cfg.Port))
            {
                int pid = GetListenerPid();
                if (pid > 0) Proc.Run("taskkill.exe", "/PID " + pid + " /T /F", 20000);
                for (int i = 0; i < 20 && Proc.PortUp(cfg.Port); i++) Thread.Sleep(500);
            }
            return !Proc.PortUp(cfg.Port);
        }

        public int GetListenerPid()
        {
            string outp = Proc.Run("netstat.exe", "-ano -p tcp", 20000);
            foreach (string raw in outp.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string[] parts = Regex.Split(line, "\\s+");
                if (parts.Length < 5) continue;
                if (parts[1].EndsWith(":" + cfg.Port, StringComparison.Ordinal))
                {
                    int pid;
                    if (int.TryParse(parts[parts.Length - 1], out pid)) return pid;
                }
            }
            return -1;
        }

        public void SyncLog()
        {
            // Windows 原生的日志直接由本进程写 LogCopyPath, 无需同步
        }

        public string GetInstalledVersion(bool force)
        {
            try
            {
                string root = NpmRoot();
                if (string.IsNullOrEmpty(root)) return "";
                string pkg = Path.Combine(root, "@deepseek-ai", "dsh", "package.json");
                if (!File.Exists(pkg)) return "";
                var m = Regex.Match(File.ReadAllText(pkg), "\"version\"\\s*:\\s*\"([^\"]+)\"");
                return m.Success ? m.Groups[1].Value : "";
            }
            catch { return ""; }
        }

        public int CheckUpdate(out string inst, out string latest, out string err, Action<string> onLine)
        {
            inst = GetInstalledVersion(true);
            latest = "";
            err = "";
            var sb = new StringBuilder();
            int rc = Proc.RunStream("cmd.exe", "/c npm view @deepseek-ai/dsh version", 60000,
                delegate(string l) { sb.AppendLine(l); if (onLine != null) onLine(l); });
            foreach (string raw in sb.ToString().Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (Regex.IsMatch(line, "^[0-9]+\\.[0-9]+")) { latest = line; }
            }
            if (rc != 0 || latest.Length == 0) { err = "查不到最新版本(npm registry 不通?)"; return 0; }
            if (inst.Length == 0) { err = "读不到已安装版本(npm root -g 是否可用?)"; return 0; }
            return Proc.CompareVersions(latest, inst) > 0 ? 1 : 2;
        }

        public string RunUpdate(Action<string> onLine)
        {
            var sb = new StringBuilder();
            int rc = Proc.RunStream("cmd.exe", "/c npm install -g @deepseek-ai/dsh@latest --no-fund --no-audit", 20 * 60 * 1000,
                delegate(string l) { sb.AppendLine(l); if (onLine != null) onLine(l); });
            if (rc != 0) { Program.Log("[UPDATE] npm 退出码=" + rc); return "failed"; }
            if (GetInstalledVersion(true).Length == 0) { Program.Log("[UPDATE] 无法确认更新后的版本"); return "failed"; }
            return "ok";
        }
    }

    /// <summary>按配置/探测结果选择后端。</summary>
    static class BackendFactory
    {
        /// <summary>创建后端; Auto 模式先看 WSL 再看 Windows 原生。</summary>
        public static IBackend Create(LauncherConfig cfg, out string detectNote)
        {
            detectNote = "";
            if (cfg.Mode == RunMode.Wsl) return new WslBackend(cfg);
            if (cfg.Mode == RunMode.Windows) return new WindowsBackend(cfg);

            var wsl = new WslBackend(cfg);
            BackendProbe wp = wsl.Probe();
            if (wp.Available)
            {
                detectNote = "自动探测: WSL 里已安装 dsh";
                return wsl;
            }
            var win = new WindowsBackend(cfg);
            BackendProbe np = win.Probe();
            if (np.Available)
            {
                detectNote = "自动探测: Windows 原生已安装 dsh";
                return win;
            }
            detectNote = "自动探测: 两个环境都没找到 dsh · WSL: " + wp.Detail + " · Windows: " + np.Detail;
            return win;   // 都缺时用 Windows 后端, 报错信息最贴近当前系统
        }
    }
}
