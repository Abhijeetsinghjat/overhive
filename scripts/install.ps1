# Builds Overhive from source and installs it for the current user (no admin rights), the Windows
# counterpart of install.sh. Needs the .NET 10 SDK. Run from a clone:
#   powershell -ExecutionPolicy Bypass -File scripts\install.ps1
# Installs into %LOCALAPPDATA%\Overhive (amcd\, cli\), puts amcd, agentctl and overhive on the user PATH and
# starts the daemon at logon through the per-user scheduled task "Overhive". Re-run it to update.
# Does not touch Claude Code or other AI-tool settings (use `overhive hooks install` for that, separately).
#   -NoStartAtLogin   install only; start the daemon yourself with `amcd`
#   -Uninstall        stop and remove the task, the PATH entries and the program files (data is kept)
#   -RemoveData       with -Uninstall: also delete the data (%USERPROFILE%\.local\state\agent-mission-control)
#   -InstallDir <dir> install somewhere else (default: %LOCALAPPDATA%\Overhive, or AMC_INSTALL_DIR)
[CmdletBinding()]
param([string]$InstallDir, [switch]$NoStartAtLogin, [switch]$Uninstall, [switch]$RemoveData)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'overhive-windows.ps1')
$base = Get-OverhiveInstallDir $InstallDir
if ($Uninstall) { Uninstall-Overhive $base -RemoveData:$RemoveData; exit 0 }

$repo = Split-Path -Parent $PSScriptRoot
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "dotnet not found: install the .NET 10 SDK from https://dotnet.microsoft.com/download" }
$arch = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
$rid = if ($arch -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }
# Same build as the release archives (scripts/package.sh): self-contained single files, so the installed
# programs do not depend on where the SDK is. Built into a staging folder first: a failed build leaves the
# current install untouched.
$stage = Join-Path $base 'staging'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
foreach ($p in @(@('Amc.Daemon', 'amcd'), @('Amc.Cli', 'cli'))) {
    Write-Host "Building $($p[0]) ($rid)..."
    & dotnet publish (Join-Path $repo "src\$($p[0])") -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
        -o (Join-Path $stage $p[1]) --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $($p[0]) failed (exit $LASTEXITCODE)" }
}
try { Install-Overhive $stage $base -NoStartAtLogin:$NoStartAtLogin }
finally { if (Test-Path $stage) { Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue } }
