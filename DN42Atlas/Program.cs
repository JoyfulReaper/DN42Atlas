using DN42Atlas.Commands;
using DN42Atlas.Policy;
using DN42Atlas.Probing;
using DN42Atlas.Scanning;

var exclusionPolicy =
    ExclusionPolicy.Load(
        Path.Combine(
            Environment.CurrentDirectory,
            "config",
            "excluded-hosts.txt"),
        Path.Combine(
            Environment.CurrentDirectory,
            "config",
            "excluded-prefixes.txt"));


var probeTargets = HttpProbeTargets.All;

switch (args.FirstOrDefault())
{
    case "report":
        await ReportCommand.ExecuteAsync(args);
        break;
    case "probe-test":
        await new ProbeTestCommand(probeTargets).ExecuteAsync();
        break;
    case "web-scan":
        await new WebScanCommand(new WebScanner(exclusionPolicy, probeTargets), probeTargets)
            .ExecuteAsync(args);
        break;
    default:
        await new ResolveCommand(new RegistryResolver(exclusionPolicy)).ExecuteAsync();
        break;
}