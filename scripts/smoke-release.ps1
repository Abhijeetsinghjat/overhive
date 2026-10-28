# The Windows counterpart of smoke-release.sh, used by CI on windows-latest. Unpacks a release zip and, with
# Windows PowerShell 5.1 as a user would, installs it (twice: the second run is an update), checks that the
# scheduled task is registered and starts the daemon, that /api/health answers and the CLI runs from the
# user PATH, then uninstalls and checks that no task and no PATH entries are left and the data is kept.
# It uses the real per-user task and PATH, so run it only on a throwaway machine (CI runner).
#   pwsh scripts/smoke-release.ps1 dist/overhive-<version>-win-x64.zip
param([Parameter(Mandatory = $true)][string]$Zip)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'overhive-windows.ps1')

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('ovh-smoke-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$base = Join-Path $env:LOCALAPPDATA 'OverhiveSmoke'
$token = Join-Path (Get-OverhiveDataDir) 'token'

function Invoke-Installer([string[]]$Arguments) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $script:dir 'install-release.ps1') -InstallDir $base @Arguments
    if ($LASTEXITCODE -ne 0) { throw "install-release.ps1 $Arguments failed (exit $LASTEXITCODE)" }
}

function Wait-Health {
    foreach ($i in 1..60) {
        if (Test-Path $token) {
            try {
                $h = Invoke-RestMethod "$script:OverhiveUrl/api/health" -Headers @{ 'X-Amc-Token' = (Get-Content $token -Raw).Trim() } -TimeoutSec 3
                return $h
            } catch { }
        }
        Start-Sleep -Seconds 1
    }
    $info = Get-ScheduledTaskInfo -TaskName $script:OverhiveTaskName -ErrorAction SilentlyContinue
    if ($info) { "task: last result $($info.LastTaskResult), last run $($info.LastRunTime)" }
    Get-Content (Join-Path $base 'logs\amcd.log') -ErrorAction SilentlyContinue | Select-Object -Last 40
    throw "daemon did not answer /api/health"
}

function Get-AmcdProcess { @(Get-Process -Name amcd -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($base, [StringComparison]::OrdinalIgnoreCase) }) }

try {
    Expand-Archive -Path $Zip -DestinationPath $tmp
    $script:dir = (Get-ChildItem $tmp -Directory | Select-Object -First 1).FullName

    "== install"
    Invoke-Installer @()
    $task = Get-ScheduledTask -TaskName $script:OverhiveTaskName
    "task: $($task.TaskName) state=$($task.State) action=$($task.Actions[0].Execute) $($task.Actions[0].Arguments)"
    if ($task.Actions[0].Arguments -notlike "*$base*") { throw "task does not run the installed amcd" }
    if (-not ($task.Triggers | Where-Object { $_.CimClass.CimClassName -eq 'MSFT_TaskLogonTrigger' })) { throw "task has no logon trigger" }
    $h = Wait-Health
    "health: $($h.overall) version $($h.version)"
    if ((Get-AmcdProcess).Count -ne 1) { throw "expected one amcd from $base, found $((Get-AmcdProcess).Count)" }

    $userPath = Get-OverhiveUserPath
    foreach ($d in 'cli', 'amcd') {
        if (-not (($userPath -split ';') | Where-Object { Test-SamePath $_ (Join-Path $base $d) })) { throw "user PATH lacks $base\$d" }
    }
    # A new shell gets the new PATH: resolve the commands the way a user's new terminal would.
    $env:Path = [Environment]::ExpandEnvironmentVariables("$([Environment]::GetEnvironmentVariable('Path', 'Machine'));$userPath")
    foreach ($c in 'overhive', 'agentctl', 'amcd') {
        $cmd = Get-Command $c -CommandType Application -ErrorAction Stop | Select-Object -First 1
        "$c -> $($cmd.Source)"
    }
    & overhive version
    if ($LASTEXITCODE -ne 0) { throw "overhive version failed" }
    & overhive status
    "status exit=$LASTEXITCODE"
    "dashboard: HTTP $((Invoke-WebRequest "$script:OverhiveUrl/" -UseBasicParsing).StatusCode)"

    # A second amcd must refuse to start and point at the task.
    $ErrorActionPreference = 'Continue'   # its message comes on stderr
    $second = & (Join-Path $base 'amcd\amcd.exe') 2>&1 | Out-String
    $ErrorActionPreference = 'Stop'
    "second amcd: $($second.Trim())"
    if ($second -notmatch 'already running' -or $second -notmatch 'Get-ScheduledTask Overhive') { throw "second amcd did not report the running task" }

    "== update (re-run)"
    $before = (Get-AmcdProcess)[0].Id
    Invoke-Installer @()
    $h = Wait-Health
    $after = Get-AmcdProcess
    "health after update: $($h.overall); amcd pid $before -> $($after.Id -join ',')"
    if ($after.Count -ne 1 -or $after[0].Id -eq $before) { throw "update did not restart the daemon exactly once" }

    "== stop and start the task"
    Stop-ScheduledTask -TaskName $script:OverhiveTaskName
    Start-Sleep -Seconds 5
    "amcd processes after Stop-ScheduledTask: $((Get-AmcdProcess).Count) (informational)"
    Start-ScheduledTask -TaskName $script:OverhiveTaskName
    $h = Wait-Health
    "health after restart: $($h.overall)"

    "== uninstall"
    Invoke-Installer @('-Uninstall')
    if (Get-ScheduledTask -TaskName $script:OverhiveTaskName -ErrorAction SilentlyContinue) { throw "task still registered" }
    if ((Get-AmcdProcess).Count -ne 0) { throw "amcd still running" }
    if ((Get-OverhiveUserPath) -split ';' | Where-Object { $_ -like "*OverhiveSmoke*" }) { throw "PATH entries left: $(Get-OverhiveUserPath)" }
    if (Test-Path $base) { throw "$base still exists" }
    if (-not (Test-Path $token)) { throw "uninstall removed the data directory without -RemoveData" }
    "smoke test passed"
} finally {
    Unregister-OverhiveTask
    Get-AmcdProcess | Stop-Process -Force -ErrorAction SilentlyContinue
    Remove-OverhiveUserPath @((Join-Path $base 'cli'), (Join-Path $base 'amcd'))
    Remove-Item -Recurse -Force $tmp, $base -ErrorAction SilentlyContinue
}
