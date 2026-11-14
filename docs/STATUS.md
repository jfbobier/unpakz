# Status (2026-11-14) – EXPERIMENTAL, mid-migration to the native backend

Direction (user, 2026-11-14; `docs/decisions/0003`, `0004`): Windows 11 only; a C++ DLL (`native/`, MSVC, libarchive + our RAR5 reader) does all
archive work; front ends are .NET Framework 4.8 (UI and CLI); formats ZIP/ZIP64 (>2 GiB), tar.gz, RAR4, RAR5; licence BSD-2-Clause.

## Where things are

| Piece | State |
|---|---|
| `native/` backend DLL (`unpakz.dll`, 7 exported C functions, ABI v1) | **works**; built with MSVC on Windows and clang/gcc on WSL |
| Formats | ZIP (stored/deflate, ZIP64 verified with a 5 GiB entry), tar.gz, RAR4 (libarchive), RAR5 (our reader: stored, methods 1-5, solid, filters, multi-volume, BLAKE2sp) |
| Guard, limits, single-write sink, pre-flight pass, selection filter, cancel, progress | in `native/src` (guard.cpp, engine.cpp, fs_native.cpp), C ABI in `api.cpp` |
| Managed front ends | **not migrated yet**: the released 0.1.0 app/CLI still run on the retired C# net10 engine. `src/Unpakz.Interop` (net48 P/Invoke layer) is written but not yet built or tested |
| Drag-out of virtual files | needs one more native call (stream one entry); not written yet |
| C# net10 Core/Engine | kept as the comparison oracle until the native backend passes the same corpus, then retired (decision 0003.6) |

## Measured

| Check | Result |
|---|---|
| `./harness.sh native` (ASan + UBSan, warnings as errors) | 7 test executables pass (~60 s): guard 15, engine 41, API 11, volumes 7, rar5 fixtures 10, rar5 container/volumes 56, rar5 decoder 55 tests |
| MSVC on Windows (`scripts/native-win.ps1`, Release, `/W4 /WX`, static CRT) | the same 7 pass |
| `unpakz.dll` called from Windows PowerShell (CLR 4.0, i.e. .NET Framework) via P/Invoke (`scripts/dll-smoke.ps1`) | ABI 1, extracted a zip with a non-ASCII name |
| Differential conformance (`harness/conformance.py`) | all fixtures, freshly generated rar -ma5 (m0-m5, solid, md4m, filters, multi-volume), zip, 7z-zip, tar.gz identical to `unrar` / 7-Zip / `tar` |
| Third-party corpus (102 RAR vectors incl. libarchive's security regressions) | every file: same result as `unrar`, or a typed refusal, or a documented decision (`KNOWN` in the script); no crash, no hang |
| Native mutation catalogue | 210 mutants (guard, engine, RAR5 container, decoder, filters); every mutant killed or listed in `EQUIVALENT` with its reason (20) |
| C# side (unchanged since 0.1.0) | 677 + 153 tests, 379 mutants |

Honest caveats
* The native mutation run was done in pieces while the catalogue grew; the 129 RAR5 mutants were judged on an optimised (non-sanitizer) build, the
  three that only a sanitizer can see were checked on the ASan build. The guard/engine mutants were judged on the ASan build earlier and not rerun after
  the RAR5 work touched `engine.cpp`'s reader plumbing (the engine tests did rerun and pass).
* Names without an encoding flag are read as UTF-8 (libarchive `hdrcharset=UTF-8`, with a per-call UTF-8 thread locale). Old zips in an OEM code
  page are refused rather than guessed. A retry with the OEM code page is the planned fix.
* RAR4 goes through libarchive: its VM-filter edge case `rar_filter_incomplete_block.rar` is refused (unrar extracts it). RAR4 archives cannot be
  generated here (`rar` 7.x dropped `-ma4`), so RAR4 coverage is the 28 third-party vectors.
* Encryption is refused everywhere (typed `Unsupported`). RAR entries without any checksum are refused. Links are never created.
* Not done: SFX / prefixed archives, tar.bz2/xz/zstd, parallel pipeline, the C++ hardware CRC (zlib-ng's `crc32` is used, already SIMD), windowed
  access for archives that do not fit in memory is not needed (files are read positionally) but has not been stress-tested beyond the 5 GiB zip.

## Next, in order
1. Native: `unpakz_read_entry` (push the bytes of one entry to a callback) for drag-out; OEM-code-page retry for zip names.
2. Managed: build `Unpakz.Interop` (net48), add the entry-stream bridge, retarget `Unpakz.Presentation`, the WinForms app, the COM layer and the CLI to
   net48 over the interop layer; port the view-model/UI matrix tests; run them against the native DLL.
3. Retire the C# Core/Engine once the managed tests pass on the native backend; publish `0.2.0` (x64, unpakz.dll + net48 exe, nothing to install).
