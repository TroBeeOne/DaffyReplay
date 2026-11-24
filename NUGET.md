# DaffyReplay

DaffyReplay takes a recording of real SQL Server traffic made by
[DaffyTee](https://www.nuget.org/packages/DaffyTee) and plays it against a test or pre-production
SQL Server - as fast as possible, one request at a time, or with the recorded timings - then reports
what failed that did not fail before, which row counts changed, and how the timings compare.

It replays at the wire. A DaffyTee recording made with `--raw` holds every request's exact TDS
bytes, so the replay sends exactly what the application sent: the same parameter types, precision
and collations, table-valued parameters, prepared statements, server cursors, bulk loads and
pooled-connection resets - with nothing installed or switched on in either database, no trace, no
Extended Events session.

## What it is for

- **Upgrades** - record on the version you run today and replay on the one you are moving to, to
  see what breaks and what slows down before you move. Most servers this matters for are older than
  2022: record on SQL Server 2016, 2017 or 2019 and replay on 2022 - or go straight to 2025. Replay
  first at the database's old compatibility level, then at the new one, and `compare` the two.
- **Compatibility levels, indexes, schema and configuration changes** - replay the same recording
  before and after, on the same server, and `compare` the two runs statement by statement.
- **Migrations** - a new server, new hardware, a new platform, measured against the real workload
  rather than a synthetic one.
- **Regression testing** - does every statement the application sends still work, and return the
  same number of rows?
- **Load and stress testing** - the real workload at its recorded rate, at twice the rate, or as fast
  as the server will take it.

## How it fits

```
production app ──► DaffyTee ──► production SQL Server
                      │  --sink file --raw --bulk full
                      ▼
          events-YYYYMMDD.ndjson
                      │
                      ▼
              daffyreplay run ──► test SQL Server, restored to the moment recording began
                      │
                      ▼
     replay.log  report.json  report.html  (results.ndjson)
```

DaffyReplay records nothing itself. To record a replay - for a byte-for-byte check, say - point
DaffyReplay at a DaffyTee and point that DaffyTee at the test server.

```
dotnet tool install -g DaffyReplay      # install
dotnet tool update -g DaffyReplay       # upgrade to the latest version
daffyreplay --version
```

## Quick start

**1. Record, with DaffyTee in front of the application** (the [recommended](#recording-with-daffytee)
settings for a replay):

```
daffytee --sink file --sink-path "D:\capture\events-{date}.ndjson" --raw --bulk full --max-event-kb 8192
```

Back up the database the moment recording starts (or plan a point-in-time restore to it), and start
DaffyTee before the application connects - or record just a window of a running system, as in
[Recording a peak window](#recording-a-peak-window).

**2. Look at what was recorded:**

```
daffyreplay inspect "D:\capture\events-202610*.ndjson"
```

It says how much can be replayed, and anything that would make a replay mean less: sessions that
began before the recording, events DaffyTee had to drop, requests without their raw bytes.

**3. Name the server to replay into.** It prompts for the password, logs in to prove it works, and
stores the password encrypted:

```
daffyreplay target add DEV01 --target "Server=tcp:sqltest01,1433;User Id=replay_user;Encrypt=Mandatory;TrustServerCertificate=True"
```

**4. Restore the backup on DEV01, and replay:**

```
daffyreplay run "D:\capture\events-202610*.ndjson" --target DEV01
```

It shows what it is about to do and asks before starting. While it runs, the console shows elapsed
time against recorded time, sessions, requests per second, new errors and lag; Ctrl+C stops cleanly
and still writes the report for what ran. Open `report.html` in the run's folder.

## Recording with DaffyTee

The file sink alone is enough - no RabbitMQ or ClickHouse is needed - and a busy six hours can be
several GB: DaffyReplay streams the files and never loads them whole. With `{date}` in the name they
roll at midnight, so give DaffyReplay a list or a wildcard. DaffyTee's own guide covers mapping
users and pointing applications at it; these are the switches that decide what a replay can do.

### Recommended: everything a replay can use

```
daffytee --sink file --sink-path "D:\capture\events-{date}.ndjson" --raw --bulk full --max-event-kb 8192
```

| Switch | What DaffyTee records | What DaffyReplay does with it |
|---|---|---|
| `--sink file --sink-path ...` | every event as a line of JSON, one file a day | reads the files, any number, by list or wildcard |
| `--raw` | each request's exact bytes, with its decoded SQL and parameters | sends those bytes: the most faithful replay |
| `--bulk full` | bulk loads (`SqlBulkCopy`, bcp) with their rows | replays them, rows and all |
| `--max-event-kb 8192` | requests up to 8 MB whole (the default is 1 MB) | larger requests are cut short and cannot be replayed, so raise it if bulk loads or big parameters need it |

`inspect` then says the recording is whole:

```
  daffytee    0.3.0 at daffytee://DEV01:1433: on, sampling 100% of requests, bulk full, raw payloads, passwords masked
  requests    1,282: rpc 836, txn 274, batch 170, bulk 2
              replayable 1,282 (100.0 %): exactly 1,282

  No warnings: the recording is complete and every request can be replayed exactly.
```

### Other choices, and what they cost a replay

Each changes what DaffyTee writes and what `inspect` and the report then say.

**Leave bulk loads out** - an ETL load in the middle of the workload is large, and often not what
you are testing:

```
daffytee --sink file --sink-path "D:\capture\events-{date}.ndjson" --raw --bulk meta
```

`--bulk meta`, DaffyTee's default, records each bulk load's table, rows and bytes, not the rows;
`--bulk off` records no event for them at all. Either way the `INSERT BULK` statement that starts a
load is still recorded. DaffyReplay leaves each load out together with its `INSERT BULK` - the server
would refuse whatever came next if one were sent alone - and says so:

```
              skipped 2: bulk loads recorded without their rows (DaffyTee --bulk meta)
              skipped 2: INSERT BULK statements whose rows are not replayed (they go together)
```

The tables those loads filled stay as the restore left them, so statements that read them later
can report different row counts. To decide at replay time instead, record with `--bulk full` and
add `--no-bulk` to `daffyreplay run`.

**Add a SQL log for people** - the same requests as runnable T-SQL, beside the events:

```
daffytee --sink file --sink-path "D:\capture\events-{date}.ndjson" --raw --bulk full --max-event-kb 8192 --sql-log "D:\capture\sql-{date}.log"
```

DaffyReplay does not read it; it costs disk.

**Decode larger responses** - `--response-kb 256` (64 by default). DaffyTee decodes each response up
to that size for its per-statement row counts, result-set sizes and errors, which is what the replay
compares statement by statement; larger responses are compared on their final count. It costs
DaffyTee memory per request in flight. Give `daffyreplay run` the same `--response-kb`.

**Without `--raw`** - smaller files. Batches and `SqlTransaction` requests still replay exactly,
rebuilt from their text and fields; procedure calls - stored procedures, and every parameterised
query (`sp_executesql`) - are skipped, or with `daffyreplay run --approximate` replayed as T-SQL with
literal values, marked approximate and compared on results and errors only. `inspect` warns:

```
    - DaffyTee was not recording raw bytes (--raw): procedure calls cannot be replayed exactly
```

Record without `--raw` only with DaffyTee 0.3.1 or later. Earlier versions decoded a `datetime`
value wrong when its time of day was after 00:02:23, so `inspect` warns about procedure calls that
hold one, and `--approximate` skips them rather than send the wrong time.

**Sampled** - `--sample 10%` captures a random tenth of requests: sessions full of holes, which a
replay cannot make sense of. If you have to sample, sample whole connections -
`--sample 25% --sample-by session` - for a functional check of a quarter of the load. `inspect` warns
either way.

**A queue for heavy traffic** - events wait in memory before they are written, up to
`--queue-max-mb` (256). Events dropped past that (`proxy.gap`) are holes in the replay, and
`inspect` counts them; give a busy server with large bulk loads more.

### Before you record

- **Back up the database, or take a snapshot, the moment recording starts - and restore it before
  every replay.** Without it, inserts hit duplicate keys, updates and deletes touch different rows,
  and the report is mostly noise. The report labels the errors typical of this. Under the full
  recovery model, a point-in-time restore to the moment recording began does the same - see
  [Recording a peak window](#recording-a-peak-window).
- **Restoring on another server?** Its logins need the same SIDs as the original's, or the restored
  database's users are orphaned and every session fails to open it. Create the logins with
  `CREATE LOGIN ... WITH SID = ...`, or remap the users after the restore (`ALTER USER ... WITH LOGIN`).
- **Start DaffyTee before the application connects** where you can. A session already open when
  recording began is missing its earlier state - SET options, temp tables, prepared statements - and
  `inspect` lists them. Applications on a connection pool reset each connection when they reuse it,
  which drops its temp tables and resets its SET options, so for them this costs little.
- Requests whose passwords DaffyTee masked (`CREATE LOGIN ... PASSWORD`) carry no raw bytes and are
  skipped.

## Recording a peak window

DaffyTee can run as a plain gateway for days and record only when asked. Its tee - the copy of
each request - switches on and off at its prompt while every session carries on untouched. Say the
busiest half hour is Monday at 10:00:

**1. Run DaffyTee as the gateway, the tee off** - every recording setting in place for when it
comes on:

```
daffytee --sink file --sink-path "D:\capture\events-{date}.ndjson" --raw --bulk full --max-event-kb 8192 --tee off
```

Applications connect through it as usual and nothing is captured; the file gets only DaffyTee's own
start and heartbeat events. Keep it running in a terminal: the `daffytee>` prompt is where the tee
is switched, and only there - by design, an admin's T-SQL cannot switch the record of their own
actions off.

**2. Have a way back to the moment the tee comes on.** Under the full recovery model - most
production databases - nothing is needed now: your regular full and log backups will restore to
any moment. Under the simple model, take a `COPY_ONLY` full backup in a quiet moment just before,
and switch the tee on as soon as it finishes.

**3. Switch it on, at DaffyTee's prompt:**

```
daffytee> :tee on
daffytee> :status
  ...
  tee      : on, sampling 100% of requests, bulk full, raw payloads, passwords masked
```

**4. Half an hour later, switch it off:**

```
daffytee> :tee off
```

Events already captured are still written. Use `:tee off`, not `:quit`: `:quit` (and Ctrl+C) stops
DaffyTee itself and disconnects every session, which in production is an outage. DaffyTee writes a
`proxy.control` event each time, saying who switched the tee and when.

**5. Restore the test server to the moment the tee came on.** `inspect` shows it, in UTC:

```
daffyreplay inspect "D:\capture\events-20261012.ndjson"

  daffytee    0.3.0 at daffytee://DEV01:1433: on, sampling 100% of requests, bulk full, raw payloads, passwords masked
              changed 2026-10-12 15:00:02 tee on (by console)
              changed 2026-10-12 15:30:05 tee off (by console)
  sessions    214: 37 began during the recording, 177 before it
```

Take a log backup, and on the test server restore the last full backup, then the log backups up to
that moment. The `proxy.control` event in the file has the time to the millisecond; `STOPAT` takes
the SQL Server's local time (here UTC-5):

```sql
RESTORE DATABASE Sales FROM DISK = N'E:\backup\Sales_full.bak' WITH NORECOVERY, REPLACE;
RESTORE LOG Sales FROM DISK = N'E:\backup\Sales_log_0945.trn' WITH NORECOVERY;
RESTORE LOG Sales FROM DISK = N'E:\backup\Sales_log_1015.trn' WITH STOPAT = '2026-10-12T10:00:02.250', RECOVERY;
```

Then put the identity columns back where production had them. SQL Server hands out identity values
from a cached block, and a database recovered to a point in time carries on after that block: the
replay's inserts would get other ids than production gave, and every later statement that names one
of them would read a different row. In each restored database:

```sql
DECLARE @table nvarchar(400), @column nvarchar(300), @sql nvarchar(max);
DECLARE identities CURSOR LOCAL FAST_FORWARD FOR
    SELECT QUOTENAME(SCHEMA_NAME(t.schema_id)) + N'.' + QUOTENAME(t.name), QUOTENAME(c.name)
    FROM sys.identity_columns AS c JOIN sys.tables AS t ON t.object_id = c.object_id
    WHERE t.is_ms_shipped = 0 AND CONVERT(bigint, c.increment_value) > 0;
OPEN identities;
FETCH identities INTO @table, @column;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'DECLARE @max bigint = (SELECT MAX(' + @column + N') FROM ' + @table + N');
                 IF @max IS NOT NULL DBCC CHECKIDENT (''' + REPLACE(@table, N'''', N'''''') + N''', RESEED, @max) WITH NO_INFOMSGS;';
    EXEC sys.sp_executesql @sql;
    FETCH identities INTO @table, @column;
END;
CLOSE identities;
DEALLOCATE identities;
```

**6. Replay the window:**

```
daffyreplay run "D:\capture\events-20261012.ndjson" --target DEV01
```

`inspect` and the run warn that sessions began before the recording - the application's pooled
connections, open since long before - which is expected here. Right at the start, expect a new
error or two: a transaction that was open when the tee came on has its first statements neither in
the recording nor, uncommitted at that moment, in the restored database, so on replay its later
statements run on their own and its commit fails ("The COMMIT TRANSACTION request has no
corresponding BEGIN TRANSACTION", 3902).

Keeping the tee on all the time works too, disk allowing: replay any window of it with `--from` and
`--to`, and restore to `--from` the same way.

## Targets and logins

Passwords are never recorded, so the logins a replay uses come from the target. By default every
session logs in as the target's own login. Sessions recorded as a particular SQL Server login - or
from a particular DaffyTee login - can be mapped to logins of their own, so permissions behave as
they did:

```
daffyreplay target logins DEV01 add svc_reports --login svc_reports         # recorded SQL Server login
daffyreplay target logins DEV01 add daffytee:alice --login alice_test       # sessions of DaffyTee login alice
```

| Command | What it does |
|---|---|
| `target add NAME [--target "..."]` | a target: server, encryption, default login. Logs in first; saves nothing if that fails |
| `target list`, `target show NAME` | the targets, never a password |
| `target test NAME` | log in again now, as the default login and every mapped one |
| `target logins NAME [add RECORDED \| remove RECORDED]` | the mapped logins |
| `target remove NAME` | delete a target and its credentials |

Targets are stored encrypted (AES-256-GCM, its key protected by DPAPI on Windows, owner-only on
Linux) in `%LOCALAPPDATA%\DaffyReplay`, or `--config-dir` / `DAFFYREPLAY_HOME`. Encryption is
Mandatory (TDS 7.4) unless the connection string says `Encrypt=Strict` (TDS 8.0, which needs a
certificate configured on the server) or `Encrypt=Optional`. An Entra ID application
(`Authentication=ActiveDirectoryServicePrincipal`, for Azure SQL) is supported with DaffyTee's own
code for it, but has not yet been tested with DaffyReplay.

## Running a replay

| `--pace` | What it does | Good for |
|---|---|---|
| `recorded` (default) | Each request starts at its recorded offset from the start. `--speed 2` halves every gap. A session stays sequential, as the application was: if its previous request has not finished, the next waits, and the wait is reported as lag. | Real-world load |
| `fast` | No waiting: every session sends its requests back to back, sessions concurrently. `--max-sessions` caps how many. | Throughput and stress |
| `serial` | One request at a time across all sessions, in recorded order. A request still running after `--serial-wait` seconds (2) is almost always blocked by a lock another session releases later in the sequence; the replay carries on, as the recording did, and counts it. | Functional regression, and per-statement timings without concurrency |

Filters work for both `inspect` and `run`: `--from` / `--to` (recorded time, UTC), `--app`,
`--login`, `--database` (all accept `*`), `--session N`, `--exclude` (a regular expression matched
against the SQL), and `--no-bulk` (bulk loads and their `INSERT BULK` statements).

Other options: `--request-timeout S` cancels a request after S seconds, as a client's command timeout
would (30 by default in serial pace); `--replay-cancels` cancels a request that was cancelled when
recorded after as long as it ran then; `--keep-app-name` and `--keep-host-name` log in with each
session's recorded application and host name, for when `APP_NAME()` matters (Resource Governor
classifiers, application logic); `--results` also writes every request's result; `--out DIR` names
the run's folder; `--yes` skips the question, for scripts.

**Safety.** DaffyReplay refuses a target that is the server the recording was made on - compared by
host and port, and by the addresses they resolve to - unless you add `--same-target` (a restored
pre-production server can be what you mean). It never replays DaffyTee's own `daffytee.*` commands,
refused logins, or distributed-transaction requests. Everything else the application did - inserts,
updates, deletes, schema changes - it does again.

## Examples

Restore the target before each run; every example assumes it.

**Does everything still work?** One request at a time, in recorded order - the cleanest functional
check, and each statement's timing without concurrency in the way:

```
daffyreplay run "D:\capture\events-20261012.ndjson" --target DEV01 --pace serial
```

**Production's load, as it happened.** The default pace; the console and the report show whether
the replay kept up (lag) and how each statement's time compares:

```
daffyreplay run "D:\capture\events-20261012.ndjson" --target DEV01
```

**Twice production's load**, or everything as fast as the server takes it, 50 sessions at a time:

```
daffyreplay run "D:\capture\events-20261012.ndjson" --target DEV01 --speed 2
daffyreplay run "D:\capture\events-20261012.ndjson" --target DEV01 --pace fast --max-sessions 50
```

**An upgrade from 2019 to 2022.** Restore the 2019 backup on the 2022 server; the database keeps
compatibility level 150. Replay, restore again, raise the level, replay again, and compare the runs
statement by statement:

```
daffyreplay run "D:\capture\events-20261012.ndjson" --target SQL2022 --out D:\replays\level-150
```

```sql
-- restore again, then:
ALTER DATABASE Sales SET COMPATIBILITY_LEVEL = 160;
```

```
daffyreplay run "D:\capture\events-20261012.ndjson" --target SQL2022 --out D:\replays\level-160
daffyreplay compare D:\replays\level-150 D:\replays\level-160
```

The same goes for an index, a configuration change or new hardware: one run before, one after.

**One application, one hour:**

```
daffyreplay run "D:\capture\events-202610*.ndjson" --target DEV01 --app "OrdersApi*" --from 2026-10-12T15:00:00Z --to 2026-10-12T16:00:00Z
```

**Leave things out** - a nightly purge that would empty the tables the rest reads, and the bulk loads:

```
daffyreplay run "D:\capture\events-20261012.ndjson" --target DEV01 --exclude "^exec dbo\.PurgeHistory" --no-bulk
```

**When the application name matters** - Resource Governor classifies sessions by it, or the
application's own logic reads `APP_NAME()` - log in as each session did; and cancel requests as the
application's command timeouts did:

```
daffyreplay run "D:\capture\events-20261012.ndjson" --target DEV01 --keep-app-name --keep-host-name --replay-cancels
```

**A recording made without `--raw`**, its procedure calls rendered as T-SQL:

```
daffyreplay run "D:\capture\events-20261012.ndjson" --target DEV01 --approximate
```

**In a script or a pipeline.** `--yes` skips the question and `report.json` holds the results; the
run's exit code says only whether it ran (0), was interrupted (130) or could not start (1):

```powershell
daffyreplay run "D:\capture\events-20261012.ndjson" --target DEV01 --pace serial --yes --out D:\replays\nightly
$report = Get-Content D:\replays\nightly\report.json | ConvertFrom-Json
if ($report.outcomes.counts.new_error -gt 0) { throw "$($report.outcomes.counts.new_error) new errors" }
```

## What a replay does with each request

- **Raw bytes are sent as recorded**, framed at the connection's packet size, with two things
  changed because they must be: the **transaction descriptor** (every request inside a transaction
  names it, and the replay server gives out its own; DaffyReplay follows the server's transaction
  announcements and writes the current one into each request, exactly as SqlClient does), and the
  outstanding-request count, which is 1.
- **Prepared statements and server cursors** (`sp_prepexec`, `sp_execute`, `sp_cursorprepexec`,
  `sp_cursorfetch`, ...) name handles the server gave out. Each recorded handle is mapped to the one
  the replay was given, and a cursor's fetches are read by the column metadata that came with its
  open.
- **Pooled-connection resets** are sent as the reset flag on the request that carried it.
- **Bulk loads** replay with their rows, together with the `INSERT BULK` statement that announces
  them - or, without their rows, not at all. A client that negotiated column encryption at login - as
  SqlClient always does - lays out its bulk metadata differently, so each session logs in the way its
  recorded bulk loads show its client did.
- **MARS sessions** replay in recorded order on one connection.
- **Without raw bytes**, a batch is rebuilt exactly from its text and a transaction request from its
  fields; a procedure call is skipped, or with `--approximate` rendered as T-SQL with its values as
  literals - marked approximate in the report and compared on its results and errors only.

## The report

Each run writes a folder (`--out`, by default `replay-YYYYMMDD-HHMMSS`):

| File | What is in it |
|---|---|
| `report.html` | The report: one self-contained page, readable offline and in light or dark |
| `report.json` | The same, for scripts and for `compare` |
| `run.json` | What was replayed, where, how - never a password |
| `replay.log` | DaffyReplay's own log, JSON lines in DaffyTee's format |
| `results.ndjson` | With `--results`: every request, recorded and replay side by side, loadable next to the recording |

**Outcomes.** Each request against its recording: *the same* (ok both times, or the same errors),
*new error* (ok when recorded, failed on replay), *changed error*, *fixed* (failed when recorded,
ok now), *timed out*, *failed* (the connection broke), *not replayed* (its session could not log in).
New errors are grouped by error number and statement; duplicate-key and foreign-key errors are
labelled as typical of data that differs from the recording's.

**Row counts.** Every statement's row count and every result set's size are compared, where
DaffyTee decoded the recorded response (it does for small ones); for larger ones, the final count.
After a restore, differences usually come from server-generated values - identities, `NEWID()`,
`GETDATE()` - and from concurrency ordering differently than it did in production; `serial` pace
reproduces them most closely.

**Timing.** p50, p95, p99 and maximum, recorded against replayed, overall and for each statement,
with the statements that got slower or faster by total time and by p95. A statement is its
procedure for a procedure call, otherwise its SQL with literals replaced and whitespace and case
normalised. Recorded timings come from production's hardware under production's concurrency; the
most telling comparison is one replay against another on the same server:

```
daffyreplay compare replay-before replay-after
```

**Pace.** In `recorded` pace, how late requests started behind the recorded schedule, split into
waiting on the server (the session's previous request was still running) and the replay host
falling behind (which would make the results understate the server), and requests per interval,
recorded against replayed.

`daffyreplay report RUN_DIR` writes report.html again from report.json.

## From RabbitMQ and ClickHouse

The file sink is the simple path to a replay. If you already publish DaffyTee's events to RabbitMQ
and a subscriber keeps them in ClickHouse, a slice of them becomes an ordinary event file - each row
must be the whole event, exactly as DaffyTee published it (with `--raw`):

```
clickhouse client --query "SELECT event FROM daffytee.events
                           WHERE time BETWEEN '2026-10-12 15:00:00' AND '2026-10-12 15:30:00'
                           ORDER BY time FORMAT TSVRaw" > slice.ndjson
daffyreplay inspect slice.ndjson
```

The table and column names are your subscriber's. A slice has no `proxy.start`, which `inspect`
mentions and is fine; restore to the slice's start as for a peak window.

## To and from sqlsim

[sqlsim](https://github.com/microsoft/sqlsimtools) (Microsoft's open-source sqlsimtools, in public
preview) replays a workload from its own replay XML, which its `convert-xel.ps1` makes from an
Extended Events capture. DaffyReplay writes that XML from a DaffyTee recording, and reads it into a
recording it can run.

### Export: a DaffyTee recording for sqlsim

```
daffyreplay export sqlsim "D:\capture\events-*.ndjson" --out D:\capture\replay.xml
sqlsim -S testsql01 -d Shop -E -replay fast -replayfile D:\capture\replay.xml
```

Every request goes in as T-SQL. Batches go as recorded. Procedure calls go as `--approximate` writes
them: declared types, table-valued and OUTPUT parameters as variables, prepared statements as
`sp_executesql`. Transactions the application ran through its driver go as `BEGIN`, `COMMIT`,
`ROLLBACK` and `SAVE TRANSACTION`, which an Extended Events capture never has. The filters
(`--from`, `--to`, `--app` and the rest) work as for `run`. What cannot be written as text - bulk
loads, server cursor calls, statements prepared before the recording began - is counted instead.
A file holds at most 100,000 events and 100 MB, sqlsim's limits; past that the export goes on in
`replay-2.xml` and on, each replayed on its own.

`-replay fast` gives each recorded session a connection of its own; `-replay stress -n 1 -r 1` runs
every request on one connection in recorded order. sqlsim logs every session in as the one login it
is given, does not reset a pooled connection between uses, and has no recorded pace; the export
lists what that means for your recording.

### Import: sqlsim's XML as a recording

```
daffyreplay import sqlsim D:\capture\replay.xml --out D:\capture\imported.ndjson
daffyreplay inspect D:\capture\imported.ndjson
daffyreplay run D:\capture\imported.ndjson --target TEST01 --pace serial
```

Each batch and procedure call becomes a request DaffyReplay sends as T-SQL: Extended Events writes a
procedure call as the T-SQL that makes it. Connections are kept as they were used - a pooled one
reused carries its reset - and each keeps its requests in order. DaffyReplay reads only this XML,
never an .xel file: for one, run sqlsim's `convert-xel.ps1` first. `--default-database` names the
database for sessions whose events do not.

An Extended Events capture knows less than a DaffyTee recording, and the report says so first, in
its own box:

- **Transactions the application ran through its driver are not in it** - SqlTransaction, EF Core,
  JDBC with autocommit off. Their statements replay one at a time, and work the application rolled
  back is committed. `BEGIN TRAN` written in the SQL is there.
- **Errors raised while a statement ran were recorded as OK** - a divide by zero, a duplicate key, a
  RAISERROR - so a new error may have happened when recorded too.
- **Rows are known per batch, not per statement.** A batch's total is compared with the replay's; a
  procedure call's rows are not compared, since the text Extended Events wrote for it adds statements
  of its own.
- **Every request replays as text**, with literal values rather than typed parameters.
- **Logins.** sqlsim's capture does not record them, so sessions replay as the target's login - unless
  the capture had the `username` action, which the import carries through.

## Good to know

- **Only what went through DaffyTee is replayed.** SQL Agent jobs, other applications, and the work
  procedures and triggers do inside the server are not separate requests; the server re-runs
  procedure and trigger bodies itself.
- **Login options.** DaffyTee 0.3 does not record the options each application logged in with;
  replayed sessions log in as SqlClient does (packet size 8000, the login's default language), and the
  report says so.
- **Non-determinism** - `GETDATE()`, `NEWID()`, plans the optimizer chooses differently - makes some
  differences expected.
- **SQL Server versions.** Tested: recordings from SQL Server 2022, replayed on 2022 and on 2025.
  2016, 2017 and 2019 speak the same protocol (TDS 7.4) and are expected to work as either, but have
  not been tested yet.
- **Named instances** by name are not supported: give the instance's port (`sqltest01,50123`).
- **The results and the report hold SQL and parameter values.** Treat them as you treat the recording.

## What has been tested

Recordings made through DaffyTee 0.3.0 and 0.3.1 from four client stacks - Microsoft.Data.SqlClient
(ADO.NET), Entity Framework Core 10, the Microsoft ODBC Driver 18 and the Microsoft JDBC Driver
13.6 - replay into a restored copy on SQL Server 2022 with no new errors and no row-count
differences, at serial and recorded pace: ad-hoc and parameterised SQL, stored procedures with OUTPUT
parameters and return values, table-valued parameters, nearly every data type including MAX values,
SqlTransaction with savepoints, ODBC and JDBC manual-commit transactions, prepared statements and
JDBC server cursors, pooled-connection resets, bulk copy, MARS, temp tables, USE, multiple result
sets, large results, errors, cancels, and a login mapped separately from the default. Replayed
through a second DaffyTee, every request matched its recording byte for byte apart from the
transaction descriptor. A SQL Server 2022 recording also replays on SQL Server 2025 over TDS 8.0
(Encrypt=Strict), and has been replayed into Azure SQL Database, logging in with SQL authentication
and following its redirect. At recorded pace the scheduler holds the recorded timings to within 0.2
ms at p99, and requests start within 1 ms of them, on Windows and Linux. Procedure calls recorded
without raw bytes replay approximately with no new errors, a `datetime` at 13:14:15.123 among their
parameters. A peak window - the tee switched on and off at DaffyTee's prompt in front of a running
pooled workload, bulk loads recorded without their rows - replays after a point-in-time restore with
no failed sessions, and in two runs at most one new error and three row-count differences out of
about 15,000 requests.

With sqlsim 1.0.0, one workload of all four stacks, captured at once by DaffyTee and by sqlsim's
Extended Events capture: exported, 99 % of the requests replayed in sqlsim and every table but the
bulk-loaded one ended as the source did, with no failure that had not happened when recorded other
than temp tables a reused connection kept. sqlsim 1.0.0 replaying its own capture of the same
workload failed on most procedure calls: it binds parameters as ODBC sends them, and SqlClient's and
JDBC's named values and table-valued parameters fail there. Imported, the capture replayed whole in
DaffyReplay; its new errors were only those Extended Events recorded as OK, and an EF Core savepoint
without the transaction its driver began; 310 of 318 batches' row totals agreed.

Not yet tested: Entra ID targets, and SQL Server versions before 2022.

## Protocol and trademarks

DaffyReplay implements the Tabular Data Stream protocol from Microsoft's published [MS-TDS]
specification; it contains no Microsoft code and is not affiliated with, endorsed by, or sponsored by
Microsoft Corporation. Microsoft, SQL Server, Azure and Entra are trademarks of Microsoft Corporation.
Java is a trademark of Oracle and/or its affiliates. RabbitMQ is a trademark of Broadcom Inc.
ClickHouse is a trademark of ClickHouse, Inc.

## License

DaffyReplay is licensed under the **MIT License**, from version 0.2.1: free to use, copy, change and
redistribute, commercially or not, in production or anywhere else. Versions 0.2.0 and earlier were
published under the Business Source License 1.1.

The full terms are in `LICENSE.txt` in the package. Copyright (c) 2026 Scott Traube and TroBeeOne LLC:
the license asks that this copyright notice and the license text go with every copy, or substantial
part, of DaffyReplay you pass on. Its one bundled dependency keeps its own license, listed in
`THIRD-PARTY-NOTICES.txt`: System.Security.Cryptography.ProtectedData (MIT).

Two things worth knowing, as it comes with no warranty:

- **A replay writes.** It sends every recorded insert, update, delete and schema change to the target
  you name, under the logins you give it. Choosing that target, and restoring it, is up to you.
- **Recordings and reports hold what was recorded:** SQL text, parameter values and logins, which may
  be personal or confidential. DaffyReplay sends no data or telemetry anywhere.

## Contact

TroBeeOne LLC: [trobee.one](https://trobee.one) ·
[LinkedIn](https://www.linkedin.com/company/trobee-one-llc) ·
source on [GitHub](https://github.com/TroBeeOne/DaffyReplay)
