using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;

namespace CodexPluginSync.Core
{
    public sealed class DesktopPackage
    {
        public bool Found;
        public string Version;
        public string PackageFullName;
        public string InstallLocation;
        public string Error;

        public string SourceBundle
        {
            get
            {
                if (string.IsNullOrEmpty(InstallLocation))
                {
                    return null;
                }
                return Path.Combine(InstallLocation, @"app\resources\plugins\openai-bundled");
            }
        }
    }

    /// <summary>
    /// Locates the OpenAI.Codex AppX package. §4.2 keeps "locate" separate from "operate" so the
    /// Core can run against a fixture (--source-bundle) on machines without Codex installed.
    ///
    /// M1 implementation detail: uses the Get-AppxPackage fallback only (the documented fallback
    /// path in §7.1/§14). The PackageManager winmd projection is a M5 packaging concern because it
    /// needs Windows SDK reference assemblies, which the in-box MSBuild build must not depend on.
    /// </summary>
    public static class AppxLocator
    {
        [DataContract]
        private sealed class PackageDto
        {
            [DataMember(Name = "Version")]
            public string Version;

            [DataMember(Name = "PackageFullName")]
            public string PackageFullName;

            [DataMember(Name = "InstallLocation")]
            public string InstallLocation;
        }

        public static DesktopPackage Locate()
        {
            DesktopPackage result = new DesktopPackage();
            string script =
                "$ErrorActionPreference='Stop';" +
                "Get-AppxPackage -Name 'OpenAI.Codex' | Sort-Object Version -Descending | " +
                "Select-Object -First 1 Version,PackageFullName,InstallLocation | ConvertTo-Json -Compress";

            try
            {
                ProcessRunner.RunResult run = ProcessRunner.Run(
                    "powershell.exe",
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script.Replace("\"", "\\\"") + "\"",
                    30000);

                string stdout = run.StandardOutput;
                if (!run.Success || string.IsNullOrEmpty(stdout) || stdout.Trim().Length == 0)
                {
                    result.Found = false;
                    result.Error = !string.IsNullOrEmpty(run.ErrorMessage) ? run.ErrorMessage : "desktop-not-found";
                    return result;
                }
                PackageDto dto = JsonRead.FromText<PackageDto>(stdout.Trim());
                if (dto == null || string.IsNullOrEmpty(dto.InstallLocation))
                {
                    result.Found = false;
                    result.Error = "desktop-not-found";
                    return result;
                }
                result.Found = true;
                result.Version = dto.Version;
                result.PackageFullName = dto.PackageFullName;
                result.InstallLocation = dto.InstallLocation;
                return result;
            }
            catch (Exception ex)
            {
                result.Found = false;
                result.Error = "desktop-query-failed:" + ex.GetType().Name;
                return result;
            }
        }
    }
}
