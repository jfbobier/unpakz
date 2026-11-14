# Benchmarks and the GPU experiment

Rule 5 of `CLAUDE.md`: no optimisation without a committed benchmark, and GPU code ships only if it beats the CPU path.

| Project | What it is | Run |
|---|---|---|
| `Unpakz.Bench` | BenchmarkDotNet: CRC-32 slicing-by-8 vs PCLMULQDQ vs a plain copy | `cd bench/Unpakz.Bench && dotnet run -c Release -- --filter '*CrcBenchmarks*' -j short --exporters github csv --artifacts ../results/crc-linux` |
| `Unpakz.Vulkan` | CRC-32 compute shader on the GPU, with the transfer and combine costs measured | Windows: `dotnet run --project bench\Unpakz.Vulkan -c Release -- 128 9` (the shader is rebuilt with `bench/compile-shaders.sh`, needs `glslc`) |

Neither is in `Unpakz.slnx`, so `./harness.sh fast` stays fast. Results live in `bench/results/`.

## Results (this laptop: Ryzen 7 PRO 7840U + Radeon 780M iGPU, **not** the 9800X3D / Nvidia desktop)

**CPU: PCLMULQDQ folding is a clear win and now ships in `Unpakz.Core`** (`bench/results/crc-linux/`, 256 MiB):
slicing-by-8 174 ms (1.5 GB/s) vs PCLMULQDQ 21 ms (12.5 GB/s) = 8x. It is tested against the software path for every length around the
fold boundaries and several seeds, and mutation-checked (H1-H7).

**GPU: no-go on this hardware** (`bench/results/vulkan-crc32-radeon780m-windows.txt`, 128 MiB, result verified equal to the CPU CRC):

| Step | Time |
|---|---|
| CPU `Crc32.Update` (one core, PCLMULQDQ) | 10.1 ms |
| GPU: copy into the buffer the shader reads | 6.2 ms |
| GPU: kernel (1 invocation per 1 KiB chunk) | 30.8 ms |
| GPU: combine 131 072 partial CRCs on the CPU | 1.2 ms |
| GPU total | 38.2 ms (3.8x slower than the CPU) |

Why it cannot win, and what was and was not tried:
* The CPU already runs at ~13 GB/s on one core, close to what a copy costs (the copy alone is 6.2 ms, 60% of the CPU's total). A GPU that
  computed for free would be at best ~1.6x, and any real kernel takes at least the time to read the memory once (an iGPU shares the
  same DRAM). In the real pipeline the CRC is computed while the decoder's output is still in cache, so the CPU number is the pessimistic one.
* The kernel as written is slow (4.4 GB/s): each lane walks its own chunk, so neighbouring lanes read lines 1 KiB apart (uncoalesced), through
  host-visible memory. Wider loads (`uvec4`) changed nothing. A coalesced layout would need the data transposed first, which costs more
  CPU time than the CPU CRC itself. Not tried: a device-local staged copy, which matters on a discrete GPU (PCIe 4.0 x16 ~ 25 GB/s would
  still not beat 10 ms for the copy plus compute).
* **Not measured:** the Nvidia GPU on the 9800X3D desktop. The tool is rerunnable there unchanged (it enumerates every Vulkan device); a
  discrete GPU adds a PCIe hop, so the expectation is the same conclusion, but that is an expectation, not a result.
* Other candidates (inflate, the RAR LZ+Huffman decoder) are serial per stream; the only GPU-shaped work would be many independent
  small entries, which are I/O-bound anyway. No experiment was run for those.

Decision: keep the hardware CRC on the CPU; no GPU code in the product. See `docs/decisions/0002-vulkan-experiment-relaxed-analyzers.md`
for how the experiment project is built.
