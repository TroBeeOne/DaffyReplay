<#
.SYNOPSIS
    Records a workload through DaffyTee, as DaffyReplay's users will: the database backed up the
    moment recording starts, DaffyTee started before the application connects, --raw and --bulk full.

.DESCRIPTION
    1. Puts the source SQL Server (docker/compose.yml) back to the seed, and backs it up as -Name.
    2. Starts the installed daffytee (0.3.0 or later) in front of it, with its own throwaway user
       store: logins app, orders and reports, mapped to svc_app, app_user and report_user.
    3. Runs ReplayProbe through it: every ADO.NET, EF Core and ODBC scenario, then -Load seconds of
       concurrent sessions; then the JDBC probe (probes/jdbc), in a Java container, through the same
       DaffyTee - unless -NoJdbc. The Microsoft JDBC driver (MIT) is downloaded into work/jdbc the
       first time; it is used only here, never shipped.
    4. Stops DaffyTee with :quit, so everything it captured is delivered, and checks the recording:
       no gaps, nothing truncated, and every replayable request carrying its raw bytes.

    The recording is -Out; the backup is /backups/<Name>-ReplayTest.bak (and ReplayOther) in the
    containers' shared volume, ready for Restore-Target.

.EXAMPLE
    scripts/record-sample.ps1 -Out samples/workload.ndjson -Load 3 -Workers 3
#>
param(
    [string]$Out = "work/recording.ndjson",
    [string]$Name = "sample",
    [int]$Load = 5,
    [int]$Workers = 4,
    [int]$Port = 14370,
    [string]$Configuration = "Debug",
    [string]$DaffyTee = "daffytee",
    [switch]$NoJdbc,
    [string]$JdbcVersion = "13.6.0.jre11",
    [string]$JavaImage = "eclipse-temurin:21-jdk"
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/common.ps1"

$Out = if ([IO.Path]::IsPathRooted($Out)) { $Out } else { [IO.Path]::GetFullPath((Join-Path (Get-Location) $Out)) }
$work = Join-Path ([IO.Path]::GetTempPath()) ("daffyreplay-record-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $work, (Split-Path -Parent $Out) | Out-Null
$password = "Probe!" + [guid]::NewGuid().ToString('N').Substring(0, 12)
$events = Join-Path $work 'events.ndjson'

Write-Host "building ReplayProbe..."
dotnet build "$script:Root/probes/ReplayProbe" -c $Configuration -v q -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed" }
$probe = "$script:Root/probes/ReplayProbe/bin/$Configuration/net10.0/ReplayProbe.exe"

Wait-Healthy source
Write-Host "resetting the source to the seed, and backing it up as '$Name'..."
Reset-Source
Backup-Source $Name

Write-Host "starting $(& $DaffyTee --version | Select-Object -First 1) on $Port..."
$proxy = Start-DaffyTee -Work $work -Port $Port -Events $events -Target '127.0.0.1,14361' -Password $password -Exe $DaffyTee
try {
    & $probe --server "127.0.0.1,$Port" --password $password --load $Load --workers $Workers | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "the workload did not run cleanly; see above" }

    if (-not $NoJdbc) {
        $jar = Join-Path $script:Root "work/jdbc/mssql-jdbc-$JdbcVersion.jar"
        if (-not (Test-Path $jar)) {
            New-Item -ItemType Directory -Force (Split-Path $jar) | Out-Null
            Invoke-WebRequest -UseBasicParsing -OutFile $jar "https://repo1.maven.org/maven2/com/microsoft/sqlserver/mssql-jdbc/$JdbcVersion/mssql-jdbc-$JdbcVersion.jar"
        }
        # Docker Desktop carries host.docker.internal to this machine's loopback, where DaffyTee listens.
        $previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        docker run --rm -v "$(Join-Path $script:Root 'probes/jdbc'):/p:ro" -v "$(Split-Path $jar):/j:ro" $JavaImage `
            java -cp "/j/$(Split-Path -Leaf $jar)" /p/JdbcProbe.java host.docker.internal $Port $password 2>&1 | ForEach-Object { "$_" } | Out-Host
        $code = $LASTEXITCODE; $ErrorActionPreference = $previous
        if ($code -ne 0) { throw "the JDBC workload did not run cleanly; see above" }
    }
}
finally {
    Stop-DaffyTee $proxy
}

# The recording must be one a replay can trust.
$all = @(Read-Events $events)
$requests = @($all | Where-Object { $_.type -like 'request.*' })
$problems = @()
if (@($all | Where-Object type -eq 'proxy.gap').Count -gt 0) { $problems += "events were dropped (proxy.gap)" }
if (@($requests | Where-Object { $_.data.request.truncated }).Count -gt 0) { $problems += "requests were truncated" }
$noRaw = @($requests | Where-Object { -not $_.data.request.raw -and -not $_.data.request.redacted -and -not $_.data.request.gatewayCommand })
if ($noRaw.Count -gt 0) { $problems += "$($noRaw.Count) request(s) without raw bytes" }
if (@($all | Where-Object type -eq 'proxy.stop').Count -eq 0) { $problems += "no proxy.stop: DaffyTee did not shut down cleanly" }
if ($problems.Count -gt 0) { throw "the recording is not fit to replay: $($problems -join '; '). Evidence kept in $work" }

Copy-Item $events $Out -Force
$sessions = @($all | Where-Object type -eq 'session.start').Count
Write-Host ""
Write-Host "recorded $($requests.Count) requests in $sessions sessions -> $Out ($([math]::Round((Get-Item $Out).Length / 1KB)) KB)"
Write-Host "backup: /backups/$Name-ReplayTest.bak, /backups/$Name-ReplayOther.bak"
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
