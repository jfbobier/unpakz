using System.ComponentModel;
using System.Diagnostics;

namespace Unpakz.Tests.Support;

/// <summary>External reference tools. When UNPAKZ_REQUIRE_ORACLE=1 (set by harness.sh) a missing tool is a failure, never a silent skip.</summary>
internal static class Oracle
{
    public static bool Required => Environment.GetEnvironmentVariable("UNPAKZ_REQUIRE_ORACLE") == "1";

    public static string? FindSevenZip()
    {
        foreach (var name in new[] { "7zz", "7z", "7z.exe" })
        {
            try { if (Run(name, ["i"], null, out _) == 0) return name; }
            catch (Win32Exception) { /* not installed */ }
        }

        return null;
    }

    /// <summary>Returns false (test should return early) when the tool is absent and not required; throws when required.</summary>
    public static bool TryGetSevenZip(out string exe)
    {
        var found = FindSevenZip();
        exe = found ?? string.Empty;
        if (found is not null) return true;
        if (Required) throw new InvalidOperationException("7-Zip oracle required (UNPAKZ_REQUIRE_ORACLE=1) but not found.");
        return false;
    }

    public static int Run(string exe, IEnumerable<string> args, string? workDir, out string output)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workDir ?? Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        output = stdout.Result + stderr.Result;
        return p.ExitCode;
    }

    public static string NewWorkDir(string label)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "work", label + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
