using System;
using System.Text;

namespace Zipx.Engine;

/// <summary>Why an archive entry name was refused.</summary>
public enum PathRejection
{
    None = 0,
    Empty,
    Rooted,
    Traversal,
    InvalidCharacter,
    ReservedName,
    TrailingDotOrSpace,
    TooLong,
    TooDeep,
}

/// <summary>Limits applied to entry names. Defaults are safe for Windows long-path mode.</summary>
public sealed class PathGuardOptions
{
    public int MaxPathLength { get; init; } = 32_000;
    public int MaxDepth { get; init; } = 256;
    public int MaxSegmentLength { get; init; } = 255;
}

/// <summary>Result of validating one entry name. <see cref="RelativePath"/> uses '/' separators and is empty when unsafe.</summary>
public readonly record struct PathCheck(bool IsSafe, PathRejection Reason, string RelativePath, bool IsDirectory);

/// <summary>
/// The single chokepoint that turns an untrusted archive entry name into a safe relative path.
/// Pure and platform independent: it applies the strictest (Windows) rules everywhere.
/// </summary>
public static class PathGuard
{
    private static readonly PathGuardOptions DefaultOptions = new();

    public static PathCheck Validate(string entryName) => Validate(entryName, DefaultOptions);

    public static PathCheck Validate(string entryName, PathGuardOptions options)
    {
        ArgumentNullException.ThrowIfNull(entryName);
        ArgumentNullException.ThrowIfNull(options);

        if (entryName.Length == 0) return Fail(PathRejection.Empty);

        // Rooted: leading separator (covers UNC, \\?\ and \\.\) or a drive prefix such as "C:" / "c:foo".
        if (entryName[0] is '/' or '\\') return Fail(PathRejection.Rooted);
        if (entryName.Length >= 2 && IsAsciiLetter(entryName[0]) && entryName[1] == ':') return Fail(PathRejection.Rooted);

        var isDirectory = entryName[^1] is '/' or '\\';
        var result = new StringBuilder(entryName.Length);
        var depth = 0;
        var start = 0;

        for (var i = 0; i <= entryName.Length; i++)
        {
            if (i < entryName.Length && entryName[i] is not ('/' or '\\')) continue;

            var segment = entryName.AsSpan(start, i - start);
            start = i + 1;

            if (segment.IsEmpty || segment is ".") continue;
            if (segment is "..") return Fail(PathRejection.Traversal);

            var reason = CheckSegment(segment, options);
            if (reason != PathRejection.None) return Fail(reason);

            depth++;
            if (depth > options.MaxDepth) return Fail(PathRejection.TooDeep);

            if (result.Length > 0) result.Append('/');
            result.Append(segment);
            if (result.Length > options.MaxPathLength) return Fail(PathRejection.TooLong);
        }

        return depth == 0
            ? Fail(PathRejection.Empty)
            : new PathCheck(true, PathRejection.None, result.ToString(), isDirectory);
    }

    private static PathRejection CheckSegment(ReadOnlySpan<char> segment, PathGuardOptions options)
    {
        foreach (var c in segment)
        {
            if (c < ' ' || c == 0x7F || c is '<' or '>' or ':' or '"' or '|' or '?' or '*')
                return PathRejection.InvalidCharacter;
        }

        if (IsReservedDeviceName(segment)) return PathRejection.ReservedName;
        if (segment[^1] is '.' or ' ') return PathRejection.TrailingDotOrSpace;
        if (segment.Length > options.MaxSegmentLength) return PathRejection.TooLong;
        return PathRejection.None;
    }

    private static bool IsReservedDeviceName(ReadOnlySpan<char> segment)
    {
        // Windows ignores the extension and trailing spaces: "NUL.txt" and "nul " both open the NUL device.
        var dot = segment.IndexOf('.');
        var stem = (dot >= 0 ? segment[..dot] : segment).TrimEnd(' ');

        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
            return true;

        if (stem.Length == 4
            && (stem[..3].Equals("COM", StringComparison.OrdinalIgnoreCase)
                || stem[..3].Equals("LPT", StringComparison.OrdinalIgnoreCase)))
        {
            var d = stem[3];
            return d is >= '1' and <= '9' or '\u00B9' or '\u00B2' or '\u00B3';
        }

        return false;
    }

    private static bool IsAsciiLetter(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');

    private static PathCheck Fail(PathRejection reason) => new(false, reason, string.Empty, false);
}
