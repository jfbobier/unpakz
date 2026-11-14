using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Unpakz.Core.Checksums;

// CRC-32 on the GPU vs the CPU. Measures the three costs that decide whether offloading can ever pay:
//   1. getting the bytes to the GPU (copy into a host-visible buffer the shader reads),
//   2. the compute itself (one invocation per 1 KiB chunk),
//   3. combining the per-chunk CRCs on the CPU.
// Usage: Unpakz.Vulkan [sizeMiB=128] [iterations=7]
internal static unsafe class Program
{
    private const int ChunkBytes = 1024;

    private static int Main(string[] args)
    {
        var sizeMiB = args.Length > 0 ? int.Parse(args[0]) : 128;
        var iterations = args.Length > 1 ? int.Parse(args[1]) : 7;
        var size = (long)sizeMiB * 1024 * 1024;

        var data = GC.AllocateUninitializedArray<byte>((int)size);
        new Random(42).NextBytes(data);

        // CPU reference and baseline.
        var cpuTimes = new double[iterations];
        uint cpuCrc = 0;
        for (var i = 0; i < iterations; i++)
        {
            var t = Stopwatch.GetTimestamp();
            cpuCrc = Crc32.Update(0, data);
            cpuTimes[i] = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
        }

        Vk vk;
        try { vk = Vk.GetApi(); }
        catch (Exception ex) { Console.WriteLine($"No Vulkan loader: {ex.Message}"); return 2; }

        var appInfo = new ApplicationInfo { SType = StructureType.ApplicationInfo, ApiVersion = Vk.Version11 };
        var ici = new InstanceCreateInfo { SType = StructureType.InstanceCreateInfo, PApplicationInfo = &appInfo };
        if (vk.CreateInstance(&ici, null, out var instance) != Result.Success) { Console.WriteLine("vkCreateInstance failed (no Vulkan driver?)."); return 2; }

        uint count = 0;
        vk.EnumeratePhysicalDevices(instance, &count, null);
        if (count == 0) { Console.WriteLine("No Vulkan physical devices."); return 2; }
        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* p = devices) vk.EnumeratePhysicalDevices(instance, &count, p);

        var results = new List<string>();
        foreach (var gpu in devices)
        {
            vk.GetPhysicalDeviceProperties(gpu, out var props);
            var name = Marshal.PtrToStringAnsi((nint)props.DeviceName) ?? "?";
            Console.WriteLine($"\n== {name} ({props.DeviceType}), maxStorageBufferRange {props.Limits.MaxStorageBufferRange / (1024 * 1024)} MiB");
            try { RunOne(vk, gpu, props, name, data, cpuCrc, cpuTimes, iterations, results); }
            catch (Exception ex) { Console.WriteLine($"   failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        Console.WriteLine($"\nCPU (Crc32.Update, {(Crc32Hardware() ? "PCLMULQDQ" : "software")}): median {Median(cpuTimes):F2} ms for {sizeMiB} MiB = {size / 1e6 / Median(cpuTimes) * 1000:F0} MB/s");
        foreach (var r in results) Console.WriteLine(r);
        return 0;
    }

    private static bool Crc32Hardware() => System.Runtime.Intrinsics.X86.Pclmulqdq.IsSupported;

    private static double Median(double[] v) { var s = (double[])v.Clone(); Array.Sort(s); return s[s.Length / 2]; }

    private static void RunOne(Vk vk, PhysicalDevice gpu, PhysicalDeviceProperties props, string name, byte[] data, uint cpuCrc, double[] cpuTimes, int iterations, List<string> results)
    {
        long size = data.Length;
        if (size > props.Limits.MaxStorageBufferRange) { Console.WriteLine("   input larger than maxStorageBufferRange; use a smaller size"); return; }

        // compute queue
        uint qn = 0;
        vk.GetPhysicalDeviceQueueFamilyProperties(gpu, &qn, null);
        var families = new QueueFamilyProperties[qn];
        fixed (QueueFamilyProperties* p = families) vk.GetPhysicalDeviceQueueFamilyProperties(gpu, &qn, p);
        var family = Array.FindIndex(families, f => (f.QueueFlags & QueueFlags.ComputeBit) != 0);
        if (family < 0) { Console.WriteLine("   no compute queue"); return; }

        var priority = 1f;
        var qci = new DeviceQueueCreateInfo { SType = StructureType.DeviceQueueCreateInfo, QueueFamilyIndex = (uint)family, QueueCount = 1, PQueuePriorities = &priority };
        var dci = new DeviceCreateInfo { SType = StructureType.DeviceCreateInfo, QueueCreateInfoCount = 1, PQueueCreateInfos = &qci };
        Check(vk.CreateDevice(gpu, &dci, null, out var device), "vkCreateDevice");
        vk.GetDeviceQueue(device, (uint)family, 0, out var queue);

        vk.GetPhysicalDeviceMemoryProperties(gpu, out var mem);
        var chunks = (uint)(size / ChunkBytes);
        var (inBuf, inMem, inPtr) = MakeBuffer(vk, device, mem, (ulong)size);
        var (outBuf, outMem, outPtr) = MakeBuffer(vk, device, mem, chunks * 4UL, preferCached: true);

        // pipeline
        var spv = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "crc32.spv"));
        ShaderModule module;
        fixed (byte* code = spv)
        {
            var smci = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)spv.Length, PCode = (uint*)code };
            Check(vk.CreateShaderModule(device, &smci, null, out module), "vkCreateShaderModule");
        }

        var bindings = stackalloc DescriptorSetLayoutBinding[2];
        for (var i = 0; i < 2; i++)
            bindings[i] = new DescriptorSetLayoutBinding { Binding = (uint)i, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit };
        var dslci = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 2, PBindings = bindings };
        Check(vk.CreateDescriptorSetLayout(device, &dslci, null, out var setLayout), "layout");

        var pcRange = new PushConstantRange { StageFlags = ShaderStageFlags.ComputeBit, Offset = 0, Size = 8 };
        var plci = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &setLayout, PushConstantRangeCount = 1, PPushConstantRanges = &pcRange };
        Check(vk.CreatePipelineLayout(device, &plci, null, out var pipelineLayout), "pipeline layout");

        var entry = (byte*)SilkMarshal.StringToPtr("main");
        var stage = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit, Module = module, PName = entry };
        var cpci = new ComputePipelineCreateInfo { SType = StructureType.ComputePipelineCreateInfo, Stage = stage, Layout = pipelineLayout };
        Check(vk.CreateComputePipelines(device, default, 1, &cpci, null, out var pipeline), "pipeline");

        var poolSize = new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 2 };
        var dpci = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 1, PoolSizeCount = 1, PPoolSizes = &poolSize };
        Check(vk.CreateDescriptorPool(device, &dpci, null, out var pool), "pool");
        var dsai = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = pool, DescriptorSetCount = 1, PSetLayouts = &setLayout };
        Check(vk.AllocateDescriptorSets(device, &dsai, out var set), "descriptor set");

        var infos = stackalloc DescriptorBufferInfo[2];
        infos[0] = new DescriptorBufferInfo { Buffer = inBuf, Offset = 0, Range = (ulong)size };
        infos[1] = new DescriptorBufferInfo { Buffer = outBuf, Offset = 0, Range = chunks * 4UL };
        var writes = stackalloc WriteDescriptorSet[2];
        for (var i = 0; i < 2; i++)
            writes[i] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = (uint)i, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &infos[i] };
        vk.UpdateDescriptorSets(device, 2, writes, 0, null);

        var cpci2 = new CommandPoolCreateInfo { SType = StructureType.CommandPoolCreateInfo, QueueFamilyIndex = (uint)family };
        Check(vk.CreateCommandPool(device, &cpci2, null, out var cmdPool), "command pool");
        var cbai = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, CommandPool = cmdPool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1 };
        Check(vk.AllocateCommandBuffers(device, &cbai, out var cmd), "command buffer");

        var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo };
        Check(vk.BeginCommandBuffer(cmd, &begin), "begin");
        vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, pipeline);
        vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, pipelineLayout, 0, 1, &set, 0, null);
        var push = stackalloc uint[2];
        push[0] = ChunkBytes / 16;
        push[1] = chunks;
        vk.CmdPushConstants(cmd, pipelineLayout, ShaderStageFlags.ComputeBit, 0, 8, push);
        vk.CmdDispatch(cmd, (chunks + 63) / 64, 1, 1);
        Check(vk.EndCommandBuffer(cmd), "end");

        var fci = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        Check(vk.CreateFence(device, &fci, null, out var fence), "fence");

        var shift = BuildShiftTables(BuildShiftOperator(ChunkBytes));
        var upload = new double[iterations];
        var compute = new double[iterations];
        var combine = new double[iterations];
        uint gpuCrc = 0;

        for (var it = 0; it < iterations; it++)
        {
            var t0 = Stopwatch.GetTimestamp();
            data.AsSpan().CopyTo(new Span<byte>((void*)inPtr, (int)size));
            var t1 = Stopwatch.GetTimestamp();

            var submit = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmd };
            Check(vk.QueueSubmit(queue, 1, &submit, fence), "submit");
            Check(vk.WaitForFences(device, 1, &fence, true, ulong.MaxValue), "wait");
            vk.ResetFences(device, 1, &fence);
            var t2 = Stopwatch.GetTimestamp();

            var partial = new uint[chunks];                                    // one bulk read: element-wise reads of mapped GPU memory are very slow
            new ReadOnlySpan<uint>((void*)outPtr, (int)chunks).CopyTo(partial);
            var acc = partial[0];
            for (var c = 1; c < partial.Length; c++) acc = Apply(shift, acc) ^ partial[c];
            gpuCrc = acc;
            var t3 = Stopwatch.GetTimestamp();

            upload[it] = Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds;
            compute[it] = Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds;
            combine[it] = Stopwatch.GetElapsedTime(t2, t3).TotalMilliseconds;
        }

        var ok = gpuCrc == cpuCrc;
        var up = Median(upload); var co = Median(compute); var cb = Median(combine);
        var total = up + co + cb;
        Console.WriteLine($"   correct: {ok} (gpu {gpuCrc:X8}, cpu {cpuCrc:X8})");
        Console.WriteLine($"   copy to GPU-visible memory {up:F2} ms | kernel {co:F2} ms | combine {cb:F2} ms | total {total:F2} ms");
        var mb = size / 1e6;
        results.Add($"{name}: copy {mb / up * 1000:F0} MB/s, kernel alone {mb / co * 1000:F0} MB/s, end-to-end {mb / total * 1000:F0} MB/s, correct={ok}");

        vk.DestroyFence(device, fence, null);
        vk.DestroyCommandPool(device, cmdPool, null);
        vk.DestroyDescriptorPool(device, pool, null);
        vk.DestroyPipeline(device, pipeline, null);
        vk.DestroyPipelineLayout(device, pipelineLayout, null);
        vk.DestroyDescriptorSetLayout(device, setLayout, null);
        vk.DestroyShaderModule(device, module, null);
        vk.UnmapMemory(device, inMem); vk.UnmapMemory(device, outMem);
        vk.DestroyBuffer(device, inBuf, null); vk.DestroyBuffer(device, outBuf, null);
        vk.FreeMemory(device, inMem, null); vk.FreeMemory(device, outMem, null);
        vk.DestroyDevice(device, null);
        SilkMarshal.Free((nint)entry);
    }

    private static (Silk.NET.Vulkan.Buffer Buffer, DeviceMemory Memory, nint Ptr) MakeBuffer(Vk vk, Device device, PhysicalDeviceMemoryProperties mem, ulong size, bool preferCached = false)
    {
        var bci = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = size, Usage = BufferUsageFlags.StorageBufferBit, SharingMode = SharingMode.Exclusive };
        Check(vk.CreateBuffer(device, &bci, null, out var buffer), "vkCreateBuffer");
        vk.GetBufferMemoryRequirements(device, buffer, out var req);

        const MemoryPropertyFlags want = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
        var type = -1;
        if (preferCached)
            for (var i = 0; i < mem.MemoryTypeCount && type < 0; i++)
                if ((req.MemoryTypeBits & (1u << i)) != 0 && (mem.MemoryTypes[i].PropertyFlags & (want | MemoryPropertyFlags.HostCachedBit)) == (want | MemoryPropertyFlags.HostCachedBit)) type = i;
        for (var i = 0; i < mem.MemoryTypeCount && type < 0; i++)
            if ((req.MemoryTypeBits & (1u << i)) != 0 && (mem.MemoryTypes[i].PropertyFlags & want) == want) type = i;
        if (type < 0) throw new InvalidOperationException("no host-visible coherent memory type");

        var mai = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = req.Size, MemoryTypeIndex = (uint)type };
        Check(vk.AllocateMemory(device, &mai, null, out var memory), "vkAllocateMemory");
        Check(vk.BindBufferMemory(device, buffer, memory, 0), "vkBindBufferMemory");
        void* ptr;
        Check(vk.MapMemory(device, memory, 0, size, 0, &ptr), "vkMapMemory");
        return (buffer, memory, (nint)ptr);
    }

    private static void Check(Result r, string what)
    {
        if (r != Result.Success) throw new InvalidOperationException($"{what} failed: {r}");
    }

    // Linear map "append n zero bytes" on a CRC value, as 32 columns (CRC(A||B) = shift(CRC(A)) ^ CRC(B) for |B| = n).
    private static uint[] BuildShiftOperator(int zeroBytes)
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }

        var columns = new uint[32];
        for (var bit = 0; bit < 32; bit++)
        {
            var s = 1u << bit;
            for (var i = 0; i < zeroBytes; i++) s = (s >> 8) ^ table[s & 0xFF];
            columns[bit] = s;
        }

        return columns;
    }

    private static uint[] BuildShiftTables(uint[] columns)
    {
        var t = new uint[4 * 256];
        for (var k = 0; k < 4; k++)
            for (var b = 0; b < 256; b++)
            {
                uint r = 0;
                for (var bit = 0; bit < 8; bit++)
                    if ((b >> bit & 1) != 0) r ^= columns[k * 8 + bit];
                t[k * 256 + b] = r;
            }

        return t;
    }

    private static uint Apply(uint[] t, uint v) => t[(int)(v & 0xFF)] ^ t[256 + (int)((v >> 8) & 0xFF)] ^ t[512 + (int)((v >> 16) & 0xFF)] ^ t[768 + (int)(v >> 24)];
}
