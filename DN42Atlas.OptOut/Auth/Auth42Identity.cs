using System.Globalization;
using System.Security.Claims;

namespace DN42Atlas.OptOut.Auth;

public sealed record Auth42Identity(
    string Subject,
    string ActiveMaintainer,
    uint Asn)
{
    public const string ActiveMaintainerClaimType =
        "dn42atlas:active_mnt";

    public const string AsnClaimType =
        "dn42atlas:asn";

    public ClaimsPrincipal ToPrincipal(string authenticationType)
    {
        var claims = new[]
        {
            new Claim("sub", Subject),
            new Claim(ActiveMaintainerClaimType, ActiveMaintainer),
            new Claim(AsnClaimType, Asn.ToString(CultureInfo.InvariantCulture))
        };

        return new ClaimsPrincipal(
            new ClaimsIdentity(
                claims,
                authenticationType,
                ActiveMaintainerClaimType,
                ClaimTypes.Role));
    }

    public static bool TryFromPrincipal(
        ClaimsPrincipal principal,
        out Auth42Identity? identity)
    {
        identity = null;

        var subject = principal.FindFirstValue("sub");
        var activeMaintainer =
            principal.FindFirstValue(ActiveMaintainerClaimType);
        var asnText = principal.FindFirstValue(AsnClaimType);

        if (string.IsNullOrWhiteSpace(subject) ||
            string.IsNullOrWhiteSpace(activeMaintainer) ||
            !uint.TryParse(
                asnText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var asn) ||
            asn == 0)
        {
            return false;
        }

        identity = new Auth42Identity(
            subject,
            activeMaintainer,
            asn);

        return true;
    }
}
