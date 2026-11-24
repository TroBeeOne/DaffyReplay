using System.Data;
using System.Data.Odbc;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.Server;
using Microsoft.EntityFrameworkCore;

namespace ReplayProbe;

/// <summary>
/// The workload DaffyReplay's recordings are made of: what three kinds of application send to SQL
/// Server, through a DaffyTee that records it.
///
///   ADO.NET  ad-hoc and parameterised SQL, stored procedures with OUTPUT and RETURN, table-valued
///            parameters, SqlTransaction with savepoints, prepared statements, pooled-connection
///            resets, bulk copy, MARS, temp tables, errors, USE, a cancel, large results
///   EF Core  LINQ queries, SaveChanges batches with identity propagation, explicit transactions
///            (with EF's own savepoints), raw SQL, ExecuteUpdate
///   ODBC     the Microsoft ODBC driver: direct and parameterised SQL, prepared statements,
///            {call} with an OUTPUT parameter, manual-commit transactions
///   load     concurrent sessions placing orders and reading, for a recording with overlap
///
///   ReplayProbe --server HOST,PORT --password PW [--only NAME] [--load SECONDS] [--workers N]
///               [--odbc-driver "ODBC Driver 18 for SQL Server"] [--direct]
///   ReplayProbe --compare RECORDING.ndjson REPLAY-CAPTURE.ndjson
///   ReplayProbe --synthetic OUT.ndjson [REQUESTS] [SESSIONS]   a recording with known gaps, for pacing
///
/// Through DaffyTee the logins are DaffyTee's: app (mapped to svc_app), orders (app_user) and
/// reports (report_user), all with --password. --direct connects straight to the SQL Server with
/// the seed's own logins instead, to develop the workload without a DaffyTee.
///
/// Every scenario checks what came back, so a recording is known to hold what was meant.
/// </summary>
internal static class Program
{
    private static int _passed, _failed;

    /// <summary>Marks this run's rows, so the checks hold however many times the probe has run before.</summary>
    private static readonly string Tag = Guid.NewGuid().ToString("N")[..8];
    private static string _server = "127.0.0.1,14370", _password = "probe-password", _odbcDriver = "ODBC Driver 18 for SQL Server";
    private static bool _direct;

    private static async Task<int> Main(string[] args)
    {
        string? only = null;
        int load = 0, workers = 4;
        if (args.Length == 3 && args[0] == "--compare") return Compare.Run(args[1], args[2]);
        if (args.Length >= 2 && args[0] == "--synthetic")
            return Synthetic.Run(args[1], args.Length > 2 ? int.Parse(args[2]) : 2000, args.Length > 3 ? int.Parse(args[3]) : 8);

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--server": _server = args[++i]; break;
                case "--password": _password = args[++i]; break;
                case "--only": only = args[++i]; break;
                case "--load": load = int.Parse(args[++i]); break;
                case "--workers": workers = int.Parse(args[++i]); break;
                case "--odbc-driver": _odbcDriver = args[++i]; break;
                case "--direct": _direct = true; break;
                default: Console.Error.WriteLine($"unknown argument {args[i]}"); return 1;
            }
        }

        var scenarios = new (string Name, Func<Task> Run)[]
        {
            ("ado-adhoc", AdoAdHoc),
            ("ado-parameters", AdoParameters),
            ("ado-proc-output", AdoProcOutput),
            ("ado-tvp", AdoTableValued),
            ("ado-transactions", AdoTransactions),
            ("ado-prepared", AdoPrepared),
            ("ado-pooling", AdoPooling),
            ("ado-bulk-copy", AdoBulkCopy),
            ("ado-mars", AdoMars),
            ("ado-temp-tables", AdoTempTables),
            ("ado-errors", AdoErrors),
            ("ado-use-database", AdoUseDatabase),
            ("ado-large-result", AdoLargeResult),
            ("ado-multiple-results", AdoMultipleResults),
            ("ado-set-options", AdoSetOptions),
            ("ado-cancel", AdoCancel),
            ("ado-report-login", AdoReportLogin),
            ("ef-queries", EfQueries),
            ("ef-save-changes", EfSaveChanges),
            ("ef-transaction", EfTransaction),
            ("ef-raw-sql", EfRawSql),
            ("odbc-direct", OdbcDirect),
            ("odbc-parameters", OdbcParameters),
            ("odbc-prepared", OdbcPrepared),
            ("odbc-call", OdbcCall),
            ("odbc-transaction", OdbcTransaction),
        };

        foreach (var (name, run) in scenarios)
        {
            if (only is not null && !name.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
            await Check(name, run);
        }

        if (load > 0 && (only is null || "load".Contains(only, StringComparison.OrdinalIgnoreCase)))
            await Check($"load ({workers} workers, {load}s)", () => Load(load, workers));

        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static async Task Check(string name, Func<Task> run)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await run();
            _passed++;
            Console.WriteLine($"ok    {name,-32} {stopwatch.ElapsedMilliseconds,6} ms");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"FAIL  {name,-32} {stopwatch.ElapsedMilliseconds,6} ms  {ex.GetType().Name}: {ex.Message.ReplaceLineEndings(" ")}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what}: expected <{expected}>, got <{actual}>");
    }

    // ---- connections ---------------------------------------------------------------------------

    /// <summary>A DaffyTee login and its SQL Server equivalent, for --direct.</summary>
    private static (string User, string Password) Login(string daffyTeeLogin) => _direct
        ? daffyTeeLogin switch
        {
            "orders" => ("app_user", "App!User2026"),
            "reports" => ("report_user", "Report!User2026"),
            _ => ("svc_app", "Svc!App2026"),
        }
        : (daffyTeeLogin, _password);

    private static string Ado(string app, string login = "app", bool pooling = false, bool mars = false, string database = "ReplayTest")
    {
        var (user, password) = Login(login);
        return $"Server=tcp:{_server};User Id={user};Password={password};Initial Catalog={database};" +
               $"Encrypt=Mandatory;TrustServerCertificate=True;Application Name={app};Workstation ID=DEV01;" +
               $"Pooling={pooling};MultipleActiveResultSets={mars};Connect Timeout=30";
    }

    private static string Odbc(string login = "app")
    {
        var (user, password) = Login(login);
        return $"Driver={{{_odbcDriver}}};Server=tcp:{_server};UID={user};PWD={password};Database=ReplayTest;" +
               "Encrypt=yes;TrustServerCertificate=yes;APP=ReplayProbe.Odbc;WSID=DEV01";
    }

    private static async Task<SqlConnection> OpenAsync(string connectionString)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<object?> ScalarAsync(SqlConnection connection, string sql, SqlTransaction? transaction = null)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    private static async Task<int> RowsAsync(SqlCommand command)
    {
        int rows = 0;
        await using var reader = await command.ExecuteReaderAsync();
        do { while (await reader.ReadAsync()) rows++; } while (await reader.NextResultAsync());
        return rows;
    }

    // ---- ADO.NET -------------------------------------------------------------------------------

    private const string AdoApp = "ReplayProbe.Ado";

    private static async Task AdoAdHoc()
    {
        await using var connection = await OpenAsync(Ado(AdoApp));
        Equal(4, (int)(await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.Customers WHERE Id IN (1, 2, 3, 4)"))!, "count");
        Equal("Grace Hopper", (string)(await ScalarAsync(connection, "SELECT Name FROM dbo.Customers WHERE Id = 2"))!, "name");

        await using var insert = new SqlCommand("INSERT dbo.AuditLog (What) VALUES (N'ad hoc from ReplayProbe')", connection);
        Equal(1, await insert.ExecuteNonQueryAsync(), "insert");

        await using var update = new SqlCommand("UPDATE dbo.Customers SET Balance = Balance + 1 WHERE Region = 'EU' AND Id <= 40", connection);
        Equal(10, await update.ExecuteNonQueryAsync(), "update");
    }

    private static async Task AdoParameters()
    {
        await using var connection = await OpenAsync(Ado(AdoApp));

        await using var select = new SqlCommand("SELECT Name, Email FROM dbo.Customers WHERE Id = @id AND Region = @region", connection);
        select.Parameters.Add("@id", SqlDbType.Int).Value = 5;
        select.Parameters.Add("@region", SqlDbType.Char, 2).Value = "EU";
        Equal(1, await RowsAsync(select), "rows");

        await using var insert = new SqlCommand("""
            INSERT dbo.TypeZoo (cTiny, cSmall, cInt, cBig, cBit, cDec, cNum, cMoney, cSmallMoney, cReal, cFloat, cDate, cTime,
                                cDt, cSdt, cDt2, cDto, cChar, cVarchar, cNchar, cNvarchar, cVarcharMax, cNvarcharMax,
                                cBinary, cVarbinary, cVarbinaryMax, cGuid, cXml, cVariant)
            OUTPUT INSERTED.Id
            VALUES (@tiny, @small, @int, @big, @bit, @dec, @num, @money, @smallmoney, @real, @float, @date, @time,
                    @dt, @sdt, @dt2, @dto, @char, @varchar, @nchar, @nvarchar, @varcharmax, @nvarcharmax,
                    @binary, @varbinary, @varbinarymax, @guid, @xml, @variant)
            """, connection);
        var p = insert.Parameters;
        p.Add("@tiny", SqlDbType.TinyInt).Value = (byte)7;
        p.Add("@small", SqlDbType.SmallInt).Value = (short)-300;
        p.Add("@int", SqlDbType.Int).Value = 123456;
        p.Add("@big", SqlDbType.BigInt).Value = 9_000_000_000L;
        p.Add("@bit", SqlDbType.Bit).Value = true;
        p.Add(new SqlParameter("@dec", SqlDbType.Decimal) { Precision = 38, Scale = 10, Value = 1234567890.0123456789m });
        p.Add(new SqlParameter("@num", SqlDbType.Decimal) { Precision = 9, Scale = 3, Value = -42.125m });
        p.Add("@money", SqlDbType.Money).Value = 19.99m;
        p.Add("@smallmoney", SqlDbType.SmallMoney).Value = 2.5m;
        p.Add("@real", SqlDbType.Real).Value = 3.25f;
        p.Add("@float", SqlDbType.Float).Value = Math.PI;
        p.Add("@date", SqlDbType.Date).Value = new DateTime(2026, 10, 10);
        p.Add(new SqlParameter("@time", SqlDbType.Time) { Scale = 7, Value = new TimeSpan(0, 13, 14, 15, 123) });
        // In the afternoon, to the millisecond: DaffyTee 0.3.0 decoded datetimes after 00:02:23 wrong, and
        // a literal of more than three decimal places is refused, which --approximate would run into.
        p.Add("@dt", SqlDbType.DateTime).Value = new DateTime(2026, 10, 10, 13, 14, 15, 123);
        p.Add("@sdt", SqlDbType.SmallDateTime).Value = new DateTime(2026, 10, 10, 12, 0, 0);
        p.Add(new SqlParameter("@dt2", SqlDbType.DateTime2) { Scale = 7, Value = new DateTime(2026, 10, 10, 12, 0, 0).AddTicks(1234567) });
        p.Add(new SqlParameter("@dto", SqlDbType.DateTimeOffset) { Scale = 7, Value = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.FromHours(2)) });
        p.Add("@char", SqlDbType.Char, 10).Value = "abc";
        p.Add("@varchar", SqlDbType.VarChar, 100).Value = "café";
        p.Add("@nchar", SqlDbType.NChar, 10).Value = "ü";
        p.Add("@nvarchar", SqlDbType.NVarChar, 100).Value = "héllo 世界";
        p.Add("@varcharmax", SqlDbType.VarChar, -1).Value = new string('v', 20000);
        p.Add("@nvarcharmax", SqlDbType.NVarChar, -1).Value = new string('é', 9000);
        p.Add("@binary", SqlDbType.Binary, 8).Value = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        p.Add("@varbinary", SqlDbType.VarBinary, 100).Value = new byte[] { 0xCA, 0xFE };
        p.Add("@varbinarymax", SqlDbType.VarBinary, -1).Value = Enumerable.Range(0, 30000).Select(i => (byte)i).ToArray();
        p.Add("@guid", SqlDbType.UniqueIdentifier).Value = new Guid("6f9619ff-8b86-d011-b42d-00c04fc964ff");
        p.Add("@xml", SqlDbType.Xml).Value = "<order id=\"1\"><line>1</line></order>";
        p.Add("@variant", SqlDbType.Variant).Value = 42.5m;
        int id = (int)(await insert.ExecuteScalarAsync())!;

        await using var back = new SqlCommand("SELECT cNvarchar, LEN(cVarcharMax), DATALENGTH(cVarbinaryMax) FROM dbo.TypeZoo WHERE Id = @id", connection);
        back.Parameters.Add("@id", SqlDbType.Int).Value = id;
        await using var reader = await back.ExecuteReaderAsync();
        Assert(await reader.ReadAsync(), "the row came back");
        Equal("héllo 世界", reader.GetString(0), "nvarchar");
        Equal(20000L, Convert.ToInt64(reader.GetValue(1)), "varchar(max)");
        Equal(30000L, Convert.ToInt64(reader.GetValue(2)), "varbinary(max)");
    }

    private static async Task AdoProcOutput()
    {
        await using var connection = await OpenAsync(Ado(AdoApp));
        for (int id = 1; id <= 3; id++)
        {
            await using var command = new SqlCommand("dbo.GetCustomer", connection) { CommandType = CommandType.StoredProcedure };
            command.Parameters.Add("@Id", SqlDbType.Int).Value = id;
            var name = command.Parameters.Add("@Name", SqlDbType.NVarChar, 100);
            name.Direction = ParameterDirection.Output;
            var ret = command.Parameters.Add("@ret", SqlDbType.Int);
            ret.Direction = ParameterDirection.ReturnValue;
            Equal(1, await RowsAsync(command), "rows");
            Equal(0, (int)ret.Value, "return");
            if (id == 2) Equal("Grace Hopper", (string)name.Value, "output");
        }
    }

    private static async Task AdoTableValued()
    {
        await using var connection = await OpenAsync(Ado(AdoApp));

        var ids = new DataTable();
        ids.Columns.Add("Id", typeof(int));
        foreach (int id in new[] { 1, 3, 7, 11 }) ids.Rows.Add(id);
        await using (var command = new SqlCommand("dbo.GetCustomersByIds", connection) { CommandType = CommandType.StoredProcedure })
        {
            command.Parameters.Add(new SqlParameter("@Ids", SqlDbType.Structured) { TypeName = "dbo.IdList", Value = ids });
            Equal(4, await RowsAsync(command), "rows");
        }

        // An order, and then the order by the id the server gave it: the server-generated value
        // every replay has to live with.
        var meta = new[] { new SqlMetaData("ProductId", SqlDbType.Int), new SqlMetaData("Qty", SqlDbType.Int) };
        var lines = new[] { (3, 2), (8, 1), (21, 5) }.Select(l =>
        {
            var record = new SqlDataRecord(meta);
            record.SetInt32(0, l.Item1);
            record.SetInt32(1, l.Item2);
            return record;
        }).ToList();

        int orderId;
        await using (var place = new SqlCommand("dbo.PlaceOrder", connection) { CommandType = CommandType.StoredProcedure })
        {
            place.Parameters.Add("@CustomerId", SqlDbType.Int).Value = 9;
            place.Parameters.Add(new SqlParameter("@Lines", SqlDbType.Structured) { TypeName = "dbo.OrderLineList", Value = lines });
            place.Parameters.Add("@Note", SqlDbType.NVarChar, 400).Value = "probe order";
            var output = place.Parameters.Add("@OrderId", SqlDbType.Int);
            output.Direction = ParameterDirection.Output;
            await place.ExecuteNonQueryAsync();
            orderId = (int)output.Value;
        }

        await using var check = new SqlCommand("SELECT COUNT(*) FROM dbo.OrderLines WHERE OrderId = @id", connection);
        check.Parameters.Add("@id", SqlDbType.Int).Value = orderId;
        Equal(3, (int)(await check.ExecuteScalarAsync())!, "order lines");
    }

    private static async Task AdoTransactions()
    {
        await using var connection = await OpenAsync(Ado(AdoApp));

        await using (var commit = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted))
        {
            await ScalarAsync(connection, "INSERT dbo.AuditLog (What) VALUES (N'committed')", commit);
            await ScalarAsync(connection, "UPDATE dbo.Products SET Stock = Stock - 1 WHERE Id = 1", commit);
            await commit.CommitAsync();
        }

        await using (var rollback = (SqlTransaction)await connection.BeginTransactionAsync())
        {
            await ScalarAsync(connection, $"INSERT dbo.AuditLog (What) VALUES (N'rolled back {Tag}')", rollback);
            await rollback.RollbackAsync();
        }

        await using (var savepoint = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            await ScalarAsync(connection, $"INSERT dbo.AuditLog (What) VALUES (N'kept {Tag}')", savepoint);
            savepoint.Save("partial");
            await ScalarAsync(connection, $"INSERT dbo.AuditLog (What) VALUES (N'undone {Tag}')", savepoint);
            savepoint.Rollback("partial");
            await savepoint.CommitAsync();
        }

        Equal(1, (int)(await ScalarAsync(connection, $"SELECT COUNT(*) FROM dbo.AuditLog WHERE What = N'kept {Tag}'"))!, "kept");
        Equal(0, (int)(await ScalarAsync(connection, $"SELECT COUNT(*) FROM dbo.AuditLog WHERE What IN (N'undone {Tag}', N'rolled back {Tag}')"))!, "undone");
    }

    private static async Task AdoPrepared()
    {
        await using var connection = await OpenAsync(Ado(AdoApp));
        await using var command = new SqlCommand("SELECT Name, Balance FROM dbo.Customers WHERE Id = @id", connection);
        command.Parameters.Add("@id", SqlDbType.Int);
        await command.PrepareAsync();
        for (int id = 10; id < 16; id++)
        {
            command.Parameters["@id"].Value = id;
            Equal(1, await RowsAsync(command), $"customer {id}");
        }

        // A second prepared statement, so the handles differ from one another.
        await using var second = new SqlCommand("UPDATE dbo.Products SET Stock = Stock + @n WHERE Id = @id", connection);
        second.Parameters.Add("@n", SqlDbType.Int).Value = 1;
        second.Parameters.Add("@id", SqlDbType.Int).Value = 2;
        await second.PrepareAsync();
        Equal(1, await second.ExecuteNonQueryAsync(), "update");
        second.Parameters["@id"].Value = 3;
        Equal(1, await second.ExecuteNonQueryAsync(), "update");
    }

    private static async Task AdoPooling()
    {
        // The same pool, five times: every reuse sends its first request with the reset flag.
        string pooled = Ado(AdoApp, pooling: true) + ";Max Pool Size=1";
        for (int i = 0; i < 5; i++)
        {
            await using var connection = await OpenAsync(pooled);
            await ScalarAsync(connection, "CREATE TABLE #scratch (n int); INSERT #scratch VALUES (1);");
            Equal(1, (int)(await ScalarAsync(connection, "SELECT COUNT(*) FROM #scratch"))!, "temp table, fresh after the reset");
        }
        SqlConnection.ClearAllPools();
    }

    private static async Task AdoBulkCopy()
    {
        await using var connection = await OpenAsync(Ado(AdoApp));
        var table = new DataTable();
        table.Columns.Add("Id", typeof(int));
        table.Columns.Add("Label", typeof(string));
        table.Columns.Add("Amount", typeof(decimal));
        table.Columns.Add("Stamp", typeof(DateTime));
        for (int i = 1; i <= 2000; i++) table.Rows.Add(i, $"row {i}", i % 7 == 0 ? DBNull.Value : i * 1.25m, new DateTime(2026, 10, 10).AddSeconds(i));

        using var bulk = new SqlBulkCopy(connection) { DestinationTableName = "dbo.BulkTarget", BatchSize = 1000 };
        await bulk.WriteToServerAsync(table);
        Assert((int)(await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.BulkTarget"))! >= 2000, "bulk rows");
    }

    private static async Task AdoMars()
    {
        await using var connection = await OpenAsync(Ado(AdoApp, mars: true));
        await using var customers = new SqlCommand("SELECT Id FROM dbo.Customers WHERE Id <= 5 ORDER BY Id", connection);
        await using var reader = await customers.ExecuteReaderAsync();
        int count = 0;
        while (await reader.ReadAsync())
        {
            // A second command on the same connection while the first reader is open: MARS.
            await using var orders = new SqlCommand("SELECT COUNT(*) FROM dbo.Orders WHERE CustomerId = @id", connection);
            orders.Parameters.Add("@id", SqlDbType.Int).Value = reader.GetInt32(0);
            await orders.ExecuteScalarAsync();
            count++;
        }
        Equal(5, count, "customers");
    }

    private static async Task AdoTempTables()
    {
        await using var connection = await OpenAsync(Ado(AdoApp));
        await ScalarAsync(connection, "CREATE TABLE #work (Id int PRIMARY KEY, Total decimal(18,2))");
        await ScalarAsync(connection, "INSERT #work SELECT TOP (50) Id, Total FROM dbo.Orders ORDER BY Id");

        await using var command = new SqlCommand("SELECT SUM(Total) FROM #work WHERE Id > @from", connection);
        command.Parameters.Add("@from", SqlDbType.Int).Value = 10;
        Assert(await command.ExecuteScalarAsync() is decimal, "sum over the temp table");
        await ScalarAsync(connection, "DROP TABLE #work");
    }

    private static async Task AdoErrors()
    {
        await using var connection = await OpenAsync(Ado(AdoApp));

        Equal(8134, await ErrorNumber(connection, "SELECT 1/0"), "divide by zero");
        Equal(2627, await ErrorNumber(connection, "INSERT dbo.Products (Id, Sku, Name, Price, Stock) VALUES (1, 'SKU-0001', N'dup', 1, 1)"), "duplicate key");
        Equal(50000, await ErrorNumber(connection, "EXEC dbo.FailWith 50000, N'raised by the probe'"), "raiserror");
        Equal(208, await ErrorNumber(connection, "SELECT * FROM dbo.NoSuchTable"), "missing table");

        // Still usable after all of that.
        Equal(1, (int)(await ScalarAsync(connection, "SELECT 1"))!, "after errors");
    }

    private static async Task<int> ErrorNumber(SqlConnection connection, string sql)
    {
        try
        {
            await ScalarAsync(connection, sql);
            return 0;
        }
        catch (SqlException ex)
        {
            return ex.Number;
        }
    }

    private static async Task AdoUseDatabase()
    {
        await using var connection = await OpenAsync(Ado(AdoApp));
        await ScalarAsync(connection, "USE ReplayOther");
        Equal("replay-test", (string)(await ScalarAsync(connection, "SELECT Value FROM dbo.Settings WHERE Name = N'mode'"))!, "other database");
        connection.ChangeDatabase("ReplayTest");
        Equal("ReplayTest", (string)(await ScalarAsync(connection, "SELECT DB_NAME()"))!, "back");
    }

    private static async Task AdoLargeResult()
    {
        await using var connection = await OpenAsync(Ado(AdoApp));
        await using var command = new SqlCommand("SELECT N, CONCAT(N'row ', N) AS Label FROM dbo.Numbers(150000)", connection);
        Equal(150000, await RowsAsync(command), "rows");
    }

    private static async Task AdoMultipleResults()
    {
        await using var connection = await OpenAsync(Ado(AdoApp));
        await using var command = new SqlCommand("dbo.SalesReport", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add("@From", SqlDbType.Date).Value = new DateTime(2026, 1, 1);
        command.Parameters.Add("@To", SqlDbType.Date).Value = new DateTime(2026, 12, 31);
        Assert(await RowsAsync(command) >= 5, "two result sets");
    }

    private static async Task AdoSetOptions()
    {
        await using var connection = await OpenAsync(Ado(AdoApp));
        await ScalarAsync(connection, "SET ARITHABORT ON; SET LOCK_TIMEOUT 5000; SET DATEFORMAT dmy;");
        Equal(new DateTime(2026, 10, 3), (DateTime)(await ScalarAsync(connection, "SELECT CAST('03/10/2026' AS datetime)"))!, "dmy");
    }

    private static async Task AdoCancel()
    {
        await using var connection = await OpenAsync(Ado(AdoApp));
        await using var command = new SqlCommand("EXEC dbo.Pause 2500", connection) { CommandTimeout = 1 };
        try
        {
            await command.ExecuteNonQueryAsync();
            throw new Exception("the command should have timed out");
        }
        catch (SqlException ex) when (ex.Number == -2)
        {
            // The timeout: SqlClient sent an attention, which DaffyTee records as "cancelled".
        }
        Equal(1, (int)(await ScalarAsync(connection, "SELECT 1"))!, "after the cancel");
    }

    private static async Task AdoReportLogin()
    {
        await using var connection = await OpenAsync(Ado("ReplayProbe.Reports", login: "reports"));
        Equal("report_user", (string)(await ScalarAsync(connection, "SELECT SUSER_SNAME()"))!, "login");
        await using var command = new SqlCommand("dbo.SalesReport", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add("@From", SqlDbType.Date).Value = new DateTime(2026, 1, 1);
        command.Parameters.Add("@To", SqlDbType.Date).Value = new DateTime(2026, 6, 30);
        Assert(await RowsAsync(command) > 0, "report rows");
        Equal(229, await ErrorNumber(connection, "DELETE dbo.AuditLog WHERE Id < 0"), "a read-only login cannot delete");
    }

    // ---- EF Core -------------------------------------------------------------------------------

    private static ShopContext Ef() => new(Ado("ReplayProbe.EF", login: "orders"));

    private static async Task EfQueries()
    {
        await using var db = Ef();
        var european = await db.Customers.Where(c => c.Region == "EU" && c.Balance > 100).OrderBy(c => c.Name).Take(10).ToListAsync();
        Assert(european.Count > 0, "customers");

        int id = european[0].Id;
        var withOrders = await db.Customers.Include(c => c.Orders).ThenInclude(o => o.Lines).SingleAsync(c => c.Id == id);
        Assert(withOrders.Orders.Count >= 0, "include");

        Assert(await db.Products.AnyAsync(p => p.Stock > 0), "any");
        int count = await db.Orders.CountAsync(o => o.Status == "shipped");
        Assert(count > 0, "count");

        var top = await db.OrderLines.GroupBy(l => l.ProductId)
            .Select(g => new { ProductId = g.Key, Units = g.Sum(l => l.Qty) })
            .OrderByDescending(x => x.Units).Take(5).ToListAsync();
        Equal(5, top.Count, "group by");
    }

    private static async Task EfSaveChanges()
    {
        await using var db = Ef();
        var customer = new Customer { Name = "EF Customer", Email = "ef@example.com", Region = "AP", Balance = 10 };
        customer.Orders.Add(new Order { Status = "new", Note = "from EF", Lines = [new OrderLine { LineNumber = 1, ProductId = 4, Qty = 2, UnitPrice = 12.99m }] });
        customer.Orders.Add(new Order { Status = "new", Note = "second", Lines = [new OrderLine { LineNumber = 1, ProductId = 5, Qty = 1, UnitPrice = 15.99m }] });
        db.Customers.Add(customer);
        db.AuditLog.Add(new AuditEntry { What = "EF batch" });
        await db.SaveChangesAsync();
        Assert(customer.Id > 200 && customer.Orders.All(o => o.Id > 500), "identities came back");

        var product = await db.Products.SingleAsync(p => p.Id == 6);
        product.Stock -= 3;
        await db.SaveChangesAsync();   // UPDATE with the rowversion check
    }

    private static async Task EfTransaction()
    {
        await using var db = Ef();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.AuditLog.Add(new AuditEntry { What = "EF transaction, part 1" });
        await db.SaveChangesAsync();         // EF sets a savepoint of its own inside a transaction it did not start
        db.AuditLog.Add(new AuditEntry { What = "EF transaction, part 2" });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private static async Task EfRawSql()
    {
        await using var db = Ef();
        string region = "NA";
        var rows = await db.Customers.FromSqlInterpolated($"SELECT * FROM dbo.Customers WHERE Region = {region}").CountAsync();
        Assert(rows > 0, "FromSql");

        int touched = await db.Products.Where(p => p.Id >= 40).ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, p => p.Stock + 1));
        Equal(11, touched, "ExecuteUpdate");

        int inserted = await db.Database.ExecuteSqlInterpolatedAsync($"INSERT dbo.AuditLog (What) VALUES ({"raw from EF"})");
        Equal(1, inserted, "ExecuteSql");
    }

    // ---- ODBC ----------------------------------------------------------------------------------

    private static async Task<OdbcConnection> OdbcOpenAsync()
    {
        var connection = new OdbcConnection(Odbc());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task OdbcDirect()
    {
        await using var connection = await OdbcOpenAsync();
        await using var command = new OdbcCommand("SELECT TOP (25) Id, Name, Balance FROM dbo.Customers ORDER BY Id", connection);
        int rows = 0;
        await using (var reader = await command.ExecuteReaderAsync()) while (await reader.ReadAsync()) rows++;
        Equal(25, rows, "rows");
    }

    private static async Task OdbcParameters()
    {
        await using var connection = await OdbcOpenAsync();
        await using var command = new OdbcCommand("SELECT Name FROM dbo.Customers WHERE Id = ? AND Region = ?", connection);
        command.Parameters.Add("@id", OdbcType.Int).Value = 6;
        command.Parameters.Add("@region", OdbcType.Char, 2).Value = "AP";
        Equal("Customer 6", (string)(await command.ExecuteScalarAsync())!, "name");

        await using var insert = new OdbcCommand("INSERT dbo.AuditLog (What) VALUES (?)", connection);
        insert.Parameters.Add("@what", OdbcType.NVarChar, 400).Value = "from ODBC";
        Equal(1, await insert.ExecuteNonQueryAsync(), "insert");
    }

    private static async Task OdbcPrepared()
    {
        await using var connection = await OdbcOpenAsync();
        await using var command = new OdbcCommand("SELECT COUNT(*) FROM dbo.Orders WHERE CustomerId = ?", connection);
        command.Parameters.Add("@id", OdbcType.Int);
        command.Prepare();
        for (int id = 1; id <= 6; id++)
        {
            command.Parameters[0].Value = id;
            Assert(await command.ExecuteScalarAsync() is int, $"orders of {id}");
        }
    }

    private static async Task OdbcCall()
    {
        await using var connection = await OdbcOpenAsync();
        await using var command = new OdbcCommand("{? = call dbo.GetCustomer(?, ?)}", connection);
        command.Parameters.Add("@ret", OdbcType.Int).Direction = ParameterDirection.ReturnValue;
        command.Parameters.Add("@Id", OdbcType.Int).Value = 2;
        var name = command.Parameters.Add("@Name", OdbcType.NVarChar, 100);
        name.Direction = ParameterDirection.Output;
        await using (var reader = await command.ExecuteReaderAsync()) while (await reader.ReadAsync()) { }
        Equal("Grace Hopper", name.Value as string, "output");
        Equal(0, command.Parameters[0].Value as int?, "return");
    }

    private static async Task OdbcTransaction()
    {
        await using var connection = await OdbcOpenAsync();
        using (var transaction = connection.BeginTransaction())
        {
            await using var insert = new OdbcCommand($"INSERT dbo.AuditLog (What) VALUES ('odbc commit {Tag}')", connection, transaction);
            await insert.ExecuteNonQueryAsync();
            transaction.Commit();
        }
        using (var transaction = connection.BeginTransaction())
        {
            await using var insert = new OdbcCommand($"INSERT dbo.AuditLog (What) VALUES ('odbc rollback {Tag}')", connection, transaction);
            await insert.ExecuteNonQueryAsync();
            transaction.Rollback();
        }
        await using var check = new OdbcCommand($"SELECT COUNT(*) FROM dbo.AuditLog WHERE What LIKE 'odbc % {Tag}'", connection);
        Equal(1, (int)(await check.ExecuteScalarAsync())!, "committed only");
    }

    // ---- load ----------------------------------------------------------------------------------

    /// <summary>
    /// Several sessions at once for <paramref name="seconds"/>, each a little OLTP application on a
    /// connection pool: orders placed (a transaction, identities, TVPs), customers read, balances
    /// moved inside SqlTransactions, now and then a report. Seeded, so two runs send the same mix.
    /// </summary>
    private static async Task Load(int seconds, int workers)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        long requests = 0;
        var meta = new[] { new SqlMetaData("ProductId", SqlDbType.Int), new SqlMetaData("Qty", SqlDbType.Int) };

        async Task Worker(int number)
        {
            var random = new Random(1000 + number);
            string connectionString = Ado($"ReplayProbe.Load{number}", login: number % 3 == 2 ? "orders" : "app", pooling: true);

            while (DateTime.UtcNow < until)
            {
                await using var connection = await OpenAsync(connectionString);
                for (int i = 0; i < 10 && DateTime.UtcNow < until; i++)
                {
                    switch (random.Next(10))
                    {
                        case < 3:
                        {
                            await using var command = new SqlCommand("dbo.GetCustomer", connection) { CommandType = CommandType.StoredProcedure };
                            command.Parameters.Add("@Id", SqlDbType.Int).Value = random.Next(1, 201);
                            command.Parameters.Add("@Name", SqlDbType.NVarChar, 100).Direction = ParameterDirection.Output;
                            await RowsAsync(command);
                            break;
                        }

                        case < 5:
                        {
                            var lines = Enumerable.Range(0, random.Next(1, 4)).Select(_ => random.Next(1, 51)).Distinct().Select(p =>
                            {
                                var record = new SqlDataRecord(meta);
                                record.SetInt32(0, p);
                                record.SetInt32(1, random.Next(1, 4));
                                return record;
                            }).ToList();
                            await using var command = new SqlCommand("dbo.PlaceOrder", connection) { CommandType = CommandType.StoredProcedure };
                            command.Parameters.Add("@CustomerId", SqlDbType.Int).Value = random.Next(1, 201);
                            command.Parameters.Add(new SqlParameter("@Lines", SqlDbType.Structured) { TypeName = "dbo.OrderLineList", Value = lines });
                            var orderId = command.Parameters.Add("@OrderId", SqlDbType.Int);
                            orderId.Direction = ParameterDirection.Output;
                            await command.ExecuteNonQueryAsync();

                            await using var read = new SqlCommand("SELECT Total, Status FROM dbo.Orders WHERE Id = @id", connection);
                            read.Parameters.Add("@id", SqlDbType.Int).Value = orderId.Value;
                            await RowsAsync(read);
                            Interlocked.Increment(ref requests);
                            break;
                        }

                        case < 7:
                        {
                            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
                            int from = random.Next(1, 201), to = random.Next(1, 201);
                            await using var debit = new SqlCommand("UPDATE dbo.Customers SET Balance = Balance - @amount WHERE Id = @id", connection, transaction);
                            debit.Parameters.Add("@amount", SqlDbType.Decimal).Value = 1.25m;
                            debit.Parameters.Add("@id", SqlDbType.Int).Value = from;
                            await debit.ExecuteNonQueryAsync();
                            await using var credit = new SqlCommand("UPDATE dbo.Customers SET Balance = Balance + @amount WHERE Id = @id", connection, transaction);
                            credit.Parameters.Add("@amount", SqlDbType.Decimal).Value = 1.25m;
                            credit.Parameters.Add("@id", SqlDbType.Int).Value = to;
                            await credit.ExecuteNonQueryAsync();
                            await transaction.CommitAsync();
                            Interlocked.Increment(ref requests);
                            break;
                        }

                        case < 9:
                        {
                            await using var command = new SqlCommand(
                                $"SELECT TOP (20) o.Id, o.Total, c.Name FROM dbo.Orders o JOIN dbo.Customers c ON c.Id = o.CustomerId WHERE c.Region = '{(new[] { "NA", "EU", "AP", "SA" })[random.Next(4)]}' ORDER BY o.Id DESC",
                                connection);
                            await RowsAsync(command);
                            break;
                        }

                        default:
                        {
                            await using var command = new SqlCommand("dbo.SalesReport", connection) { CommandType = CommandType.StoredProcedure };
                            command.Parameters.Add("@From", SqlDbType.Date).Value = new DateTime(2026, 1, 1);
                            command.Parameters.Add("@To", SqlDbType.Date).Value = new DateTime(2026, 12, 31);
                            await RowsAsync(command);
                            break;
                        }
                    }

                    Interlocked.Increment(ref requests);
                    await Task.Delay(random.Next(0, 15));
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, workers).Select(Worker));
        SqlConnection.ClearAllPools();
        Assert(requests > workers * 5, $"only {requests} requests");
    }
}
