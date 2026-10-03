using System.Net;
using System.Net.Sockets;

namespace DN42Atlas.Networking;

public static class Dn42AddressSpace
{
    public static bool Contains(
        string value)
    {
        if (!IPAddress.TryParse(
            value,
            out var address))
        {
            return false;
        }

        var bytes =
            address.GetAddressBytes();

        if (address.AddressFamily ==
            AddressFamily.InterNetworkV6)
        {
            //
            // DN42 uses ULA IPv6 space.
            //
            return bytes[0] == 0xfd;
        }

        if (address.AddressFamily !=
            AddressFamily.InterNetwork)
        {
            return false;
        }

        //
        // 172.20.0.0/14
        //
        if (bytes[0] == 172 &&
            bytes[1] >= 20 &&
            bytes[1] <= 23)
        {
            return true;
        }

        //
        // 172.31.0.0/16
        //
        if (bytes[0] == 172 &&
            bytes[1] == 31)
        {
            return true;
        }

        //
        // 10.100.0.0/14
        //
        if (bytes[0] == 10 &&
            bytes[1] >= 100 &&
            bytes[1] <= 103)
        {
            return true;
        }

        //
        // 10.127.0.0/16
        //
        if (bytes[0] == 10 &&
            bytes[1] == 127)
        {
            return true;
        }

        return false;
    }
}
