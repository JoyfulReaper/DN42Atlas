namespace DN42Atlas.Registry;

public sealed record RegistryUpdateResult(
    string CommitSha,
    DateTimeOffset ObservedAt,
    string Upstream);

public sealed class RegistryUpdater(
    IRegistryRepositoryInspector? repositoryInspector = null,
    TimeProvider? timeProvider = null)
{
    private const string LockFileName =
        "dn42atlas-registry-update.lock";

    private readonly IRegistryRepositoryInspector repositoryInspector =
        repositoryInspector ?? new GitRegistryRepositoryInspector();

    private readonly TimeProvider timeProvider =
        timeProvider ?? TimeProvider.System;

    public async Task<RegistryUpdateResult> UpdateAsync(
        string registryRoot,
        string? configuredUpstream = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryRoot);

        var root = Path.GetFullPath(registryRoot);

        if (!Directory.Exists(root) ||
            !RegistryCheckoutLayout.IsPresent(root) ||
            !repositoryInspector.TryInspect(root, out var initialState) ||
            initialState is null)
        {
            throw new InvalidOperationException(
                "The configured registry path is not an inspectable Git checkout.");
        }

        await using var updateLock = AcquireLock(initialState.GitDirectory);

        await RequireCleanWorkingTreeAsync(root, cancellationToken);

        var upstream = await ResolveUpstreamAsync(
            root,
            configuredUpstream,
            cancellationToken);

        await RequireSuccessfulGitAsync(
            root,
            cancellationToken,
            "fetch",
            "--prune",
            "origin");
        await RequireSuccessfulGitAsync(
            root,
            cancellationToken,
            "reset",
            "--hard",
            upstream);

        await RequireCleanWorkingTreeAsync(root, cancellationToken);

        if (!repositoryInspector.TryInspect(root, out var updatedState) ||
            updatedState is null)
        {
            throw new InvalidOperationException(
                "The updated registry commit could not be inspected.");
        }

        var observedAt = timeProvider.GetUtcNow();

        await RegistrySnapshotObservationStore.WriteAsync(
            updatedState.GitDirectory,
            new RegistrySnapshotObservation(
                updatedState.CommitSha,
                observedAt),
            cancellationToken);

        return new RegistryUpdateResult(
            updatedState.CommitSha,
            observedAt,
            upstream);
    }

    private static FileStream AcquireLock(string gitDirectory)
    {
        try
        {
            return new FileStream(
                Path.Combine(gitDirectory, LockFileName),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                "Another registry update is already running.",
                ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException(
                "The registry update lock could not be acquired.",
                ex);
        }
    }

    private static async Task<string> ResolveUpstreamAsync(
        string root,
        string? configuredUpstream,
        CancellationToken cancellationToken)
    {
        var upstream = configuredUpstream?.Trim();

        if (string.IsNullOrEmpty(upstream))
        {
            var detected = await GitProcess.RunAsync(
                root,
                cancellationToken,
                "rev-parse",
                "--abbrev-ref",
                "--symbolic-full-name",
                "@{upstream}");

            if (!detected.Succeeded)
                throw new InvalidOperationException(
                    "The registry branch has no configured upstream. Set DN42ATLAS_REGISTRY_UPSTREAM to an origin branch.");

            upstream = detected.Output;
        }

        if (!upstream.StartsWith("origin/", StringComparison.Ordinal) ||
            upstream.Length == "origin/".Length)
        {
            throw new InvalidOperationException(
                "The registry upstream must name a branch under origin.");
        }

        var branch = upstream["origin/".Length..];
        var validBranch = await GitProcess.RunAsync(
            root,
            cancellationToken,
            "check-ref-format",
            "--branch",
            branch);

        if (!validBranch.Succeeded)
            throw new InvalidOperationException(
                "The configured registry upstream branch is invalid.");

        return upstream;
    }

    private static async Task RequireCleanWorkingTreeAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var status = await GitProcess.RunAsync(
            root,
            cancellationToken,
            "status",
            "--porcelain",
            "--untracked-files=all");

        if (!status.Succeeded)
            throw new InvalidOperationException(
                "The registry working tree status could not be inspected.");

        if (!string.IsNullOrEmpty(status.Output))
            throw new InvalidOperationException(
                "The registry working tree is not clean; update was not attempted.");
    }

    private static async Task RequireSuccessfulGitAsync(
        string root,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var result = await GitProcess.RunAsync(
            root,
            cancellationToken,
            arguments);

        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"Registry Git operation '{arguments[0]}' failed.");
    }
}
