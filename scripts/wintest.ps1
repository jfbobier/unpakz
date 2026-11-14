# Runs one filtered test run on Windows and prints only the interesting lines. Usage (inside the synced copy): .\scripts\wintest.ps1 "<filter>"
param([string]$Filter, [string]$Pattern = "sticks out|text is|overlaps|Assert\.|Failure|Passed!|Failed!")
$log = Join-Path $env:TEMP ("unpakz-wintest-" + [guid]::NewGuid().ToString("N").Substring(0,6) + ".log")
cmd /c "dotnet test tests\Unpakz.Windows.Tests -c Release --no-build --filter `"$Filter`" --logger console;verbosity=detailed > `"$log`" 2>&1"
Get-Content $log | Where-Object { $_ -match $Pattern } | Select-Object -First 14 | ForEach-Object { $_.Trim().Substring(0, [Math]::Min(330, $_.Trim().Length)) }
"--- " + ((Get-Content $log | Select-String -Pattern "Total tests|Passed|Failed" | Select-Object -Last 4) -join " ")
