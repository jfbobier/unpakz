namespace Zipx.Core.Zip;

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
}
