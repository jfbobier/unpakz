# unpakz

> **EXPERIMENTAL – not production-tested.** Developed as a harness-engineering exercise (AI-assisted, test-first, mutation-verified
> on Linux/WSL). It has **never been run on Windows** and has no GUI yet. Do not use it on data you cannot afford to lose,
> and review the code before trusting it: archive extraction is a security boundary. See [docs/STATUS.md](docs/STATUS.md).

Extraction-only ZIP and RAR 5 library for .NET 10, designed around: no temp folders (one write per byte), hostile-archive safety
(zip-slip, reserved names, links, bombs), and a verification harness (`./harness.sh fast|full`).
