using System.Diagnostics;
using DN42Atlas.OptOut.Web;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class OperationsTests
{
    [TestMethod]
    public async Task BothOperatorPagesHaveAtlasBackLink()
    {
        using var fixture = await MutationFixture.CreateAsync();
        var pages = new[] { OptOutPage.RenderSignedOut(),
            OptOutPage.RenderSignedIn(fixture.Identity, [], [], [], fixture.Snapshot) };
        foreach (var page in pages)
        {
            Assert.Contains("<a href=\"/\">← Back to DN42Atlas</a>", page);
            Assert.IsLessThan(page.IndexOf("<h1>", StringComparison.Ordinal), page.IndexOf("← Back to DN42Atlas", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task ReleaseRetentionPreservesCurrentIgnoresUnsafeEntriesAndCleansOnlyOwnedTemp()
    {
        using var files = new TestFiles();
        var script = """
            set -euo pipefail
            source "$1"
            mkdir releases outside
            valid_retention 0
            valid_retention 3
            valid_retention 0003
            for invalid in '' -1 1.5 abc; do
                if valid_retention "$invalid"; then exit 9; fi
            done
            releases="$PWD/releases"
            a=20261001T120000Z-aaaaaaaaaaaa
            b=20261002T120000Z-bbbbbbbbbbbb
            c=20261003T120000Z-cccccccccccc
            d=20261004T120000Z-dddddddddddd
            e=20261005T120000Z-eeeeeeeeeeee
            mkdir "$releases/$a" "$releases/$b" "$releases/$c" "$releases/$d" "$releases/$e"
            mkdir "$releases/.other.tmp" "$releases/manual-not-a-release"
            echo keep > outside/sentinel
            ln -s "$PWD/outside" "$releases/20261006T120000Z-ffffffffffff"
            ln -s "$PWD/outside" "$releases/$b/nested-link"
            # Current deliberately is not the lexically newest release.
            ln -s "$releases/$a" current
            prune_releases "$releases" "$PWD/current" 3
            [[ -d "$releases/$a" && ! -e "$releases/$b" && -d "$releases/$c" && -d "$releases/$d" && -d "$releases/$e" ]]
            [[ -L "$releases/20261006T120000Z-ffffffffffff" && -f outside/sentinel ]]
            [[ -d "$releases/.other.tmp" && -d "$releases/manual-not-a-release" ]]
            # Invalid retention and an external current target must fail before deletion.
            if prune_releases "$releases" "$PWD/current" -1; then exit 10; fi
            ln -s "$PWD/outside" external-current
            if prune_releases "$releases" "$PWD/external-current" 0; then exit 11; fi
            [[ -d "$releases/$e" ]]
            # Failed removal reports failure and leaves the current release intact.
            rm() { echo "Simulated removal failure" >&2; return 1; }
            if prune_releases "$releases" "$PWD/current" 0; then exit 12; fi
            unset -f rm
            prune_releases "$releases" "$PWD/current" 000
            [[ -d "$releases/$a" && ! -e "$releases/$c" && ! -e "$releases/$d" && ! -e "$releases/$e" ]]
            [[ -f outside/sentinel ]]
            # EXIT cleanup belongs only to this deployment's known private temp directory.
            mkdir "$releases/.owned.tmp"
            set +e
            (
                RELEASES_DIR="$releases"
                TEMP="$releases/.owned.tmp"
                temp_owned=true
                link_owned=false
                trap cleanup_deploy EXIT
                exit 7
            )
            status=$?
            set -e
            [[ $status -eq 7 && ! -e "$releases/.owned.tmp" && -d "$releases/.other.tmp" ]]
            """;
        await RunBashAsync(files, script);
    }

    [TestMethod]
    public async Task FailedPublishOrRestartDoesNotPruneAndSuccessfulActivationDoes()
    {
        using var files = new TestFiles();
        var script = """
            set -euo pipefail
            source "$1"
            # The real script refuses root. Exercise main with harmless mock deployment
            # commands under a normal account; root hosts still test retention separately.
            if [[ $EUID -eq 0 ]]; then exit 0; fi
            git() {
                case "$*" in
                    'rev-parse --short=12 HEAD') echo aaaaaaaaaaaa ;;
                    *) return 0 ;;
                esac
            }
            date() { echo 20261004T120000Z; }
            dotnet() { [[ "${publish_fail:-false}" != true ]]; }
            systemctl() { return 0; }
            sudo() {
                [[ "${restart_fail:-false}" != true ]] || return 1
                if [[ "$*" == *"is-active"* && "${health_fail:-false}" == true ]]; then return 1; fi
                return 0
            }
            # Git-Bash needs real symlink semantics, not its default copy emulation.
            export MSYS=winsymlinks:nativestrict
            root="$PWD/deployment"
            mkdir -p "$root/releases/20261001T120000Z-bbbbbbbbbbbb" "$root/releases/.someone-else.tmp"
            ln -s "$root/releases/20261001T120000Z-bbbbbbbbbbbb" "$root/current"
            export DN42ATLAS_OPTOUT_DEPLOY_ROOT="$root"
            export DN42ATLAS_OPTOUT_RETAIN_PREVIOUS=0
            set +e
            ( set -e; publish_fail=true; main )
            status=$?
            set -e
            [[ $status -ne 0 && -d "$root/releases/20261001T120000Z-bbbbbbbbbbbb" ]]
            [[ ! -e "$root/releases/.20261004T120000Z-aaaaaaaaaaaa.tmp" && -d "$root/releases/.someone-else.tmp" ]]
            set +e
            ( set -e; restart_fail=true; main )
            status=$?
            set -e
            [[ $status -ne 0 && -d "$root/releases/20261001T120000Z-bbbbbbbbbbbb" ]]
            date() { echo 20261005T120000Z; }
            set +e
            ( set -e; health_fail=true; main )
            status=$?
            set -e
            [[ $status -ne 0 && -d "$root/releases/20261001T120000Z-bbbbbbbbbbbb" ]]
            date() { echo 20261006T120000Z; }
            main
            [[ -d "$root/releases/20261006T120000Z-aaaaaaaaaaaa" ]]
            [[ ! -e "$root/releases/20261001T120000Z-bbbbbbbbbbbb" && ! -e "$root/releases/20261004T120000Z-aaaaaaaaaaaa" ]]
            [[ ! -e "$root/releases/20261005T120000Z-aaaaaaaaaaaa" ]]
            [[ -d "$root/releases/.someone-else.tmp" ]]
            """;
        await RunBashAsync(files, script);
    }

    private static async Task RunBashAsync(TestFiles files, string script)
    {
        var bash = OperatingSystem.IsWindows() ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe") : "/bin/bash";
        if (!File.Exists(bash)) { Assert.Inconclusive("Bash unavailable."); return; }
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "deploy-optout.sh"))) root = root.Parent;
        Assert.IsNotNull(root);
        var harness = Path.Combine(files.DirectoryPath, "operations-test.sh");
        File.WriteAllText(harness, script.Replace("\r\n", "\n") + "\n");
        var start = new ProcessStartInfo(bash)
        { WorkingDirectory = files.DirectoryPath, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["MSYS"] = "winsymlinks:nativestrict";
        start.ArgumentList.Add(harness.Replace('\\', '/'));
        start.ArgumentList.Add(Path.Combine(root.FullName, "deploy-optout.sh").Replace('\\', '/'));
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.AreEqual(0, process.ExitCode, (await stdout) + "\n" + (await stderr));
    }
}
