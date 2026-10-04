using System.Security.Cryptography;
using System.Text.Json;
using DN42Atlas.OptOut.Auth;
using DN42Atlas.OptOut.Registry;
using Microsoft.AspNetCore.DataProtection;

namespace DN42Atlas.OptOut.Web;

public sealed class ConfirmationTokens(IDataProtectionProvider provider, TimeProvider? clock = null)
{
    private readonly IDataProtector protector = provider.CreateProtector("DN42Atlas.OperatorConfirmation.v1");
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private sealed record Payload(string Operation, ExactResource Resource, string Subject, string Maintainer,
        DateTimeOffset IssuedAt, long? ActiveRecordId, long? LatestRecordId);

    public string Create(string operation, ExactResource resource, Auth42Identity identity, long? recordId) =>
        protector.Protect(JsonSerializer.Serialize(new Payload(operation, resource, identity.Subject,
            identity.ActiveMaintainer, clock.GetUtcNow(), operation == "include" ? recordId : null,
            operation == "exclude" ? recordId : null)));

    public bool TryValidate(string token, string operation, ExactResource resource, Auth42Identity identity,
        out long? recordId)
    {
        recordId = null;
        if (string.IsNullOrEmpty(token) || token.Length > 4096) return false;
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(protector.Unprotect(token));
            if (payload == null || payload.Operation != operation || payload.Resource != resource ||
                payload.Subject != identity.Subject || payload.Maintainer != identity.ActiveMaintainer ||
                payload.IssuedAt > clock.GetUtcNow() || clock.GetUtcNow() - payload.IssuedAt > TimeSpan.FromMinutes(5) ||
                (operation == "include" && payload.ActiveRecordId is not > 0)) return false;
            recordId = operation == "include" ? payload.ActiveRecordId : payload.LatestRecordId;
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException or ArgumentException)
        { return false; }
    }
}
