using DN42Atlas.OptOut.Auth;
using DN42Atlas.OptOut.Registry;

namespace DN42Atlas.OptOut.Exclusions;

public enum MutationStatus { Success, InvalidRequest, NotAuthorized, NotRecorded, RecordedRuntimeUnavailable,
    RecordedPublicationWithdrawn, WithdrawalFailed, Uncertain, UncertainWithdrawalFailed }

public sealed class ExclusionMutationCoordinator(ExclusionStore store, RegistryResourceAuthorizer authorizer,
    ExclusionReconciler reconciler, MutationPaths paths, ILogger<ExclusionMutationCoordinator> logger) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<MutationStatus> ExcludeAsync(Auth42Identity identity, string type, string value,
        CancellationToken cancellationToken = default)
    {
        ExactResource resource;
        try { resource = ExactResource.Parse(type, value); }
        catch (Exception ex) when (ex is ArgumentException or FormatException) { return MutationStatus.InvalidRequest; }

        await gate.WaitAsync(cancellationToken);
        try
        {
            DN42Atlas.Registry.RegistrySnapshot? snapshot;
            IReadOnlyList<ExclusionRecord> before;
            try
            {
                snapshot = authorizer.Authorize(identity.ActiveMaintainer, resource);
                if (snapshot == null) return MutationStatus.NotAuthorized;
                before = await store.GetActiveAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Resource authorization or pre-mutation database read failed.");
                return MutationStatus.NotRecorded;
            }

            var backup = paths.Runtime + $".{Guid.NewGuid():N}.backup";
            try { if (File.Exists(paths.Runtime)) File.Move(paths.Runtime, backup); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Cannot make runtime policy fail closed before mutation.");
                return MutationStatus.NotRecorded;
            }

            try
            {
                // Complete recovery even if the client disconnects after the critical sequence starts.
                await store.AddAsync(new NewExclusionRecord(resource.Type, resource.Value, identity.Subject,
                    identity.ActiveMaintainer, identity.Asn, snapshot.CommitSha!, snapshot.ObservedAt!.Value,
                    DateTimeOffset.UtcNow));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Database exclusion mutation failed.");
                // An INSERT may have committed even if its caller saw an exception. Confirm before restoring.
                try
                {
                    var after = await store.GetActiveAsync();
                    if (before.Select(r => r.Id).SequenceEqual(after.Select(r => r.Id)))
                    {
                        if (File.Exists(backup) && !File.Exists(paths.Runtime)) File.Move(backup, paths.Runtime);
                        return MutationStatus.NotRecorded;
                    }
                    if (after.Any(r => r.ResourceType == resource.Type && r.ResourceValue == resource.Value))
                    {
                        return reconciler.WithdrawPublic() ? MutationStatus.RecordedRuntimeUnavailable : MutationStatus.WithdrawalFailed;
                    }
                }
                catch (Exception verification) { logger.LogCritical(verification, "Cannot confirm database mutation outcome."); }
                return reconciler.WithdrawPublic() ? MutationStatus.Uncertain : MutationStatus.UncertainWithdrawalFailed;
            }

            var result = await reconciler.ReconcileAsync();
            if (result is ReconciliationStatus.Success or ReconciliationStatus.PublicationWithdrawn)
            {
                try { File.Delete(backup); }
                catch (Exception ex) { logger.LogWarning(ex, "Unable to remove old private runtime backup."); }
            }
            return result switch
            {
                ReconciliationStatus.Success => MutationStatus.Success,
                ReconciliationStatus.RuntimeUnavailable => MutationStatus.RecordedRuntimeUnavailable,
                ReconciliationStatus.PublicationWithdrawn => MutationStatus.RecordedPublicationWithdrawn,
                _ => MutationStatus.WithdrawalFailed
            };
        }
        finally { gate.Release(); }
    }

    public async Task<ReconciliationStatus> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { return await reconciler.ReconcileAsync(); }
        finally { gate.Release(); }
    }

    public void Dispose() => gate.Dispose();
}
