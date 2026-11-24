<#
.SYNOPSIS
    End-to-end check of daffyreplay export sqlsim and import sqlsim: one workload captured both ways at
    once - by DaffyTee, and by sqlsim's own Extended Events capture - then each replayed by the other tool.

.DESCRIPTION
    Needs docker/compose.yml's source and target SQL Servers, the installed daffytee, and sqlsim
    (Microsoft's sqlsimtools, github.com/microsoft/sqlsimtools): -Sqlsim names its sqlsim.exe, or
    sqlsim 1.0.0 for Windows is downloaded into work/sqlsim the first time. It is used only here.

     1. An Extended Events session on the source, as sqlsim's replay/setup-test-capture.sql defines it
        (sql_batch_completed, rpc_completed, login, logout; session_id and database_name), filtered to
        the probe's applications; then scripts/record-sample.ps1 records the workload through DaffyTee
        while it runs. The capture becomes replay XML as sqlsim's convert-xel.ps1 makes it: each event's
        event_data in a <replay> root, sp_cursor* calls left out.
     2. export: the DaffyTee recording as sqlsim replay XML - nearly all of it written, well-formed by
        sqlsim Studio's rules - replayed by sqlsim on one connection in recorded order (-replay stress
        -n 1 -r 1) into a restored target. Every table but the bulk-loaded one must end as the source
        did, and every statement that failed must have failed when recorded, or be a temp table that
        sqlsim's connections keep (sqlsim does not reset a pooled connection).
     3. import: sqlsim's XML as a recording, run by DaffyReplay at serial pace into a restored target.
        Everything imported must replay; batches' row totals are compared, and nearly all agree; new
        errors may be only those Extended Events records as OK (the probe's deliberate errors) and
        SAVE TRANSACTION without the transaction the driver began; and the report says it is imported.
     4. For reference, sqlsim replaying its own capture (fast mode): reported, not checked.

    Asserts on what each run produced - never merely on the absence of an error.
#>
param(
    [string]$Sqlsim = "",
    [string]$SqlsimVersion = "1.0.0",
    [int]$Load = 5,
    [int]$Workers = 4,
    [string]$Configuration = "Debug",
    [string]$DaffyTee = "daffytee",
    [switch]$NoJdbc
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

<# Row counts of every user table in the probe's databases. #>
function Get-Rows([string]$Container) {
    $rows = @{}
    foreach ($db in $script:Databases) {
        $sql = "SELECT s.name + '.' + t.name, SUM(p.rows) FROM [$db].sys.tables t JOIN [$db].sys.schemas s ON s.schema_id = t.schema_id " +
               "JOIN [$db].sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1) GROUP BY s.name, t.name"
        foreach ($line in (Invoke-Sql $Container $sql)) { $name, $count = "$line".Trim() -split '\s+'; if ($name) { $rows["$db.$name"] = [long]$count } }
    }
    return $rows
}

<# A <data> field of an Extended Events event: its value's text. #>
function Field($event, [string]$name) {
    $value = ($event.data | Where-Object name -eq $name).value
    if ($value -is [Xml.XmlElement]) { $value.InnerText } else { "$value" }
}

<# sqlsim, its password piped in by cmd: Windows PowerShell would put a byte-order mark before it. #>
function Invoke-Sqlsim([string]$Xml, [string]$Json, [string[]]$Arguments) {
    $line = ($Arguments -join ' ')
    cmd /c "echo Svc!App2026| `"$Sqlsim`" -S tcp:127.0.0.1,14362 -U svc_app -P - -C -d ReplayTest -replayfile `"$Xml`" -querystats -json -o `"$Json`" -q $line > `"$Json.out`" 2>&1"
    return Get-Content -Raw $Json | ConvertFrom-Json
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("daffyreplay-sqlsim-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $work | Out-Null
$env:DAFFYREPLAY_HOME = Join-Path $work 'home'
$exe = "$script:Root/src/DaffyReplay/bin/$Configuration/net10.0/daffyreplay.exe"
Write-Host "working in $work"

if (-not $Sqlsim) {
    $kit = Join-Path $script:Root "work/sqlsim/$SqlsimVersion"
    $Sqlsim = Join-Path $kit 'sqlsim/x64/sqlsim.exe'
    if (-not (Test-Path $Sqlsim)) {
        New-Item -ItemType Directory -Force $kit | Out-Null
        $zip = Join-Path $kit "sqlsim-$SqlsimVersion-win-x64.zip"
        Invoke-WebRequest -UseBasicParsing -OutFile $zip "https://github.com/microsoft/sqlsimtools/releases/download/v$SqlsimVersion/sqlsim-$SqlsimVersion-win-x64.zip"
        Unblock-File $zip
        Expand-Archive $zip -DestinationPath $kit -Force
    }
}
Write-Host "sqlsim: $Sqlsim"

Write-Host "building ($Configuration)..."
dotnet build "$script:Root/DaffyReplay.slnx" -c $Configuration -v q -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed" }
Wait-Healthy source, target

# ---- one workload, captured both ways ----------------------------------------------------------------

docker exec -u 0 daffyreplay-source-1 bash -c "rm -rf /var/opt/mssql/xe && mkdir -p /var/opt/mssql/xe && chown mssql /var/opt/mssql/xe" | Out-Null
$filter = "WHERE sqlserver.client_app_name LIKE N'ReplayProbe%'"
Invoke-Sql source (
    "IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = 'daffyreplay_sqlsim') DROP EVENT SESSION daffyreplay_sqlsim ON SERVER; " +
    "CREATE EVENT SESSION daffyreplay_sqlsim ON SERVER " +
    "ADD EVENT sqlserver.sql_batch_completed(ACTION(sqlserver.session_id, sqlserver.database_name) $filter), " +
    "ADD EVENT sqlserver.rpc_completed(ACTION(sqlserver.session_id, sqlserver.database_name) $filter), " +
    "ADD EVENT sqlserver.login(ACTION(sqlserver.session_id, sqlserver.database_name) $filter), " +
    "ADD EVENT sqlserver.logout(ACTION(sqlserver.session_id, sqlserver.database_name) $filter) " +
    "ADD TARGET package0.event_file(SET filename = N'/var/opt/mssql/xe/daffyreplay_sqlsim.xel', max_file_size = 100) " +
    "WITH (MAX_MEMORY = 4096 KB, MAX_DISPATCH_LATENCY = 1 SECONDS); " +
    "ALTER EVENT SESSION daffyreplay_sqlsim ON SERVER STATE = START;") -Quiet

$recording = Join-Path $work 'recording.ndjson'
try {
    & "$PSScriptRoot/record-sample.ps1" -Out $recording -Name sqlsim -Load $Load -Workers $Workers -Configuration $Configuration -DaffyTee $DaffyTee -NoJdbc:$NoJdbc | Out-Host
}
finally {
    Start-Sleep -Seconds 2   # the session's dispatch latency
    Invoke-Sql source "ALTER EVENT SESSION daffyreplay_sqlsim ON SERVER STATE = STOP;" -Quiet
}
$sourceRows = Get-Rows source

# convert-xel.ps1's conversion, over SQL authentication: each event's event_data, as SQL Server wrote it.
$native = Join-Path $work 'native.xml'
$connection = New-Object System.Data.SqlClient.SqlConnection("Server=tcp:127.0.0.1,14361;User Id=sa;Password=$script:SaPassword;Encrypt=True;TrustServerCertificate=True")
$connection.Open()
$command = $connection.CreateCommand()
$command.CommandTimeout = 300
$command.CommandText = "SELECT object_name, CAST(event_data AS nvarchar(max)) FROM sys.fn_xe_file_target_read_file(N'/var/opt/mssql/xe/daffyreplay_sqlsim*.xel', NULL, NULL, NULL) " +
                       "WHERE object_name IN ('sql_batch_completed', 'rpc_completed', 'login', 'logout') ORDER BY file_name, file_offset"
$reader = $command.ExecuteReader()
$writer = [IO.StreamWriter]::new($native, $false, [Text.Encoding]::UTF8)
$writer.WriteLine('<?xml version="1.0" encoding="utf-8"?>')
$writer.WriteLine('<replay source="/var/opt/mssql/xe/daffyreplay_sqlsim*.xel" server="127.0.0.1,14361">')
while ($reader.Read()) {
    if ($reader.GetString(0) -eq 'rpc_completed' -and $reader.GetString(1) -match 'sp_cursor') { continue }
    $writer.WriteLine("  $($reader.GetString(1))")
}
$writer.WriteLine('</replay>'); $writer.Close(); $reader.Close(); $connection.Close()
Invoke-Sql source "DROP EVENT SESSION daffyreplay_sqlsim ON SERVER;" -Quiet
docker exec -u 0 daffyreplay-source-1 bash -c "rm -rf /var/opt/mssql/xe" | Out-Null

$requests = @(Read-Events $recording | Where-Object { $_.type -like 'request.*' })
$nativeEvents = ([xml](Get-Content -Raw -Encoding UTF8 $native)).replay.event
Check "the workload was captured both ways" (($requests.Count -gt 1000) -and (@($nativeEvents | Where-Object name -eq 'rpc_completed').Count -gt 500)) "$($requests.Count) DaffyTee requests, $(@($nativeEvents).Count) XEvents"

"Svc!App2026`n" | & $exe target add DEV01 --target "Server=tcp:127.0.0.1,14362;User Id=svc_app;Encrypt=Mandatory;TrustServerCertificate=True" | Out-Null
"App!User2026`n" | & $exe target logins DEV01 add app_user --login app_user | Out-Null
"Report!User2026`n" | & $exe target logins DEV01 add report_user --login report_user | Out-Null

# ---- export: DaffyTee's recording, replayed by sqlsim -------------------------------------------------

$exported = Join-Path $work 'exported.xml'
$console = Tool export sqlsim $recording --out $exported
Write-Host $console
$written = if ($console -match 'exported\s+([\d,]+) of ([\d,]+) requests') { [long]($Matches[1] -replace ',', '') } else { 0 }
Check "export: at least 99 % of the requests written" (($LASTEXITCODE -eq 0) -and ($written -ge $requests.Count * 0.99)) "$written of $($requests.Count)"

$studio = [Xml.XmlReaderSettings]::new(); $studio.DtdProcessing = 'Prohibit'; $studio.XmlResolver = $null; $studio.MaxCharactersInDocument = 100000000
$events = 0; $xml = [Xml.XmlReader]::Create($exported, $studio)
try { while ($xml.Read()) { if ($xml.NodeType -eq 'Element' -and $xml.LocalName -eq 'event') { $events++ } }; $wellFormed = $true } catch { $wellFormed = $false } finally { $xml.Dispose() }
Check "export: well-formed by sqlsim Studio's rules, an event per request written" ($wellFormed -and $events -eq $written) "$events events"

Restore-Target target sqlsim
$stress = Invoke-Sqlsim $exported (Join-Path $work 'export-sqlsim.json') @('-replay', 'stress', '-n', '1', '-r', '1')
Check "export: sqlsim replayed every event" ($stress.replay_summary.result.events -eq $written) "$($stress.replay_summary.result.events) of $written"
$targetRows = Get-Rows target
$differ = @($sourceRows.Keys | Where-Object { $_ -notlike '*BulkTarget' -and $targetRows[$_] -ne $sourceRows[$_] } | ForEach-Object { "$_ $($targetRows[$_])/$($sourceRows[$_])" })
Check "export: after sqlsim, every table but the bulk-loaded one ends as the source did" ($differ.Count -eq 0) ($differ -join '; ')
$failedWhenRecorded = @(([xml](Get-Content -Raw -Encoding UTF8 $exported)).replay.event | Where-Object { (Field $_ 'result') -ne '0' } | ForEach-Object { Field $_ 'batch_text' })
$unexpected = @($stress.query_stats.queries | Where-Object { $_.failures -gt 0 -and $_.query -notin $failedWhenRecorded -and $_.query -notmatch 'CREATE TABLE #' })
Check "export: what failed in sqlsim failed when recorded too, or is a temp table sqlsim's connection kept" ($unexpected.Count -eq 0) (($unexpected | ForEach-Object { $_.query }) -join ' | ')

# ---- import: sqlsim's capture, replayed by DaffyReplay ------------------------------------------------

$imported = Join-Path $work 'imported.ndjson'
$console = Tool import sqlsim $native --out $imported
Write-Host $console
$importedRequests = @(Read-Events $imported | Where-Object { $_.type -like 'request.*' })
$statements = @($nativeEvents | Where-Object { $_.name -in 'sql_batch_completed', 'rpc_completed' -and (Field $_ 'object_name') -ne 'sp_reset_connection' -and (Field $_ 'batch_text') -notmatch '^\s*insert bulk' })
Check "import: every batch and procedure call in the capture became a request (resets as flags, INSERT BULK left out)" (($LASTEXITCODE -eq 0) -and ($importedRequests.Count -eq $statements.Count)) "$($importedRequests.Count) requests, $($statements.Count) statements"
$inspect = Tool inspect $imported
Check "import: inspect says the recording was imported, and can all be replayed" (($inspect -match 'imported\s+from sqlsim') -and ($inspect -match 'replayable [\d,]+ \(100\.0 %\)')) $inspect

Restore-Target target sqlsim
$out = Join-Path $work 'import-run'
$console = Tool run $imported --target DEV01 --yes --results --pace serial --out $out
$r = Get-Content -Raw "$out/report.json" | ConvertFrom-Json
Check "import: every imported request replayed" ($r.coverage.replayed -eq $importedRequests.Count) "$($r.coverage.replayed) of $($importedRequests.Count)"
$compared = @(Get-Content "$out/results.ndjson" | ConvertFrom-Json | Where-Object { $null -ne $_.rowsDiffer })
Check "import: batches' row totals compared, and at least 95 % agree" (($compared.Count -gt 100) -and ($r.outcomes.rowsDiffer -le $compared.Count * 0.05)) "$($r.outcomes.rowsDiffer) of $($compared.Count) differ"
# What Extended Events records as OK though it failed: the probe's divide by zero, duplicate key, RAISERROR and
# permission errors; and a savepoint in a transaction only the driver began (EF Core), which it has no event for.
$blindSpots = 8134, 2627, 2601, 50000, 229, 628
$unexplained = @(@($r.outcomes.newErrors) + @($r.outcomes.changedErrors) | Where-Object { $_ -and $_.number -notin $blindSpots })
Check "import: new errors are only those Extended Events recorded as OK, or a savepoint without its driver's transaction" ($unexplained.Count -eq 0) ($unexplained | ConvertTo-Json -Depth 4)
$html = Get-Content -Raw "$out/report.html"
Check "import: the report says it is imported, and what that leaves out" (($html -match 'callout imported') -and ($html -match 'Transactions the application ran through its driver'))

# ---- for reference: sqlsim replaying its own capture ---------------------------------------------------

Restore-Target target sqlsim
$own = Invoke-Sqlsim $native (Join-Path $work 'native-sqlsim.json') @('-replay', 'fast')
Write-Host ""
Write-Host ("      sqlsim, its own capture (fast):      {0} ok, {1} failed of {2} events" -f $own.replay_summary.result.ok, $own.replay_summary.result.error, $own.replay_summary.result.events)
Write-Host ("      sqlsim, DaffyReplay's export (stress): {0} ok, {1} failed of {2} events" -f $stress.replay_summary.result.ok, $stress.replay_summary.result.error, $stress.replay_summary.result.events)
Write-Host ("      DaffyReplay, the imported capture:   {0} same, {1} new errors, {2} rows differ of {3} compared" -f $r.outcomes.counts.same, $r.outcomes.counts.new_error, $r.outcomes.rowsDiffer, $compared.Count)

Write-Host ""
if ($script:failures -gt 0) {
    Write-Host "$($script:failures) check(s) FAILED - evidence kept in $work" -ForegroundColor Red
    exit 1
}
Write-Host "all checks passed"
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
exit 0
