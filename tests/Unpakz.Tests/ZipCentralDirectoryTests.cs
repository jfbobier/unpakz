using System.Buffers.Binary;
using System.Text;
using Unpakz.Core;
using Unpakz.Core.Zip;
using Unpakz.Tests.Support;

namespace Unpakz.Tests;

public class ZipCentralDirectoryTests
{
    private static readonly byte[] Hello = "hello world"u8.ToArray();

    private static byte[] Sample() => new ZipBuilder()
        .Add("a.txt", Hello)
        .Add("dir/", [])
        .Add("dir/b.bin", new byte[1000], deflate: true)
        .Build();

    // ---- happy paths ----
    [Fact]
    public void Reads_entries_in_order_with_correct_metadata()
    {
        var entries = ZipCentralDirectory.Read(Sample());

        Assert.Equal(3, entries.Count);
        Assert.Equal("a.txt", entries[0].Name);
        Assert.Equal(0, entries[0].Method);
        Assert.Equal((ulong)Hello.Length, entries[0].UncompressedSize);
        Assert.Equal((ulong)Hello.Length, entries[0].CompressedSize);
        Assert.Equal(ZipBuilder.Crc32(Hello), entries[0].Crc32);
        Assert.False(entries[0].IsDirectory);

        Assert.Equal("dir/", entries[1].Name);
        Assert.True(entries[1].IsDirectory);

        Assert.Equal("dir/b.bin", entries[2].Name);
        Assert.Equal(8, entries[2].Method);
        Assert.Equal(1000UL, entries[2].UncompressedSize);
        Assert.True(entries[2].CompressedSize < 1000);
        Assert.True(entries[2].LocalHeaderOffset > entries[0].LocalHeaderOffset);
    }

    [Fact]
    public void Empty_archive_has_no_entries() =>
        Assert.Empty(ZipCentralDirectory.Read(new ZipBuilder().Build()));

    [Fact]
    public void Archive_comment_is_skipped_up_to_maximum_length()
    {
        var zip = new ZipBuilder { Comment = new byte[65535] }.Add("a", Hello).Build();
        Assert.Single(ZipCentralDirectory.Read(zip));
    }

    [Fact]
    public void Zip64_end_record_is_followed()
    {
        var zip = new ZipBuilder { ForceZip64Eocd = true }.Add("a", Hello).Add("b", Hello).Build();
        var e = ZipCentralDirectory.Read(zip);
        Assert.Equal(["a", "b"], e.Select(x => x.Name));
    }

    [Fact]
    public void Zip64_extra_field_supplies_saturated_sizes_and_offset()
    {
        var zip = new ZipBuilder().Add("x", Hello).Add("y", Hello, zip64Extra: true).Build();
        var e = ZipCentralDirectory.Read(zip);
        Assert.Equal((ulong)Hello.Length, e[1].UncompressedSize);
        Assert.Equal((ulong)Hello.Length, e[1].CompressedSize);
        Assert.True(e[1].LocalHeaderOffset > 0);
    }

    [Fact]
    public void Names_decode_as_utf8_when_flagged_and_cp437_otherwise()
    {
        var zip = new ZipBuilder()
            .Add("日本/é.txt", Hello, utf8: true)
            .Add("caféüñ", Hello, utf8: false)
            .Build();
        var e = ZipCentralDirectory.Read(zip);
        Assert.Equal("日本/é.txt", e[0].Name);
        Assert.Equal("caféüñ", e[1].Name);
    }

    [Fact]
    public void Cp437_table_is_complete()
    {
        Assert.Equal(128, Cp437.HighHalf.Length);
        Assert.Equal('Ç', Cp437.Decode(0x80));
        Assert.Equal(' ', Cp437.Decode(0xFF));
        Assert.Equal('A', Cp437.Decode((byte)'A'));
        Assert.Equal(128, Cp437.HighHalf.Distinct().Count());
    }

    // ---- structural rejections ----
    [Fact]
    public void Rejects_input_too_small() =>
        Assert.Throws<ArchiveException>(() => ZipCentralDirectory.Read(new byte[10]));

    [Fact]
    public void Rejects_input_without_end_record() =>
        Assert.Throws<ArchiveException>(() => ZipCentralDirectory.Read(new byte[5000]));

    [Fact]
    public void Rejects_central_directory_offset_beyond_file()
    {
        var zip = Sample();
        var eocd = zip.Length - 22;
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(eocd + 16), (uint)zip.Length + 100);
        Assert.Throws<ArchiveException>(() => ZipCentralDirectory.Read(zip));
    }

    [Fact]
    public void Rejects_central_directory_size_beyond_file()
    {
        var zip = Sample();
        var eocd = zip.Length - 22;
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(eocd + 12), 0x7FFFFFFF);
        Assert.Throws<ArchiveException>(() => ZipCentralDirectory.Read(zip));
    }

    [Fact]
    public void Rejects_entry_count_impossible_for_directory_size()
    {
        var zip = Sample();
        var eocd = zip.Length - 22;
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(eocd + 10), 60000);
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(eocd + 8), 60000);
        Assert.Throws<ArchiveException>(() => ZipCentralDirectory.Read(zip));
    }

    [Fact]
    public void Rejects_entry_count_over_configured_limit()
    {
        var zip = new ZipBuilder().Add("a", Hello).Add("b", Hello).Add("c", Hello).Build();
        Assert.Throws<ArchiveException>(() => ZipCentralDirectory.Read(zip, new ZipLimits { MaxEntries = 2 }));
    }

    [Fact]
    public void Rejects_bad_central_header_signature()
    {
        var zip = Sample();
        var cd = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(zip.Length - 22 + 16));
        zip[cd] ^= 0xFF;
        Assert.Throws<ArchiveException>(() => ZipCentralDirectory.Read(zip));
    }

    [Fact]
    public void Rejects_name_length_overrunning_directory()
    {
        var zip = Sample();
        var cd = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(zip.Length - 22 + 16));
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(cd + 28), 65535);
        Assert.Throws<ArchiveException>(() => ZipCentralDirectory.Read(zip));
    }

    [Fact]
    public void Rejects_extra_field_overrun()
    {
        var zip = new ZipBuilder().Add("y", Hello, zip64Extra: true).Build();
        var cd = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(zip.Length - 22 + 16));
        // extra header lives after 46 + name(1); bump its declared data size past the end
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(cd + 46 + 1 + 2), 5000);
        Assert.Throws<ArchiveException>(() => ZipCentralDirectory.Read(zip));
    }

    [Fact]
    public void Rejects_zip64_markers_without_locator()
    {
        var zip = Sample();
        var eocd = zip.Length - 22;
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(eocd + 16), 0xFFFFFFFF);
        Assert.Throws<ArchiveException>(() => ZipCentralDirectory.Read(zip));
    }

    [Fact]
    public void Rejects_local_header_offset_outside_archive()
    {
        var zip = Sample();
        var cd = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(zip.Length - 22 + 16));
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(cd + 42), (uint)zip.Length);
        Assert.Throws<ArchiveException>(() => ZipCentralDirectory.Read(zip));
    }

    [Fact]
    public void Rejects_stored_entry_whose_sizes_disagree()
    {
        var zip = Sample();
        var cd = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(zip.Length - 22 + 16));
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(cd + 24), 999);   // uncompressed size of stored entry 0
        Assert.Throws<ArchiveException>(() => ZipCentralDirectory.Read(zip));
    }

    [Fact]
    public void Rejects_multi_disk_archives_as_unsupported()
    {
        var zip = Sample();
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(zip.Length - 22 + 4), 1);
        var ex = Assert.Throws<ArchiveException>(() => ZipCentralDirectory.Read(zip));
        Assert.Equal(ArchiveError.Unsupported, ex.Error);
    }

    [Fact]
    public void Invalid_utf8_name_is_rejected()
    {
        var zip = new ZipBuilder().Add("abc", Hello).Build();
        var cd = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(zip.Length - 22 + 16));
        zip[cd + 46] = 0xFF;   // lone 0xFF is never valid UTF-8, flag bit 11 is set
        Assert.Throws<ArchiveException>(() => ZipCentralDirectory.Read(zip));
    }

    [Fact]
    public void Rejects_hostile_zip64_entry_count_without_allocating()
    {
        var zip = new ZipBuilder { ForceZip64Eocd = true }.Add("a", Hello).Build();
        var e64 = zip.Length - 22 - 20 - 56;
        BinaryPrimitives.WriteUInt64LittleEndian(zip.AsSpan(e64 + 24), (1UL << 31) + 5);   // entries on this disk
        BinaryPrimitives.WriteUInt64LittleEndian(zip.AsSpan(e64 + 32), (1UL << 31) + 5);   // total entries
        Assert.Throws<ArchiveException>(() => ZipCentralDirectory.Read(zip, new ZipLimits { MaxEntries = long.MaxValue }));
    }

    [Fact]
    public void Fake_end_record_inside_comment_is_ignored()
    {
        // A comment ending in an EOCD-looking record whose comment length does not fit the file must not hijack parsing.
        var fake = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(fake, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(fake.AsSpan(8), 7);
        BinaryPrimitives.WriteUInt16LittleEndian(fake.AsSpan(10), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(fake.AsSpan(12), 12345);
        BinaryPrimitives.WriteUInt32LittleEndian(fake.AsSpan(16), 54321);
        BinaryPrimitives.WriteUInt16LittleEndian(fake.AsSpan(20), 5);   // claims a comment that is not there, so it is not a valid end record
        var zip = new ZipBuilder { Comment = fake }.Add("real", Hello).Build();
        Assert.Equal("real", Assert.Single(ZipCentralDirectory.Read(zip)).Name);
    }

    // ---- robustness sweeps: only ArchiveException may escape ----
    [Fact]
    public void Every_truncation_is_handled()
    {
        var zip = Sample();
        for (var len = 0; len < zip.Length; len++)
        {
            try { _ = ZipCentralDirectory.Read(zip.AsSpan(0, len)); }
            catch (ArchiveException) { /* expected */ }
        }
    }

    [Fact]
    public void Every_single_byte_corruption_is_handled()
    {
        var original = Sample();
        var zip = (byte[])original.Clone();
        for (var i = 0; i < zip.Length; i++)
        {
            foreach (var mask in new byte[] { 0x01, 0x80, 0xFF })
            {
                zip[i] ^= mask;
                try { _ = ZipCentralDirectory.Read(zip); }
                catch (ArchiveException) { /* expected */ }
                zip[i] = original[i];
            }
        }
    }

    [Fact]
    public void Random_garbage_is_handled()
    {
        var rng = new Random(7);
        for (var i = 0; i < 2000; i++)
        {
            var buf = new byte[rng.Next(0, 400)];
            rng.NextBytes(buf);
            if (buf.Length >= 22 && i % 2 == 0)
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(buf.Length - 22), 0x06054b50);   // plausible EOCD
            try { _ = ZipCentralDirectory.Read(buf); }
            catch (ArchiveException) { }
        }
    }

    [Fact]
    public void Entry_names_never_leak_replacement_characters_for_valid_input()
    {
        var zip = new ZipBuilder().Add("ok/ü.txt", Hello).Build();
        Assert.DoesNotContain('�', ZipCentralDirectory.Read(zip)[0].Name);
        Assert.Equal("ok/ü.txt", Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(ZipCentralDirectory.Read(zip)[0].Name)));
    }
}
