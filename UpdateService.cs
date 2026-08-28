using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace PrinterDemon;

internal sealed class UpdateService
{
    private const string ManifestUrl =
        "https://github.com/ChickenAlexanderPillow/printer-demon/releases/latest/download/latest.json";
    private static readonly HttpClient Client = CreateClient();
    public Version? LatestVersion { get; private set; }

    public async Task<PrinterUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        var manifest = await Client.GetFromJsonAsync<UpdateManifest>(ManifestUrl, cancellationToken);
        if (manifest is null || !Version.TryParse(manifest.Version, out var availableVersion))
            return null;

        LatestVersion = availableVersion;
        var currentVersion = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
        return availableVersion > currentVersion
            ? new PrinterUpdate(availableVersion, manifest.Url, manifest.Notes)
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

        var scriptPath = Path.Combine(Path.GetTempPath(), $"PrinterDemon-update-{Guid.NewGuid():N}.cmd");
        var script = $"@echo off\r\ntimeout /t 2 /nobreak >nul\r\ncopy /Y {Quote(downloadPath)} {Quote(currentExe)} >nul\r\ndel {Quote(downloadPath)}\r\nstart \"\" {Quote(currentExe)}\r\ndel \"%~f0\"\r\n";
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

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PrinterDemon-Updater");
        return client;
    }

    private sealed record UpdateManifest(
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("notes")] string? Notes);
}

internal sealed record PrinterUpdate(Version Version, string Url, string? Notes);
