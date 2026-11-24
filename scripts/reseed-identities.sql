-- After a point-in-time restore (RESTORE LOG ... WITH STOPAT), and before a replay: put each identity
-- back to the highest value in its column. SQL Server hands out identity values from a cached block,
-- and a database recovered to a point in time carries on after that block, so without this the
-- replay's inserts get other ids than production gave them - and every later statement that names
-- one of those ids reads a different row. Run it in each restored database. Ascending identities
-- only; empty tables are left as they are.

DECLARE @table nvarchar(400), @column nvarchar(300), @sql nvarchar(max);

DECLARE identities CURSOR LOCAL FAST_FORWARD FOR
    SELECT QUOTENAME(SCHEMA_NAME(t.schema_id)) + N'.' + QUOTENAME(t.name), QUOTENAME(c.name)
    FROM sys.identity_columns AS c
    JOIN sys.tables AS t ON t.object_id = c.object_id
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
