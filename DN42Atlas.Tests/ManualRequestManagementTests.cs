using DN42Atlas.OptOut.ManualRequests;
using DN42Atlas.OptOut.Web;
using Microsoft.AspNetCore.Http.HttpResults;
using System.Diagnostics;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class ManualRequestManagementTests
{
    [TestMethod]
    [DataRow("OptOut", "Opt-out request")]
    [DataRow("BroaderOrWildcard", "Broader / wildcard exclusion")]
    [DataRow("OwnershipOrAuthentication", "Ownership or authentication problem")]
    [DataRow("Correction", "Correction")]
    [DataRow("Other", "Other")]
    public async Task ContactOptionsUseFriendlyLabelsAndCanonicalValues(string value, string label)
    {
        using var fixture = await MutationFixture.CreateAsync();
        var html = ((ContentHttpResult)ContactEndpoints.Get(fixture.Context(), fixture.Antiforgery)).ResponseContent!;
        Assert.Contains($"<option value=\"{value}\">{label}</option>", html);
    }

    [TestMethod]
    [DataRow(ManualRequestStatus.Reviewed)]
    [DataRow(ManualRequestStatus.Resolved)]
    [DataRow(ManualRequestStatus.Rejected)]
    public async Task StatusTransitionsSetPreserveAndClearReviewTimestampWithoutOtherMutation(ManualRequestStatus status)
    {
        using var fixture = await MutationFixture.CreateAsync();
        var path = Path.Combine(fixture.Files.DirectoryPath, "requests.db");
        await ManualRequestStore.InitializeAsync(path);
        var store = new ManualRequestStore(path);
        var record = await store.AddAsync(new("example.dn42", "human", ManualRequestType.Other, "PRIVATE message"));
        var originals = Snapshot(fixture, path);
        var output = new StringWriter();
        var before = DateTimeOffset.UtcNow;
        Assert.AreEqual(0, await ManualRequestCommands.ExecuteAsync(["manual-request-status", record.Id.ToString(), status.ToString()], store, output));
        var reviewed = (await store.ReadAsync(record.Id)).Single();
        Assert.AreEqual(status.ToString(), reviewed.Status);
        Assert.IsNotNull(reviewed.ReviewedUtc);
        Assert.IsTrue(reviewed.ReviewedUtc >= before && reviewed.ReviewedUtc <= DateTimeOffset.UtcNow);
        Assert.AreEqual(TimeSpan.Zero, reviewed.ReviewedUtc.Value.Offset);
        Assert.IsEmpty(await store.ReadAsync());
        Assert.IsTrue(await store.SetStatusAsync(record.Id, ManualRequestStatus.Resolved));
        Assert.AreEqual(reviewed.ReviewedUtc, (await store.ReadAsync(record.Id)).Single().ReviewedUtc);
        Assert.IsTrue(await store.SetStatusAsync(record.Id, ManualRequestStatus.Resolved));
        Assert.AreEqual(reviewed.ReviewedUtc, (await store.ReadAsync(record.Id)).Single().ReviewedUtc);
        Assert.IsTrue(await store.SetStatusAsync(record.Id, ManualRequestStatus.Pending));
        var pending = (await store.ReadAsync()).Single();
        Assert.IsNull(pending.ReviewedUtc);
        Assert.AreEqual(record.Message, pending.Message);
        Assert.AreEqual(record.CreatedUtc, pending.CreatedUtc);
        before = DateTimeOffset.UtcNow;
        Assert.IsTrue(await store.SetStatusAsync(record.Id, ManualRequestStatus.Rejected));
        Assert.IsTrue((await store.ReadAsync(record.Id)).Single().ReviewedUtc >= before);
        AssertUnchanged(originals);
    }

    [TestMethod]
    [DataRow("manual-request-status", "0", "Reviewed")]
    [DataRow("manual-request-status", "-1", "Reviewed")]
    [DataRow("manual-request-status", "9223372036854775808", "Reviewed")]
    [DataRow("manual-request-status", "1; DELETE FROM ManualRequests", "Reviewed")]
    [DataRow("manual-request-status", "1", "reviewed")]
    [DataRow("manual-request-status", "1", "0")]
    [DataRow("manual-request-status", "1", "Unknown")]
    [DataRow("manual-request-status", "1", "Reviewed'--")]
    [DataRow("manual-request-delete", "-1", null)]
    [DataRow("manual-request-delete", "not-an-id", null)]
    public async Task InvalidManagementArgumentsReturnUsageWithoutMutation(string command, string id, string? status)
    {
        using var files = new TestFiles();
        var path = Path.Combine(files.DirectoryPath, "requests.db");
        await ManualRequestStore.InitializeAsync(path);
        var store = new ManualRequestStore(path);
        await store.AddAsync(new("example.dn42", "human", ManualRequestType.Other, "PRIVATE message"));
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();
        var args = status == null ? new[] { command, id } : [command, id, status];
        Assert.AreEqual(2, await ManualRequestCommands.ExecuteAsync(args, store, output));
        Assert.Contains("Usage:", output.ToString());
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
        Assert.AreEqual(2, await ManualRequestCommands.ExecuteAsync([command], store, output));
    }

    [TestMethod]
    public async Task DeletionAndMissingIdsOnlyAffectSelectedManualRequest()
    {
        using var fixture = await MutationFixture.CreateAsync();
        var path = Path.Combine(fixture.Files.DirectoryPath, "requests.db");
        await ManualRequestStore.InitializeAsync(path);
        var store = new ManualRequestStore(path);
        var first = await store.AddAsync(new("one.dn42", "human", ManualRequestType.Other, "PRIVATE first"));
        var second = await store.AddAsync(new("two.dn42", "human", ManualRequestType.OptOut, "PRIVATE second"));
        var originals = Snapshot(fixture, path);
        var output = new StringWriter();
        Assert.AreEqual(1, await ManualRequestCommands.ExecuteAsync(["manual-request-status", "999", "Reviewed"], store, output));
        Assert.Contains("Request not found.", output.ToString());
        Assert.AreEqual(0, await ManualRequestCommands.ExecuteAsync(["manual-request-delete", first.Id.ToString()], store, output));
        Assert.AreEqual(1, await ManualRequestCommands.ExecuteAsync(["manual-request-delete", first.Id.ToString()], store, output));
        Assert.IsEmpty(await store.ReadAsync(first.Id));
        Assert.AreEqual(second, (await store.ReadAsync()).Single());
        output.GetStringBuilder().Clear();
        Assert.AreEqual(0, await ManualRequestCommands.ExecuteAsync(["manual-requests"], store, output));
        Assert.DoesNotContain("PRIVATE", output.ToString());
        Assert.AreEqual(0, await ManualRequestCommands.ExecuteAsync(["manual-request", second.Id.ToString()], store, output));
        Assert.Contains("PRIVATE second", output.ToString());
        AssertUnchanged(originals);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => store.DeleteAsync(0));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => store.SetStatusAsync(-1, ManualRequestStatus.Reviewed));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => store.SetStatusAsync(second.Id, (ManualRequestStatus)99));
    }

    [TestMethod]
    public async Task AdminHelperInvokesCliLoadsEnvironmentAndRequiresExactDeleteConfirmation()
    {
        var bash = OperatingSystem.IsWindows() ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe") : "/bin/bash";
        if (!File.Exists(bash)) { Assert.Inconclusive("Bash is unavailable on this host."); return; }
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "dn42atlas-request-admin"))) root = root.Parent;
        Assert.IsNotNull(root);
        using var files = new TestFiles();
        Directory.CreateDirectory(Path.Combine(files.DirectoryPath, "bin"));
        File.WriteAllText(Path.Combine(files.DirectoryPath, "deployment.env"), "ADMIN_TEST_SENTINEL=private-test-value\n");
        File.WriteAllText(Path.Combine(files.DirectoryPath, "bin", "dotnet"), """
            #!/usr/bin/env bash
            set -euo pipefail
            [[ "$ADMIN_TEST_SENTINEL" == private-test-value ]]
            [[ -f DN42Atlas.slnx ]]
            echo "$*" >> "$CALL_LOG"
            if [[ "$*" == *"manual-request 404" ]]; then
                echo "Request not found."
                exit 1
            fi
            if [[ "$*" == *"manual-request "* ]]; then echo "Full request: PRIVATE message"; fi
            """.Replace("\r\n", "\n") + "\n");
        var harness = Path.Combine(files.DirectoryPath, "harness.sh");
        File.WriteAllText(harness, """
            set -euo pipefail
            export PATH="$PWD/bin:$PATH"
            export CALL_LOG="$PWD/calls.txt"
            export DN42ATLAS_ADMIN_ENV_FILE="$PWD/deployment.env"
            chmod +x bin/dotnet
            printf '1\n2\n7\n3\n7\n2\n4\n7\nno\n4\n7\nDELETE\n4\n404\n5\n' | bash "$1"
            """.Replace("\r\n", "\n") + "\n");
        var start = new ProcessStartInfo(bash)
        { WorkingDirectory = files.DirectoryPath, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(harness.Replace('\\', '/'));
        start.ArgumentList.Add(Path.Combine(root.FullName, "dn42atlas-request-admin").Replace('\\', '/'));
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var output = await stdout;
        Assert.AreEqual(0, process.ExitCode, await stderr);
        Assert.Contains("DN42Atlas Manual Request Admin", output);
        Assert.Contains("Cancelled.", output);
        Assert.Contains("Full request: PRIVATE message", output);
        Assert.DoesNotContain("private-test-value", output);
        var calls = File.ReadAllLines(Path.Combine(files.DirectoryPath, "calls.txt"));
        Assert.HasCount(8, calls);
        Assert.IsTrue(calls[0].EndsWith("-- manual-requests", StringComparison.Ordinal));
        Assert.IsTrue(calls[3].EndsWith("-- manual-request-status 7 Reviewed", StringComparison.Ordinal));
        Assert.IsTrue(calls[5].EndsWith("-- manual-request 7", StringComparison.Ordinal));
        Assert.IsTrue(calls[6].EndsWith("-- manual-request-delete 7", StringComparison.Ordinal));
        Assert.IsTrue(calls[7].EndsWith("-- manual-request 404", StringComparison.Ordinal));
        Assert.HasCount(1, calls.Where(call => call.Contains("manual-request-delete", StringComparison.Ordinal)));
    }

    private static Dictionary<string, byte[]> Snapshot(MutationFixture fixture, string database) =>
        Directory.GetFiles(fixture.Files.DirectoryPath, "*", SearchOption.AllDirectories)
            .Where(path => path != database).ToDictionary(path => path, File.ReadAllBytes);

    private static void AssertUnchanged(Dictionary<string, byte[]> originals)
    {
        foreach (var (path, bytes) in originals) CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path), path);
    }
}
