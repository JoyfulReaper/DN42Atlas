using System.Security.Claims;
using DN42Atlas.OptOut.Auth;
using DN42Atlas.OptOut.Exclusions;
using DN42Atlas.OptOut.Registry;
using DN42Atlas.OptOut.Web;
using DN42Atlas.Policy;
using DN42Atlas.Registry;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

const string cookieScheme = "DN42Atlas.Cookie";
const string oidcScheme = "Auth42";

var builder = WebApplication.CreateBuilder(args);

var maintenanceCommand = args.FirstOrDefault();

if (maintenanceCommand is "exclusions-init" or "exclusions-materialize")
{
    try
    {
        var databasePath = RequiredSetting(
            builder.Configuration,
            "DN42ATLAS_EXCLUSION_DB_PATH");
        var runtimeBundlePath = RequiredSetting(
            builder.Configuration,
            "DN42ATLAS_RUNTIME_EXCLUSIONS_PATH");

        if (maintenanceCommand == "exclusions-init")
        {
            await ExclusionMaintenance.InitializeAsync(
                databasePath,
                runtimeBundlePath);
            Console.WriteLine(
                "Exclusion database and empty runtime policy initialized.");
        }
        else
        {
            await ExclusionMaintenance.MaterializeAsync(
                databasePath,
                runtimeBundlePath);
            Console.WriteLine(
                "Runtime exclusion policy materialized from active records.");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        Environment.ExitCode = 1;
    }

    return;
}

var clientId = RequiredSetting(
    builder.Configuration,
    "DN42ATLAS_OIDC_CLIENT_ID");
var clientSecret = RequiredSetting(
    builder.Configuration,
    "DN42ATLAS_OIDC_CLIENT_SECRET");
var authority = RequiredSetting(
    builder.Configuration,
    "DN42ATLAS_OIDC_AUTHORITY");
var registryPath = RequiredSetting(
    builder.Configuration,
    "DN42ATLAS_REGISTRY_PATH");
var exclusionDatabasePath = RequiredSetting(
    builder.Configuration,
    "DN42ATLAS_EXCLUSION_DB_PATH");
var runtimeExclusionsPath = RequiredSetting(
    builder.Configuration,
    "DN42ATLAS_RUNTIME_EXCLUSIONS_PATH");
var registryMaximumAgeHours = PositiveIntSetting(
    builder.Configuration,
    "DN42ATLAS_REGISTRY_MAX_AGE_HOURS",
    defaultValue: 72);
var registryDomainPath = Path.Combine(registryPath, "data", "dns");
var registryIpv4Path = Path.Combine(registryPath, "data", "inetnum");
var registryIpv6Path = Path.Combine(registryPath, "data", "inet6num");
var exclusionStore = new ExclusionStore(exclusionDatabasePath);
exclusionStore.ValidateExisting();
_ = RuntimeExclusionBundle.Load(runtimeExclusionsPath);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto;
});

builder.Services.AddSingleton(
    new RegistryDomainCatalog(registryDomainPath));
builder.Services.AddSingleton(
    new RegistryAllocationCatalog(
        registryIpv4Path,
        registryIpv6Path));
builder.Services.AddSingleton(
    new RegistrySnapshotService(
        registryPath,
        TimeSpan.FromHours(registryMaximumAgeHours)));
builder.Services.AddSingleton(exclusionStore);

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = cookieScheme;
        options.DefaultChallengeScheme = oidcScheme;
    })
    .AddCookie(cookieScheme, options =>
    {
        options.Cookie.Name = "__Host-DN42Atlas.OptOut";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.SlidingExpiration = true;
    })
    .AddOpenIdConnect(oidcScheme, options =>
    {
        options.Authority = authority;
        options.ClientId = clientId;
        options.ClientSecret = clientSecret;
        options.CallbackPath = "/signin-oidc";
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.MapInboundClaims = false;

        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("dn42");

        options.ClaimActions.MapJsonKey(
            Auth42IdentityParser.Dn42ClaimType,
            Auth42IdentityParser.Dn42ClaimType,
            "JSON");

        options.CorrelationCookie.SameSite = SameSiteMode.None;
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
        options.NonceCookie.SameSite = SameSiteMode.None;
        options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;

        options.Events = new OpenIdConnectEvents
        {
            OnTicketReceived = context =>
            {
                if (context.Principal is null ||
                    !Auth42IdentityParser.TryParse(
                        context.Principal,
                        out var identity))
                {
                    context.Fail("Authenticated DN42 identity is invalid.");
                    return Task.CompletedTask;
                }

                context.Principal = identity!.ToPrincipal(cookieScheme);
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
    app.UseHsts();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/operator", (
    ClaimsPrincipal principal,
    RegistryDomainCatalog domains,
    RegistryAllocationCatalog allocations,
    RegistrySnapshotService snapshots) =>
{
    if (principal.Identity?.IsAuthenticated != true)
        return Results.Content(
            OptOutPage.RenderSignedOut(),
            "text/html; charset=utf-8");

    if (!Auth42Identity.TryFromPrincipal(principal, out var identity))
        return Results.Unauthorized();

    var snapshot = snapshots.GetSnapshot();
    IReadOnlyList<string> maintainedDomains;
    MaintainedAllocations maintainedAllocations;
    var ownershipAvailable = true;

    try
    {
        maintainedDomains =
            domains.FindDomains(identity!.ActiveMaintainer);
        maintainedAllocations =
            allocations.FindAllocations(identity.ActiveMaintainer);
    }
    catch (Exception ex) when (
        ex is IOException or UnauthorizedAccessException)
    {
        maintainedDomains = [];
        maintainedAllocations = new MaintainedAllocations([], []);
        ownershipAvailable = false;
    }

    return Results.Content(
        OptOutPage.RenderSignedIn(
            identity!,
            maintainedDomains,
            maintainedAllocations.Ipv4Prefixes,
            maintainedAllocations.Ipv6Prefixes,
            snapshot,
            ownershipAvailable),
        "text/html; charset=utf-8");
});

app.MapGet("/login", () =>
    Results.Challenge(
        new AuthenticationProperties { RedirectUri = "/operator" },
        [oidcScheme]));

app.MapGet("/logout", () =>
    Results.SignOut(
        new AuthenticationProperties { RedirectUri = "/operator" },
        [cookieScheme, oidcScheme]));

app.Run();

static string RequiredSetting(
    IConfiguration configuration,
    string name)
{
    var value = configuration[name];

    if (string.IsNullOrWhiteSpace(value))
        throw new InvalidOperationException(
            $"Required configuration value '{name}' is missing.");

    return value;
}

static int PositiveIntSetting(
    IConfiguration configuration,
    string name,
    int defaultValue)
{
    var value = configuration[name];

    if (string.IsNullOrWhiteSpace(value))
        return defaultValue;

    if (!int.TryParse(value, out var parsed) || parsed <= 0)
        throw new InvalidOperationException(
            $"Configuration value '{name}' must be a positive integer.");

    return parsed;
}

public partial class Program;
