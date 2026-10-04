using DN42Atlas.Commands;
using DN42Atlas.Policy;

namespace DN42Atlas.OptOut.Exclusions;

public enum ReconciliationStatus { Success, RuntimeUnavailable, PublicationWithdrawn, WithdrawalFailed }

public sealed class ExclusionReconciler(ExclusionStore store, MutationPaths paths, ILogger<ExclusionReconciler> logger,
    Func<CancellationToken, Task>? materialize = null,
    Func<ExclusionPolicy, CancellationToken, Task>? republish = null)
{
    public bool BeginRestrictiveMutation()
    {
        try { RestrictiveReconciliationFence.Establish(paths); }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Cannot durably fence restrictive reconciliation.");
            _ = WithdrawPublic();
            return false;
        }
        // nginx serves these files independently of this process. Remove both before any commit.
        return WithdrawPublic();
    }

    public async Task RecoverPendingAsync()
    {
        if (!RestrictiveReconciliationFence.IsPending(paths)) return;
        logger.LogWarning("Recovering unfinished restrictive reconciliation before startup.");
        if (await ReconcileAsync() != ReconciliationStatus.Success)
            throw new InvalidOperationException("Unfinished exclusion reconciliation could not be recovered. The listing was withdrawn where possible; startup refused.");
    }

    public async Task<ReconciliationStatus> ReconcileAsync()
    {
        if (!BeginRestrictiveMutation()) return ReconciliationStatus.WithdrawalFailed;
        ExclusionPolicy policy;
        var backup = paths.Runtime + $".{Guid.NewGuid():N}.backup";
        try
        {
            // Maintenance recovery must also fence a possibly outdated policy before rebuilding it.
            if (File.Exists(paths.Runtime)) File.Move(paths.Runtime, backup);
            await (materialize?.Invoke(CancellationToken.None)
                ?? RuntimeExclusionMaterializer.MaterializeAsync(store, paths.Runtime));
            _ = RuntimeExclusionBundle.Load(paths.Runtime);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Runtime reconciliation failed; keeping crawler policy unavailable.");
            try { File.Delete(paths.Runtime); }
            catch (Exception cleanup) { logger.LogCritical(cleanup, "Unable to withdraw runtime policy."); }
            return WithdrawPublic() ? ReconciliationStatus.RuntimeUnavailable : ReconciliationStatus.WithdrawalFailed;
        }

        try { File.Delete(backup); }
        catch (Exception ex) { logger.LogWarning(ex, "Unable to remove old private runtime backup."); }

        try
        {
            policy = ExclusionPolicy.Load(paths.Hosts, paths.Prefixes, paths.Runtime);
            if (republish != null) await republish(policy, CancellationToken.None);
            else await new RepublishCommand(policy, paths.Published, paths.State).ExecuteAsync();
            File.Delete(paths.Pending);
            return ReconciliationStatus.Success;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Public reconciliation failed; withdrawing current Atlas listing.");
            return WithdrawPublic() ? ReconciliationStatus.PublicationWithdrawn : ReconciliationStatus.WithdrawalFailed;
        }
    }

    public bool WithdrawPublic()
    {
        var success = true;
        foreach (var name in new[] { "index.html", "latest.json" })
        {
            try { File.Delete(Path.Combine(paths.Published, name)); }
            catch (Exception ex)
            {
                success = false;
                logger.LogCritical(ex, "Unable to withdraw a stable public artifact.");
            }
        }
        return success;
    }
}
