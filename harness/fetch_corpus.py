#!/usr/bin/env python3
"""Builds corpus/external/libarchive/ from libarchive's own RAR test vectors (uuencoded in its release tarball).
These are third-party, untrusted data: never executed, opened only by the test harness. Each file is recorded with a SHA-256 in
MANIFEST.txt. Usage: python3 harness/fetch_corpus.py <libarchive-3.8.11.tar.xz>   (the tarball is the one pinned in docs/THIRD-PARTY.md)"""
import binascii, hashlib, os, sys, tarfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PINNED = "a3dc0872b1b39da34dc18ef1b944f0f11ffd37425d70ae04be679279f1cf43d2"

def uudecode(text):
    out = bytearray(); started = False
    for line in text.splitlines():
        if line.startswith("begin "): started = True; continue
        if not started: continue
        if line.strip() == "end": break
        if not line or line == "`": continue
        out += binascii.a2b_uu(line.encode("latin-1"))
    return bytes(out)

def main():
    tar = sys.argv[1]
    h = hashlib.sha256(open(tar, "rb").read()).hexdigest()
    if h != PINNED: sys.exit(f"tarball SHA-256 {h} does not match the pinned {PINNED}")
    dest = os.path.join(ROOT, "corpus", "external", "libarchive"); os.makedirs(dest, exist_ok=True)
    manifest = []
    with tarfile.open(tar) as t:
        for m in t.getmembers():
            n = os.path.basename(m.name)
            if m.isfile() and "/libarchive/test/" in m.name and n.startswith("test_read_format_") and (".rar" in n or ".exe" in n) and n.endswith(".uu"):
                data = uudecode(t.extractfile(m).read().decode("latin-1"))
                out = n[len("test_read_format_"):-3]
                if out.endswith(".Z"): continue           # a compressed container, not an archive we read directly
                open(os.path.join(dest, out), "wb").write(data)
                manifest.append(f"{hashlib.sha256(data).hexdigest()}  {out}")
    open(os.path.join(dest, "MANIFEST.txt"), "w").write("\n".join(sorted(manifest, key=lambda l: l.split()[1])) + "\n")
    print(len(manifest), "files written to", dest)
main()
