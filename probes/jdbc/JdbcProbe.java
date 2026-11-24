import java.sql.*;
import java.util.*;

/**
 * The JDBC part of DaffyReplay's workload: what a Java application sends through the Microsoft JDBC
 * driver, which speaks TDS its own way - prepared statements as sp_prepexec then sp_execute, batches,
 * {call} with OUTPUT parameters, manual-commit transactions, and server cursors (selectMethod=cursor:
 * sp_cursorprepexec, sp_cursorfetch, sp_cursorclose), whose handles a replay has to map.
 *
 *   java -cp mssql-jdbc.jar JdbcProbe.java HOST PORT PASSWORD [LOGIN]
 *
 * Every scenario checks what came back, as ReplayProbe's do.
 */
public class JdbcProbe {
    static int passed, failed;
    static String host, password, login = "app";
    static int port;

    interface Scenario { void run() throws Exception; }

    public static void main(String[] args) throws Exception {
        host = args[0];
        port = Integer.parseInt(args[1]);
        password = args[2];
        if (args.length > 3) login = args[3];

        check("jdbc-statement", JdbcProbe::statement);
        check("jdbc-prepared", JdbcProbe::prepared);
        check("jdbc-batch", JdbcProbe::batch);
        check("jdbc-callable-output", JdbcProbe::callable);
        check("jdbc-transaction", JdbcProbe::transaction);
        check("jdbc-cursor", JdbcProbe::cursor);
        check("jdbc-errors", JdbcProbe::errors);

        System.out.println();
        System.out.println(passed + " passed, " + failed + " failed");
        System.exit(failed == 0 ? 0 : 1);
    }

    static void check(String name, Scenario scenario) {
        long start = System.nanoTime();
        try {
            scenario.run();
            passed++;
            System.out.printf("ok    %-32s %6d ms%n", name, (System.nanoTime() - start) / 1_000_000);
        } catch (Throwable ex) {
            failed++;
            System.out.printf("FAIL  %-32s %6d ms  %s%n", name, (System.nanoTime() - start) / 1_000_000, ex);
        }
    }

    static void expect(boolean ok, String what) {
        if (!ok) throw new AssertionError(what);
    }

    static Connection open(String extra) throws SQLException {
        String url = "jdbc:sqlserver://" + host + ":" + port + ";databaseName=ReplayTest;encrypt=true;trustServerCertificate=true;"
                   + "applicationName=ReplayProbe.Jdbc;workstationID=DEV01;" + extra;
        return DriverManager.getConnection(url, login, password);
    }

    static void statement() throws SQLException {
        try (Connection c = open(""); Statement s = c.createStatement();
             ResultSet r = s.executeQuery("SELECT TOP (10) Id, Name FROM dbo.Customers ORDER BY Id")) {
            int rows = 0;
            while (r.next()) rows++;
            expect(rows == 10, "rows " + rows);
        }
    }

    static void prepared() throws SQLException {
        try (Connection c = open("");
             PreparedStatement p = c.prepareStatement("SELECT Name, Balance FROM dbo.Customers WHERE Id = ?")) {
            for (int id = 20; id < 26; id++) {
                p.setInt(1, id);
                try (ResultSet r = p.executeQuery()) {
                    expect(r.next(), "customer " + id);
                }
            }
            try (PreparedStatement u = c.prepareStatement("UPDATE dbo.Products SET Stock = Stock + ? WHERE Id = ?")) {
                for (int id = 30; id < 33; id++) {
                    u.setInt(1, 1);
                    u.setInt(2, id);
                    expect(u.executeUpdate() == 1, "update " + id);
                }
            }
        }
    }

    static void batch() throws SQLException {
        try (Connection c = open("");
             PreparedStatement p = c.prepareStatement("INSERT dbo.AuditLog (What) VALUES (?)")) {
            for (int i = 0; i < 5; i++) {
                p.setString(1, "jdbc batch " + i);
                p.addBatch();
            }
            int[] counts = p.executeBatch();
            expect(counts.length == 5, "batch of " + counts.length);
        }
    }

    static void callable() throws SQLException {
        try (Connection c = open("");
             CallableStatement call = c.prepareCall("{? = call dbo.GetCustomer(?, ?)}")) {
            call.registerOutParameter(1, Types.INTEGER);
            call.setInt(2, 2);
            call.registerOutParameter(3, Types.NVARCHAR);
            try (ResultSet r = call.executeQuery()) { while (r.next()) { } }
            expect("Grace Hopper".equals(call.getString(3)), "output " + call.getString(3));
            expect(call.getInt(1) == 0, "return " + call.getInt(1));
        }
    }

    static void transaction() throws SQLException {
        try (Connection c = open("")) {
            c.setAutoCommit(false);
            try (PreparedStatement p = c.prepareStatement("INSERT dbo.AuditLog (What) VALUES (?)")) {
                p.setString(1, "jdbc committed");
                p.executeUpdate();
                c.commit();
                p.setString(1, "jdbc rolled back");
                p.executeUpdate();
                c.rollback();
            }
            c.setAutoCommit(true);
        }
    }

    /** selectMethod=cursor: a server cursor, fetched a few rows at a time. */
    static void cursor() throws SQLException {
        try (Connection c = open("selectMethod=cursor;");
             PreparedStatement p = c.prepareStatement("SELECT Id, Name FROM dbo.Customers WHERE Region = ? ORDER BY Id")) {
            p.setFetchSize(10);
            for (String region : new String[] { "EU", "AP" }) {
                p.setString(1, region);
                int rows = 0;
                try (ResultSet r = p.executeQuery()) { while (r.next()) rows++; }
                expect(rows >= 50, region + " rows " + rows);
            }
        }
    }

    static void errors() throws SQLException {
        try (Connection c = open(""); Statement s = c.createStatement()) {
            try {
                // The driver raises it when the result is read, not when the statement is sent.
                try (ResultSet r = s.executeQuery("SELECT 1/0")) { while (r.next()) { } }
                throw new AssertionError("no error");
            } catch (SQLException ex) {
                expect(ex.getErrorCode() == 8134, "error " + ex.getErrorCode());
            }
            try (ResultSet r = s.executeQuery("SELECT 1")) { expect(r.next(), "after the error"); }
        }
    }
}
