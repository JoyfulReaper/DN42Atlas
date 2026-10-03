namespace DN42Atlas.Commands;

public static class CommandUsage
{
    public static void Print()
    {
        Console.WriteLine("Usage: dn42atlas [command]");
        Console.WriteLine("  resolve                         Registry parsing and DNS resolution (default)");
        Console.WriteLine("  web-scan [resolution-file]       HTTP/HTTPS scan and HTML report");
        Console.WriteLine("  report <web-probe.json>          Generate an HTML report");
        Console.WriteLine("  probe-test                      Single-host HTTP/HTTPS probe test");
        Console.WriteLine("  run                             Resolve, scan the fresh results, and generate HTML");
        Console.WriteLine("  --help, -h, help                 Show this usage");
    }
}
