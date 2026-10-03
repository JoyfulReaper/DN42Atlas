using System.Net;
using System.Text;
using DN42Atlas.Commands;
using DN42Atlas.Networking;
using DN42Atlas.Probing;
using DN42Atlas.Registry;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class ProbeSafetyTests
{
    [TestMethod]
    [DataRow("good.dn42", true)]
    [DataRow("GOOD.DN42", true)]
    [DataRow("outside.example/#.dn42", false)]
    [DataRow("allowed.dn42/path.dn42", false)]
    [DataRow("allowed.dn42?query.dn42", false)]
    [DataRow("outside.example@allowed.dn42", false)]
    [DataRow("allowed.dn42:80", false)]
    [DataRow("not-dn42.example", false)]
    public void DestinationRequiresAnActualDn42Hostname(string hostname, bool expected)
        => Assert.AreEqual(expected, ProbeDestination.IsDn42Hostname(hostname));

    [TestMethod]
    [DataRow("172.20.1.1")]
    [DataRow("fd42:1234::1")]
    [DataRow("192.0.2.1")]
    public async Task PublicProberRejectsExcludedOrExternalAddressSetsBeforeConnecting(string rejected)
    {
        using var files = new TestFiles(prefixes: "172.20.1.0/24\nfd42:1234::/48");
        IPAddress[] addresses = [IPAddress.Parse("fd42::1"), IPAddress.Parse(rejected)];
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            HttpProber.ProbeAsync("good.dn42", "http", 80, files.LoadPolicy(), addresses));
    }

    [TestMethod]
    public async Task ProbeTestChecksHostnameBeforeDns()
    {
        using var files = new TestFiles("burble.dn42");
        var command = new ProbeTestCommand(HttpProbeTargets.All, files.LoadPolicy(),
            _ => throw new AssertFailedException("Excluded host must never reach DNS."),
            (_, _, _, _) => throw new AssertFailedException("Excluded host must never be probed."));
        await command.ExecuteAsync();
    }

    [TestMethod]
    [DataRow("172.20.1.1")]
    [DataRow("fd42:1234::1")]
    [DataRow("192.0.2.1")]
    public async Task ProbeTestChecksEveryResolvedAddressBeforeProbing(string rejected)
    {
        using var files = new TestFiles(prefixes: "172.20.1.0/24\nfd42:1234::/48");
        var command = new ProbeTestCommand(HttpProbeTargets.All, files.LoadPolicy(),
            _ => Task.FromResult(new[] { IPAddress.Parse("fd42::1"), IPAddress.Parse(rejected) }),
            (_, _, _, _) => throw new AssertFailedException("Unsafe address set must never be probed."));
        await command.ExecuteAsync();
    }

    [TestMethod]
    public async Task PinnedTransportUsesOnlyApprovedAddressesAndPreservesHostAcrossRequests()
    {
        var approved = IPAddress.Parse("fd42::1");
        IPAddress[] addresses = [approved];
        var streams = new List<DuplexStream>();
        using var handler = PinnedHttpConnection.CreateHandler("good.dn42", addresses, (destinations, port, _) =>
        {
            CollectionAssert.AreEqual(new[] { approved }, destinations);
            Assert.AreEqual(8080, port);
            var status = streams.Count == 0 ? "404 Not Found" : "200 OK";
            var stream = new DuplexStream($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            streams.Add(stream);
            return ValueTask.FromResult<Stream>(stream);
        });
        // Mutating the caller's array cannot redirect an already configured transport.
        addresses[0] = IPAddress.Parse("192.0.2.1");
        Assert.IsFalse(handler.UseProxy);
        Assert.IsFalse(handler.AllowAutoRedirect);
        Assert.IsNotNull(handler.SslOptions.RemoteCertificateValidationCallback);
        using var client = new HttpClient(handler);
        var result = await HttpProber.ProbeAsync("good.dn42", "http", 8080, client, TimeSpan.FromSeconds(5));
        Assert.AreEqual(200, result.StatusCode);
        Assert.HasCount(2, streams);
        Assert.Contains("GET /robots.txt HTTP/1.1", streams[0].Requests);
        Assert.Contains("GET / HTTP/1.1", streams[1].Requests);
        foreach (var stream in streams)
        {
            Assert.Contains("Host: good.dn42:8080", stream.Requests);
            Assert.Contains("DN42Atlas/0.1 (+https://joyfulreaper.dn42/)", stream.Requests);
        }
    }

    [TestMethod]
    public async Task PinnedTransportRejectsUnexpectedRequestHostBeforeConnection()
    {
        using var handler = PinnedHttpConnection.CreateHandler("good.dn42", [IPAddress.Parse("fd42::1")],
            (_, _, _) => throw new AssertFailedException("Unexpected host must not connect."));
        using var client = new HttpClient(handler);
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.GetAsync("http://other.dn42/"));
    }

    private sealed class DuplexStream(string response) : Stream
    {
        private readonly MemoryStream input = new(Encoding.ASCII.GetBytes(response));
        private readonly MemoryStream output = new();
        public string Requests => Encoding.ASCII.GetString(output.ToArray());
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => input.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => output.WriteAsync(buffer, cancellationToken);
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            // Keep captured request bytes available to assertions after the HTTP client closes the connection.
            if (disposing) input.Dispose();
            base.Dispose(disposing);
        }
    }
}
