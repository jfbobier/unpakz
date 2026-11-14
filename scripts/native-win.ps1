# Builds the native code with MSVC (Release, warnings as errors) and runs every native test. Run inside the synced Windows copy.
$ErrorActionPreference = "Continue"
. .\scripts\winenv.ps1
$b = "native\build-msvc"
if (-not (Test-Path "$b\CMakeCache.txt")) { cmake -S native -B $b -G Ninja -DCMAKE_BUILD_TYPE=Release 2>&1 | Select-Object -Last 3 }
cmake --build $b 2>&1 | Select-String -Pattern "error|FAILED|warning C|fatal" | Select-Object -First 40
ctest --test-dir $b --output-on-failure 2>&1 | Select-Object -Last 30
if ($LASTEXITCODE -eq 0) { "NATIVE-PASS" } else { "NATIVE-FAIL" }
