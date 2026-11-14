using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Unpakz.Interop;

/// <summary>Default extraction folder: next to the archive, named after it ("backup.zip" -> "backup", "set.part01.rar" -> "set", "src.tar.gz" -> "src").</summary>
public static class DestinationNaming
{
    private static readonly Regex ArchiveSuffix = new(@"\.part\d+\.rar$|\.tar\.gz$|\.tgz$|\.rar$|\.zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string DirectoryOf(string archivePath)
    {
        if (string.IsNullOrEmpty(archivePath)) throw new ArgumentException("A path is required.", nameof(archivePath));
        return Path.GetDirectoryName(Path.GetFullPath(archivePath))!;
    }

    public static string FolderNextTo(string archivePath)
    {
        if (string.IsNullOrEmpty(archivePath)) throw new ArgumentException("A path is required.", nameof(archivePath));
        var full = Path.GetFullPath(archivePath);
        var file = Path.GetFileName(full);
        var name = ArchiveSuffix.Replace(file, string.Empty).Trim().TrimEnd('.', ' ');

        if (name.Length == 0 || !SafeNames.IsSafeSegment(name)) name = "extracted";
        if (string.Equals(name, file, StringComparison.OrdinalIgnoreCase)) name += " (extracted)";
        return Path.Combine(Path.GetDirectoryName(full)!, name);
    }
}
