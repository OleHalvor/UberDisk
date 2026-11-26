using System.Net;
using System.Security.Cryptography;
using UberDisk.Core.Updates;

namespace UberDisk.Core.Tests;

public class UpdaterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uberdisk-update-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private const string Latest = """
        {
          "tag_name": "v0.3.0", "html_url": "https://github.com/OleHalvor/UberDisk/releases/tag/v0.3.0",
          "draft": false, "prerelease": false,
          "assets": [
            { "name": "UberDisk-win-x64.zip", "size": 10, "state": "uploaded", "digest": "sha256:00",
              "browser_download_url": "https://github.com/OleHalvor/UberDisk/releases/download/v0.3.0/UberDisk-win-x64.zip" },
            { "name": "UberDisk.exe", "size": 71030919, "state": "uploaded",
              "digest": "sha256:425484A994AC329E3FD04399FCB28590B4D0CD97A14FB1BA20F9377A3EDD4660",
              "browser_download_url": "https://github.com/OleHalvor/UberDisk/releases/download/v0.3.0/UberDisk.exe" }
          ]
        }
        """;

    [Theory]
    [InlineData("v0.3.0", "0.3.0.0")]
    [InlineData("0.3", "0.3.0.0")]
    [InlineData("V1.2.3-beta.1", "1.2.3.0")]
    [InlineData("0.2.0+abc123", "0.2.0.0")]
    [InlineData("2", "2.0.0.0")]
    public void ParsesVersions(string text, string expected) =>
        Assert.Equal(Version.Parse(expected), Updater.ParseVersion(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    public void RejectsNonVersions(string? text) => Assert.Null(Updater.ParseVersion(text));

    [Fact]
    public void ReadsTheExeAssetOfTheLatestRelease()
    {
        var r = Updater.Parse(Latest)!;
        Assert.Equal("v0.3.0", r.Tag);
        Assert.Equal(new Version(0, 3, 0, 0), r.Version);
        Assert.Equal("https://github.com/OleHalvor/UberDisk/releases/download/v0.3.0/UberDisk.exe", r.ExeUrl);
        Assert.Equal(71030919, r.ExeSize);
        Assert.Equal("425484a994ac329e3fd04399fcb28590b4d0cd97a14fb1ba20f9377a3edd4660", r.ExeSha256);
        Assert.Equal("https://github.com/OleHalvor/UberDisk/releases/tag/v0.3.0", r.PageUrl);
    }

    [Fact]
    public void ComparesAgainstTheRunningVersion()
    {
        var r = Updater.Parse(Latest)!;
        Assert.True(Updater.IsNewer(r, "0.2.0"));
        Assert.True(Updater.IsNewer(r, "0.2.9"));
        Assert.False(Updater.IsNewer(r, "0.3.0"));
        Assert.False(Updater.IsNewer(r, "0.3"));
        Assert.False(Updater.IsNewer(r, "0.10.0"));
        Assert.False(Updater.IsNewer(r, "garbage"));
    }

    [Fact]
    public void IgnoresDraftsPrereleasesAndForeignLinks()
    {
        Assert.Null(Updater.Parse(Latest.Replace("\"prerelease\": false", "\"prerelease\": true")));
        Assert.Null(Updater.Parse(Latest.Replace("\"draft\": false", "\"draft\": true")));
        var foreign = Updater.Parse(Latest.Replace("https://github.com/OleHalvor/UberDisk/releases/download/v0.3.0/UberDisk.exe",
                                                   "http://example.com/UberDisk.exe"))!;
        Assert.Null(foreign.ExeUrl);
        var noExe = Updater.Parse(Latest.Replace("\"UberDisk.exe\"", "\"Other.exe\""))!;
        Assert.Null(noExe.ExeUrl);
    }

    [Fact]
    public void SwapPutsTheNewExeInPlaceAndKeepsTheOldAside()
    {
        string exe = Path.Combine(_dir, "UberDisk.exe"), dl = exe + Updater.DownloadSuffix;
        File.WriteAllText(exe, "old");
        File.WriteAllText(dl, "new");
        Updater.Swap(exe, dl);
        Assert.Equal("new", File.ReadAllText(exe));
        Assert.Equal("old", File.ReadAllText(exe + Updater.OldSuffix));
        Assert.False(File.Exists(dl));
    }

    [Fact]
    public void SwapRestoresTheOldExeWhenTheNewOneIsMissing()
    {
        string exe = Path.Combine(_dir, "UberDisk.exe");
        File.WriteAllText(exe, "old");
        Assert.ThrowsAny<IOException>(() => Updater.Swap(exe, exe + Updater.DownloadSuffix));
        Assert.Equal("old", File.ReadAllText(exe));
    }

    [Fact]
    public async Task CleanupRemovesLeftovers()
    {
        string exe = Path.Combine(_dir, "UberDisk.exe");
        File.WriteAllText(exe, "x");
        File.WriteAllText(exe + Updater.OldSuffix, "x");
        File.WriteAllText(exe + Updater.DownloadSuffix, "x");
        await Updater.CleanupAsync(exe);
        Assert.True(File.Exists(exe));
        Assert.False(File.Exists(exe + Updater.OldSuffix));
        Assert.False(File.Exists(exe + Updater.DownloadSuffix));
    }

    private static ReleaseInfo Release(byte[] body, string? sha = null, long? size = null) =>
        new("v9.0.0", new Version(9, 0, 0, 0), "https://github.com/x", "https://github.com/x/UberDisk.exe", size ?? body.Length,
            sha ?? Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant());

    [Fact]
    public async Task DownloadChecksHashAndReportsProgress()
    {
        var body = new byte[300_000];
        new Random(1).NextBytes(body);
        using var http = new HttpClient(new Fixed(body));
        string dest = Path.Combine(_dir, "dl");
        double last = 0;
        await Updater.DownloadAsync(http, Release(body), dest, new SyncProgress(p => last = p), default);
        Assert.Equal(body, File.ReadAllBytes(dest));
        Assert.Equal(1.0, last);
    }

    [Fact]
    public async Task DownloadWithTheWrongHashOrSizeIsDeleted()
    {
        var body = new byte[5000];
        using var http = new HttpClient(new Fixed(body));
        string dest = Path.Combine(_dir, "dl");
        await Assert.ThrowsAsync<InvalidDataException>(() => Updater.DownloadAsync(http, Release(body, sha: new string('0', 64)), dest, null, default));
        Assert.False(File.Exists(dest));
        await Assert.ThrowsAsync<InvalidDataException>(() => Updater.DownloadAsync(http, Release(body, size: 6000), dest, null, default));
        Assert.False(File.Exists(dest));
    }

    private sealed class Fixed(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
