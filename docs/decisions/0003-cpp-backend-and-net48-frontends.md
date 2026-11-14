# 0003 – C++ backend DLL (libarchive-based) with .NET Framework 4.8 front ends

Date: 2026-11-14. Supersedes the "net10, pure-C# decoders" decisions in `docs/PLAN.md`.

## Decision (user, 2026-11-14)
1. Windows only, Windows 11 as the baseline. (Windows already ships `tar.exe`, i.e. bsdtar; our value is the safety guard, no double write,
   selection + drag-out, RAR and archives over 2 GiB.)
2. `unpakz.dll` (C++, Win32 APIs) holds all archive logic, limits, `PathGuard` and the file sink. Front ends are .NET Framework 4.8
   (in the box on Windows 11): the WinForms UI and the CLI, calling a flat C API by P/Invoke. The guard is authoritative in C++;
   the managed side re-checks names it displays or hands to Explorer (defence in depth, never the only check).
3. Formats: ZIP/ZIP64 (including > 2 GiB), tar + gzip, RAR4 and RAR5, all streaming.
4. The format readers are **libarchive** (the library behind bsdtar), vendored read-side only. Our own C++ RAR5 decoder (port of the verified
   C# one) is kept behind a switch and compared with libarchive's in tests ("hybrid").
5. Licence follows libarchive: BSD-2-Clause (see 0004).
6. The C# net10 Core/Engine is retired once the C++ backend passes the same corpus (fixtures, 7-Zip/rar oracles, corruption sweeps,
   security suite). It stays in the tree until then as the comparison oracle.

## Consequences / rules carried over
* CLAUDE.md rules still hold: guard chokepoint (we never use libarchive's `archive_write_disk`; only our sink opens output files),
  one write per byte (temp *file* in the target dir + rename), hostile metadata (limits wrap libarchive), typed errors, benchmark
  before any optimisation claim, no code without a failing test.
* Portable core: everything except the sink and Windows shims builds and tests on WSL (clang/g++, CMake) with ASan/UBSan and
  libFuzzer. libarchive's RAR4 reader (PPMd, VM filters) has a CVE history, so fuzzing it is the first-class harness item.
* The mutation runner is text-based and carries over (build/test command becomes CMake/ctest); mutants for C++ replace the C# ones.
* Front ends: UI view-models, the UI matrix and the screenshot checks are retargeted to net48 (netstandard2.0 libraries are acceptable;
  new NuGet dependencies need the usual name/purpose/licence statement).
* The hardware CRC (PCLMULQDQ, ~8x, see bench/) is re-implemented in C++ before any "fast" claim is made for the new backend.
