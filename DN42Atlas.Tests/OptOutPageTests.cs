using DN42Atlas.OptOut.Auth;
using DN42Atlas.OptOut.Web;

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
            ["<img src=x onerror=alert(1)>.dn42"]);

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.DoesNotContain("<img src=x onerror=alert(1)>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;-MNT", html);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;.dn42", html);
    }
}
