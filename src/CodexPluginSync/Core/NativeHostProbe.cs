using Microsoft.Win32;

namespace CodexPluginSync.Core
{
    /// <summary>
    /// §9 native-messaging probe (read-only). M1 only detects Chrome; the Edge provider is
    /// deliberately not implemented until §16.2 case 6 is verified on a real machine (C-7).
    /// </summary>
    public static class NativeHostProbe
    {
        public static NativeHostEntry ProbeChrome(string expectedExecutable)
        {
            NativeHostEntry entry = new NativeHostEntry();
            entry.TargetPath = expectedExecutable;

            string manifestPath = null;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(CodexPaths.ChromeNativeHostRegistryPath, false))
                {
                    if (key == null)
                    {
                        entry.State = "not-registered";
                        entry.Registered = false;
                        return entry;
                    }
                    entry.Registered = true;
                    object value = key.GetValue(null);
                    manifestPath = value as string;
                }
            }
            catch (System.Exception ex)
            {
                entry.State = "no-permission";
                entry.Registered = null;
                entry.Detail = ex.GetType().Name;
                return entry;
            }

            if (string.IsNullOrEmpty(manifestPath))
            {
                entry.State = "corrupt";
                entry.Registered = true;
                entry.Detail = "registry-default-empty";
                return entry;
            }

            entry.ManifestPath = manifestPath;
            if (!System.IO.File.Exists(manifestPath))
            {
                entry.State = "stale";
                entry.Detail = "manifest-missing";
                return entry;
            }

            try
            {
                NativeHostManifestDto dto = JsonRead.FromFile<NativeHostManifestDto>(manifestPath);
                string currentPath = dto != null ? dto.Path : null;
                if (string.IsNullOrEmpty(currentPath))
                {
                    entry.State = "corrupt";
                    entry.Detail = "manifest-path-empty";
                    return entry;
                }
                entry.CurrentExecutable = currentPath;
                if (expectedExecutable == null)
                {
                    entry.State = "unknown";
                    entry.Detail = "expected-unresolved";
                    return entry;
                }
                // Compare resolved targets, not strings: ...\chrome\latest\... is a junction
                // to ...\chrome\<version>\... and is a correct registration.
                if (!System.IO.File.Exists(currentPath))
                {
                    entry.State = "stale";
                    entry.Detail = "registered-executable-missing";
                    return entry;
                }
                bool same = PathIdentity.AreSameTarget(currentPath, expectedExecutable);
                entry.State = same ? "ok" : "outdated";
                entry.Detail = same ? null : "resolved-target-differs";
                return entry;
            }
            catch (System.Exception ex)
            {
                entry.State = "corrupt";
                entry.Detail = "manifest-unreadable:" + ex.GetType().Name;
                return entry;
            }
        }
    }

    [System.Runtime.Serialization.DataContract]
    public sealed class NativeHostManifestDto
    {
        [System.Runtime.Serialization.DataMember(Name = "name")]
        public string Name;

        [System.Runtime.Serialization.DataMember(Name = "path")]
        public string Path;

        [System.Runtime.Serialization.DataMember(Name = "type")]
        public string Type;
    }
}
