using Unpakz.Core;
using Unpakz.Engine;

namespace Unpakz.Tests.Support;

/// <summary>Test file system with Windows-like case-insensitivity, symlink simulation and a full operation log.</summary>
internal sealed class InMemoryFileSystem : IFileSystem
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Dirs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Links { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTime?> LastWrite { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> CreatedDirs { get; } = [];
    public List<(string Temp, string Final)> TempFiles { get; } = [];
    public long BytesWritten { get; private set; }
    public int Commits { get; private set; }
    public List<long> Preallocations { get; } = [];

    /// <summary>Invoked right after the temp file exists; lets a test simulate another process racing us.</summary>
    public Action<string>? OnCreateFile { get; set; }

    public InMemoryFileSystem(string root) => Dirs.Add(Path.GetPathRoot(root)!);

    public PathKind GetKind(string path) =>
        Links.Contains(path) ? PathKind.ReparsePoint : Files.ContainsKey(path) ? PathKind.File : Dirs.Contains(path) ? PathKind.Directory : PathKind.None;

    public void CreateDirectory(string path)
    {
        if (GetKind(path) == PathKind.Directory) return;
        if (GetKind(path) != PathKind.None) throw new IOException("exists as non-directory: " + path);
        var parent = Path.GetDirectoryName(path)!;
        if (!Dirs.Contains(parent)) throw new DirectoryNotFoundException(parent);
        Dirs.Add(path);
        CreatedDirs.Add(path);
    }

    public IFileWriter CreateFile(string finalPath, long preallocate)
    {
        var dir = Path.GetDirectoryName(finalPath)!;
        if (!Dirs.Contains(dir)) throw new DirectoryNotFoundException(dir);
        var temp = Path.Combine(dir, ".unpakz-" + Guid.NewGuid().ToString("N") + ".tmp");
        Files[temp] = [];
        TempFiles.Add((temp, finalPath));
        Preallocations.Add(preallocate);
        OnCreateFile?.Invoke(finalPath);
        return new Writer(this, temp, finalPath);
    }

    public string[] LeftoverTemps() => [.. Files.Keys.Where(k => Path.GetFileName(k).StartsWith(".unpakz-", StringComparison.Ordinal))];

    private sealed class Writer(InMemoryFileSystem fs, string temp, string final) : IFileWriter
    {
        private readonly MemoryStream _data = new();
        private bool _done;

        public void Write(ReadOnlySpan<byte> data) { _data.Write(data); fs.BytesWritten += data.Length; }

        public void Commit(DateTime? lastWriteLocal, bool overwrite)
        {
            if (fs.GetKind(final) == PathKind.Directory) throw new ArchiveException("directory in the way", ArchiveError.Conflict);
            if (fs.GetKind(final) != PathKind.None && !overwrite) throw new ArchiveException("exists", ArchiveError.Conflict);
            fs.Files.Remove(temp);
            fs.Links.Remove(final);          // rename replaces the link itself, never its target
            fs.Files[final] = _data.ToArray();
            fs.LastWrite[final] = lastWriteLocal;
            fs.Commits++;
            _done = true;
        }

        public void Dispose()
        {
            if (!_done) fs.Files.Remove(temp);
            _data.Dispose();
        }
    }
}
