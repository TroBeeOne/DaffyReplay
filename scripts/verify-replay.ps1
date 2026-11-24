<#
.SYNOPSIS
    End-to-end check of DaffyReplay: a fresh recording through DaffyTee, replayed at every pace
    into a restored copy of the database, with every result asserted.

.DESCRIPTION
    Needs docker/compose.yml's source and target SQL Servers, and the installed daffytee (0.3.0 or
    later). It starts and stops its own DaffyTees, and keeps its own target store, so it disturbs
    nothing already running.

     1. The unit tests, then the integration tests against the target.
     2. A fresh recording (scripts/record-sample.ps1): ADO.NET, EF Core, ODBC and JDBC, then concurrent load.
     3. inspect: complete, and every request replayable exactly.
     4. The safety rails: a target that is the recorded server is refused; so is a run that cannot ask.
     5. Each pace into a freshly restored target. serial and recorded must reproduce the recording:
        no new errors, no row-count differences, nothing failed or left out. fast runs sessions
        concurrently, so server-generated values (identities) can land on different rows: it must
        replay everything, and any new error must be the data-state kind the report labels.
     6. Byte-level fidelity: a replay through a second DaffyTee, whose recording of it must hold
        every recorded request payload, byte for byte, apart from the transaction descriptor and
        outstanding-request count that a replay sets on purpose.
     7. Pacing accuracy: a synthetic recording with known gaps, replayed at recorded pace - the
        scheduler's error and each request's start must be within 2 ms at p99.
     8. The mutation check: with descriptor patching switched off the same replay must fail, which
        proves step 5 would notice. (Handle mapping has its own, in the integration tests.)

    scripts/verify-linux.ps1 runs the pacing check and a replay of the committed sample on Linux.

    Asserts on what each run produced - never merely on the absence of an error.
#>
param(
    [int]$Load = 5,
    [int]$Workers = 4,
    [string]$Configuration = "Debug",
    [string]$DaffyTee = "daffytee",
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/common.ps1"
$script:failures = 0

<# Runs daffyreplay with its stderr captured as text: under 'Stop', Windows PowerShell would treat it as a failure. #>
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

$work = Join-Path ([IO.Path]::GetTempPath()) ("daffyreplay-verify-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $work | Out-Null
$env:DAFFYREPLAY_HOME = Join-Path $work 'home'
$exe = "$script:Root/src/DaffyReplay/bin/$Configuration/net10.0/daffyreplay.exe"
$probe = "$script:Root/probes/ReplayProbe/bin/$Configuration/net10.0/ReplayProbe.exe"
Write-Host "working in $work"

Write-Host "building ($Configuration)..."
dotnet build "$script:Root/DaffyReplay.slnx" -c $Configuration -v q -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed" }
Wait-Healthy source, target

if (-not $SkipTests) {
    $env:DAFFYREPLAY_TEST_SERVER = '127.0.0.1,14362'
    $tests = dotnet test "$script:Root/tests/DaffyReplay.Tests" -c $Configuration --no-build -v q -nologo 2>&1 | Out-String
    Check "unit and integration tests pass" ($LASTEXITCODE -eq 0 -and $tests -match 'Passed!') ($tests -split "`n" | Select-String 'FAIL|Failed!' | Out-String)
    Remove-Item Env:\DAFFYREPLAY_TEST_SERVER
}

# ---- a recording ---------------------------------------------------------------------------------

$recording = Join-Path $work 'recording.ndjson'
& "$PSScriptRoot/record-sample.ps1" -Out $recording -Name verify -Load $Load -Workers $Workers -Configuration $Configuration -DaffyTee $DaffyTee | Out-Host
$events = @(Read-Events $recording)
$requests = @($events | Where-Object { $_.type -like 'request.*' })
Check "the recording holds requests from ADO.NET, EF Core, ODBC and JDBC" (
    @($requests | Where-Object { $_.data.session.app -in 'ReplayProbe.Ado', 'ReplayProbe.EF', 'ReplayProbe.Odbc', 'ReplayProbe.Jdbc' } | ForEach-Object { $_.data.session.app } | Sort-Object -Unique).Count -eq 4)
Check "JDBC's server cursors are in it" (@($requests | Where-Object { $_.data.request.calls.proc -eq 'sp_cursorfetch' }).Count -gt 5)

$inspect = Tool inspect $recording
Check "inspect: every request replayable exactly, no warnings" (($inspect -match 'replayable [\d,]+ \(100\.0 %\): exactly') -and ($inspect -match 'No warnings')) $inspect

# ---- targets and the safety rails ------------------------------------------------------------------

"Svc!App2026`n" | & $exe target add DEV01 --target "Server=tcp:127.0.0.1,14362;User Id=svc_app;Encrypt=Mandatory;TrustServerCertificate=True" | Out-Null
Check "target add logs in and saves" ($LASTEXITCODE -eq 0)
"App!User2026`n" | & $exe target logins DEV01 add app_user --login app_user | Out-Null
"Report!User2026`n" | & $exe target logins DEV01 add report_user --login report_user | Out-Null
Check "mapped logins are tested and saved" ((& $exe target logins DEV01 | Out-String) -match 'report_user\s+report_user')

"wrong`n" | & $exe target add BAD --target "Server=tcp:127.0.0.1,14362;User Id=svc_app;Encrypt=Mandatory;TrustServerCertificate=True" | Out-Null
Check "target add refuses a login that fails" ($LASTEXITCODE -ne 0)

"Svc!App2026`n" | & $exe target add PROD --target "Server=tcp:localhost,14361;User Id=svc_app;Encrypt=Mandatory;TrustServerCertificate=True" | Out-Null
$refused = Tool run $recording --target PROD --yes
Check "a target that is the recorded server is refused (localhost = 127.0.0.1)" (($LASTEXITCODE -ne 0) -and ($refused -match 'Refused: target .PROD.')) $refused

$unasked = "" | Tool run $recording --target DEV01
Check "a run that cannot ask, and has no --yes, does not start" (($LASTEXITCODE -ne 0) -and ($unasked -match 'add --yes')) $unasked

# ---- each pace ---------------------------------------------------------------------------------------

function Invoke-Replay([string]$Name, [string[]]$Arguments) {
    Restore-Target target verify
    $out = Join-Path $work $Name
    $console = Tool run $recording --target DEV01 --yes --results --out $out @Arguments
    if ($LASTEXITCODE -ne 0) { Write-Host $console }
    return [pscustomobject]@{ Exit = $LASTEXITCODE; Report = (Get-Content -Raw "$out/report.json" | ConvertFrom-Json); Console = $console; Folder = $out }
}

function Count($report, [string]$outcome) { $v = $report.outcomes.counts.$outcome; if ($null -eq $v) { 0 } else { [long]$v } }

$total = $requests.Count
foreach ($pace in 'serial', 'recorded') {
    $run = Invoke-Replay $pace @('--pace', $pace)
    $r = $run.Report
    Check "${pace}: every request replayed" (($run.Exit -eq 0) -and ($r.coverage.replayed -eq $total)) "$($r.coverage.replayed) of $total"
    Check "${pace}: no new or changed errors" (((Count $r 'new_error') + (Count $r 'changed_error')) -eq 0) ($r.outcomes.newErrors | ConvertTo-Json -Depth 4)
    Check "${pace}: no row-count differences" ($r.outcomes.rowsDiffer -eq 0) ($r.outcomes.rowMismatches | ConvertTo-Json -Depth 4)
    Check "${pace}: nothing failed, timed out or left out" (((Count $r 'failed') + (Count $r 'timeout') + (Count $r 'not_replayed')) -eq 0)
    Check "${pace}: the recorded errors happen again, the same" ((Count $r 'same') -ge ($total - 1) -and @($r.outcomes.unchangedErrors).Count -ge 4)
    Check "${pace}: results.ndjson has a line per request" (@(Get-Content "$($run.Folder)/results.ndjson").Count -eq $total)
}
$recordedRun = $r
Check "recorded: the scheduler keeps time (p99 error under 2 ms)" ($recordedRun.pace.schedulerError.p99 -lt 2) "p99 $($recordedRun.pace.schedulerError.p99) ms"
Check "recorded: the replay took about as long as the recording" ([math]::Abs($recordedRun.run.wallSeconds - $recordedRun.run.recording.seconds) -lt 3) "$($recordedRun.run.wallSeconds) s against $($recordedRun.run.recording.seconds) s"

$fast = Invoke-Replay 'fast' @('--pace', 'fast')
$f = $fast.Report
Check "fast: every request replayed, none failed" (($fast.Exit -eq 0) -and ($f.coverage.replayed -eq $total) -and ((Count $f 'failed') -eq 0)) "$($f.coverage.replayed) of $total"
Check "fast: faster than the recording" ($f.run.wallSeconds -lt $f.run.recording.seconds)
$unexplained = @($f.outcomes.newErrors | Where-Object { -not $_.likelyDataState })
Check "fast: any new error is the data-state kind (concurrent identities)" ($unexplained.Count -eq 0) ($unexplained | ConvertTo-Json -Depth 4)
Write-Host "      fast pace: $(Count $f 'new_error') new error(s), $($f.outcomes.rowsDiffer) row-count difference(s) from concurrency"

# ---- the upgrade case: recorded on 2022, replayed on 2025 over TDS 8.0 ------------------------------------------

if ((docker inspect -f '{{.State.Health.Status}}' daffyreplay-target25-1 2>$null) -eq 'healthy') {
    Enable-StrictTls target25
    Restore-Target target25 verify
    "Svc!App2026`n" | Tool target add SQL2025 --target "Server=tcp:127.0.0.1,14363;User Id=svc_app;Encrypt=Strict;TrustServerCertificate=True" | Out-Null
    Check "target add over Encrypt=Strict (TDS 8.0) to SQL Server 2025" ($LASTEXITCODE -eq 0)
    "App!User2026`n" | Tool target logins SQL2025 add app_user --login app_user | Out-Null
    "Report!User2026`n" | Tool target logins SQL2025 add report_user --login report_user | Out-Null
    $upgrade = Tool run $recording --target SQL2025 --pace serial --yes --out (Join-Path $work 'upgrade')
    $u = Get-Content -Raw (Join-Path $work 'upgrade/report.json') | ConvertFrom-Json
    Check "upgrade: the 2022 recording replays on 2025 with no new errors or row differences" (
        ($LASTEXITCODE -eq 0) -and ($u.coverage.replayed -eq $total) -and ((Count $u 'new_error') + (Count $u 'changed_error') -eq 0) -and ($u.outcomes.rowsDiffer -eq 0)) $upgrade
}
else {
    Write-Host "skip  the upgrade case (start SQL Server 2025 with: docker compose -f docker/compose.yml --profile 2025 up -d)"
}

# ---- byte-level fidelity, through a second DaffyTee ---------------------------------------------------------

Restore-Target target verify
$teeWork = Join-Path $work 'tee2'
New-Item -ItemType Directory $teeWork | Out-Null
$password = "Tee2!" + [guid]::NewGuid().ToString('N').Substring(0, 10)
$capture = Join-Path $teeWork 'capture.ndjson'
$tee = Start-DaffyTee -Work $teeWork -Port 14371 -Events $capture -Target '127.0.0.1,14362' -Password $password -Exe $DaffyTee
try {
    "$password`n" | & $exe target add VIATEE --target "Server=tcp:127.0.0.1,14371;User Id=app;Encrypt=Mandatory;TrustServerCertificate=True" | Out-Null
    "$password`n" | & $exe target logins VIATEE add app_user --login orders | Out-Null
    "$password`n" | & $exe target logins VIATEE add report_user --login reports | Out-Null
    $through = Tool run $recording --target VIATEE --pace serial --yes --out (Join-Path $work 'through-tee')
    Check "a replay through DaffyTee runs" ($LASTEXITCODE -eq 0) $through
}
finally {
    Stop-DaffyTee $tee
}
$fidelity = & $probe --compare $recording $capture | Out-String
Check "byte-level fidelity: every request payload replayed exactly (transaction descriptors aside)" ($LASTEXITCODE -eq 0) $fidelity
Write-Host ($fidelity.Trim())

# ---- pacing accuracy: known gaps ---------------------------------------------------------------------------------

$synthetic = Join-Path $work 'synthetic.ndjson'
& $probe --synthetic $synthetic 2000 8 | Out-Null
$paced = Tool run $synthetic --target DEV01 --pace recorded --yes --out (Join-Path $work 'pacing')
$p = (Get-Content -Raw (Join-Path $work 'pacing/report.json') | ConvertFrom-Json).pace
Check "pacing: scheduler error p99 at most 2 ms (known gaps of 1-40 ms)" (($LASTEXITCODE -eq 0) -and ($p.schedulerError.p99 -le 2)) "p99 $($p.schedulerError.p99) ms"
Check "pacing: requests start within 2 ms of their time at p99 (replay host lag)" ($p.lagHost.p99 -le 2) "p99 $($p.lagHost.p99) ms"
Write-Host "      pacing: scheduler error p99 $($p.schedulerError.p99) ms, host lag p99 $($p.lagHost.p99) ms"

# ---- the mutation check ----------------------------------------------------------------------------------------

$mutant = Invoke-Replay 'mutation-descriptors' @('--pace', 'serial', '--debug-no-descriptors')
Check "mutation: without descriptor patching, requests in transactions fail and the checks see it" ((Count $mutant.Report 'new_error') -gt 100) "$(Count $mutant.Report 'new_error') new errors"

Write-Host ""
if ($script:failures -gt 0) {
    Write-Host "$($script:failures) check(s) FAILED - evidence kept in $work" -ForegroundColor Red
    exit 1
}
Write-Host "all checks passed"
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
exit 0
