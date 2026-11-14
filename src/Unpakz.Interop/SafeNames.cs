using System;

namespace Unpakz.Interop;

/// <summary>
/// The client's own second look at names (the native guard is authoritative). It only ever says "no": anything that could leave a folder, name a
/// device, or confuse the shell is refused here too.
/// </summary>
public static class SafeNames
{
    private static readonly string[] Reserved = { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" };

    public static bool IsSafeSegment(string segment)
    {
        if (segment is null || segment.Length == 0 || segment.Length > 255 || segment == "." || segment == "..") return false;
        foreach (var c in segment)
            if (c < ' ' || c == 0x7F || "<>:\"|?*/\\".IndexOf(c) >= 0) return false;
        if (segment[segment.Length - 1] == '.' || segment[segment.Length - 1] == ' ') return false;

        var dot = segment.IndexOf('.');
        var stem = (dot >= 0 ? segment.Substring(0, dot) : segment).TrimEnd(' ');
        foreach (var r in Reserved) if (string.Equals(stem, r, StringComparison.OrdinalIgnoreCase)) return false;
        if (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))
        {
            var d = stem[3];
            if ((d >= '1' && d <= '9') || d == '¹' || d == '²' || d == '³') return false;
        }

        return true;
    }

    /// <summary>A '/'-separated relative path whose every segment is safe (no leading '/', no drive, no '..').</summary>
    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 32000) return false;
        foreach (var segment in path.Split('/')) if (!IsSafeSegment(segment)) return false;
        return true;
    }
}
