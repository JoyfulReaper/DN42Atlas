using System.Net;
using DN42Atlas.Probing;
using DN42Atlas.Registry;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class HttpProberTests
{
    [TestMethod]
    [DataRow("User-agent: *\nDisallow: /", false)]
    [DataRow("\uFEFFUser-agent: *\nDisallow: /", false)]
    [DataRow("User-agent: *\nDisallow: /\nAllow: /$", true)]
    [DataRow("User-agent: DN42Atlas\nDisallow: /\nUser-agent: *\nAllow: /", false)]
    [DataRow("# empty policy", true)]
    public async Task RobotsPolicyControlsHomepageFetching(string robots, bool allowed)
    {
        var requests = new List<string>();
        using var client = CreateClient(requests, path => path == "/robots.txt"
            ? Response(HttpStatusCode.OK, robots)
            : Response(HttpStatusCode.OK, "<title>Example</title>"));
        var result = await Probe(client);
        Assert.AreEqual(allowed, result.RobotsAllowed);
        CollectionAssert.AreEqual(allowed ? new[] { "/robots.txt", "/" } : new[] { "/robots.txt" }, requests);
        Assert.AreEqual(allowed ? "Example" : null, result.Title);
    }

    [TestMethod]
    public async Task TruncatedRobotsPolicyNeverAuthorizesHomepage()
    {
        var requests = new List<string>();
        using var client = CreateClient(requests, _ => Response(HttpStatusCode.OK,
            "User-agent: *\n" + new string('#', 65536) + "\nDisallow: /\n"));
        var result = await Probe(client);
        Assert.AreEqual(RobotsStatus.Unavailable, result.Robots);
        Assert.IsNull(result.RobotsAllowed);
        CollectionAssert.AreEqual(new[] { "/robots.txt" }, requests);
    }

    [TestMethod]
    [DataRow("<html>not a robots policy</html>")]
    [DataRow("User-agent *\nDisallow: /")]
    [DataRow("User-agent: \nDisallow: /")]
    [DataRow("User-agent: *\nDisallow: not-a-path")]
    public async Task AmbiguousRobotsResponseNeverAuthorizesHomepage(string robots)
    {
        var requests = new List<string>();
        using var client = CreateClient(requests, _ => Response(HttpStatusCode.OK, robots));
        var result = await Probe(client);
        Assert.IsNull(result.RobotsAllowed);
        CollectionAssert.AreEqual(new[] { "/robots.txt" }, requests);
    }

    [TestMethod]
    [DataRow(301)]
    [DataRow(403)]
    [DataRow(500)]
    public async Task UnavailableRobotsStatusesDoNotFetchHomepageOrRedirect(int status)
    {
        var requests = new List<string>();
        using var client = CreateClient(requests, _ =>
        {
            var response = Response((HttpStatusCode)status, "");
            response.Headers.Location = new Uri("http://other.dn42/");
            return response;
        });
        var result = await Probe(client);
        Assert.AreEqual(RobotsStatus.Unavailable, result.Robots);
        Assert.AreEqual("http://other.dn42/", result.RedirectLocation);
        CollectionAssert.AreEqual(new[] { "/robots.txt" }, requests);
    }

    [TestMethod]
    public async Task Robots404PermitsHomepageButHomepageRedirectIsOnlyRecorded()
    {
        var requests = new List<string>();
        using var client = CreateClient(requests, path =>
        {
            var response = Response(path == "/robots.txt" ? HttpStatusCode.NotFound : HttpStatusCode.Redirect, "");
            response.Headers.Location = new Uri("http://other.dn42/");
            return response;
        });
        var result = await Probe(client);
        Assert.AreEqual(RobotsStatus.NoRules, result.Robots);
        Assert.AreEqual(302, result.StatusCode);
        Assert.AreEqual("http://other.dn42/", result.HomepageRedirectLocation);
        CollectionAssert.AreEqual(new[] { "/robots.txt", "/" }, requests);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task StalledBodyReadReceivesADeadline(bool robots)
    {
        var requests = new List<string>();
        using var client = CreateClient(requests, path => path == "/robots.txt" && !robots
            ? Response(HttpStatusCode.NotFound, "")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream()) });
        var result = await Probe(client, TimeSpan.FromMilliseconds(100)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsNotNull(robots ? result.Error : result.HomepageError);
        Assert.AreEqual(robots ? RobotsStatus.Unavailable : RobotsStatus.NoRules, result.Robots);
        Assert.HasCount(robots ? 1 : 2, requests);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task StreamIoFailuresAreContainedPerOrigin(bool robots)
    {
        var requests = new List<string>();
        using var client = CreateClient(requests, path => path == "/robots.txt" && !robots
            ? Response(HttpStatusCode.NotFound, "")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream(fail: true)) });
        var result = await Probe(client);
        Assert.IsNotNull(robots ? result.Error : result.HomepageError);
    }

    private static Task<HttpProbeResult> Probe(HttpClient client, TimeSpan? timeout = null) =>
        HttpProber.ProbeAsync("good.dn42", "http", 80, client, timeout ?? TimeSpan.FromSeconds(5));

    private static HttpResponseMessage Response(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

    private static HttpClient CreateClient(List<string> requests, Func<string, HttpResponseMessage> response) =>
        new(new FakeHandler(request =>
        {
            Assert.Contains("DN42Atlas/0.1", request.Headers.UserAgent.ToString());
            var path = request.RequestUri!.AbsolutePath;
            requests.Add(path);
            return response(path);
        }));

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private sealed class BlockingStream(bool fail = false) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (fail) throw new IOException("Simulated broken body stream.");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
