using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace PrinterDemon;

internal sealed class UpdateService
{
    private const string ManifestUrl =
        "https://github.com/ChickenAlexanderPillow/printer-demon/releases/latest/download/latest.json";
    private static readonly HttpClient Client = CreateClient();
    public Version? LatestVersion { get; private set; }

    public async Task<PrinterUpdate?> CheckAsync(bool allowInstall = true, CancellationToken cancellationToken = default)
    {
        var manifest = await Client.GetFromJsonAsync<UpdateManifest>(ManifestUrl, cancellationToken);
        if (manifest is null || !Version.TryParse(manifest.Version, out var availableVersion))
            return null;

        LatestVersion = availableVersion;
        var currentVersion = GetCurrentFileVersion();
        return allowInstall && availableVersion > currentVersion
            ? new PrinterUpdate(availableVersion, manifest.Url, manifest.Sha256, manifest.Notes)
            : null;
    }

    public async Task InstallAndRestartAsync(PrinterUpdate update, CancellationToken cancellationToken = default)
    {
        var currentExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not locate Printer Demon.");
        var downloadPath = Path.Combine(Path.GetTempPath(), $"PrinterDemon-update-{Guid.NewGuid():N}.exe");
        await using (var source = await Client.GetStreamAsync(update.Url, cancellationToken))
        await using (var destination = File.Create(downloadPath))
        {
            await source.CopyToAsync(destination, cancellationToken);
        }

        var downloadedInfo = FileVersionInfo.GetVersionInfo(downloadPath);
        var downloadedVersion = downloadedInfo.FileVersion;
        string downloadedHash;
        await using (var downloadedFile = File.OpenRead(downloadPath))
        {
            downloadedHash = Convert.ToHexString(await SHA256.HashDataAsync(downloadedFile, cancellationToken));
        }
        if (!Version.TryParse(downloadedVersion, out var parsedDownloadedVersion)
            || parsedDownloadedVersion < update.Version
            || !string.Equals(downloadedInfo.OriginalFilename, "PrinterDemon.dll", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(downloadedHash, update.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(downloadPath);
            throw new InvalidOperationException("The downloaded update is not a valid Printer Demon executable.");
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), $"PrinterDemon-update-{Guid.NewGuid():N}.cmd");
        var script = $"@echo off\r\ntimeout /t 2 /nobreak >nul\r\ncopy /Y {Quote(downloadPath)} {Quote(currentExe)} >nul\r\ndel {Quote(downloadPath)}\r\nstart \"\" {Quote(currentExe)} --skip-update-check\r\ndel \"%~f0\"\r\n";
        await File.WriteAllTextAsync(scriptPath, script, cancellationToken);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {Quote(scriptPath)}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static Version GetCurrentFileVersion()
    {
        var currentExe = Environment.ProcessPath;
        var fileVersion = currentExe is null ? null : FileVersionInfo.GetVersionInfo(currentExe).FileVersion;
        return Version.TryParse(fileVersion, out var version)
            ? version
            : Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PrinterDemon-Updater");
        return client;
    }

    private sealed record UpdateManifest(
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("notes")] string? Notes);
}

internal sealed record PrinterUpdate(Version Version, string Url, string Sha256, string? Notes);
