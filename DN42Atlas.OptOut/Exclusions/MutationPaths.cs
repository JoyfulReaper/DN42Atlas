using DN42Atlas.Publishing;

namespace DN42Atlas.OptOut.Exclusions;

public sealed record MutationPaths(string Hosts, string Prefixes, string Runtime, string Published, string State)
{
    public string Pending => Runtime + ".reconciliation-pending";

    public static MutationPaths FromConfiguration(IConfiguration configuration)
    {
        string Required(string name)
        {
            var value = configuration[name];
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
                throw new InvalidOperationException($"Configuration '{name}' must be an explicit absolute path.");
            return Path.GetFullPath(value);
        }
        var paths = new MutationPaths(Required("DN42ATLAS_EXCLUDED_HOSTS_PATH"),
            Required("DN42ATLAS_EXCLUDED_PREFIXES_PATH"), Required("DN42ATLAS_RUNTIME_EXCLUSIONS_PATH"),
            Required("DN42ATLAS_PUBLISHED_PATH"), Required("DN42ATLAS_PUBLICATION_STATE_PATH"));
        var operationLock = AtlasOperationLock.GetPath(paths.State, paths.Published);
        foreach (var path in new[] { paths.Hosts, paths.Prefixes, paths.Runtime, paths.State, paths.Pending,
                     Required("DN42ATLAS_EXCLUSION_DB_PATH") })
            PublicationState.EnsureOutsidePublicRoot(path, paths.Published);
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (new[] { paths.Hosts, paths.Prefixes, paths.Runtime, paths.State, paths.Pending, operationLock, Required("DN42ATLAS_EXCLUSION_DB_PATH") }
            .Distinct(comparison).Count() != 7)
            throw new InvalidOperationException("Mutation files must have distinct paths.");
        if (configuration["DN42ATLAS_MANUAL_REQUEST_DB_PATH"] is string manualDb &&
            (comparison.Equals(Path.GetFullPath(manualDb), paths.Pending) || comparison.Equals(Path.GetFullPath(manualDb), operationLock)))
            throw new InvalidOperationException("Manual request storage must not use the reconciliation fence path.");
        return paths;
    }
}
