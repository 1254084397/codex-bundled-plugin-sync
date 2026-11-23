using System;
using System.IO;

namespace CodexPluginSync.Core
{
    /// <summary>
    /// §8 EFS facts. M1 only probes capability and reads the current attribute; the
    /// apply/restore sequence belongs to the Sync engine.
    /// Non-encrypted local volumes are reported as unsupported rather than as an error.
    /// </summary>
    public static class EfsProbe
    {
        public static bool? IsEncrypted(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                return null;
            }
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                return (attributes & FileAttributes.Encrypted) == FileAttributes.Encrypted;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static bool IsSupported()
        {
            string directory = CodexPaths.ToolDataDirectory;
            string probePath = Path.Combine(directory, "_efs-probe.tmp");
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(probePath, "probe");
                File.Encrypt(probePath);
                FileAttributes attributes = File.GetAttributes(probePath);
                bool encrypted = (attributes & FileAttributes.Encrypted) == FileAttributes.Encrypted;
                return encrypted;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(probePath))
                    {
                        File.Delete(probePath);
                    }
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
