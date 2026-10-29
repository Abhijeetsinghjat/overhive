# Installs an unpacked Windows release archive (overhive-<version>-win-x64.zip) for the current user:
# no admin rights and no .NET needed. The Windows counterpart of install-release.sh. From the unpacked folder:
#   powershell -ExecutionPolicy Bypass -File .\install-release.ps1
# Copies amcd\ and cli\ to %LOCALAPPDATA%\Overhive, puts amcd, agentctl and overhive on the user PATH and
# starts the daemon at logon through the per-user scheduled task "Overhive". Re-run (a newer one) to update.
# Does not touch Claude Code or other AI-tool settings (use `overhive hooks install` for that, separately).
#   -NoStartAtLogin   install only; start the daemon yourself with `amcd`
#   -Uninstall        stop and remove the task, the PATH entries and the program files (data is kept);
#                     also works later as: & "$env:LOCALAPPDATA\Overhive\install-release.ps1" -Uninstall
#   -RemoveData       with -Uninstall: also delete the data (%USERPROFILE%\.local\state\agent-mission-control)
#   -InstallDir <dir> install somewhere else (default: %LOCALAPPDATA%\Overhive, or AMC_INSTALL_DIR)
#   -InPlace          for package managers (Scoop): use the files where they are, leave PATH alone and only
#                     add (or, with -Uninstall, remove) the scheduled task
[CmdletBinding()]
param([string]$InstallDir, [switch]$NoStartAtLogin, [switch]$Uninstall, [switch]$RemoveData, [switch]$InPlace)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'overhive-windows.ps1')
$base = if ($InPlace) { $PSScriptRoot } else { Get-OverhiveInstallDir $InstallDir }
if ($Uninstall) { Uninstall-Overhive $base -RemoveData:$RemoveData -InPlace:$InPlace; exit 0 }
Install-Overhive $PSScriptRoot $base -NoStartAtLogin:$NoStartAtLogin -InPlace:$InPlace
