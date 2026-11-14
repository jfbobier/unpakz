# Status (2026-11-14) – v0.2.0, EXPERIMENTAL

Architecture (user decisions, `docs/decisions/0003`, `0004`, `0005`): Windows 11 only; C++ backend DLL (`unpakz_native.dll`, MSVC) that does all archive work
(libarchive + our RAR5 reader); .NET Framework 4.8 front ends (WinForms app, CLI) over a netstandard2.0 interop layer; ZIP/ZIP64 (>2 GiB), tar.gz, RAR4,
RAR5; BSD-2-Clause. The C# net10 engine of 0.1.0 is retired.

## Measured (all run on 2026-11-14)

| Check | Result |
|---|---|
| `./harness.sh fast` (WSL) | native tests (optimised build) pass; managed build with warnings as errors; 68 managed tests over the real native library pass |
| `./harness.sh native` (WSL, ASan + UBSan, warnings as errors) | 7 native test programs pass: guard 15, engine 41, API 13, volumes 7, rar5 fixtures 11, rar5 container/volumes 56, rar5 decoder 55 |
| `.\harness.ps1 fast` (Windows 11, MSVC Release `/W4 /WX`, .NET Framework 4.8 and .NET 10 test hosts) | the same 7 native programs pass; 68 managed tests pass; 21 Windows tests pass (COM data object, registry, real windows at 4 font scales x 3 sizes in 5 states, real button clicks) |
| Packaged zips smoke-tested from a clean folder (`scripts/package.ps1`) | CLI extracted zip (non-ASCII name), tar.gz and a solid RAR5, `t` and `l` work; the app starts with an archive argument and keeps running |
| Differential conformance (`harness/conformance.py`) | fixtures, freshly generated `rar -ma5` (m0-m5, solid, md4m, filters, multi-volume), zip, 7z-zip, tar.gz identical to `unrar` / 7-Zip / `tar` |
| Third-party corpus (102 RAR vectors incl. libarchive's security regressions) | each file: same result as `unrar`, or a typed refusal, or a documented decision (`KNOWN` in the script); no crash, no hang |
| Native mutation catalogue | 210 mutants; each killed or listed in `EQUIVALENT` with its reason (20). Guard/engine mutants were judged on the ASan build, RAR5 mutants on an optimised build (three sanitizer-only ones checked on ASan) |
| ZIP64 | an opt-in test extracts a 5 GiB entry (`UNPAKZ_BIG=1`) |

## Honest caveats

* Real OLE drag into Explorer is **not verified** (injected input cannot start a drag on the test machine). The data object is tested through
  `IDataObject`/`IStream`, and entries stream through `unpakz_read_entry`.
* The guard/engine native mutants were not rerun after the RAR5 work touched the reader plumbing in `engine.cpp`; the engine tests were, and pass.
* The managed C# mutation catalogue (379 mutants) was retired with the C# engine; the new managed layer has tests but no mutation run yet.
* The managed unit tests run against the native library through P/Invoke; the Windows UI tests ran on this one machine.
* Names without an encoding flag are read as UTF-8 (per-call UTF-8 thread locale); OEM-code-page zips are refused rather than guessed.
* RAR4 goes through libarchive; the VM-filter edge case `rar_filter_incomplete_block.rar` is refused. RAR4 files cannot be generated here
  (`rar` 7.x dropped `-ma4`): coverage is the 28 third-party vectors.
* Encryption is refused everywhere; RAR entries without any checksum are refused; links are never created.
* No performance benchmarks of the new backend yet (zlib-ng is SIMD; the RAR5 decoder is a straight port, untuned).
* Not done: SFX / prefixed archives, split ZIP, tar.bz2/xz/zstd, plain `.gz`, OEM-codepage retry for zip names, a "packed size" column.

## Next
1. A real drag-and-drop observation on a desktop session (or a UIA-driven test), and a benchmark pass on large archives (benchmark first, per CLAUDE.md rule 5).
2. A mutation run for the managed layer, and an OEM-codepage retry for old zips.
