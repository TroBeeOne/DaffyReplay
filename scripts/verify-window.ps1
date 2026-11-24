<#
.SYNOPSIS
    The peak-window workflow, end to end: DaffyTee in front of a running application with the tee
    off, switched on at its prompt for a while and off again; the target restored to the moment it
    went on; the window replayed.

.DESCRIPTION
    Needs docker/compose.yml's source and target SQL Servers, and the installed daffytee (0.3.0 or
    later).

     1. The source is put back to the seed, in the full recovery model, and backed up.
     2. DaffyTee starts with --tee off and --bulk meta (its default for bulk loads), and
        ReplayProbe's concurrent load starts through it on pooled connections.
     3. After a while, :tee on at DaffyTee's prompt. A bulk copy runs from a new connection; later,
        :tee off. The load carries on throughout, and DaffyTee is stopped after it.
     4. A log backup, and the target restored with STOPAT = the time of the 'tee on' proxy.control
        event: the database as it was when the recording began. Then scripts/reseed-identities.sql:
        a database recovered to a point in time carries its identities on from the end of a cached
        block, not from the last value used.
     5. inspect must see the sessions that were already open, the tee switched, and the bulk loads
        recorded without their rows; serial and recorded pace must replay everything, with no
        session failing, no bulk-load error (4022), and at most a few new errors or row-count
        differences - a transaction the tee came on in the middle of commits nothing on replay.
#>
param(
    [int]$Before = 10,
    [int]$Window = 30,
    [int]$After = 8,
    [int]$Workers = 4,
    [int]$Port = 14372,
    [string]$Configuration = "Debug",
    [string]$DaffyTee = "daffytee"
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/common.ps1"
$script:failures = 0

function Tool {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $text = ($input | & $exe @args 2>&1 | ForEach-Object { "$_" }) -join "`n"; $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $previous }
    $global:LASTEXITCODE = $code
    return $text
}

function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Host "ok    $name" }
    else { Write-Host "FAIL  $name  $detail" -ForegroundColor Red; $script:failures++ }
}

function Send([Diagnostics.Process]$Process, [string]$Command) {
    # An empty line first: Windows PowerShell puts a byte-order mark before the first line it sends.
    $Process.StandardInput.WriteLine('')
    $Process.StandardInput.WriteLine($Command)
    $Process.StandardInput.Flush()
}

function Count($report, [string]$outcome) { $v = $report.outcomes.counts.$outcome; if ($null -eq $v) { 0 } else { [long]$v } }

$work = Join-Path ([IO.Path]::GetTempPath()) ("daffyreplay-window-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $work | Out-Null
$env:DAFFYREPLAY_HOME = Join-Path $work 'home'
$exe = "$script:Root/src/DaffyReplay/bin/$Configuration/net10.0/daffyreplay.exe"
$probe = "$script:Root/probes/ReplayProbe/bin/$Configuration/net10.0/ReplayProbe.exe"
Write-Host "working in $work"

dotnet build "$script:Root/DaffyReplay.slnx" -c $Configuration -v q -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed" }
Wait-Healthy source, target

# ---- 1. the source, backed up, with its log kept --------------------------------------------------------

Reset-Source
foreach ($database in $script:Databases) {
    Invoke-Sql source ("ALTER DATABASE [$database] SET RECOVERY FULL; " +
                       "BACKUP DATABASE [$database] TO DISK = N'/backups/window-$database.bak' WITH INIT, FORMAT;") -Quiet
}

# ---- 2-3. a gateway first, a recording later ---------------------------------------------------------------

$events = Join-Path $work 'events.ndjson'
$password = "Window!" + [guid]::NewGuid().ToString('N').Substring(0, 10)
$proxy = Start-DaffyTee -Work $work -Port $Port -Events $events -Target '127.0.0.1,14361' -Password $password -Exe $DaffyTee -Extra '--tee off --bulk meta'
try {
    $seconds = $Before + $Window + $After
    $load = Start-Process -FilePath $probe -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $work 'load.out') `
        -ArgumentList '--server', "127.0.0.1,$Port", '--password', $password, '--only', 'load', '--load', $seconds, '--workers', $Workers
    Start-Sleep -Seconds $Before
    Send $proxy ':tee on'
    Write-Host "tee on"
    Start-Sleep -Seconds 3
    & $probe --server "127.0.0.1,$Port" --password $password --only bulk-copy | Out-Null
    Check "a bulk copy from a new connection, during the window" ($LASTEXITCODE -eq 0)
    Start-Sleep -Seconds ($Window - 3)
    Send $proxy ':tee off'
    Write-Host "tee off"
    $load.WaitForExit()
    # Start-Process leaves ExitCode empty unless its handle was taken at the start; the probe's own summary says.
    $loadOut = Get-Content -Raw (Join-Path $work 'load.out')
    Check "the load ran through it all, undisturbed by the tee" ($loadOut -match '1 passed, 0 failed') $loadOut
}
finally {
    Stop-DaffyTee $proxy
}

# ---- 4. the target, as the database was when the tee went on ------------------------------------------------

$all = @(Read-Events $events)
$on = @($all | Where-Object { $_.type -eq 'proxy.control' -and $_.data.change -eq 'tee on' })
Check "the recording says when the tee went on" ($on.Count -eq 1)
$stopAt = ([DateTimeOffset]::Parse($on[0].time)).UtcDateTime.ToString('yyyy-MM-ddTHH:mm:ss.fff')
Write-Host "restoring to $stopAt UTC (the container's clock is UTC)"
foreach ($database in $script:Databases) {
    Invoke-Sql source "BACKUP LOG [$database] TO DISK = N'/backups/window-$database.trn' WITH INIT, FORMAT;" -Quiet
}

function Restore-Window {
    foreach ($database in $script:Databases) {
        Invoke-Sql target ("IF DB_ID(N'$database') IS NOT NULL ALTER DATABASE [$database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                           "RESTORE DATABASE [$database] FROM DISK = N'/backups/window-$database.bak' WITH NORECOVERY, REPLACE; " +
                           "RESTORE LOG [$database] FROM DISK = N'/backups/window-$database.trn' WITH STOPAT = '$stopAt', RECOVERY; " +
                           "ALTER DATABASE [$database] SET MULTI_USER;") -Quiet
        docker exec daffyreplay-target-1 /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P $script:SaPassword -d $database -b -i /tmp/reseed-identities.sql | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "reseeding $database failed" }
    }
}
docker cp "$PSScriptRoot/reseed-identities.sql" daffyreplay-target-1:/tmp/reseed-identities.sql | Out-Null

# ---- 5. inspect and replay --------------------------------------------------------------------------------------

$requests = @($all | Where-Object { $_.type -like 'request.*' })
$inspect = Tool inspect $events
Write-Host $inspect
Check "inspect: sessions already open when the tee went on" ($inspect -match '[\d,]+ before it') $inspect
Check "inspect: the tee switched on and off, at the prompt" (($inspect -match 'changed .* tee on \(by console\)') -and ($inspect -match 'changed .* tee off \(by console\)'))
Check "inspect: bulk loads without rows are skipped with their INSERT BULK" (($inspect -match 'bulk loads recorded without their rows') -and ($inspect -match 'INSERT BULK statements whose rows'))
Check "inspect: no seq gaps reported" ($inspect -notmatch 'gaps in seq')
Check "inspect: the settings are the ones the tee came on with" (($inspect -match 'daffytee .*: on, sampling 100%') -and ($inspect -notmatch 'not recording raw bytes|was sampling'))

"Svc!App2026`n" | Tool target add DEV01 --target "Server=tcp:127.0.0.1,14362;User Id=svc_app;Encrypt=Mandatory;TrustServerCertificate=True" | Out-Null
"App!User2026`n" | Tool target logins DEV01 add app_user --login app_user | Out-Null

$bulk = 2 * @($requests | Where-Object { $_.type -eq 'request.bulk' }).Count
foreach ($pace in 'serial', 'recorded') {
    Restore-Window
    $out = Join-Path $work $pace
    $console = Tool run $events --target DEV01 --pace $pace --yes --out $out
    $r = Get-Content -Raw "$out/report.json" | ConvertFrom-Json
    $new = (Count $r 'new_error') + (Count $r 'changed_error')
    Check "${pace}: every replayable request replayed" (($LASTEXITCODE -eq 0) -and ($r.coverage.replayed -eq ($requests.Count - $bulk))) "$($r.coverage.replayed) of $($requests.Count - $bulk)`n$console"
    Check "${pace}: no session failed or was left out" (((Count $r 'failed') + (Count $r 'not_replayed')) -eq 0)
    Check "${pace}: no bulk-load error (4022)" (@($r.outcomes.newErrors | Where-Object { $_.number -eq 4022 }).Count -eq 0)
    Check "${pace}: at most a few new errors, at the window's edge" ($new -le 2 * $Workers) ($r.outcomes.newErrors | ConvertTo-Json -Depth 4)
    Check "${pace}: at most a few row-count differences" ($r.outcomes.rowsDiffer -le 2 * $Workers) ($r.outcomes.rowMismatches | ConvertTo-Json -Depth 4)
    Write-Host "      ${pace}: $($r.coverage.replayed) replayed, $new new error(s), $($r.outcomes.rowsDiffer) row-count difference(s)"
    foreach ($e in @($r.outcomes.newErrors) + @($r.outcomes.changedErrors)) { if ($e) { Write-Host "        $($e.count) x $($e.number) $($e.message)  [$($e.statement)]" } }
}

Write-Host ""
if ($script:failures -gt 0) {
    Write-Host "$($script:failures) check(s) FAILED - evidence kept in $work" -ForegroundColor Red
    exit 1
}
Write-Host "all checks passed"
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
