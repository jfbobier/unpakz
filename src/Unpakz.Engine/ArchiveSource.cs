using System;
using System.Collections.Generic;
using Unpakz.Core;

namespace Unpakz.Engine;

/// <summary>An archive opened from disk: finds the volumes, maps them, parses the structure. Dispose to release the files.</summary>
public sealed class ArchiveSource : IDisposable
{
    private readonly List<MappedFile> _mapped;

    public IArchive Archive { get; }

    public IReadOnlyList<string> VolumePaths { get; }

    private ArchiveSource(IArchive archive, IReadOnlyList<string> paths, List<MappedFile> mapped)
    {
        Archive = archive;
        VolumePaths = paths;
        _mapped = mapped;
    }

    public static ArchiveSource Open(string path, ArchiveLimits? limits = null)
    {
        limits ??= new ArchiveLimits();
        var paths = VolumeLocator.Find(path, limits.MaxVolumes);
        var mapped = new List<MappedFile>(paths.Count);
        try
        {
            foreach (var p in paths) mapped.Add(MappedFile.Open(p));
            var memories = new List<ReadOnlyMemory<byte>>(mapped.Count);
            foreach (var m in mapped) memories.Add(m.Memory);
            return new ArchiveSource(ArchiveReader.Open(memories, limits), paths, mapped);
        }
        catch
        {
            foreach (var m in mapped) m.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var m in _mapped) m.Dispose();
        _mapped.Clear();
    }
}
