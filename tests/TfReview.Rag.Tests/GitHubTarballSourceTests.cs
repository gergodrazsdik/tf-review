using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;

namespace TfReview.Rag.Tests;

public class GitHubTarballSourceTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static readonly TfReviewConfig Config = new("owner/standards", Sha, ["conventions"], false);

    [Test]
    public async Task Asks_for_the_exact_commit()
    {
        var handler = new FakeHandler(Tarball(("top/adr/ADR-001.md", "doc")));
        using var target = new TargetDirectory();

        await Source(handler).FetchAsync(Config, target.Path);

        await Assert.That(handler.Requested!.AbsolutePath).IsEqualTo($"/owner/standards/tar.gz/{Sha}");
    }

    [Test]
    public async Task Unpacks_docs_and_policies_without_the_top_directory()
    {
        using var target = new TargetDirectory();

        await Source(Tarball(("top/adr/ADR-001.md", "doc"), ("top/policies/POL-001.rego", "package x"))).FetchAsync(Config, target.Path);

        await Assert.That(await File.ReadAllTextAsync(Path.Combine(target.Path, "adr", "ADR-001.md"))).IsEqualTo("doc");
        await Assert.That(File.Exists(Path.Combine(target.Path, "policies", "POL-001.rego"))).IsTrue();
    }

    [Test]
    public async Task Ignores_everything_that_isnt_a_doc_or_a_policy()
    {
        using var target = new TargetDirectory();

        await Source(Tarball(("top/run.sh", "evil"), ("top/.github/x.yml", "y"), ("top/keep.md", "doc"))).FetchAsync(Config, target.Path);

        await Assert.That(Directory.GetFiles(target.Path, "*", SearchOption.AllDirectories).Length).IsEqualTo(1);
    }

    [Test]
    [Arguments("top/../../evil.md")]
    [Arguments("../evil.md")]
    [Arguments("/etc/evil.md")]
    [Arguments("top/a/../../../evil.rego")]
    public async Task Refuses_a_path_outside_the_target(string name)
    {
        using var target = new TargetDirectory();

        await Assert.That(() => Source(Tarball((name, "evil"))).FetchAsync(Config, target.Path))
            .Throws<StandardsFetchException>();
        await Assert.That(File.Exists(Path.Combine(target.Path, "..", "evil.md"))).IsFalse();
    }

    [Test]
    public async Task Skips_links_so_nothing_can_write_through_one()
    {
        using var target = new TargetDirectory();
        var outside = Path.Combine(target.Path, "..", $"outside-{Guid.NewGuid():N}");
        var archive = Archive(writer =>
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "top/link") { LinkName = outside });
            Add(writer, "top/link/evil.md", "evil");
        });

        await Source(archive).FetchAsync(Config, target.Path);

        await Assert.That(Directory.Exists(outside)).IsFalse();
        await Assert.That(new FileInfo(Path.Combine(target.Path, "link")).LinkTarget).IsNull();
    }

    [Test]
    public async Task Refuses_too_much_unpacked_data()
    {
        using var target = new TargetDirectory();
        var tooBig = new string('a', 51 * 1024 * 1024);

        await Assert.That(() => Source(Tarball(("top/big.md", tooBig))).FetchAsync(Config, target.Path))
            .Throws<StandardsFetchException>();
    }

    [Test]
    public async Task Refuses_too_many_entries()
    {
        using var target = new TargetDirectory();
        var archive = Archive(writer =>
        {
            for (var i = 0; i < 5_001; i++)
            {
                Add(writer, $"top/{i}.txt", string.Empty);
            }
        });

        await Assert.That(() => Source(archive).FetchAsync(Config, target.Path)).Throws<StandardsFetchException>();
    }

    [Test]
    public async Task Refuses_a_download_over_the_limit()
    {
        using var target = new TargetDirectory();
        var handler = new FakeHandler(new byte[21 * 1024 * 1024]);

        await Assert.That(() => Source(handler).FetchAsync(Config, target.Path)).Throws<StandardsFetchException>();
    }

    [Test]
    public async Task Refuses_something_that_isnt_an_archive()
    {
        using var target = new TargetDirectory();

        await Assert.That(() => Source(Encoding.UTF8.GetBytes("<html>not a tarball</html>")).FetchAsync(Config, target.Path))
            .Throws<StandardsFetchException>();
    }

    [Test]
    public async Task Refuses_an_http_error()
    {
        using var target = new TargetDirectory();
        var handler = new FakeHandler([]) { Status = HttpStatusCode.NotFound };

        await Assert.That(() => Source(handler).FetchAsync(Config, target.Path)).Throws<StandardsFetchException>();
    }

    // The bug this pins: a timeout wasn't caught, so the review crashed instead of saying what went wrong.
    [Test]
    public async Task Tries_once_more_after_a_timeout()
    {
        var handler = new FailingHandler(failures: 1, new TaskCanceledException("Timed out."), Tarball(("top/adr/ADR-001.md", "doc")));
        using var target = new TargetDirectory();

        await new GitHubTarballSource(new HttpClient(handler) { BaseAddress = new Uri("https://codeload.example.test/") }).FetchAsync(Config, target.Path);

        await Assert.That(handler.Calls).IsEqualTo(2);
        await Assert.That(File.Exists(Path.Combine(target.Path, "adr/ADR-001.md"))).IsTrue();
    }

    [Test]
    public async Task Reports_a_timeout_that_keeps_happening_as_a_failed_fetch()
    {
        await AssertFailedFetch(new TaskCanceledException("Timed out."));
    }

    [Test]
    public async Task Reports_a_network_failure_that_keeps_happening_as_a_failed_fetch()
    {
        await AssertFailedFetch(new HttpRequestException("No route."));
    }

    [Test]
    public async Task Reports_a_broken_connection_as_a_failed_fetch()
    {
        await AssertFailedFetch(new IOException("Connection reset."));
    }

    [Test]
    public async Task The_callers_cancellation_isnt_a_failed_fetch()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        using var target = new TargetDirectory();
        var handler = new FailingHandler(failures: 2, new TaskCanceledException("Cancelled."), []);
        var source = new GitHubTarballSource(new HttpClient(handler) { BaseAddress = new Uri("https://codeload.example.test/") });

        await Assert.That(async () => { await source.FetchAsync(Config, target.Path, cancelled.Token); }).Throws<OperationCanceledException>();
    }

    private static async Task AssertFailedFetch(Exception failure)
    {
        var handler = new FailingHandler(failures: 2, failure, []);
        using var target = new TargetDirectory();
        var source = new GitHubTarballSource(new HttpClient(handler) { BaseAddress = new Uri("https://codeload.example.test/") });

        await Assert.That(async () => { await source.FetchAsync(Config, target.Path); }).Throws<StandardsFetchException>();
        await Assert.That(handler.Calls).IsEqualTo(2);
    }

    private static GitHubTarballSource Source(byte[] body) => Source(new FakeHandler(body));

    private static GitHubTarballSource Source(FakeHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://codeload.example.test/") });

    private static byte[] Tarball(params (string Name, string Content)[] files) => Archive(writer =>
    {
        foreach (var (name, content) in files)
        {
            Add(writer, name, content);
        }
    });

    private static void Add(TarWriter writer, string name, string content) =>
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
        });

    private static byte[] Archive(Action<TarWriter> write)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            write(writer);
        }

        return output.ToArray();
    }

    private sealed class FailingHandler(int failures, Exception failure, byte[] body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Calls <= failures
                ? Task.FromException<HttpResponseMessage>(failure)
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private sealed class FakeHandler(byte[] body) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        public Uri? Requested { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new ByteArrayContent(body) });
        }
    }

    private sealed class TargetDirectory : IDisposable
    {
        private readonly DirectoryInfo parent = Directory.CreateTempSubdirectory("tfreview-test-");

        public string Path => System.IO.Path.Combine(parent.FullName, "target");

        public void Dispose() => parent.Delete(recursive: true);
    }
}
