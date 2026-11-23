using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexPluginSync.Core
{
    /// <summary>
    /// Path identity for comparisons that must not be fooled by junctions/symlinks.
    /// Real-machine finding: Codex keeps <c>plugins\cache\openai-bundled\chrome\latest</c> as a
    /// junction to the current version directory, and the Chrome native-messaging manifest points
    /// at <c>...\chrome\latest\extension-host\...\extension-host.exe</c>. A plain string compare
    /// reports that as "outdated" — a false positive that would make the tool rewrite a correct
    /// manifest. We therefore compare resolved final paths.
    /// </summary>
    public static class PathIdentity
    {
        private const uint FileShareAll = 0x00000007;      // read | write | delete
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000; // required to open a directory handle
        private const uint VolumeNameDos = 0;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string fileName, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetFinalPathNameByHandleW(IntPtr file, StringBuilder path, uint pathLength, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>
        /// Returns the fully resolved path (junctions and symlinks expanded), or the normalized
        /// input path when the OS cannot resolve it. Returns null only when the input is unusable.
        /// </summary>
        public static string Resolve(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return null;
            }

            IntPtr handle = InvalidHandle;
            try
            {
                handle = CreateFileW(full, 0, FileShareAll, IntPtr.Zero, OpenExisting,
                    FileFlagBackupSemantics, IntPtr.Zero);
                if (handle == InvalidHandle)
                {
                    return full;
                }
                StringBuilder buffer = new StringBuilder(1024);
                uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, VolumeNameDos);
                if (length == 0 || length >= buffer.Capacity)
                {
                    return full;
                }
                return StripExtendedPrefix(buffer.ToString());
            }
            catch (Exception)
            {
                return full;
            }
            finally
            {
                if (handle != InvalidHandle)
                {
                    CloseHandle(handle);
                }
            }
        }

        /// <summary>True when both paths exist-independent-equivalent after resolving links.</summary>
        public static bool AreSameTarget(string left, string right)
        {
            string resolvedLeft = Resolve(left);
            string resolvedRight = Resolve(right);
            if (resolvedLeft == null || resolvedRight == null)
            {
                return false;
            }
            return string.Equals(resolvedLeft.TrimEnd('\\'), resolvedRight.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string StripExtendedPrefix(string path)
        {
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            {
                return @"\\" + path.Substring(8);
            }
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                return path.Substring(4);
            }
            return path;
        }
    }
}
