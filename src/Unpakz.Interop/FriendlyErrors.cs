using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Unpakz.Interop;

/// <summary>Plain-language text for errors, safe to put on screen (no stack traces, no control characters, bounded length).</summary>
public static class FriendlyErrors
{
    private const int MaxDetail = 300;

    public static string Describe(Exception error)
    {
        if (error is null) throw new ArgumentNullException(nameof(error));
        switch (error)
        {
            case UnpakzException { Status: UnpakzStatus.MissingVolume } m:
                return "Part " + (m.Volume > 0 ? m.Volume.ToString(CultureInfo.InvariantCulture) : "?") + " of this multi-part archive is missing. " +
                       "Keep all parts (name.part1.rar, name.part2.rar, ...) in the same folder and open the first one.";
            case UnpakzException a: return ForArchive(a);
            case OperationCanceledException: return "Extraction was cancelled.";
            case FileNotFoundException f: return "File not found: " + Clean(f.FileName ?? f.Message);
            case DirectoryNotFoundException d: return "Folder not found: " + Clean(d.Message);
            case UnauthorizedAccessException: return "Access was denied. Choose another folder, or check the folder's permissions.";
            case IOException io when io.Message.IndexOf("space", StringComparison.OrdinalIgnoreCase) >= 0:
                return "The disk is full. Free some space or choose another drive.";
            case IOException io: return "A file operation failed: " + Clean(io.Message);
            default: return "Unexpected error (" + error.GetType().Name + "): " + Clean(error.Message);
        }
    }

    private static string ForArchive(UnpakzException a)
    {
        var detail = Clean(a.Message);
        switch (a.Status)
        {
            case UnpakzStatus.Corrupt: return "The archive is damaged or incomplete. (" + detail + ")";
            case UnpakzStatus.Unsupported: return "This archive uses a feature that is not supported yet. (" + detail + ")";
            case UnpakzStatus.LimitExceeded: return "The archive exceeds a safety limit. (" + detail + ")";
            case UnpakzStatus.UnsafePath:
                return "The archive contains a file that would be written outside the chosen folder or in an unsafe way, so it was refused. Nothing was extracted. (" + detail + ")";
            case UnpakzStatus.Conflict: return "A file already exists or two files in the archive collide. (" + detail + ")";
            case UnpakzStatus.Io: return "A file operation failed: " + detail;
            default: return detail;
        }
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
