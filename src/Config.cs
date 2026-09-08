using System;
using System.IO;
using System.Text.RegularExpressions;

namespace DshLauncher
{
    /// <summary>启动器运行模式。</summary>
    enum RunMode { Auto, Wsl, Windows }

    /// <summary>
    /// 启动器配置。优先级: 环境变量 &gt; exe 同目录的 launcher.json &gt; 默认值。
    /// 所有项都可以不写, 默认值即为最常见的场景(自动探测 + 3080 端口)。
    /// </summary>
    class LauncherConfig
    {
        /// <summary>运行模式; Auto 表示自动探测。</summary>
        public RunMode Mode = RunMode.Auto;
        /// <summary>DSH Web UI 监听端口。</summary>
        public int Port = 3080;
        /// <summary>WSL 发行版名; 空表示自动选第一个装有 dsh 的发行版。</summary>
        public string WslDistro = "";
        /// <summary>自定义 WSL 启动脚本(绝对路径); 空表示用内置脚本。</summary>
        public string WslStartScript = "";
        /// <summary>DSH_HOME; 空表示平台默认。</summary>
        public string DshHome = "";
        /// <summary>实际读取到的配置文件路径(可能不存在)。</summary>
        public string ConfigPath = "";

        /// <summary>失败注入用: 覆盖启动命令(自测用, 正常使用不要设置)。</summary>
        public string FakeStart = "";

        /// <summary>读取配置: 先读 launcher.json, 再用环境变量覆盖。</summary>
        public static LauncherConfig Load(string baseDir)
        {
            var c = new LauncherConfig();
            c.ConfigPath = Path.Combine(baseDir, "launcher.json");
            try
            {
                if (File.Exists(c.ConfigPath))
                {
                    string json = File.ReadAllText(c.ConfigPath);
                    string mode = JsonStr(json, "mode");
                    if (!string.IsNullOrEmpty(mode)) c.Mode = ParseMode(mode, c.Mode);
                    int? port = JsonInt(json, "port");
                    if (port.HasValue && port.Value > 0) c.Port = port.Value;
                    c.WslDistro = JsonStr(json, "wslDistro") ?? c.WslDistro;
                    c.WslStartScript = JsonStr(json, "wslStartScript") ?? c.WslStartScript;
                    c.DshHome = JsonStr(json, "dshHome") ?? c.DshHome;
                }
            }
            catch { /* 配置损坏时退回默认值, 不阻塞启动 */ }

            c.Mode = ParseMode(Env("DSH_LAUNCHER_MODE"), c.Mode);

            string p = Env("DSH_PORT");
            int ip;
            if (int.TryParse(p, out ip) && ip > 0) c.Port = ip;

            string d = Env("DSH_WSL_DISTRO");
            if (!string.IsNullOrEmpty(d)) c.WslDistro = d;

            string s = Env("DSH_WSL_START");
            if (!string.IsNullOrEmpty(s)) c.WslStartScript = s;

            string h = Env("DSH_HOME");
            if (!string.IsNullOrEmpty(h)) c.DshHome = h;

            c.FakeStart = Env("DSH_LAUNCHER_FAKE_START");
            if (string.IsNullOrEmpty(c.FakeStart)) c.FakeStart = Env("DSH_FAKE_WSL_CMD");
            return c;
        }

        static string Env(string name)
        {
            string v = Environment.GetEnvironmentVariable(name);
            return v == null ? null : v.Trim();
        }

        static RunMode ParseMode(string v, RunMode fallback)
        {
            if (string.IsNullOrEmpty(v)) return fallback;
            switch (v.Trim().ToLowerInvariant())
            {
                case "wsl": return RunMode.Wsl;
                case "windows": case "win": case "native": return RunMode.Windows;
                case "auto": return RunMode.Auto;
                default: return fallback;
            }
        }

        // ---- 极简 JSON 取值(只处理扁平键值, 不引入依赖) ----

        static string JsonStr(string json, string key)
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? Regex.Unescape(m.Groups[1].Value) : null;
        }

        static int? JsonInt(string json, string key)
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?\\d+)");
            if (!m.Success) return null;
            int v;
            return int.TryParse(m.Groups[1].Value, out v) ? (int?)v : null;
        }
    }
}
