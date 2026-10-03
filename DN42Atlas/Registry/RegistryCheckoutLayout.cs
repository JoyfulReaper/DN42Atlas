namespace DN42Atlas.Registry;

public static class RegistryCheckoutLayout
{
    public static bool IsPresent(string registryRoot) =>
        Directory.Exists(Path.Combine(registryRoot, "data", "dns")) &&
        Directory.Exists(Path.Combine(registryRoot, "data", "inetnum")) &&
        Directory.Exists(Path.Combine(registryRoot, "data", "inet6num"));
}
