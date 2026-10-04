using DN42Atlas.OptOut.Auth;
using DN42Atlas.OptOut.Exclusions;
using DN42Atlas.OptOut.Registry;
using DN42Atlas.Policy;
using DN42Atlas.Registry;
using Microsoft.AspNetCore.Antiforgery;

namespace DN42Atlas.OptOut.Web;

public static class OperatorEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/operator", GetAsync);
        app.MapPost("/operator", PostAsync);
    }

    public static async Task<IResult> GetAsync(HttpContext context, RegistryDomainCatalog domains,
        RegistryAllocationCatalog allocations, RegistrySnapshotService snapshots, ExclusionStore store,
        MutationPaths paths, IAntiforgery antiforgery, ILoggerFactory logs)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (context.User.Identity?.IsAuthenticated != true)
            return Results.Content(OptOutPage.RenderSignedOut(), "text/html; charset=utf-8");
        if (!Auth42Identity.TryFromPrincipal(context.User, out var identity)) return Results.Unauthorized();
        var snapshot = snapshots.GetSnapshot();
        IReadOnlyList<string> maintainedDomains = [];
        var maintainedAllocations = new MaintainedAllocations([], []);
        var excluded = new HashSet<ExactResource>();
        var available = true;
        try
        {
            maintainedDomains = domains.FindDomains(identity!.ActiveMaintainer);
            maintainedAllocations = allocations.FindAllocations(identity.ActiveMaintainer);
            var active = await store.GetActiveAsync(context.RequestAborted);
            foreach (var record in active) excluded.Add(new(record.ResourceType, record.ResourceValue));
            var policy = ExclusionPolicy.Load(paths.Hosts, paths.Prefixes, paths.Runtime);
            foreach (var domain in maintainedDomains.Where(d => policy.IsHostExcluded(d)))
                excluded.Add(new(ExclusionResourceType.Domain, domain));
            foreach (var prefix in maintainedAllocations.Ipv4Prefixes.Where(policy.PrefixRules.Contains))
                excluded.Add(new(ExclusionResourceType.IPv4Prefix, prefix));
            foreach (var prefix in maintainedAllocations.Ipv6Prefixes.Where(policy.PrefixRules.Contains))
                excluded.Add(new(ExclusionResourceType.IPv6Prefix, prefix));
        }
        catch (Exception ex)
        {
            logs.CreateLogger("OperatorDashboard").LogWarning(ex, "Operator dashboard data unavailable.");
            available = false;
        }
        var token = antiforgery.GetAndStoreTokens(context).RequestToken!;
        return Results.Content(OptOutPage.RenderSignedIn(identity!, maintainedDomains,
            maintainedAllocations.Ipv4Prefixes, maintainedAllocations.Ipv6Prefixes, snapshot, available,
            excluded, token, context.Request.Query["result"].ToString()), "text/html; charset=utf-8");
    }

    public static async Task<IResult> PostAsync(HttpContext context, IAntiforgery antiforgery,
        ExclusionMutationCoordinator coordinator)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (context.User.Identity?.IsAuthenticated != true ||
            !Auth42Identity.TryFromPrincipal(context.User, out var identity)) return Results.Unauthorized();
        if (context.Request.ContentLength > 4096 || context.Request.ContentType == null ||
            !context.Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest();

        IFormCollection form;
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            form = await context.Request.ReadFormAsync(context.RequestAborted);
        }
        catch (Exception ex) when (ex is AntiforgeryValidationException or InvalidDataException or BadHttpRequestException)
        { return Results.BadRequest(); }

        if (form.Count != 3 || form.Files.Count != 0 ||
            form["__RequestVerificationToken"].Count != 1 ||
            form["resourceType"].Count != 1 || form["resourceValue"].Count != 1)
            return Results.BadRequest();

        var status = await coordinator.ExcludeAsync(identity!, form["resourceType"].ToString(),
            form["resourceValue"].ToString(), context.RequestAborted);
        if (status == MutationStatus.Success) return Results.Redirect("/operator?result=excluded");
        var code = status switch
        {
            MutationStatus.InvalidRequest => StatusCodes.Status400BadRequest,
            MutationStatus.NotAuthorized => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status503ServiceUnavailable
        };
        return Results.Content(OptOutPage.RenderMutationStatus(status), "text/html; charset=utf-8", statusCode: code);
    }
}
