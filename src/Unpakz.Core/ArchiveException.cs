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
}

/// <summary>The only exception type decoders and parsers may throw for bad input.</summary>
public sealed class ArchiveException : Exception
{
    public ArchiveError Error { get; }

    public ArchiveException(string message, ArchiveError error = ArchiveError.Corrupt) : base(message) => Error = error;

    public ArchiveException(string message, Exception inner, ArchiveError error = ArchiveError.Corrupt) : base(message, inner) => Error = error;

    public ArchiveException() : this("Invalid archive.") { }
}
