using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Every page script parses. A script with one syntax error does not run at
/// all, so the page it drives renders empty with nothing in the server log:
/// on 2026-10-25 a comment pasted mid-line into defect-reports.js swallowed the
/// rest of a fetch call and the whole Defect reports page went blank.
/// Uses Node's parser (node --check); skips where Node is not installed.
/// </summary>
public class ScriptSyntaxTests
{
    private readonly ITestOutputHelper _out;
    public ScriptSyntaxTests(ITestOutputHelper output) => _out = output;

    private static string? ScriptsDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var js = Path.Combine(d.FullName, "SharbatlyQMS.Web", "wwwroot", "js");
            if (Directory.Exists(js)) return js;
        }
        return null;
    }

    [Fact]
    public void Every_page_script_parses()
    {
        var dir = ScriptsDir();
        Assert.NotNull(dir);
        var failures = new List<string>();
        foreach (var file in Directory.GetFiles(dir!, "*.js", SearchOption.AllDirectories))
        {
            ProcessStartInfo psi = new("node", $"--check \"{file}\"")
            {
                RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false
            };
            Process? p;
            try { p = Process.Start(psi); }
            catch (System.ComponentModel.Win32Exception) { _out.WriteLine("Node is not installed; skipped."); return; }
            if (p == null) return;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
            if (p.ExitCode != 0) failures.Add($"{Path.GetFileName(file)}: {err.Split('\n').FirstOrDefault(l => l.Contains("Error")) ?? err}");
            else _out.WriteLine($"ok  {Path.GetFileName(file)}");
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
