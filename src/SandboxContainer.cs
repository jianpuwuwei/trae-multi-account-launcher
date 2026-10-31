using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

namespace TraeSandbox
{
    /// <summary>
    /// 一个账号对应的隔离容器：独立 user-data / 扩展 / AppData / TEMP / Home。
    /// 进程树通过容器目录标记（--user-data-dir 指向容器内路径）识别，不依赖外部沙箱。
    /// </summary>
    internal sealed class SandboxContainer
    {
        private const string TraeProcessName = "Trae CN.exe";

        public string Name { get; private set; }
        public string RootDir { get; private set; }
        public bool IsolateHome { get; set; }

        public SandboxContainer(string rootBase, int account, bool isolateHome)
        {
            Name = "account-" + account;
            RootDir = Path.Combine(rootBase, Name);
            IsolateHome = isolateHome;
        }

        public string PortableDir { get { return Path.Combine(RootDir, "portable"); } }
        public string UserDataDir { get { return Path.Combine(PortableDir, "user-data"); } }
        public string RoamingDir { get { return Path.Combine(RootDir, "AppData", "Roaming"); } }
        public string LocalDir { get { return Path.Combine(RootDir, "AppData", "Local"); } }
        public string TempDir { get { return Path.Combine(RootDir, "Temp"); } }
        public string HomeDir { get { return Path.Combine(RootDir, "Home"); } }
        public string MetaFile { get { return Path.Combine(RootDir, "container.ini"); } }

        public void EnsureLayout()
        {
            Directory.CreateDirectory(RootDir);
            Directory.CreateDirectory(PortableDir);
            Directory.CreateDirectory(UserDataDir);
            Directory.CreateDirectory(RoamingDir);
            Directory.CreateDirectory(LocalDir);
            Directory.CreateDirectory(TempDir);
            if (IsolateHome)
            {
                Directory.CreateDirectory(HomeDir);
            }
        }

        private Dictionary<string, string> BuildEnvironment()
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // 便携模式：Trae 的 user-data、extensions、argv.json 全部落在容器内
            map["VSCODE_PORTABLE"] = PortableDir;
            map["APPDATA"] = RoamingDir;
            map["LOCALAPPDATA"] = LocalDir;
            map["TEMP"] = TempDir;
            map["TMP"] = TempDir;
            if (IsolateHome)
            {
                map["USERPROFILE"] = HomeDir;
            }

            // 清理父进程（可能来自主实例终端）带过来的 VS Code/Electron 耦合变量
            map["VSCODE_APPDATA"] = null;
            map["VSCODE_EXTENSIONS"] = null;
            map["VSCODE_IPC_HOOK"] = null;
            map["VSCODE_IPC_HOOK_CLI"] = null;
            map["VSCODE_IPC_HOOK_EXTHOST"] = null;
            map["VSCODE_NLS_CONFIG"] = null;
            map["ELECTRON_RUN_AS_NODE"] = null;
            return map;
        }

        public bool IsRunning()
        {
            return GetProcessIds().Length > 0;
        }

        /// <summary>容器内全部 Trae 进程 PID；识别失败时退回记录的主进程 PID。</summary>
        public int[] GetProcessIds()
        {
            int[] found = FindByMarker();
            if (found.Length > 0)
            {
                return found;
            }
            int pid = ReadMetaPid();
            if (pid > 0 && IsProcessAlive(pid))
            {
                return new int[] { pid };
            }
            return new int[0];
        }

        public int Start(string exePath, string[] extraArgs)
        {
            if (IsRunning())
            {
                return -1;
            }
            EnsureLayout();

            Dictionary<string, string> overrides = BuildEnvironment();
            IntPtr envBlock = Win32Api.BuildEnvironmentBlock(overrides);
            Win32Api.STARTUPINFO si = new Win32Api.STARTUPINFO();
            si.cb = (uint)Marshal.SizeOf(typeof(Win32Api.STARTUPINFO));

            System.Text.StringBuilder cmd = new System.Text.StringBuilder();
            cmd.Append(Win32Api.Quote(exePath));
            // 显式传 user-data-dir：既保证隔离，又作为命令行标记用于识别容器进程
            cmd.Append(" --user-data-dir=");
            cmd.Append(Win32Api.Quote(UserDataDir));
            if (extraArgs != null)
            {
                foreach (string arg in extraArgs)
                {
                    cmd.Append(' ');
                    cmd.Append(Win32Api.Quote(arg));
                }
            }

            try
            {
                Win32Api.PROCESS_INFORMATION pi;
                bool ok = Win32Api.CreateProcess(
                    exePath, cmd.ToString(), IntPtr.Zero, IntPtr.Zero, false,
                    Win32Api.CreateUnicodeEnvironment, envBlock, Path.GetDirectoryName(exePath), ref si, out pi);
                if (!ok)
                {
                    throw new System.ComponentModel.Win32Exception(
                        Marshal.GetLastWin32Error(), "创建进程失败");
                }
                try
                {
                    WriteMeta((int)pi.dwProcessId, exePath);
                    return (int)pi.dwProcessId;
                }
                finally
                {
                    Win32Api.CloseHandle(pi.hThread);
                    Win32Api.CloseHandle(pi.hProcess);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(envBlock);
            }
        }

        public bool Stop()
        {
            bool stopped = false;
            int mainPid = ReadMetaPid();
            if (mainPid > 0 && IsProcessAlive(mainPid))
            {
                // 按主进程树终止，一次性带走大部分子进程
                KillTree(mainPid);
                stopped = true;
            }
            // 再按容器目录标记清扫剩余进程（含被重新挂接的孤儿进程）
            for (int round = 0; round < 3; round++)
            {
                int[] pids = FindByMarker();
                if (pids.Length == 0)
                {
                    break;
                }
                foreach (int pid in pids)
                {
                    KillProcess(pid);
                    stopped = true;
                }
                Thread.Sleep(400);
            }
            return stopped;
        }

        public void Reset()
        {
            Stop();
            Thread.Sleep(500);
            for (int attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    if (Directory.Exists(RootDir))
                    {
                        Directory.Delete(RootDir, true);
                    }
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(500);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(500);
                }
            }
            throw new IOException("容器目录被占用，无法删除：" + RootDir);
        }

        private int[] FindByMarker()
        {
            List<int> list = new List<int>();
            string marker = UserDataDir;
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = '" + TraeProcessName + "'"))
                {
                    using (ManagementObjectCollection items = searcher.Get())
                    {
                        foreach (ManagementBaseObject item in items)
                        {
                            string commandLine = item["CommandLine"] as string;
                            if (commandLine != null
                                && commandLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                object value = item["ProcessId"];
                                if (value != null)
                                {
                                    list.Add(Convert.ToInt32(value));
                                }
                            }
                        }
                    }
                }
            }
            catch (ManagementException) { }
            catch (UnauthorizedAccessException) { }
            catch (COMException) { }
            return list.ToArray();
        }

        private static bool IsProcessAlive(int pid)
        {
            try
            {
                Process process = Process.GetProcessById(pid);
                return !process.HasExited
                    && string.Equals(process.ProcessName + ".exe", TraeProcessName, StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static void KillProcess(int pid)
        {
            try
            {
                Process process = Process.GetProcessById(pid);
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        private static void KillTree(int pid)
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo("taskkill", "/PID " + pid.ToString() + " /T /F");
                info.CreateNoWindow = true;
                info.UseShellExecute = false;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                Process.Start(info).WaitForExit(5000);
            }
            catch (Exception) { }
        }

        private void WriteMeta(int pid, string exePath)
        {
            try
            {
                string[] lines = new string[]
                {
                    "pid=" + pid.ToString(),
                    "exe=" + exePath,
                    "started=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };
                File.WriteAllLines(MetaFile, lines);
            }
            catch (IOException) { }
        }

        private int ReadMetaPid()
        {
            try
            {
                if (!File.Exists(MetaFile))
                {
                    return 0;
                }
                foreach (string line in File.ReadAllLines(MetaFile))
                {
                    if (line.StartsWith("pid=", StringComparison.OrdinalIgnoreCase))
                    {
                        int pid;
                        if (int.TryParse(line.Substring(4), out pid))
                        {
                            return pid;
                        }
                    }
                }
            }
            catch (IOException) { }
            return 0;
        }
    }
}
