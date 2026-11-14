# The verification harness

This project is a harness-engineering exercise: the checks matter as much as the code. The rules in `CLAUDE.md` are enforced by what
is described here. If you change behaviour, change the harness first.

## Commands

| What | Command | Time | Where |
|---|---|---|---|
| Build (warnings are errors) + all tests | `./harness.sh fast` | ~20 s | Linux / WSL |
| Same, plus every mutant | `./harness.sh full` | ~1.5 h | Linux / WSL |
| Check that every mutation still applies (no tests run) | `python3 harness/mutate.py --check` | 1 s | Linux / WSL |
| Run some mutants | `python3 harness/mutate.py =RD1,RD2` or `... RD` (substring of id or file) | ~15 s each | Linux / WSL |
| Windows build + all tests, incl. UI | `.\harness.ps1 fast` (PowerShell) | ~2 min | Windows |
| Run the Windows tree from WSL | `scripts/winsync.sh '<powershell command>'` | | WSL → Windows |
| One filtered Windows test run | `.\scripts\wintest.ps1 "DisplayName~Name" "<regex of lines to show>"` | | Windows |
| Real mouse-driven drag test (opt-in) | `$env:UNPAKZ_INTERACTIVE_TESTS=1` then run the Windows tests | | Windows, unlocked desktop |
| Regenerate the committed `.rar` fixtures | `UNPAKZ_REGEN_FIXTURES=1 dotnet test --filter Regenerate_fixtures` | | needs `tools/rar` |

`harness.sh` sets `UNPAKZ_REQUIRE_ORACLE=1`: a missing `rar`/`7z` then **fails** instead of silently skipping. Without it (plain
`dotnet test`, or on Windows where the tools are not on `PATH`) the oracle tests return early and count as passed without having run.
That is the one place where a green run can mean less than it says; the Windows run therefore proves the engine on NTFS, not the
differential comparisons.

## Layers

| Layer | What it proves | Examples |
|---|---|---|
| Unit | one rule, one outcome | `PathGuardTests`, `VIntTests`, `DosTimeTests`, `Crc32Tests`, `RarFilterTests` |
| Spec vectors by hand | arithmetic that cannot be validated by round-tripping | filter vectors worked out on paper |
| Randomised / exhaustive sweeps | no input makes the code misbehave | every truncation, every byte flip (`Every_*`), 200,000 random names, random token streams against a model |
| "Never silently wrong data" | corruption is either rejected with `ArchiveException` or yields the original bytes | `RarFixtureTests` sweeps over the committed archives |
| Differential vs 7-Zip | we extract what 7-Zip extracts | `DifferentialZipTests`, `DifferentialExtractionTests` |
| Differential vs `rar`/`unrar` | we decode what RAR decodes: methods 1-5, solid, forced filters, tiny and huge dictionaries, multi-volume | `RarCompressedOracleTests`, `RarOracleTests` |
| Test-side encoder | hostile bitstreams no real compressor would produce | `RarBlockBuilder` (canonical Huffman encoder + LZ model), `Rar5Builder`, `ZipBuilder` |
| Security suite | the engine refuses to be abused | traversal, reserved names, ADS, case collisions, symlinks, overlap, bombs, races (`ArchiveExtractorTests`, `LocalFileSystemTests`) |
| I/O proofs | one write per byte, no temp directories | counting file systems (in-memory and real) |
| View-model matrix | what the windows do, over 6 formats × operations × picker outcomes × failures | `UiMatrixTests` (Linux and Windows) |
| Windows UI matrix | the real forms lay out cleanly: 6 states × 4 font scales (100-200%) × 3 sizes, list window 6 × 3 × 2, real button clicks over 6 formats; PNG screenshots are written to `bin-win/.../ui-shots/` | `tests/Unpakz.Windows.Tests/UiTests.cs` |
| COM tests | the drag source behaves like a data object when Explorer calls it | `DataObjectTests` call `IDataObject` / `IStream` only through the interfaces |
| Real registry | the file-type registration lands in HKCU and is removable | `RegistryTests` (scratch key) |

Fixtures: `tests/Unpakz.Tests/Fixtures/` holds small archives made by the real `rar` 7.23 (single archives, solid, forced delta/x86
filters, a 256 MB declared dictionary, and three multi-volume sets). They let the corruption sweeps run on machines without `rar`.

### Setting up the oracles (Linux / WSL)

1. `7z`: `pacman -S 7zip` (or any package that provides `7z` / `7zz`).
2. `rar` + `unrar`: download `rarlinux-x64-<ver>.tar.gz` from rarlab.com, verify its SHA-512, unpack so that `tools/rar/rar` and
   `tools/rar/unrar` exist (`tools/` is git-ignored; the trial binary is never redistributed). Override with `UNPAKZ_RAR=/path/to/rar`.

## The mutation harness

Tests that never fail prove nothing. `harness/mutate.py` breaks the code on purpose and demands that a test notices.

* `harness/mutations.py` lists mutants as `(id, file, exact text, replacement)`. The text must occur **exactly once** in the file.
* For each mutant the script rewrites the file, runs `dotnet test` (Release), restores the file (also on Ctrl-C / SIGTERM), and classifies:
  * **killed**: the suite failed. Good. A hang (240 s timeout) counts as killed.
  * **survived**: the suite passed. This is a test gap. Write the missing test, then rerun that id.
  * **invalid**: the mutated code does not compile. A catalogue bug: fix the replacement text.
  * **stale**: the text no longer matches. Update the catalogue (`--check` finds these in a second).
* A survivor is only acceptable if it is *equivalent* (no behaviour differs). It must be listed in `EQUIVALENT` in `mutations.py`
  with a reason a reviewer can challenge; the run fails on any unaccepted survivor.
* Never build, sync or publish while a mutation run is going: the files are rewritten in place.

What the survivors taught us (typical): a later check often produces the same error as the guard under test, so the guard looks
unnecessary. Each guard needs an input where only it can be responsible (a wrapping size, an overshooting last symbol, an
exactly-at-the-limit value). Several real defects were found this way (see git history: "mutants" commits).

### Results so far

Updated in `docs/STATUS.md` (kept next to the numbers it describes).

## Working rules (from experience)

* Test first. A new test that passes on the first run is suspect: break the code and watch it fail.
* No vacuous assertions (`Assert.True(x && false)`, `a == a`, ternaries with equal branches). Review your own tests after writing them.
* A failed-open or failed-extract must leave nothing behind (temp files, mappings, partial output) and must say what to do next.
* Emit screenshots for UI work and look at them. A layout check can pass on a window that looks wrong (it found fixed fonts that ignored
  scaling, a redundant subtitle and a misleading "Ready." while busy only when viewed).
* Anything that needs an oracle must fail, not skip, when the oracle is missing (`UNPAKZ_REQUIRE_ORACLE=1`).

## Windows notes

* Linux/WSL and Windows build the same tree: `Directory.Build.props` sends Windows output to `obj-win/` and `bin-win/`.
* On the development machine a DLP policy blocks Windows from writing into the WSL share (`\\wsl.localhost\...`), so
  `scripts/winsync.sh` copies the sources (no binaries, no `tools/`, no `.git`) into a Downloads sub-folder and runs there.
  See `docs/decisions/0001-windows-build-blocked-by-dlp.md`.
* A hung test host can keep DLLs and logs locked; `wintest.ps1` uses a unique log per run and the sync folder can be changed with
  `UNPAKZ_WIN_DIR`.

## Known gaps

* A real drag from the window into Explorer has not been observed. Injected mouse input could not start even a standard WinForms drag on
  the test machine (the opt-in test runs that control first and skips with a message). The data object itself is verified through its COM
  interfaces.
* Hardware-specific behaviour (high DPI monitors, multi-monitor) is only approximated by font-scale emulation.
* No performance gate yet (see the benchmark and Vulkan notes in `docs/`).

## The native backend harness (C++ `unpakz.dll`, since 0.2)

The product's engine is `native/` (see `docs/decisions/0003`). Its checks mirror the C# ones and add sanitizers and third-party vectors.

| What | Command | Where |
|---|---|---|
| Build + all native tests under AddressSanitizer + UBSan, warnings as errors | `./harness.sh native` (= `scripts/native-test.sh`; last line `NATIVE-PASS` / `NATIVE-FAIL` / `NATIVE-BUILD-ERROR`) | WSL |
| Same with MSVC (Release, `/W4 /WX`, static CRT) | `scripts/winsync.sh '.\scripts\native-win.ps1'` | WSL -> Windows |
| Differential conformance vs `unrar` / 7-Zip / `tar` on fixtures, freshly generated archives and the third-party corpus | `python3 harness/conformance.py [--corpus]` | WSL |
| Fetch the third-party RAR vectors (libarchive's test archives, SHA-256-pinned tarball) | `python3 harness/fetch_corpus.py <libarchive-3.8.11.tar.xz>` | WSL |
| Native mutants | `UNPAKZ_MUTATE_MODE=native python3 harness/mutate.py [=ID,ID] [--check]` | WSL |
| 5 GiB zip64 entry (no 2/4 GiB limit anywhere) | `UNPAKZ_BIG=1 native/build-linux/engine_test zip64` | WSL |

| Layer | Where |
|---|---|
| Guard spec (ported from the C# suite, plus 200,000 random names) | `native/tests/guard_test.cpp` |
| Engine security, single-write, limits, overwrite policies, races, selection | `native/tests/engine_test.cpp` on an in-memory file system with an operation log |
| C ABI on the real file system: argument validation, struct versioning, error buffers, callbacks | `native/tests/api_test.cpp` |
| Volume discovery | `native/tests/volumes_test.cpp` |
| RAR5 decoder: fixtures from the real `rar`, filters actually hit, solid skip, volumes, every byte flip and truncation | `native/tests/rar5_test.cpp` |
| Third-party vectors (102 RAR archives incl. libarchive's security regressions): same result as unrar or a typed refusal, never a crash or a hang | `harness/conformance.py --corpus` |

Decisions that the conformance script records instead of hiding (`KNOWN` in `harness/conformance.py`): links are never created, files without any
checksum are refused, malformed extra records are refused, file versions are kept as `name;N`.

The mutation runner refuses to judge on a red baseline. Mutants that cannot change behaviour (defence in depth behind a libarchive check that
already fires) are listed with the reason in `harness/native_mutations.py`.
