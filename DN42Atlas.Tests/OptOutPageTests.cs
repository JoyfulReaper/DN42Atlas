using DN42Atlas.OptOut.Auth;
using DN42Atlas.OptOut.Web;
using DN42Atlas.Registry;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class OptOutPageTests
{
    [TestMethod]
    public void AuthenticatedValuesAreHtmlEncoded()
    {
        var html = OptOutPage.RenderSignedIn(
            new Auth42Identity(
                "subject",
                "<script>alert(1)</script>-MNT",
                4242420425),
            ["<img src=x onerror=alert(1)>.dn42"],
            ["<b>172.20.220.48/28</b>"],
            ["<i>fdf0:e12c:5528::/48</i>"],
            FreshSnapshot());

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.DoesNotContain("<img src=x onerror=alert(1)>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;-MNT", html);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;.dn42", html);
        Assert.Contains("IPv4 prefixes you can manage", html);
        Assert.Contains("&lt;b&gt;172.20.220.48/28&lt;/b&gt;", html);
        Assert.Contains("IPv6 prefixes you can manage", html);
        Assert.Contains("&lt;i&gt;fdf0:e12c:5528::/48&lt;/i&gt;", html);
    }

    [TestMethod]
    public void DashboardRendersFreshRegistryState()
    {
        var html = OptOutPage.RenderSignedIn(
            Identity(),
            [],
            [],
            [],
            FreshSnapshot());

        Assert.Contains("Registry snapshot", html);
        Assert.Contains("abcdef123456", html);
        Assert.Contains("2026-10-03 18:00 UTC", html);
        Assert.Contains("2h 14m", html);
        Assert.Contains("Fresh", html);
        Assert.DoesNotContain("Automatic opt-out approval is disabled", html);
    }

    [TestMethod]
    public void DashboardRendersStaleRegistryWarning()
    {
        var snapshot = FreshSnapshot() with
        {
            Age = TimeSpan.FromHours(73),
            Status = RegistrySnapshotStatus.Stale
        };

        var html = OptOutPage.RenderSignedIn(
            Identity(),
            [],
            [],
            [],
            snapshot);

        Assert.Contains("STALE", html);
        Assert.Contains(
            "Automatic opt-out approval is disabled until the registry is refreshed.",
            html);
    }

    private static Auth42Identity Identity() =>
        new("subject", "JOYFULREAPER-MNT", 4242420425);

    private static RegistrySnapshot FreshSnapshot() =>
        new(
            "/registry",
            "abcdef1234567890abcdef1234567890abcdef12",
            new DateTimeOffset(2026, 10, 3, 17, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 3, 18, 0, 0, TimeSpan.Zero),
            TimeSpan.FromHours(2) + TimeSpan.FromMinutes(14),
            RegistrySnapshotStatus.Fresh);
}
