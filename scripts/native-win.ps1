# Builds the native code with MSVC (Release, warnings as errors) and runs every native test. Run inside the synced Windows copy.
# Leaves native\build-msvc\unpakz_native.dll, which the managed projects copy next to their executables.
$ErrorActionPreference = "Continue"
. .\scripts\winenv.ps1
$b = "native\build-msvc"
# Configure every time: it is cheap once the cache exists (the first configure of libarchive takes minutes) and it picks up CMake changes.
cmake -S native -B $b -G Ninja -DCMAKE_BUILD_TYPE=Release 2>&1 | Select-Object -Last 2
cmake --build $b 2>&1 | Select-String -Pattern "error|FAILED|warning C|fatal" | Select-Object -First 40
if ($LASTEXITCODE -ne 0 -and -not $?) { "NATIVE-BUILD-ERROR"; exit 1 }
ctest --test-dir $b --output-on-failure 2>&1 | Select-Object -Last 30
if ($LASTEXITCODE -eq 0) { "NATIVE-PASS" } else { "NATIVE-FAIL"; exit 1 }
