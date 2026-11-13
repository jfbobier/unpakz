"""Mutation catalogue: (id, file, exact text to find (must occur once), replacement)."""
PG = "src/Unpakz.Engine/PathGuard.cs"
ZCD = "src/Unpakz.Core/Zip/ZipCentralDirectory.cs"
ZEE = "src/Unpakz.Core/Zip/ZipEntryExtractor.cs"
CRC = "src/Unpakz.Core/Checksums/Crc32.cs"
PLN = "src/Unpakz.Engine/ExtractionPlanner.cs"
EXT = "src/Unpakz.Engine/ArchiveExtractor.cs"
LFS = "src/Unpakz.Engine/LocalFileSystem.cs"
DOS = "src/Unpakz.Core/Zip/DosTime.cs"
ZAR = "src/Unpakz.Core/Zip/ZipArchive.cs"
RAR = "src/Unpakz.Core/Rar/Rar5Archive.cs"
VIN = "src/Unpakz.Core/Rar/VInt.cs"
STO = "src/Unpakz.Core/StoredData.cs"

MUTANTS = [
    # --- PathGuard
    ("G1", PG, 'if (segment is "..") return Fail(PathRejection.Traversal);', ''),
    ("G2", PG, "or ':' or '\"'", "or '\"'"),
    ("G3", PG, "var dot = segment.IndexOf('.');", "var dot = -1;"),
    ("G4", PG, "if (entryName.Length >= 2 && IsAsciiLetter(entryName[0]) && entryName[1] == ':') return Fail(PathRejection.Rooted);", ""),
    ("G5", PG, "if (entryName[0] is '/' or '\\\\') return Fail(PathRejection.Rooted);", ""),
    ("G6", PG, "if (segment[^1] is '.' or ' ') return PathRejection.TrailingDotOrSpace;", ""),
    ("G7", PG, "if (segment.Length > options.MaxSegmentLength) return PathRejection.TooLong;", ""),
    ("G8", PG, "if (depth > options.MaxDepth) return Fail(PathRejection.TooDeep);", ""),
    ("G9", PG, "if (result.Length > options.MaxPathLength) return Fail(PathRejection.TooLong);", ""),
    ("G10", PG, "return d is >= '1' and <= '9' or '\\u00B9' or '\\u00B2' or '\\u00B3';", "return d is >= '1' and <= '9';"),
    ("G11", PG, "if (c < ' ' || c == 0x7F ||", "if (c == 0x7F ||"),
    # --- ZIP central directory
    ("P1", ZCD, "if (count > cdSize / CentralFixed)", "if (false)"),
    ("P2", ZCD, "if (pos + EocdSize + commentLen == zip.Length) return pos;", "return pos;"),
    ("P3", ZCD, 'if (extra.Length - 4 < size) throw new ArchiveException("Extra field overruns its container.");', ""),
    ("P4", ZCD, "if (localOffset > cdStart || cdStart - localOffset < LocalFixed)", "if (false)"),
    ("P5", ZCD, "if (method == 0 && (flags & 1) == 0 && csize != usize)", "if (false)"),
    # --- entry extractor
    ("E1", ZEE, 'if (crc != entry.Crc32) throw new ArchiveException("CRC-32 mismatch.");', ""),
    ("E2", ZEE, "if (entry.UncompressedSize > entry.CompressedSize * MaxDeflateRatio + 64)", "if (false)"),
    ("E3", ZEE, "if ((ulong)n > expected - total) throw", "if (false) throw"),
    ("E4", ZEE, "if (total != expected) throw", "if (false) throw"),
    ("E5", ZEE, "if (start > len || entry.CompressedSize > len - start) throw", "if (false) throw"),
    ("E6", ZEE, "if (entry.UncompressedSize > (ulong)limits.MaxEntrySize)", "if (false)"),
    ("E7", ZEE, "if (entry.Method is not (0 or 8))", "if (false)"),
    ("E8", ZEE, "if (entry.IsEncrypted) throw", "if (false) throw"),
    ("E9", ZEE, 'if (entry.UncompressedSize != 0) throw new ArchiveException("Directory entry has data.");', ""),
    ("E10", ZEE, "if (BinaryPrimitives.ReadUInt32LittleEndian(h) != LocalSig) throw", "if (false) throw"),
    ("E11", ZEE, "if (lho > len || len - lho < LocalFixed) throw", "if (false) throw"),
    # --- CRC
    ("C1", CRC, "^ t[(int)(hi >> 24)];", ";"),
    ("C2", CRC, "for (; i < data.Length; i++)", "for (; i < data.Length - 1; i++)"),
    # --- planner
    ("L1", PLN, "if (e.Kind == EntryKind.Symlink) { skipped.Add(new SkippedEntry(e.Name, SkipReason.Symlink)); continue; }", ""),
    ("L2", PLN, "if (e.Kind == EntryKind.Special) { skipped.Add(new SkippedEntry(e.Name, SkipReason.SpecialFile)); continue; }", ""),
    ("L3", PLN, "if (options.SkipUnsafePaths) {", "if (true) {"),
    ("L4", PLN, "if ((long)destinationRoot.Length + 1 + rel.Length > options.MaxFullPathLength)", "if (false)"),
    ("L5", PLN, 'if (e.UncompressedSize != 0 || e.CompressedSize != 0) throw new ArchiveException("Directory entry has data.");', ""),
    ("L6", ZAR, 'if (z.IsEncrypted) throw new ArchiveException("Encrypted entries are not supported yet.", ArchiveError.Unsupported);', ""),
    ("L7", ZAR, "if (z.Method is not (0 or 8))", "if (false)"),
    ("L16", PLN, "archive.CheckSupported(e);", ""),
    ("ZA1", ZAR, "if (type == SymlinkType) return EntryKind.Symlink;", ""),
    ("ZA2", ZAR, "if (type is not (0 or RegularType or DirectoryType)) return EntryKind.Special;", ""),
    ("ZA3", ZAR, "return z.IsDirectory || type == DirectoryType ? EntryKind.Directory : EntryKind.File;", "return z.IsDirectory ? EntryKind.Directory : EntryKind.File;"),
    ("ZA4", ZAR, "return (z.LocalHeaderOffset, start + length);", "return (start, start + length);"),
    ("ZA5", ZAR, "DosTime.TryConvert(z.DosDateTime, out var t) ? t : null", "null"),
    ("L8", PLN, "if (total > options.MaxTotalSize)", "if (false)"),
    ("L9", PLN, "var kinds = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);", "var kinds = new Dictionary<string, bool>(StringComparer.Ordinal);"),
    ("L10", PLN, "if (kinds.TryGetValue(prefix, out var prefixIsDir)) { if (!prefixIsDir) return false; }", "if (kinds.TryGetValue(prefix, out var prefixIsDir)) { }"),
    ("L11", PLN, "if (isDir && existingIsDir) { duplicateDirectory = true; return true; }", "if (isDir && existingIsDir) { duplicateDirectory = false; return true; }"),
    ("L13", PLN, "if (ranges[i].Start < ranges[i - 1].End)", "if (ranges[i].Start <= ranges[i - 1].End)"),
    ("L14", PLN, "if (duplicateDirectory) continue;", ""),
    ("L15", PLN, "if (ranges[i].Start < ranges[i - 1].End)", "if (ranges[i].Start < ranges[i - 1].Start)"),
    # --- executor
    ("X1", EXT, "if (slash > 0) EnsureDirectory(fs, root, item.RelativePath[..slash], verified);", ""),
    ("X2", EXT, "case PathKind.ReparsePoint:", "case (PathKind)99:"),
    ("X3", EXT, "if (options.Overwrite == OverwritePolicy.Fail)", "if (false)"),
    ("X4", EXT, "if (options.Overwrite == OverwritePolicy.Skip)", "if (options.Overwrite == OverwritePolicy.Fail)"),
    ("X5", EXT, "case PathKind.Directory:", "case (PathKind)98:"),
    ("X6", EXT, "if (!full.StartsWith(prefix, cmp)) throw", "if (false) throw"),
    ("X8", EXT, "writer.Commit(mtime, options.Overwrite == OverwritePolicy.Overwrite);", "writer.Commit(mtime, true);"),
    ("X9", EXT, "ct.ThrowIfCancellationRequested();\n            writer.Write(data);", "_ = ct;\n            writer.Write(data);"),
    ("X10", EXT, "cancellationToken.ThrowIfCancellationRequested();\n            var full", "var full"),
    ("X11", EXT, "DateTime? mtime = options.RestoreTimestamps ? entry.LastWriteLocal : null;", "DateTime? mtime = null;"),
    ("X12", EXT, "var prealloc = declared <= options.PreallocateLimit ? declared : 0;", "var prealloc = declared;"),
    ("X13", EXT, 'if (kind == PathKind.File) throw new ArchiveException("Destination is a file.", ArchiveError.Conflict);', ""),
    ("X14", EXT, "case PathKind.File:", "case (PathKind)97:"),
    # --- real file system
    ("F2", LFS, "if ((a & FileAttributes.ReparsePoint) != 0) return PathKind.ReparsePoint;", ""),
    ("F5", LFS, "if (lastWriteLocal is { } t) File.SetLastWriteTime(temp, t);", ""),
    ("F6", LFS, "try { File.Delete(temp); }", "try { }"),
    # --- RAR vint / stored data
    ("V1", VIN, "if (i == 9 && payload > 1) return false;", ""),
    ("V2", VIN, "i < data.Length && i < 10;", "i < data.Length && i < 11;"),
    ("V3", VIN, "value |= payload << (7 * i);", "value = payload << (7 * i);"),
    ("V4", VIN, "if ((b & 0x80) == 0)", "if ((b & 0x80) != 0)"),
    ("S1", STO, "crc = Checksums.Crc32.Update(crc, chunk);", ""),
    ("S2", STO, "sink.Write(chunk);   // straight from the archive bytes: no intermediate copy", ""),
    ("S3", STO, "Math.Min(Chunk, data.Length - pos)", "Chunk"),
    # --- RAR5 container
    ("RA1", RAR, 'if (len - pos < 5) throw new ArchiveException("Truncated RAR block header.");', ""),
    ("RA2", RAR, 'if (headerSize == 0 || headerSize > MaxHeaderSize) throw new ArchiveException("RAR header size out of range.");', ""),
    ("RA3", RAR, 'if (headerEnd > len) throw new ArchiveException("RAR header extends past the archive.");', ""),
    ("RA4", RAR, "if (Crc32.Compute(span[(int)(pos + 4)..(int)headerEnd]) != crcStored)", "if (false)"),
    ("RA5", RAR, 'if (extraSize > (ulong)c.Remaining) throw new ArchiveException("RAR extra area larger than its header.");', ""),
    ("RA6", RAR, 'if (dataSize > (ulong)(len - headerEnd)) throw new ArchiveException("RAR data area extends past the archive.");', ""),
    ("RA7", RAR, "if ((flags & 0x18) != 0) throw", "if (false) throw"),
    ("RA8", RAR, 'if (type != 1) throw new ArchiveException("RAR main header missing.");', ""),
    ("RA9", RAR, 'if (!first) throw new ArchiveException("Duplicate RAR main header.");', ""),
    ("RA10", RAR, "if (files.Count >= limits.MaxEntries) throw", "if (false) throw"),
    ("RA11", RAR, 'if (!sawEnd) throw new ArchiveException("RAR end-of-archive block missing.");', ""),
    ("RA12", RAR, "if ((archiveFlags & 0x01) != 0) throw", "if (false) throw"),
    ("RA13", RAR, 'if (nameLen > (ulong)c.Remaining) throw new ArchiveException("RAR file name overruns its header.");', ""),
    ("RA14", RAR, 'if (c.Pos > extraStart) throw new ArchiveException("RAR file name overlaps the extra area.");', ""),
    ("RA15", RAR, 'if (size == 0 || size > (ulong)extra.Remaining) throw new ArchiveException("Malformed RAR extra record.");', 'if (size == 0) throw new ArchiveException("Malformed RAR extra record.");'),
    ("RA16", RAR, "case 1: encrypted = true; break;", "case 1: break;"),
    ("RA17", RAR, "case 5: redirected = true; break;", "case 5: break;"),
    ("RA18", RAR, "mtime = t ?? mtime;", "mtime = t is null ? mtime : mtime;"),
    ("RA19", RAR, 'if (f.Encrypted) throw', 'if (false) throw'),
    ("RA20", RAR, 'if (f.UnknownSize) throw', 'if (false) throw'),
    ("RA21", RAR, 'if (f.Method != 0) throw', 'if (false) throw'),
    ("RA22", RAR, 'if (f.Crc32 is null) throw', 'if (false) throw'),
    ("RA23", RAR, 'if (f.UnpackedSize > (ulong)limits.MaxEntrySize) throw', 'if (false) throw'),
    ("RA24", RAR, 'if (f.PackedSize != f.UnpackedSize) throw', 'if (false) throw'),
    ("RA25", RAR, 'if (crc != f.Crc32) throw new ArchiveException("CRC-32 mismatch.");', ""),
    ("RA26", RAR, 'if (f.UnpackedSize != 0) throw new ArchiveException("Directory entry has data.");', ""),
    ("RA27", RAR, "if (redirected) return EntryKind.Symlink;", ""),
    ("RA28", RAR, "if (directory) return EntryKind.Directory;", ""),
    ("RA29", RAR, "if (host != 1) return EntryKind.File;", ""),
    ("RA30", RAR, "0xA000 => EntryKind.Symlink,", "0xA000 => EntryKind.File,"),
    ("RA31", RAR, "0x4000 => EntryKind.Directory,", "0x4000 => EntryKind.File,"),
    ("RA32", RAR, "_ => EntryKind.Special,", "_ => EntryKind.File,"),
    ("RA33", RAR, "return ((ulong)f.BlockStart, (ulong)f.DataStart + f.PackedSize);", "return ((ulong)f.DataStart, (ulong)f.DataStart + f.PackedSize);"),
    ("RA34", RAR, "fileTime <= MaxFileTime ? DateTime.FromFileTimeUtc((long)fileTime).ToLocalTime() : null;", "DateTime.FromFileTimeUtc((long)fileTime).ToLocalTime();"),
    ("RA35", RAR, 'if (type == 4) throw new ArchiveException("Encrypted RAR headers are not supported.", ArchiveError.Unsupported);', ""),
    ("RA36", RAR, "case 4:", "case 400:"),
    ("RA37", RAR, "if ((flags & 4) == 0)", "if (false)"),
    ("RA38", RAR, 'if (!HasSignature(span)) throw new ArchiveException("Not a RAR 5 archive.");', ""),
    ("RA39", RAR, "if (f.Kind != EntryKind.File) return;", ""),
    # --- DOS time
    ("D2", DOS, "if (day > DateTime.DaysInMonth(year, month)) return false;", ""),
    ("D3", DOS, "hour > 23", "hour > 24"),
    ("D4", DOS, "second > 59", "second > 60"),
    ("D5", DOS, "minute > 59", "minute > 60"),
]

# Mutants that survive by design. Each needs a reason a reviewer can challenge.
EQUIVALENT = {
}
