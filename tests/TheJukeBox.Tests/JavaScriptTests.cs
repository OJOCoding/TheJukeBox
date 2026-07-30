using System.Diagnostics;
using System.Text;
using Xunit.Abstractions;

namespace TheJukeBox.Tests;

/// <summary>
/// Runs the frontend's JavaScript tests as part of <c>dotnet test</c>.
/// </summary>
/// <remarks>
/// <para>
/// The queue model under <c>wwwroot/js/player</c> is pure logic with real invariants, and
/// its predecessor's defect was a modelling error rather than a typo, so it is exactly the
/// kind of code that needs tests. Those tests use Node's built-in runner, which means no
/// package.json, no node_modules and no build step, keeping the frontend's
/// no-dependencies constraint intact.
/// </para>
/// <para>
/// Bridged into the .NET suite so one command runs everything. A separate command is a
/// command that gets forgotten, and CI would then report green while the frontend logic
/// was broken.
/// </para>
/// <para>
/// Skipped rather than failed when Node is absent, because Node is not required to build
/// or run this application and a contributor without it should still get a green suite.
/// CI has Node, so the tests are genuinely enforced there.
/// </para>
/// </remarks>
public class JavaScriptTests(ITestOutputHelper output)
{
    [Fact]
    public void Frontend_queue_tests_pass()
    {
        var repositoryRoot = FindRepositoryRoot();
        var testFile = Path.Combine(repositoryRoot, "tests", "js", "queue.test.mjs");

        Assert.True(File.Exists(testFile), $"Expected the JavaScript tests at {testFile}.");

        if (!TryRunNode($"--test \"{testFile}\"", repositoryRoot, out var exitCode, out var runnerOutput))
        {
            // xUnit v2 has no dynamic skip, so this is reported through the test output
            // rather than silently passing. Visible in the log, and CI has Node, so the
            // tests are enforced where it matters.
            output.WriteLine(
                "SKIPPED: Node.js is not on PATH, so the frontend queue tests did not run. "
                + "Install Node and re-run, or run them directly: node --test tests/js/queue.test.mjs");

            return;
        }

        output.WriteLine(runnerOutput);

        // The assertion message is the point: a bare "exit code 1" would send someone
        // hunting, whereas the runner's own output names the failing case.
        Assert.True(exitCode == 0, $"Node reported failing frontend tests:{Environment.NewLine}{runnerOutput}");

        // Guards against the suite silently becoming empty. A moved file or a renamed glob
        // would otherwise exit 0 having run nothing, and this test would pass vacuously
        // while claiming the frontend is covered.
        Assert.Contains("pass ", runnerOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("pass 0", runnerOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("fail 1", runnerOutput, StringComparison.Ordinal);
    }

    private static bool TryRunNode(string arguments, string workingDirectory, out int exitCode, out string output)
    {
        exitCode = -1;
        output = string.Empty;

        var startInfo = new ProcessStartInfo("node", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(startInfo);

            if (process is null)
            {
                return false;
            }

            var combined = new StringBuilder();
            combined.Append(process.StandardOutput.ReadToEnd());
            combined.Append(process.StandardError.ReadToEnd());

            // Generous, but bounded: a hung runner must fail the suite rather than hang CI.
            if (!process.WaitForExit(milliseconds: 120_000))
            {
                process.Kill(entireProcessTree: true);
                output = "The Node test runner timed out.";
                return true;
            }

            exitCode = process.ExitCode;
            output = combined.ToString();

            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            // Node is not installed. Reported as absent, not as a failure.
            return false;
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TheJukeBox.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the repository root walking up from {AppContext.BaseDirectory}.");
    }
}
