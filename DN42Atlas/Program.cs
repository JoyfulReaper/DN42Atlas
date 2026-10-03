using DN42Atlas.Commands;
using DN42Atlas.Policy;
using DN42Atlas.Probing;
using DN42Atlas.Scanning;

var command = args.FirstOrDefault() ?? "resolve";
if (command is "--help" or "-h" or "help")
{
    CommandUsage.Print();
    return 0;
}

if (command is not ("resolve" or "web-scan" or "report" or "probe-test" or "run" or "registry-update"))
{
    CommandUsage.Print();
    return 2;
}

try
{
    if (command == "registry-update")
        return await RegistryUpdateCommand.ExecuteAsync();

    var exclusionPolicy = ExclusionPolicy.Load(
        Path.Combine(Environment.CurrentDirectory, "config", "excluded-hosts.txt"),
        Path.Combine(Environment.CurrentDirectory, "config", "excluded-prefixes.txt"),
        Environment.GetEnvironmentVariable("DN42ATLAS_RUNTIME_EXCLUSIONS_PATH"));
    var probeTargets = HttpProbeTargets.All;
    var resolve = new ResolveCommand(new RegistryResolver(exclusionPolicy));
    var webScan = new WebScanCommand(new WebScanner(exclusionPolicy, probeTargets), probeTargets, exclusionPolicy);

    switch (command)
    {
        case "report":
            return await ReportCommand.ExecuteAsync(args, exclusionPolicy);
        case "probe-test":
            await new ProbeTestCommand(probeTargets, exclusionPolicy).ExecuteAsync();
            return 0;
        case "web-scan":
            return await webScan.ExecuteAsync(args) != null ? 0 : 1;
        case "run":
            return await new RunCommand(resolve, webScan).ExecuteAsync();
        default:
            await resolve.ExecuteAsync();
            return 0;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
