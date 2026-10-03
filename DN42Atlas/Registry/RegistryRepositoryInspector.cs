using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace DN42Atlas.Registry;

public sealed record RegistryRepositoryState(
    string CommitSha,
    DateTimeOffset? CommitTimestamp,
    string GitDirectory);

public interface IRegistryRepositoryInspector
{
    bool TryInspect(
        string registryRoot,
        out RegistryRepositoryState? state);
}

public sealed class GitRegistryRepositoryInspector :
    IRegistryRepositoryInspector
{
    public bool TryInspect(
        string registryRoot,
        out RegistryRepositoryState? state)
    {
        state = null;

        try
        {
            var configuredRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(registryRoot));
            var topLevel = GitProcess.Run(
                configuredRoot,
                "rev-parse",
                "--show-toplevel");

            if (!topLevel.Succeeded ||
                !string.Equals(
                    configuredRoot,
                    Path.TrimEndingDirectorySeparator(
                        Path.GetFullPath(topLevel.Output)),
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                return false;
            }

            var commit = GitProcess.Run(
                configuredRoot,
                "rev-parse",
                "HEAD");
            var gitDirectory = GitProcess.Run(
                configuredRoot,
                "rev-parse",
                "--absolute-git-dir");

            if (!commit.Succeeded ||
                !IsCommitSha(commit.Output) ||
                !gitDirectory.Succeeded ||
                string.IsNullOrWhiteSpace(gitDirectory.Output))
            {
                return false;
            }

            DateTimeOffset? commitTimestamp = null;
            var timestamp = GitProcess.Run(
                configuredRoot,
                "show",
                "-s",
                "--format=%cI",
                "HEAD");

            if (timestamp.Succeeded &&
                DateTimeOffset.TryParse(
                    timestamp.Output,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsedTimestamp))
            {
                commitTimestamp = parsedTimestamp;
            }

            state = new RegistryRepositoryState(
                commit.Output,
                commitTimestamp,
                Path.GetFullPath(gitDirectory.Output));

            return true;
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            Win32Exception)
        {
            return false;
        }
    }

    private static bool IsCommitSha(string value) =>
        value.Length is >= 7 and <= 64 &&
        value.All(Uri.IsHexDigit);
}

internal sealed record GitProcessResult(
    int ExitCode,
    string Output,
    string Error)
{
    public bool Succeeded => ExitCode == 0;
}

internal static class GitProcess
{
    public static GitProcessResult Run(
        string workingDirectory,
        params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = CreateStartInfo(
                workingDirectory,
                arguments)
        };

        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new GitProcessResult(
            process.ExitCode,
            output.Trim(),
            error.Trim());
    }

    public static async Task<GitProcessResult> RunAsync(
        string workingDirectory,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = CreateStartInfo(
                workingDirectory,
                arguments)
        };

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        return new GitProcessResult(
            process.ExitCode,
            (await outputTask).Trim(),
            (await errorTask).Trim());
    }

    private static ProcessStartInfo CreateStartInfo(
        string workingDirectory,
        IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        return startInfo;
    }
}
