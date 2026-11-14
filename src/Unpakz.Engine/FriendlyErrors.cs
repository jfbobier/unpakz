using System;
using System.IO;
using System.Text;
using Unpakz.Core;

namespace Unpakz.Engine;

/// <summary>Plain-language text for errors, safe to put on screen (no stack traces, no control characters, bounded length).</summary>
public static class FriendlyErrors
{
    private const int MaxDetail = 300;

    public static string Describe(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error switch
        {
            ArchiveException { Error: ArchiveError.MissingVolume } a =>
                $"Part {a.Volume?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"} of this multi-part archive is missing. " +
                "Keep all parts (name.part1.rar, name.part2.rar, ...) in the same folder and open the first one.",
            ArchiveException a => ForArchive(a),
            OperationCanceledException => "Extraction was cancelled.",
            FileNotFoundException f => $"File not found: {Clean(f.FileName ?? f.Message)}",
            DirectoryNotFoundException d => $"Folder not found: {Clean(d.Message)}",
            UnauthorizedAccessException => "Access was denied. Choose another folder, or check the folder's permissions.",
            IOException io when io.Message.Contains("space", StringComparison.OrdinalIgnoreCase) =>
                "The disk is full. Free some space or choose another drive.",
            IOException io => $"A file operation failed: {Clean(io.Message)}",
            _ => $"Unexpected error ({error.GetType().Name}): {Clean(error.Message)}",
        };
    }

    private static string ForArchive(ArchiveException a)
    {
        var detail = Clean(a.Message);
        return a.Error switch
        {
            ArchiveError.Corrupt => $"The archive is damaged or incomplete. ({detail})",
            ArchiveError.Unsupported => $"This archive uses a feature that is not supported yet. ({detail})",
            ArchiveError.LimitExceeded => $"The archive exceeds a safety limit. ({detail})",
            ArchiveError.UnsafePath => $"The archive contains a file that would be written outside the chosen folder or in an unsafe way, so it was refused. Nothing was extracted. ({detail})",
            ArchiveError.Conflict => $"A file already exists or two files in the archive collide. ({detail})",
            _ => detail,
        };
    }

    private static string Clean(string text)
    {
        var sb = new StringBuilder(Math.Min(text.Length, MaxDetail));
        foreach (var c in text)
        {
            if (sb.Length >= MaxDetail) { sb.Append('…'); break; }
            sb.Append(char.IsControl(c) ? ' ' : c);
        }

        return sb.ToString().Trim();
    }
}
