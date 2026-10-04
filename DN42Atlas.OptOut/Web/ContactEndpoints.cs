using System.Text.Encodings.Web;
using DN42Atlas.OptOut.ManualRequests;
using JoyfulReaperLib.Ntfy;
using Microsoft.AspNetCore.Antiforgery;

namespace DN42Atlas.OptOut.Web;

public static class ContactEndpoints
{
    private const int BodyLimit = 32768;
    private static readonly string[] Fields = ["Resource", "Contact", "RequestType", "Message", "Website", "__RequestVerificationToken"];
    public static void Map(WebApplication app)
    {
        app.MapGet("/contact", Get);
        app.MapPost("/contact", PostAsync);
    }

    public static IResult Get(HttpContext context, IAntiforgery antiforgery)
    {
        context.Response.Headers.CacheControl = "no-store";
        var token = HtmlEncoder.Default.Encode(antiforgery.GetAndStoreTokens(context).RequestToken!);
        var notice = context.Request.Query["result"] == "recorded"
            ? "<p>Your request has been recorded for manual review. This is not an immediate exclusion.</p>" : "";
        var types = string.Join("", Enum.GetValues<ManualRequestType>().Select(type =>
            $"<option value=\"{type}\">{RequestTypeLabel(type)}</option>"));
        return Results.Content($$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>DN42Atlas manual request</title>
            <style>:root { color-scheme: light dark; font-family: system-ui, sans-serif; } main { max-width: 48rem; margin: 3rem auto; padding: 0 1rem; line-height: 1.55; } textarea { max-width: 100%; }</style></head>
            <body><main><h1>DN42Atlas manual request</h1>{{notice}}
            <p><a href="/operator">Authenticated self-service</a> is preferred when available. This form is for manual review of urgent, broad or wildcard requests, ownership/authentication problems, corrections, and other concerns. Submission does not create or revoke an exclusion.</p>
            <form method="post" action="/contact">
            <input type="hidden" name="__RequestVerificationToken" value="{{token}}">
            <p><label>Resource <input name="Resource" required maxlength="255"></label><br>Examples: example.dn42, 172.20.1.0/24, fd00::/48, *.example.dn42</p>
            <p><label>Contact <input name="Contact" required maxlength="255"></label><br>An email address or another way for the operator to contact you.</p>
            <p><label>Request type <select name="RequestType" required>{{types}}</select></label></p>
            <p><label>Message <textarea name="Message" required maxlength="2000" rows="8" cols="50"></textarea></label></p>
            <div hidden><label>Leave empty <input name="Website" tabindex="-1" autocomplete="off"></label></div>
            <button type="submit">Submit for manual review</button></form><p><a href="/operator">Self-service</a> · <a href="/opt-out.html">Opt-out information</a></p>
            </main></body></html>
            """, "text/html; charset=utf-8");
    }

    private static string RequestTypeLabel(ManualRequestType type) => type switch
    {
        ManualRequestType.OptOut => "Opt-out request",
        ManualRequestType.BroaderOrWildcard => "Broader / wildcard exclusion",
        ManualRequestType.OwnershipOrAuthentication => "Ownership or authentication problem",
        ManualRequestType.Correction => "Correction",
        ManualRequestType.Other => "Other",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    public static async Task<IResult> PostAsync(HttpContext context, IAntiforgery antiforgery, ManualRequestStore store,
        ContactRateLimiter limiter, INtfyPublisher publisher, ILoggerFactory logs)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!limiter.TryAcquire()) return Results.StatusCode(429);
        if (context.Request.ContentLength > BodyLimit ||
            !System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var media) ||
            !string.Equals(media.MediaType, "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) ||
            media.Parameters.Any(p => !p.Name.Equals("charset", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(p.Value?.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))) return Results.BadRequest();
        var originalBody = context.Request.Body;
        try
        {
            // Bound even chunked requests before the antiforgery service parses the form.
            using var body = new MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                if (body.Length + read > BodyLimit) return Results.BadRequest();
                body.Write(buffer, 0, read);
            }
            body.Position = 0;
            context.Request.Body = body;
            await antiforgery.ValidateRequestAsync(context);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (form.Files.Count != 0 || form.Count != Fields.Length ||
                Fields.Any(field => form[field].Count != 1) || form.Keys.Except(Fields, StringComparer.Ordinal).Any())
                return Results.BadRequest();
            if (!string.IsNullOrEmpty(form["Website"].ToString())) return Results.Redirect("/contact?result=recorded");
            if (!ContactValidation.TryParse(form, out var input)) return Results.BadRequest();
            var record = await store.AddAsync(input!, context.RequestAborted);
            return new RecordedRequestResult(record, publisher, logs.CreateLogger("ManualRequestNotification"));
        }
        catch (Exception ex) when (ex is AntiforgeryValidationException or InvalidDataException or BadHttpRequestException)
        { return Results.BadRequest(); }
        finally { context.Request.Body = originalBody; }
    }
}

public sealed class RecordedRequestResult(ManualRequest record, INtfyPublisher publisher, ILogger logger) : IResult
{
    public async Task ExecuteAsync(HttpContext context)
    {
        try
        {
            await Results.Redirect("/contact?result=recorded").ExecuteAsync(context);
            await context.Response.CompleteAsync();
        }
        finally
        {
            try
            {
                await publisher.PublishAsync(new NtfyMessage
                {
                    Title = "DN42Atlas manual request", Priority = NtfyPriority.High, Tags = ["dn42", "atlas"],
                    Message = $"New {record.RequestType} request\nResource: {record.Resource}\nContact: {record.Contact}\nRequest ID: {record.Id}"
                }, CancellationToken.None);
            }
            catch (Exception ex) { logger.LogWarning("Manual request notification failed ({FailureType}); the request remains recorded.", ex.GetType().Name); }
        }
    }
}
