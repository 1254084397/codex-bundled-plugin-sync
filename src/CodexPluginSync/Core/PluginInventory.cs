using System;
using System.Collections.Generic;
using System.IO;

namespace CodexPluginSync.Core
{
    /// <summary>
    /// Reads plugin manifests on both sides (Desktop bundle / persistent marketplace / caches).
    /// Ground truth for the on-disk layout is scripts/check-openai-bundled.ps1.
    /// </summary>
    public static class PluginInventory
    {
        public static readonly string[] ManagedPlugins = new string[] { "browser", "chrome", "computer-use" };

        /// <summary>Enumerates &lt;root&gt;\plugins\*\.codex-plugin\plugin.json, sorted by name.</summary>
        public static List<PluginRef> ReadBundle(string bundleRoot)
        {
            List<PluginRef> result = new List<PluginRef>();
            string pluginsDirectory = Path.Combine(bundleRoot, "plugins");
            if (!Directory.Exists(pluginsDirectory))
            {
                return result;
            }
            string[] directories;
            try
            {
                directories = Directory.GetDirectories(pluginsDirectory);
            }
            catch (Exception)
            {
                return result;
            }
            Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < directories.Length; i++)
            {
                string manifestPath = Path.Combine(Path.Combine(directories[i], ".codex-plugin"), "plugin.json");
                PluginManifestDto manifest = TryReadManifest(manifestPath);
                if (manifest == null || string.IsNullOrEmpty(manifest.Name))
                {
                    continue;
                }
                PluginRef reference = new PluginRef();
                reference.Name = manifest.Name;
                reference.Version = manifest.Version;
                result.Add(reference);
            }
            result.Sort(CompareByName);
            return result;
        }

        public static PluginManifestDto TryReadManifest(string manifestPath)
        {
            try
            {
                if (!File.Exists(manifestPath))
                {
                    return null;
                }
                return JsonRead.FromFile<PluginManifestDto>(manifestPath);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// §7.1 step 5/6: expected set = materialization key ∩ Desktop bundle; falls back to the
        /// package marketplace manifest on first run. The key exists precisely to avoid syncing
        /// plugins the Desktop package ships but never persists.
        /// </summary>
        public static List<PluginRef> ReadExpectedPlugins(string sourceBundleRoot, string marketBundleRoot,
            List<PluginRef> desktopPlugins, out string diagnostics)
        {
            diagnostics = null;
            Dictionary<string, string> desktopByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < desktopPlugins.Count; i++)
            {
                desktopByName[desktopPlugins[i].Name] = desktopPlugins[i].Version;
            }

            string keyPath = Path.Combine(marketBundleRoot, ".materialization-key");
            List<PluginEntryDto> names = null;
            string source = "materialization-key";

            if (File.Exists(keyPath))
            {
                try
                {
                    PluginListDto key = JsonRead.FromFile<PluginListDto>(keyPath);
                    names = key != null ? key.Plugins : null;
                }
                catch (Exception ex)
                {
                    diagnostics = "materialization-key-unreadable:" + ex.GetType().Name;
                    names = null;
                }
            }

            if (names == null)
            {
                source = "marketplace-manifest";
                string manifestPath = Path.Combine(sourceBundleRoot, ".agents", "plugins", "marketplace.json");
                try
                {
                    if (File.Exists(manifestPath))
                    {
                        PluginListDto manifest = JsonRead.FromFile<PluginListDto>(manifestPath);
                        names = manifest != null ? manifest.Plugins : null;
                    }
                    else
                    {
                        diagnostics = "marketplace-manifest-missing:" + manifestPath;
                        return new List<PluginRef>();
                    }
                }
                catch (Exception ex)
                {
                    diagnostics = "marketplace-manifest-unreadable:" + ex.GetType().Name;
                    return new List<PluginRef>();
                }
            }

            List<PluginRef> expected = new List<PluginRef>();
            if (names == null)
            {
                return expected;
            }
            for (int i = 0; i < names.Count; i++)
            {
                PluginEntryDto entry = names[i];
                if (entry == null || string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }
                string version;
                if (!desktopByName.TryGetValue(entry.Name, out version))
                {
                    continue;
                }
                PluginRef reference = new PluginRef();
                reference.Name = entry.Name;
                reference.Version = version;
                expected.Add(reference);
            }
            expected.Sort(CompareByName);
            diagnostics = diagnostics ?? ("expected-source=" + source);
            return expected;
        }

        /// <summary>Newest cached version of a managed plugin, or null when no cache manifest exists.</summary>
        public static string ReadCacheVersion(string cacheParent, string pluginName)
        {
            string pluginRoot = Path.Combine(cacheParent, pluginName);
            if (!Directory.Exists(pluginRoot))
            {
                return null;
            }
            try
            {
                // 不跟随 reparse point：实机上 cache\<plugin>\latest 是指向版本目录的 junction，
                // 若沿用它遍历，遇到自引用 junction 会无限递归（§7.4 也不允许递归 junction）。
                List<string> manifests = new List<string>();
                CollectManifests(pluginRoot, manifests, 0);
                string newestVersion = null;
                DateTime newestWrite = DateTime.MinValue;
                for (int i = 0; i < manifests.Count; i++)
                {
                    string candidate = manifests[i];
                    if (!candidate.EndsWith(@"\.codex-plugin\plugin.json", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    DateTime writeUtc;
                    try
                    {
                        writeUtc = File.GetLastWriteTimeUtc(candidate);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    PluginManifestDto manifest = TryReadManifest(candidate);
                    if (manifest == null || string.IsNullOrEmpty(manifest.Version))
                    {
                        continue;
                    }
                    if (newestVersion == null || writeUtc > newestWrite)
                    {
                        newestVersion = manifest.Version;
                        newestWrite = writeUtc;
                    }
                }
                return newestVersion;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void CollectManifests(string directory, List<string> results, int depth)
        {
            if (depth > 8)
            {
                return;
            }
            try
            {
                string[] files = Directory.GetFiles(directory, "plugin.json");
                for (int i = 0; i < files.Length; i++)
                {
                    results.Add(files[i]);
                }
                string[] children = Directory.GetDirectories(directory);
                for (int i = 0; i < children.Length; i++)
                {
                    FileAttributes attributes = File.GetAttributes(children[i]);
                    if ((attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                    {
                        continue;
                    }
                    CollectManifests(children[i], results, depth + 1);
                }
            }
            catch (Exception)
            {
            }
        }

        public static string Format(List<PluginRef> plugins)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < plugins.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append('\n');
                }
                sb.Append(plugins[i].Name).Append('@').Append(plugins[i].Version);
            }
            return sb.ToString();
        }

        public static bool SameSet(List<PluginRef> left, List<PluginRef> right)
        {
            return string.Equals(Format(left), Format(right), StringComparison.Ordinal);
        }

        private static int CompareByName(PluginRef a, PluginRef b)
        {
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }
    }
}
