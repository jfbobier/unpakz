#!/usr/bin/env python3
"""Differential conformance: extract many archives with unpakz (native test CLI) and with an independent oracle
(unrar for RAR, 7-Zip for everything else) and require byte-identical trees.
Archives come from committed fixtures and from the real `rar` / `7z` / `zip` / `tar` tools (generated fresh, deterministic data).
Usage: python3 harness/conformance.py [--cli PATH] [--filter SUBSTRING] [--keep]
Exit code: 0 all identical, 1 any mismatch or unexpected failure."""
import argparse, hashlib, os, random, shutil, subprocess, sys, tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RAR = os.path.join(ROOT, "tools", "rar", "rar")
FIXTURES = os.path.join(ROOT, "tests", "Unpakz.Tests", "Fixtures")

def tree(path):
    out = {}
    for base, dirs, files in os.walk(path):
        for d in dirs:
            out[os.path.relpath(os.path.join(base, d), path).replace(os.sep, "/") + "/"] = None
        for f in files:
            p = os.path.join(base, f)
            if os.path.islink(p):
                continue
            with open(p, "rb") as fh:
                out[os.path.relpath(p, path).replace(os.sep, "/")] = hashlib.sha256(fh.read()).hexdigest()
    return out

def make_data(root, seed=1):
    r = random.Random(seed)
    os.makedirs(os.path.join(root, "sub", "deep"), exist_ok=True)
    os.makedirs(os.path.join(root, "emptydir"), exist_ok=True)
    def w(rel, data):
        with open(os.path.join(root, rel), "wb") as f: f.write(data)
    words = [b"alpha", b"beta", b"gamma", b"delta", b"epsilon", b"zeta", b"eta", b"theta"]
    w("text.txt", b" ".join(r.choice(words) for _ in range(60000)))
    w("random.bin", bytes(r.getrandbits(8) for _ in range(300000)))
    w("zeros.bin", bytes(1_500_000))
    w("empty.txt", b"")
    w("tiny.txt", b"hello")
    w("sub/table.csv", b"\n".join(b"%d,%d,%s" % (i, i * i, r.choice(words)) for i in range(20000)))
    w("sub/deep/audio.raw", bytes(int(128 + 100 * __import__("math").sin(i / 9.0)) & 255 for i in range(200000)))
    w("sub/deep/code.bin", bytes([0xE8, 0x10, 0, 0, 0] * 5000 + [r.getrandbits(8) for _ in range(20000)]))
    w("unicode-é日本.txt", "unicode names\n".encode() * 100)

def run(cmd, cwd=None):
    return subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, timeout=600)

def oracle_extract(archive, dest, kind):
    os.makedirs(dest, exist_ok=True)
    if kind == "tar":
        p = run(["tar", "xzf", archive, "-C", dest])
    elif kind == "rar":
        p = run(["unrar", "x", "-y", "-idq", archive, dest + "/"])
    else:
        p = run(["7z", "x", "-y", "-bd", f"-o{dest}", archive])
    return p.returncode == 0, p.stdout + p.stderr

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cli", default=os.path.join(ROOT, "native", "build-linux", "unpakz_testcli"))
    ap.add_argument("--filter", default="")
    ap.add_argument("--keep", action="store_true")
    a = ap.parse_args()
    work = tempfile.mkdtemp(prefix="unpakz-conf-")
    cases = []   # (name, archive path, oracle kind)

    # committed fixtures
    for f in sorted(os.listdir(FIXTURES)):
        p = os.path.join(FIXTURES, f)
        if f.endswith(".rar") and os.path.isfile(p):
            cases.append(("fixture/" + f, p, "rar"))
    for d in sorted(os.listdir(FIXTURES)):
        dp = os.path.join(FIXTURES, d)
        if os.path.isdir(dp):
            first = sorted(os.listdir(dp))[0]
            cases.append(("fixture-volumes/" + d, os.path.join(dp, first), "rar"))

    # generated
    src = os.path.join(work, "src"); make_data(src)
    gen = os.path.join(work, "gen"); os.makedirs(gen)
    def add(name, cmd, out, kind, cwd=src):
        r = run(cmd, cwd=cwd)
        if r.returncode not in (0, 1):   # rar/zip return 1 for warnings
            print(f"SETUP-SKIP {name}: {' '.join(cmd)} -> {r.returncode}: {(r.stdout + r.stderr)[-200:]}")
            return
        cases.append((name, out, kind))
    if os.path.exists(RAR):
        for ver in ("ma4", "ma5"):
            for m in ("m0", "m1", "m3", "m5"):
                out = os.path.join(gen, f"{ver}-{m}.rar")
                add(f"rar/{ver}-{m}", [RAR, "a", "-r", f"-{ver}", f"-{m}", "-idq", "-ep1", out, "."], out, "rar")
            out = os.path.join(gen, f"{ver}-solid.rar")
            add(f"rar/{ver}-solid", [RAR, "a", "-r", f"-{ver}", "-s", "-m3", "-idq", out, "."], out, "rar")
            out = os.path.join(gen, f"{ver}-md4m.rar")
            add(f"rar/{ver}-md4m", [RAR, "a", "-r", f"-{ver}", "-m4", "-md4m", "-idq", out, "."], out, "rar")
        out = os.path.join(gen, "ma4-ppmd.rar")
        add("rar/ma4-ppmd", [RAR, "a", "-r", "-ma4", "-m3", "-mc4a+", "-idq", out, "."], out, "rar")       # PPMd text mode
        out = os.path.join(gen, "ma5-filters.rar")
        add("rar/ma5-filters", [RAR, "a", "-r", "-ma5", "-m5", "-mcX+", "-mcD+", "-idq", out, "."], out, "rar")
        for ver in ("ma4", "ma5"):
            vd = os.path.join(gen, f"vol-{ver}"); os.makedirs(vd)
            out = os.path.join(vd, "v.rar")
            r = run([RAR, "a", "-r", f"-{ver}", "-m3", "-v200k", "-idq", out, "."], cwd=src)
            vols = sorted(f for f in os.listdir(vd) if f.endswith(".rar"))
            if vols: cases.append((f"rar/volumes-{ver}", os.path.join(vd, vols[0]), "rar"))
    out = os.path.join(gen, "t.zip"); add("zip/deflate", ["zip", "-q", "-r", "-6", out, "."], out, "7z")
    out = os.path.join(gen, "store.zip"); add("zip/store", ["zip", "-q", "-r", "-0", out, "."], out, "7z")
    out = os.path.join(gen, "7z.zip"); add("zip/7z-deflate", ["7z", "a", "-tzip", "-mx9", "-bd", "-bso0", out, "."], out, "7z")
    out = os.path.join(gen, "t.tar.gz"); add("tar.gz", ["tar", "czf", out, "."], out, "tar")
    out = os.path.join(gen, "t.tgz"); add("tgz-9", ["tar", "-I", "gzip -9", "-cf", out, "."], out, "tar")

    failures = 0
    for name, archive, kind in cases:
        if a.filter and a.filter not in name: continue
        ours = os.path.join(work, "ours", name.replace("/", "_"))
        theirs = os.path.join(work, "theirs", name.replace("/", "_"))
        ok, why = oracle_extract(archive, theirs, kind)
        p = run([a.cli, "x", archive, "-o", ours])
        if not ok:
            print(f"ORACLE-FAIL {name}: {why.strip()[-200:]} (unpakz exit {p.returncode})"); continue
        if p.returncode != 0:
            print(f"FAIL {name}: unpakz exit {p.returncode}: {p.stderr.strip()[-300:]}"); failures += 1; continue
        t1, t2 = tree(ours), tree(theirs)
        if t1 != t2:
            only1 = sorted(set(t1) - set(t2)); only2 = sorted(set(t2) - set(t1))
            diff = sorted(k for k in set(t1) & set(t2) if t1[k] != t2[k])
            print(f"MISMATCH {name}: only-unpakz={only1[:3]} only-oracle={only2[:3]} different={diff[:3]}"); failures += 1
        else:
            print(f"ok   {name} ({len(t1)} entries)")
    print(f"\n{failures} failure(s)")
    if not a.keep: shutil.rmtree(work, ignore_errors=True)
    else: print("kept", work)
    sys.exit(1 if failures else 0)

main()
