using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
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
        /// <summary>容器专用工作区目录；Trae 只有打开工作区后才会加载用户扩展。</summary>
        public string WorkspaceDir { get { return Path.Combine(RootDir, "workspace"); } }
        public string MetaFile { get { return Path.Combine(RootDir, "container.ini"); } }

        /// <summary>Trae 的用户扩展目录；隔离 USERPROFILE 时位于容器内的 Home。</summary>
        public string ExtensionsDir
        {
            get
            {
                string profile = IsolateHome
                    ? HomeDir
                    : Environment.GetEnvironmentVariable("USERPROFILE");
                return Path.Combine(profile, ".trae-cn", "extensions");
            }
        }

        public void EnsureLayout()
        {
            Directory.CreateDirectory(RootDir);
            Directory.CreateDirectory(PortableDir);
            Directory.CreateDirectory(UserDataDir);
            Directory.CreateDirectory(RoamingDir);
            Directory.CreateDirectory(LocalDir);
            Directory.CreateDirectory(TempDir);
            Directory.CreateDirectory(WorkspaceDir);
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

        /// <summary>
        /// 把指定的扩展目录复制到容器扩展目录（例如自动签到插件），并写入 Trae 的
        /// extensions.json 索引。只复制文件夹是不够的：Trae 会把它当作“已移除”的残留
        /// 扩展写进 .obsolete，导致扩展不加载。
        /// 已存在的同名扩展目录会被跳过，避免覆盖运行中的扩展。
        /// 返回实际复制的扩展目录名。
        /// </summary>
        public string[] SyncExtensions(string[] sources)
        {
            List<string> copied = new List<string>();
            if (sources == null || sources.Length == 0)
            {
                return copied.ToArray();
            }

            string targetRoot = ExtensionsDir;
            Directory.CreateDirectory(targetRoot);
            foreach (string raw in sources)
            {
                string source = raw;
                if (string.IsNullOrEmpty(source) || !Directory.Exists(source))
                {
                    continue;
                }
                string name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar));
                string target = Path.Combine(targetRoot, name);
                if (SamePath(source, target))
                {
                    // 未隔离 USERPROFILE 时源与目标可能是同一个目录
                    continue;
                }
                if (!Directory.Exists(target))
                {
                    try
                    {
                        CopyDirectory(source, target);
                        copied.Add(name);
                    }
                    catch (IOException)
                    {
                        // 复制失败不影响启动
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
                if (Directory.Exists(target))
                {
                    try
                    {
                        EnsureExtensionIndex(target);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            return copied.ToArray();
        }

        private static bool SamePath(string first, string second)
        {
            return string.Equals(
                Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>把扩展登记到容器扩展目录的 extensions.json，并从 .obsolete 中清除。</summary>
        private void EnsureExtensionIndex(string extensionDir)
        {
            string name = Path.GetFileName(extensionDir.TrimEnd(Path.DirectorySeparatorChar));
            string packageFile = Path.Combine(extensionDir, "package.json");
            string publisher = ReadJsonString(packageFile, "publisher");
            string extensionName = ReadJsonString(packageFile, "name");
            string version = ReadJsonString(packageFile, "version");
            if (string.IsNullOrEmpty(publisher) || string.IsNullOrEmpty(extensionName) || string.IsNullOrEmpty(version))
            {
                return;
            }

            string id = publisher + "." + extensionName;
            string indexPath = Path.Combine(ExtensionsDir, "extensions.json");
            string existing = File.Exists(indexPath) ? File.ReadAllText(indexPath).Trim() : "[]";
            if (existing.Length == 0 || existing[0] != '[')
            {
                existing = "[]";
            }

            if (existing.IndexOf("\"id\":\"" + id + "\"", StringComparison.OrdinalIgnoreCase) < 0)
            {
                string entry = BuildIndexEntry(id, version, extensionDir, name);
                string body = existing.Substring(1, existing.Length - 2).Trim();
                string output = body.Length == 0
                    ? "[" + entry + "]"
                    : "[" + body + "," + entry + "]";
                File.WriteAllText(indexPath, output, new UTF8Encoding(false));
            }
            RemoveFromObsolete(name);
        }

        private static string BuildIndexEntry(string id, string version, string extensionDir, string relativeLocation)
        {
            string fsPath = extensionDir.TrimEnd(Path.DirectorySeparatorChar);
            // Trae 的扩展位置使用 file URI；Windows 盘符统一小写，与 VS Code 一致
            string path = "/" + fsPath.Replace('\\', '/');
            if (path.Length > 2 && path[2] == ':')
            {
                path = "/" + char.ToLowerInvariant(path[1]) + path.Substring(2);
            }
            string external = "file:///" + path.Substring(1);
            long installed = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;

            StringBuilder sb = new StringBuilder();
            sb.Append("{\"identifier\":{\"id\":\"").Append(id).Append("\"},");
            sb.Append("\"version\":\"").Append(version).Append("\",");
            sb.Append("\"location\":{\"$mid\":1,\"fsPath\":\"").Append(fsPath.Replace("\\", "\\\\")).Append("\",\"_sep\":1,");
            sb.Append("\"external\":\"").Append(external.Replace("\\", "\\\\")).Append("\",");
            sb.Append("\"path\":\"").Append(path).Append("\",\"scheme\":\"file\"},");
            sb.Append("\"relativeLocation\":\"").Append(relativeLocation).Append("\",");
            sb.Append("\"metadata\":{\"installedTimestamp\":").Append(installed.ToString());
            sb.Append(",\"pinned\":true,\"source\":\"vsix\"}}");
            return sb.ToString();
        }

        private static string ReadJsonString(string file, string key)
        {
            if (!File.Exists(file))
            {
                return null;
            }
            Match match = Regex.Match(File.ReadAllText(file),
                "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>从 .obsolete 中移除该扩展（键是扩展目录名，值是布尔）。</summary>
        private void RemoveFromObsolete(string relativeLocation)
        {
            string file = Path.Combine(ExtensionsDir, ".obsolete");
            if (!File.Exists(file))
            {
                return;
            }
            string text = File.ReadAllText(file).Trim();
            if (text.IndexOf(relativeLocation, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return;
            }
            List<string> kept = new List<string>();
            foreach (string part in text.Trim('{', '}', ' ').Split(','))
            {
                string item = part.Trim();
                if (item.Length == 0 || item.IndexOf(relativeLocation, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }
                kept.Add(item);
            }
            if (kept.Count == 0)
            {
                File.Delete(file);
                return;
            }
            File.WriteAllText(file, "{" + string.Join(",", kept.ToArray()) + "}", new UTF8Encoding(false));
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            }
            foreach (string dir in Directory.GetDirectories(source))
            {
                CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
            }
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
            // 容器内的 WebView2/Edge crashpad 等非 Trae 进程也会占用容器文件
            if (KillProcessesUsingRoot())
            {
                stopped = true;
            }
            // 继承容器环境启动的浏览器（msedge.exe 等）命令行里没有容器路径，
            // 只能通过“谁锁着容器里的文件”来定位
            if (KillLockHolders())
            {
                stopped = true;
            }
            return stopped;
        }

        /// <summary>
        /// 终止锁定容器文件的浏览器类进程。容器进程会把 LOCALAPPDATA 传给默认浏览器，
        /// 于是 msedge.exe 会用容器内的 Edge 配置目录并长期锁住 lockfile/Cookies，
        /// 导致容器目录无法删除。
        /// </summary>
        private bool KillLockHolders()
        {
            List<string> files = new List<string>();
            CollectFiles(RootDir, files, 4000);
            if (files.Count == 0)
            {
                return false;
            }

            int[] pids;
            try
            {
                pids = RestartManager.GetLockHolderProcessIds(files.ToArray());
            }
            catch (Exception)
            {
                return false;
            }

            bool killed = false;
            int self = Process.GetCurrentProcess().Id;
            foreach (int pid in pids)
            {
                if (pid == self)
                {
                    continue;
                }
                string name;
                try
                {
                    using (Process process = Process.GetProcessById(pid))
                    {
                        name = process.ProcessName.ToLowerInvariant() + ".exe";
                    }
                }
                catch (ArgumentException) { continue; }
                catch (InvalidOperationException) { continue; }

                // 只清理由容器派生的浏览器进程，避免误杀客户端本身或用户程序
                if (name != "msedge.exe"
                    && name != "msedgewebview2.exe"
                    && name != "crashpad_handler.exe")
                {
                    continue;
                }
                KillTree(pid);
                killed = true;
            }
            return killed;
        }

        private static void CollectFiles(string dir, List<string> files, int limit)
        {
            if (files.Count >= limit || !Directory.Exists(dir))
            {
                return;
            }
            try
            {
                foreach (string file in Directory.GetFiles(dir))
                {
                    files.Add(file);
                    if (files.Count >= limit)
                    {
                        return;
                    }
                }
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    CollectFiles(sub, files, limit);
                    if (files.Count >= limit)
                    {
                        return;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>终止命令行里带容器根目录的容器进程（例如 WebView2 的 crashpad）。</summary>
        private bool KillProcessesUsingRoot()
        {
            bool killed = false;
            int self = Process.GetCurrentProcess().Id;
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, Name, CommandLine FROM Win32_Process"))
                {
                    using (ManagementObjectCollection items = searcher.Get())
                    {
                        foreach (ManagementBaseObject item in items)
                        {
                            string name = item["Name"] as string;
                            string commandLine = item["CommandLine"] as string;
                            // 只清理容器自身会派生的进程，避免误杀命令行里恰好带该路径的终端/编辑器
                            if (name == null || commandLine == null
                                || !IsContainerChildProcess(name)
                                || commandLine.IndexOf(RootDir, StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                continue;
                            }
                            object value = item["ProcessId"];
                            if (value == null)
                            {
                                continue;
                            }
                            int pid = Convert.ToInt32(value);
                            if (pid == self)
                            {
                                continue;
                            }
                            KillProcess(pid);
                            killed = true;
                        }
                    }
                }
            }
            catch (ManagementException) { }
            catch (UnauthorizedAccessException) { }
            catch (COMException) { }
            return killed;
        }

        private static bool IsContainerChildProcess(string name)
        {
            return string.Equals(name, TraeProcessName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "msedge.exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "msedgewebview2.exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "crashpad_handler.exe", StringComparison.OrdinalIgnoreCase);
        }

        public void Reset()
        {
            Stop();
            Thread.Sleep(500);
            string lastError = null;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    if (Directory.Exists(RootDir))
                    {
                        DeleteTree(RootDir);
                    }
                    return;
                }
                catch (IOException ex)
                {
                    lastError = ex.Message;
                }
                catch (UnauthorizedAccessException ex)
                {
                    lastError = ex.Message;
                }
                // 仍被占用：定位并终止占用容器文件的浏览器进程后再试
                if (KillLockHolders())
                {
                    Thread.Sleep(800);
                }
                else
                {
                    Thread.Sleep(500);
                }
            }
            throw new IOException("容器目录被占用，无法删除：" + RootDir
                + (string.IsNullOrEmpty(lastError) ? "" : "（" + lastError + "）"));
        }

        /// <summary>
        /// 递归删除目录：先清除只读属性再删文件。Trae 的 ai-agent 快照里有 git 对象
        /// （只读文件），Directory.Delete(recursive) 遇到它们会直接失败。
        /// 注：.NET 的 File.SetAttributes 在这些文件上会被拒绝，必须走 Win32 SetFileAttributes。
        /// </summary>
        private static void DeleteTree(string dir)
        {
            foreach (string file in Directory.GetFiles(dir))
            {
                try
                {
                    FileAttributes attrs = File.GetAttributes(file);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                    {
                        uint normal = (uint)((int)attrs & ~(int)FileAttributes.ReadOnly);
                        SetFileAttributes(file, normal);
                    }
                    File.Delete(file);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
            foreach (string sub in Directory.GetDirectories(dir))
            {
                DeleteTree(sub);
            }
            Directory.Delete(dir, false);
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetFileAttributes(string fileName, uint fileAttributes);

        /// <summary>用 Windows Restart Manager 查询锁定指定文件的进程（无需外部工具）。</summary>
        private static class RestartManager
        {
            [StructLayout(LayoutKind.Sequential)]
            private struct RM_UNIQUE_PROCESS
            {
                public int dwProcessId;
                public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct RM_PROCESS_INFO
            {
                public RM_UNIQUE_PROCESS Process;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
                public string strAppName;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
                public string strServiceShortName;
                public int ApplicationType;
                public uint AppStatus;
                public uint TSSessionId;
                [MarshalAs(UnmanagedType.Bool)]
                public bool bRestartable;
            }

            [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
            private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

            [DllImport("rstrtmgr.dll")]
            private static extern int RmEndSession(uint pSessionHandle);

            [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
            private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
                uint nApplications, RM_UNIQUE_PROCESS[] rgApplications, uint nServices, string[] rgsServiceNames);

            [DllImport("rstrtmgr.dll")]
            private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded,
                ref uint pnProcInfo, [In, Out] RM_PROCESS_INFO[] rgAffectedApps, ref uint lpdwRebootReasons);

            /// <summary>返回锁定这些文件的进程 ID；查询失败时返回空数组。</summary>
            public static int[] GetLockHolderProcessIds(string[] files)
            {
                uint session;
                if (RmStartSession(out session, 0, "TraeSandbox-" + Guid.NewGuid().ToString("N")) != 0)
                {
                    return new int[0];
                }
                try
                {
                    if (RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null) != 0)
                    {
                        return new int[0];
                    }
                    uint needed = 0;
                    uint count = 0;
                    uint reasons = 0;
                    RmGetList(session, out needed, ref count, null, ref reasons);
                    if (needed == 0)
                    {
                        return new int[0];
                    }
                    count = needed;
                    RM_PROCESS_INFO[] infos = new RM_PROCESS_INFO[count];
                    if (RmGetList(session, out needed, ref count, infos, ref reasons) != 0)
                    {
                        return new int[0];
                    }
                    int[] pids = new int[count];
                    for (int i = 0; i < count; i++)
                    {
                        pids[i] = infos[i].Process.dwProcessId;
                    }
                    return pids;
                }
                finally
                {
                    RmEndSession(session);
                }
            }
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
