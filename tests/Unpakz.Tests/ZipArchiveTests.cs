using Unpakz.Core;
using Unpakz.Core.Zip;
using Unpakz.Tests.Support;

namespace Unpakz.Tests;

public class ZipArchiveTests
{
    [Fact]
    public void Spans_cover_header_and_data_and_tile_a_contiguous_archive()
    {
        var zip = new ZipBuilder().Add("a", new byte[10]).AddDirectory("d").Add("d/b", new byte[100], deflate: true).Add("c", []).Build();
        var a = ZipArchive.Open(zip);
        var spans = a.Entries.Select(a.GetSpan).ToArray();

        Assert.Equal(0UL, spans[0].Start);                                    // starts at the local header, not at the data
        for (var i = 1; i < spans.Length; i++) Assert.Equal(spans[i - 1].End, spans[i].Start);   // header+data tile exactly
        Assert.Equal(30UL + 1 + 10, spans[0].End);
    }

    [Fact]
    public void Maps_entries_to_format_neutral_view()
    {
        var zip = new ZipBuilder().Add("f.txt", new byte[7]).AddDirectory("d").Add("l", "x"u8.ToArray(), externalAttrs: ZipBuilder.SymlinkAttr)
            .Add("p", [], externalAttrs: ZipBuilder.FifoAttr).Add("m", [], externalAttrs: ZipBuilder.DirModeAttr).Build();
        var a = ZipArchive.Open(zip);

        Assert.Equal("zip", a.Format);
        Assert.Equal([EntryKind.File, EntryKind.Directory, EntryKind.Symlink, EntryKind.Special, EntryKind.Directory], a.Entries.Select(e => e.Kind));
        Assert.Equal([0, 1, 2, 3, 4], a.Entries.Select(e => e.Index));
        Assert.Equal(7UL, a.Entries[0].UncompressedSize);
        Assert.Equal(new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Local), a.Entries[0].LastWriteLocal);
    }

    [Fact]
    public void Reader_detects_zip_without_a_signature_at_offset_zero_only_via_end_record()
    {
        var zip = new ZipBuilder().Add("a", new byte[3]).Build();
        Assert.Equal("zip", ArchiveReader.Open(zip).Format);
        Assert.Throws<ArchiveException>(() => ArchiveReader.Open(new byte[100]));
    }
}
