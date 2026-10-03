using System.Security.Claims;
using DN42Atlas.OptOut.Auth;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class Auth42IdentityParserTests
{
    [TestMethod]
    public void OneValidDn42ClaimIsAccepted()
    {
        var principal = Principal(
            """
            {
              "active_mnt": "JOYFULREAPER-MNT",
              "asn": 4242420425,
              "route": ["172.20.220.48/28"],
              "active_email": "not-retained@example.invalid"
            }
            """);

        Assert.IsTrue(Auth42IdentityParser.TryParse(principal, out var identity));
        Assert.IsNotNull(identity);
        Assert.AreEqual("operator-subject", identity.Subject);
        Assert.AreEqual("JOYFULREAPER-MNT", identity.ActiveMaintainer);
        Assert.AreEqual(4242420425u, identity.Asn);

        var reduced = identity.ToPrincipal("test");
        Assert.AreEqual(3, reduced.Claims.Count());
        Assert.IsNull(reduced.FindFirst("active_email"));
        Assert.IsNull(reduced.FindFirst(Auth42IdentityParser.Dn42ClaimType));
    }

    [TestMethod]
    public void DuplicateIdenticalDn42ClaimsAreAccepted()
    {
        const string claim =
            """{"active_mnt":"JOYFULREAPER-MNT","asn":4242420425}""";

        Assert.IsTrue(
            Auth42IdentityParser.TryParse(
                Principal(claim, claim),
                out var identity));
        Assert.AreEqual("JOYFULREAPER-MNT", identity!.ActiveMaintainer);
    }

    [TestMethod]
    public void RicherAndSmallerMatchingClaimsAreAccepted()
    {
        var principal = Principal(
            """{"active_mnt":"JOYFULREAPER-MNT","asn":4242420425,"route6":["fdf0:e12c:5528::/48"],"timestamp":1791063059}""",
            """{"active_mnt":"joyfulreaper-mnt","asn":4242420425}""");

        Assert.IsTrue(Auth42IdentityParser.TryParse(principal, out _));
    }

    [TestMethod]
    public void ConflictingActiveMaintainersAreRejected()
    {
        var principal = Principal(
            """{"active_mnt":"JOYFULREAPER-MNT","asn":4242420425}""",
            """{"active_mnt":"OTHER-MNT","asn":4242420425}""");

        Assert.IsFalse(Auth42IdentityParser.TryParse(principal, out _));
    }

    [TestMethod]
    public void ConflictingAsnsAreRejected()
    {
        var principal = Principal(
            """{"active_mnt":"JOYFULREAPER-MNT","asn":4242420425}""",
            """{"active_mnt":"JOYFULREAPER-MNT","asn":4242420426}""");

        Assert.IsFalse(Auth42IdentityParser.TryParse(principal, out _));
    }

    [TestMethod]
    public void MalformedJsonFailsClosed()
    {
        var principal = Principal(
            """{"active_mnt":"JOYFULREAPER-MNT","asn":4242420425}""",
            "{not-json");

        Assert.IsFalse(Auth42IdentityParser.TryParse(principal, out _));
    }

    [TestMethod]
    public void MissingActiveMaintainerIsRejected()
    {
        Assert.IsFalse(
            Auth42IdentityParser.TryParse(
                Principal("""{"asn":4242420425}"""),
                out _));
    }

    [TestMethod]
    public void MissingDn42ClaimIsRejected()
    {
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("sub", "operator-subject")], "test"));

        Assert.IsFalse(Auth42IdentityParser.TryParse(principal, out _));
    }

    [TestMethod]
    public void MissingOrInvalidAsnIsRejected()
    {
        Assert.IsFalse(
            Auth42IdentityParser.TryParse(
                Principal("""{"active_mnt":"JOYFULREAPER-MNT"}"""),
                out _));
        Assert.IsFalse(
            Auth42IdentityParser.TryParse(
                Principal("""{"active_mnt":"JOYFULREAPER-MNT","asn":0}"""),
                out _));
    }

    private static ClaimsPrincipal Principal(params string[] dn42Claims)
    {
        var claims = new List<Claim>
        {
            new("sub", "operator-subject")
        };

        claims.AddRange(
            dn42Claims.Select(
                value => new Claim(Auth42IdentityParser.Dn42ClaimType, value)));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
}
