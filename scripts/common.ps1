<#
.SYNOPSIS
    Helpers shared by DaffyReplay's scripts: the docker SQL Servers, backups and restores, and a
    DaffyTee started and stopped the way a person would - so it delivers everything it captured.
#>

$script:SaPassword = if ($env:MSSQL_SA_PASSWORD) { $env:MSSQL_SA_PASSWORD } else { 'DaffyReplay!Dev2026' }
$script:Root = Split-Path -Parent $PSScriptRoot
$script:Databases = @('ReplayTest', 'ReplayOther')

function Invoke-Sql([string]$Container, [string]$Query, [switch]$Quiet) {
    $output = docker exec "daffyreplay-$Container-1" /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P $script:SaPassword -b -h -1 -W -Q "SET NOCOUNT ON; $Query" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd on $Container failed: $($output | Out-String)" }
    if (-not $Quiet) { return $output }
}

function Wait-Healthy([string[]]$Containers, [int]$Seconds = 120) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    foreach ($container in $Containers) {
        while ((docker inspect -f '{{.State.Health.Status}}' "daffyreplay-$container-1" 2>$null) -ne 'healthy') {
            if ((Get-Date) -gt $deadline) { throw "daffyreplay-$container-1 is not healthy; start it with: docker compose -f docker/compose.yml up -d" }
            Start-Sleep -Seconds 2
        }
    }
}

<# The source back to the seed, exactly: dropped and seeded again, so every recording starts the same. #>
function Reset-Source {
    Invoke-Sql source ("ALTER DATABASE ReplayTest SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ReplayTest; " +
                       "ALTER DATABASE ReplayOther SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ReplayOther;") -Quiet
    $output = docker exec daffyreplay-source-1 /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P $script:SaPassword -b -i /seed/seed.sql 2>&1
    if ($LASTEXITCODE -ne 0) { throw "seeding the source failed: $($output | Out-String)" }
}

<# What a user does the moment recording starts: back the databases up, into the volume the targets share. #>
function Backup-Source([string]$Name) {
    foreach ($database in $script:Databases) {
        Invoke-Sql source "BACKUP DATABASE [$database] TO DISK = N'/backups/$Name-$database.bak' WITH INIT, COPY_ONLY, FORMAT;" -Quiet
    }
}

<# What a user does before every replay: put the target back to the moment recording began. #>
function Restore-Target([string]$Container, [string]$Name) {
    foreach ($database in $script:Databases) {
        Invoke-Sql $Container ("IF DB_ID(N'$database') IS NOT NULL ALTER DATABASE [$database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                               "RESTORE DATABASE [$database] FROM DISK = N'/backups/$Name-$database.bak' WITH REPLACE; " +
                               "ALTER DATABASE [$database] SET MULTI_USER;") -Quiet
    }
}

<#
    A DaffyTee with its own throwaway user store, recording to a file. It runs with its prompt and
    stdin kept open, so Stop-DaffyTee can type :quit - DaffyTee then delivers everything still queued
    and writes proxy.stop, which killing it would not.
#>
function Start-DaffyTee([string]$Work, [int]$Port, [string]$Events, [string]$Target, [string]$Password, [string]$Exe = 'daffytee', [string]$Extra = '') {
    $exePath = (Get-Command $Exe -ErrorAction Stop).Source
    $teeHome = Join-Path $Work 'daffytee-home'
    $env:DAFFYTEE_HOME = $teeHome

    $logins = @{ app = 'svc_app:Svc!App2026'; orders = 'app_user:App!User2026'; reports = 'report_user:Report!User2026' }
    foreach ($login in $logins.Keys) {
        $user, $secret = $logins[$login] -split ':', 2
        $connection = "Server=tcp:$Target;User Id=$user;Password=$secret;Initial Catalog=ReplayTest;Encrypt=Mandatory;TrustServerCertificate=True"
        "$Password`n$Password`n" | & $exePath user add $login --target $connection | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "daffytee user add $login failed" }
    }

    $arguments = "--port $Port --repl --sink file --sink-path `"$Events`" --raw --bulk full --max-event-kb 8192 " +
                 "--instance DEV01:14400 --heartbeat 0 --log-file `"$Work\daffytee.log`" $Extra"
    $psi = [Diagnostics.ProcessStartInfo]::new('cmd.exe', "/c `"`"$exePath`" $arguments > `"$Work\daffytee.out`" 2>&1`"")
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.CreateNoWindow = $true
    $psi.Environment['DAFFYTEE_HOME'] = $teeHome
    $process = [Diagnostics.Process]::Start($psi)

    if (-not (Wait-Port $Port)) { Stop-DaffyTee $process; throw "DaffyTee did not start listening on $Port; see $Work\daffytee.out" }
    return $process
}

function Stop-DaffyTee([Diagnostics.Process]$Process, [int]$Seconds = 30) {
    if ($null -eq $Process -or $Process.HasExited) { return }
    # Windows PowerShell writes a byte-order mark before the first line it sends, which would make
    # that line no command at all; an empty line takes it.
    try { $Process.StandardInput.WriteLine(''); $Process.StandardInput.WriteLine(':quit'); $Process.StandardInput.Flush() } catch { }
    if (-not $Process.WaitForExit($Seconds * 1000)) {
        Write-Warning "DaffyTee did not stop within ${Seconds}s; killing it (its last events may be missing)"
        Get-CimInstance Win32_Process -Filter "ParentProcessId = $($Process.Id)" | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
    }
}

<#
    A certificate for TDS 8.0 (Encrypt=Strict): SQL Server accepts strict connections only with a
    configured certificate - the one it generates for itself does not count. Self-signed, for this
    test container alone; idempotent, restarting the container only the first time.
#>
function Enable-StrictTls([string]$Container) {
    $configured = docker exec "daffyreplay-$Container-1" bash -c "grep -c tlscert /var/opt/mssql/mssql.conf 2>/dev/null || true"
    if ([int]$configured -gt 0) { return }
    $setup = 'cd /var/opt/mssql && openssl req -x509 -nodes -newkey rsa:2048 -subj "/CN=localhost" ' +
             '-addext "subjectAltName=DNS:localhost,IP:127.0.0.1" -keyout tls.key -out tls.pem -days 365 2>/dev/null && chmod 600 tls.key && ' +
             '/opt/mssql/bin/mssql-conf set network.tlscert /var/opt/mssql/tls.pem && /opt/mssql/bin/mssql-conf set network.tlskey /var/opt/mssql/tls.key && ' +
             '/opt/mssql/bin/mssql-conf set network.tlsprotocols 1.2,1.3 && /opt/mssql/bin/mssql-conf set network.forceencryption 0'
    docker exec "daffyreplay-$Container-1" bash -c $setup | Out-Null
    docker restart "daffyreplay-$Container-1" | Out-Null
    Start-Sleep -Seconds 5
    Wait-Healthy $Container
}

function Wait-Port([int]$Port, [int]$Seconds = 30) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        try { $c = New-Object Net.Sockets.TcpClient; $c.Connect('127.0.0.1', $Port); $c.Close(); return $true } catch { Start-Sleep -Milliseconds 250 }
    }
    return $false
}

function Read-Events([string]$Path) {
    Get-Content -Encoding UTF8 $Path | Where-Object { $_.Length -gt 0 } | ForEach-Object { $_ | ConvertFrom-Json }
}
