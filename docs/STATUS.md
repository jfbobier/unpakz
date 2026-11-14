# Status (2026-11-14) – EXPERIMENTAL

Everything below was built and tested on Linux (WSL, .NET 10.0.114). **Nothing has been compiled or run on Windows.**
Verified = covered by the test suite (494 tests, `./harness.sh fast`, ~15 s) and, where stated, the mutation harness.

## Works and is verified
| Area | Verification |
|---|---|
| ZIP / ZIP64 read: stored + deflate, UTF-8/CP437 names, CRC-32 | unit + corruption sweeps + 7-Zip differential; parser/extractor mutants all killed |
| Extraction engine: plan-then-write, PathGuard, collisions, links skipped, overwrite policies, cancellation, progress | in-memory + real-FS tests; mutants all killed |
| Single-write proof: temp *file* in the target directory + rename, no temp directories | counting file system tests (in-memory and real) |
| RAR5 container: headers, CRCs, extras, kinds, timestamps, solid flag | real `rar`/`unrar` oracle; mutants all killed |
| RAR5 decoder: methods 1-5, solid groups, delta/x86/ARM filters, any dictionary, lazy window | real `rar` 7.23 oracle (forced filters, wrap, long range), handcrafted bitstreams, committed fixtures + corruption sweeps ("never silently wrong data"); 111 mutants killed or justified-equivalent |
| RAR5 multi-volume sets (stored, compressed, solid, filters across volumes) | real `rar -v` sets vs source files; builder-made hostile sets; fixtures + sweeps |
| File-level API: `ArchiveSource`, `VolumeLocator` (`name.partN.rar`), memory-mapped `MappedFile` | unit tests; live-mapping counter proves release on failure paths |

## Known test gaps (from the last mutation run – not yet closed)
- Multi-volume batch (47 mutants): 38 killed, **4 survived** – `MV10` (volume out-of-order check when number < expected),
  `MV11` (continuation must start with the split-before flag), `VL3` (locator ignores sets with a different base name),
  `MF7` (mapped view pointer release) – and 5 catalogue entries do not compile (`MV8`, `MV19`, `MV23`, `MF4`, `AS2`): catalogue bugs.
- Only the decoder batch (111) and earlier batches were driven to zero unexplained survivors.
- `./harness.sh full` runs the whole catalogue (~270 mutants, ~1 h); it has not been run end-to-end since the last additions.

## Not done
- **WinForms app / Explorer integration**: not started. Planned: extract-all + file list with selection, folder picker or drag-out
  using *virtual files* (so Explorer pulls bytes from a stream and no temp copy is made), registration for "Default apps".
  Needed engine pieces (entry filter, streaming entry reader, friendly error text) are designed but not written.
- Windows layer: `Unpakz.Windows` sink (pre-sized/overlapped I/O), second-layer path check via `GetFinalPathNameByHandle`,
  8.3 short names / junctions – all untested territory.
- CLI, benchmarks vs 7-Zip on large files, parallel pipeline, hardware CRC, Vulkan experiment.
- Formats/features: RAR4 (VM filters, PPMd), AES (ZIP and RAR), BLAKE2sp-only integrity, SFX / prefixed archives, split ZIP,
  Deflate64/bzip2/LZMA/zstd, ZIP Unicode-path extra field.
- **Single archive files larger than 2 GiB are refused** (`ReadOnlyMemory<byte>` limit; clear error). Multi-volume sets are not
  affected per volume. Windowed access is the planned fix and the top item for the large-file milestone.
- Per-part CRCs of non-final RAR volumes are not checked (the whole-file CRC is).
- Decoder speed is unoptimised (≈ tens of MB/s on literal-heavy data); no tuning has been done by design (benchmark first).

## Equivalent mutants (justified in `harness/mutations.py`)
RD44, RD80, RD81, RD82, RD86, RD88, RD90 – each with its reason in the file.
