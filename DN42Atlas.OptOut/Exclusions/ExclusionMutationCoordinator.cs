using DN42Atlas.OptOut.Auth;
using DN42Atlas.OptOut.Registry;
using DN42Atlas.Policy;
using DN42Atlas.Commands;
using DN42Atlas.Publishing;

namespace DN42Atlas.OptOut.Exclusions;

public enum MutationStatus { Success, InvalidRequest, NotAuthorized, NotRecorded, RecordedRuntimeUnavailable,
    RecordedPublicationWithdrawn, WithdrawalFailed, Uncertain, UncertainWithdrawalFailed,
    Conflict, InclusionRestored, InclusionUnavailable, Busy }

// Inclusion binds the active row; exclusion binds the latest historical generation (possibly absent).
public sealed record ConfirmationPreparation(MutationStatus Status, ExactResource? Resource = null, long? RecordId = null);

// Narrow operation seams also allow recovery failures to be exercised without a crawler or web server.
public sealed class InclusionOperations
{
    public Func<string, Task>? MaterializeCandidate { get; init; }
    public Func<ExclusionPolicy, Task>? Republish { get; init; }
    public Action<string, string>? InstallRuntime { get; init; }
    public Func<long, DateTimeOffset, Task<bool>>? Reactivate { get; init; }
}

public sealed class ExclusionMutationCoordinator(ExclusionStore store, RegistryResourceAuthorizer authorizer,
    ExclusionReconciler reconciler, MutationPaths paths, ILogger<ExclusionMutationCoordinator> logger,
    InclusionOperations? inclusionOperations = null) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);

    private async Task<bool> TryEnterMutationAsync(CancellationToken cancellationToken)
    {
        if (await gate.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken)) return true;
        logger.LogInformation("Another Atlas mutation owns the local gate; mutation deferred without changes.");
        return false;
    }

    private async Task<AtlasOperationLock?> TryAcquireOperationAsync(CancellationToken cancellationToken)
    {
        var operation = await AtlasOperationLock.TryAcquireAsync(paths.State, paths.Published,
            TimeSpan.FromSeconds(1), cancellationToken);
        if (operation == null) logger.LogInformation("Atlas publisher owns the operation lock; mutation deferred without changes.");
        return operation;
    }

    public static bool IsManuallyExcluded(ExclusionPolicy manual, ExactResource resource) =>
        resource.Type == ExclusionResourceType.Domain
            ? manual.IsHostExcluded(resource.Value) : manual.IsPrefixExcluded(resource.Value);

    public async Task<ConfirmationPreparation> PrepareAsync(Auth42Identity identity, string operation,
        string type, string value, CancellationToken cancellationToken = default)
    {
        ExactResource resource;
        try { resource = ExactResource.Parse(type, value); }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        { return new(MutationStatus.InvalidRequest); }
        if (operation is not ("exclude" or "include")) return new(MutationStatus.InvalidRequest);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (authorizer.Authorize(identity.ActiveMaintainer, resource) == null)
                return new(MutationStatus.NotAuthorized);
            _ = ExclusionPolicy.Load(paths.Hosts, paths.Prefixes, paths.Runtime);
            var manual = ExclusionPolicy.Load(paths.Hosts, paths.Prefixes);
            var active = (await store.GetActiveAsync(cancellationToken))
                .SingleOrDefault(r => r.ResourceType == resource.Type && r.ResourceValue == resource.Value);
            // A new exclusion confirmation is only meaningful for an included resource.
            // Otherwise its active-row ID would remain unchanged after inclusion revoked that row.
            if (IsManuallyExcluded(manual, resource) || (operation == "include" && active == null) ||
                (operation == "exclude" && active != null))
                return new(MutationStatus.Conflict);
            var recordId = operation == "include" ? active!.Id
                : (await store.GetLatestAsync(resource.Type, resource.Value, cancellationToken))?.Id;
            return new(MutationStatus.Success, resource, recordId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Confirmation preparation failed.");
            return new(MutationStatus.NotRecorded);
        }
        finally { gate.Release(); }
    }

    public async Task<MutationStatus> IncludeAsync(Auth42Identity identity, string type, string value,
        long expectedActiveRecordId, CancellationToken cancellationToken = default)
    {
        ExactResource resource;
        try { resource = ExactResource.Parse(type, value); }
        catch (Exception ex) when (ex is ArgumentException or FormatException) { return MutationStatus.InvalidRequest; }
        if (!await TryEnterMutationAsync(cancellationToken)) return MutationStatus.Busy;
        try
        {
            using var operation = await TryAcquireOperationAsync(cancellationToken);
            if (operation == null) return MutationStatus.Busy;
            ExclusionRecord? active;
            try
            {
                if (authorizer.Authorize(identity.ActiveMaintainer, resource) == null) return MutationStatus.NotAuthorized;
                var manual = ExclusionPolicy.Load(paths.Hosts, paths.Prefixes);
                if (IsManuallyExcluded(manual, resource)) return MutationStatus.Conflict;
                active = (await store.GetActiveAsync(cancellationToken))
                    .SingleOrDefault(r => r.ResourceType == resource.Type && r.ResourceValue == resource.Value);
                if (active == null || active.Id != expectedActiveRecordId) return MutationStatus.Conflict;
                _ = ExclusionPolicy.Load(paths.Hosts, paths.Prefixes, paths.Runtime);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Inclusion authorization or pre-mutation read failed.");
                return MutationStatus.NotRecorded;
            }

            var backup = paths.Runtime + $".{Guid.NewGuid():N}.backup";
            var candidate = paths.Runtime + $".{Guid.NewGuid():N}.candidate";
            try { File.Move(paths.Runtime, backup); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Cannot fence runtime policy before inclusion.");
                return MutationStatus.NotRecorded;
            }
            var revokedUtc = DateTimeOffset.UtcNow;
            try
            {
                // Client cancellation cannot interrupt this critical sequence or its recovery.
                if (!await store.RevokeAsync(active.Id, revokedUtc)) throw new InvalidOperationException("Active exclusion changed.");
                await (inclusionOperations?.MaterializeCandidate?.Invoke(candidate)
                    ?? RuntimeExclusionMaterializer.MaterializeAsync(store, candidate));
                _ = RuntimeExclusionBundle.Load(candidate);
                var policy = ExclusionPolicy.Load(paths.Hosts, paths.Prefixes, candidate);
                // Manual policy may have changed while preparing the candidate.
                if (IsManuallyExcluded(ExclusionPolicy.Load(paths.Hosts, paths.Prefixes), resource))
                    throw new InvalidOperationException("Manual policy now excludes the resource.");
                if (inclusionOperations?.Republish != null) await inclusionOperations.Republish(policy);
                else await new RepublishCommand(policy, paths.Published, paths.State).ExecuteAsync();
                if (inclusionOperations?.InstallRuntime != null) inclusionOperations.InstallRuntime(candidate, paths.Runtime);
                else File.Move(candidate, paths.Runtime);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Inclusion failed; recovering the original exclusion.");
                try
                {
                    // Reactivation is also restrictive: withdraw any candidate listing before
                    // restoring the audit row, and leave durable recovery evidence if interrupted.
                    if (!reconciler.BeginRestrictiveMutation(operation))
                        throw new InvalidOperationException("Cannot fence exclusion reactivation.");
                    File.Delete(paths.Runtime);
                    var row = await store.GetByIdAsync(active.Id);
                    if (row == null) throw new InvalidOperationException("Original exclusion is unavailable.");
                    if (row.RevokedUtc != null &&
                        !(await (inclusionOperations?.Reactivate?.Invoke(active.Id, revokedUtc)
                            ?? store.ReactivateAsync(active.Id, revokedUtc))))
                        throw new InvalidOperationException("Cannot prove exclusion reactivation.");
                    var restored = await store.GetByIdAsync(active.Id);
                    if (restored != active) throw new InvalidOperationException("Original exclusion was not restored.");
                    if (await reconciler.ReconcileUnderLockAsync(operation) != ReconciliationStatus.Success)
                        throw new InvalidOperationException("Exclusion recovery did not complete.");
                    DeletePrivate(backup);
                    return MutationStatus.InclusionRestored;
                }
                catch (Exception recovery)
                {
                    logger.LogCritical(recovery, "Inclusion recovery could not be proven.");
                    var runtimeUnavailable = true;
                    try { File.Delete(paths.Runtime); }
                    catch (Exception cleanup) { runtimeUnavailable = false; logger.LogCritical(cleanup, "Cannot withdraw runtime policy."); }
                    var withdrawn = reconciler.WithdrawPublic();
                    return runtimeUnavailable && withdrawn ? MutationStatus.InclusionUnavailable : MutationStatus.WithdrawalFailed;
                }
            }
            finally { DeletePrivate(candidate); }
            DeletePrivate(backup);
            return MutationStatus.Success;
        }
        finally { gate.Release(); }
    }

    private void DeletePrivate(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { logger.LogWarning(ex, "Unable to remove private inclusion staging file."); }
    }

    public async Task<MutationStatus> ExcludeAsync(Auth42Identity identity, string type, string value,
        CancellationToken cancellationToken = default, long? expectedLatestRecordId = null)
    {
        ExactResource resource;
        try { resource = ExactResource.Parse(type, value); }
        catch (Exception ex) when (ex is ArgumentException or FormatException) { return MutationStatus.InvalidRequest; }

        if (!await TryEnterMutationAsync(cancellationToken)) return MutationStatus.Busy;
        try
        {
            using var operation = await TryAcquireOperationAsync(cancellationToken);
            if (operation == null) return MutationStatus.Busy;
            DN42Atlas.Registry.RegistrySnapshot? snapshot;
            IReadOnlyList<ExclusionRecord> before;
            bool alreadyActive;
            try
            {
                snapshot = authorizer.Authorize(identity.ActiveMaintainer, resource);
                if (snapshot == null) return MutationStatus.NotAuthorized;
                before = await store.GetActiveAsync(cancellationToken);
                alreadyActive = before.Any(r => r.ResourceType == resource.Type && r.ResourceValue == resource.Value);
                if (!alreadyActive &&
                    (await store.GetLatestAsync(resource.Type, resource.Value, cancellationToken))?.Id != expectedLatestRecordId)
                    return MutationStatus.Conflict;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Resource authorization or pre-mutation database read failed.");
                return MutationStatus.NotRecorded;
            }

            // Durable fence and static withdrawal precede SQLite, so process termination
            // cannot strand an old listing after a restrictive exclusion commits.
            if (!reconciler.BeginRestrictiveMutation(operation)) return MutationStatus.NotRecorded;
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
                // An active exclusion is idempotent regardless of the token's historical generation.
                // Still reconcile its policy/publication, but never attempt another INSERT.
                if (!alreadyActive)
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
                        // Regenerate from authoritative unchanged records; never restore stale public backups.
                        _ = await reconciler.ReconcileUnderLockAsync(operation);
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

            var result = await reconciler.ReconcileUnderLockAsync(operation);
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
