using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace CodexPluginSync.Core
{
    public sealed class SyncOptions
    {
        public string CodexHome;
        public string SourceBundleOverride;
        public bool Force;
        /// <summary>selftest 专用：跳过 Chrome native host（不读注册表、不改真实清单）。</summary>
        public bool SkipNativeHost;
        public TextWriter Out;
        public TextWriter Err;
    }

    public sealed class SyncRunResult
    {
        public int ExitCode = ExitCodesLocator.Error;
        public string Status;
        public string Message;
        public List<string> Lines = new List<string>();
    }

    /// <summary>避免 Core 依赖 Cli：这里只声明本类用到的退出码。</summary>
    public static class ExitCodesLocator
    {
        public const int Ok = 0;
        public const int Error = 1;
        public const int NeedsAttention = 2;
    }

    /// <summary>
    /// `--sync` 的 C# 实现（M1 增量 3 第一步）。
    /// 已完成：组件集合 + stage 复制（保留 junction）+ SyncTransaction 七阶段 + 摘要校验。
    /// 未接线（本 build 明确报告）：EFS 开启/恢复、Chrome native host 写入。
    /// </summary>
    public static class SyncEngine
    {
        private static readonly string[] ManagedPlugins = new string[] { "browser", "chrome", "computer-use" };

        public static SyncRunResult Run(SyncOptions options)
        {
            SyncRunResult result = new SyncRunResult();
            TextWriter output = options.Out ?? Console.Out;
            TextWriter error = options.Err ?? Console.Error;

            try
            {
                Settings settings = Settings.Load(null);
                string codexHome = CodexPaths.ResolveCodexHome(options.CodexHome ?? settings.CodexHome);
                CodexPaths paths = new CodexPaths(codexHome);

                string sourceBundle = options.SourceBundleOverride;
                string appxVersion = "override";
                if (string.IsNullOrEmpty(sourceBundle))
                {
                    DesktopPackage package = AppxLocator.Locate();
                    if (!package.Found)
                    {
                        result.Message = "找不到 Codex Desktop 的内置插件目录：" + package.Error;
                        error.WriteLine(result.Message);
                        return result;
                    }
                    sourceBundle = package.SourceBundle;
                    appxVersion = package.Version;
                }

                string chromeVersion = null;
                NativeHostResult nativeHost = null;
                List<SyncTarget> targets = new List<SyncTarget>();
                string runId = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" +
                    Guid.NewGuid().ToString("N").Substring(0, 6);
                string backupDirectory = Path.Combine(paths.BackupsRoot, "openai-bundled-sync-" + runId);

                // 1) Marketplace -------------------------------------------------
                bool bundleNeedsSync = options.Force || !MarketplaceIsCurrent(sourceBundle, paths.MarketBundle);
                if (bundleNeedsSync)
                {
                    SyncTarget marketplace = new SyncTarget();
                    marketplace.Component = "marketplace";
                    marketplace.LivePath = paths.MarketBundle;
                    marketplace.StagePath = Path.Combine(paths.MarketParent, "openai-bundled.stage-" + runId);
                    marketplace.BackupPath = Path.Combine(backupDirectory, "marketplace.previous");
                    marketplace.ExistedBefore = Directory.Exists(paths.MarketBundle);
                    Directory.CreateDirectory(paths.MarketParent);
                    Installer.CopyTreePreservingLinks(sourceBundle, marketplace.StagePath);
                    targets.Add(marketplace);
                    result.Lines.Add("staged marketplace（" + CountFiles(marketplace.StagePath) + " 个文件）");
                }

                // 2) 受管缓存 -----------------------------------------------------
                for (int i = 0; i < ManagedPlugins.Length; i++)
                {
                    string plugin = ManagedPlugins[i];
                    string sourcePlugin = Path.Combine(Path.Combine(sourceBundle, "plugins"), plugin);
                    if (!Directory.Exists(sourcePlugin))
                    {
                        result.Message = "内置包中缺少插件：" + plugin;
                        error.WriteLine(result.Message);
                        return result;
                    }
                    string manifestPath = Path.Combine(sourcePlugin, ".codex-plugin\\plugin.json");
                    PluginManifestDto manifest = PluginInventory.TryReadManifest(manifestPath);
                    if (manifest == null || string.IsNullOrEmpty(manifest.Version))
                    {
                        result.Message = "内置插件清单不可读：" + manifestPath;
                        error.WriteLine(result.Message);
                        return result;
                    }
                    string version = manifest.Version;
                    if (plugin == "chrome") { chromeVersion = version; }
                    string livePath = Path.Combine(paths.CacheParent, plugin);
                    string cachedManifest = Path.Combine(Path.Combine(livePath, version), ".codex-plugin\\plugin.json");
                    if (!options.Force && File.Exists(cachedManifest))
                    {
                        result.Lines.Add(plugin + "：已是 " + version + "，跳过");
                        continue;
                    }

                    SyncTarget target = new SyncTarget();
                    target.Component = "cache:" + plugin;
                    target.LivePath = livePath;
                    target.StagePath = Path.Combine(paths.CacheParent, plugin + ".stage-" + runId);
                    target.BackupPath = Path.Combine(backupDirectory, "cache-" + plugin + ".previous");
                    target.ExistedBefore = Directory.Exists(livePath);

                    string versionStage = Path.Combine(target.StagePath, version);
                    Directory.CreateDirectory(target.StagePath);
                    Installer.CopyTreePreservingLinks(sourcePlugin, versionStage);
                    targets.Add(target);
                    result.Lines.Add("staged " + plugin + "@" + version + "（" + CountFiles(versionStage) + " 个文件）");
                }

                if (targets.Count == 0)
                {
                    result.Status = "no-op";
                    result.Message = "全部组件已是最新，无需写入。";
                    result.ExitCode = ExitCodesLocator.Ok;
                    output.WriteLine(result.Message);
                    return result;
                }

                Directory.CreateDirectory(paths.BackupsRoot);
                SyncTransactionResult transaction;
                if (options.SkipNativeHost || string.IsNullOrEmpty(chromeVersion))
                {
                    transaction = SyncTransaction.Execute(paths, backupDirectory, runId, targets);
                }
                else
                {
                    string chromeVersionForHost = chromeVersion;
                    transaction = SyncTransaction.Execute(paths, backupDirectory, runId, targets, delegate
                    {
                        nativeHost = NativeHostWriter.Apply(codexHome, paths.CacheParent, chromeVersionForHost, backupDirectory);
                        if (!nativeHost.Skipped && !nativeHost.Applied)
                        {
                            throw new InvalidOperationException("Chrome native host 更新失败：" + nativeHost.Reason);
                        }
                    });
                }
                result.Status = transaction.Status;
                result.Message = transaction.Message;
                for (int i = 0; i < transaction.Actions.Count; i++)
                {
                    result.Lines.Add(transaction.Actions[i]);
                }

                if (transaction.Status == SyncTransaction.StatusCommitted)
                {
                    result.ExitCode = ExitCodesLocator.Ok;
                }
                else if (transaction.Status == SyncTransaction.StatusNeedsAttention)
                {
                    result.ExitCode = ExitCodesLocator.NeedsAttention;
                }
                else
                {
                    result.ExitCode = ExitCodesLocator.Error;
                }

                for (int i = 0; i < result.Lines.Count; i++)
                {
                    output.WriteLine(result.Lines[i]);
                }
                output.WriteLine("事务状态：" + transaction.Status + "（备份：" + backupDirectory + "）");
                if (nativeHost == null)
                {
                    output.WriteLine("Chrome native host：未处理（缺 chrome 版本信息）");
                }
                else if (nativeHost.Skipped)
                {
                    output.WriteLine("Chrome native host：" + nativeHost.Reason);
                }
                else
                {
                    output.WriteLine("Chrome native host：已更新为 " + nativeHost.Reason + "（原目标：" + nativeHost.PreviousTargetPath + "）");
                }
                output.WriteLine("注意：本 build 尚未接线 EFS（目录加密状态未改动）。");
                output.WriteLine("Desktop 包版本：" + appxVersion);
                return result;
            }
            catch (Exception ex)
            {
                result.Message = ex.Message;
                result.Status = "error";
                result.ExitCode = ExitCodesLocator.Error;
                error.WriteLine("同步失败：" + ex.Message);
                return result;
            }
        }

        private static bool MarketplaceIsCurrent(string sourceBundle, string marketBundle)
        {
            if (!Directory.Exists(marketBundle)) { return false; }
            string diagnostics;
            List<PluginRef> expected = PluginInventory.ReadExpectedPlugins(sourceBundle, marketBundle,
                PluginInventory.ReadBundle(sourceBundle), out diagnostics);
            if (diagnostics != null && diagnostics.Length > 0)
            {
                // 期望集合无法确定时按"需要同步"处理，交给事务做完整校验
                return false;
            }
            List<PluginRef> local = PluginInventory.ReadBundle(marketBundle);
            if (expected.Count != local.Count) { return false; }
            for (int i = 0; i < expected.Count; i++)
            {
                if (i >= local.Count) { return false; }
                if (expected[i].Name != local[i].Name || expected[i].Version != local[i].Version)
                {
                    return false;
                }
            }
            return true;
        }

        private static int CountFiles(string directory)
        {
            if (!Directory.Exists(directory)) { return 0; }
            return Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length;
        }
    }
}
