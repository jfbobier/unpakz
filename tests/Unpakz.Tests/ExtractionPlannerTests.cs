using System.Buffers.Binary;
using Unpakz.Core;
using Unpakz.Core.Zip;
using Unpakz.Engine;
using Unpakz.Tests.Support;

namespace Unpakz.Tests;

public class ExtractionPlannerTests
{
    private static readonly byte[] Data = "payload"u8.ToArray();
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "dest"));

    private static ExtractionPlan Plan(byte[] zip, ExtractOptions? o = null) =>
        ExtractionPlanner.Plan(ZipArchive.Open(zip), Root, o ?? new ExtractOptions());

    private static ArchiveError PlanError(byte[] zip, ExtractOptions? o = null) =>
        Assert.Throws<ArchiveException>(() => Plan(zip, o)).Error;

    private static int CdOf(byte[] zip) => (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(zip.Length - 22 + 16));

    [Fact]
    public void Plans_normal_tree_with_normalized_paths()
    {
        var zip = new ZipBuilder().Add("a.txt", Data).AddDirectory("d").Add("d\\b.txt", Data).Add("d/e/c.txt", Data, deflate: true).Build();
        var plan = Plan(zip);
        Assert.Equal(["a.txt", "d", "d/b.txt", "d/e/c.txt"], plan.Items.Select(i => i.RelativePath));
        Assert.Equal([false, true, false, false], plan.Items.Select(i => i.IsDirectory));
        Assert.Empty(plan.Skipped);
        Assert.Equal(Data.Length * 3L, plan.TotalBytes);
    }

    [Fact]
    public void Empty_archive_gives_empty_plan() => Assert.Empty(Plan(new ZipBuilder().Build()).Items);

    // ---- unsafe paths ----
    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\windows\\x")]
    [InlineData("\\\\server\\share\\x")]
    [InlineData("CON")]
    [InlineData("dir/NUL.txt")]
    [InlineData("a.txt:stream")]
    [InlineData("trailing.")]
    public void Unsafe_names_abort_the_whole_plan(string name)
    {
        var zip = new ZipBuilder().Add("fine.txt", Data).Add(name, Data).Build();
        Assert.Equal(ArchiveError.UnsafePath, PlanError(zip));
    }

    [Fact]
    public void Unsafe_names_can_be_skipped_and_are_reported()
    {
        var zip = new ZipBuilder().Add("ok.txt", Data).Add("../bad.txt", Data).Add("also/ok.txt", Data).Build();
        var plan = Plan(zip, new ExtractOptions { SkipUnsafePaths = true });
        Assert.Equal(["ok.txt", "also/ok.txt"], plan.Items.Select(i => i.RelativePath));
        var s = Assert.Single(plan.Skipped);
        Assert.Equal(SkipReason.UnsafePath, s.Reason);
        Assert.Equal("../bad.txt", s.Name);
    }

    [Fact]
    public void Honours_custom_path_limits()
    {
        var zip = new ZipBuilder().Add("a/b/c/d.txt", Data).Build();
        var opts = new ExtractOptions { PathGuard = new PathGuardOptions { MaxDepth = 3 } };
        Assert.Equal(ArchiveError.UnsafePath, PlanError(zip, opts));
    }

    [Fact]
    public void Full_path_including_destination_must_fit_limit()
    {
        var zip = new ZipBuilder().Add(new string('a', 100) + ".txt", Data).Build();
        var tight = new ExtractOptions { MaxFullPathLength = Root.Length + 50 };
        Assert.Equal(ArchiveError.LimitExceeded, PlanError(zip, tight));
        Assert.Single(Plan(zip, new ExtractOptions { MaxFullPathLength = Root.Length + 200 }).Items);
    }

    // ---- collisions ----
    [Fact]
    public void Exact_duplicate_names_conflict() =>
        Assert.Equal(ArchiveError.Conflict, PlanError(new ZipBuilder().Add("a.txt", Data).Add("a.txt", Data).Build()));

    [Fact]
    public void Case_variants_conflict_because_windows_is_case_insensitive() =>
        Assert.Equal(ArchiveError.Conflict, PlanError(new ZipBuilder().Add("Readme.txt", Data).Add("README.TXT", Data).Build()));

    [Fact]
    public void Case_variant_directories_conflict_with_file() =>
        Assert.Equal(ArchiveError.Conflict, PlanError(new ZipBuilder().Add("Dir/x", Data).Add("dir", Data).Build()));

    [Fact]
    public void File_then_directory_of_same_name_conflicts() =>
        Assert.Equal(ArchiveError.Conflict, PlanError(new ZipBuilder().Add("a", Data).Add("a/b", Data).Build()));

    [Fact]
    public void Directory_then_file_of_same_name_conflicts() =>
        Assert.Equal(ArchiveError.Conflict, PlanError(new ZipBuilder().Add("a/b", Data).Add("a", Data).Build()));

    [Fact]
    public void Directory_entry_then_file_of_same_name_conflicts() =>
        Assert.Equal(ArchiveError.Conflict, PlanError(new ZipBuilder().AddDirectory("a").Add("a", Data).Build()));

    [Fact]
    public void Repeated_directory_entries_are_harmless()
    {
        var plan = Plan(new ZipBuilder().AddDirectory("a").AddDirectory("A").AddDirectory("a/b").Add("a/b/f", Data).Build());
        Assert.Equal(3, plan.Items.Count(i => i.IsDirectory) + plan.Items.Count(i => !i.IsDirectory) - 0);
    }

    // ---- special entries ----
    [Fact]
    public void Symlink_entries_are_skipped_never_planned()
    {
        var zip = new ZipBuilder().Add("link", "/etc/passwd"u8.ToArray(), externalAttrs: ZipBuilder.SymlinkAttr).Add("f", Data).Build();
        var plan = Plan(zip);
        Assert.Equal(["f"], plan.Items.Select(i => i.RelativePath));
        Assert.Equal(SkipReason.Symlink, Assert.Single(plan.Skipped).Reason);
    }

    [Fact]
    public void Symlink_with_unsafe_name_is_still_just_skipped()
    {
        var zip = new ZipBuilder().Add("../link", "x"u8.ToArray(), externalAttrs: ZipBuilder.SymlinkAttr).Build();
        Assert.Equal(SkipReason.Symlink, Assert.Single(Plan(zip).Skipped).Reason);
    }

    [Fact]
    public void Device_and_fifo_entries_are_skipped()
    {
        var zip = new ZipBuilder().Add("pipe", [], externalAttrs: ZipBuilder.FifoAttr).Build();
        var plan = Plan(zip);
        Assert.Empty(plan.Items);
        Assert.Equal(SkipReason.SpecialFile, Assert.Single(plan.Skipped).Reason);
    }

    [Fact]
    public void Unix_directory_mode_without_trailing_slash_is_a_directory()
    {
        var zip = new ZipBuilder().Add("folder", [], externalAttrs: ZipBuilder.DirModeAttr).Build();
        Assert.True(Assert.Single(Plan(zip).Items).IsDirectory);
    }

    [Fact]
    public void Directory_with_data_is_corrupt()
    {
        var zip = new ZipBuilder().Add("folder", Data, externalAttrs: ZipBuilder.DirModeAttr).Build();
        Assert.Equal(ArchiveError.Corrupt, PlanError(zip));
    }

    // ---- structure / limits ----
    [Fact]
    public void Overlapping_entry_data_is_rejected()
    {
        var zip = new ZipBuilder().Add("a", Data).Add("b", Data).Build();
        var cd = CdOf(zip);
        var firstLho = BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(cd + 42));
        var secondHdr = cd + 46 + 1;                                    // after first CD record (name "a")
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(secondHdr + 42), firstLho);   // b now points at a's data
        Assert.Equal(ArchiveError.Corrupt, PlanError(zip));
    }

    [Fact]
    public void Entry_whose_header_hides_inside_another_entrys_data_is_rejected()
    {
        // Entry "a" stores a complete, valid local header+data for "b"; the central directory then points "b" at it.
        var inner = new byte[30 + 1 + 3];
        BinaryPrimitives.WriteUInt32LittleEndian(inner, 0x04034b50);
        BinaryPrimitives.WriteUInt32LittleEndian(inner.AsSpan(14), ZipBuilder.Crc32("BBB"u8));
        BinaryPrimitives.WriteUInt32LittleEndian(inner.AsSpan(18), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(inner.AsSpan(22), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(inner.AsSpan(26), 1);
        inner[30] = (byte)'b';
        "BBB"u8.CopyTo(inner.AsSpan(31));

        var zip = new ZipBuilder().Add("a", inner).Add("b", "BBB"u8.ToArray()).Build();
        var cd = CdOf(zip);
        var aDataStart = BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(cd + 42)) + 30 + 1;
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(cd + 46 + 1 + 42), aDataStart);
        Assert.Equal(ArchiveError.Corrupt, PlanError(zip));
    }

    [Fact]
    public void Adjacent_entries_are_not_overlap()
    {
        var zip = new ZipBuilder().Add("a", Data).Add("b", Data).Add("c", []).Add("d", Data).Build();
        Assert.Equal(4, Plan(zip).Items.Count);
    }

    [Fact]
    public void Total_size_limit_is_enforced()
    {
        var zip = new ZipBuilder().Add("a", new byte[600]).Add("b", new byte[600]).Build();
        Assert.Equal(ArchiveError.LimitExceeded, PlanError(zip, new ExtractOptions { MaxTotalSize = 1000 }));
        Assert.Equal(2, Plan(zip, new ExtractOptions { MaxTotalSize = 1200 }).Items.Count);
    }

    [Fact]
    public void Unsupported_method_aborts_planning_before_anything_is_written()
    {
        var zip = new ZipBuilder().Add("a", Data).Add("b", Data).Build();
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(CdOf(zip) + 46 + 1 + 10), 99);   // second entry method
        Assert.Equal(ArchiveError.Unsupported, PlanError(zip));
    }

    [Fact]
    public void Encrypted_entry_aborts_planning()
    {
        var zip = new ZipBuilder().Add("a", Data).Build();
        var cd = CdOf(zip);
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(cd + 8), 1);
        Assert.Equal(ArchiveError.Unsupported, PlanError(zip));
    }
}
