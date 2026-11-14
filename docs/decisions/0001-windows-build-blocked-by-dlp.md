# 0001 – Windows builds from WSL are blocked by a DLP policy

Date: 2026-11-14

`powershell.exe` can read the project through `\\wsl.localhost\Arch\home\jeff\zip` and `dotnet 10.0.403` runs, but the first
restore fails when NuGet writes `obj-win\...\project.assets.json` into the share:
"This action is blocked. Please refer to the data loss prevention notification for further information."

Decision: do not work around a DLP control (no copying the tree to a Windows folder without explicit authorisation).
Consequences: nothing in this repository has been executed on Windows. Windows-only code (WinForms app, COM drag source, registry
integration) is compiled on Linux (`EnableWindowsTargeting`) but untested; its logic is kept in cross-platform, tested libraries.
To lift this: run `harness.ps1 fast` on a Windows machine that is allowed to build from a local folder.
