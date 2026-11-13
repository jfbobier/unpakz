# Unpakz plan

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
Unpakz.Core       pure decoders + format parsers (ZIP, ZIP64, RAR5, later RAR4). No I/O, no OS.
Unpakz.Engine     extraction pipeline: planner -> parallel workers -> sink. PathGuard, limits, progress.
Unpakz.Windows    sinks (pre-sized files, overlapped I/O, no-buffering for big files), long paths, ADS/reserved-name rules, file times/attrs.
Unpakz.Gpu        Vulkan compute experiment (separate assembly, optional, behind a gate).
Unpakz.Cli        thin CLI over Engine.
Unpakz.App        WinForms GUI (open, list, extract here, extract to...; Explorer verb via file association).
tests/          Unpakz.Tests (unit/property), Unpakz.Conformance (corpus + differential), Unpakz.Fuzz, Unpakz.Security
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
3. **Differential testing**: extract the same archive with Unpakz and 7-Zip; compare the tree (names, bytes, sizes, mtimes, attributes) bit-for-bit.
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

## M1 status
Done: ZIP/ZIP64 central directory parser, entry extractor (stored + deflate via zlib-ng baseline, CRC-32 slicing-by-8, 1032:1 ratio bound,
exact-size + CRC verification, bounded writes), IOutputSink, 7-Zip differential tests (Copy, Deflate -mx1/5/9, 20 MB + 2000 small files, Unicode names).
132 tests; every guard mutation-verified (parser 5/5, extractor 11/11 after adding 3 tests for survivors, CRC 2/2, differential 2/2).
Known gaps / next: engine pipeline (real file sink, PathGuard integration, case-collision/duplicate/reparse handling, overlap detection),
CLI, I/O proofs (single write, no temp dir), SFX-prefixed archives, Unicode path extra (0x7075), AES, Deflate64, hardware CRC (needs benchmark first).

## M1 status update: engine pipeline + security suite
Done: ExtractionPlanner (pure, I/O-free, refuses before any write), ArchiveExtractor (plan -> create dest -> per-file link check -> decode into same-dir
temp file -> verify size+CRC -> rename), IFileSystem with LocalFileSystem + test InMemoryFileSystem (operation log), DosTime, cancellation, progress.
Security rules enforced and tested: traversal/rooted/UNC/drive-relative, reserved device names, ADS, trailing dot/space, case-insensitive duplicates,
file/dir collisions, symlink + special entries skipped (never created), pre-existing symlink/junction in destination path refused, symlink at final
path replaced not followed, overlap detection (header+data ranges), total size/path length/entry limits, hostile preallocation not trusted,
late-appearing file never overwritten (race), failure never leaves temp files, nothing ever outside destination (sweeps).
Proofs: bytes written == bytes extracted; temp file always in the final directory (rename, not copy); no scratch directories; same on real FS.
Harness: harness/mutate.py + harness/mutations.py (62 mutants, all killed; run by `./harness.sh full`). Differential vs 7-Zip: archive creation and full-tree extraction.
Not yet: parallel execution, Windows sink (handle-relative opens to close the check/use window on directories), 8.3 short-name aliases,
NTFS junction handling verified on Windows, CLI, benchmarks.

## Decisions (2026-11-13, later)
- Filenames: PathGuard stays the primary, pure, cross-platform gate (testable on Linux). Windows native APIs are a SECOND layer in Unpakz.Windows:
  after creating each file/dir the OS is asked what it actually resolved (GetFinalPathNameByHandle) and the result must be under the destination;
  this catches junctions, 8.3 short names and anything PathGuard missed. We do not delegate validation to Path.GetFullPath/Win32 normalisation, because
  those silently rewrite (collapse `..`, strip trailing dots/spaces), which is exactly the class of attack being defended.
- Format abstraction: IArchive/ArchiveEntry; planner + executor are format-neutral. ArchiveReader detects RAR5 by signature, else ZIP.

## RAR status
Done: RAR5 container (all block types parsed, header CRC32 verified, vint, extras: time/redirection/encryption/hash), stored entries, kinds
(file/dir/symlink via redirection or Unix mode/special), timestamps, solid-flag archives with stored data, limits, hostile-header hardening
(wrap-around sizes, backward jump loop, oversize headers). Differential vs real rar 7.23 / unrar (stored, solid, symlinks, -p, -hp, -htb, -m3 parse).
115 mutants (46 new for RAR), all killed.
Reported Unsupported (explicitly, before any write): compressed methods, encrypted files/headers, BLAKE2-only integrity, multi-volume, unknown size.
Next: RAR5 decoder (LZ + Huffman, filters delta/E8/E8E9/ARM, solid windows); reference = libarchive's BSD-2 RAR5 reader (fetch, read as data, credit);
accept only when every `rar -m1..-m5` x dictionary x solid archive matches `unrar` output byte for byte. Then BLAKE2sp, AES-256; then RAR4 (VM filters, PPMd H).
Also pending: SFX / prefixed archives, multi-volume.
