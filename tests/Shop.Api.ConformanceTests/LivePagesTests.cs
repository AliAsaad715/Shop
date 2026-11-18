using System.Net;
using System.Text.RegularExpressions;
using Xunit;

namespace Shop.Api.ConformanceTests;

/// <summary>
/// The live pages (task R4-08) through the real Program: they load without a token, every file under /app gets the
/// pages' own CSP, and nothing in them needs inline code, which that CSP would block.
/// </summary>
[Collection("API conformance")]
public sealed partial class LivePagesTests(ConformanceFixture fixture)
{
    private const string LivePagesPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; "
        + "connect-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";

    public static TheoryData<string, string> Files => new()
    {
        { "/app/orders.html", "text/html" },
        { "/app/admin.html", "text/html" },
        { "/app/css/app.css", "text/css" },
        { "/app/favicon.svg", "image/svg+xml" },
        { "/app/js/session.js", "text/javascript" },
        { "/app/js/orders.js", "text/javascript" },
        { "/app/js/admin.js", "text/javascript" },
        { "/app/lib/signalr/signalr.min.js", "text/javascript" },
    };

    [Theory]
    [MemberData(nameof(Files))]
    public async Task EveryFileLoadsWithoutATokenUnderThePagesOwnPolicy(string path, string mediaType)
    {
        using var response = await fixture.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(LivePagesPolicy, Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));

        // Checked on every load, so a deploy never leaves a browser on old scripts
        Assert.True(response.Headers.CacheControl?.NoCache, "Live page files must be revalidated on every load.");
        Assert.NotNull(response.Headers.ETag);
    }

    [Theory]
    [InlineData("/app")]
    [InlineData("/app/")]
    public async Task TheAppPathOpensTheCustomersPage(string path)
    {
        using var response = await fixture.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/app/orders.html", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task AMissingFileIsA404AndTheApiKeepsItsOwnPolicy()
    {
        using var missing = await fixture.Client.GetAsync("/app/no-such-page.html");
        using var api = await fixture.Client.GetAsync("/api/v1/no-such-resource");

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("form-action 'none'", Assert.Single(api.Headers.GetValues("Content-Security-Policy")));
        Assert.DoesNotContain("script-src", Assert.Single(api.Headers.GetValues("Content-Security-Policy")));
    }

    [Theory]
    [InlineData("/app/orders.html")]
    [InlineData("/app/admin.html")]
    public async Task ThePagesHaveNoInlineScriptsStylesOrHandlers(string path)
    {
        var html = await fixture.Client.GetStringAsync(path);

        // Every script comes from a file, and nothing is styled or wired up inline: script-src 'self' and
        // style-src 'self' without 'unsafe-inline' would block it
        Assert.All(ScriptTag().Matches(html), script => Assert.Contains("src=\"", script.Value));
        Assert.DoesNotMatch(StyleTagOrAttribute(), html);
        Assert.DoesNotMatch(EventHandlerAttribute(), html);
    }

    [Theory]
    [InlineData("/app/js/session.js")]
    [InlineData("/app/js/orders.js")]
    [InlineData("/app/js/admin.js")]
    public async Task ThePagesScriptsNeverWriteHtmlOrEvaluateCode(string path)
    {
        var script = await fixture.Client.GetStringAsync(path);

        // Order data reaches the page as text only, so a product name can never become markup
        Assert.DoesNotMatch(HtmlWriting(), script);
    }

    [GeneratedRegex("<script[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptTag();

    [GeneratedRegex(@"<style|\sstyle\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex StyleTagOrAttribute();

    [GeneratedRegex(@"\son[a-z]+\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex EventHandlerAttribute();

    [GeneratedRegex(@"innerHTML|outerHTML|insertAdjacentHTML|document\.write|\beval\(|new Function")]
    private static partial Regex HtmlWriting();
}
