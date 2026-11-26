using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace UberDisk.Core.Updates;

/// <summary>The newest release on GitHub, as far as updating is concerned.</summary>
/// <param name="ExeUrl">Download link of the release's UberDisk.exe, or null if the release has none.</param>
/// <param name="ExeSha256">Lower-case hex SHA-256 that GitHub reports for that file, or null if it reports none.</param>
public sealed record ReleaseInfo(string Tag, Version Version, string PageUrl, string? ExeUrl, long ExeSize, string? ExeSha256);

/// <summary>
/// Self-update from GitHub releases with no installer: ask the releases API for the latest release, download its
/// UberDisk.exe next to the running one, check size and SHA-256, then swap the files. Windows lets a running exe be
/// renamed (not deleted), so the old one becomes UberDisk.exe.old and is deleted by the next start.
/// </summary>
public static class Updater
{
    public const string ExeAssetName = "UberDisk.exe";
    public const string OldSuffix = ".old";
    public const string DownloadSuffix = ".download";

    /// <summary>"v0.3.0", "0.3", "0.3.0-beta+abc" → 0.3.0 (build/revision filled with 0 so comparisons behave).</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) s = s[..cut];
        if (!Version.TryParse(s.Contains('.') ? s : s + ".0", out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
    }

    public static bool IsNewer(ReleaseInfo release, string currentVersion) =>
        ParseVersion(currentVersion) is { } cur && release.Version > cur;

    /// <summary>Reads one release object from the GitHub API (GET /repos/{owner}/{repo}/releases/latest).</summary>
    public static ReleaseInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        if (root.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
        var tag = Str(root, "tag_name");
        if (ParseVersion(tag) is not { } version) return null;

        string? url = null, sha = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                if (!string.Equals(Str(a, "name"), ExeAssetName, StringComparison.OrdinalIgnoreCase)) continue;
                if (Str(a, "state") is { } state && state != "uploaded") continue;
                url = Str(a, "browser_download_url");
                size = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var n) ? n : 0;
                if (Str(a, "digest") is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha = digest[7..].ToLowerInvariant();
                break;
            }
        }
        // Only https downloads from GitHub itself.
        if (url != null && !url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) url = null;
        return new ReleaseInfo(tag!, version, Str(root, "html_url") ?? "", url, size, sha);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static HttpClient NewClient(string currentVersion)
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UberDisk", currentVersion));
        return http;
    }

    /// <summary>The latest non-draft, non-prerelease release, or null if the repo has none.</summary>
    public static async Task<ReleaseInfo?> GetLatestAsync(HttpClient http, string repo, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var resp = await http.SendAsync(req, timeout.Token).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return Parse(await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
    }

    /// <summary>Downloads the release's exe to <paramref name="dest"/>, reporting 0..1, and checks size and hash.</summary>
    public static async Task DownloadAsync(HttpClient http, ReleaseInfo release, string dest, IProgress<double>? progress, CancellationToken ct)
    {
        if (release.ExeUrl == null) throw new InvalidOperationException($"Release {release.Tag} has no {ExeAssetName}.");
        try
        {
            using var resp = await http.GetAsync(release.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            long total = release.ExeSize > 0 ? release.ExeSize : resp.Content.Headers.ContentLength ?? 0;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buf = new byte[1 << 16];
                long done = 0;
                int n;
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                while (true)
                {
                    idle.CancelAfter(TimeSpan.FromSeconds(60)); // a stalled connection fails instead of hanging
                    n = await src.ReadAsync(buf, idle.Token).ConfigureAwait(false);
                    if (n == 0) break;
                    sha.AppendData(buf, 0, n);
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    if (total > 0) progress?.Report(Math.Min(1.0, (double)done / total));
                }
                if (release.ExeSize > 0 && done != release.ExeSize)
                    throw new InvalidDataException($"The download is {done:N0} bytes, expected {release.ExeSize:N0}.");
            }
            if (release.ExeSha256 is { } want && Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant() != want)
                throw new InvalidDataException("The download doesn't match the SHA-256 that GitHub lists for it.");
        }
        catch
        {
            TryDelete(dest);
            throw;
        }
    }

    /// <summary>
    /// Puts <paramref name="newExe"/> in place of <paramref name="exe"/> (which may be running): exe → exe.old,
    /// newExe → exe. Puts the old file back if the second step fails.
    /// </summary>
    public static void Swap(string exe, string newExe)
    {
        string old = exe + OldSuffix;
        TryDelete(old);
        File.Move(exe, old, overwrite: true);
        try { File.Move(newExe, exe); }
        catch
        {
            try { File.Move(old, exe); } catch { /* nothing more to try */ }
            throw;
        }
    }

    /// <summary>Removes what an earlier update left: exe.old (once the old process has let go) and a broken download.</summary>
    public static async Task CleanupAsync(string exe, CancellationToken ct = default)
    {
        TryDelete(exe + DownloadSuffix);
        string old = exe + OldSuffix;
        for (int i = 0; i < 20 && File.Exists(old); i++)
        {
            if (TryDelete(old)) break;
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch { return false; }
    }
}
