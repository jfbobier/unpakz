# 0005 – Managed layers on the native backend; library file name

Date: 2026-11-14

* `Unpakz.Interop` and `Unpakz.Presentation` target **netstandard2.0**: one build serves .NET Framework 4.8 (the shipped front ends) and .NET 10 (tests on WSL against
  `libunpakz_native.so`). The WinForms app, the COM layer and the CLI target net48.
* The native library file is **`unpakz_native.dll`**. It was first `unpakz.dll`, which on a case-insensitive file system is also the first thing the .NET loader probes for the managed
  assembly `Unpakz` (the app), producing "module was expected to contain an assembly manifest". `unpakz_core` was taken by the static library target.
* The CLI's assembly is `unpakz.exe` and the app's `Unpakz.exe`: they differ only by case, so they ship in separate zips and the Windows tests reference the app only.
* The view-models lost the "Packed" column (the native API does not report packed sizes) and take an `ArchiveSession` instead of the old engine's `ArchiveSource`.
* Zero-entry results from files with no recognisable signature are refused ("not a supported archive"): a plain tar has no signature and zeros read as an empty tar.
* Test-only NuGet packages added: `System.Memory` 4.5.5 (Microsoft, MIT) for the Windows tests' shared fixture builders on .NET Framework;
  `Microsoft.NETFramework.ReferenceAssemblies` 1.0.3 (Microsoft, MIT, build-time only) so net48 projects also build on Linux. No runtime dependencies were added to the shipped zips.
