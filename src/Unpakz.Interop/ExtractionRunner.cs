using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Unpakz.Interop;

public enum RunStatus { Success, Failed, Cancelled }

public sealed class ExtractionProgress
{
    public ExtractionProgress(ulong bytesWritten, ulong totalBytes)
    {
        BytesWritten = bytesWritten;
        TotalBytes = totalBytes;
    }

    public ulong BytesWritten { get; }

    public ulong TotalBytes { get; }
}

public sealed class RunOutcome
{
    public RunOutcome(RunStatus status, string message, ExtractResult? result, UnpakzStatus? error = null)
    {
        Status = status;
        Message = message;
        Result = result;
        Error = error;
    }

    public RunStatus Status { get; }

    public string Message { get; }

    public ExtractResult? Result { get; }

    public UnpakzStatus? Error { get; }
}

/// <summary>Runs an extraction off the calling thread and turns every end state into a message a person can read.</summary>
public static class ExtractionRunner
{
    public static async Task<RunOutcome> RunAsync(
        ArchiveSession session,
        string destination,
        IReadOnlyCollection<int>? selection = null,
        OverwritePolicy overwrite = OverwritePolicy.Fail,
        IProgress<ExtractionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (session is null) throw new ArgumentNullException(nameof(session));
        if (string.IsNullOrEmpty(destination)) throw new ArgumentException("A destination is required.", nameof(destination));

        var options = new ExtractOptions { Overwrite = overwrite, Cancellation = cancellationToken };
        if (selection is not null)
        {
            var keep = EntrySelection.Expand(session.Entries, selection);
            options.Filter = e => keep.Contains((int)e.Index);
        }

        if (progress is not null) options.Progress = (done, total, _) => progress.Report(new ExtractionProgress(done, total));

        try
        {
            var result = await Task.Run(() => NativeArchive.Extract(session.Path, destination, options), cancellationToken).ConfigureAwait(false);
            return new RunOutcome(RunStatus.Success, Summarize(result, destination), result);
        }
        catch (OperationCanceledException ex)
        {
            return new RunOutcome(RunStatus.Cancelled, FriendlyErrors.Describe(ex), null);
        }
        catch (Exception ex) when (ex is UnpakzException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new RunOutcome(RunStatus.Failed, FriendlyErrors.Describe(ex), null, (ex as UnpakzException)?.Status);
        }
    }

    private static string Summarize(ExtractResult r, string destination)
    {
        var inv = CultureInfo.InvariantCulture;
        var text = r.Files == 1 ? "1 file extracted to " + destination + "." : r.Files.ToString(inv) + " files extracted to " + destination + ".";
        if (r.Skipped > 0) text += " " + r.Skipped.ToString(inv) + " skipped (links, special files or files that already existed).";
        return text;
    }
}
