using DN42Atlas.Publishing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class ArtifactPublisherTests
{
    [TestMethod]
    public async Task PublishCreatesAndReplacesStableFilesWithoutChangingHistory()
    {
        using var files = new TestFiles();
        var json = files.Write("web-probe-20261003-125026.json", "{\"Results\":[]}");
        var html = files.Write("web-probe-20261003-125026.html", "<!doctype html><title>Atlas</title>");
        var published = Path.Combine(files.DirectoryPath, "published");
        await ArtifactPublisher.PublishAsync(json, published);
        Assert.AreEqual(await File.ReadAllTextAsync(json), await File.ReadAllTextAsync(Path.Combine(published, "latest.json")));
        Assert.AreEqual(await File.ReadAllTextAsync(html), await File.ReadAllTextAsync(Path.Combine(published, "index.html")));

        var nextJson = files.Write("web-probe-20261003-125027.json", "{\"Results\":[],\"GeneratedAt\":\"new\"}");
        var nextHtml = files.Write("web-probe-20261003-125027.html", "<!doctype html><title>New Atlas</title>");
        await ArtifactPublisher.PublishAsync(nextJson, published);
        Assert.AreEqual(await File.ReadAllTextAsync(nextJson), await File.ReadAllTextAsync(Path.Combine(published, "latest.json")));
        Assert.AreEqual(await File.ReadAllTextAsync(nextHtml), await File.ReadAllTextAsync(Path.Combine(published, "index.html")));
        Assert.AreEqual("{\"Results\":[]}", await File.ReadAllTextAsync(json));
        Assert.AreEqual("<!doctype html><title>Atlas</title>", await File.ReadAllTextAsync(html));
        Assert.HasCount(3, Directory.GetFiles(published));
        var optOut = await File.ReadAllTextAsync(Path.Combine(published, "opt-out.html"));
        Assert.Contains("Opt out of DN42Atlas", optOut);
        Assert.Contains("https://github.com/JoyfulReaper/DN42Atlas/issues", optOut);
        Assert.Contains("href=\"index.html\"", optOut);
    }

    [TestMethod]
    public async Task RepeatedPublicationPreservesManuallyMaintainedPagesAndAssets()
    {
        using var files = new TestFiles();
        var json = files.Write("scan.json", "{\"Results\":[]}");
        files.Write("scan.html", "new report");
        var published = Path.Combine(files.DirectoryPath, "published");
        await ArtifactPublisher.PublishAsync(json, published);
        var optOut = Path.Combine(published, "opt-out.html");
        byte[] customPage = [0xEF, 0xBB, 0xBF, 65, 66, 67, 13, 10];
        await File.WriteAllBytesAsync(optOut, customPage);
        var about = Path.Combine(published, "about.html");
        var css = Path.Combine(published, "site.css");
        await File.WriteAllTextAsync(about, "manual about page");
        await File.WriteAllTextAsync(css, "manual styles");
        for (var i = 0; i < 2; i++)
        {
            files.Write("scan.html", $"report {i}");
            await ArtifactPublisher.PublishAsync(json, published);
            CollectionAssert.AreEqual(customPage, await File.ReadAllBytesAsync(optOut));
            Assert.AreEqual("manual about page", await File.ReadAllTextAsync(about));
            Assert.AreEqual("manual styles", await File.ReadAllTextAsync(css));
            Assert.AreEqual($"report {i}", await File.ReadAllTextAsync(Path.Combine(published, "index.html")));
        }
        Assert.HasCount(5, Directory.GetFiles(published));
    }

    [TestMethod]
    public async Task MissingGeneratedHtmlLeavesBothPreviousFilesIntactAndCleansStaging()
    {
        using var files = new TestFiles();
        var published = Path.Combine(files.DirectoryPath, "published");
        Directory.CreateDirectory(published);
        var latest = Path.Combine(published, "latest.json");
        var index = Path.Combine(published, "index.html");
        await File.WriteAllTextAsync(latest, "old JSON");
        await File.WriteAllTextAsync(index, "old HTML");
        var json = files.Write("incomplete.json", "new JSON");
        await Assert.ThrowsAsync<FileNotFoundException>(() => ArtifactPublisher.PublishAsync(json, published));
        Assert.AreEqual("old JSON", await File.ReadAllTextAsync(latest));
        Assert.AreEqual("old HTML", await File.ReadAllTextAsync(index));
        Assert.HasCount(2, Directory.GetFiles(published));
    }

    [TestMethod]
    public async Task CancelledStagingLeavesPreviousPublicationIntact()
    {
        using var files = new TestFiles();
        var published = Path.Combine(files.DirectoryPath, "published");
        Directory.CreateDirectory(published);
        var latest = Path.Combine(published, "latest.json");
        var index = Path.Combine(published, "index.html");
        await File.WriteAllTextAsync(latest, "old JSON");
        await File.WriteAllTextAsync(index, "old HTML");
        var json = files.Write("new.json", "new JSON");
        files.Write("new.html", "new HTML");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ArtifactPublisher.PublishAsync(json, published, cancellation.Token));
        Assert.AreEqual("old JSON", await File.ReadAllTextAsync(latest));
        Assert.AreEqual("old HTML", await File.ReadAllTextAsync(index));
        Assert.HasCount(2, Directory.GetFiles(published));
    }
}
