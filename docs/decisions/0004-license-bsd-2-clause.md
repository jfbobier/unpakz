# 0004 – Licence: BSD-2-Clause, aligned with libarchive

Date: 2026-11-14

The project will vendor libarchive, whose code is predominantly BSD-2-Clause (a few files carry other permissive terms).
Decision (user): align with it. Our own code moves from MIT to BSD-2-Clause, copyright Jean-Francois Bobier.
Steps, done in the commit that vendors libarchive: (1) pin a released libarchive tarball with its SHA-256, (2) audit every vendored
file's header and list any that are not BSD-2-Clause in `docs/THIRD-PARTY.md`, (3) keep libarchive's `COPYING` and per-file notices
untouched, (4) replace `LICENSE`, (5) the C# RAR5 decoder keeps its existing credit to libarchive's reader (Grzegorz Antoniak).
