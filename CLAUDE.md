# Unpakz – engineering rules

Windows-native extraction-only ZIP/RAR tool in .NET. This repo is a harness-engineering exercise:
the test/verification framework is a first-class deliverable, not an afterthought.

## Non-negotiables
1. **No code without a failing test or a stated acceptance check first** (spec clause, corpus file, or benchmark gate).
2. **Extraction is a security boundary.** Every output path passes the single `PathGuard` chokepoint. No other code may open an output file.
3. **One write per byte.** Data goes decoder -> final destination file. No temp *folders*. A same-directory temp *file name* + atomic rename is allowed; a copy from temp to target is not. A test must prove this.
4. **Never trust archive metadata**: sizes, offsets, counts, names, and compression ratios are hostile until validated. Hard limits are configurable and default-safe.
5. **Every performance claim needs a BenchmarkDotNet result committed in `bench/results/`.** No benchmark, no optimization. GPU code ships only if it beats the CPU path on the gate benchmark.
6. **Warnings are errors**, nullable enabled, analyzers on, `Directory.Build.props` is the single source of settings.
7. **Decoders are pure**: `ReadOnlySpan/Memory` in, bytes out, no file/OS access. Windows specifics live only in `Unpakz.Windows`.
8. Corrupt/truncated/malicious input must produce a typed `ArchiveException`, never a crash, hang, or unbounded allocation.

## Workflow
- Core/Engine/tests build and run on WSL (`dotnet`, Arch). Windows-only projects (WinForms app, Windows sink) build via `powershell.exe`.
- `./harness.sh fast` (build with warnings-as-errors + unit tests, <30 s) before every commit; `./harness.sh full` (conformance, fuzz-smoke, mutation sample) before merging a milestone.
- New checks must be mutation-verified: break the guarded code on purpose and confirm a test fails.
- Commits are small and each one is green. Record decisions in `docs/decisions/NNNN-title.md`.
- Third-party archives are untrusted data: keep them in `corpus/external/`, never executed, opened only via the test harness.
- NuGet packages: only add after stating name, purpose and license; prefer zero runtime dependencies.
- Do not claim done without showing the harness output.
