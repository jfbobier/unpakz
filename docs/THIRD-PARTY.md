# Third-party references

## libarchive RAR5 reader (reference for the RAR5 compression algorithm)
RARLAB's public technical note documents the RAR5 container but not its compression algorithm. `src/Unpakz.Core/Rar/Rar5Decoder.cs`
re-implements the algorithm (Huffman table layout and construction, symbol / length / distance coding, repeat-distance cache,
filters) following the behaviour of libarchive's `archive_read_support_format_rar5.c`:

    Copyright (c) 2018 Grzegorz Antoniak (http://antoniak.org)
    BSD 2-Clause "Simplified" License

The code here is our own structure (streaming window with lazy growth, exhaustive bounds checking, pure filter functions,
solid-group management); it is verified against the real RARLAB `rar`/`unrar` binaries, not against libarchive.
We did not use or copy RARLAB's unrar source (its licence forbids use to recreate the RAR compression algorithm).
