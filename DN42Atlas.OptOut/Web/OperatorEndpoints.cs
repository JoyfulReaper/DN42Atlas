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
        var selfService = new HashSet<ExactResource>();
        var manualExcluded = new HashSet<ExactResource>();
        var available = true;
        try
        {
            maintainedDomains = domains.FindDomains(identity!.ActiveMaintainer);
            maintainedAllocations = allocations.FindAllocations(identity.ActiveMaintainer);
            var active = await store.GetActiveAsync(context.RequestAborted);
            foreach (var record in active) selfService.Add(new(record.ResourceType, record.ResourceValue));
            _ = ExclusionPolicy.Load(paths.Hosts, paths.Prefixes, paths.Runtime);
            var policy = ExclusionPolicy.Load(paths.Hosts, paths.Prefixes);
            foreach (var domain in maintainedDomains.Where(d => policy.IsHostExcluded(d)))
                manualExcluded.Add(new(ExclusionResourceType.Domain, domain));
            foreach (var prefix in maintainedAllocations.Ipv4Prefixes.Where(policy.IsPrefixExcluded))
                manualExcluded.Add(new(ExclusionResourceType.IPv4Prefix, prefix));
            foreach (var prefix in maintainedAllocations.Ipv6Prefixes.Where(policy.IsPrefixExcluded))
                manualExcluded.Add(new(ExclusionResourceType.IPv6Prefix, prefix));
        }
        catch (Exception ex)
        {
            logs.CreateLogger("OperatorDashboard").LogWarning(ex, "Operator dashboard data unavailable.");
            available = false;
        }
        var token = antiforgery.GetAndStoreTokens(context).RequestToken!;
        return Results.Content(OptOutPage.RenderSignedIn(identity!, maintainedDomains,
            maintainedAllocations.Ipv4Prefixes, maintainedAllocations.Ipv6Prefixes, snapshot, available,
            selfService, token, context.Request.Query["result"].ToString(), manualExcluded), "text/html; charset=utf-8");
    }

    public static async Task<IResult> PostAsync(HttpContext context, IAntiforgery antiforgery,
        ExclusionMutationCoordinator coordinator)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (context.User.Identity?.IsAuthenticated != true ||
            !Auth42Identity.TryFromPrincipal(context.User, out var identity)) return Results.Unauthorized();
        if (context.Request.ContentLength > 8192 || context.Request.ContentType == null ||
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

        var intent = form["intent"].ToString();
        var confirmation = intent is "confirm-exclude" or "confirm-include";
        if (intent is not ("confirm-exclude" or "confirm-include" or "exclude" or "include") ||
            form.Count != (confirmation ? 4 : 5) || form.Files.Count != 0 || form["intent"].Count != 1 ||
            form["__RequestVerificationToken"].Count != 1 ||
            form["resourceType"].Count != 1 || form["resourceValue"].Count != 1 ||
            (!confirmation && form["confirmationToken"].Count != 1))
            return Results.BadRequest();

        var tokens = context.RequestServices.GetRequiredService<ConfirmationTokens>();
        var operation = confirmation ? intent[8..] : intent;
        MutationStatus status;
        if (confirmation)
        {
            var prepared = await coordinator.PrepareAsync(identity!, operation, form["resourceType"].ToString(),
                form["resourceValue"].ToString(), context.RequestAborted);
            status = prepared.Status;
            if (status == MutationStatus.Success)
            {
                var token = tokens.Create(operation, prepared.Resource!, identity!, prepared.RecordId);
                return Results.Content(OptOutPage.RenderConfirmation(operation, prepared.Resource!,
                    antiforgery.GetAndStoreTokens(context).RequestToken!, token), "text/html; charset=utf-8");
            }
        }
        else
        {
            ExactResource resource;
            try { resource = ExactResource.Parse(form["resourceType"].ToString(), form["resourceValue"].ToString()); }
            catch (Exception ex) when (ex is ArgumentException or FormatException) { return Results.BadRequest(); }
            if (!tokens.TryValidate(form["confirmationToken"].ToString(), operation, resource, identity!, out var recordId))
                return Results.BadRequest();
            status = operation == "exclude"
                ? await coordinator.ExcludeAsync(identity!, resource.Type.ToString(), resource.Value, context.RequestAborted, recordId)
                : await coordinator.IncludeAsync(identity!, resource.Type.ToString(), resource.Value, recordId!.Value, context.RequestAborted);
            if (status == MutationStatus.Success) return Results.Redirect("/operator?result=" + (operation == "exclude" ? "excluded" : "included"));
        }
        if (status == MutationStatus.Busy) context.Response.Headers.RetryAfter = "30";
        var code = status switch
        {
            MutationStatus.InvalidRequest => StatusCodes.Status400BadRequest,
            MutationStatus.NotAuthorized => StatusCodes.Status403Forbidden,
            MutationStatus.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status503ServiceUnavailable
        };
        return Results.Content(OptOutPage.RenderMutationStatus(status), "text/html; charset=utf-8", statusCode: code);
    }
}
