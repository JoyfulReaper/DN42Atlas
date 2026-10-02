using System.Net;
using DN42Atlas.Registry;

var registryPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    "dn42-registry",
    "data",
    "dns");

var domains = new List<DomainObject>();

foreach (var file in Directory.EnumerateFiles(registryPath))
{
    try
    {
        domains.Add(DomainParser.Parse(file));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Registry parse failed: {file}: {ex.Message}");
    }
}

Console.WriteLine($"Registered domains: {domains.Count}");
Console.WriteLine();

var resolved = 0;
var unresolved = 0;

foreach (var domain in domains.OrderBy(x => x.Domain))
{
    try
    {
        var addresses = await Dns.GetHostAddressesAsync(domain.Domain);

        if (addresses.Length == 0)
        {
            unresolved++;
            Console.WriteLine($"[-] {domain.Domain}");
            continue;
        }

        resolved++;

        Console.WriteLine($"[+] {domain.Domain}");

        foreach (var address in addresses)
            Console.WriteLine($"    {address}");
    }
    catch
    {
        unresolved++;
        Console.WriteLine($"[-] {domain.Domain}");
    }
}

Console.WriteLine();
Console.WriteLine($"Registered: {domains.Count}");
Console.WriteLine($"Resolved:   {resolved}");
Console.WriteLine($"Unresolved: {unresolved}");