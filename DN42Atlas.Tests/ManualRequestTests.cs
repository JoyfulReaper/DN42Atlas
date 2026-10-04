using System.Text;
using DN42Atlas.OptOut.ManualRequests;
using DN42Atlas.OptOut.Web;
using JoyfulReaperLib.Ntfy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Configuration;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.DataProtection;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class ManualRequestTests
{
    [TestMethod]
    [DataRow("OptOut")]
    [DataRow("BroaderOrWildcard")]
    [DataRow("OwnershipOrAuthentication")]
    [DataRow("Correction")]
    [DataRow("Other")]
    public async Task ValidSubmissionPersistsBeforeResponseAndNotifiesOnceWithoutExclusionMutation(string type)
    {
        using var f = await MutationFixture.CreateAsync();
        var path = Path.Combine(f.Files.DirectoryPath, "requests.db");
        await ManualRequestStore.InitializeAsync(path);
        var store = new ManualRequestStore(path);
        var files = Directory.GetFiles(f.Files.DirectoryPath, "*", SearchOption.AllDirectories).Where(p => p != path).ToArray();
        var original = files.Select(File.ReadAllBytes).ToArray();
        var post = Post(f, type);
        var fake = new FakePublisher { BeforePublish = () =>
        {
            Assert.AreEqual(302, post.Response.StatusCode);
            Assert.AreEqual("/contact?result=recorded", post.Response.Headers.Location.ToString());
            Assert.HasCount(1, new ManualRequestStore(path).ReadAsync().GetAwaiter().GetResult());
        } };
        var result = await ContactEndpoints.PostAsync(post, f.Antiforgery, store, new(), fake, f.Logs);
        Assert.IsInstanceOfType<RecordedRequestResult>(result);
        var record = (await new ManualRequestStore(path).ReadAsync()).Single();
        Assert.AreEqual("*.example.dn42", record.Resource);
        Assert.AreEqual("DN42 forum handle: operator", record.Contact);
        Assert.AreEqual("PRIVATE full message\nsecond line <script>alert(1)</script>", record.Message);
        Assert.AreEqual(type, record.RequestType.ToString());
        Assert.AreEqual("Pending", record.Status);
        Assert.IsNull(record.ReviewedUtc);
        Assert.IsEmpty(fake.Messages);
        await result.ExecuteAsync(post);
        Assert.HasCount(1, fake.Messages);
        var notification = fake.Messages.Single();
        Assert.AreEqual("DN42Atlas manual request", notification.Title);
        Assert.AreEqual(NtfyPriority.High, notification.Priority);
        CollectionAssert.AreEqual(new[] { "dn42", "atlas" }, notification.Tags.ToArray());
        Assert.Contains(type, notification.Message);
        Assert.Contains(record.Resource, notification.Message);
        Assert.Contains(record.Contact, notification.Message);
        Assert.Contains($"Request ID: {record.Id}", notification.Message);
        Assert.DoesNotContain("PRIVATE", notification.Message);
        Assert.DoesNotContain("<script>", notification.Message);
        Assert.IsNull(notification.ClickUrl);
        for (var i = 0; i < files.Length; i++) CollectionAssert.AreEqual(original[i], File.ReadAllBytes(files[i]), files[i]);
        foreach (var name in new[] { "latest.json", "index.html" })
            Assert.DoesNotContain("PRIVATE", File.ReadAllText(Path.Combine(f.Paths.Published, name)));
        var page = (ContentHttpResult)ContactEndpoints.Get(f.Context(), f.Antiforgery);
        Assert.DoesNotContain(record.Message, page.ResponseContent!);
        Assert.DoesNotContain("<script>", page.ResponseContent!);
    }

    [TestMethod]
    public async Task PersistenceFailureDoesNotReturnSuccessOrNotify()
    {
        using var f = await MutationFixture.CreateAsync();
        var store = new ManualRequestStore(Path.Combine(f.Files.DirectoryPath, "missing-requests.db"));
        var context = Post(f);
        var originalBody = context.Request.Body;
        var fake = new FakePublisher();
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            ContactEndpoints.PostAsync(context, f.Antiforgery, store, new(), fake, f.Logs));
        Assert.IsEmpty(fake.Messages);
        Assert.IsFalse(context.Response.HasStarted);
        Assert.AreSame(originalBody, context.Request.Body);
    }

    [TestMethod]
    public async Task NotificationFailureStillReturnsSuccessAndKeepsDurableSingleRequest()
    {
        using var f = await MutationFixture.CreateAsync();
        var path = Path.Combine(f.Files.DirectoryPath, "requests.db");
        await ManualRequestStore.InitializeAsync(path);
        var store = new ManualRequestStore(path);
        var context = Post(f);
        var fake = new FakePublisher { Fail = true };
        var result = await ContactEndpoints.PostAsync(context, f.Antiforgery, store, new(), fake, f.Logs);
        await result.ExecuteAsync(context);
        Assert.AreEqual(302, context.Response.StatusCode);
        Assert.HasCount(1, await new ManualRequestStore(path).ReadAsync());
        Assert.HasCount(1, fake.Messages);
        var get = f.Context();
        get.Request.QueryString = new("?result=recorded");
        var html = ((ContentHttpResult)ContactEndpoints.Get(get, f.Antiforgery)).ResponseContent!;
        Assert.Contains("Your request has been recorded for manual review.", html);
        Assert.DoesNotContain("PRIVATE", html);
        Assert.Contains("no-store", get.Response.Headers.CacheControl.ToString());
    }

    [TestMethod]
    [DataRow("Resource", "")]
    [DataRow("Contact", "")]
    [DataRow("Message", "")]
    [DataRow("RequestType", "")]
    [DataRow("RequestType", "invalid")]
    [DataRow("RequestType", "0")]
    [DataRow("RequestType", "optout")]
    [DataRow("Resource", "bad\nresource")]
    [DataRow("Contact", "bad\tcontact")]
    [DataRow("Message", "bad\u001bmessage")]
    [DataRow("Resource", "oversized")]
    [DataRow("Contact", "oversized")]
    [DataRow("Message", "oversized")]
    [DataRow("Unexpected", "field")]
    [DataRow("__RequestVerificationToken", "invalid")]
    public async Task InvalidFieldsRejectWithoutStorageOrNotification(string field, string value)
    {
        using var f = await MutationFixture.CreateAsync();
        var path = Path.Combine(f.Files.DirectoryPath, "requests.db");
        await ManualRequestStore.InitializeAsync(path);
        var store = new ManualRequestStore(path);
        var context = Post(f);
        var form = context.Request.Form.ToDictionary(p => p.Key, p => p.Value);
        if (value == "oversized") value = new string('x', field == "Message" ? 2001 : 256);
        form[field] = value;
        context.Request.Form = new FormCollection(form);
        var fake = new FakePublisher();
        var result = await ContactEndpoints.PostAsync(context, f.Antiforgery, store, new(), fake, f.Logs);
        Assert.AreEqual(400, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.IsEmpty(await store.ReadAsync());
        Assert.IsEmpty(fake.Messages);
    }

    [TestMethod]
    [DataRow("missing-field")]
    [DataRow("missing-antiforgery")]
    [DataRow("wrong-cookie")]
    [DataRow("duplicate")]
    [DataRow("json")]
    [DataRow("multipart")]
    [DataRow("wrong-charset")]
    [DataRow("body-too-large")]
    [DataRow("chunked-too-large")]
    public async Task MalformedRequestRejectsBeforePersistence(string failure)
    {
        using var f = await MutationFixture.CreateAsync();
        var path = Path.Combine(f.Files.DirectoryPath, "requests.db");
        await ManualRequestStore.InitializeAsync(path);
        var store = new ManualRequestStore(path);
        var context = Post(f);
        var form = context.Request.Form.ToDictionary(p => p.Key, p => p.Value);
        switch (failure)
        {
            case "missing-field": form.Remove("Contact"); break;
            case "missing-antiforgery": form.Remove("__RequestVerificationToken"); break;
            case "wrong-cookie": context.Request.Headers.Cookie = ""; break;
            case "duplicate": form["Resource"] = new StringValues(["one", "two"]); break;
            case "json": context.Request.ContentType = "application/json"; break;
            case "multipart": context.Request.ContentType = "multipart/form-data; boundary=test"; break;
            case "wrong-charset": context.Request.ContentType = "application/x-www-form-urlencoded; charset=latin1"; break;
            case "body-too-large": context.Request.ContentLength = 32769; break;
            case "chunked-too-large": context.Request.Body = new MemoryStream(new byte[32769]); break;
        }
        context.Request.Form = new FormCollection(form);
        Assert.AreEqual(400, ((IStatusCodeHttpResult)await ContactEndpoints.PostAsync(context, f.Antiforgery, store, new(), new FakePublisher(), f.Logs)).StatusCode);
        Assert.IsEmpty(await store.ReadAsync());
    }

    [TestMethod]
    public async Task HoneypotLooksSuccessfulWithoutRecordingOrNotifying()
    {
        using var f = await MutationFixture.CreateAsync();
        var path = Path.Combine(f.Files.DirectoryPath, "requests.db");
        await ManualRequestStore.InitializeAsync(path);
        var store = new ManualRequestStore(path);
        var context = Post(f);
        var form = context.Request.Form.ToDictionary(p => p.Key, p => p.Value);
        form["Website"] = "bot";
        context.Request.Form = new FormCollection(form);
        var fake = new FakePublisher();
        var result = (RedirectHttpResult)await ContactEndpoints.PostAsync(context, f.Antiforgery, store, new(), fake, f.Logs);
        Assert.AreEqual("/contact?result=recorded", result.Url);
        Assert.IsEmpty(await store.ReadAsync());
        Assert.IsEmpty(fake.Messages);
    }

    [TestMethod]
    public async Task GlobalRateLimitCannotBeBypassedWithForwardedHeaders()
    {
        using var f = await MutationFixture.CreateAsync();
        var path = Path.Combine(f.Files.DirectoryPath, "requests.db");
        await ManualRequestStore.InitializeAsync(path);
        var store = new ManualRequestStore(path);
        var clock = new TestClock();
        var limiter = new ContactRateLimiter(clock, 1);
        var first = Post(f);
        Assert.IsInstanceOfType<RecordedRequestResult>(await ContactEndpoints.PostAsync(first, f.Antiforgery, store, limiter, new FakePublisher(), f.Logs));
        var second = Post(f);
        second.Request.Headers["X-Forwarded-For"] = "192.0.2.123";
        Assert.AreEqual(429, ((IStatusCodeHttpResult)await ContactEndpoints.PostAsync(second, f.Antiforgery, store, limiter, new FakePublisher(), f.Logs)).StatusCode);
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.IsTrue(limiter.TryAcquire());
        Assert.HasCount(1, await store.ReadAsync());
    }

    [TestMethod]
    public async Task PrivateStoreAndCliListDetailPreserveReviewData()
    {
        using var files = new TestFiles();
        var path = Path.Combine(files.DirectoryPath, "requests.db");
        await ManualRequestStore.InitializeAsync(path);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ManualRequestStore.InitializeAsync(path));
        var store = new ManualRequestStore(path);
        store.ValidateExisting();
        var record = await store.AddAsync(new("example.dn42", "human handle", ManualRequestType.Other, "PRIVATE full message"));
        var writer = new StringWriter();
        Assert.AreEqual(0, await ManualRequestCommands.ExecuteAsync(["manual-requests"], store, writer));
        Assert.Contains("human handle", writer.ToString());
        Assert.DoesNotContain("PRIVATE", writer.ToString());
        writer.GetStringBuilder().Clear();
        Assert.AreEqual(0, await ManualRequestCommands.ExecuteAsync(["manual-request", record.Id.ToString()], store, writer));
        Assert.Contains("PRIVATE full message", writer.ToString());
        Assert.AreEqual(1, await ManualRequestCommands.ExecuteAsync(["manual-request", "999"], store, writer));
        Assert.AreEqual(2, await ManualRequestCommands.ExecuteAsync(["manual-request", "-1"], store, writer));
        Assert.IsNull((await store.ReadAsync(record.Id)).Single().ReviewedUtc);
        if (!OperatingSystem.IsWindows()) Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.ThrowsExactly<FileNotFoundException>(() => new ManualRequestStore(Path.Combine(files.DirectoryPath, "missing.db")).ValidateExisting());
    }

    [TestMethod]
    public void RequestDatabaseMustBeAbsolutePrivateAndDistinct()
    {
        using var files = new TestFiles();
        var published = Path.Combine(files.DirectoryPath, "published");
        var values = new Dictionary<string, string?> { ["DN42ATLAS_PUBLISHED_PATH"] = published,
            ["DN42ATLAS_MANUAL_REQUEST_DB_PATH"] = "relative.db" };
        string Read() => ManualRequestStore.ConfiguredPath(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        Assert.ThrowsExactly<InvalidOperationException>(() => Read());
        values["DN42ATLAS_MANUAL_REQUEST_DB_PATH"] = Path.Combine(published, "requests.db");
        Assert.ThrowsExactly<InvalidDataException>(() => Read());
        values["DN42ATLAS_MANUAL_REQUEST_DB_PATH"] = files.HostsPath;
        values["DN42ATLAS_EXCLUDED_HOSTS_PATH"] = files.HostsPath;
        Assert.ThrowsExactly<InvalidOperationException>(() => Read());
    }

    [TestMethod]
    public async Task AnonymousRealHttpFormUsesAntiforgeryPersistsAndReturnsPrg()
    {
        using var files = new TestFiles();
        var path = Path.Combine(files.DirectoryPath, "requests.db");
        await ManualRequestStore.InitializeAsync(path);
        var store = new ManualRequestStore(path);
        var fake = new FakePublisher();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<ContactRateLimiter>();
        builder.Services.AddSingleton<INtfyPublisher>(fake);
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "__Host-DN42Atlas.Antiforgery";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });
        builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
        { options.ValueCountLimit = 6; options.ValueLengthLimit = 4096; options.BufferBodyLengthLimit = 32768; });
        await using var app = builder.Build();
        app.Use((context, next) => { context.Request.Scheme = "https"; return next(context); });
        ContactEndpoints.Map(app);
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new(address) };
            using var page = await client.GetAsync("/contact");
            var html = await page.Content.ReadAsStringAsync();
            var token = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"");
            Assert.IsTrue(token.Success);
            Assert.Contains("no-store", page.Headers.CacheControl!.ToString());
            client.DefaultRequestHeaders.Add("Cookie", page.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
            var form = new Dictionary<string, string> { ["Resource"] = "*.example.dn42", ["Contact"] = "forum operator",
                ["RequestType"] = "Other", ["Message"] = "PRIVATE body", ["Website"] = "",
                ["__RequestVerificationToken"] = System.Net.WebUtility.HtmlDecode(token.Groups[1].Value) };
            using var response = await client.PostAsync("/contact", new FormUrlEncodedContent(form));
            Assert.AreEqual(System.Net.HttpStatusCode.Redirect, response.StatusCode);
            Assert.AreEqual("/contact?result=recorded", response.Headers.Location!.OriginalString);
            await fake.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.HasCount(1, fake.Messages);
            Assert.HasCount(1, await store.ReadAsync());
            using var bad = await client.PostAsync("/contact", new StringContent("{\"Message\":\"bad\"}", Encoding.UTF8, "application/json"));
            Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.HasCount(1, await store.ReadAsync());
        }
        finally { await app.StopAsync(); }
    }

    [TestMethod]
    public void ExactLimitsAreAcceptedAndAllUserTextIsTrimmed()
    {
        var form = new FormCollection(new Dictionary<string, StringValues>
        { ["Resource"] = " " + new string('r', 255) + " ", ["Contact"] = new string('c', 255),
            ["Message"] = new string('m', 2000), ["RequestType"] = " Other " });
        Assert.IsTrue(ContactValidation.TryParse(form, out var input));
        Assert.AreEqual(255, input!.Resource.Length);
        Assert.AreEqual(2000, input.Message.Length);
    }

    private static DefaultHttpContext Post(MutationFixture f, string type = "Other")
    {
        var get = f.Context();
        get.User = new ClaimsPrincipal(new ClaimsIdentity());
        var token = f.Antiforgery.GetAndStoreTokens(get).RequestToken!;
        var context = f.Context();
        context.User = get.User;
        context.Request.Method = "POST";
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = get.Response.Headers.SetCookie.ToString().Split(';')[0];
        context.Request.Path = "/contact";
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["Resource"] = " *.example.dn42 ", ["Contact"] = " DN42 forum handle: operator ",
            ["RequestType"] = type, ["Message"] = " PRIVATE full message\nsecond line <script>alert(1)</script> ",
            ["Website"] = "", ["__RequestVerificationToken"] = token
        });
        return context;
    }

    private sealed class FakePublisher : INtfyPublisher
    {
        public List<NtfyMessage> Messages { get; } = [];
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Fail { get; init; }
        public Action? BeforePublish { get; init; }
        public Task PublishAsync(NtfyMessage notification, CancellationToken cancellationToken = default)
        {
            BeforePublish?.Invoke(); Messages.Add(notification); Completed.TrySetResult();
            if (Fail) throw new IOException("secret must not be logged");
            return Task.CompletedTask;
        }
    }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
