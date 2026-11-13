using System;
using System.IO;
using Unpakz.Core;

namespace Unpakz.Engine;

/// <summary>System.IO implementation. Windows-specific fast paths will live in Unpakz.Windows.</summary>
public sealed class LocalFileSystem : IFileSystem
{
    public PathKind GetKind(string path)
    {
        FileAttributes a;
        try { a = File.GetAttributes(path); }
        catch (FileNotFoundException) { return PathKind.None; }
        catch (DirectoryNotFoundException) { return PathKind.None; }

        if ((a & FileAttributes.ReparsePoint) != 0) return PathKind.ReparsePoint;
        return (a & FileAttributes.Directory) != 0 ? PathKind.Directory : PathKind.File;
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public IFileWriter CreateFile(string finalPath, long preallocate)
    {
        var dir = Path.GetDirectoryName(finalPath)!;
        var temp = Path.Combine(dir, ".unpakz-" + Guid.NewGuid().ToString("N") + ".tmp");
        var stream = new FileStream(temp, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 0,
            PreallocationSize = Math.Max(0, preallocate),
            Options = FileOptions.SequentialScan,
        });
        return new Writer(stream, temp, finalPath);
    }

    private sealed class Writer(FileStream stream, string temp, string final) : IFileWriter
    {
        private bool _committed;

        public void Write(ReadOnlySpan<byte> data) => stream.Write(data);

        public void Commit(DateTime? lastWriteLocal, bool overwrite)
        {
            stream.Dispose();
            if (lastWriteLocal is { } t) File.SetLastWriteTime(temp, t);

            try
            {
                File.Move(temp, final, overwrite);
            }
            catch (IOException) when (!overwrite && (File.Exists(final) || Directory.Exists(final)))
            {
                throw new ArchiveException("Destination already exists.", ArchiveError.Conflict);
            }
            catch (IOException) when (Directory.Exists(final))
            {
                throw new ArchiveException("A directory exists where a file should be written.", ArchiveError.Conflict);
            }

            _committed = true;
        }

        public void Dispose()
        {
            stream.Dispose();
            if (_committed) return;
            try { File.Delete(temp); }
            catch (IOException) { /* best effort: temp file name is unique and hidden */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
