using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Unpakz.Core.Checksums;

namespace Unpakz.Bench;

[MemoryDiagnoser]
public class CrcBenchmarks
{
    private byte[] _data = [];

    [Params(4 * 1024, 1024 * 1024, 256 * 1024 * 1024)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _data = new byte[Size];
        new Random(42).NextBytes(_data);
        if (Crc32.Update(0, _data) != Crc32.UpdateSoftware(0, _data)) throw new InvalidOperationException("CLMUL CRC disagrees with the software CRC");
    }

    [Benchmark(Baseline = true)]
    public uint SlicingBy8() => Crc32.UpdateSoftware(0, _data);

    [Benchmark]
    public uint Pclmulqdq() => Crc32.Update(0, _data);

    [Benchmark]
    public byte[] MemoryCopy() => _data.AsSpan().ToArray();
}

public static class Program
{
    public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
