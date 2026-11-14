#!/usr/bin/env python3
"""Mutation harness: break guarded code on purpose, require the test suite to notice.

Usage: python3 harness/mutate.py [filter-substring | =ID1,ID2,... | --check]
Each mutant must (a) apply exactly once, (b) compile, (c) make the suite fail. Survivors fail the run unless listed
in EQUIVALENT with a reason. Source files are always restored (also on Ctrl-C / SIGTERM).
"""
import atexit, os, signal, subprocess, sys, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mutations import MUTANTS, EQUIVALENT

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
originals = {}

def restore():
    for path, text in originals.items():
        with open(path, "w", encoding="utf-8", newline="") as f:
            f.write(text)

atexit.register(restore)
for sig in (signal.SIGINT, signal.SIGTERM):
    signal.signal(sig, lambda *_: sys.exit(130))

TIMEOUT = 240   # seconds; a mutant that makes the code hang is detected, not stuck forever


def run_tests():
    try:
        p = subprocess.run(["dotnet", "test", "Unpakz.slnx", "-c", "Release", "--nologo", "-v", "q"], cwd=ROOT, capture_output=True, text=True,
                           env={**os.environ, "UNPAKZ_REQUIRE_ORACLE": "1"}, timeout=TIMEOUT)
    except subprocess.TimeoutExpired:
        subprocess.run(["pkill", "-f", "testhost"], check=False)
        return 1, False
    out = p.stdout + p.stderr
    compile_error = " error CS" in out or " error CA" in out or " error xUnit" in out or " error IDE" in out
    return p.returncode, compile_error

flt = sys.argv[1] if len(sys.argv) > 1 else ""
if flt == "--check":   # fast: only verify every mutation still applies exactly once
    stale = 0
    for mid, rel, find, _ in MUTANTS:
        with open(os.path.join(ROOT, rel), encoding="utf-8", newline="") as f:
            n = f.read().count(find)
        if n != 1:
            stale += 1; print(f"{mid:6} STALE ({n} matches) in {rel}")
    print(f"{len(MUTANTS)} mutants, {stale} stale")
    sys.exit(1 if stale else 0)
results = {"killed": 0, "survived": [], "invalid": [], "stale": []}
if flt.startswith("="):
    wanted = set(flt[1:].split(","))
    selected = [m for m in MUTANTS if m[0] in wanted]
else:
    selected = [m for m in MUTANTS if flt in m[0] or flt in m[1]]
t0 = time.time()
for mid, rel, find, repl in selected:
    path = os.path.join(ROOT, rel)
    with open(path, encoding="utf-8", newline="") as f:
        text = f.read()
    originals[path] = text
    if text.count(find) != 1:
        results["stale"].append(f"{mid} (found {text.count(find)}x)")
        print(f"{mid:6} STALE ({text.count(find)} matches)")
        continue
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write(text.replace(find, repl))
    rc, compile_error = run_tests()
    restore()
    if compile_error:
        results["invalid"].append(mid); print(f"{mid:6} INVALID (does not compile)")
    elif rc != 0:
        results["killed"] += 1; print(f"{mid:6} killed")
    else:
        results["survived"].append(mid)
        why = EQUIVALENT.get(mid)
        print(f"{mid:6} SURVIVED" + (f"  (accepted equivalent: {why})" if why else "  <-- test gap"))
    originals.pop(path)

bad = [m for m in results["survived"] if m not in EQUIVALENT] + results["invalid"] + results["stale"]
print(f"\n{len(selected)} mutants in {time.time()-t0:.0f}s: killed {results['killed']}, survived {len(results['survived'])} "
      f"(unaccepted {len([m for m in results['survived'] if m not in EQUIVALENT])}), invalid {len(results['invalid'])}, stale {len(results['stale'])}")
sys.exit(1 if bad else 0)
