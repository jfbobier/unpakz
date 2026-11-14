# 0002 – The Vulkan experiment project builds without warnings-as-errors

Date: 2026-11-14

`bench/Unpakz.Vulkan` is a throw-away measurement tool (raw Vulkan interop, pointers, `unsafe`). It is not part of `Unpakz.slnx`,
not shipped, and never referenced by product code. Rule 6 (warnings are errors) is relaxed **for this one project only** in its
csproj; `Directory.Build.props` is unchanged. If the experiment ever produces a shipped GPU path, it moves into its own product
assembly with the full rules, tests and mutants first (CLAUDE.md rule 5: GPU code ships only if it beats the CPU path on the gate benchmark).

Packages added (bench only): `BenchmarkDotNet` 0.15.8 (MIT, benchmark harness), `Silk.NET.Vulkan` 2.22.0 (MIT, Vulkan bindings).
