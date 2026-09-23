using System.Net;
using System.Security.Cryptography;
using System.Text;
using RootedAndroidGameVM.Core.Downloads;
using RootedAndroidGameVM.Core.Setup;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class DownloadSourceAndImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rgvm-download-tests", Guid.NewGuid().ToString("N"));

    private string CreateDirectory(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Mirror_rule_rewrites_a_matching_prefix()
    {
        var configuration = DownloadSourceConfiguration.Create(
            [DownloadSourceRule.Create("https://dl.google.com/android/repository/", "https://mirror.example.com/android/")]);

        var rewritten = configuration.Rewrite(new Uri("https://dl.google.com/android/repository/x.zip"));

        Assert.Equal("https://mirror.example.com/android/x.zip", rewritten.ToString());
    }

    [Fact]
    public void Mirror_rule_leaves_unmatched_sources_unchanged()
    {
        var configuration = DownloadSourceConfiguration.Create(
            [DownloadSourceRule.Create("https://dl.google.com/android/repository/", "https://mirror.example.com/")]);

        var original = new Uri("https://github.com/owner/repo/releases/download/v1/a.apk");

        Assert.Equal(original, configuration.Rewrite(original));
    }

    [Theory]
    [InlineData("not-a-url", "https://mirror.example.com/")]
    [InlineData("ftp://mirror.example.com/", "https://mirror.example.com/")]
    [InlineData("https://dl.google.com/", "relative")]
    public void Invalid_mirror_rules_are_rejected(string from, string to)
    {
        Assert.Throws<ArgumentException>(() => DownloadSourceRule.Create(from, to));
    }

    [Fact]
    public async Task Mirror_configuration_round_trips_and_clears()
    {
        var store = new DownloadSourceConfigurationStore(_root);
        await store.SaveAsync(DownloadSourceConfiguration.Create(
            [DownloadSourceRule.Create("https://github.com/", "https://ghproxy.example.com/")]));

        var reloaded = new DownloadSourceConfigurationStore(_root).Read();
        Assert.Single(reloaded.Rules);
        Assert.Equal("https://ghproxy.example.com", reloaded.Rules[0].To);

        store.Clear();
        Assert.False(store.Read().HasRules);
    }

    [Fact]
    public async Task Downloader_uses_the_rewritten_source()
    {
        var payload = Encoding.UTF8.GetBytes("mirrored");
        var hash = Convert.ToHexStringLower(SHA256.HashData(payload));
        var handler = new RecordingHandler(payload);
        using var client = new HttpClient(handler);
        var downloader = new VerifiedDownloader(client, _ => TimeSpan.Zero,
            source => new Uri(source.AbsoluteUri.Replace("https://origin.invalid/", "https://mirror.invalid/")));
        var destination = Path.Combine(CreateDirectory("cache"), "payload.bin");

        await downloader.DownloadAsync(new Uri("https://origin.invalid/payload.bin"), destination, hash);

        Assert.Equal("https://mirror.invalid/payload.bin", handler.LastRequestUri);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task Import_accepts_a_verified_file_into_the_cache()
    {
        var payload = Encoding.UTF8.GetBytes("good-component");
        var item = ItemFor("good", "good.zip", payload);
        var source = CreateDirectory("source");
        await File.WriteAllBytesAsync(Path.Combine(source, "good.zip"), payload);
        var cache = Path.Combine(_root, "cache");

        var results = await new DependencyImportService([item]).ImportAsync(source, cache);

        var result = Assert.Single(results);
        Assert.Equal("imported", result.Outcome);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(cache, "good.zip")));
    }

    [Fact]
    public async Task Import_reports_hash_mismatch_for_a_same_named_file()
    {
        var item = ItemFor("good", "good.zip", Encoding.UTF8.GetBytes("expected"));
        var source = CreateDirectory("source");
        await File.WriteAllBytesAsync(Path.Combine(source, "good.zip"), Encoding.UTF8.GetBytes("tampered"));

        var results = await new DependencyImportService([item]).ImportAsync(source, Path.Combine(_root, "cache"));

        Assert.Equal("hash-mismatch", Assert.Single(results).Outcome);
    }

    [Fact]
    public async Task Import_reports_missing_when_no_file_matches()
    {
        var item = ItemFor("good", "good.zip", Encoding.UTF8.GetBytes("expected"));
        var source = CreateDirectory("source");

        var results = await new DependencyImportService([item]).ImportAsync(source, Path.Combine(_root, "cache"));

        Assert.Equal("missing", Assert.Single(results).Outcome);
    }

    [Fact]
    public async Task Import_matches_by_hash_when_the_name_differs()
    {
        var payload = Encoding.UTF8.GetBytes("renamed-component");
        var item = ItemFor("good", "good.zip", payload);
        var source = CreateDirectory("source");
        await File.WriteAllBytesAsync(Path.Combine(source, "downloaded-by-browser.bin"), payload);
        var cache = Path.Combine(_root, "cache");

        var results = await new DependencyImportService([item]).ImportAsync(source, cache);

        Assert.Equal("imported", Assert.Single(results).Outcome);
        Assert.True(File.Exists(Path.Combine(cache, "good.zip")));
    }

    [Fact]
    public async Task Import_recognizes_an_already_cached_component()
    {
        var payload = Encoding.UTF8.GetBytes("cached");
        var item = ItemFor("good", "good.zip", payload);
        var cache = CreateDirectory("cache");
        await File.WriteAllBytesAsync(Path.Combine(cache, "good.zip"), payload);
        var source = CreateDirectory("source");

        var results = await new DependencyImportService([item]).ImportAsync(source, cache);

        Assert.Equal("already-cached", Assert.Single(results).Outcome);
    }

    [Fact]
    public async Task Catalog_reports_verified_state_of_the_cache()
    {
        var payload = Encoding.UTF8.GetBytes("cataloged");
        var item = ItemFor("good", "good.zip", payload);
        var cache = CreateDirectory("cache");

        var missing = await DependencyDownloadCatalog.InspectAsync(cache, items: [item]);
        Assert.False(Assert.Single(missing).Verified);

        await File.WriteAllBytesAsync(Path.Combine(cache, "good.zip"), payload);
        var present = await DependencyDownloadCatalog.InspectAsync(cache, items: [item]);
        Assert.True(Assert.Single(present).Verified);
    }

    [Fact]
    public async Task Catalog_recognizes_a_verified_file_with_a_different_name()
    {
        var payload = Encoding.UTF8.GetBytes("renamed-in-cache");
        var item = ItemFor("good", "good.zip", payload);
        var cache = CreateDirectory("cache");
        await File.WriteAllBytesAsync(Path.Combine(cache, "downloaded-by-browser.bin"), payload);

        var states = await DependencyDownloadCatalog.InspectAsync(cache, items: [item]);

        Assert.True(Assert.Single(states).Verified);
    }

    [Fact]
    public async Task Adopt_renames_a_content_verified_file_to_the_expected_name()
    {
        var payload = Encoding.UTF8.GetBytes("adopt-me");
        var item = ItemFor("good", "good.zip", payload);
        var cache = CreateDirectory("cache");
        await File.WriteAllBytesAsync(Path.Combine(cache, "weird-name.bin"), payload);

        var adopted = await DependencyDownloadCatalog.AdoptAllAsync(cache, [item]);

        Assert.Contains("good.zip", adopted);
        Assert.True(File.Exists(Path.Combine(cache, "good.zip")));
        Assert.False(File.Exists(Path.Combine(cache, "weird-name.bin")));
        Assert.True(Assert.Single(await DependencyDownloadCatalog.InspectAsync(cache, items: [item])).Verified);
    }

    [Fact]
    public void China_preset_rewrites_google_and_github_sources()
    {
        var configuration = DownloadSourceConfiguration.Create(DownloadMirrorPresets.China.Rules);

        Assert.Equal(
            "https://mirrors.cloud.tencent.com/AndroidSDK/platform-tools_r37.0.1-win.zip",
            configuration.Rewrite(new Uri("https://dl.google.com/android/repository/platform-tools_r37.0.1-win.zip")).ToString());
        Assert.Equal(
            "https://ghfast.top/https://github.com/topjohnwu/Magisk/releases/download/v30.6/Magisk-v30.6.apk",
            configuration.Rewrite(new Uri("https://github.com/topjohnwu/Magisk/releases/download/v30.6/Magisk-v30.6.apk")).ToString());
        Assert.Equal(
            "https://mirrors.cloud.tencent.com/AndroidSDK/sys-img/google_apis_playstore/x86_64-35_r09.zip",
            configuration.Rewrite(new Uri("https://dl.google.com/android/repository/sys-img/google_apis_playstore/x86_64-35_r09.zip")).ToString());
    }

    [Fact]
    public void Mirror_presets_are_findable_by_id()
    {
        Assert.NotNull(DownloadMirrorPresets.Find("china"));
        Assert.NotNull(DownloadMirrorPresets.Find("CHINA"));
        Assert.Null(DownloadMirrorPresets.Find("does-not-exist"));
    }

    [Fact]
    public async Task Downloader_aborts_a_stalled_response_instead_of_hanging()
    {
        using var client = new HttpClient(new StallHandler());
        var downloader = new VerifiedDownloader(client, _ => TimeSpan.Zero, null,
            stallTimeout: TimeSpan.FromMilliseconds(150), maxAttempts: 1);
        var destination = Path.Combine(CreateDirectory("cache"), "payload.bin");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            downloader.DownloadAsync(new Uri("https://example.invalid/payload.bin"), destination, new string('0', 64)));
    }

    [Fact]
    public async Task Downloader_retries_after_a_stall_and_succeeds()
    {
        var payload = Encoding.UTF8.GetBytes("after-stall");
        var hash = Convert.ToHexStringLower(SHA256.HashData(payload));
        var handler = new StallThenSuccessHandler(payload);
        using var client = new HttpClient(handler);
        var downloader = new VerifiedDownloader(client, _ => TimeSpan.Zero, null,
            stallTimeout: TimeSpan.FromMilliseconds(150));
        var destination = Path.Combine(CreateDirectory("cache"), "payload.bin");

        await downloader.DownloadAsync(new Uri("https://example.invalid/payload.bin"), destination, hash);

        Assert.True(handler.Attempts >= 2);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
    }

    private static DependencyDownloadItem ItemFor(string id, string fileName, byte[] payload) =>
        new(id, id, "1", $"https://example.invalid/{fileName}", fileName,
            Convert.ToHexStringLower(SHA256.HashData(payload)), payload.Length);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class RecordingHandler(byte[] payload) : HttpMessageHandler
    {
        public string? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri?.AbsoluteUri;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
            response.Content.Headers.ContentLength = payload.Length;
            return Task.FromResult(response);
        }
    }

    private sealed class StallHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) });
    }

    private sealed class StallThenSuccessHandler(byte[] payload) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            HttpContent content = Attempts == 1 ? new StreamContent(new StallingStream()) : new ByteArrayContent(payload);
            if (content is ByteArrayContent) content.Headers.ContentLength = payload.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
