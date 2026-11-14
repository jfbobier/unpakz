# Status (2026-11-14) – EXPERIMENTAL

Measured on the build in this repository: Linux/WSL (.NET 10.0.114) and Windows (.NET 10, built in a copy under Downloads).

| Check | Result |
|---|---|
| `./harness.sh fast` (Linux) | 676 tests pass, warnings-as-errors build |
| Windows solution (`Unpakz.Windows.slnx`) | the same 676 + 153 Windows-only tests pass (COM data object, registry, UI matrix and clicks) |
| Mutation catalogue | 372 mutants; full strict run on 2026-11-14: Linux 334 (313 killed, 16 survived of which 10 are justified equivalents, 5 did not compile), Windows COM 5 and UI 12 |
| After triage | every unaccepted survivor now has a test and is killed (re-run individually); every uncompilable mutant was repaired and re-run; 10 equivalents are listed with reasons in `harness/mutations.py` |

Honest caveats:
* The mutation runner now refuses to judge on a red baseline (a bug of mine produced one false "kill" before that). One Windows
  re-run of `VD3` reported a survivor once and did not reproduce in four further runs; the cause was not found (suspected stale
  incremental build in the Windows copy).
* The full catalogue was run in one pass *before* the triage tests were added; the triage mutants were re-run individually, not the
  whole catalogue again.
* Real OLE drag into Explorer is **not verified**: the injected input in this environment cannot start a standard drag. The data
  object is tested through `IDataObject`/`IStream` exactly as Explorer calls it, and an opt-in end-to-end test exists but self-skips.
* Windows: only the framework-dependent build was tested; no installer, no signing.

## Works
ZIP/ZIP64 (stored, deflate); RAR5 (stored and methods 1-5, solid, filters, multi-volume); single-write extraction (temp file in the
destination folder + rename); PathGuard and pre-flight conflict checks; CLI (`l`, `x`, `t`); WinForms app (extract all / here /
to named folder, file list with selection, drag-out of virtual files); per-user "Default apps" registration plan.

## Not done
RAR4, AES (ZIP and RAR), BLAKE2sp-only integrity, SFX/prefixed archives, split ZIP, Deflate64/bzip2/LZMA/zstd; archives over 2 GiB
as a single file (clear error, use volumes); per-part CRCs of non-final RAR volumes; parallel pipeline; decoder tuning.
Benchmarks and the Vulkan experiment: see `bench/` once present.
