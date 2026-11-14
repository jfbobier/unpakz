# Builds the release zips from a clean tree on Windows: unpakz-app-win-x64.zip, unpakz-cli-win-x64.zip, SHA256SUMS.txt (in artifacts\).
param([string]$Version = "0.2.0")
$ErrorActionPreference = "Stop"
Set-Location -LiteralPath (Split-Path $PSScriptRoot -Parent)
.\scripts\native-win.ps1 | Select-Object -Last 1
foreach ($p in "src\Unpakz.App", "src\Unpakz.Cli") { dotnet build $p -c Release -warnaserror -v q; if ($LASTEXITCODE) { throw "build failed: $p" } }
Remove-Item -Recurse -Force artifacts -ErrorAction SilentlyContinue
New-Item -ItemType Directory artifacts\app, artifacts\cli | Out-Null
$dlls = "Unpakz.Interop.dll", "unpakz_native.dll"
Copy-Item "bin-win\Unpakz.App\Release\net48\Unpakz.exe", "bin-win\Unpakz.App\Release\net48\Unpakz.exe.config", "bin-win\Unpakz.App\Release\net48\Unpakz.Presentation.dll", "bin-win\Unpakz.App\Release\net48\Unpakz.Windows.dll" artifacts\app
Copy-Item "bin-win\Unpakz.Cli\Release\net48\unpakz.exe", "bin-win\Unpakz.Cli\Release\net48\unpakz.exe.config" artifacts\cli
foreach ($d in $dlls) { Copy-Item "bin-win\Unpakz.App\Release\net48\$d" artifacts\app; Copy-Item "bin-win\Unpakz.Cli\Release\net48\$d" artifacts\cli }
foreach ($dir in "app", "cli") {
  Copy-Item LICENSE, docs\THIRD-PARTY.md "artifacts\$dir"
  Copy-Item third_party\libarchive\COPYING "artifacts\$dir\LICENSE-libarchive.txt"      # BSD-2-Clause requires the notices to travel with binaries
  Copy-Item third_party\zlib-ng\LICENSE.md "artifacts\$dir\LICENSE-zlib-ng.txt"
}
Compress-Archive -Force -Path artifacts\app\* -DestinationPath artifacts\unpakz-app-win-x64.zip
Compress-Archive -Force -Path artifacts\cli\* -DestinationPath artifacts\unpakz-cli-win-x64.zip
Get-FileHash artifacts\*.zip -Algorithm SHA256 | % { $_.Hash.ToLower() + "  " + (Split-Path $_.Path -Leaf) } | Set-Content artifacts\SHA256SUMS.txt -Encoding ascii
Get-Content artifacts\SHA256SUMS.txt
