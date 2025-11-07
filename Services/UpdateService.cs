using GreenLuma_Manager.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GreenLuma_Manager.Services
{
    public partial class UpdateService
    {
        private static string CurrentVersion => MainWindow.Version;
        private const string GitHubApiUrl = "https://api.github.com/repos/3vil3vo/GreenLuma-Manager/releases/latest";
        private static readonly string[] RcSeparator = ["-rc"];
        private static readonly string[] DashSeparator = ["-"];

        private static readonly HttpClient _client;

        [GeneratedRegex("\"tag_name\"\\s*:\\s*\"([^\"]+)\"")]
        private static partial Regex TagNameRegex();

        [GeneratedRegex("\"browser_download_url\"\\s*:\\s*\"([^\"]+)\"")]
        private static partial Regex BrowserDownloadUrlRegex();

        [GeneratedRegex("\"body\"\\s*:\\s*\"([^\"]*)\"")]
        private static partial Regex BodyRegex();

        static UpdateService()
        {
            _client = new HttpClient();
            _client.DefaultRequestHeaders.Add("User-Agent", "GreenLuma-Manager");
        }

        public static async Task<UpdateInfo?> CheckForUpdatesAsync()
        {
            try
            {
                string response = await _client.GetStringAsync(GitHubApiUrl);
                return ParseUpdateInfo(response);
            }
            catch
            {
                return null;
            }
        }

        private static UpdateInfo? ParseUpdateInfo(string jsonResponse)
        {
            var tagMatch = TagNameRegex().Match(jsonResponse);
            var downloadMatch = BrowserDownloadUrlRegex().Match(jsonResponse);
            var bodyMatch = BodyRegex().Match(jsonResponse);

            if (!tagMatch.Success)
                return null;

            string latestTag = tagMatch.Groups[1].Value;
            string downloadUrl = downloadMatch.Success ? downloadMatch.Groups[1].Value : string.Empty;
            string releaseNotes = bodyMatch.Success
                ? bodyMatch.Groups[1].Value.Replace("\\n", "\n").Replace("\\r", "\r")
                : string.Empty;

            string currentNormalized = NormalizeVersion(CurrentVersion);
            string latestNormalized = NormalizeVersion(latestTag);

            return new UpdateInfo
            {
                CurrentVersion = CurrentVersion,
                LatestVersion = FormatDisplayVersion(latestTag),
                LatestVersionTag = latestTag,
                UpdateAvailable =
                    string.Compare(latestNormalized, currentNormalized, StringComparison.OrdinalIgnoreCase) > 0,
                DownloadUrl = downloadUrl,
                ReleaseNotes = releaseNotes
            };
        }

        private static string NormalizeVersion(string version)
        {
            string cleaned = version.ToLower().Trim().Replace("v", "");

            if (string.IsNullOrEmpty(cleaned))
                return "00000.00000.00000.0000.0000";

            if (cleaned.StartsWith("rc") && !cleaned.Contains(".0.0"))
            {
                string rcPart = cleaned[2..];
                string[] rcParts = rcPart.Split('.');

                int rcMajor = ParseVersionPart(rcParts, 0);
                int rcMinor = rcParts.Length > 1 ? ParseVersionPart(rcParts, 1) : 0;

                return $"00001.00000.00000.{rcMajor:D4}.{rcMinor:D4}";
            }

            string[] parts = cleaned.Split('-', 2);
            string baseVersion = parts[0];

            string[] versionParts = baseVersion.Split('.');
            int major = ParseVersionPart(versionParts, 0);
            int minor = ParseVersionPart(versionParts, 1);
            int patch = ParseVersionPart(versionParts, 2);

            if (parts.Length == 1)
            {
                return $"{major:D5}.{minor:D5}.{patch:D5}.9999.9999";
            }

            string prerelease = parts[1];

            if (prerelease.StartsWith("rc"))
            {
                string rcPart = prerelease[2..];
                string[] rcParts = rcPart.Split('.');

                int rcMajor = ParseVersionPart(rcParts, 0);
                int rcMinor = rcParts.Length > 1 ? ParseVersionPart(rcParts, 1) : 0;

                return $"{major:D5}.{minor:D5}.{patch:D5}.{rcMajor:D4}.{rcMinor:D4}";
            }

            return $"{major:D5}.{minor:D5}.{patch:D5}.0000.0000";
        }

        private static int ParseVersionPart(string[] parts, int index)
        {
            if (index >= parts.Length)
                return 0;

            if (int.TryParse(parts[index], out int result))
                return result;

            return 0;
        }

        private static string FormatDisplayVersion(string version)
        {
            string cleaned = version.ToLower().Replace("v", "");

            if (cleaned.Contains("-rc"))
            {
                string[] parts = cleaned.Split(RcSeparator, StringSplitOptions.None);
                if (parts.Length > 1)
                {
                    return "RC" + parts[1].ToUpper();
                }
            }

            if (cleaned.StartsWith("rc"))
            {
                return cleaned.ToUpper();
            }

            return cleaned;
        }

        public static async Task<bool> PerformAutoUpdateAsync(string downloadUrl)
        {
            try
            {
                string currentExePath = Environment.ProcessPath!;
                string tempExePath = await DownloadUpdate(downloadUrl);

                CreateAndExecuteUpdateScript(tempExePath, currentExePath);

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static async Task<string> DownloadUpdate(string downloadUrl)
        {
            string tempDir = Path.GetTempPath();
            string tempExePath = Path.Combine(tempDir, "GreenLumaManager_Update.exe");

            using var response = await _client.GetAsync(downloadUrl);
            response.EnsureSuccessStatusCode();

            using var fileStream = new FileStream(tempExePath, FileMode.Create, FileAccess.Write, FileShare.None);
            await response.Content.CopyToAsync(fileStream);

            return tempExePath;
        }

        private static void CreateAndExecuteUpdateScript(string tempExePath, string currentExePath)
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), "update.bat");
            string scriptContent = GenerateUpdateScript(tempExePath, currentExePath);

            File.WriteAllText(scriptPath, scriptContent);

            Process.Start(new ProcessStartInfo
            {
                FileName = scriptPath,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }

        private static string GenerateUpdateScript(string tempExePath, string currentExePath)
        {
            string currentExeName = Path.GetFileName(currentExePath);

            return $@"@echo off
echo Waiting for application to close...
timeout /t 2 /nobreak >nul

taskkill /F /IM ""{currentExeName}"" >nul 2>&1

echo Waiting for process to fully terminate...
:wait_process
tasklist /FI ""IMAGENAME eq {currentExeName}"" 2>NUL | find /I /N ""{currentExeName}"">NUL
if ""%ERRORLEVEL%""==""0"" (
    timeout /t 1 /nobreak >nul
    goto wait_process
)

echo Process terminated.
echo Updating...

del ""{currentExePath}"" >nul 2>&1
:wait_delete
if exist ""{currentExePath}"" (
    timeout /t 1 /nobreak >nul
    del ""{currentExePath}"" >nul 2>&1
    goto wait_delete
)

move /y ""{tempExePath}"" ""{currentExePath}""
if errorlevel 1 (
    echo Update failed!
    pause
    exit /b 1
)

echo.
echo ========================================
echo Update Complete!
echo ========================================
echo.
echo Restarting GreenLuma Manager...
timeout /t 2 /nobreak >nul

start """" ""{currentExePath}""
exit
";
        }
    }
}
