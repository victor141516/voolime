using System.Diagnostics;
using System.Globalization;
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
    private const string ApplyUpdateArgument = "--voolime-apply-update";
    private const string SourceArgument = "--source";
    private const string TargetArgument = "--target";
    private const string PreviousPidArgument = "--previous-pid";
    private static readonly TimeSpan HttpRequestTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PreviousProcessWaitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReplaceRetryTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient _httpClient = new();

    public UpdateService()
    {
        _httpClient.Timeout = HttpRequestTimeout;
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
                LaunchUpdater(downloadedPath);
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

        using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
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

    private static void LaunchUpdater(string downloadedPath)
    {
        var currentPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The current executable path could not be resolved.");
        var startInfo = new ProcessStartInfo
        {
            FileName = downloadedPath,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(ApplyUpdateArgument);
        startInfo.ArgumentList.Add(SourceArgument);
        startInfo.ArgumentList.Add(downloadedPath);
        startInfo.ArgumentList.Add(TargetArgument);
        startInfo.ArgumentList.Add(currentPath);
        startInfo.ArgumentList.Add(PreviousPidArgument);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

        var process = Process.Start(startInfo);

        if (process is null)
        {
            throw new InvalidOperationException("The updater helper process could not be started.");
        }

        AppLogger.Info($"Updater helper process started from {downloadedPath} with PID {process.Id}.");
    }

    public static bool IsUpdaterCommand(IReadOnlyList<string> args) =>
        args.Any(static arg => string.Equals(arg, ApplyUpdateArgument, StringComparison.Ordinal));

    public static int RunUpdaterCommand(IReadOnlyList<string> args)
    {
        try
        {
            var command = ParseUpdaterCommand(args);
            ApplyUpdate(command);
            return 0;
        }
        catch (Exception ex)
        {
            WriteUpdaterLog($"Updater failed: {ex}");
            return 1;
        }
    }

    private static UpdaterCommand ParseUpdaterCommand(IReadOnlyList<string> args)
    {
        var sourcePath = ReadArgument(args, SourceArgument);
        var targetPath = ReadArgument(args, TargetArgument);
        var previousPidValue = ReadArgument(args, PreviousPidArgument);

        if (!int.TryParse(previousPidValue, NumberStyles.None, CultureInfo.InvariantCulture, out var previousPid))
        {
            throw new InvalidOperationException("The updater command contains an invalid previous process ID.");
        }

        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            throw new InvalidOperationException("The updater source executable could not be found.");
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new InvalidOperationException("The updater target executable was not provided.");
        }

        return new UpdaterCommand(sourcePath, targetPath, previousPid);
    }

    private static string ReadArgument(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }

        throw new InvalidOperationException($"Missing updater argument {name}.");
    }

    private static void ApplyUpdate(UpdaterCommand command)
    {
        WriteUpdaterLog(
            $"Updater started. Target: {command.TargetPath}. Source: {command.SourcePath}. Previous PID: {command.PreviousPid}.");
        WaitForPreviousProcess(command.PreviousPid);
        ReplaceExecutable(command.SourcePath, command.TargetPath);
        WriteUpdaterLog("Executable replaced.");

        var updatedStartInfo = new ProcessStartInfo
        {
            FileName = command.TargetPath,
            WorkingDirectory = Path.GetDirectoryName(command.TargetPath) ?? string.Empty,
            UseShellExecute = false
        };
        if (Process.Start(updatedStartInfo) is null)
        {
            throw new InvalidOperationException("The updated app could not be launched.");
        }

        WriteUpdaterLog("Updated app launched.");
    }

    private static void WaitForPreviousProcess(int previousPid)
    {
        try
        {
            using var process = Process.GetProcessById(previousPid);
            if (!process.WaitForExit((int)PreviousProcessWaitTimeout.TotalMilliseconds))
            {
                throw new TimeoutException("The previous Voolime process did not exit before the updater timeout.");
            }

            WriteUpdaterLog("Previous process exited.");
        }
        catch (ArgumentException)
        {
            WriteUpdaterLog("Previous process was already closed.");
        }
        catch (InvalidOperationException)
        {
            WriteUpdaterLog("Previous process was already closed.");
        }
    }

    private static void ReplaceExecutable(string sourcePath, string targetPath)
    {
        var deadline = DateTimeOffset.Now.Add(ReplaceRetryTimeout);
        Exception? lastError = null;

        while (DateTimeOffset.Now < deadline)
        {
            try
            {
                File.Copy(sourcePath, targetPath, overwrite: true);
                return;
            }
            catch (IOException ex)
            {
                lastError = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                lastError = ex;
            }

            Thread.Sleep(250);
        }

        throw new IOException("The updater could not replace the executable before the timeout.", lastError);
    }

    private static void WriteUpdaterLog(string message)
    {
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Voolime",
                "Logs",
                "updater.log");
            var directory = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(logPath, $"{DateTimeOffset.Now:o} {message}{Environment.NewLine}");
        }
        catch
        {
        }
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

    private sealed record UpdaterCommand(string SourcePath, string TargetPath, int PreviousPid);
}
