using DN42Atlas.OptOut.Auth;
using DN42Atlas.OptOut.Exclusions;
using DN42Atlas.OptOut.Registry;
using DN42Atlas.OptOut.Web;
using DN42Atlas.OptOut.ManualRequests;
using JoyfulReaperLib.Ntfy;
using DN42Atlas.Policy;
using DN42Atlas.Registry;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.AspNetCore.Http.Features;

const string cookieScheme = "DN42Atlas.Cookie";
const string oidcScheme = "Auth42";

var builder = WebApplication.CreateBuilder(args);

var maintenanceCommand = args.FirstOrDefault();

if (maintenanceCommand is "manual-requests-init" or "manual-requests" or "manual-request")
{
    try
    {
        var path = ManualRequestStore.ConfiguredPath(builder.Configuration);
        if (maintenanceCommand == "manual-requests-init")
        {
            if (args.Length != 1) { Console.Error.WriteLine("Usage: manual-requests-init"); Environment.ExitCode = 2; }
            else { await ManualRequestStore.InitializeAsync(path); Console.WriteLine("Private manual request database initialized."); }
        }
        else Environment.ExitCode = await ManualRequestCommands.ExecuteAsync(args, new(path), Console.Out);
    }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
    return;
}

if (maintenanceCommand is "exclusions-init" or "exclusions-materialize" or "exclusions-reconcile")
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
        else if (maintenanceCommand == "exclusions-materialize")
        {
            await ExclusionMaintenance.MaterializeAsync(
                databasePath,
                runtimeBundlePath);
            Console.WriteLine(
                "Runtime exclusion policy materialized from active records.");
        }
        else
        {
            var paths = MutationPaths.FromConfiguration(builder.Configuration);
            using var logs = LoggerFactory.Create(options => options.AddConsole());
            var result = await new ExclusionReconciler(new ExclusionStore(databasePath), paths,
                logs.CreateLogger<ExclusionReconciler>()).ReconcileAsync();
            if (result != ReconciliationStatus.Success)
                throw new InvalidOperationException("Exclusion reconciliation failed. The public listing was withdrawn where possible.");
            Console.WriteLine("Runtime policy and public Atlas reconciled from active database records.");
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
if (!Path.IsPathFullyQualified(registryPath))
    throw new InvalidOperationException("DN42ATLAS_REGISTRY_PATH must be an explicit absolute path.");
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
var mutationPaths = MutationPaths.FromConfiguration(builder.Configuration);
exclusionStore.ValidateExisting();
var manualRequestStore = new ManualRequestStore(ManualRequestStore.ConfiguredPath(builder.Configuration));
manualRequestStore.ValidateExisting();
_ = ExclusionPolicy.Load(mutationPaths.Hosts, mutationPaths.Prefixes, runtimeExclusionsPath);

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
builder.Services.AddSingleton(mutationPaths);
builder.Services.AddSingleton(service => new RegistryResourceAuthorizer(
    service.GetRequiredService<RegistryDomainCatalog>(), service.GetRequiredService<RegistryAllocationCatalog>(),
    service.GetRequiredService<RegistrySnapshotService>().GetSnapshot));
builder.Services.AddSingleton<ExclusionReconciler>();
builder.Services.AddSingleton<ExclusionMutationCoordinator>();
builder.Services.AddSingleton<ConfirmationTokens>();
builder.Services.AddSingleton(manualRequestStore);
builder.Services.AddSingleton<ContactRateLimiter>();
builder.Services.AddJoyfulReaperNtfy(builder.Configuration);
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "__Host-DN42Atlas.Antiforgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.FormFieldName = "__RequestVerificationToken";
});
builder.Services.Configure<FormOptions>(options =>
{
    options.ValueCountLimit = 6;
    options.KeyLengthLimit = 64;
    options.ValueLengthLimit = 4096;
    options.BufferBodyLengthLimit = 32768;
});

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
app.UseExceptionHandler(errors => errors.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
    context.Response.Headers.CacheControl = "no-store";
    await context.Response.WriteAsync("Operator service unavailable. Please contact the operator.");
}));

if (!app.Environment.IsDevelopment())
    app.UseHsts();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

OperatorEndpoints.Map(app);
ContactEndpoints.Map(app);

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
