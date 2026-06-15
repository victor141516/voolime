using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Forms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;

namespace Voolime;

internal sealed class UpdateService
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/victor141516/voolime/releases/latest";
    private const string ExeAssetName = "Voolime.exe";
    private readonly HttpClient _httpClient = new();

    public UpdateService()
    {
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Voolime");
    }

    public void CheckOnStartup(WpfApplication application)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await CheckForUpdatesAsync(application);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Update check failed: {ex.Message}");
            }
        });
    }

    private async Task CheckForUpdatesAsync(WpfApplication application)
    {
        AppLogger.Info("Checking for updates.");
        using var response = await _httpClient.GetAsync(LatestReleaseApiUrl);
        if (!response.IsSuccessStatusCode)
        {
            AppLogger.Warn($"Update check returned HTTP {(int)response.StatusCode}.");
            return;
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions());
        if (release is null || string.IsNullOrWhiteSpace(release.TagName))
        {
            AppLogger.Warn("Update check returned an empty release payload.");
            return;
        }

        var latestVersion = ParseVersion(release.TagName);
        var currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        AppLogger.Info($"Current version {currentVersion}; latest release {release.TagName}.");
        if (latestVersion <= currentVersion)
        {
            AppLogger.Info("No update is available.");
            return;
        }

        var asset = release.Assets?.FirstOrDefault(static a =>
            string.Equals(a.Name, ExeAssetName, StringComparison.OrdinalIgnoreCase));
        if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
        {
            AppLogger.Warn("Latest release does not contain a Voolime.exe asset.");
            return;
        }

        await application.Dispatcher.InvokeAsync(() => PromptForUpdate(application, release, asset));
    }

    private void PromptForUpdate(WpfApplication application, GitHubRelease release, GitHubAsset asset)
    {
        var message = $"Voolime {release.TagName} is available.\n\nInstall it now?";
        var answer = Forms.MessageBox.Show(
            message,
            "Voolime update available",
            Forms.MessageBoxButtons.YesNo,
            Forms.MessageBoxIcon.Information);

        if (answer != Forms.DialogResult.Yes)
        {
            AppLogger.Info("User declined the available update.");
            return;
        }

        if (!CanSelfUpdate())
        {
            AppLogger.Warn("Self-update is not feasible from the current process path.");
            PromptForManualDownload(asset.BrowserDownloadUrl!, release.HtmlUrl);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var downloadedPath = await DownloadUpdateAsync(asset.BrowserDownloadUrl!);
                var scriptPath = CreateUpdaterScript(downloadedPath);
                LaunchUpdater(scriptPath);
                AppLogger.Info("Updater helper launched. Shutting down current app.");
                await application.Dispatcher.InvokeAsync(application.Shutdown);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Self-update failed.", ex);
                await application.Dispatcher.InvokeAsync(() => PromptForManualDownload(asset.BrowserDownloadUrl!, release.HtmlUrl));
            }
        });
    }

    private async Task<string> DownloadUpdateAsync(string downloadUrl)
    {
        AppLogger.Info("Downloading update asset.");
        var tempDirectory = Path.Combine(Path.GetTempPath(), "VoolimeUpdate");
        Directory.CreateDirectory(tempDirectory);
        var targetPath = Path.Combine(tempDirectory, $"Voolime-{Guid.NewGuid():N}.exe");

        using var response = await _httpClient.GetAsync(downloadUrl);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = File.Create(targetPath);
        await source.CopyToAsync(target);

        AppLogger.Info($"Update downloaded to {targetPath}.");
        return targetPath;
    }

    private static bool CanSelfUpdate()
    {
        var currentPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentPath) || !File.Exists(currentPath))
        {
            return false;
        }

        if (!string.Equals(Path.GetExtension(currentPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var directory = Path.GetDirectoryName(currentPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            var probePath = Path.Combine(directory, $".voolime-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probePath, string.Empty);
            File.Delete(probePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string CreateUpdaterScript(string downloadedPath)
    {
        var currentPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The current executable path could not be resolved.");
        var scriptPath = Path.Combine(Path.GetTempPath(), $"VoolimeUpdate-{Guid.NewGuid():N}.ps1");
        var updaterLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Voolime",
            "Logs",
            "updater.log");
        var currentPid = Environment.ProcessId;
        var escapedCurrent = EscapePowerShellString(currentPath);
        var escapedDownloaded = EscapePowerShellString(downloadedPath);
        var escapedScript = EscapePowerShellString(scriptPath);
        var escapedUpdaterLog = EscapePowerShellString(updaterLogPath);

        var script = $$"""
        $ErrorActionPreference = 'Stop'
        $current = '{{escapedCurrent}}'
        $downloaded = '{{escapedDownloaded}}'
        $log = '{{escapedUpdaterLog}}'
        $script = '{{escapedScript}}'
        function Write-UpdaterLog([string] $message) {
            try {
                $directory = [System.IO.Path]::GetDirectoryName($log)
                if (-not [string]::IsNullOrWhiteSpace($directory)) {
                    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
                }

                [System.IO.File]::AppendAllText(
                    $log,
                    "$(Get-Date -Format o) $message$([System.Environment]::NewLine)")
            }
            catch {
            }
        }

        try {
            Write-UpdaterLog "Updater started. Current: $current. Downloaded: $downloaded."
            try {
                Wait-Process -Id {{currentPid}} -Timeout 30 -ErrorAction Stop
                Write-UpdaterLog "Previous process exited."
            }
            catch {
                if (Get-Process -Id {{currentPid}} -ErrorAction SilentlyContinue) {
                    throw
                }
                Write-UpdaterLog "Previous process was already closed."
            }

            $deadline = (Get-Date).AddSeconds(30)
            do {
                try {
                    Copy-Item -LiteralPath $downloaded -Destination $current -Force
                    Write-UpdaterLog "Executable replaced."
                    break
                }
                catch {
                    if ((Get-Date) -ge $deadline) {
                        throw
                    }
                    Start-Sleep -Milliseconds 250
                }
            } while ($true)

            Start-Process -FilePath $current
            Write-UpdaterLog "Updated app launched."
        }
        catch {
            Write-UpdaterLog "Updater failed: $($_.Exception.ToString())"
            throw
        }
        finally {
            Remove-Item -LiteralPath $downloaded -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $script -Force -ErrorAction SilentlyContinue
        }
        """;

        File.WriteAllText(scriptPath, script);
        AppLogger.Info($"Updater script created at {scriptPath}.");
        return scriptPath;
    }

    private static void LaunchUpdater(string scriptPath)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });

        if (process is null)
        {
            throw new InvalidOperationException("The updater helper process could not be started.");
        }

        AppLogger.Info($"Updater helper process started with PID {process.Id}.");
    }

    private static void PromptForManualDownload(string assetUrl, string? releaseUrl)
    {
        var answer = Forms.MessageBox.Show(
            "Voolime cannot replace the running executable automatically from this location.\n\nOpen the download page instead?",
            "Manual update",
            Forms.MessageBoxButtons.YesNo,
            Forms.MessageBoxIcon.Information);

        if (answer != Forms.DialogResult.Yes)
        {
            return;
        }

        var url = !string.IsNullOrWhiteSpace(assetUrl) ? assetUrl : releaseUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        AppLogger.Info("Opened manual update URL.");
    }

    private static Version ParseVersion(string tagName)
    {
        var value = tagName.Trim().TrimStart('v', 'V');
        return Version.TryParse(value, out var version) ? version : new Version(0, 0, 0);
    }

    private static JsonSerializerOptions JsonOptions() =>
        new() { PropertyNameCaseInsensitive = true };

    private static string EscapePowerShellString(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")]
        string TagName,
        [property: JsonPropertyName("html_url")]
        string? HtmlUrl,
        [property: JsonPropertyName("assets")]
        IReadOnlyList<GitHubAsset>? Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")]
        string Name,
        [property: JsonPropertyName("browser_download_url")]
        string? BrowserDownloadUrl);
}
