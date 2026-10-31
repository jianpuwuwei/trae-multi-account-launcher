using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace TraeSandbox
{
    /// <summary>
    /// 自研 Trae 隔离容器启动器（不依赖 Sandboxie 等外部沙箱）。
    /// 用法：
    ///   TraeSandbox.exe dual [n]   首次启动主实例（若未运行）并打开 n 号容器（默认 2）
    ///   TraeSandbox.exe start n    只启动 n 号容器
    ///   TraeSandbox.exe stop n     停止 n 号容器
    ///   TraeSandbox.exe stop all   停止全部容器
    ///   TraeSandbox.exe reset n    清空 n 号容器（等价退出登录并清除缓存）
    ///   TraeSandbox.exe status     查看容器状态
    /// </summary>
    internal static class Program
    {
        private static string _rootBase;
        private static string _configuredExe;
        private static bool _isolateHome = true;

        private const string ProcessName = "Trae CN";

        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch (Exception) { }

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            LoadConfig(baseDir);

            string command = args.Length > 0 ? args[0].ToLowerInvariant() : "dual";
            try
            {
                switch (command)
                {
                    case "dual":
                        return Dual(args.Length > 1 ? ParseAccount(args[1]) : 2);
                    case "start":
                        return StartOnly(args.Length > 1 ? ParseAccount(args[1]) : 2);
                    case "stop":
                        return StopCommand(args.Length > 1 ? args[1] : "all");
                    case "reset":
                        return Reset(args.Length > 1 ? ParseAccount(args[1]) : 2);
                    case "status":
                        return Status();
                    case "help":
                    case "-h":
                    case "--help":
                        PrintHelp();
                        return 0;
                    default:
                        Console.WriteLine("未知命令：" + command);
                        PrintHelp();
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[错误] " + ex.Message);
                return 1;
            }
        }

        private static int Dual(int account)
        {
            string exe = RequireTraeExe();
            Console.WriteLine("Trae 程序：" + exe);

            if (!IsHostRunning(exe))
            {
                Console.WriteLine("主实例未运行，正在启动主实例...");
                ProcessStartInfo info = new ProcessStartInfo(exe);
                info.UseShellExecute = false;
                info.WorkingDirectory = Path.GetDirectoryName(exe);
                Process.Start(info);
                System.Threading.Thread.Sleep(2500);
            }
            else
            {
                Console.WriteLine("主实例已在运行。");
            }

            return LaunchContainer(exe, account);
        }

        private static int StartOnly(int account)
        {
            string exe = RequireTraeExe();
            return LaunchContainer(exe, account);
        }

        private static int LaunchContainer(string exe, int account)
        {
            SandboxContainer container = new SandboxContainer(_rootBase, account, _isolateHome);
            if (container.IsRunning())
            {
                Console.WriteLine(container.Name + " 已在运行，PID：" + string.Join(", ", container.GetProcessIds()));
                TryFocus(container);
                return 0;
            }

            Console.WriteLine("启动容器：" + container.Name);
            Console.WriteLine("隔离目录：" + container.RootDir);
            int pid = container.Start(exe, null);
            if (pid < 0)
            {
                Console.WriteLine(container.Name + " 已在运行。");
                return 0;
            }
            Console.WriteLine("已启动，主进程 PID：" + pid.ToString());
            Console.WriteLine("该容器为独立登录态，可直接登录另一个账号。");
            return 0;
        }

        private static int StopCommand(string target)
        {
            if (string.Equals(target, "all", StringComparison.OrdinalIgnoreCase))
            {
                int count = 0;
                foreach (int account in ListAccounts())
                {
                    SandboxContainer container = new SandboxContainer(_rootBase, account, _isolateHome);
                    if (container.Stop())
                    {
                        Console.WriteLine("已停止 " + container.Name);
                        count++;
                    }
                }
                Console.WriteLine("共停止 " + count.ToString() + " 个容器。");
                return 0;
            }

            SandboxContainer one = new SandboxContainer(_rootBase, ParseAccount(target), _isolateHome);
            if (one.Stop())
            {
                Console.WriteLine("已停止 " + one.Name);
            }
            else
            {
                Console.WriteLine(one.Name + " 未在运行。");
            }
            return 0;
        }

        private static int Reset(int account)
        {
            SandboxContainer container = new SandboxContainer(_rootBase, account, _isolateHome);
            container.Reset();
            Console.WriteLine("已清空 " + container.Name + "，下次启动将使用全新登录态。");
            return 0;
        }

        private static int Status()
        {
            Console.WriteLine("容器根目录：" + _rootBase);
            int[] accounts = ListAccounts();
            if (accounts.Length == 0)
            {
                Console.WriteLine("尚未创建任何容器。");
            }
            foreach (int account in accounts)
            {
                SandboxContainer container = new SandboxContainer(_rootBase, account, _isolateHome);
                bool running = container.IsRunning();
                string pids = string.Join(", ", container.GetProcessIds());
                Console.WriteLine(string.Format("{0}  {1}  {2}",
                    container.Name,
                    running ? "运行中" : "已停止",
                    running ? ("PID: " + pids) : ""));
            }
            Console.WriteLine("主实例：" + (IsHostRunning(ResolveTraeExe(false)) ? "运行中" : "未运行"));
            return 0;
        }

        private static int[] ListAccounts()
        {
            List<int> list = new List<int>();
            if (!Directory.Exists(_rootBase))
            {
                return list.ToArray();
            }
            foreach (string dir in Directory.GetDirectories(_rootBase, "account-*"))
            {
                string name = Path.GetFileName(dir);
                int account;
                if (int.TryParse(name.Substring("account-".Length), out account))
                {
                    list.Add(account);
                }
            }
            list.Sort();
            return list.ToArray();
        }

        private static void TryFocus(SandboxContainer container)
        {
            foreach (int pid in container.GetProcessIds())
            {
                try
                {
                    Process process = Process.GetProcessById(pid);
                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        Win32Api.ShowWindow(process.MainWindowHandle, 9);
                        Win32Api.SetForegroundWindow(process.MainWindowHandle);
                    }
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
        }

        private static int ParseAccount(string value)
        {
            int account;
            if (!int.TryParse(value, out account) || account < 1 || account > 99)
            {
                throw new ArgumentException("账号编号必须是 1-99 的整数");
            }
            return account;
        }

        private static string RequireTraeExe()
        {
            string exe = ResolveTraeExe(true);
            if (string.IsNullOrEmpty(exe))
            {
                throw new FileNotFoundException(
                    "未找到 Trae CN 程序，请在 config.ini 中填写 traePath");
            }
            return exe;
        }

        private static string ResolveTraeExe(bool required)
        {
            if (!string.IsNullOrEmpty(_configuredExe) && File.Exists(_configuredExe))
            {
                return _configuredExe;
            }

            // 1) 从正在运行的主实例取路径
            try
            {
                foreach (Process process in Process.GetProcessesByName(ProcessName))
                {
                    try
                    {
                        if (process.MainModule != null && File.Exists(process.MainModule.FileName))
                        {
                            return process.MainModule.FileName;
                        }
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }

            // 2) 从卸载注册表取安装目录
            string[] roots = new string[]
            {
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
                @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };
            RegistryKey[] hives = new RegistryKey[] { Registry.LocalMachine, Registry.CurrentUser };
            foreach (RegistryKey hive in hives)
            {
                foreach (string root in roots)
                {
                    try
                    {
                        using (RegistryKey key = hive.OpenSubKey(root))
                        {
                            if (key == null)
                            {
                                continue;
                            }
                            foreach (string sub in key.GetSubKeyNames())
                            {
                                try
                                {
                                    using (RegistryKey item = key.OpenSubKey(sub))
                                    {
                                        string display = item.GetValue("DisplayName") as string;
                                        if (display == null || display.IndexOf("trae", StringComparison.OrdinalIgnoreCase) < 0)
                                        {
                                            continue;
                                        }
                                        string location = item.GetValue("InstallLocation") as string;
                                        string found = FindExeInFolder(location);
                                        if (!string.IsNullOrEmpty(found))
                                        {
                                            return found;
                                        }
                                        string icon = item.GetValue("DisplayIcon") as string;
                                        if (!string.IsNullOrEmpty(icon))
                                        {
                                            string candidate = icon.Trim('"');
                                            int comma = candidate.LastIndexOf(',');
                                            if (comma > 0)
                                            {
                                                candidate = candidate.Substring(0, comma);
                                            }
                                            if (File.Exists(candidate))
                                            {
                                                return candidate;
                                            }
                                        }
                                    }
                                }
                                catch (Exception) { }
                            }
                        }
                    }
                    catch (Exception) { }
                }
            }

            // 3) 常见安装位置
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string[] candidates = new string[]
            {
                Path.Combine(local, "Programs", "Trae CN", "Trae CN.exe"),
                Path.Combine(programFiles, "Trae CN", "Trae CN.exe"),
                @"E:\files1\Trae CN\Trae CN.exe"
            };
            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            return null;
        }

        private static string FindExeInFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                return null;
            }
            string direct = Path.Combine(folder, "Trae CN.exe");
            if (File.Exists(direct))
            {
                return direct;
            }
            foreach (string file in Directory.GetFiles(folder, "*.exe"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.IndexOf("Trae", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return file;
                }
            }
            return null;
        }

        /// <summary>主实例 = 不在任何容器作业中的 Trae 进程。</summary>
        private static bool IsHostRunning(string exePath)
        {
            try
            {
                int[] containerPids = AllContainerPids();
                Process[] processes = Process.GetProcessesByName(ProcessName);
                if (processes.Length - containerPids.Length >= 3)
                {
                    return true;
                }
                foreach (Process process in processes)
                {
                    if (process.MainWindowHandle != IntPtr.Zero && !Contains(containerPids, process.Id))
                    {
                        return true;
                    }
                }
            }
            catch (Exception) { }
            return false;
        }

        private static int[] AllContainerPids()
        {
            List<int> pids = new List<int>();
            foreach (int account in ListAccounts())
            {
                SandboxContainer container = new SandboxContainer(_rootBase, account, _isolateHome);
                pids.AddRange(container.GetProcessIds());
            }
            return pids.ToArray();
        }

        private static bool Contains(int[] values, int value)
        {
            foreach (int item in values)
            {
                if (item == value)
                {
                    return true;
                }
            }
            return false;
        }

        private static void LoadConfig(string baseDir)
        {
            string root = Path.GetDirectoryName(baseDir.TrimEnd(Path.DirectorySeparatorChar));
            string[] candidates = new string[]
            {
                Path.Combine(root, "config.ini"),
                Path.Combine(baseDir, "config.ini")
            };
            string file = null;
            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    file = candidate;
                    break;
                }
            }

            string rootDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TraeSandbox");
            if (file != null)
            {
                foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
                {
                    string text = line.Trim();
                    if (text.Length == 0 || text.StartsWith("#") || text.StartsWith(";"))
                    {
                        continue;
                    }
                    int eq = text.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }
                    string key = text.Substring(0, eq).Trim();
                    string value = text.Substring(eq + 1).Trim();
                    if (string.Equals(key, "traePath", StringComparison.OrdinalIgnoreCase))
                    {
                        _configuredExe = Environment.ExpandEnvironmentVariables(value);
                    }
                    else if (string.Equals(key, "rootDir", StringComparison.OrdinalIgnoreCase))
                    {
                        if (value.Length > 0)
                        {
                            rootDir = Environment.ExpandEnvironmentVariables(value);
                        }
                    }
                    else if (string.Equals(key, "isolateHome", StringComparison.OrdinalIgnoreCase))
                    {
                        _isolateHome = !(value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase));
                    }
                }
            }
            _rootBase = rootDir;
        }

        private static void PrintHelp()
        {
            Console.WriteLine("用法：");
            Console.WriteLine("  TraeSandbox.exe dual [n]    启动主实例（若未运行）+ n 号隔离容器（默认 2）");
            Console.WriteLine("  TraeSandbox.exe start n     只启动 n 号隔离容器");
            Console.WriteLine("  TraeSandbox.exe stop n      停止 n 号隔离容器");
            Console.WriteLine("  TraeSandbox.exe stop all    停止全部隔离容器");
            Console.WriteLine("  TraeSandbox.exe reset n     清空 n 号容器（退出登录并清除缓存）");
            Console.WriteLine("  TraeSandbox.exe status      查看容器状态");
            Console.WriteLine("配置：config.ini（traePath / rootDir / isolateHome）");
        }
    }
}
