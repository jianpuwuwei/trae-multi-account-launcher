using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TraeSandbox
{
    /// <summary>Win32 调用：带自定义环境块创建进程、窗口前置。</summary>
    internal static class Win32Api
    {
        public const uint CreateUnicodeEnvironment = 0x00000400;

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFO
        {
            public uint cb;
            public IntPtr lpReserved;
            public IntPtr lpDesktop;
            public IntPtr lpTitle;
            public uint dwX;
            public uint dwY;
            public uint dwXSize;
            public uint dwYSize;
            public uint dwXCountChars;
            public uint dwYCountChars;
            public uint dwFillAttribute;
            public uint dwFlags;
            public ushort wShowWindow;
            public ushort cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CreateProcess(
            string lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
            bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory,
            ref STARTUPINFO si, out PROCESS_INFORMATION pi);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        /// <summary>构造 CREATE_UNICODE_ENVIRONMENT 需要的环境块；value 为 null 表示删除该变量。</summary>
        public static IntPtr BuildEnvironmentBlock(IDictionary<string, string> overrides)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry item in Environment.GetEnvironmentVariables())
            {
                string key = item.Key as string;
                if (key != null)
                {
                    map[key] = item.Value as string;
                }
            }
            foreach (KeyValuePair<string, string> item in overrides)
            {
                if (item.Value == null)
                {
                    map.Remove(item.Key);
                }
                else
                {
                    map[item.Key] = item.Value;
                }
            }

            List<string> entries = new List<string>();
            foreach (KeyValuePair<string, string> item in map)
            {
                entries.Add(item.Key + "=" + (item.Value == null ? string.Empty : item.Value));
            }
            string block = string.Join("\0", entries.ToArray()) + "\0\0";
            return Marshal.StringToHGlobalUni(block);
        }

        public static string Quote(string value)
        {
            if (value == null)
            {
                return "\"\"";
            }
            if (value.Length > 0 && value.IndexOfAny(new char[] { ' ', '\t', '"' }) < 0)
            {
                return value;
            }
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append('"');
            int backslashes = 0;
            foreach (char c in value)
            {
                if (c == '\\')
                {
                    backslashes++;
                    sb.Append(c);
                }
                else if (c == '"')
                {
                    sb.Append('\\', backslashes + 1);
                    sb.Append('"');
                    backslashes = 0;
                }
                else
                {
                    sb.Append('\\', backslashes);
                    backslashes = 0;
                    sb.Append(c);
                }
            }
            sb.Append('\\', backslashes);
            sb.Append('"');
            return sb.ToString();
        }
    }
}
