using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnifiedAudio.Services;

internal sealed class UpdateService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AppLog _log;

    public UpdateService(AppLog log)
    {
        _log = log;
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, AppIdentity.ReleasesApiUrl);
            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _log.Warn($"GitHub release check returned {(int)response.StatusCode}.");
                return UpdateCheckResult.Fail(((int)response.StatusCode >= 500) ? "github" : "network");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            {
                return UpdateCheckResult.Fail("missing-release");
            }

            var latest = ParseVersion(release.TagName) ?? ParseVersion(release.Name);
            var current = CurrentVersion();
            if (latest is null)
            {
                return UpdateCheckResult.Fail("invalid-version");
            }

            var asset = release.Assets?.FirstOrDefault(IsInstallerAsset);
            if (latest <= current)
            {
                return UpdateCheckResult.UpToDate(current);
            }

            if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
            {
                return UpdateCheckResult.Fail("missing-installer");
            }

            var hashAsset = release.Assets?.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, asset.Name + ".sha256", StringComparison.OrdinalIgnoreCase))
                ?? release.Assets?.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, "SHA256SUMS", StringComparison.OrdinalIgnoreCase));
            if (hashAsset is null || string.IsNullOrWhiteSpace(hashAsset.BrowserDownloadUrl))
                return UpdateCheckResult.Fail("missing-hash");

            return UpdateCheckResult.Available(
                current,
                latest,
                release.TagName,
                asset.BrowserDownloadUrl,
                asset.Name ?? "UnifiedAudioSetup.exe",
                hashAsset.BrowserDownloadUrl);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("Failed to check GitHub for updates.", ex);
            return UpdateCheckResult.Fail("network");
        }
    }

    public async Task<string> DownloadInstallerAsync(UpdateCheckResult update, IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        if (!update.IsUpdateAvailable
            || string.IsNullOrWhiteSpace(update.DownloadUrl)
            || string.IsNullOrWhiteSpace(update.HashDownloadUrl))
        {
            throw new InvalidOperationException("There is no installer to download.");
        }

        var folder = Path.Combine(Path.GetTempPath(), "UnifiedAudioUpdates");
        Directory.CreateDirectory(folder);
        var fileName = string.IsNullOrWhiteSpace(update.AssetName) ? "UnifiedAudioSetup.exe" : Path.GetFileName(update.AssetName);
        var destination = Path.Combine(folder, fileName);

        var hashManifest = await Http.GetStringAsync(update.HashDownloadUrl, cancellationToken).ConfigureAwait(false);
        var expectedHash = ParseExpectedSha256(hashManifest, fileName)
            ?? throw new InvalidDataException("The release SHA-256 manifest is invalid or does not name this installer.");

        using var request = new HttpRequestMessage(HttpMethod.Get, update.DownloadUrl);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long read = 0;
            int count;
            while ((count = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                read += count;
                if (total is > 0)
                    progress?.Report(Math.Clamp(read / (double)total.Value, 0, 1));
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var downloaded = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actualHash = await SHA256.HashDataAsync(downloaded, cancellationToken).ConfigureAwait(false);
        var expectedBytes = Convert.FromHexString(expectedHash);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedBytes))
        {
            downloaded.Close();
            File.Delete(destination);
            throw new InvalidDataException("The downloaded installer failed SHA-256 verification and was removed.");
        }

        progress?.Report(1);
        _log.Info($"Verified update installer SHA-256: {expectedHash}.");
        return destination;
    }

    public void LaunchInstaller(string installerPath)
    {
        if (!File.Exists(installerPath))
        {
            throw new FileNotFoundException("The downloaded installer is missing.", installerPath);
        }

        var start = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/S /LAUNCH",
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath)
        };
        if (Process.Start(start) is null)
        {
            throw new InvalidOperationException("The installer could not be started.");
        }
    }

    public static Version CurrentVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);
    }

    private static bool IsInstallerAsset(GitHubReleaseAsset asset)
    {
        if (string.IsNullOrWhiteSpace(asset.Name) || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
        {
            return false;
        }

        return asset.Name.StartsWith(AppIdentity.InstallerAssetName, StringComparison.OrdinalIgnoreCase)
            && asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static Version? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            text = text[1..];
        }

        var end = 0;
        while (end < text.Length && (char.IsDigit(text[end]) || text[end] is '.'))
        {
            end++;
        }

        if (end == 0)
        {
            return null;
        }

        return Version.TryParse(text[..end], out var version) ? Normalize(version) : null;
    }

    private static Version Normalize(Version version)
    {
        return new Version(
            Math.Max(version.Major, 0),
            Math.Max(version.Minor, 0),
            version.Build < 0 ? 0 : version.Build,
            version.Revision < 0 ? 0 : version.Revision);
    }

    internal static string? ParseExpectedSha256(string manifest, string assetName)
    {
        foreach (var rawLine in manifest.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 64 && line.All(Uri.IsHexDigit)) return line.ToUpperInvariant();
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0].Length != 64 || !parts[0].All(Uri.IsHexDigit)) continue;
            var namedAsset = parts[^1].TrimStart('*');
            if (string.Equals(Path.GetFileName(namedAsset), assetName, StringComparison.OrdinalIgnoreCase))
                return parts[0].ToUpperInvariant();
        }
        return null;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UnifiedAudio", CurrentVersion().ToString(3)));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }
        public string? Name { get; set; }
        public List<GitHubReleaseAsset>? Assets { get; set; }
    }

    private sealed class GitHubReleaseAsset
    {
        public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}

internal sealed record UpdateCheckResult(
    bool Succeeded,
    bool IsUpdateAvailable,
    Version CurrentVersion,
    Version? LatestVersion,
    string? TagName,
    string? DownloadUrl,
    string? AssetName,
    string? HashDownloadUrl,
    string? ErrorCode)
{
    public static UpdateCheckResult Available(Version current, Version latest, string? tag, string url, string asset, string hashUrl) =>
        new(true, true, current, latest, tag, url, asset, hashUrl, null);

    public static UpdateCheckResult UpToDate(Version current) =>
        new(true, false, current, current, null, null, null, null, null);

    public static UpdateCheckResult Fail(string errorCode) =>
        new(false, false, UpdateService.CurrentVersion(), null, null, null, null, null, errorCode);
}
