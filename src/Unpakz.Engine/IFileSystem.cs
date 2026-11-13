using System;
using Unpakz.Core;

namespace Unpakz.Engine;

public enum PathKind { None, File, Directory, ReparsePoint }

/// <summary>
/// Output side of extraction. A writer targets a temporary *file* in the final directory and is renamed on
/// <see cref="Commit"/>; disposing without committing removes it. Bytes are written once, to that file.
/// </summary>
public interface IFileSystem
{
    /// <summary>Kind of the path itself, never following a link at the final component.</summary>
    PathKind GetKind(string path);

    void CreateDirectory(string path);

    IFileWriter CreateFile(string finalPath, long preallocate);
}

public interface IFileWriter : IOutputSink, IDisposable
{
    /// <summary>Finalizes the file: sets the timestamp and atomically renames it to its final name.</summary>
    void Commit(DateTime? lastWriteLocal, bool overwrite);
}
