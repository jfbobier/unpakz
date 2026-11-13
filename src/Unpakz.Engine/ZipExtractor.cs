using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Unpakz.Core;
using Unpakz.Core.Zip;

namespace Unpakz.Engine;

/// <summary>
/// Plans, then extracts. Order of operations is the security model: (1) parse + validate everything with no I/O,
/// (2) create the destination, (3) per file: verify no link in the path, decode straight into a same-directory temp
/// file, verify size + CRC, rename. A failed file never survives; completed files stay.
/// </summary>
public static class ZipExtractor
{
    public static ExtractionResult Extract(
        ReadOnlyMemory<byte> zip,
        string destination,
        ExtractOptions? options = null,
        IFileSystem? fileSystem = null,
        IProgress<ExtractionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(destination);
        options ??= new ExtractOptions();
        var fs = fileSystem ?? new LocalFileSystem();

        cancellationToken.ThrowIfCancellationRequested();
        var entries = ZipCentralDirectory.Read(zip.Span, options.Limits);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var plan = ExtractionPlanner.Plan(zip, entries, root, options);

        EnsureRoot(fs, root);
        var verified = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
        var skipped = new List<SkippedEntry>(plan.Skipped);
        var totalFiles = 0;
        foreach (var it in plan.Items) if (!it.IsDirectory) totalFiles++;

        var state = new State(plan.TotalBytes, totalFiles, progress);
        var dirs = 0;

        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = ResolveUnderRoot(root, item.RelativePath);

            if (item.IsDirectory)
            {
                EnsureDirectory(fs, root, item.RelativePath, verified);
                dirs++;
                continue;
            }

            var slash = item.RelativePath.LastIndexOf('/');
            if (slash > 0) EnsureDirectory(fs, root, item.RelativePath[..slash], verified);

            switch (fs.GetKind(full))
            {
                case PathKind.Directory:
                    throw new ArchiveException($"A directory exists where '{ExtractionPlanner.Printable(item.RelativePath)}' should be written.", ArchiveError.Conflict);
                case PathKind.File or PathKind.ReparsePoint:
                    if (options.Overwrite == OverwritePolicy.Fail)
                        throw new ArchiveException($"'{ExtractionPlanner.Printable(item.RelativePath)}' already exists.", ArchiveError.Conflict);
                    if (options.Overwrite == OverwritePolicy.Skip)
                    {
                        skipped.Add(new SkippedEntry(item.Entry.Name, SkipReason.AlreadyExists));
                        continue;
                    }

                    break;
            }

            ExtractFile(zip, item.Entry, full, fs, options, state, cancellationToken);
        }

        return new ExtractionResult(state.FilesCompleted, dirs, state.BytesWritten, skipped);
    }

    private static void ExtractFile(ReadOnlyMemory<byte> zip, ZipEntryInfo entry, string full, IFileSystem fs,
        ExtractOptions options, State state, CancellationToken ct)
    {
        var declared = (long)Math.Min(entry.UncompressedSize, long.MaxValue);
        var prealloc = declared <= options.PreallocateLimit ? declared : 0;

        using var writer = fs.CreateFile(full, prealloc);
        ZipEntryExtractor.Extract(zip, entry, new TrackingSink(writer, state, ct), options.Limits);

        DateTime? mtime = options.RestoreTimestamps && DosTime.TryConvert(entry.DosDateTime, out var t) ? t : null;
        writer.Commit(mtime, options.Overwrite == OverwritePolicy.Overwrite);

        state.FilesCompleted++;
        state.Report();
    }

    internal static string ResolveUnderRoot(string root, string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(prefix, cmp)) throw new ArchiveException("Path escapes the destination.", ArchiveError.UnsafePath);
        return full;
    }

    private static void EnsureRoot(IFileSystem fs, string root)
    {
        var missing = new Stack<string>();
        var cur = root;
        while (true)
        {
            var kind = fs.GetKind(cur);
            if (kind is PathKind.Directory or PathKind.ReparsePoint) break;
            if (kind == PathKind.File) throw new ArchiveException("Destination is a file.", ArchiveError.Conflict);

            missing.Push(cur);
            var parent = Path.GetDirectoryName(cur);
            if (parent is null) break;
            cur = parent;
        }

        while (missing.Count > 0) fs.CreateDirectory(missing.Pop());
    }

    private static void EnsureDirectory(IFileSystem fs, string root, string relativeDir, HashSet<string> verified)
    {
        var cur = root;
        foreach (var segment in relativeDir.Split('/'))
        {
            cur = Path.Combine(cur, segment);
            if (verified.Contains(cur)) continue;

            switch (fs.GetKind(cur))
            {
                case PathKind.ReparsePoint:
                    throw new ArchiveException($"Refusing to extract through a link at '{ExtractionPlanner.Printable(segment)}'.", ArchiveError.UnsafePath);
                case PathKind.File:
                    throw new ArchiveException($"A file exists where directory '{ExtractionPlanner.Printable(segment)}' is needed.", ArchiveError.Conflict);
                case PathKind.None:
                    fs.CreateDirectory(cur);
                    break;
            }

            verified.Add(cur);
        }
    }

    private sealed class State(long totalBytes, int totalFiles, IProgress<ExtractionProgress>? progress)
    {
        public long BytesWritten;
        public int FilesCompleted;

        public void Report() => progress?.Report(new ExtractionProgress(BytesWritten, totalBytes, FilesCompleted, totalFiles));
    }

    private sealed class TrackingSink(IFileWriter writer, State state, CancellationToken ct) : IOutputSink
    {
        public void Write(ReadOnlySpan<byte> data)
        {
            ct.ThrowIfCancellationRequested();
            writer.Write(data);
            state.BytesWritten += data.Length;
            state.Report();
        }
    }
}
