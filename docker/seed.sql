-- Seed for DaffyReplay's test SQL Servers. Idempotent: safe to run again.
--
--   docker exec daffyreplay-source-1 /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "DaffyReplay!Dev2026" -i /seed/seed.sql
--
-- The same script seeds the source ("production") and the targets, but a replay test never relies
-- on that: it backs the source up the moment recording starts and restores that backup on the
-- target, as a real user would.
--
-- Logins, standing in for the shapes a recording holds:
--   svc_app      a service account several applications share (db_owner)
--   app_user     an application's own login: read, write, execute
--   report_user  a reporting login: read only

SET NOCOUNT ON;

IF DB_ID('ReplayTest') IS NULL CREATE DATABASE ReplayTest;
GO
IF DB_ID('ReplayOther') IS NULL CREATE DATABASE ReplayOther;
GO

-- Fixed SIDs, the same on every server: a database backed up on one and restored on another then
-- finds its users' logins already there. Without them every user is orphaned after the restore -
-- the first thing anyone replaying into a freshly restored copy runs into. A login made earlier
-- with another SID is made again.
IF SUSER_SID('svc_app') <> 0x5D1A0E7F00000000000000000000A001 DROP LOGIN svc_app;
IF SUSER_SID('app_user') <> 0x5D1A0E7F00000000000000000000A002 DROP LOGIN app_user;
IF SUSER_SID('report_user') <> 0x5D1A0E7F00000000000000000000A003 DROP LOGIN report_user;
IF SUSER_ID('svc_app') IS NULL
    CREATE LOGIN svc_app WITH PASSWORD = 'Svc!App2026', CHECK_POLICY = OFF, DEFAULT_DATABASE = ReplayTest,
        SID = 0x5D1A0E7F00000000000000000000A001;
IF SUSER_ID('app_user') IS NULL
    CREATE LOGIN app_user WITH PASSWORD = 'App!User2026', CHECK_POLICY = OFF, DEFAULT_DATABASE = ReplayTest,
        SID = 0x5D1A0E7F00000000000000000000A002;
IF SUSER_ID('report_user') IS NULL
    CREATE LOGIN report_user WITH PASSWORD = 'Report!User2026', CHECK_POLICY = OFF, DEFAULT_DATABASE = ReplayTest,
        SID = 0x5D1A0E7F00000000000000000000A003;
GO

USE ReplayTest;
GO

IF USER_ID('svc_app') IS NULL CREATE USER svc_app FOR LOGIN svc_app;
IF USER_ID('app_user') IS NULL CREATE USER app_user FOR LOGIN app_user;
IF USER_ID('report_user') IS NULL CREATE USER report_user FOR LOGIN report_user;
ALTER USER svc_app WITH LOGIN = svc_app;
ALTER USER app_user WITH LOGIN = app_user;
ALTER USER report_user WITH LOGIN = report_user;
ALTER ROLE db_owner ADD MEMBER svc_app;
ALTER ROLE db_datareader ADD MEMBER app_user;
ALTER ROLE db_datawriter ADD MEMBER app_user;
GRANT EXECUTE TO app_user;
ALTER ROLE db_datareader ADD MEMBER report_user;
GRANT EXECUTE ON SCHEMA::dbo TO report_user;
GO

IF OBJECT_ID('dbo.Customers') IS NULL
CREATE TABLE dbo.Customers
(
    Id        int IDENTITY(1,1) PRIMARY KEY,
    Name      nvarchar(100)    NOT NULL,
    Email     varchar(200)     NULL,
    Region    char(2)          NOT NULL DEFAULT 'NA',
    Balance   decimal(18,2)    NOT NULL DEFAULT 0,
    CreatedAt datetime2(7)     NOT NULL DEFAULT SYSUTCDATETIME(),
    Notes     nvarchar(max)    NULL,
    Photo     varbinary(max)   NULL,
    RowGuid   uniqueidentifier NOT NULL DEFAULT NEWID()
);
GO

IF OBJECT_ID('dbo.Products') IS NULL
CREATE TABLE dbo.Products
(
    Id         int            NOT NULL PRIMARY KEY,
    Sku        varchar(20)    NOT NULL UNIQUE,
    Name       nvarchar(100)  NOT NULL,
    Price      money          NOT NULL,
    Stock      int            NOT NULL,
    Weight     real           NULL,
    Attributes xml            NULL,
    UpdatedAt  datetimeoffset(3) NOT NULL DEFAULT SYSDATETIMEOFFSET(),
    Version    rowversion
);
GO

IF OBJECT_ID('dbo.Orders') IS NULL
CREATE TABLE dbo.Orders
(
    Id         int IDENTITY(1,1) PRIMARY KEY,
    CustomerId int            NOT NULL REFERENCES dbo.Customers(Id),
    OrderedAt  datetime2(3)   NOT NULL DEFAULT SYSUTCDATETIME(),
    Status     varchar(20)    NOT NULL DEFAULT 'new',
    Total      decimal(18,2)  NOT NULL DEFAULT 0,
    Note       nvarchar(400)  NULL
);
GO

IF OBJECT_ID('dbo.OrderLines') IS NULL
CREATE TABLE dbo.OrderLines
(
    OrderId    int     NOT NULL REFERENCES dbo.Orders(Id),
    LineNumber     int     NOT NULL,
    ProductId  int     NOT NULL REFERENCES dbo.Products(Id),
    Qty        int     NOT NULL,
    UnitPrice  money   NOT NULL,
    PRIMARY KEY (OrderId, LineNumber)
);
GO

IF OBJECT_ID('dbo.AuditLog') IS NULL
CREATE TABLE dbo.AuditLog
(
    Id    bigint IDENTITY(1,1) PRIMARY KEY,
    At    datetime2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
    Who   sysname       NOT NULL DEFAULT SUSER_SNAME(),
    What  nvarchar(400) NOT NULL
);
GO

-- A wide, empty target for bulk copy.
IF OBJECT_ID('dbo.BulkTarget') IS NULL
CREATE TABLE dbo.BulkTarget
(
    Id      int           NOT NULL,
    Label   nvarchar(50)  NOT NULL,
    Amount  decimal(18,4) NULL,
    Stamp   datetime2(3)  NOT NULL
);
GO

-- One column of nearly every type, for round trips through parameters and result sets.
IF OBJECT_ID('dbo.TypeZoo') IS NULL
CREATE TABLE dbo.TypeZoo
(
    Id      int IDENTITY(1,1) PRIMARY KEY,
    cTiny   tinyint NULL, cSmall smallint NULL, cInt int NULL, cBig bigint NULL, cBit bit NULL,
    cDec    decimal(38,10) NULL, cNum numeric(9,3) NULL, cMoney money NULL, cSmallMoney smallmoney NULL,
    cReal   real NULL, cFloat float NULL,
    cDate   date NULL, cTime time(7) NULL, cDt datetime NULL, cSdt smalldatetime NULL, cDt2 datetime2(7) NULL,
    cDto    datetimeoffset(7) NULL,
    cChar   char(10) NULL, cVarchar varchar(100) NULL, cNchar nchar(10) NULL, cNvarchar nvarchar(100) NULL,
    cVarcharMax varchar(max) NULL, cNvarcharMax nvarchar(max) NULL,
    cBinary binary(8) NULL, cVarbinary varbinary(100) NULL, cVarbinaryMax varbinary(max) NULL,
    cGuid   uniqueidentifier NULL, cXml xml NULL, cVariant sql_variant NULL,
    cText   text NULL, cNtext ntext NULL, cImage image NULL
);
GO

IF TYPE_ID('dbo.IdList') IS NULL
    CREATE TYPE dbo.IdList AS TABLE (Id int NOT NULL PRIMARY KEY);
GO
IF TYPE_ID('dbo.OrderLineList') IS NULL
    CREATE TYPE dbo.OrderLineList AS TABLE (ProductId int NOT NULL, Qty int NOT NULL);
GO

-- Rows on demand, for large results without storing them.
CREATE OR ALTER FUNCTION dbo.Numbers(@Count int)
RETURNS TABLE
AS RETURN
    WITH e1(n) AS (SELECT 1 FROM (VALUES (1),(1),(1),(1),(1),(1),(1),(1),(1),(1)) v(n)),
         e3(n) AS (SELECT 1 FROM e1 a CROSS JOIN e1 b CROSS JOIN e1 c),
         e9(n) AS (SELECT 1 FROM e3 a CROSS JOIN e3 b CROSS JOIN e3 c)
    SELECT TOP (@Count) CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS int) AS N FROM e9;
GO

CREATE OR ALTER PROCEDURE dbo.GetCustomer
    @Id int,
    @Name nvarchar(100) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT @Name = Name FROM dbo.Customers WHERE Id = @Id;
    SELECT Id, Name, Email, Region, Balance FROM dbo.Customers WHERE Id = @Id;
    RETURN CASE WHEN @Name IS NULL THEN 1 ELSE 0 END;
END
GO

CREATE OR ALTER PROCEDURE dbo.GetCustomersByIds
    @Ids dbo.IdList READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SELECT c.Id, c.Name FROM dbo.Customers c JOIN @Ids i ON i.Id = c.Id ORDER BY c.Id;
END
GO

-- An order in one transaction of its own: identity, inventory, audit - the shape most OLTP writes take.
CREATE OR ALTER PROCEDURE dbo.PlaceOrder
    @CustomerId int,
    @Lines dbo.OrderLineList READONLY,
    @Note nvarchar(400) = NULL,
    @OrderId int OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
        INSERT dbo.Orders (CustomerId, Note) VALUES (@CustomerId, @Note);
        SET @OrderId = SCOPE_IDENTITY();

        INSERT dbo.OrderLines (OrderId, LineNumber, ProductId, Qty, UnitPrice)
        SELECT @OrderId, ROW_NUMBER() OVER (ORDER BY l.ProductId), l.ProductId, l.Qty, p.Price
        FROM @Lines l JOIN dbo.Products p ON p.Id = l.ProductId;

        UPDATE p SET Stock = p.Stock - l.Qty
        FROM dbo.Products p JOIN @Lines l ON l.ProductId = p.Id;

        UPDATE dbo.Orders
        SET Total = (SELECT SUM(Qty * UnitPrice) FROM dbo.OrderLines WHERE OrderId = @OrderId)
        WHERE Id = @OrderId;

        INSERT dbo.AuditLog (What) VALUES (CONCAT(N'order ', @OrderId, N' for customer ', @CustomerId));
    COMMIT;
    RETURN 0;
END
GO

CREATE OR ALTER PROCEDURE dbo.SalesReport
    @From date, @To date
AS
BEGIN
    SET NOCOUNT ON;
    SELECT c.Region, COUNT(*) AS Orders, SUM(o.Total) AS Total
    FROM dbo.Orders o JOIN dbo.Customers c ON c.Id = o.CustomerId
    WHERE o.OrderedAt >= @From AND o.OrderedAt < DATEADD(day, 1, @To)
    GROUP BY c.Region ORDER BY c.Region;

    SELECT TOP (10) p.Sku, p.Name, SUM(l.Qty) AS Units
    FROM dbo.OrderLines l JOIN dbo.Products p ON p.Id = l.ProductId
    GROUP BY p.Sku, p.Name ORDER BY Units DESC, p.Sku;
END
GO

CREATE OR ALTER PROCEDURE dbo.FailWith
    @Number int, @Message nvarchar(200), @Severity int = 16, @State int = 1
AS
BEGIN
    RAISERROR(@Message, @Severity, @State);
END
GO

CREATE OR ALTER PROCEDURE dbo.Pause
    @Milliseconds int
AS
BEGIN
    DECLARE @delay datetime = DATEADD(millisecond, @Milliseconds, 0);
    WAITFOR DELAY @delay;
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.Customers)
BEGIN
    INSERT dbo.Customers (Name, Email, Region, Balance, Notes)
    SELECT CONCAT(N'Customer ', n),
           CASE WHEN n % 7 = 0 THEN NULL ELSE CONCAT('customer', n, '@example.com') END,
           CHOOSE(n % 4 + 1, 'NA', 'EU', 'AP', 'SA'),
           CAST((n * 37) % 1000 AS decimal(18,2)) + 0.25,
           CASE WHEN n % 10 = 0 THEN REPLICATE(N'note ', 50) END
    FROM dbo.Numbers(200);

    UPDATE dbo.Customers SET Name = N'Zoë Ünïcode' WHERE Id = 4;
    UPDATE dbo.Customers SET Name = N'Grace Hopper' WHERE Id = 2;

    INSERT dbo.Products (Id, Sku, Name, Price, Stock, Weight, Attributes)
    SELECT n, CONCAT('SKU-', RIGHT(CONCAT('0000', n), 4)), CONCAT(N'Product ', n),
           CAST(n * 3 + 0.99 AS money), 100000, n * 0.5,
           CASE WHEN n % 5 = 0 THEN CAST(CONCAT('<p n="', n, '"><c>red</c></p>') AS xml) END
    FROM dbo.Numbers(50);

    INSERT dbo.Orders (CustomerId, OrderedAt, Status, Total)
    SELECT (n % 200) + 1, DATEADD(minute, n * 17, '2026-01-01'), CHOOSE(n % 3 + 1, 'new', 'shipped', 'closed'), 0
    FROM dbo.Numbers(500);

    INSERT dbo.OrderLines (OrderId, LineNumber, ProductId, Qty, UnitPrice)
    SELECT o.Id, l.N, ((o.Id * 7 + l.N) % 50) + 1, l.N, p.Price
    FROM dbo.Orders o
    CROSS JOIN dbo.Numbers(3) l
    JOIN dbo.Products p ON p.Id = ((o.Id * 7 + l.N) % 50) + 1;

    UPDATE o SET Total = t.Total
    FROM dbo.Orders o
    JOIN (SELECT OrderId, SUM(Qty * UnitPrice) AS Total FROM dbo.OrderLines GROUP BY OrderId) t ON t.OrderId = o.Id;
END
GO

USE ReplayOther;
GO
IF USER_ID('svc_app') IS NULL CREATE USER svc_app FOR LOGIN svc_app;
ALTER USER svc_app WITH LOGIN = svc_app;
ALTER ROLE db_owner ADD MEMBER svc_app;
IF OBJECT_ID('dbo.Settings') IS NULL
    CREATE TABLE dbo.Settings (Name sysname PRIMARY KEY, Value nvarchar(200) NOT NULL);
IF NOT EXISTS (SELECT 1 FROM dbo.Settings)
    INSERT dbo.Settings VALUES (N'mode', N'replay-test'), (N'version', N'1');
GO

PRINT 'seeded';
