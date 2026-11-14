using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Unpakz.Engine;

/// <summary>Default extraction folder: next to the archive, named after it ("backup.zip" -> "backup", "set.part01.rar" -> "set").</summary>
public static partial class DestinationNaming
{
    [GeneratedRegex(@"\.part\d+\.rar$|\.rar$|\.zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArchiveSuffix();

    public static string DirectoryOf(string archivePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        return Path.GetDirectoryName(Path.GetFullPath(archivePath))!;
    }

    public static string FolderNextTo(string archivePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        var full = Path.GetFullPath(archivePath);
        var file = Path.GetFileName(full);
        var name = ArchiveSuffix().Replace(file, string.Empty).Trim().TrimEnd('.', ' ');

        if (name.Length == 0 || !PathGuard.Validate(name).IsSafe) name = "extracted";
        if (string.Equals(name, file, StringComparison.OrdinalIgnoreCase)) name += " (extracted)";
        return Path.Combine(Path.GetDirectoryName(full)!, name);
    }
}
