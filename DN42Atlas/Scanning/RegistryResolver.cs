using System.Net;
using System.Net.Sockets;
using DN42Atlas.Policy;
using DN42Atlas.Registry;

namespace DN42Atlas.Scanning;

public sealed class RegistryResolver
{
    private readonly ExclusionPolicy exclusionPolicy;
    private readonly Func<string, Task<IPAddress[]>> resolveAsync;

    public RegistryResolver(ExclusionPolicy exclusionPolicy,
        Func<string, Task<IPAddress[]>>? resolveAsync = null)
    {
        this.exclusionPolicy = exclusionPolicy;
        this.resolveAsync = resolveAsync ?? (hostname => Dns.GetHostAddressesAsync(hostname));
    }

    public async Task<RegistryResolutionResult> ResolveAsync(string registryPath)
    {
        var registryDomains =
            new List<DomainObject>();

        foreach (var file in
            Directory.EnumerateFiles(
                registryPath))
        {
            try
            {
                registryDomains.Add(
                    DomainParser.Parse(
                        file));
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"Registry parse failed: " +
                    $"{file}: {ex.Message}");
            }
        }

        registryDomains =
            registryDomains
                .Where(
                    x =>
                        x.Domain.EndsWith(
                            ".dn42",
                            StringComparison.OrdinalIgnoreCase))
                .ToList();

        var excludedRegistryHosts =
            registryDomains
                .Where(
                    x =>
                        exclusionPolicy.IsHostExcluded(
                            x.Domain))
                .Select(
                    x => x.Domain)
                .ToList();

        registryDomains =
            registryDomains
                .Where(
                    x =>
                        !exclusionPolicy.IsHostExcluded(
                            x.Domain))
                .ToList();

        Console.WriteLine(
            $"Excluded before DNS: " +
            $"{excludedRegistryHosts.Count}");

        Console.WriteLine(
            $"Registered domains: " +
            $"{registryDomains.Count}");

        Console.WriteLine();

        var resolutions =
            new List<DomainResolution>();

        foreach (var domain in
            registryDomains.OrderBy(
                x => x.Domain))
        {
            try
            {
                var addresses =
                    await resolveAsync(
                        domain.Domain);

                var excludedAddress =
                    addresses.FirstOrDefault(
                        address =>
                            exclusionPolicy.IsAddressExcluded(
                                address));

                if (excludedAddress != null)
                {
                    Console.WriteLine(
                        $"[exclude] {domain.Domain} " +
                        $"resolved to excluded address " +
                        $"{excludedAddress}");

                    continue;
                }

                var result =
                    new DomainResolution
                    {
                        Domain =
                            domain.Domain,

                        Status =
                            addresses.Length > 0
                                ? ResolutionStatus.Resolved
                                : ResolutionStatus.NotFound,

                        Addresses =
                            addresses.ToList()
                    };

                resolutions.Add(
                    result);

                if (result.Status ==
                    ResolutionStatus.Resolved)
                {
                    Console.WriteLine(
                        $"[+] {domain.Domain}");

                    foreach (
                        var address in addresses)
                    {
                        Console.WriteLine(
                            $"    {address}");
                    }
                }
                else
                {
                    Console.WriteLine(
                        $"[-] {domain.Domain}");
                }
            }
            catch (SocketException ex)
            {
                var status =
                    ex.SocketErrorCode switch
                    {
                        SocketError.HostNotFound =>
                            ResolutionStatus.NotFound,

                        SocketError.TryAgain =>
                            ResolutionStatus.TemporaryFailure,

                        _ =>
                            ResolutionStatus.Error
                    };

                resolutions.Add(
                    new DomainResolution
                    {
                        Domain =
                            domain.Domain,

                        Status =
                            status,

                        Error =
                            ex.SocketErrorCode
                                .ToString()
                    });

                Console.WriteLine(
                    $"[-] {domain.Domain} " +
                    $"({status})");
            }
            catch (Exception ex)
            {
                resolutions.Add(
                    new DomainResolution
                    {
                        Domain =
                            domain.Domain,

                        Status =
                            ResolutionStatus.Error,

                        Error =
                            ex.Message
                    });

                Console.WriteLine(
                    $"[!] {domain.Domain}: " +
                    $"{ex.Message}");
            }
        }

        return new RegistryResolutionResult(registryDomains.Count, resolutions);
    }
}
