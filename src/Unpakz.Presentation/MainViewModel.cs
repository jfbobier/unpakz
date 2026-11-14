using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unpakz.Core;
using Unpakz.Engine;

namespace Unpakz.Presentation;

/// <summary>
/// Everything the main window does, with no UI in it: open an archive, extract all / here / to a named folder / a selection, report
/// progress, cancel, prepare a drag. The windows only bind to this and forward clicks.
/// </summary>
public sealed class MainViewModel : IDisposable
{
    private readonly IFolderPicker _picker;
    private readonly IPrompt _prompt;
    private readonly Func<string, ArchiveSource> _opener;
    private readonly IFileSystem? _fileSystem;
    private CancellationTokenSource? _cts;
    private string _status = "Open or drop a .zip or .rar file.";
    private double _progress;
    private bool _busy;

    public MainViewModel(IFolderPicker picker, IPrompt prompt, Func<string, ArchiveSource>? opener = null, IFileSystem? fileSystem = null)
    {
        _fileSystem = fileSystem;
        _picker = picker ?? throw new ArgumentNullException(nameof(picker));
        _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
        _opener = opener ?? (path => ArchiveSource.Open(path));
    }

    /// <summary>Raised with the property name after a property changes. May be raised from a worker thread: views must marshal.</summary>
    public event Action<string>? Changed;

    public string? ArchivePath { get; private set; }

    public ArchiveSource? Source { get; private set; }

    public bool IsOpen => Source is not null;

    /// <summary>File name, then <see cref="Details"/>.</summary>
    public string Summary { get; private set; } = string.Empty;

    /// <summary>Format, number of files, size, parts: everything except the name (which the window shows on its own).</summary>
    public string Details { get; private set; } = string.Empty;

    public RunStatus? LastStatus { get; private set; }

    public string Status
    {
        get => _status;
        private set { _status = value; Changed?.Invoke(nameof(Status)); }
    }

    public double Progress
    {
        get => _progress;
        private set { _progress = value; Changed?.Invoke(nameof(Progress)); }
    }

    public bool IsBusy
    {
        get => _busy;
        private set { _busy = value; Changed?.Invoke(nameof(IsBusy)); }
    }

    public bool TryOpen(string path)
    {
        Close();
        try
        {
            var source = _opener(path);
            Source = source;
            ArchivePath = Path.GetFullPath(path);
            Details = Describe(source);
            Summary = Path.GetFileName(ArchivePath) + " · " + Details;
            LastStatus = null;
            Status = "Ready.";
            Changed?.Invoke(nameof(Summary));
            Changed?.Invoke(nameof(IsOpen));
            return true;
        }
        catch (Exception ex) when (ex is ArchiveException or IOException or UnauthorizedAccessException)
        {
            Status = FriendlyErrors.Describe(ex);
            Changed?.Invoke(nameof(IsOpen));
            return false;
        }
    }

    private static string Describe(ArchiveSource source)
    {
        var inv = CultureInfo.InvariantCulture;
        var files = source.Archive.Entries.Count(e => e.Kind == EntryKind.File);
        var size = source.VolumePaths.Sum(p => new FileInfo(p).Length);
        var text = string.Create(inv, $"{source.Archive.Format.ToUpperInvariant()} · {files} files · {FormatSize(size)}");
        if (source.VolumePaths.Count > 1) text += string.Create(inv, $" · {source.VolumePaths.Count} parts");
        return text;
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return v.ToString(u == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[u];
    }

    // ---- operations ----
    public Task ExtractAllAsync() => PickAndRunAsync("Extract all files to", null);

    public Task ExtractSelectedAsync(IReadOnlyCollection<int> entryIndexes)
    {
        ArgumentNullException.ThrowIfNull(entryIndexes);
        if (entryIndexes.Count == 0)
        {
            Status = "Select one or more files first.";
            return Task.CompletedTask;
        }

        return PickAndRunAsync("Extract the selected files to", entryIndexes);
    }

    public Task ExtractHereAsync() => RunAsync(DestinationNaming.DirectoryOf(RequirePath()), null);

    public Task ExtractToNamedFolderAsync() => RunAsync(DestinationNaming.FolderNextTo(RequirePath()), null);

    private async Task PickAndRunAsync(string title, IReadOnlyCollection<int>? selection)
    {
        if (!IsOpen || IsBusy) return;
        var destination = _picker.Pick(title, DestinationNaming.DirectoryOf(RequirePath()));
        if (string.IsNullOrWhiteSpace(destination)) return;
        await RunAsync(destination, selection).ConfigureAwait(false);
    }

    private string RequirePath() => ArchivePath ?? throw new InvalidOperationException("No archive is open.");

    private async Task RunAsync(string destination, IReadOnlyCollection<int>? selection)
    {
        if (!IsOpen || IsBusy) return;
        var context = SynchronizationContext.Current;      // dialogs must be shown on the thread that started the operation
        IsBusy = true;
        Status = "Extracting…";                  // before the first progress event, so no listener sees "Ready." while busy
        Progress = 0;
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new ImmediateProgress<ExtractionProgress>(p => Progress = p.TotalBytes > 0 ? Math.Min(1.0, (double)p.BytesWritten / p.TotalBytes) : 0);
            var outcome = await ExtractionRunner.RunAsync(Source!.Archive, destination, selection, null, progress, _fileSystem, _cts.Token).ConfigureAwait(false);

            if (outcome.Status == RunStatus.Failed && outcome.Error == ArchiveError.Conflict
                && Ask(context, $"Some files already exist in {destination}. Replace them?"))
            {
                Progress = 0;
                outcome = await ExtractionRunner.RunAsync(Source.Archive, destination, selection, new ExtractOptions { Overwrite = OverwritePolicy.Overwrite }, progress, _fileSystem, _cts.Token).ConfigureAwait(false);
            }

            if (outcome.Status == RunStatus.Success) Progress = 1;
            LastStatus = outcome.Status;
            Status = outcome.Message;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }

    private bool Ask(SynchronizationContext? context, string message)
    {
        if (context is null) return _prompt.ConfirmReplace(message);
        var answer = false;
        context.Send(_ => answer = _prompt.ConfirmReplace(message), null);
        return answer;
    }

    public void Cancel() => _cts?.Cancel();

    // ---- drag out ----
    /// <summary>The files a drag would carry (null = everything), or null if the archive cannot be dragged (reason in <see cref="Status"/>).</summary>
    public VirtualFileList? PrepareDrag(IReadOnlyCollection<int>? entryIndexes)
    {
        if (!IsOpen) return null;
        try
        {
            return VirtualFileList.Build(Source!.Archive, entryIndexes);
        }
        catch (ArchiveException ex)
        {
            Status = FriendlyErrors.Describe(ex);
            return null;
        }
    }

    /// <summary>A forward-only stream with the decoded bytes of one dragged file. Nothing is written to disk.</summary>
    public Stream OpenDragStream(VirtualFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var archive = Source?.Archive ?? throw new InvalidOperationException("No archive is open.");
        return EntryStream.Open(archive, archive.Entries[file.EntryIndex]);
    }

    private void Close()
    {
        Source?.Dispose();
        Source = null;
        ArchivePath = null;
        Summary = string.Empty;
        Details = string.Empty;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        Close();
        Changed?.Invoke(nameof(IsOpen));
    }

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
