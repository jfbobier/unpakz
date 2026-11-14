using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Unpakz.Core;
using Unpakz.Engine;

namespace Unpakz.Cli;

/// <summary>Command line: unpakz l|x|t archive [options]. Returns 0 on success, 1 on an archive/IO error, 2 on bad usage, 130 if cancelled.</summary>
public static class CliApp
{
    public const int Ok = 0, Failed = 1, Usage = 2, Cancelled = 130;

    private const string Help = """
        unpakz - extraction-only ZIP and RAR 5 tool (EXPERIMENTAL)

        Usage:
          unpakz l <archive>                      list contents
          unpakz t <archive>                      test: decode everything, check CRCs, write nothing
          unpakz x <archive> [paths...] [options] extract (all, or only the given files/folders)

        Options for x:
          -o <folder>     destination (default: a folder named after the archive, next to it)
          --overwrite     replace existing files
          --skip          keep existing files
          --here          extract into the archive's own folder
        Multi-part RAR sets: pass any part (name.part1.rar, name.part2.rar ...); all parts must be in one folder.
        """;

    public static int Run(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { output.WriteLine(Help); return args.Length == 0 ? Usage : Ok; }
        if (args[0] is "--version") { output.WriteLine("unpakz 0.1.0 (experimental)"); return Ok; }
        if (args.Length < 2) { error.WriteLine("Missing archive. Try: unpakz --help"); return Usage; }

        var command = args[0];
        if (command is not ("l" or "x" or "t")) { error.WriteLine($"Unknown command '{command}'. Try: unpakz --help"); return Usage; }

        string? destination = null;
        var policy = OverwritePolicy.Fail;
        var here = false;
        var paths = new List<string>();
        for (var i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o" when i + 1 < args.Length: destination = args[++i]; break;
                case "-o": error.WriteLine("-o needs a folder."); return Usage;
                case "--overwrite": policy = OverwritePolicy.Overwrite; break;
                case "--skip": policy = OverwritePolicy.Skip; break;
                case "--here": here = true; break;
                default:
                    if (args[i].StartsWith('-')) { error.WriteLine($"Unknown option '{args[i]}'."); return Usage; }
                    paths.Add(args[i]);
                    break;
            }
        }

        try
        {
            using var source = ArchiveSource.Open(args[1]);
            return command switch
            {
                "l" => List(source, output),
                "t" => Test(source, output, error, cancellationToken),
                _ => Extract(source, output, error, destination, here, policy, paths, cancellationToken),
            };
        }
        catch (OperationCanceledException) { error.WriteLine("Cancelled."); return Cancelled; }
        catch (Exception ex) when (ex is ArchiveException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine(FriendlyErrors.Describe(ex));
            return Failed;
        }
    }

    private static int List(ArchiveSource source, TextWriter output)
    {
        var inv = CultureInfo.InvariantCulture;
        output.WriteLine(string.Create(inv, $"{source.Archive.Format.ToUpperInvariant()}, {source.VolumePaths.Count} file(s)"));
        foreach (var e in source.Archive.Entries)
        {
            var type = e.Kind switch { EntryKind.Directory => "D", EntryKind.Symlink => "L", EntryKind.Special => "S", _ => "-" };
            var when = e.LastWriteLocal?.ToString("yyyy-MM-dd HH:mm", inv) ?? "                ";
            output.WriteLine(string.Create(inv, $"{type} {e.UncompressedSize,14} {when}  {Sanitize(e.Name)}"));
        }

        return Ok;
    }

    private static int Test(ArchiveSource source, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var archive = source.Archive;
        var failures = 0;
        var tested = 0;
        foreach (var e in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (e.Kind != EntryKind.File) continue;
            try
            {
                archive.CheckSupported(e);
                archive.Extract(e, new NullSink(), new ArchiveLimits());
                tested++;
            }
            catch (ArchiveException ex)
            {
                failures++;
                error.WriteLine($"{Sanitize(e.Name)}: {FriendlyErrors.Describe(ex)}");
            }
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{tested} file(s) OK, {failures} failed."));
        return failures == 0 ? Ok : Failed;
    }

    private static int Extract(ArchiveSource source, TextWriter output, TextWriter error, string? destination, bool here,
        OverwritePolicy policy, List<string> paths, CancellationToken ct)
    {
        var archivePath = source.VolumePaths[0];
        destination ??= here ? DestinationNaming.DirectoryOf(archivePath) : DestinationNaming.FolderNextTo(archivePath);

        var options = new ExtractOptions { Overwrite = policy };
        if (paths.Count > 0)
        {
            var wanted = new List<int>();
            foreach (var p in paths)
            {
                var normalized = EntrySelection.Normalize(p);
                var hit = source.Archive.Entries.Where(e => EntrySelection.Normalize(e.Name) == normalized || EntrySelection.Normalize(e.Name).StartsWith(normalized + "/", StringComparison.Ordinal)).Select(e => e.Index).ToList();
                if (hit.Count == 0) { error.WriteLine($"Not in the archive: {Sanitize(p)}"); return Failed; }
                wanted.AddRange(hit);
            }

            var keep = EntrySelection.Expand(source.Archive.Entries, wanted);
            options = options.WithFilter(e => keep.Contains(e.Index));
        }

        var result = ArchiveExtractor.Extract(source.Archive, destination, options, null, null, ct);
        var inv = CultureInfo.InvariantCulture;
        output.WriteLine(string.Create(inv, $"{result.Files} file(s) extracted to {destination}"));
        foreach (var s in result.Skipped) output.WriteLine($"skipped ({s.Reason}): {Sanitize(s.Name)}");
        return Ok;
    }

    private static string Sanitize(string name) => new(name.Select(c => char.IsControl(c) ? '?' : c).ToArray());

    private sealed class NullSink : IOutputSink
    {
        public void Write(ReadOnlySpan<byte> data)
        {
        }
    }
}
