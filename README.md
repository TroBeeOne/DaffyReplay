<img src="icon.png" alt="DaffyReplay" width="128" align="right">

# DaffyReplay

[![NuGet](https://img.shields.io/nuget/v/DaffyReplay.svg)](https://www.nuget.org/packages/DaffyReplay)

Replays a [DaffyTee](https://www.nuget.org/packages/DaffyTee) recording of SQL Server traffic against
a test or pre-production server, and reports what changed. It is a .NET tool on NuGet,
[DaffyReplay](https://www.nuget.org/packages/DaffyReplay):

```
dotnet tool install -g DaffyReplay
```

[NUGET.md](NUGET.md) is the user guide, the same page as on NuGet: recording for replay, targets,
paces, the report. This file is for working on DaffyReplay.

- [Build and run](#build-and-run) · [How it works](#how-it-works) · [Design notes](#design-notes)
- [Testing](#testing) · [Layout](#layout) · [Relation to DaffyTee](#relation-to-daffytee) · [Licence](#licence)

## Build and run

```
dotnet build DaffyReplay.slnx
dotnet run --project src/DaffyReplay -- --help
dotnet pack src/DaffyReplay -c Release -o nupkg        # nupkg/DaffyReplay.<version>.nupkg
```

.NET 10. The tool's one package dependency is System.Security.Cryptography.ProtectedData (DPAPI for
the target store on Windows); there is no SqlClient in it - it speaks TDS itself.

The test environment is `docker/compose.yml`: a *source* SQL Server 2022 (14361) that plays production,
behind DaffyTee while a workload is recorded; a *target* 2022 (14362) to replay into; and, with
`--profile 2025`, a 2025 target (14363) for the upgrade case. All three share a `backups` volume, so
a backup taken on the source when recording starts is restored on a target before each replay.

```
docker compose -f docker/compose.yml --profile 2025 up -d
docker exec daffyreplay-source-1 /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "DaffyReplay!Dev2026" -i /seed/seed.sql
```

(`scripts/record-sample.ps1` reseeds the source itself.)

## How it works

```
recording files ─► RecordingIndex ─► SortedIndex ─► Scheduler ─► SessionPlayer (one per session)
 (NDJSON)          one streaming      start order;   recorded /    load event by offset
                   pass: sessions,    spills to      fast /        build request (raw, or rebuilt)
                   stats, entries     disk if big    serial        patch descriptor and handles
                                                                   ReplayConnection: send, walk response
                                                                   compare with the recording
                                                                          │
                                         report.json, report.html ◄─ ReportBuilder ◄─ results
```

- **Index pass** (`Recording/`). One read of every file: each request's file, offset, session, seq
  and start time (about 40 bytes), the session table, and the statistics `inspect` prints. Events are
  written when a request completes but stamped with its start, so file order is not start order; the
  entries are sorted by start time, in chunks spilled to disk past four million.
- **Scheduling** (`Replay/Scheduler.cs`). `recorded` pace runs one dispatcher on a dedicated thread
  that sleeps coarsely and spins for the last 20 ms (Windows' timer ticks every 15.6 ms), feeding a
  queue per session; `fast` feeds the queues without waiting; `serial` hands each request to its
  session and waits for it, up to `--serial-wait`.
- **The TDS client** (`Tds/`, `Target/`). Login, PRELOGIN, TLS (7.4 and 8.0) and redirects are DaffyTee's
  `UpstreamConnector`, copied. New here: `TdsResponseStream` and `ResponseWalker`, which walk a
  response of any size to its end without holding it - every token, every row, value by value -
  and `ReplayConnection`, which sends a request, walks its response, and keeps the transaction
  descriptor and current database.
- **Patching** (`Replay/RequestPatcher.cs`). The transaction descriptor in ALL_HEADERS, and the
  prepared-statement and cursor handles RPCs name, mapped recorded to replay.
- **Comparison and report** (`Results/`). Outcome, row counts and result-set sizes against what
  DaffyTee decoded; timings per statement fingerprint, in reservoirs so memory stays bounded.
- **sqlsim** (`Interop/`). `export sqlsim` walks the same sorted index a replay does and writes each
  request as T-SQL in an Extended Events-shaped `sql_batch_completed` (procedure calls through
  `SqlText`, transaction-manager requests as `BEGIN`/`COMMIT TRANSACTION`), in files within sqlsim's
  limits. `import sqlsim` streams sqlsim's XML into `request.batch` events marked `importedFrom`;
  that marker, and nothing else, makes `Comparison` compare a batch's row total (all XEvents'
  `row_count` knows) and puts the "imported" box at the top of the report. A DaffyTee recording never
  carries it, so its comparison is untouched.

## Design notes

Found while building, each with a test that fails without the fix:

- **One TDS packet per TLS record.** SQL Server drops a connection whose TLS records split TDS
  packets. `ReplayConnection` writes each packet separately, as SqlClient does.
- **Bulk loads and column encryption.** SqlClient announces COLUMNENCRYPTION at login, and its bulk
  metadata then carries a CEK table (two zero bytes when no key is used). Under a login without the
  feature the server refuses the load (4816). DaffyReplay offers the feature by default and, per
  session, reads the recorded bulk metadata (`ResponseWalker.HasCekTable`) to log in the way that
  client did. (`BulkLoadTests`)
- **Cursor fetches carry no metadata.** A server cursor's fetch responses say "no new metadata"; the
  rows take the shape sent with the cursor's open. `HandleMap` keeps each replay cursor's metadata and
  the walker inherits it. DaffyTee does not do this yet: 0.3.1 stops at a fetch's first row and says
  why in `detailProblem`, where 0.3.0 read the rows as tokens. (`HandleMappingTests`)
- **A reset does not announce its database.** After a pooled-connection reset the session is back in
  its login database without an ENVCHANGE saying so; the connection follows, as SqlClient does.
- **Serial pace and locks.** Strictly one at a time deadlocks when one session waits on a lock that
  another releases only later in recorded order. Serial pace carries on after `--serial-wait` and
  counts the overlap.
- **MARS outstanding-request count.** Recorded MARS requests can say two requests are in flight;
  replayed one at a time they say one - the second field, after the descriptor, that a replay changes.
- **An INSERT BULK goes with its rows.** The server takes whatever follows an `INSERT BULK` to be the
  bulk data and refuses anything else (4022, "Bulk load data was expected but not sent"), failing an
  innocent request. A recording made with DaffyTee's default `--bulk meta` has the `INSERT BULK` but
  not the rows, so `RecordingIndex` holds each `INSERT BULK` back until the session's next request
  shows whether its rows will be replayed - and leaves the two out together when they will not be
  (`--bulk meta`, `--bulk off`, filtered, truncated). (`RecordingTests`, `BulkLoadTests`)
- **The settings are the tee's.** A DaffyTee started with `--tee off` says only "off" at
  `proxy.start`; what it recorded with arrives in the `proxy.control` event that switched it on.
- **Identities after a point-in-time restore.** SQL Server hands out identity values from a cached
  block, and a database recovered with `STOPAT` carries on after the block (1,500 where production
  was at 1,008), so replayed inserts get other ids and later statements naming them read other rows.
  Reseeding to each column's maximum (`scripts/reseed-identities.sql`, also in NUGET.md) fixed every
  row-count difference in `verify-window.ps1`.

A DaffyTee 0.3.0 bug surfaced too: `TypeInfoReader` multiplied a `datetime` value's three-hundredths
of a second by 100,000 as a `uint`, which overflows for any time after 00:02:23, so events and
`--sql-log` showed such `datetime` parameters wrong. Exact replay is unaffected (it sends the raw
bytes). DaffyTee 0.3.1 fixed it by rounding to the millisecond, as SQL Server shows the value: a
`datetime` literal takes no more than three decimal places (error 241), so a tick-exact `.1233333`
could not be replayed as T-SQL. DaffyReplay 0.1.1 decodes the same way (`SqlTextTests`). For
recordings from 0.3.0 or earlier, `inspect` warns about procedure calls without raw bytes that hold a
`datetime` with a time of day, and `--approximate` skips them rather than send the wrong time
(`RecordingTests`).

## Testing

```
dotnet test tests/DaffyReplay.Tests                                    # unit tests, no network
$env:DAFFYREPLAY_TEST_SERVER = "127.0.0.1,14362"; dotnet test tests/DaffyReplay.Tests   # and against the target
scripts/verify-replay.ps1        # end to end: record, inspect, safety rails, every pace, fidelity, pacing, mutations
scripts/verify-window.ps1        # a peak window: tee switched on and off at DaffyTee's prompt, point-in-time restore
scripts/verify-linux.ps1         # build and replay in the .NET SDK container on Linux
scripts/verify-sqlsim.ps1        # export to and import from sqlsim, against sqlsim itself
```

- **Unit tests** need nothing but the committed recording, `samples/workload.ndjson`: reading,
  indexing and ordering, spill to disk, filters, fingerprints, report maths, rebuilt requests,
  patching, approximate T-SQL.
- **Integration tests** (`[SqlFact]`, skipped without `DAFFYREPLAY_TEST_SERVER`) drive
  `ReplayConnection` against the target: 300,000-row results, every type, errors, attention and its
  acknowledgement, resets, rebuilt SqlTransaction requests, bulk loads under both logins, prepared and
  cursor handles mapped when the replay is given other numbers - each with a mutation check that the
  test fails without the mechanism it tests.
- **`scripts/record-sample.ps1`** records a workload as a user would: the source reseeded and backed
  up, the installed `daffytee` started with `--raw --bulk full`, then `probes/ReplayProbe` (ADO.NET,
  EF Core, ODBC, concurrent load) and `probes/jdbc` (JDBC, in a Java container) through it, and the
  recording checked for gaps, truncation and missing raw bytes. It re-records the committed sample:
  `-Out samples/workload.ndjson -Name sample -Load 3 -Workers 3`.
- **`scripts/verify-replay.ps1`** asserts on everything a run produced: serial and recorded pace
  reproduce the recording (no new errors, no row differences, nothing left out); fast pace replays
  everything and any new error is the data-state kind; a replay through a second DaffyTee matches the
  recording byte for byte apart from descriptors (`ReplayProbe --compare`); a synthetic recording
  with known gaps keeps time within 2 ms at p99; with descriptor patching switched off
  (`--debug-no-descriptors`) the same replay fails; and, when the 2025 container is up, the 2022
  recording replays on 2025 over TDS 8.0.
- **`scripts/verify-window.ps1`** records the way NUGET.md's peak-window section says to: DaffyTee
  started with `--tee off --bulk meta` in front of a running pooled load, `:tee on` typed at its
  prompt, a bulk copy from a new connection, `:tee off`; then a log backup, a restore with `STOPAT`
  the `tee on` event's time, the identities reseeded. `inspect` must see the sessions already open,
  the switches, the settings the tee came on with, and the bulk loads left out with their
  `INSERT BULK`; serial and recorded pace must replay everything with no failed session, no 4022
  and at most a few new errors or row differences at the window's edge.
- **`scripts/verify-sqlsim.ps1`** captures one workload two ways at once: through DaffyTee
  (`record-sample.ps1`) and with sqlsim's own Extended Events session, converted as its
  `convert-xel.ps1` does. The export must replay in sqlsim (1.0.0, downloaded into `work/sqlsim`
  unless `-Sqlsim` names one) with every table but the bulk-loaded one ending as the source did, and
  nothing failing that did not fail when recorded, bar temp tables sqlsim's unreset connections keep.
  The import must replay whole in DaffyReplay, with row totals agreeing for 95 % of batches, new
  errors only of the kinds Extended Events records as OK, and the report saying it was imported.

## Layout

```
src/DaffyReplay/
  Program.cs  AppPaths.cs  Log.cs  LogFile.cs
  Tds/          from DaffyTee: TdsConstants, TdsCursor, TdsWriter, PacketWriter, PacketFramer,
                TdsMessageReader, PreLogin, PreLoginTlsStream, Login7, LoginResponse, AllHeaders,
                TransactionRequest, TypeInfoReader, RequestParser (+ argument offsets)
                new: TdsResponseStream, ResponseWalker
  Target/       from DaffyTee: TargetConnector (UpstreamConnector), TargetEndpoint, EntraTokenSource, SocketTuning
  Recording/    RecordedEvent, RecordingFiles, RecordingIndex, SortedIndex, Inspector
  Replay/       ReplayConnection, SessionPlayer, Scheduler, RequestPatcher, RequestBuilder, SqlText, ReplayContext
  Results/      ResultRecord, Report, ReportBuilder, ReportWriter, HtmlReport, Fingerprint
  Security/     from DaffyTee: SecretProtector; new: TargetStore
  Cli/          from DaffyTee: Prompter, TextTable; new: Arguments, TargetCommands, RunCommand, CompareCommand,
                SqlsimCommands
  Interop/      Sqlsim, SqlsimExport, SqlsimImport: to and from sqlsim's replay XML
  IO/           from DaffyTee: RollingFileWriter
tests/DaffyReplay.Tests/   xUnit
probes/ReplayProbe/        the .NET workload (ADO.NET, EF Core, ODBC, load), --compare, --synthetic
probes/jdbc/               the JDBC workload
scripts/                   record-sample, verify-replay, verify-window, verify-linux, verify-sqlsim, common;
                           reseed-identities.sql
docker/                    compose.yml, seed.sql
samples/workload.ndjson    the committed recording
docs/                      example reports
```

## Relation to DaffyTee

DaffyReplay reads DaffyTee's published event schema (`data.v` = 1) and nothing else. The TDS client
half was copied out of DaffyTee 0.3.0 and re-namespaced, as DaffyTee's came from DaffyDB. Three
DaffyTee additions would make replays more faithful, none needed:
recording each client's LOGIN7 (option flags, packet size, language, features - column encryption
among them), keeping output parameters from responses too large to decode (a prepared handle comes
back at the end), and recording which reset flag a request carried.

## Licence

MIT License, from version 0.2.1 (0.2.0 and earlier were published under the Business Source License
1.1); see [LICENSE.txt](LICENSE.txt) and
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

Copyright (c) 2026 Scott Traube and TroBeeOne LLC. Use it, change it and pass it on as you like; the
license asks that this copyright notice and the license text go with every copy, or substantial
part, of DaffyReplay you pass on.

## What has been tested

Recordings made through DaffyTee 0.3.0 and 0.3.1 from Microsoft.Data.SqlClient (ADO.NET), Entity
Framework Core 10, the Microsoft ODBC Driver 18 and the Microsoft JDBC Driver 13.6 replay into a
restored copy on SQL Server 2022 with no new errors and no row-count differences at serial and
recorded pace - ad-hoc and parameterised SQL, procedures with OUTPUT and RETURN, table-valued
parameters, nearly every type including MAX values, SqlTransaction with savepoints, ODBC and JDBC
manual-commit transactions, prepared statements and JDBC server cursors, pooled resets, bulk copy,
MARS, temp tables, USE, multiple and large result sets, errors, cancels and a separately mapped
login - and match their recording byte for byte apart from the transaction descriptor. The same
recording replays on SQL Server 2025 over TDS 8.0; recorded pace keeps time within 0.2 ms at p99
(requests start within 1 ms) on Windows and Linux; procedure calls without raw bytes replay
approximately with no new errors, a `datetime` at 13:14:15.123 among their parameters. A peak
window - the tee switched on and off at DaffyTee's prompt in front of a running pooled workload,
bulk loads recorded without their rows - replays after a point-in-time restore with no failed
sessions, and in two runs at most one new error and three row-count differences out of about 15,000
requests. A recording has also been replayed into Azure SQL Database, logging in with SQL
authentication and following its redirect. With sqlsim 1.0.0, the same workload captured by
DaffyTee and by sqlsim's Extended Events session at once: exported, it replays in sqlsim with every
table but the bulk-loaded one ending as the source did; imported, sqlsim's capture replays whole in
DaffyReplay, its new errors only those Extended Events records as OK. Entra ID targets, and SQL Server versions before 2022,
are not yet tested.
