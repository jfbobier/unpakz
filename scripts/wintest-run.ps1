# Runs the Windows test project with an xunit filter and propagates dotnet's exit code. Used by harness/mutate.py (windows mode).
param([string]$Filter = "")
$ErrorActionPreference = "Continue"
$dotnetArgs = @("test", "tests\Unpakz.Windows.Tests", "-c", "Release", "-v", "q")
if ($Filter) { $dotnetArgs += @("--filter", $Filter) }
& dotnet @dotnetArgs 2>&1 | ForEach-Object { $_.ToString() }
exit $LASTEXITCODE
