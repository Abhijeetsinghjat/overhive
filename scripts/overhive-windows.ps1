# Shared by install.ps1 and install-release.ps1 (dot-sourced; the Windows counterpart of service.sh).
# Installs per user, without admin rights:
#   <InstallDir>\amcd\amcd.exe, <InstallDir>\cli\agentctl.exe (+ overhive.exe, the same program)
#   both folders on the user PATH (HKCU\Environment)
#   a per-user scheduled task "Overhive" that starts amcd at logon, hidden, and restarts it after a crash.
# Logs: <InstallDir>\logs\amcd.log (the previous run is kept as amcd.prev.log).
# Never touches Claude Code or other AI-tool settings (use `overhive hooks install` for that, separately).
# Works in Windows PowerShell 5.1 and PowerShell 7.

$script:OverhiveTaskName = 'Overhive'
$script:OverhiveUrl = 'http://127.0.0.1:47821'

function Get-OverhiveInstallDir([string]$InstallDir) {
    if ($InstallDir) { return [IO.Path]::GetFullPath($InstallDir).TrimEnd('\') }
    if ($env:AMC_INSTALL_DIR) { return [IO.Path]::GetFullPath($env:AMC_INSTALL_DIR).TrimEnd('\') }
    return (Join-Path $env:LOCALAPPDATA 'Overhive')
}

function Get-OverhiveDataDir {
    if ($env:AMC_DataDir) { return $env:AMC_DataDir }
    $state = if ($env:XDG_STATE_HOME) { $env:XDG_STATE_HOME } else { Join-Path $env:USERPROFILE '.local\state' }
    return (Join-Path $state 'agent-mission-control')
}

# --- user PATH (HKCU\Environment), keeping REG_EXPAND_SZ so entries like %USERPROFILE%\bin keep working ---

function Get-OverhiveUserPath {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment')
    try { return [string]$key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
    finally { $key.Close() }
}

function Set-OverhiveUserPath([string]$Value) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
    try {
        if ($Value) { $key.SetValue('Path', $Value, [Microsoft.Win32.RegistryValueKind]::ExpandString) }
        else { $key.DeleteValue('Path', $false) }
    } finally { $key.Close() }
    # Tell Explorer and new terminals that the environment changed (each call broadcasts WM_SETTINGCHANGE).
    [Environment]::SetEnvironmentVariable('OVERHIVE_ENV_REFRESH', '1', 'User')
    [Environment]::SetEnvironmentVariable('OVERHIVE_ENV_REFRESH', $null, 'User')
}

function Test-SamePath([string]$A, [string]$B) {
    $a = [Environment]::ExpandEnvironmentVariables($A).TrimEnd('\')
    $b = [Environment]::ExpandEnvironmentVariables($B).TrimEnd('\')
    return [string]::Equals($a, $b, [StringComparison]::OrdinalIgnoreCase)
}

function Add-OverhiveUserPath([string[]]$Dirs) {
    $entries = @((Get-OverhiveUserPath) -split ';' | Where-Object { $_ })
    $changed = $false
    foreach ($d in $Dirs) {
        if (-not ($entries | Where-Object { Test-SamePath $_ $d })) { $entries += $d; $changed = $true }
        if (-not (($env:Path -split ';') | Where-Object { $_ -and (Test-SamePath $_ $d) })) { $env:Path = "$env:Path;$d" }
    }
    if ($changed) { Set-OverhiveUserPath ($entries -join ';') }
}

function Remove-OverhiveUserPath([string[]]$Dirs) {
    $entries = @((Get-OverhiveUserPath) -split ';' | Where-Object { $_ })
    $kept = @($entries | Where-Object { $e = $_; -not ($Dirs | Where-Object { Test-SamePath $e $_ }) })
    if ($kept.Count -ne $entries.Count) { Set-OverhiveUserPath ($kept -join ';') }
    $env:Path = (@($env:Path -split ';' | Where-Object { $e = $_; $e -and -not ($Dirs | Where-Object { Test-SamePath $e $_ }) })) -join ';'
}

# --- the scheduled task ---

function Stop-Overhive([string]$Base) {
    $task = Get-ScheduledTask -TaskName $script:OverhiveTaskName -ErrorAction SilentlyContinue
    if ($task) { Stop-ScheduledTask -TaskName $script:OverhiveTaskName -ErrorAction SilentlyContinue }
    # The task's restart loop first (so it does not start amcd again), then amcd itself, also one started by hand.
    $runner = Join-Path $Base 'amcd-task.cmd'
    Get-CimInstance Win32_Process -Filter "Name = 'cmd.exe' OR Name = 'conhost.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($runner, [StringComparison]::OrdinalIgnoreCase) -ge 0 } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    $exe = Join-Path $Base 'amcd\amcd.exe'
    Get-Process -Name amcd -ErrorAction SilentlyContinue | Where-Object { $_.Path -and (Test-SamePath $_.Path $exe) } |
        Stop-Process -Force -ErrorAction SilentlyContinue
    foreach ($i in 1..20) {
        if (-not (Get-Process -Name amcd -ErrorAction SilentlyContinue | Where-Object { $_.Path -and (Test-SamePath $_.Path $exe) })) { return }
        Start-Sleep -Milliseconds 250
    }
}

function Register-OverhiveTask([string]$Base) {
    $logs = Join-Path $Base 'logs'
    New-Item -ItemType Directory -Force $logs | Out-Null
    # The task runs this script through a headless console host: no window at logon, and the daemon's
    # output goes to the log. It restarts amcd after a crash (exit code other than 0, or 1 = already running).
    $runner = Join-Path $Base 'amcd-task.cmd'
    $cmd = @(
        '@echo off'
        'rem Started at logon by the "Overhive" scheduled task (scripts/overhive-windows.ps1). Do not edit.'
        'if exist "%~dp0logs\amcd.log" move /y "%~dp0logs\amcd.log" "%~dp0logs\amcd.prev.log" >nul'
        ':start'
        'echo [%date% %time%] starting amcd>>"%~dp0logs\amcd.log"'
        '"%~dp0amcd\amcd.exe" >>"%~dp0logs\amcd.log" 2>&1'
        'set rc=%errorlevel%'
        'echo [%date% %time%] amcd exited with code %rc%>>"%~dp0logs\amcd.log"'
        'if "%rc%"=="0" exit /b 0'
        'if "%rc%"=="1" exit /b 1'
        'ping -n 4 127.0.0.1 >nul'
        'goto start'
    )
    [IO.File]::WriteAllLines($runner, [string[]]$cmd, (New-Object Text.UTF8Encoding $false))

    $user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $conhost = Join-Path $env:SystemRoot 'System32\conhost.exe'
    $action = New-ScheduledTaskAction -Execute $conhost -Argument "--headless `"$runner`"" -WorkingDirectory $Base
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) `
        -MultipleInstances IgnoreNew -Priority 5
    Register-ScheduledTask -TaskName $script:OverhiveTaskName -Action $action -Trigger $trigger -Principal $principal `
        -Settings $settings -Description "Overhive daemon (amcd): local mission control for AI coding agents. $script:OverhiveUrl" -Force | Out-Null
}

function Unregister-OverhiveTask {
    if (Get-ScheduledTask -TaskName $script:OverhiveTaskName -ErrorAction SilentlyContinue) {
        Stop-ScheduledTask -TaskName $script:OverhiveTaskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $script:OverhiveTaskName -Confirm:$false
    }
}

function Wait-OverhiveDashboard([int]$Seconds = 30) {
    foreach ($i in 1..($Seconds * 2)) {
        try { Invoke-WebRequest "$script:OverhiveUrl/" -UseBasicParsing -TimeoutSec 2 | Out-Null; return $true } catch { }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

# --- install / uninstall ---

# Copies <Source>\amcd and <Source>\cli into <Base> (unless they are the same folder), adds overhive.exe,
# the PATH entries and the scheduled task, starts the daemon. Re-running it updates an existing install.
function Install-Overhive([string]$Source, [string]$Base, [switch]$NoStartAtLogin, [switch]$InPlace) {
    foreach ($f in 'amcd\amcd.exe', 'cli\agentctl.exe') {
        if (-not (Test-Path (Join-Path $Source $f))) { throw "missing $f in $Source" }
    }
    Stop-Overhive $Base
    New-Item -ItemType Directory -Force $Base | Out-Null
    if (-not (Test-SamePath $Source $Base)) {
        foreach ($d in 'amcd', 'cli') {
            $target = Join-Path $Base $d
            if (Test-Path $target) { Remove-Item -Recurse -Force $target }
            Copy-Item -Recurse (Join-Path $Source $d) $target
        }
        # Keep the installer next to the install so `install-release.ps1 -Uninstall` works later.
        foreach ($s in 'install-release.ps1', 'overhive-windows.ps1') {
            $p = Join-Path $PSScriptRoot $s
            if ((Test-Path $p) -and -not (Test-SamePath $PSScriptRoot $Base)) { Copy-Item -Force $p (Join-Path $Base $s) }
        }
    }
    # `overhive` is the same program as `agentctl`: a hard link (no extra space), or a copy where links are not possible.
    $cli = Join-Path $Base 'cli\agentctl.exe'; $alias = Join-Path $Base 'cli\overhive.exe'
    if (Test-Path $alias) { Remove-Item -Force $alias }
    try { New-Item -ItemType HardLink -Path $alias -Target $cli -ErrorAction Stop | Out-Null } catch { Copy-Item $cli $alias }

    $dirs = @((Join-Path $Base 'cli'), (Join-Path $Base 'amcd'))
    if (-not $InPlace) { Add-OverhiveUserPath $dirs }

    if ($NoStartAtLogin) {
        Unregister-OverhiveTask
        Write-Host "Installed. Start the daemon with: amcd"
    } else {
        Register-OverhiveTask $Base
        Start-ScheduledTask -TaskName $script:OverhiveTaskName
        if (Wait-OverhiveDashboard) {
            Write-Host "Installed and started (scheduled task '$script:OverhiveTaskName', starts at logon)."
        } else {
            Write-Warning "Installed, but the daemon did not answer yet. Log: $(Join-Path $Base 'logs\amcd.log')"
        }
        Write-Host "Logs: $(Join-Path $Base 'logs\amcd.log')"
    }
    Write-Host "Claude Code and other AI-tool settings were not changed; connect them with: overhive hooks install"
    if (-not $InPlace) { Write-Host "Open a new terminal to use the overhive, agentctl and amcd commands." }
    Write-Host "Dashboard: $script:OverhiveUrl"
}

function Uninstall-Overhive([string]$Base, [switch]$RemoveData, [switch]$InPlace) {
    Write-Host "Note: hooks in AI-tool settings are not removed; if you installed them, run 'overhive hooks uninstall' first."
    Unregister-OverhiveTask
    Stop-Overhive $Base
    if (-not $InPlace) {
        Remove-OverhiveUserPath @((Join-Path $Base 'cli'), (Join-Path $Base 'amcd'))
        if (Test-Path $Base) {
            # A folder cannot be deleted while it is the current directory (e.g. when run as .\install-release.ps1 from it).
            if ((Get-Location).ProviderPath.StartsWith($Base, [StringComparison]::OrdinalIgnoreCase)) { Set-Location $env:USERPROFILE }
            [Environment]::CurrentDirectory = $env:USERPROFILE
            Remove-Item -Recurse -Force $Base
        }
    }
    $data = Get-OverhiveDataDir
    if ($RemoveData) {
        if (Test-Path $data) { Remove-Item -Recurse -Force $data }
        Write-Host "Removed Overhive and its data ($data)."
    } else {
        Write-Host "Removed Overhive. Your data is kept in $data (re-run with -RemoveData to delete it)."
    }
}
