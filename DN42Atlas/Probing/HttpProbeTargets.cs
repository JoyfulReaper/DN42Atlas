namespace DN42Atlas.Probing;

public static class HttpProbeTargets
{
    public static IReadOnlyList<(string Scheme, int Port)> All { get; } = new[]
{
    ("http", 80),
    ("https", 443),

    ("http", 81),
    ("http", 3000),
    ("http", 3001),
    ("http", 4000),
    ("http", 5000),
    ("http", 5001),
    ("http", 7000),
    ("http", 8000),
    ("http", 8001),
    ("http", 8008),
    ("http", 8080),
    ("http", 8081),
    ("http", 8088),
    ("http", 8880),
    ("http", 8888),
    ("http", 9000),
    ("http", 9090),

    ("https", 4443),
    ("https", 8443),
    ("https", 9443),
    ("https", 10443)
};


}
