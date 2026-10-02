using DN42Atlas.Registry;

var path = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    "dn42-registry",
    "data",
    "dns",
    "eb.dn42");

var domain = DomainObjectParser.Parse(path);

Console.WriteLine($"Domain: {domain.Domain}");

Console.WriteLine("Maintainers:");
foreach (var maintainer in domain.Maintainers)
{
    Console.WriteLine($"  {maintainer}");
}

Console.WriteLine("Nameservers:");
foreach (var nameServer in domain.NameServers)
{
    Console.WriteLine($"  {nameServer}");
}