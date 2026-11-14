# Windows entry point (PowerShell 5.1+). Usage: .\harness.ps1 fast
param([string]$Mode = "fast")
$ErrorActionPreference = "Stop"
Set-Location -LiteralPath $PSScriptRoot
$env:DOTNET_NOLOGO = "1"
switch ($Mode) {
  "fast" {
    .\scripts\native-win.ps1                       # MSVC build of the native library + its tests (writes native\build-msvc\unpakz.dll)
    dotnet build Unpakz.Windows.slnx -c Release -warnaserror -v q
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    dotnet test Unpakz.Windows.slnx -c Release --no-build -v q
    exit $LASTEXITCODE
  }
  default { Write-Error "usage: .\harness.ps1 fast"; exit 2 }
}
