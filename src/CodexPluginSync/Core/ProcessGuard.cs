using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CodexPluginSync.Core
{
    /// <summary>§10.1 detection only. Closing processes is a GUI-only capability (M3).</summary>
    public static class ProcessGuard
    {
        public static readonly string[] DefaultLockingProcessNames =
            new string[] { "chrome", "msedge", "ChatGPT", "extension-host" };

        public static List<LockingProcess> Detect(List<string> extraNames)
        {
            List<LockingProcess> result = new List<LockingProcess>();
            List<string> names = new List<string>(DefaultLockingProcessNames);
            if (extraNames != null)
            {
                for (int i = 0; i < extraNames.Count; i++)
                {
                    string name = extraNames[i];
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }
                    if (!names.Exists(delegate(string existing) {
                        return string.Equals(existing, name, StringComparison.OrdinalIgnoreCase);
                    }))
                    {
                        names.Add(name);
                    }
                }
            }

            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                Process[] processes;
                try
                {
                    processes = Process.GetProcessesByName(name);
                }
                catch (Exception)
                {
                    continue;
                }
                for (int p = 0; p < processes.Length; p++)
                {
                    using (Process process = processes[p])
                    {
                        LockingProcess entry = new LockingProcess();
                        entry.Name = SafeName(process);
                        try
                        {
                            entry.Pid = process.Id;
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                        try
                        {
                            entry.HasWindow = process.MainWindowHandle != IntPtr.Zero;
                        }
                        catch (Exception)
                        {
                            entry.HasWindow = false;
                        }
                        result.Add(entry);
                    }
                }
            }
            result.Sort(delegate(LockingProcess a, LockingProcess b) { return a.Pid.CompareTo(b.Pid); });
            return result;
        }

        private static string SafeName(Process process)
        {
            try
            {
                return process.ProcessName;
            }
            catch (Exception)
            {
                return "unknown";
            }
        }
    }
}
