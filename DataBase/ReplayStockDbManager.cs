using Dapper;
using MoneyCreator.Models;
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;

namespace MoneyCreator.DataBase
{
    public sealed class ReplayHogaSnapshotRecord
    {
        public string Code { get; set; }
        public DateTime SnapshotTime { get; set; }
        public double AskPrice1 { get; set; }
        public long AskQty1 { get; set; }
        public double AskPrice2 { get; set; }
        public long AskQty2 { get; set; }
        public double AskPrice3 { get; set; }
        public long AskQty3 { get; set; }
        public double AskPrice4 { get; set; }
        public long AskQty4 { get; set; }
        public double AskPrice5 { get; set; }
        public long AskQty5 { get; set; }
        public double BidPrice1 { get; set; }
        public long BidQty1 { get; set; }
        public double BidPrice2 { get; set; }
        public long BidQty2 { get; set; }
        public double BidPrice3 { get; set; }
        public long BidQty3 { get; set; }
        public double BidPrice4 { get; set; }
        public long BidQty4 { get; set; }
        public double BidPrice5 { get; set; }
        public long BidQty5 { get; set; }
        public long TotalAskQty { get; set; }
        public long TotalBidQty { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public HogaData ToHogaData()
        {
            return new HogaData
            {
                Code = Code,
                TotalAskQty = TotalAskQty,
                TotalBidQty = TotalBidQty,
                Asks = BuildItems(true),
                Bids = BuildItems(false)
            };
        }

        private List<HogaItem> BuildItems(bool asks)
        {
            var items = new List<HogaItem>();

            if (asks)
            {
                AddItem(items, AskPrice1, AskQty1);
                AddItem(items, AskPrice2, AskQty2);
                AddItem(items, AskPrice3, AskQty3);
                AddItem(items, AskPrice4, AskQty4);
                AddItem(items, AskPrice5, AskQty5);
            }
            else
            {
                AddItem(items, BidPrice1, BidQty1);
                AddItem(items, BidPrice2, BidQty2);
                AddItem(items, BidPrice3, BidQty3);
                AddItem(items, BidPrice4, BidQty4);
                AddItem(items, BidPrice5, BidQty5);
            }

            return items;
        }

        private static void AddItem(List<HogaItem> items, double price, long qty)
        {
            if (price <= 0 && qty <= 0)
                return;

            items.Add(new HogaItem
            {
                Price = price,
                Quantity = qty,
                KrxQuantity = qty,
                IsCurrentPrice = items.Count == 0
            });
        }
    }

    public sealed class ReplayStockDbManager
    {
        private readonly string _dbDirectory;
        private readonly DateTime _tradingDate;
        private readonly string _connectionString;

        public string DatabasePath { get; }

        public ReplayStockDbManager()
            : this(@"F:\Project\DB", DateTime.Today)
        {
        }

        public ReplayStockDbManager(DateTime tradingDate)
            : this(@"F:\Project\DB", tradingDate)
        {
        }

        public ReplayStockDbManager(string dbDirectory, DateTime tradingDate)
        {
            _dbDirectory = string.IsNullOrWhiteSpace(dbDirectory) ? @"F:\Project\DB" : dbDirectory;
            _tradingDate = tradingDate == DateTime.MinValue ? DateTime.Today : tradingDate.Date;

            if (!Directory.Exists(_dbDirectory))
                Directory.CreateDirectory(_dbDirectory);

            DatabasePath = Path.Combine(_dbDirectory, $"ReplayData_{_tradingDate:yyyyMMdd}.db");
            _connectionString = $"Data Source={DatabasePath};Version=3;Journal Mode=Wal;Synchronous=Normal;";

            InitializeDatabase();
        }

        public void InitializeDatabase()
        {
            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                conn.Execute(@"
                    CREATE TABLE IF NOT EXISTS ReplayRawTicks (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Code TEXT NOT NULL,
                        Price REAL NOT NULL,
                        Volume INTEGER NOT NULL,
                        Strength REAL,
                        AskVolume INTEGER,
                        BidVolume INTEGER,
                        Side TEXT,
                        CreatedAt DATETIME NOT NULL,
                        UNIQUE(Code, CreatedAt, Price, Volume)
                    );
                    CREATE INDEX IF NOT EXISTS idx_replay_rawticks_code_time ON ReplayRawTicks (Code, CreatedAt ASC);

                    CREATE TABLE IF NOT EXISTS ReplayMinuteCandles (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Code TEXT NOT NULL,
                        Name TEXT,
                        CandleTime DATETIME NOT NULL,
                        Interval TEXT NOT NULL DEFAULT '1m',
                        Open REAL NOT NULL,
                        High REAL NOT NULL,
                        Low REAL NOT NULL,
                        Close REAL NOT NULL,
                        Volume INTEGER NOT NULL,
                        AccumulatedVolume INTEGER,
                        Strength REAL,
                        TotalAskQty INTEGER,
                        TotalBidQty INTEGER,
                        Source TEXT,
                        CreatedAt DATETIME NOT NULL,
                        UNIQUE(Code, CandleTime, Interval)
                    );
                    CREATE INDEX IF NOT EXISTS idx_replay_minute_code_time ON ReplayMinuteCandles (Code, CandleTime ASC);

                    CREATE TABLE IF NOT EXISTS ReplayHogaSnapshots (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Code TEXT NOT NULL,
                        SnapshotTime DATETIME NOT NULL,
                        AskPrice1 REAL, AskQty1 INTEGER,
                        AskPrice2 REAL, AskQty2 INTEGER,
                        AskPrice3 REAL, AskQty3 INTEGER,
                        AskPrice4 REAL, AskQty4 INTEGER,
                        AskPrice5 REAL, AskQty5 INTEGER,
                        BidPrice1 REAL, BidQty1 INTEGER,
                        BidPrice2 REAL, BidQty2 INTEGER,
                        BidPrice3 REAL, BidQty3 INTEGER,
                        BidPrice4 REAL, BidQty4 INTEGER,
                        BidPrice5 REAL, BidQty5 INTEGER,
                        TotalAskQty INTEGER,
                        TotalBidQty INTEGER,
                        CreatedAt DATETIME NOT NULL,
                        UNIQUE(Code, SnapshotTime)
                    );
                    CREATE INDEX IF NOT EXISTS idx_replay_hoga_code_time ON ReplayHogaSnapshots (Code, SnapshotTime ASC);
                ");
            }
        }

        public void SaveRawTicks(IEnumerable<TickData> ticks)
        {
            var list = ticks == null
                ? new List<TickData>()
                : ticks.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Code) && x.Price > 0).ToList();

            if (list.Count == 0)
                return;

            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                using (var tran = conn.BeginTransaction())
                {
                    conn.Execute(@"
                        INSERT OR IGNORE INTO ReplayRawTicks
                            (Code, Price, Volume, Strength, AskVolume, BidVolume, Side, CreatedAt)
                        VALUES
                            (@Code, @Price, @Volume, @Strength, @AskVolume, @BidVolume, @Side, @CreatedAt);", list, tran);
                    tran.Commit();
                }
            }
        }

        public void SaveMinuteCandles(IEnumerable<MinuteCandleRecord> candles)
        {
            var list = candles == null
                ? new List<MinuteCandleRecord>()
                : candles.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Code) && x.CandleTime != DateTime.MinValue).ToList();

            if (list.Count == 0)
                return;

            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                using (var tran = conn.BeginTransaction())
                {
                    conn.Execute(@"
                        INSERT OR REPLACE INTO ReplayMinuteCandles
                            (Code, Name, CandleTime, Interval, Open, High, Low, Close, Volume,
                             AccumulatedVolume, Strength, TotalAskQty, TotalBidQty, Source, CreatedAt)
                        VALUES
                            (@Code, @Name, @CandleTime, @Interval, @Open, @High, @Low, @Close, @Volume,
                             @AccumulatedVolume, @Strength, @TotalAskQty, @TotalBidQty, @Source, @CreatedAt);", list, tran);
                    tran.Commit();
                }
            }
        }

        public void SaveHogaSnapshots(IEnumerable<ReplayHogaSnapshotRecord> snapshots)
        {
            var list = snapshots == null
                ? new List<ReplayHogaSnapshotRecord>()
                : snapshots.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Code) && x.SnapshotTime != DateTime.MinValue).ToList();

            if (list.Count == 0)
                return;

            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                using (var tran = conn.BeginTransaction())
                {
                    conn.Execute(@"
                        INSERT OR REPLACE INTO ReplayHogaSnapshots
                            (Code, SnapshotTime,
                             AskPrice1, AskQty1, AskPrice2, AskQty2, AskPrice3, AskQty3, AskPrice4, AskQty4, AskPrice5, AskQty5,
                             BidPrice1, BidQty1, BidPrice2, BidQty2, BidPrice3, BidQty3, BidPrice4, BidQty4, BidPrice5, BidQty5,
                             TotalAskQty, TotalBidQty, CreatedAt)
                        VALUES
                            (@Code, @SnapshotTime,
                             @AskPrice1, @AskQty1, @AskPrice2, @AskQty2, @AskPrice3, @AskQty3, @AskPrice4, @AskQty4, @AskPrice5, @AskQty5,
                             @BidPrice1, @BidQty1, @BidPrice2, @BidQty2, @BidPrice3, @BidQty3, @BidPrice4, @BidQty4, @BidPrice5, @BidQty5,
                             @TotalAskQty, @TotalBidQty, @CreatedAt);", list, tran);
                    tran.Commit();
                }
            }
        }

        public List<MinuteCandleRecord> LoadDailyMinuteCandles(string code, DateTime tradingDate)
        {
            if (string.IsNullOrWhiteSpace(code))
                return new List<MinuteCandleRecord>();

            var from = tradingDate.Date;
            var to = from.AddDays(1);

            using (var conn = new SQLiteConnection(_connectionString))
            {
                const string sql = @"
                    SELECT *
                    FROM ReplayMinuteCandles
                    WHERE Code = @code
                      AND CandleTime >= @fromTime
                      AND CandleTime < @toTime
                    ORDER BY CandleTime ASC;";

                return conn.Query<MinuteCandleRecord>(sql, new
                {
                    code,
                    fromTime = from.ToString("yyyy-MM-dd HH:mm:ss"),
                    toTime = to.ToString("yyyy-MM-dd HH:mm:ss")
                }).ToList();
            }
        }

        public List<TrackedSymbol> LoadDailyTicks(string code, DateTime tradingDate)
        {
            if (string.IsNullOrWhiteSpace(code))
                return new List<TrackedSymbol>();

            var from = tradingDate.Date;
            var to = from.AddDays(1);

            using (var conn = new SQLiteConnection(_connectionString))
            {
                const string sql = @"
                    SELECT Code, Price, Volume, Strength, AskVolume, BidVolume, Side, CreatedAt
                    FROM ReplayRawTicks
                    WHERE Code = @code
                      AND CreatedAt >= @fromTime
                      AND CreatedAt < @toTime
                    ORDER BY CreatedAt ASC;";

                var rows = conn.Query<TickData>(sql, new
                {
                    code,
                    fromTime = from.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    toTime = to.ToString("yyyy-MM-dd HH:mm:ss.fff")
                }).ToList();

                return ConvertTicks(rows);
            }
        }

        public List<TrackedSymbol> LoadRecentTicks(string code, DateTime replayTime, int minutes)
        {
            var all = LoadDailyTicks(code, replayTime.Date);
            if (all.Count == 0)
                return all;

            var from = replayTime.AddMinutes(-Math.Max(1, minutes));
            var recent = all
                .Where(x => x.LastUpdated >= from && x.LastUpdated <= replayTime)
                .OrderBy(x => x.LastUpdated)
                .ToList();

            if (recent.Count > 0)
                return recent;

            return all.Take(Math.Max(1, minutes * 60)).ToList();
        }

        public List<ReplayHogaSnapshotRecord> LoadDailyHogaSnapshots(string code, DateTime tradingDate)
        {
            if (string.IsNullOrWhiteSpace(code))
                return new List<ReplayHogaSnapshotRecord>();

            var from = tradingDate.Date;
            var to = from.AddDays(1);

            using (var conn = new SQLiteConnection(_connectionString))
            {
                const string sql = @"
                    SELECT *
                    FROM ReplayHogaSnapshots
                    WHERE Code = @code
                      AND SnapshotTime >= @fromTime
                      AND SnapshotTime < @toTime
                    ORDER BY SnapshotTime ASC;";

                return conn.Query<ReplayHogaSnapshotRecord>(sql, new
                {
                    code,
                    fromTime = from.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    toTime = to.ToString("yyyy-MM-dd HH:mm:ss.fff")
                }).ToList();
            }
        }

        public List<Symbol> LoadSymbols()
        {
            using (var conn = new SQLiteConnection(_connectionString))
            {
                const string sql = @"
                    SELECT Code, Code AS Name, CAST(MAX(Price) AS TEXT) AS LastPrice, '' AS Change
                    FROM ReplayRawTicks
                    GROUP BY Code
                    ORDER BY Code ASC;";

                return conn.Query<Symbol>(sql).ToList();
            }
        }

        private static List<TrackedSymbol> ConvertTicks(List<TickData> rows)
        {
            var result = new List<TrackedSymbol>();

            if (rows == null || rows.Count == 0)
                return result;

            double high = 0;
            double low = 0;
            long accumulatedVolume = 0;

            foreach (var row in rows.OrderBy(x => x.CreatedAt))
            {
                double price = row.Price;
                if (price <= 0)
                    continue;

                if (high <= 0 || price > high)
                    high = price;

                if (low <= 0 || price < low)
                    low = price;

                accumulatedVolume += Math.Abs(row.Volume);

                result.Add(new TrackedSymbol
                {
                    Code = row.Code,
                    LastPrice = price,
                    ClosePrice = price,
                    HighPrice = high,
                    LowPrice = low,
                    LastVolume = row.Volume,
                    AccumulatedVolume = accumulatedVolume,
                    Strength = row.Strength,
                    Side = row.Side,
                    TotalAskQty = row.AskVolume,
                    TotalBidQty = row.BidVolume,
                    LastUpdated = row.CreatedAt
                });
            }

            return result;
        }
    }
}
