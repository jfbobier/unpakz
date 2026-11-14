using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unpakz.Core;

namespace Unpakz.Engine;

public enum RunStatus { Success, Failed, Cancelled }

public sealed record RunOutcome(RunStatus Status, string Message, ExtractionResult? Result, ArchiveError? Error = null);

/// <summary>Runs an extraction off the calling thread and turns every end state into a message a person can read.</summary>
public static class ExtractionRunner
{
    public static async Task<RunOutcome> RunAsync(
        IArchive archive,
        string destination,
        IReadOnlyCollection<int>? selection = null,
        ExtractOptions? options = null,
        IProgress<ExtractionProgress>? progress = null,
        IFileSystem? fileSystem = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrEmpty(destination);

        options ??= new ExtractOptions();
        if (selection is not null)
        {
            var keep = EntrySelection.Expand(archive.Entries, selection);
            options = options.WithFilter(e => keep.Contains(e.Index));
        }

        try
        {
            var result = await Task.Run(() => ArchiveExtractor.Extract(archive, destination, options, fileSystem, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
            return new RunOutcome(RunStatus.Success, Summarize(result, destination), result);
        }
        catch (OperationCanceledException ex)
        {
            return new RunOutcome(RunStatus.Cancelled, FriendlyErrors.Describe(ex), null);
        }
        catch (Exception ex) when (ex is ArchiveException or IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
        {
            return new RunOutcome(RunStatus.Failed, FriendlyErrors.Describe(ex), null, (ex as ArchiveException)?.Error);
        }
    }

    private static string Summarize(ExtractionResult r, string destination)
    {
        var inv = CultureInfo.InvariantCulture;
        var text = r.Files == 1
            ? $"1 file extracted to {destination}."
            : string.Create(inv, $"{r.Files} files extracted to {destination}.");

        if (r.Skipped.Count > 0)
        {
            var kinds = string.Join(", ", r.Skipped.GroupBy(s => s.Reason).Select(g => string.Create(inv, $"{g.Count()} {Describe(g.Key)}")));
            text += string.Create(inv, $" {r.Skipped.Count} skipped ({kinds}).");
        }

        return text;
    }

    private static string Describe(SkipReason reason) => reason switch
    {
        SkipReason.Symlink => "link",
        SkipReason.SpecialFile => "special file",
        SkipReason.UnsafePath => "unsafe name",
        _ => "already existed",
    };
}
