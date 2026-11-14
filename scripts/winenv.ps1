# Loads the MSVC x64 environment (VS 2022 Build Tools) plus its bundled CMake and Ninja into the current PowerShell session.
$root = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools"
cmd /c "`"$root\VC\Auxiliary\Build\vcvars64.bat`" >nul && set" | ForEach-Object { if ($_ -match '^([^=]+)=(.*)$') { Set-Item -Path "Env:$($Matches[1])" -Value $Matches[2] } }
$env:PATH = "$root\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin;$root\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja;$env:PATH"
