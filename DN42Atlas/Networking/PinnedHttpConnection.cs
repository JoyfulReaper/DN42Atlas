using System.Net;
using System.Net.Sockets;

namespace DN42Atlas.Networking;

internal static class PinnedHttpConnection
{
    internal static SocketsHttpHandler CreateHandler(
        string hostname,
        IReadOnlyList<IPAddress> addresses,
        Func<IPAddress[], int, CancellationToken, ValueTask<Stream>>? connect = null)
    {
        // Capture a copy: subsequent caller mutations cannot change approved endpoints.
        var approvedAddresses = addresses.ToArray();
        var canonicalHostname = new UriBuilder("http", hostname).Uri.IdnHost;
        connect ??= ConnectAsync;
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
            ConnectCallback = async (context, cancellationToken) =>
            {
                if (!string.Equals(context.DnsEndPoint.Host, canonicalHostname, StringComparison.OrdinalIgnoreCase))
                    throw new HttpRequestException("Unexpected probe destination.");

                return await connect(approvedAddresses.ToArray(), context.DnsEndPoint.Port, cancellationToken);
            }
        };
    }

    private static async ValueTask<Stream> ConnectAsync(IPAddress[] addresses, int port, CancellationToken cancellationToken)
    {
        // Keep the runtime's multi-address connection handling, using approved IPs instead of DNS.
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(addresses, port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
