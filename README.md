# unpakz

> **Experimental (v0.1.0).** Written test-first with AI assistance as a harness-engineering exercise. It has been tested on Linux/WSL
> and on Windows 11 (automated tests only, no field use). Real drag-and-drop into Explorer has **not** been observed working
> (see [Known limitations](#known-limitations)). Review the code before trusting it with anything important: archive extraction is
> a security boundary.

An extraction-only ZIP and RAR tool for Windows, built on .NET 10. It does not create archives.

## What it does

* **Formats**: ZIP / ZIP64 (stored, deflate), RAR 5 (stored and compressed methods 1-5, solid archives, delta / x86 / ARM filters,
  multi-part sets `name.part1.rar …`).
* **No temporary folder.** Each file is decoded straight into a hidden temp *file* in its destination folder and renamed when its size
  and CRC check out, so every byte is written once. A failed file leaves nothing behind. Dragging files out hands Explorer a
  stream of decoded bytes instead of extracting to a scratch folder first.
* **Refuses hostile archives before writing anything**: `..` traversal, absolute / UNC / drive paths, reserved device names (`CON`,
  `NUL.txt`, `COM¹` …), alternate data streams, trailing dots, case collisions, files that overlap inside the archive, symlinks and
  special files (skipped and reported), zip bombs (size and ratio limits), oversized dictionaries, and existing files (it asks first).
* **Windows app**: open or drop an archive; *Extract to…* (folder picker), *Extract to “name\”*, *Extract here*; a **file list** with
  search, sort, multi-select (picking a folder takes its contents) and *Extract selected to…*; drag either onto an Explorer folder.
  Progress and cancel. Optional registration in *Open with* / *Default apps* (per user, no admin).
* **Command line** (`unpakz`): `l` list, `t` test (decode everything, write nothing), `x` extract (all or named paths; `-o`, `--here`,
  `--skip`, `--overwrite`). Works on any OS with .NET 10.

![main window](docs/img/main.png) ![file list](docs/img/list.png)

## Install and use

Requires the **.NET 10 Desktop Runtime** (Windows) — the first launch shows Microsoft's download prompt if it is missing.

1. Download `unpakz-app-win-x64.zip` from the release page and unzip it anywhere.
2. Run `Unpakz.exe`, then drop a `.zip` or `.rar` on the window, or use *Open another archive…*.
   `Unpakz.exe path\to\file.zip` opens it directly; so does choosing Unpakz in *Open with*.
3. Optional: *Show in Open with / Default apps* (bottom of the window), then pick Unpakz for `.zip` / `.rar` in Windows Settings.

Command line (`unpakz` from `unpakz-cli-win-x64.zip`, or `dotnet run --project src/Unpakz.Cli -- …`):

```
unpakz l backup.zip                     list
unpakz t set.part1.rar                  test a multi-part RAR set (all parts in one folder)
unpakz x backup.zip -o D:\restore       extract
unpakz x backup.zip docs readme.txt     extract only these paths (a folder takes its contents)
```

Exit codes: 0 ok, 1 error (message explains), 2 usage, 130 cancelled.

## How it is verified

The verification harness is the main deliverable. See **[HARNESS.md](HARNESS.md)** for commands, layers and the mutation harness.

* 677 automated tests (Linux and Windows) plus 153 Windows-only tests: COM data object, registry, and a UI matrix (6 window states ×
  4 font scales × 3 sizes with screenshots, real button clicks over 6 archive formats).
* Outputs are compared with 7-Zip and with RARLAB's `rar` / `unrar` (stored, methods 1-5, solid, forced filters, multi-volume).
* 379 mutations of the code are applied one at a time; each must make a test fail (the few justified equivalents and the caveats are
  listed in [docs/STATUS.md](docs/STATUS.md)).
* Corruption sweeps flip every byte and truncate at every length of real archives: the result must be an `ArchiveException` or the
  original bytes, never silently wrong data.

## Known limitations

* **Drag-and-drop into Explorer is unverified.** The data object is tested through its COM interfaces; but injected mouse input could not
  start even a standard WinForms drag on the test machine, so no drop was observed. *Extract to…* is the verified path.
* A single archive file larger than 2 GiB is refused with a clear message (a .NET memory limit); multi-part sets are fine per part.
* Not supported yet: encrypted archives (ZIP or RAR), RAR 4, BLAKE2-only RAR checksums, self-extracting stubs, split ZIP,
  Deflate64 / bzip2 / LZMA / zstd.
* Performance has not been tuned (see `docs/`): it is correct first. I/O will usually dominate.
* Windows-specific path rules (junctions, 8.3 names) rely on `PathGuard` plus the pre-existing-link check; the planned
  `GetFinalPathNameByHandle` second layer is not implemented.

## Layout

| Project | Role |
|---|---|
| `src/Unpakz.Core` | pure parsers and decoders (ZIP, RAR5): bytes in, bytes out, no file or OS access |
| `src/Unpakz.Engine` | planner, extractor, `PathGuard`, file systems, streaming reader, selection, volume lookup, memory-mapped files |
| `src/Unpakz.Presentation` | view-models for both windows (UI-free, fully testable) |
| `src/Unpakz.Cli` | the command line |
| `src/Unpakz.Windows` | COM virtual-file drag source, `IStream` bridge, registry registration (Windows only) |
| `src/Unpakz.App` | the WinForms windows (Windows only) |
| `tests/`, `harness/`, `HARNESS.md` | the verification harness |

## Build and test

```
./harness.sh fast                      # Linux / WSL: build with warnings as errors + all tests (~20 s)
.\harness.ps1 fast                     # Windows: also builds the app and runs the UI tests
dotnet publish src/Unpakz.App -c Release -r win-x64 --no-self-contained
```

## Credits and licence

MIT, © 2026 Jean-Francois Bobier — see [LICENSE](LICENSE). The RAR 5 compression algorithm was implemented following libarchive's BSD-2
reader (credited in [docs/THIRD-PARTY.md](docs/THIRD-PARTY.md)); no RARLAB or 7-Zip RAR source was used.
