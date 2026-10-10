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
    [DataRow(false)]
    [DataRow(true)]
    public async Task TruncatedRobotsPolicyNeverAuthorizesHomepage(bool redirect)
    {
        var requests = new List<string>();
        using var client = CreateClient(requests, path => redirect && path == "/robots.txt"
            ? Redirect(HttpStatusCode.Redirect, "/rules.txt") : Response(HttpStatusCode.OK,
            "User-agent: *\n" + new string('#', 65536) + "\nDisallow: /\n"));
        var result = await Probe(client);
        Assert.AreEqual(RobotsStatus.Unavailable, result.Robots);
        Assert.IsNull(result.RobotsAllowed);
        CollectionAssert.AreEqual(redirect ? new[] { "/robots.txt", "/rules.txt" } : new[] { "/robots.txt" }, requests);
    }

    [TestMethod]
    [DataRow(301, "http", true)]
    [DataRow(301, "http", false)]
    [DataRow(302, "http", true)]
    [DataRow(303, "http", false)]
    [DataRow(307, "http", true)]
    [DataRow(308, "http", false)]
    [DataRow(302, "https", true)]
    [DataRow(302, "https", false)]
    public async Task SameHostHttpsRobotsRedirectHonorsPolicy(int status, string scheme, bool allowed)
    {
        var requests = new List<string>();
        using var client = CreateClient(requests, path => path switch
        {
            "/robots.txt" => Redirect((HttpStatusCode)status, "https://GOOD.DN42/rules.txt"),
            "/rules.txt" => Response(HttpStatusCode.OK, allowed ? "User-agent: *\nAllow: /" : "User-agent: *\nDisallow: /"),
            _ => Response(HttpStatusCode.OK, "<title>Example</title>")
        }, absoluteUris: true);
        var result = await HttpProber.ProbeAsync("good.dn42", scheme, scheme == "https" ? 443 : 80,
            client, TimeSpan.FromSeconds(5));
        var expected = new List<string> { $"{scheme}://good.dn42/robots.txt", "https://good.dn42/rules.txt" };
        if (allowed) expected.Add($"{scheme}://good.dn42/");
        CollectionAssert.AreEqual(expected, requests);
        Assert.AreEqual(allowed ? RobotsStatus.Allowed : RobotsStatus.Disallowed, result.Robots);
        Assert.AreEqual(allowed, result.RobotsAllowed);
        Assert.AreEqual(200, result.RobotsStatusCode);
        Assert.AreEqual(allowed ? "Example" : null, result.Title);
    }

    [TestMethod]
    [DataRow("rules.txt")]
    [DataRow("/rules.txt")]
    [DataRow("//GOOD.DN42/rules.txt")]
    public async Task SameHostRelativeRobotsRedirectIsFollowed(string location)
    {
        var requests = new List<string>();
        using var client = CreateClient(requests, path => path == "/robots.txt"
            ? Redirect(HttpStatusCode.Redirect, location)
            : Response(HttpStatusCode.OK, "User-agent: *\nDisallow: /"), absoluteUris: true);
        var result = await Probe(client);
        Assert.AreEqual(RobotsStatus.Disallowed, result.Robots);
        CollectionAssert.AreEqual(new[] { "http://good.dn42/robots.txt", "http://good.dn42/rules.txt" }, requests);
    }

    [TestMethod]
    [DataRow("http://other.dn42/robots.txt")]
    [DataRow("//other.dn42/robots.txt")]
    [DataRow("https://good.dn42.other.dn42/robots.txt")]
    [DataRow("https://good.dn42@other.dn42/robots.txt")]
    [DataRow("ftp://good.dn42/robots.txt")]
    public async Task UnsafeRobotsRedirectNeverAuthorizesHomepage(string location)
    {
        var requests = new List<string>();
        using var client = CreateClient(requests, _ => Redirect(HttpStatusCode.Redirect, location));
        var result = await Probe(client);
        Assert.AreEqual(RobotsStatus.Unavailable, result.Robots);
        Assert.IsNull(result.RobotsAllowed);
        CollectionAssert.AreEqual(new[] { "/robots.txt" }, requests);
    }

    [TestMethod]
    [DataRow(301)]
    [DataRow(302)]
    [DataRow(303)]
    [DataRow(307)]
    [DataRow(308)]
    [DataRow(403)]
    [DataRow(500)]
    public async Task RedirectedRobotsFailureDoesNotFetchAnotherRedirectOrHomepage(int status)
    {
        var requests = new List<string>();
        using var client = CreateClient(requests, path => path == "/robots.txt"
            ? Redirect(HttpStatusCode.Redirect, "/rules.txt")
            : Redirect((HttpStatusCode)status, "/third.txt"));
        var result = await Probe(client);
        Assert.AreEqual(RobotsStatus.Unavailable, result.Robots);
        Assert.IsNull(result.RobotsAllowed);
        Assert.AreEqual(status, result.RobotsStatusCode);
        CollectionAssert.AreEqual(new[] { "/robots.txt", "/rules.txt" }, requests);
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
    [DataRow(302)]
    [DataRow(303)]
    [DataRow(307)]
    [DataRow(308)]
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
    [DataRow("http://other.dn42/")]
    [DataRow("https://good.dn42/")]
    public async Task Robots404PermitsHomepageButHomepageRedirectIsOnlyRecorded(string location)
    {
        var requests = new List<string>();
        using var client = CreateClient(requests, path =>
        {
            var response = Response(path == "/robots.txt" ? HttpStatusCode.NotFound : HttpStatusCode.Redirect, "");
            response.Headers.Location = new Uri(location);
            return response;
        });
        var result = await Probe(client);
        Assert.AreEqual(RobotsStatus.NoRules, result.Robots);
        Assert.AreEqual(302, result.StatusCode);
        Assert.AreEqual(location, result.HomepageRedirectLocation);
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
    [DataRow(false)]
    [DataRow(true)]
    public async Task RedirectedRobotsBodyHonorsDeadlineAndCancellation(bool cancelCaller)
    {
        var requests = new List<string>();
        using var cancellation = new CancellationTokenSource();
        using var client = CreateClient(requests, path =>
        {
            if (path == "/robots.txt") return Redirect(HttpStatusCode.Redirect, "/rules.txt");
            if (cancelCaller) cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream()) };
        });
        var result = await HttpProber.ProbeAsync("good.dn42", "http", 80, client,
            cancelCaller ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(100), cancellation.Token)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(RobotsStatus.Unavailable, result.Robots);
        Assert.IsNotNull(result.Error);
        CollectionAssert.AreEqual(new[] { "/robots.txt", "/rules.txt" }, requests);
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

    private static HttpResponseMessage Redirect(HttpStatusCode status, string location)
    {
        var response = Response(status, "");
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static HttpClient CreateClient(List<string> requests, Func<string, HttpResponseMessage> response, bool absoluteUris = false) =>
        new(new FakeHandler(request =>
        {
            Assert.AreEqual("DN42Atlas/0.2 (+https://dn42atlas.dn42/)", request.Headers.UserAgent.ToString());
            var path = request.RequestUri!.AbsolutePath;
            requests.Add(absoluteUris ? request.RequestUri.AbsoluteUri : path);
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
