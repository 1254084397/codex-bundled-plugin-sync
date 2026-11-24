using System;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace CodexPluginSync.Core
{
    /// <summary>Optional, read-only GitHub release check used by the GUI.</summary>
    public sealed class UpdateInfo
    {
        public bool Succeeded;
        public bool IsNewer;
        public string CurrentVersion;
        public string LatestVersion;
        public string ReleaseUrl;
        public string Message;
    }

    public static class UpdateChecker
    {
        public const string RepositoryUrl = "https://github.com/1254084397/codex-bundled-plugin-sync";
        private const string LatestReleaseApi = "https://api.github.com/repos/1254084397/codex-bundled-plugin-sync/releases/latest";

        public static UpdateInfo Check(string currentVersion)
        {
            UpdateInfo result = new UpdateInfo { CurrentVersion = currentVersion };
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(LatestReleaseApi);
                request.Method = "GET";
                request.Accept = "application/vnd.github+json";
                request.UserAgent = "CodexPluginSync/" + currentVersion;
                request.Timeout = 5000;
                request.ReadWriteTimeout = 5000;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                {
                    ReleaseDto release = ReadJson<ReleaseDto>(stream);
                    string latest = NormalizeVersion(release == null ? null : release.TagName);
                    result.LatestVersion = latest;
                    result.ReleaseUrl = release == null ? RepositoryUrl + "/releases" : release.HtmlUrl;
                    result.Succeeded = !string.IsNullOrEmpty(latest);
                    result.IsNewer = result.Succeeded && IsGreater(latest, currentVersion);
                    result.Message = result.Succeeded ? null : "GitHub 没有可用的稳定发布版本";
                    return result;
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse errorResponse = ex.Response as HttpWebResponse;
                if (errorResponse != null && errorResponse.StatusCode == HttpStatusCode.NotFound)
                {
                    result.Message = "GitHub 暂无已发布的 Release 版本";
                    result.ReleaseUrl = RepositoryUrl + "/releases";
                    return result;
                }
                result.Message = "暂时无法检查 GitHub 版本：" + ex.Message;
                result.ReleaseUrl = RepositoryUrl + "/releases";
                return result;
            }
            catch (Exception ex)
            {
                result.Message = "版本检查失败：" + ex.Message;
                result.ReleaseUrl = RepositoryUrl + "/releases";
                return result;
            }
        }

        private static T ReadJson<T>(Stream stream)
        {
            return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }

        private static string NormalizeVersion(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            string value = tag.Trim();
            if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase)) value = value.Substring(1);
            Version version;
            return Version.TryParse(value, out version) ? version.ToString(3) : null;
        }

        private static bool IsGreater(string latest, string current)
        {
            Version latestVersion;
            Version currentVersion;
            if (!Version.TryParse(latest, out latestVersion) || !Version.TryParse(current, out currentVersion)) return false;
            return latestVersion > currentVersion;
        }

        [DataContract]
        private sealed class ReleaseDto
        {
            [DataMember(Name = "tag_name")] public string TagName;
            [DataMember(Name = "html_url")] public string HtmlUrl;
        }
    }
}
