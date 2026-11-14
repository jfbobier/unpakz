#!/usr/bin/env bash
# Builds the native code (Debug + AddressSanitizer + UBSan, warnings as errors) and runs every native test.
# Prints exactly one verdict line last: NATIVE-PASS, NATIVE-FAIL or NATIVE-BUILD-ERROR (used by harness/mutate.py).
cd "$(dirname "$0")/.."
B=native/build-linux
export ASAN_OPTIONS=detect_leaks=1:abort_on_error=0 UBSAN_OPTIONS=print_stacktrace=1:halt_on_error=1
if [ ! -f $B/CMakeCache.txt ]; then
  cmake -S native -B $B -G "Unix Makefiles" -DCMAKE_BUILD_TYPE=Debug -DUNPAKZ_SANITIZE=ON </dev/null >$B.configure.log 2>&1 || { cat $B.configure.log | tail -20; echo NATIVE-BUILD-ERROR; exit 1; }
fi
if ! cmake --build $B -j"$(nproc)" </dev/null 2>&1 | tail -40 | tee $B.build.log | grep -q "error"; then :; fi
if grep -q "error" $B.build.log; then cat $B.build.log; echo NATIVE-BUILD-ERROR; exit 1; fi
if ctest --test-dir $B --output-on-failure --timeout 600 ${UNPAKZ_CTEST_FILTER:+-R "$UNPAKZ_CTEST_FILTER"} </dev/null; then echo NATIVE-PASS; else echo NATIVE-FAIL; exit 1; fi
