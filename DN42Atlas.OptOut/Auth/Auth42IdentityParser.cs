using System.Security.Claims;
using System.Text.Json;

namespace DN42Atlas.OptOut.Auth;

public static class Auth42IdentityParser
{
    public const string Dn42ClaimType = "dn42";

    public static bool TryParse(
        ClaimsPrincipal principal,
        out Auth42Identity? identity)
    {
        identity = null;

        var subjects = principal.FindAll("sub")
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (subjects.Length != 1)
            return false;

        var dn42Claims = principal.FindAll(Dn42ClaimType).ToArray();

        if (dn42Claims.Length == 0)
            return false;

        string? activeMaintainer = null;
        uint? asn = null;

        foreach (var claim in dn42Claims)
        {
            if (!TryParseClaim(
                    claim.Value,
                    out var claimMaintainer,
                    out var claimAsn))
            {
                return false;
            }

            if (activeMaintainer is null)
            {
                activeMaintainer = claimMaintainer;
                asn = claimAsn;
                continue;
            }

            if (!string.Equals(
                    activeMaintainer,
                    claimMaintainer,
                    StringComparison.OrdinalIgnoreCase) ||
                asn != claimAsn)
            {
                return false;
            }
        }

        identity = new Auth42Identity(
            subjects[0],
            activeMaintainer!,
            asn!.Value);

        return true;
    }

    private static bool TryParseClaim(
        string value,
        out string activeMaintainer,
        out uint asn)
    {
        activeMaintainer = string.Empty;
        asn = 0;

        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("active_mnt", out var maintainerElement) ||
                maintainerElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(maintainerElement.GetString()) ||
                !root.TryGetProperty("asn", out var asnElement) ||
                asnElement.ValueKind != JsonValueKind.Number ||
                !asnElement.TryGetUInt32(out asn) ||
                asn == 0)
            {
                return false;
            }

            activeMaintainer = maintainerElement.GetString()!.Trim();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
