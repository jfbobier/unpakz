#!/usr/bin/env python3
"""Mutation harness: break guarded code on purpose, require the test suite to notice.

Usage: python3 harness/mutate.py [filter-substring | =ID1,ID2,... | --check]
Each mutant must (a) apply exactly once, (b) compile, (c) make the suite fail. Survivors fail the run unless listed
in EQUIVALENT with a reason. Source files are always restored (also on Ctrl-C / SIGTERM).
"""
import atexit, os, signal, subprocess, sys, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
if os.environ.get("UNPAKZ_MUTATE_MODE") == "native":
    from native_mutations import MUTANTS, EQUIVALENT
else:
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
MODE = os.environ.get("UNPAKZ_MUTATE_MODE", "linux")      # "windows": run the Windows test project in the synced Downloads copy
WIN_FILTER = os.environ.get("UNPAKZ_MUTATE_WIN_FILTER", "FullyQualifiedName~DataObjectTests|FullyQualifiedName~RegistryTests")


def run_tests(rel=None):
    """Returns (verdict, output). Verdicts: killed, survived, invalid, hang. Anything else is an infrastructure failure and aborts the run."""
    if MODE == "native":
        cmd = ["bash", "scripts/native-test.sh"]
        limit = TIMEOUT * 2
        if rel and rel.endswith("rar5.cpp"): os.environ["UNPAKZ_CTEST_FILTER"] = "rar5_test|rar5_container_test|rar5_decoder_test|api_test"
        else: os.environ.pop("UNPAKZ_CTEST_FILTER", None)
    elif MODE == "windows":
        cmd = ["bash", "scripts/winsync.sh", ".\\scripts\\wintest-run.ps1 -Filter '%s'" % WIN_FILTER]
        limit = TIMEOUT * 2
    else:
        cmd = ["dotnet", "test", "Unpakz.slnx", "-c", "Release", "--nologo", "-v", "q"]
        limit = TIMEOUT
    try:
        p = subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True, timeout=limit, env={**os.environ, "UNPAKZ_REQUIRE_ORACLE": "1"})
    except subprocess.TimeoutExpired:
        subprocess.run(["pkill", "-f", "testhost"], check=False)
        if MODE == "windows":
            subprocess.run(["powershell.exe", "-NoProfile", "-Command", "Get-Process testhost -ErrorAction SilentlyContinue | Stop-Process -Force"], check=False)
        return "hang", ""
    out = p.stdout + p.stderr
    if MODE == "native":
        if "NATIVE-BUILD-ERROR" in out: return "invalid", out
        if "NATIVE-FAIL" in out: return "killed", out
        if "NATIVE-PASS" in out: return "survived", out
        return "infra", out
    if any(m in out for m in (" error CS", " error CA", " error xUnit", " error IDE", " error CS")):
        return "invalid", out
    if "Failed!" in out or "Test Run Failed" in out or "Test host process crashed" in out or "test run was aborted" in out.lower():
        return "killed", out
    if "Passed!" in out and p.returncode == 0:
        return "survived", out
    return "infra", out


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
# A kill only means something if the unmodified tree passes. Check that first, so a red baseline cannot masquerade as a killed mutant.
baseline, baseline_out = run_tests()
if baseline != "survived":
    print(f"BASELINE IS NOT GREEN (verdict: {baseline}); no mutant can be judged. Fix the tree first.")
    print(baseline_out[-1500:])
    sys.exit(4)
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
    verdict, out = run_tests(rel)
    restore()
    if verdict == "infra":
        print(f"{mid:6} INFRASTRUCTURE FAILURE: neither a pass nor a fail was reported; aborting so no verdict is invented.")
        print(out[-1500:])
        sys.exit(3)
    if verdict == "invalid":
        results["invalid"].append(mid); print(f"{mid:6} INVALID (does not compile)")
    elif verdict in ("killed", "hang"):
        results["killed"] += 1; print(f"{mid:6} killed" + ("  (hang: timeout)" if verdict == "hang" else ""))
    else:
        results["survived"].append(mid)
        why = EQUIVALENT.get(mid)
        print(f"{mid:6} SURVIVED" + (f"  (accepted equivalent: {why})" if why else "  <-- test gap"))
    originals.pop(path)

bad = [m for m in results["survived"] if m not in EQUIVALENT] + results["invalid"] + results["stale"]
print(f"\n{len(selected)} mutants in {time.time()-t0:.0f}s: killed {results['killed']}, survived {len(results['survived'])} "
      f"(unaccepted {len([m for m in results['survived'] if m not in EQUIVALENT])}), invalid {len(results['invalid'])}, stale {len(results['stale'])}")
if MODE == "windows":   # never leave a mutated tree in the Windows copy
    subprocess.run(["bash", "scripts/winsync.sh", "'restored'"], cwd=ROOT, check=False, capture_output=True)
sys.exit(1 if bad else 0)
