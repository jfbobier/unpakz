namespace Unpakz.Core.Zip;

/// <summary>One validated central-directory record. Sizes/offsets are already ZIP64-resolved.</summary>
public sealed record ZipEntryInfo(
    string Name,
    ushort VersionMadeBy,
    ushort Flags,
    ushort Method,
    uint DosDateTime,
    uint Crc32,
    ulong CompressedSize,
    ulong UncompressedSize,
    ulong LocalHeaderOffset,
    uint ExternalAttributes)
{
    public bool IsEncrypted => (Flags & 1) != 0;

    public bool IsDirectory => Name.Length > 0 && Name[^1] is '/' or '\\';

    /// <summary>POSIX file-type bits (S_IFMT) when the entry was made on Unix, otherwise 0 (unknown / regular).</summary>
    public uint UnixFileType => (VersionMadeBy >> 8) == 3 ? (ExternalAttributes >> 16) & 0xF000 : 0;
}
