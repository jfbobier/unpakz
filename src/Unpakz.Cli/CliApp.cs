using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Unpakz.Interop;

namespace Unpakz.Cli;

/// <summary>Command line: unpakz l|x|t archive [options]. Returns 0 on success, 1 on an archive/IO error, 2 on bad usage, 130 if cancelled.</summary>
public static class CliApp
{
    public const int Ok = 0, Failed = 1, Usage = 2, Cancelled = 130;

    private const string Help = @"unpakz - extraction-only ZIP, RAR and tar.gz tool (EXPERIMENTAL)

Usage:
  unpakz l <archive>                      list contents
  unpakz t <archive>                      test: decode everything, check checksums, write nothing
  unpakz x <archive> [paths...] [options] extract (all, or only the given files/folders)

Options for x:
  -o <folder>     destination (default: a folder named after the archive, next to it)
  --overwrite     replace existing files
  --skip          keep existing files
  --here          extract into the archive's own folder
Multi-part RAR sets: pass any part (name.part1.rar, name.part2.rar ...); all parts must be in one folder.";

    public static int Run(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        if (args is null) throw new ArgumentNullException(nameof(args));
        if (output is null) throw new ArgumentNullException(nameof(output));
        if (error is null) throw new ArgumentNullException(nameof(error));

        if (args.Length == 0 || args[0] == "-h" || args[0] == "--help" || args[0] == "help") { output.WriteLine(Help); return args.Length == 0 ? Usage : Ok; }
        if (args[0] == "--version") { output.WriteLine("unpakz 0.2.0 (experimental)"); return Ok; }
        if (args.Length < 2) { error.WriteLine("Missing archive. Try: unpakz --help"); return Usage; }

        var command = args[0];
        if (command != "l" && command != "x" && command != "t") { error.WriteLine("Unknown command '" + command + "'. Try: unpakz --help"); return Usage; }

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
                case "--overwrite": policy = OverwritePolicy.Replace; break;
                case "--skip": policy = OverwritePolicy.Skip; break;
                case "--here": here = true; break;
                default:
                    if (args[i].StartsWith("-", StringComparison.Ordinal)) { error.WriteLine("Unknown option '" + args[i] + "'."); return Usage; }
                    paths.Add(args[i]);
                    break;
            }
        }

        try
        {
            var session = ArchiveSession.Open(args[1]);
            switch (command)
            {
                case "l": return List(session, output);
                case "t": return Test(session, output, error, cancellationToken);
                default: return Extract(session, output, error, destination, here, policy, paths, cancellationToken);
            }
        }
        catch (OperationCanceledException) { error.WriteLine("Cancelled."); return Cancelled; }
        catch (Exception ex) when (ex is UnpakzException || ex is IOException || ex is UnauthorizedAccessException)
        {
            error.WriteLine(FriendlyErrors.Describe(ex));
            return Failed;
        }
    }

    private static int List(ArchiveSession session, TextWriter output)
    {
        var inv = CultureInfo.InvariantCulture;
        output.WriteLine(session.Format + ", " + session.VolumePaths.Count.ToString(inv) + " file(s)");
        foreach (var e in session.Entries)
        {
            string type;
            switch (e.Kind)
            {
                case EntryKind.Directory: type = "D"; break;
                case EntryKind.Symlink:
                case EntryKind.Hardlink: type = "L"; break;
                case EntryKind.Special: type = "S"; break;
                default: type = "-"; break;
            }

            var when = e.LastWriteUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", inv) ?? "                ";
            output.WriteLine(type + " " + e.Size.ToString(inv).PadLeft(14) + " " + when + "  " + Sanitize(e.Name));
        }

        return Ok;
    }

    private static int Test(ArchiveSession session, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var failures = 0;
        var tested = 0;
        foreach (var e in session.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (e.Kind != EntryKind.File) continue;
            try
            {
                NativeArchive.ReadEntry(session.Path, e.Index, (_, __) => !ct.IsCancellationRequested);     // decode and verify, discard the bytes
                tested++;
            }
            catch (UnpakzException ex)
            {
                failures++;
                error.WriteLine(Sanitize(e.Name) + ": " + FriendlyErrors.Describe(ex));
            }
        }

        output.WriteLine(tested.ToString(CultureInfo.InvariantCulture) + " file(s) OK, " + failures.ToString(CultureInfo.InvariantCulture) + " failed.");
        return failures == 0 ? Ok : Failed;
    }

    private static int Extract(ArchiveSession session, TextWriter output, TextWriter error, string? destination, bool here, OverwritePolicy policy, List<string> paths, CancellationToken ct)
    {
        var archivePath = session.VolumePaths[0];
        destination ??= here ? DestinationNaming.DirectoryOf(archivePath) : DestinationNaming.FolderNextTo(archivePath);

        var options = new ExtractOptions { Overwrite = policy, Cancellation = ct };
        if (paths.Count > 0)
        {
            var wanted = new List<int>();
            foreach (var p in paths)
            {
                var normalized = EntrySelection.Normalize(p);
                var hit = session.Entries.Where(e => EntrySelection.Normalize(e.Name) == normalized || EntrySelection.Normalize(e.Name).StartsWith(normalized + "/", StringComparison.Ordinal)).Select(e => (int)e.Index).ToList();
                if (hit.Count == 0) { error.WriteLine("Not in the archive: " + Sanitize(p)); return Failed; }
                wanted.AddRange(hit);
            }

            var keep = EntrySelection.Expand(session.Entries, wanted);
            options.Filter = e => keep.Contains((int)e.Index);
        }

        var result = NativeArchive.Extract(session.Path, destination, options);
        output.WriteLine(result.Files.ToString(CultureInfo.InvariantCulture) + " file(s) extracted to " + destination);
        if (result.Skipped > 0) output.WriteLine(result.Skipped.ToString(CultureInfo.InvariantCulture) + " skipped (links, special files or files that already existed)");
        return Ok;
    }

    private static string Sanitize(string name) => new(name.Select(c => char.IsControl(c) ? '?' : c).ToArray());
}
