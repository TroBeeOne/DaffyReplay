<#
.SYNOPSIS
    DaffyReplay on Linux: built and run in the .NET SDK container, on docker/compose.yml's network.

.DESCRIPTION
    Copies the source (not bin, obj, CONTEXT or work) into a Linux container, publishes it, keeps
    its target store there (an owner-only key file, as on any Linux host, rather than DPAPI), and:
      1. replays the committed recording (samples/workload.ndjson) serially into the target,
         restored from a fresh backup of the seed - which the recording's own starting point was;
      2. replays a synthetic recording with known gaps at recorded pace, and checks the
         scheduler's error and each request's start are within 2 ms at p99.
#>
param([string]$Image = "mcr.microsoft.com/dotnet/sdk:10.0")

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/common.ps1"
Wait-Healthy source, target

Write-Host "a fresh backup of the seed, restored on the target..."
Reset-Source
Backup-Source linux
Restore-Target target linux

$probe = "$script:Root/probes/ReplayProbe/bin/Debug/net10.0/ReplayProbe.exe"
dotnet build "$script:Root/probes/ReplayProbe" -v q -nologo | Out-Null
$work = Join-Path $script:Root 'work'
New-Item -ItemType Directory -Force $work | Out-Null
& $probe --synthetic (Join-Path $work 'synthetic-linux.ndjson') 2000 8 | Out-Null

$script = @'
set -e
mkdir -p /tmp/r && tar -C /src --exclude=./CONTEXT --exclude="*/bin" --exclude="*/obj" --exclude=./work -cf - . | tar -C /tmp/r -xf -
dotnet publish /tmp/r/src/DaffyReplay -c Release -o /tmp/dr -v q -nologo > /dev/null
export DAFFYREPLAY_HOME=/tmp/home
printf 'Svc!App2026\n' | dotnet /tmp/dr/daffyreplay.dll target add LINUX --target "Server=tcp:target,1433;User Id=svc_app;Encrypt=Mandatory;TrustServerCertificate=True" > /dev/null
printf 'App!User2026\n' | dotnet /tmp/dr/daffyreplay.dll target logins LINUX add app_user --login app_user > /dev/null
printf 'Report!User2026\n' | dotnet /tmp/dr/daffyreplay.dll target logins LINUX add report_user --login report_user > /dev/null
echo "key file: $(stat -c %a /tmp/home/store.key)"
dotnet /tmp/dr/daffyreplay.dll run /src/samples/workload.ndjson --target LINUX --pace serial --yes --out /tmp/serial > /dev/null
dotnet /tmp/dr/daffyreplay.dll run /src/work/synthetic-linux.ndjson --target LINUX --pace recorded --yes --out /tmp/pacing > /dev/null
mkdir -p /out && cp /tmp/serial/report.json /out/serial.json && cp /tmp/pacing/report.json /out/pacing.json
uname -sr
'@
$out = Join-Path $work 'linux'
New-Item -ItemType Directory -Force $out | Out-Null
# A file rather than a pipe: Windows PowerShell would put a byte-order mark in front of the script.
[IO.File]::WriteAllText((Join-Path $out 'run.sh'), $script.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding $false))
$ErrorActionPreference = 'Continue'
$console = (docker run --rm --network daffyreplay_default -v "${script:Root}:/src:ro" -v "${out}:/out" $Image bash /out/run.sh 2>&1 |
    ForEach-Object { "$_" }) -join "`n"
$ErrorActionPreference = 'Stop'
Write-Host $console.Trim()

$failures = 0
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Host "ok    $name" } else { Write-Host "FAIL  $name  $detail" -ForegroundColor Red; $script:failures++ }
}

Check "the target store's key is owner-only on Linux" ($console -match 'key file: 600')
$serial = Get-Content -Raw "$out/serial.json" | ConvertFrom-Json
$new = [long]$serial.outcomes.counts.new_error + [long]$serial.outcomes.counts.changed_error
Check "Linux: the committed recording replays with no new errors and no row differences" (
    ($serial.coverage.replayed -eq $serial.coverage.requests) -and ($new -eq 0) -and ($serial.outcomes.rowsDiffer -eq 0)) "new $new, rows $($serial.outcomes.rowsDiffer)"
$pace = (Get-Content -Raw "$out/pacing.json" | ConvertFrom-Json).pace
Check "Linux: scheduler error p99 at most 2 ms" ($pace.schedulerError.p99 -le 2) "p99 $($pace.schedulerError.p99) ms"
Check "Linux: requests start within 2 ms of their time at p99" ($pace.lagHost.p99 -le 2) "p99 $($pace.lagHost.p99) ms"
Write-Host "      pacing on Linux: scheduler error p99 $($pace.schedulerError.p99) ms, host lag p99 $($pace.lagHost.p99) ms"

if ($failures -gt 0) { Write-Host "$failures check(s) FAILED" -ForegroundColor Red; exit 1 }
Write-Host "all checks passed"
