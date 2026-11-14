# Windows entry point (PowerShell 5.1+). Usage: .\harness.ps1 fast
param([string]$Mode = "fast")
$ErrorActionPreference = "Stop"
Set-Location -LiteralPath $PSScriptRoot
$env:DOTNET_NOLOGO = "1"
switch ($Mode) {
  "fast" {
    .\scripts\native-win.ps1                       # MSVC build of the native library + its tests (writes native\build-msvc\unpakz_native.dll)
    foreach ($p in "src\Unpakz.App", "src\Unpakz.Cli", "tests\Unpakz.Interop.Tests", "tests\Unpakz.Windows.Tests") {
      dotnet build $p -c Release -warnaserror -v q
      if ($LASTEXITCODE) { exit $LASTEXITCODE }
    }
    foreach ($p in "tests\Unpakz.Interop.Tests", "tests\Unpakz.Windows.Tests") {
      dotnet test $p -c Release --no-build -v q
      if ($LASTEXITCODE) { exit $LASTEXITCODE }
    }
  }
  default { Write-Error "usage: .\harness.ps1 fast"; exit 2 }
}
