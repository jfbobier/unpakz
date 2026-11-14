using System;

namespace Unpakz.Core;

public enum ArchiveError
{
    /// <summary>Structure is invalid, inconsistent or truncated.</summary>
    Corrupt,
    /// <summary>Valid but not supported by this tool (multi-disk, unknown method, ...).</summary>
    Unsupported,
    /// <summary>A configured safety limit was exceeded.</summary>
    LimitExceeded,
    /// <summary>An entry name or destination state would let extraction escape or misbehave (traversal, reserved name, symlink in path).</summary>
    UnsafePath,
    /// <summary>Entries collide with each other or with existing files and policy forbids proceeding.</summary>
    Conflict,
    /// <summary>A volume of a multi-volume set was not provided (see <see cref="ArchiveException.Volume"/>).</summary>
    MissingVolume,
}

/// <summary>The only exception type decoders and parsers may throw for bad input.</summary>
public sealed class ArchiveException : Exception
{
    public ArchiveError Error { get; }

    /// <summary>For <see cref="ArchiveError.MissingVolume"/>: the 1-based number of the volume that is needed (as in name.part3.rar).</summary>
    public int? Volume { get; init; }

    public ArchiveException(string message, ArchiveError error = ArchiveError.Corrupt) : base(message) => Error = error;

    public ArchiveException(string message, Exception inner, ArchiveError error = ArchiveError.Corrupt) : base(message, inner) => Error = error;

    public ArchiveException() : this("Invalid archive.") { }
}
