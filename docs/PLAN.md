# Zipx plan

## Goal
Fast, small, safe, extraction-only archive tool for Windows (ZIP/ZIP64 first, RAR5 then RAR4), with a minimal GUI and
default-app association. Target machine: Ryzen 9800X3D (Zen 5, AVX-512), 64 GB RAM, Nvidia GPU, NVMe.

## What "efficient" means (measurable)
| Metric | Gate |
|---|---|
| Bytes written to disk per extracted byte | exactly 1.0 (verified) |
| Temp directories created | 0 (verified) |
| Many-small-files extraction (e.g. 100k files) | >= baseline 7-Zip, target 1.5x |
| Large single-file store/deflate extraction | within 10% of disk write throughput |
| Cold start to first window / CLI | CLI < 50 ms, GUI < 200 ms |
| Install size | < 5 MB framework-dependent, < 30 MB self-contained trimmed |

## Architecture
```
Zipx.Core       pure decoders + format parsers (ZIP, ZIP64, RAR5, later RAR4). No I/O, no OS.
Zipx.Engine     extraction pipeline: planner -> parallel workers -> sink. PathGuard, limits, progress.
Zipx.Windows    sinks (pre-sized files, overlapped I/O, no-buffering for big files), long paths, ADS/reserved-name rules, file times/attrs.
Zipx.Gpu        Vulkan compute experiment (separate assembly, optional, behind a gate).
Zipx.Cli        thin CLI over Engine.
Zipx.App        WinForms GUI (open, list, extract here, extract to...; Explorer verb via file association).
tests/          Zipx.Tests (unit/property), Zipx.Conformance (corpus + differential), Zipx.Fuzz, Zipx.Security
bench/          BenchmarkDotNet suites + committed results
```

## Pipeline design
1. Parse the central directory (ZIP) from the end; memory-map the archive. Validate everything (offsets in range, no overlaps, counts vs size).
2. Plan: build the entry list, run `PathGuard` on every name, pre-compute directories, create them once, sort/batch for locality.
3. Execute: one worker per core; each worker decodes an entry straight into its final file (pre-sized with SetEndOfFile, so no fragmentation). Large entries split into chunks only when the format allows it (stored; independent deflate blocks via sync points are not generally available, so large deflate entries run on a single core while other entries run in parallel).
4. CRC32 with PCLMULQDQ/VPCLMULQDQ folding, verified before the rename/commit; failure deletes the partial file.
5. RAR: non-solid archives parallelize per file; solid archives are sequential by design (LZ window dependency); pipeline decode and write on separate threads.
6. 3D V-Cache: keep per-worker working sets (window + buffers) within L3 slices; measure with ETW/`perf` counters before tuning.

## Deflate decision
.NET 9+ ships zlib-ng, which is already SIMD-optimized. We start from `System.IO.Compression` as the baseline, and write our own inflate only if
benchmarks show a measurable win (candidate: avoiding stream/copy overhead with span-based, whole-entry inflate into the final buffer). RAR has no
built-in equivalent: the RAR5 decoder (LZ + Huffman + filters: delta, x86 E8/E9, ARM) is written clean-room from the public technical notes. We do not use or copy `unrar` source (license forbids it for this purpose).

## GPU (Vulkan) – experiment, gated
Honest prior: Deflate/RAR LZ streams are serial; GPU decode needs many independent chunks and the PCIe copy often cancels the gain on a fast CPU.
Plan: (1) micro-benchmark CRC32/Adler on CPU (likely ~memory bandwidth) vs a Vulkan compute shader including upload cost; (2) only if (1) shows a win at
realistic sizes, try batched inflate of many independent small entries. Ship nothing GPU unless it passes the gate; otherwise document the negative result.
Vulkan binding: Silk.NET.Vulkan (MIT) or raw P/Invoke; shaders as precompiled SPIR-V checked in.

## The verification harness (the main exercise)
1. **Spec conformance**: PKWARE APPNOTE clauses mapped to test names; RAR5 notes likewise. Each clause -> at least one test.
2. **Corpus**: generated (synthetic, deterministic, via our own writer helpers and 7z/Info-ZIP on Windows) + external public vectors in `corpus/external/` with SHA-256 manifest. RAR files cannot be generated without the proprietary `rar`, so we need existing archives (WinRAR installed?) or public test vectors.
3. **Differential testing**: extract the same archive with Zipx and 7-Zip; compare the tree (names, bytes, sizes, mtimes, attributes) bit-for-bit.
4. **Security suite**: zip-slip (`..\`, absolute, UNC, drive-relative `C:foo`), reserved device names (CON, NUL, COM1), trailing dot/space, alternate data streams (`a.txt:evil`), case-collisions, symlink/reparse entries, duplicate names, overlapping entries, ZIP64 overflow fields, bogus counts, zip bombs (ratio and total-size limits), nested archives, long paths.
5. **Robustness**: truncate at every byte offset, flip every byte (sampled), property-based tests (FsCheck), coverage-guided fuzzing of the parsers (SharpFuzz/libFuzzer-style) with a stored regression corpus.
6. **I/O proofs**: a counting sink test (bytes written == bytes extracted), a file-system watcher/ETW test that proves no temp directory and no second write.
7. **Performance gates**: BenchmarkDotNet results committed; CI-style script fails on > 5% regression vs baseline.
8. **Quality gates**: line+branch coverage thresholds on Core/Engine, mutation testing (Stryker.NET) on parsers/PathGuard with a minimum score, warnings-as-errors.
9. **Harness script**: `harness.ps1 fast|full|bench|fuzz` as the one entry point, deterministic output summary.

## Milestones
- M0: repo, props, harness.ps1, CI-style gates, empty projects, one passing test. PathGuard + tests first.
- M1: ZIP stored+deflate, ZIP64, CLI, security suite, differential vs 7-Zip.
- M2: parallel pipeline + Windows sink, I/O proofs, first benchmarks vs 7-Zip.
- M3: GUI + file association (per-user, HKCU, no admin).
- M4: RAR5 non-solid, then solid, filters. Then RAR4 if wanted.
- M5: Vulkan gate experiment, documented go/no-go.
- M6: AES ZIP, Deflate64, bzip2/LZMA/zstd (decide later), signed build.

## Decisions (2026-11-13)
- Target net10.0, framework-dependent. Users without the runtime get the standard apphost "install .NET" prompt (accepted).
- GUI: WinForms. Engine/Core/tests build and run on WSL (Arch, dotnet-sdk 10.0.114); Windows (.NET 8/9/10 SDKs) for WinForms + Windows sink.
- Oracle: 7-Zip (`/usr/sbin/7z` on WSL, also on Windows) and `unrar` for RAR.
- Benchmarks emphasise large files (multi-GB) as well as many-small-files.
- RAR: target the most complex variant. RAR5 first (common), then RAR4 (RarVM filters, PPMd var.H, audio predictor). The RARLAB technote documents headers only, NOT the compression algorithm. Reference implementation for the algorithms: libarchive (BSD-2). Do NOT copy unrar/7-Zip RAR sources (unRAR license restriction).
- RAR fixtures: official RARLAB `rar` 7.23 (trial, linux-x64) fetched from rarlab.com, SHA-512 checked against the AUR PKGBUILD, unpacked to `tools/rar/` (gitignored, local-only, not redistributed, not installed system-wide so pacman's `unrar` stays). It generates RAR4 (`-ma4`) and RAR5 (`-ma5`) archives: solid, filters, PPMd (`-mc`), encrypted, large dictionaries.

## M0 status
Done: props (warnings-as-errors, nullable, analyzers), PathGuard + 63 tests, mutation sanity check (3/3 killed), harness.sh fast.
