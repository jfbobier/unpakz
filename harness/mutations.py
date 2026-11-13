"""Mutation catalogue: (id, file, exact text to find (must occur once), replacement)."""
PG = "src/Unpakz.Engine/PathGuard.cs"
ZCD = "src/Unpakz.Core/Zip/ZipCentralDirectory.cs"
ZEE = "src/Unpakz.Core/Zip/ZipEntryExtractor.cs"
CRC = "src/Unpakz.Core/Checksums/Crc32.cs"
PLN = "src/Unpakz.Engine/ExtractionPlanner.cs"
EXT = "src/Unpakz.Engine/ZipExtractor.cs"
LFS = "src/Unpakz.Engine/LocalFileSystem.cs"
DOS = "src/Unpakz.Core/Zip/DosTime.cs"

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
    ("L1", PLN, "if (type == SymlinkType) { skipped.Add(new SkippedEntry(e.Name, SkipReason.Symlink)); continue; }", ""),
    ("L2", PLN, "if (type is not (0 or RegularType or DirectoryType)) { skipped.Add(new SkippedEntry(e.Name, SkipReason.SpecialFile)); continue; }", ""),
    ("L3", PLN, "if (options.SkipUnsafePaths) {", "if (true) {"),
    ("L4", PLN, "if ((long)destinationRoot.Length + 1 + rel.Length > options.MaxFullPathLength)", "if (false)"),
    ("L5", PLN, 'if (e.UncompressedSize != 0 || e.CompressedSize != 0) throw new ArchiveException("Directory entry has data.");', ""),
    ("L6", PLN, 'if (e.IsEncrypted) throw new ArchiveException("Encrypted entries are not supported yet.", ArchiveError.Unsupported);', ""),
    ("L7", PLN, "if (e.Method is not (0 or 8))", "if (false)"),
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
    ("X11", EXT, "DateTime? mtime = options.RestoreTimestamps && DosTime.TryConvert(entry.DosDateTime, out var t) ? t : null;", "DateTime? mtime = null;"),
    ("X12", EXT, "var prealloc = declared <= options.PreallocateLimit ? declared : 0;", "var prealloc = declared;"),
    ("X13", EXT, 'if (kind == PathKind.File) throw new ArchiveException("Destination is a file.", ArchiveError.Conflict);', ""),
    ("X14", EXT, "case PathKind.File:", "case (PathKind)97:"),
    # --- real file system
    ("F2", LFS, "if ((a & FileAttributes.ReparsePoint) != 0) return PathKind.ReparsePoint;", ""),
    ("F5", LFS, "if (lastWriteLocal is { } t) File.SetLastWriteTime(temp, t);", ""),
    ("F6", LFS, "try { File.Delete(temp); }", "try { }"),
    # --- DOS time
    ("D2", DOS, "if (day > DateTime.DaysInMonth(year, month)) return false;", ""),
    ("D3", DOS, "hour > 23", "hour > 24"),
    ("D4", DOS, "second > 59", "second > 60"),
    ("D5", DOS, "minute > 59", "minute > 60"),
]

# Mutants that survive by design. Each needs a reason a reviewer can challenge.
EQUIVALENT = {
}
