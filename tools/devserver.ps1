<#
.SYNOPSIS
    Drive the shared local dev server for Uriel's in-game tests (tools/vrclient/README.md section 6).

.DESCRIPTION
    pwsh tools/devserver.ps1 status            is the server running? which Uriel DLL is deployed?
    pwsh tools/devserver.ps1 stop   [-Label x] close the game client, back up both logs, stop gracefully
    pwsh tools/devserver.ps1 start  [-Label x] back up both logs, start the server, wait until Uriel is initialized
    pwsh tools/devserver.ps1 deploy [-Label x] stop, build Release (the build copies Uriel.dll), verify the deployed
                                               DLL equals the build output, start

    The server is shared with Nyarlathotep (world 'nyardev', port 9876). Rules from Nyarlathotep's testing practice:
    logs are backed up before every start (a start overwrites them); the client is closed before a stop (a stop with
    a player online left 'Couldn't remap old Modification Id' errors); the stop is graceful (taskkill without /F, so
    the world saves), forced only after 60 s.
#>
param(
    [Parameter(Mandatory, Position = 0)][ValidateSet('status', 'stop', 'start', 'deploy')][string]$Action,
    [string]$Label = 'session'
)
$ErrorActionPreference = 'Stop'
$Server = 'C:\Program Files (x86)\Steam\steamapps\common\VRisingDedicatedServer'
$PluginLog = Join-Path $Server 'BepInEx\LogOutput.log'
$ServerLog = Join-Path $Server 'logs\NyarDev.log'
$Deployed = Join-Path $Server 'BepInEx\plugins\Uriel.dll'
$Repo = Split-Path $PSScriptRoot -Parent
$Built = Join-Path $Repo 'Uriel\Uriel\bin\Release\net6.0\Uriel.dll'
$VrClient = Join-Path $Repo 'tools\vrclient\vrclient.py'

function Get-Server { Get-Process VRisingServer -ErrorAction SilentlyContinue }

function Backup-Logs([string]$Why) {
    $dir = Join-Path $env:TEMP ("uriel-logs-{0}-{1}-{2}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $Label, $Why)
    New-Item -ItemType Directory -Force $dir | Out-Null
    foreach ($f in @($PluginLog, $ServerLog)) { if (Test-Path $f) { Copy-Item $f $dir } }
    Write-Host "logs backed up: $dir"
}

function Stop-DevServer {
    $p = Get-Server
    if (-not $p) { Write-Host 'server: not running'; return }
    if (Get-Process VRising -ErrorAction SilentlyContinue) {
        Write-Host 'closing the game client first'
        python $VrClient close | Out-Host
        Start-Sleep -Seconds 5
    }
    Backup-Logs 'stop'
    Write-Host "stopping server PID $($p.Id) (graceful)"
    taskkill /PID $p.Id | Out-Host
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Server) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
    if (Get-Server) {
        Write-Host 'still running after 60 s: forcing'
        taskkill /PID $p.Id /F | Out-Host
        Start-Sleep -Seconds 3
    }
    if (Get-Server) { throw 'server did not stop' }
    Write-Host 'server: stopped'
}

function Start-DevServer {
    if (Get-Server) { Write-Host 'server: already running'; return }
    Backup-Logs 'start'
    $env:SteamAppId = '1604030'
    Start-Process (Join-Path $Server 'VRisingServer.exe') -WorkingDirectory $Server `
        -ArgumentList '-persistentDataPath .\save-data-nyardev -serverName "Nyar Dev" -saveName nyardev -logFile .\logs\NyarDev.log'
    Write-Host 'server: starting; waiting for "Server Setup Complete" and "Uriel initialized"'
    $deadline = (Get-Date).AddSeconds(300)
    $setup = $false; $uriel = $false
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
        if (-not (Get-Server)) { throw 'server exited during start (check Bitdefender, then both logs)' }
        if (-not $setup -and (Test-Path $ServerLog)) { $setup = [bool](Select-String -Path $ServerLog -Pattern 'Server Setup Complete' -SimpleMatch -Quiet) }
        if (-not $uriel -and (Test-Path $PluginLog)) { $uriel = [bool](Select-String -Path $PluginLog -Pattern 'Uriel initialized' -SimpleMatch -Quiet) }
        if ($setup -and $uriel) { break }
    }
    if (-not ($setup -and $uriel)) { throw "server not ready after 300 s (setup=$setup uriel=$uriel)" }
    Select-String -Path $PluginLog -Pattern 'Uriel initialized|\[Uriel UNIT\] boot sweep' | ForEach-Object { Write-Host $_.Line }
    # The server's BepInEx console opens in Windows Terminal over the game's main menu, where vrclient's OCR then
    # can't find PLAY (2026-10-15). Minimize it; -WindowStyle on Start-Process doesn't reach a Terminal window.
    Add-Type -Namespace DevServer -Name Win -MemberDefinition '[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr h, int c);' -ErrorAction SilentlyContinue
    Get-Process | Where-Object { $_.MainWindowTitle -like 'BepInEx*VRisingServer*' } | ForEach-Object { [void][DevServer.Win]::ShowWindow($_.MainWindowHandle, 6) }
    Write-Host 'server: ready'
}

switch ($Action) {
    'status' {
        $p = Get-Server
        Write-Host ("server: {0}" -f ($(if ($p) { "running PID $($p.Id)" } else { 'stopped' })))
        Write-Host ("client: {0}" -f ($(if (Get-Process VRising -ErrorAction SilentlyContinue) { 'running' } else { 'not running' })))
        if (Test-Path $Deployed) { Write-Host "deployed: $((Get-FileHash $Deployed).Hash.Substring(0,12)) $((Get-Item $Deployed).LastWriteTime)" }
        if (Test-Path $Built) { Write-Host "built:    $((Get-FileHash $Built).Hash.Substring(0,12)) $((Get-Item $Built).LastWriteTime)" }
    }
    'stop' { Stop-DevServer }
    'start' { Start-DevServer }
    'deploy' {
        Stop-DevServer
        dotnet build (Join-Path $Repo 'Uriel\Uriel.sln') -c Release | Select-String -Pattern 'error|Warn|copied|Build succeeded' | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'build failed' }
        if ((Get-FileHash $Built).Hash -ne (Get-FileHash $Deployed).Hash) { throw 'deployed Uriel.dll differs from the build output' }
        Write-Host 'deployed DLL matches the build'
        Start-DevServer
    }
}
