using Dapper;
using MoneyCreator.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MoneyCreator.DataBase
{
    public class StockDbManager
    {
        // 외부 KiwoomApiService에서 직접 넣는 Raw Tick 큐
        public ConcurrentQueue<TickData> _tickQueue = new ConcurrentQueue<TickData>();
        public event Action<string> OnLog;

        // RawTick은 용량이 커서 날짜별 DB 권장
        private readonly string _rawConnStr = $"Data Source=F:\\Project\\DB\\RawData_{DateTime.Now:yyyyMMdd}.db;Version=3;Journal Mode=Wal;Synchronous=Off;temp_store=2;";

        // 주문/체결/매매결과/차트용 핵심 데이터는 메인 DB
        private readonly string _mainConnStr = @"Data Source=E:\Project\DB\MoneyCreator_Main.db;Version=3;Journal Mode=Wal;Synchronous=Normal;";

        // 일별/누적 성과 요약 전용 DB
        // 원본 TradeResults/Orders를 기준으로 언제든 재집계 가능한 캐시 성격이다.
        private readonly string _performanceConnStr = @"Data Source=E:\Project\DB\Performance.db;Version=3;Journal Mode=Wal;Synchronous=Normal;";

        // ML 학습용 대용량 데이터는 날짜별 DB로 분리
        private readonly string _mlDbDirectory = @"E:\Project\DB\ML";
        private readonly object _mlInitLock = new object();
        private readonly ConcurrentDictionary<string, bool> _initializedMlDatabases = new ConcurrentDictionary<string, bool>();

        private const int RawTickBatchSize = 2000;
        private const int RawTickMaxBatchSize = 4000;

        private const int MinuteBatchSize = 500;
        private const int MinuteMaxBatchSize = 1000;

        private const int FeatureBatchSize = 500;
        private const int FeatureMaxBatchSize = 1000;

        private const int SignalBatchSize = 500;
        private const int SignalMaxBatchSize = 1000;

        private const int TradeResultBatchSize = 200;
        private const int TradeResultMaxBatchSize = 500;

        private const int OutcomeLabelBatchSize = 500;
        private const int OutcomeLabelMaxBatchSize = 1000;

        private readonly ConcurrentQueue<MinuteCandleRecord> _minuteQueue = new ConcurrentQueue<MinuteCandleRecord>();
        private readonly ConcurrentQueue<FeatureSnapshotRecord> _featureQueue = new ConcurrentQueue<FeatureSnapshotRecord>();
        private readonly ConcurrentQueue<SignalLogRecord> _signalQueue = new ConcurrentQueue<SignalLogRecord>();
        private readonly ConcurrentQueue<TradeResultRecord> _tradeResultQueue = new ConcurrentQueue<TradeResultRecord>();
        private readonly ConcurrentQueue<SnapshotOutcomeLabelRecord> _outcomeLabelQueue = new ConcurrentQueue<SnapshotOutcomeLabelRecord>();

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly object _flushLock = new object();
        private static readonly TimeSpan TimedFlushInterval = TimeSpan.FromSeconds(30);

        private DateTime _lastRawTickFlush = DateTime.Now;
        private DateTime _lastMinuteFlush = DateTime.Now;
        private DateTime _lastFeatureFlush = DateTime.Now;
        private DateTime _lastSignalFlush = DateTime.Now;
        private DateTime _lastTradeResultFlush = DateTime.Now;
        private DateTime _lastOutcomeLabelFlush = DateTime.Now;

        public StockDbManager(ConcurrentQueue<TickData> tickQueue)
        {
            _tickQueue = tickQueue ?? new ConcurrentQueue<TickData>();
            InitializeDatabase();
        }

        public void InitializeDatabase()
        {
            EnsureDirectoryExists(_rawConnStr);
            EnsureDirectoryExists(_mainConnStr);
            EnsureDirectoryExists(_performanceConnStr);
            EnsureMlDatabase(DateTime.Now);
            InitializePerformanceDatabase();
            using (var conn = new SQLiteConnection(_rawConnStr))
            {
                conn.Open();
                conn.Execute(@"
                    CREATE TABLE IF NOT EXISTS RawTicks (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Code TEXT NOT NULL,
                        Price REAL NOT NULL,
                        Volume INTEGER NOT NULL,
                        Strength REAL,
                        AskVolume INTEGER,
                        BidVolume INTEGER,
                        Side TEXT,
                        CreatedAt DATETIME NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_rawticks_code_time ON RawTicks (Code, CreatedAt DESC);
                    CREATE INDEX IF NOT EXISTS idx_rawticks_time ON RawTicks (CreatedAt DESC);

                    -- 기존 코드 호환용. 기존 RealTime_Ticks를 사용 중이면 그대로 유지된다.
                    CREATE TABLE IF NOT EXISTS RealTime_Ticks (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Code TEXT NOT NULL,
                        Price REAL,
                        Volume INTEGER,
                        Strength REAL,
                        AskVolume INTEGER,
                        BidVolume INTEGER,
                        Side TEXT,
                        CreatedAt DATETIME DEFAULT (STRFTIME('%Y-%m-%d %H:%M:%f', 'NOW'))
                    );
                    CREATE INDEX IF NOT EXISTS idx_realtime_ticks_code_time ON RealTime_Ticks (Code, CreatedAt DESC);
                ");
            }

            using (var conn = new SQLiteConnection(_mainConnStr))
            {
                conn.Open();
                conn.Execute(@"
                    CREATE TABLE IF NOT EXISTS MinuteCandles (
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
                    CREATE INDEX IF NOT EXISTS idx_minute_code_time ON MinuteCandles (Code, CandleTime DESC);

                    CREATE TABLE IF NOT EXISTS Orders (
                        OrderNo TEXT PRIMARY KEY,
                        TradeGroupId TEXT NOT NULL,
                        OrderTime DATETIME,
                        Code TEXT,
                        Name TEXT,
                        OrderType TEXT,
                        Qty INTEGER,
                        ExecQty INTEGER,
                        Price REAL,
                        ExecPrice REAL,
                        Status TEXT,
                        StrategyName TEXT,
                        Fee REAL,
                        Tax REAL,
                        ParentOrderNo TEXT,
                        TiRexDirection TEXT,
                        TiRexConfidence REAL,
                        TiRexPredictedReturn REAL
                    );
                    CREATE INDEX IF NOT EXISTS idx_orders_group ON Orders (TradeGroupId);
                    CREATE INDEX IF NOT EXISTS idx_orders_code_time ON Orders (Code, OrderTime DESC);

                    CREATE TABLE IF NOT EXISTS TradeResults (
                        TradeResultId TEXT PRIMARY KEY,
                        TradeGroupId TEXT,
                        Code TEXT NOT NULL,
                        Name TEXT,
                        StrategyName TEXT,
                        TradeMode TEXT,
                        EntryTime DATETIME,
                        ExitTime DATETIME,
                        EntryPrice REAL,
                        ExitPrice REAL,
                        Quantity INTEGER,
                        ProfitRate REAL,
                        ProfitAmount REAL,
                        MaxProfitRate REAL,
                        MinProfitRate REAL,
                        HoldSeconds REAL,
                        Label INTEGER,
                        TiRexDirection TEXT,
                        TiRexConfidence REAL,
                        TiRexPredictedReturn REAL,
                        ExitReason TEXT,
                        CreatedAt DATETIME NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_trade_result_code_time ON TradeResults (Code, EntryTime DESC);
                    CREATE INDEX IF NOT EXISTS idx_trade_result_group ON TradeResults (TradeGroupId);

                    -- 기존 코드 호환용
                    CREATE TABLE IF NOT EXISTS TradeSummary (
                        TradeId INTEGER PRIMARY KEY AUTOINCREMENT,
                        TradeGroupId TEXT NOT NULL UNIQUE,
                        Code TEXT,
                        Name TEXT,
                        StrategyName TEXT,
                        TradeMode TEXT,
                        EntryTime DATETIME,
                        ExitTime DATETIME,
                        AvgBuyPrice REAL,
                        BuyQty INTEGER,
                        AvgSellPrice REAL,
                        SellQty INTEGER,
                        ProfitRate REAL,
                        ProfitAmount REAL,
                        MaxProfitRate REAL,
                        MinProfitRate REAL,
                        HoldTimeMin REAL,
                        TotalFeeTax REAL
                    );
                ");

                EnsureOrderTiRexColumns(conn);
                EnsureTradeResultColumns(conn);
            }
        }


        private void InitializePerformanceDatabase()
        {
            using (var conn = new SQLiteConnection(_performanceConnStr))
            {
                conn.Open();
                conn.Execute(@"
                    CREATE TABLE IF NOT EXISTS DailyPerformance (
                        TradeDate TEXT PRIMARY KEY,
                        TradeCount INTEGER NOT NULL DEFAULT 0,
                        WinCount INTEGER NOT NULL DEFAULT 0,
                        LossCount INTEGER NOT NULL DEFAULT 0,
                        FlatCount INTEGER NOT NULL DEFAULT 0,
                        GrossProfit REAL NOT NULL DEFAULT 0,
                        GrossLoss REAL NOT NULL DEFAULT 0,
                        FeeTax REAL NOT NULL DEFAULT 0,
                        NetProfit REAL NOT NULL DEFAULT 0,
                        AvgProfitRate REAL NOT NULL DEFAULT 0,
                        WinRate REAL NOT NULL DEFAULT 0,
                        MaxProfit REAL NOT NULL DEFAULT 0,
                        MaxLoss REAL NOT NULL DEFAULT 0,
                        BestTradeRate REAL NOT NULL DEFAULT 0,
                        WorstTradeRate REAL NOT NULL DEFAULT 0,
                        UpdatedAt DATETIME NOT NULL,
                        CreatedAt DATETIME NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS PerformanceBuildState (
                        TradeDate TEXT PRIMARY KEY,
                        Status TEXT NOT NULL,
                        LastAttemptAt DATETIME NOT NULL,
                        ErrorMessage TEXT,
                        UpdatedAt DATETIME NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS TotalPerformance (
                        Id INTEGER PRIMARY KEY CHECK (Id = 1),
                        TradeCount INTEGER NOT NULL DEFAULT 0,
                        WinCount INTEGER NOT NULL DEFAULT 0,
                        LossCount INTEGER NOT NULL DEFAULT 0,
                        FlatCount INTEGER NOT NULL DEFAULT 0,
                        GrossProfit REAL NOT NULL DEFAULT 0,
                        GrossLoss REAL NOT NULL DEFAULT 0,
                        FeeTax REAL NOT NULL DEFAULT 0,
                        NetProfit REAL NOT NULL DEFAULT 0,
                        AvgProfitRate REAL NOT NULL DEFAULT 0,
                        WinRate REAL NOT NULL DEFAULT 0,
                        MaxProfit REAL NOT NULL DEFAULT 0,
                        MaxLoss REAL NOT NULL DEFAULT 0,
                        BestTradeRate REAL NOT NULL DEFAULT 0,
                        WorstTradeRate REAL NOT NULL DEFAULT 0,
                        UpdatedAt DATETIME NOT NULL,
                        CreatedAt DATETIME NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS idx_daily_performance_date ON DailyPerformance (TradeDate DESC);
                    CREATE INDEX IF NOT EXISTS idx_performance_state_status ON PerformanceBuildState (Status, TradeDate DESC);
                ");
            }
        }

        private string GetMlConnStr(DateTime date)
        {
            var safeDate = date == default(DateTime) ? DateTime.Now : date;
            string fileName = $"MLData_{safeDate:yyyyMMdd}.db";
            return $"Data Source={Path.Combine(_mlDbDirectory, fileName)};Version=3;Journal Mode=Wal;Synchronous=Normal;";
        }
        private void EnsureTradeResultColumns(SQLiteConnection conn)
        {
            EnsureColumnExists(conn,
                "TradeResults",
                "TradeMode",
                "TEXT");

            EnsureColumnExists(conn,
                "TradeResults",
                "TiRexDirection",
                "TEXT");

            EnsureColumnExists(conn,
                "TradeResults",
                "TiRexConfidence",
                "REAL DEFAULT 0");

            EnsureColumnExists(conn,
                "TradeResults",
                "TiRexPredictedReturn",
                "REAL DEFAULT 0");
        }
        private void EnsureMlDatabase(DateTime date)
        {
            var safeDate = date == default(DateTime) ? DateTime.Now : date;
            string key = safeDate.ToString("yyyyMMdd");

            if (_initializedMlDatabases.ContainsKey(key))
                return;

            lock (_mlInitLock)
            {
                if (_initializedMlDatabases.ContainsKey(key))
                    return;

                string connStr = GetMlConnStr(safeDate);
                EnsureDirectoryExists(connStr);

                using (var conn = new SQLiteConnection(connStr))
                {
                    conn.Open();
                    conn.Execute(SqlCreateMlTables);
                    EnsureFeatureSnapshotMarketStateColumns(conn);
                    EnsureSignalLogTiRexColumns(conn);
                }

                _initializedMlDatabases[key] = true;
            }
        }
        private void EnsureSignalLogTiRexColumns(SQLiteConnection conn)
        {
            if (conn == null)
                return;

            EnsureColumnExists(conn, "SignalLogs", "TradeMode", "TEXT");
            EnsureColumnExists(conn, "SignalLogs", "TiRexDirection", "TEXT");
            EnsureColumnExists(conn, "SignalLogs", "TiRexConfidence", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "SignalLogs", "TiRexPredictedReturn", "REAL DEFAULT 0");
        }

        // 기존 날짜별 ML DB가 이미 만들어진 경우에도 누락 컬럼을 자동 보강한다.
        private void EnsureFeatureSnapshotMarketStateColumns(SQLiteConnection conn)
        {
            if (conn == null)
                return;

            EnsureColumnExists(conn, "FeatureSnapshots", "Phase", "TEXT");
            EnsureColumnExists(conn, "FeatureSnapshots", "IsSideways", "INTEGER DEFAULT 0");
            EnsureColumnExists(conn, "FeatureSnapshots", "TrendStrength", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "FeatureSnapshots", "VolatilityRate", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "FeatureSnapshots", "RangePosition", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "FeatureSnapshots", "TradeMode", "TEXT");

            EnsureSignalLogTiRexColumns(conn);
        }

        private void EnsureOrderTiRexColumns(SQLiteConnection conn)
        {
            EnsureColumnExists(conn, "Orders", "TiRexDirection", "TEXT");
            EnsureColumnExists(conn, "Orders", "TiRexConfidence", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "Orders", "TiRexPredictedReturn", "REAL DEFAULT 0");
        }

        private void EnsureColumnExists(SQLiteConnection conn, string tableName, string columnName, string columnDefinition)
        {
            bool exists = false;

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info({tableName});";

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string name = Convert.ToString(reader["name"]);
                        if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
                        {
                            exists = true;
                            break;
                        }
                    }
                }
            }

            if (!exists)
                conn.Execute($"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};");
        }

        private void EnsureDirectoryExists(string connectionString)
        {
            var builder = new SQLiteConnectionStringBuilder(connectionString);
            var directory = Path.GetDirectoryName(builder.DataSource);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);
        }

        // ============================================================
        // Queue 등록 메서드: 실시간 경로에서는 이쪽 사용 권장
        // ============================================================
        public void EnqueueRawTick(TickData tick)
        {
            if (tick == null || string.IsNullOrWhiteSpace(tick.Code)) return;
            _tickQueue.Enqueue(tick);
        }

        public void EnqueueMinuteCandle(MinuteCandleRecord candle)
        {
            if (candle == null || string.IsNullOrWhiteSpace(candle.Code)) return;
            _minuteQueue.Enqueue(candle);
        }

        public void EnqueueMinuteCandles(IEnumerable<MinuteCandleRecord> candles)
        {
            if (candles == null) return;
            foreach (var c in candles)
                EnqueueMinuteCandle(c);
        }

        public void EnqueueFeatureSnapshot(FeatureSnapshotRecord snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Code)) return;
            _featureQueue.Enqueue(snapshot);
        }

        public void EnqueueSignalLog(SignalLogRecord signal)
        {
            if (signal == null || string.IsNullOrWhiteSpace(signal.Code)) return;
            _signalQueue.Enqueue(signal);
        }

        public void EnqueueTradeResult(TradeResultRecord result)
        {
            if (result == null || string.IsNullOrWhiteSpace(result.Code)) return;
            _tradeResultQueue.Enqueue(result);
        }

        public void BuildTradeResultFromOrders(string tradeGroupId)
        {
            if (string.IsNullOrWhiteSpace(tradeGroupId))
                return;

            using (var conn = new SQLiteConnection(_mainConnStr))
            {
                conn.Open();

                var orders = conn.Query<OrderInfo>(@"
            SELECT *
            FROM Orders
            WHERE TradeGroupId = @tradeGroupId
              AND ExecQty > 0
            ORDER BY OrderTime ASC",
                    new { tradeGroupId }).ToList();

                var buys = orders
                    .Where(x => x.OrderType != null && x.OrderType.Contains("매수"))
                    .OrderBy(x => ParseDbDateTime(x.OrderTime))
                    .ToList();

                var sells = orders
                    .Where(x => x.OrderType != null && x.OrderType.Contains("매도"))
                    .OrderBy(x => ParseDbDateTime(x.OrderTime))
                    .ToList();

                if (buys.Count == 0 || sells.Count == 0)
                    return;

                int buyQty = buys.Sum(x => Math.Abs(x.ExecQty));
                int sellQty = sells.Sum(x => Math.Abs(x.ExecQty));

                if (buyQty <= 0 || sellQty <= 0)
                    return;

                int matchedQty = Math.Min(buyQty, sellQty);

                if (matchedQty <= 0)
                    return;

                double buyAmount = CalculateMatchedExecutionAmount(buys, matchedQty);
                double sellAmount = CalculateMatchedExecutionAmount(sells, matchedQty);

                if (buyAmount <= 0 || sellAmount <= 0)
                    return;

                double avgBuy = buyAmount / matchedQty;
                double avgSell = sellAmount / matchedQty;

                double profitAmount = sellAmount - buyAmount;
                double profitRate = avgBuy <= 0 ? 0 : ((avgSell - avgBuy) / avgBuy) * 100.0;

                var firstBuy = buys.First();
                var lastSell = sells.Last();

                DateTime entryTime = ParseDbDateTime(firstBuy.OrderTime);
                DateTime exitTime = ParseDbDateTime(lastSell.OrderTime);

                string exitReason = ResolveExitReason(sells);
                string strategyName = ResolveEntryStrategyName(buys, sells);
                SignalLogRecord tirex = ResolveTiRexForTrade(firstBuy.Code, entryTime);

                var result = new TradeResultRecord
                {
                    TradeResultId = $"TR_{tradeGroupId}",
                    TradeGroupId = tradeGroupId,
                    Code = firstBuy.Code,
                    Name = firstBuy.Name,
                    StrategyName = strategyName,
                    EntryTime = entryTime,
                    ExitTime = exitTime,
                    EntryPrice = avgBuy,
                    ExitPrice = avgSell,
                    Quantity = matchedQty,
                    ProfitRate = profitRate,
                    ProfitAmount = profitAmount,
                    MaxProfitRate = profitRate,
                    MinProfitRate = profitRate,
                    HoldSeconds = Math.Max(0, (exitTime - entryTime).TotalSeconds),
                    Label = profitAmount > 0 ? 1 : profitAmount < 0 ? -1 : 0,
                    ExitReason = exitReason,
                    TradeMode = tirex == null ? null : tirex.TradeMode,
                    TiRexDirection = tirex == null ? null : tirex.TiRexDirection,
                    TiRexConfidence = tirex == null ? 0 : tirex.TiRexConfidence,
                    TiRexPredictedReturn = tirex == null ? 0 : tirex.TiRexPredictedReturn,
                    CreatedAt = DateTime.Now
                };

                InsertTradeResult(result);
            }
        }
        private double CalculateMatchedExecutionAmount(List<OrderInfo> orders, int targetQty)
        {
            if (orders == null || targetQty <= 0)
                return 0;

            int remainQty = targetQty;
            double amount = 0;

            foreach (var order in orders)
            {
                if (order == null || remainQty <= 0)
                    break;

                int qty = Math.Abs(order.ExecQty);
                double price = GetExecutionPrice(order);

                if (qty <= 0 || price <= 0)
                    continue;

                int useQty = Math.Min(remainQty, qty);

                amount += price * useQty;
                remainQty -= useQty;
            }

            return amount;
        }

        private SignalLogRecord ResolveTiRexForTrade(string code, DateTime entryTime)
        {
            if (string.IsNullOrWhiteSpace(code) || entryTime == default(DateTime))
                return null;

            try
            {
                EnsureMlDatabase(entryTime);

                using (var conn = new SQLiteConnection(GetMlConnStr(entryTime)))
                {
                    conn.Open();

                    // 매수 직전/직후 가장 가까운 자동매매 신호의 TiRex 값을 사용한다.
                    // SignalTime은 주문 생성보다 약간 빠르거나 같은 시각일 수 있어 ±10초 범위로 잡는다.
                    return conn.QueryFirstOrDefault<SignalLogRecord>(@"
                        SELECT *
                        FROM SignalLogs
                        WHERE Code = @code
                          AND SignalTime >= @fromTime
                          AND SignalTime <= @toTime
                          AND TiRexDirection IS NOT NULL
                          AND TiRexDirection <> ''
                        ORDER BY ABS(strftime('%s', SignalTime) - strftime('%s', @entryTime)) ASC
                        LIMIT 1;",
                        new
                        {
                            code,
                            entryTime,
                            fromTime = entryTime.AddSeconds(-10),
                            toTime = entryTime.AddSeconds(10)
                        });
                }
            }
            catch
            {
                return null;
            }
        }

        private string ResolveExitReason(List<OrderInfo> sells)
        {
            if (sells == null || sells.Count == 0)
                return "UNKNOWN_EXIT";

            var ordered = sells
                .OrderByDescending(x => ParseDbDateTime(x.OrderTime))
                .ToList();

            foreach (var order in ordered)
            {
                string strategy = NormalizeStrategyName(order.StrategyName);

                if (IsAutoExitStrategy(strategy))
                    return strategy;
            }

            var last = ordered.First();

            string fallback = NormalizeStrategyName(last.StrategyName);

            if (!string.IsNullOrWhiteSpace(fallback) &&
                fallback != "CHEJAN" &&
                fallback != "ORDER_TR")
                return fallback;

            if (!string.IsNullOrWhiteSpace(last.OrderType))
                return last.OrderType;

            return "UNKNOWN_EXIT";
        }
        private string ResolveEntryStrategyName(List<OrderInfo> buys, List<OrderInfo> sells)
        {
            if (buys != null)
            {
                foreach (var order in buys.OrderBy(x => ParseDbDateTime(x.OrderTime)))
                {
                    string strategy = NormalizeStrategyName(order.StrategyName);

                    if (!string.IsNullOrWhiteSpace(strategy) &&
                        strategy != "CHEJAN" &&
                        strategy != "ORDER_TR")
                        return strategy;
                }
            }

            if (sells != null)
            {
                foreach (var order in sells.OrderByDescending(x => ParseDbDateTime(x.OrderTime)))
                {
                    string strategy = NormalizeStrategyName(order.StrategyName);

                    if (IsAutoExitStrategy(strategy))
                        return "AUTO";
                }
            }

            return "ORDER_HISTORY";
        }

        private static string NormalizeStrategyName(string strategyName)
        {
            if (string.IsNullOrWhiteSpace(strategyName))
                return string.Empty;

            return strategyName.Trim();
        }

        private static bool IsAutoExitStrategy(string strategyName)
        {
            if (string.IsNullOrWhiteSpace(strategyName))
                return false;

            return strategyName == "RiskExit" ||
                   strategyName == "EmergencyExit" ||
                   strategyName == "StopLoss" ||
                   strategyName == "Timeout" ||
                   strategyName == "ProfitProtectionExit";
        }

        public void RebuildTradeResultsFromOrders(DateTime tradeDate)
        {
            DateTime targetDate = tradeDate.Date;

            using (var conn = new SQLiteConnection(_mainConnStr))
            {
                conn.Open();

                var orders = conn.Query<OrderInfo>(@"
                    SELECT *
                    FROM Orders
                    WHERE ExecQty > 0
                    ORDER BY Code, OrderTime ASC").ToList();

                var targetGroups = orders
                    .Where(x =>
                        !string.IsNullOrWhiteSpace(x.TradeGroupId) &&
                        ParseDbDateTime(x.OrderTime, targetDate).Date == targetDate)
                    .GroupBy(x => x.TradeGroupId)
                    .Select(g => g.Key)
                    .Distinct()
                    .ToList();

                foreach (var groupId in targetGroups)
                    BuildTradeResultFromOrders(groupId);
            }
        }

        private double GetExecutionPrice(OrderInfo order)
        {
            if (order == null)
                return 0;

            if (order.ExecPrice > 0)
                return order.ExecPrice;

            return order.Price > 0 ? order.Price : 0;
        }

        private DateTime ParseDbDateTime(string value)
        {
            return ParseDbDateTime(value, DateTime.Today);
        }

        private DateTime ParseDbDateTime(string value, DateTime baseDate)
        {
            if (string.IsNullOrWhiteSpace(value))
                return baseDate == default(DateTime) ? DateTime.Now : baseDate;

            value = value.Trim();
            DateTime dt;

            if (DateTime.TryParseExact(
                value,
                "HH:mm:ss",
                null,
                System.Globalization.DateTimeStyles.None,
                out dt))
            {
                return baseDate.Date.Add(dt.TimeOfDay);
            }

            if (DateTime.TryParseExact(
                value,
                "HHmmss",
                null,
                System.Globalization.DateTimeStyles.None,
                out dt))
            {
                return baseDate.Date.Add(dt.TimeOfDay);
            }

            if (DateTime.TryParseExact(
                value,
                "yyyyMMddHHmmss",
                null,
                System.Globalization.DateTimeStyles.None,
                out dt))
            {
                return dt;
            }

            if (DateTime.TryParse(value, out dt))
                return dt;

            return baseDate == default(DateTime) ? DateTime.Now : baseDate;
        }

        public void EnqueueSnapshotOutcomeLabel(SnapshotOutcomeLabelRecord label)
        {
            if (label == null || string.IsNullOrWhiteSpace(label.Code) || string.IsNullOrWhiteSpace(label.SnapshotId)) return;
            _outcomeLabelQueue.Enqueue(label);
        }

        // ============================================================
        // 즉시 Insert 메서드: 수동 저장/테스트/체결내역용
        // ============================================================
        public string GetTradeGroupId(string code)
        {
            using (var conn = new SQLiteConnection(_mainConnStr))
            {
                const string sql = @"
                    SELECT TradeGroupId
                    FROM Orders
                    WHERE Code=@code
                      AND TradeGroupId NOT IN (SELECT TradeGroupId FROM TradeResults WHERE TradeGroupId IS NOT NULL)
                    ORDER BY OrderTime DESC
                    LIMIT 1";

                return conn.QueryFirstOrDefault<string>(sql, new { code })
                    ?? $"TG_{DateTime.Now:yyyyMMddHHmmssfff}_{code}";
            }
        }

        public void InsertOrder(OrderInfo order)
        {
            if (order == null || string.IsNullOrWhiteSpace(order.Code))
                return;

            if (string.IsNullOrWhiteSpace(order.OrderNo))
                order.OrderNo = $"PENDING_{DateTime.Now:yyyyMMddHHmmssfff}_{order.Code}";

            if (string.IsNullOrWhiteSpace(order.TradeGroupId))
                order.TradeGroupId = GetTradeGroupId(order.Code);

            using (var conn = new SQLiteConnection(_mainConnStr))
            {
                const string sql = @"
            INSERT INTO Orders (
                OrderNo, TradeGroupId, OrderTime, Code, Name, OrderType,
                Qty, ExecQty, Price, ExecPrice, Status, StrategyName,
                Fee, Tax, ParentOrderNo, TiRexDirection, TiRexConfidence, TiRexPredictedReturn
            ) VALUES (
                @OrderNo, @TradeGroupId, @OrderTime, @Code, @Name, @OrderType,
                @Qty, @ExecQty, @Price, @ExecPrice, @Status, @StrategyName,
                @Fee, @Tax, @ParentOrderNo, @TiRexDirection, @TiRexConfidence, @TiRexPredictedReturn
            )
            ON CONFLICT(OrderNo) DO UPDATE SET
                OrderTime = excluded.OrderTime,
                Code = excluded.Code,
                Name = excluded.Name,
                OrderType = excluded.OrderType,
                Qty = excluded.Qty,
                ExecQty = excluded.ExecQty,
                Price = excluded.Price,
                ExecPrice = excluded.ExecPrice,
                Status = excluded.Status,
                Fee = excluded.Fee,
                Tax = excluded.Tax,
                ParentOrderNo = excluded.ParentOrderNo,
                TiRexDirection =
                    CASE
                        WHEN excluded.TiRexDirection IS NOT NULL AND excluded.TiRexDirection <> ''
                        THEN excluded.TiRexDirection
                        ELSE Orders.TiRexDirection
                    END,
                TiRexConfidence =
                    CASE
                        WHEN excluded.TiRexConfidence > 0
                        THEN excluded.TiRexConfidence
                        ELSE Orders.TiRexConfidence
                    END,
                TiRexPredictedReturn =
                    CASE
                        WHEN excluded.TiRexPredictedReturn <> 0
                        THEN excluded.TiRexPredictedReturn
                        ELSE Orders.TiRexPredictedReturn
                    END,

                TradeGroupId =
                    CASE
                        WHEN Orders.TradeGroupId IS NULL OR Orders.TradeGroupId = ''
                        THEN excluded.TradeGroupId
                        ELSE Orders.TradeGroupId
                    END,

                StrategyName =
                    CASE
                        WHEN excluded.StrategyName IS NOT NULL
                             AND excluded.StrategyName <> ''
                             AND excluded.StrategyName NOT IN ('CHEJAN', 'ORDER_TR')
                             AND (
                                 Orders.StrategyName IS NULL
                                 OR Orders.StrategyName = ''
                                 OR Orders.StrategyName IN ('CHEJAN', 'ORDER_TR')
                             )
                        THEN excluded.StrategyName

                        WHEN Orders.StrategyName IS NULL OR Orders.StrategyName = ''
                        THEN excluded.StrategyName

                        ELSE Orders.StrategyName
                    END;
        ";

                conn.Execute(sql, order);
            }
        }

        public List<OrderInfo> LoadOrdersForChartMarkers(string code, DateTime date)
        {
            using (var conn = new SQLiteConnection(_mainConnStr))
            {
                const string sql = @"
            SELECT *
            FROM Orders
            WHERE Code = @code
              AND ExecQty > 0
              AND OrderTime >= @fromTime
              AND OrderTime < @toTime
            ORDER BY OrderTime ASC;";

                return conn.Query<OrderInfo>(sql, new
                {
                    code,
                    fromTime = date.Date.ToString("yyyy-MM-dd 00:00:00"),
                    toTime = date.Date.AddDays(1).ToString("yyyy-MM-dd 00:00:00")
                }).ToList();
            }
        }


        public void InsertMinuteCandle(MinuteCandleRecord candle)
        {
            if (candle == null) return;
            using (var conn = new SQLiteConnection(_mainConnStr))
                conn.Execute(SqlInsertMinuteCandle, candle);
        }

        public void InsertFeatureSnapshot(FeatureSnapshotRecord snapshot)
        {
            if (snapshot == null) return;
            EnsureMlDatabase(snapshot.SnapshotTime);
            using (var conn = new SQLiteConnection(GetMlConnStr(snapshot.SnapshotTime)))
                conn.Execute(SqlInsertFeatureSnapshot, snapshot);
        }

        public void InsertSignalLog(SignalLogRecord signal)
        {
            if (signal == null) return;
            EnsureMlDatabase(signal.SignalTime);
            using (var conn = new SQLiteConnection(GetMlConnStr(signal.SignalTime)))
                conn.Execute(SqlInsertSignalLog, signal);
        }

        public void InsertTradeResult(TradeResultRecord result)
        {
            if (result == null) return;
            using (var conn = new SQLiteConnection(_mainConnStr))
                conn.Execute(SqlInsertTradeResult, result);
        }

        public void InsertSnapshotOutcomeLabel(SnapshotOutcomeLabelRecord label)
        {
            if (label == null) return;
            EnsureMlDatabase(label.SnapshotTime);
            using (var conn = new SQLiteConnection(GetMlConnStr(label.SnapshotTime)))
                conn.Execute(SqlInsertSnapshotOutcomeLabel, label);
        }

        // ============================================================
        // 조회 보조: 프로그램 시작 시 -5분 RawTick 복원용
        // ============================================================
        public List<TickData> LoadRecentRawTicks(string code, DateTime fromTime)
        {
            using (var conn = new SQLiteConnection(_rawConnStr))
            {
                const string sql = @"
                    SELECT
                        Code,
                        CAST(Price AS INTEGER) AS Price,
                        Volume,
                        Strength,
                        AskVolume,
                        BidVolume,
                        Side,
                        CreatedAt
                    FROM RawTicks
                    WHERE Code=@code AND CreatedAt >= @fromTime
                    ORDER BY CreatedAt ASC;";

                return conn.Query<TickData>(sql, new
                {
                    code,
                    fromTime = fromTime.ToString("yyyy-MM-dd HH:mm:ss.fff")
                }).ToList();
            }
        }

        public List<MinuteCandleRecord> LoadMinuteCandles(string code, DateTime fromTime)
        {
            using (var conn = new SQLiteConnection(_mainConnStr))
            {
                const string sql = @"
                    SELECT *
                    FROM MinuteCandles
                    WHERE Code=@code AND CandleTime >= @fromTime
                    ORDER BY CandleTime ASC;";

                return conn.Query<MinuteCandleRecord>(sql, new
                {
                    code,
                    fromTime = fromTime.ToString("yyyy-MM-dd HH:mm:ss")
                }).ToList();
            }
        }

        // ============================================================
        // Worker: RawTick / Minute / Feature / Signal / Result 일괄 저장
        // ============================================================
        public void DbSaveWorker()
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;

            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    FlushByCountOnly();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DB 저장 워커 에러] {ex.Message}");
                    OnLog?.Invoke($"[DB 저장 워커 에러] {ex.Message}");
                }

                Thread.Sleep(250);
            }

            try
            {
                FlushAllPending();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DB 최종 Flush 에러] {ex.Message}");
                OnLog?.Invoke($"[DB 최종 Flush 에러] {ex.Message}");
            }
        }
        private void FlushByCountOnly()
        {
            lock (_flushLock)
            {
                DateTime now = DateTime.Now;

                if (_tickQueue.Count >= RawTickBatchSize ||
                    (!_tickQueue.IsEmpty && now - _lastRawTickFlush >= TimedFlushInterval))
                    FlushRawTicks();

                if (_minuteQueue.Count >= MinuteBatchSize ||
                    (!_minuteQueue.IsEmpty && now - _lastMinuteFlush >= TimedFlushInterval))
                    FlushMinuteCandles();

                if (_featureQueue.Count >= FeatureBatchSize ||
                    (!_featureQueue.IsEmpty && now - _lastFeatureFlush >= TimedFlushInterval))
                    FlushFeatureSnapshots();

                if (_signalQueue.Count >= SignalBatchSize ||
                    (!_signalQueue.IsEmpty && now - _lastSignalFlush >= TimedFlushInterval))
                    FlushSignalLogs();

                if (_tradeResultQueue.Count >= TradeResultBatchSize ||
                    (!_tradeResultQueue.IsEmpty && now - _lastTradeResultFlush >= TimedFlushInterval))
                    FlushTradeResults();

                if (_outcomeLabelQueue.Count >= OutcomeLabelBatchSize ||
                    (!_outcomeLabelQueue.IsEmpty && now - _lastOutcomeLabelFlush >= TimedFlushInterval))
                    FlushSnapshotOutcomeLabels();
            }
        }
        public void StopWorker()
        {
            _cts.Cancel();
            FlushAllPending();
        }
        public void FlushAllPending()
        {
            lock (_flushLock)
            {
                while (!_tickQueue.IsEmpty)
                    FlushRawTicks();

                while (!_minuteQueue.IsEmpty)
                    FlushMinuteCandles();

                while (!_featureQueue.IsEmpty)
                    FlushFeatureSnapshots();

                while (!_signalQueue.IsEmpty)
                    FlushSignalLogs();

                while (!_tradeResultQueue.IsEmpty)
                    FlushTradeResults();

                while (!_outcomeLabelQueue.IsEmpty)
                    FlushSnapshotOutcomeLabels();
            }
        }
        private void FlushRawTicks()
        {
            var batch = new List<TickData>(RawTickMaxBatchSize);

            while (_tickQueue.TryDequeue(out var tick) &&
                   batch.Count < RawTickMaxBatchSize)
            {
                batch.Add(tick);
            }

            if (batch.Count == 0)
                return;

            using (var conn = new SQLiteConnection(_rawConnStr))
            {
                conn.Open();

                using (var tran = conn.BeginTransaction())
                {
                    conn.Execute(SqlInsertRawTick, batch, tran);
                    tran.Commit();
                    _lastRawTickFlush = DateTime.Now;
                }
            }
        }
        private void FlushMinuteCandles()
        {
            var batch = new List<MinuteCandleRecord>(MinuteMaxBatchSize);

            while (_minuteQueue.TryDequeue(out var item) &&
                   batch.Count < MinuteMaxBatchSize)
            {
                batch.Add(item);
            }

            if (batch.Count == 0)
                return;

            using (var conn = new SQLiteConnection(_mainConnStr))
            {
                conn.Open();

                using (var tran = conn.BeginTransaction())
                {
                    conn.Execute(SqlInsertMinuteCandle, batch, tran);
                    tran.Commit();
                    _lastMinuteFlush = DateTime.Now;
                }
            }
        }
        private void FlushFeatureSnapshots()
        {
            var batch = new List<FeatureSnapshotRecord>(FeatureMaxBatchSize);

            while (_featureQueue.TryDequeue(out var item) &&
                   batch.Count < FeatureMaxBatchSize)
            {
                batch.Add(item);
            }

            if (batch.Count == 0)
                return;

            foreach (var group in batch.GroupBy(x => (x.SnapshotTime == default(DateTime) ? DateTime.Now : x.SnapshotTime).Date))
            {
                EnsureMlDatabase(group.Key);

                using (var conn = new SQLiteConnection(GetMlConnStr(group.Key)))
                {
                    conn.Open();

                    using (var tran = conn.BeginTransaction())
                    {
                        conn.Execute(SqlInsertFeatureSnapshot, group.ToList(), tran);
                        tran.Commit();
                        _lastFeatureFlush = DateTime.Now;
                    }
                }
            }
        }
        private void FlushSignalLogs()
        {
            var batch = new List<SignalLogRecord>(SignalMaxBatchSize);

            while (_signalQueue.TryDequeue(out var item) &&
                   batch.Count < SignalMaxBatchSize)
            {
                batch.Add(item);
            }

            if (batch.Count == 0)
                return;

            foreach (var group in batch.GroupBy(x => (x.SignalTime == default(DateTime) ? DateTime.Now : x.SignalTime).Date))
            {
                EnsureMlDatabase(group.Key);

                using (var conn = new SQLiteConnection(GetMlConnStr(group.Key)))
                {
                    conn.Open();

                    using (var tran = conn.BeginTransaction())
                    {
                        conn.Execute(SqlInsertSignalLog, group.ToList(), tran);
                        tran.Commit();
                        _lastSignalFlush = DateTime.Now;
                    }
                }
            }
        }
        private void FlushTradeResults()
        {
            var batch = new List<TradeResultRecord>(TradeResultMaxBatchSize);

            while (_tradeResultQueue.TryDequeue(out var item) &&
                   batch.Count < TradeResultMaxBatchSize)
            {
                batch.Add(item);
            }

            if (batch.Count == 0)
                return;

            using (var conn = new SQLiteConnection(_mainConnStr))
            {
                conn.Open();

                using (var tran = conn.BeginTransaction())
                {
                    conn.Execute(SqlInsertTradeResult, batch, tran);
                    tran.Commit();
                    _lastTradeResultFlush = DateTime.Now;
                }
            }
        }


        private void FlushSnapshotOutcomeLabels()
        {
            var batch = new List<SnapshotOutcomeLabelRecord>(OutcomeLabelMaxBatchSize);

            while (_outcomeLabelQueue.TryDequeue(out var item) &&
                   batch.Count < OutcomeLabelMaxBatchSize)
            {
                batch.Add(item);
            }

            if (batch.Count == 0)
                return;

            foreach (var group in batch.GroupBy(x => (x.SnapshotTime == default(DateTime) ? DateTime.Now : x.SnapshotTime).Date))
            {
                EnsureMlDatabase(group.Key);

                using (var conn = new SQLiteConnection(GetMlConnStr(group.Key)))
                {
                    conn.Open();

                    using (var tran = conn.BeginTransaction())
                    {
                        conn.Execute(SqlInsertSnapshotOutcomeLabel, group.ToList(), tran);
                        tran.Commit();
                        _lastOutcomeLabelFlush = DateTime.Now;
                    }
                }
            }
        }

        private const string SqlCreateMlTables = @"
                    CREATE TABLE IF NOT EXISTS FeatureSnapshots (
                        SnapshotId TEXT PRIMARY KEY,
                        Code TEXT NOT NULL,
                        SnapshotTime DATETIME NOT NULL,
                        Price REAL NOT NULL,
                        PriceChangeRate REAL,
                        Strength REAL,
                        StrengthDelta REAL,
                        VolumeAccel REAL,
                        BidAskRatio REAL,
                        BuyPressureScore REAL,
                        SecondTrend TEXT,
                        MinuteTrend TEXT,
                        HourTrend TEXT,
                        Phase TEXT,
                        IsSideways INTEGER DEFAULT 0,
                        TrendStrength REAL DEFAULT 0,
                        VolatilityRate REAL DEFAULT 0,
                        RangePosition REAL DEFAULT 0,
                        TickCount5Min INTEGER,
                        SecondCandleCount INTEGER,
                        MinuteCandleCount INTEGER,
                        SignalAction TEXT,
                        SignalScore INTEGER,
                        StrategyName TEXT,
                        TradeMode TEXT,
                        CreatedAt DATETIME NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_feature_code_time ON FeatureSnapshots (Code, SnapshotTime DESC);

                    CREATE TABLE IF NOT EXISTS SignalLogs (
                        SignalId TEXT PRIMARY KEY,
                        Code TEXT NOT NULL,
                        SignalTime DATETIME NOT NULL,
                        Action TEXT NOT NULL,
                        Score INTEGER,
                        StrategyName TEXT,
                        Reason TEXT,
                        AiUsed INTEGER,
                        AiApproved INTEGER,
                        AiReason TEXT,
                        MlUsed INTEGER,
                        MlApproved INTEGER,
                        MlScore REAL,
                        SnapshotId TEXT,
                        TradeMode TEXT,
                        TiRexDirection TEXT,
                        TiRexConfidence REAL,
                        TiRexPredictedReturn REAL,
                        CreatedAt DATETIME NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_signal_code_time ON SignalLogs (Code, SignalTime DESC);

                    CREATE TABLE IF NOT EXISTS SnapshotOutcomeLabels (
                        OutcomeId TEXT PRIMARY KEY,
                        SnapshotId TEXT NOT NULL,
                        Code TEXT NOT NULL,
                        SnapshotTime DATETIME NOT NULL,
                        LabelTime DATETIME NOT NULL,
                        ReturnAfter30s REAL,
                        ReturnAfter1m REAL,
                        ReturnAfter3m REAL,
                        ReturnAfter5m REAL,
                        ReturnAfter10m REAL,
                        MaxProfitAfter10m REAL,
                        MaxLossAfter10m REAL,
                        HitProfitFirst INTEGER,
                        HitStopFirst INTEGER,
                        ProfitHitSeconds REAL,
                        StopHitSeconds REAL,
                        LabelBuyGood INTEGER,
                        LabelAvoid INTEGER,
                        CreatedAt DATETIME NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_outcome_snapshot ON SnapshotOutcomeLabels (SnapshotId);
                    CREATE INDEX IF NOT EXISTS idx_outcome_code_time ON SnapshotOutcomeLabels (Code, SnapshotTime DESC);
                ";

        private const string SqlInsertRawTick = @"
            INSERT INTO RawTicks (Code, Price, Volume, Strength, AskVolume, BidVolume, Side, CreatedAt)
            VALUES (@Code, @Price, @Volume, @Strength, @AskVolume, @BidVolume, @Side, @CreatedAt);";

        private const string SqlInsertMinuteCandle = @"
            INSERT OR REPLACE INTO MinuteCandles (
                Code, Name, CandleTime, Interval, Open, High, Low, Close,
                Volume, AccumulatedVolume, Strength, TotalAskQty, TotalBidQty,
                Source, CreatedAt
            ) VALUES (
                @Code, @Name, @CandleTime, @Interval, @Open, @High, @Low, @Close,
                @Volume, @AccumulatedVolume, @Strength, @TotalAskQty, @TotalBidQty,
                @Source, @CreatedAt
            );";

        private const string SqlInsertFeatureSnapshot = @"
            INSERT OR REPLACE INTO FeatureSnapshots (
                SnapshotId, Code, SnapshotTime, Price, PriceChangeRate, Strength,
                StrengthDelta, VolumeAccel, BidAskRatio, BuyPressureScore,
                SecondTrend, MinuteTrend, HourTrend, Phase, IsSideways, TrendStrength,
                VolatilityRate, RangePosition, TickCount5Min, SecondCandleCount,
                MinuteCandleCount, SignalAction, SignalScore, StrategyName, TradeMode, CreatedAt
            ) VALUES (
                @SnapshotId, @Code, @SnapshotTime, @Price, @PriceChangeRate, @Strength,
                @StrengthDelta, @VolumeAccel, @BidAskRatio, @BuyPressureScore,
                @SecondTrend, @MinuteTrend, @HourTrend, @Phase, @IsSideways, @TrendStrength,
                @VolatilityRate, @RangePosition, @TickCount5Min, @SecondCandleCount,
                @MinuteCandleCount, @SignalAction, @SignalScore, @StrategyName, @TradeMode, @CreatedAt
            );";

        private const string SqlInsertSignalLog = @"
            INSERT OR REPLACE INTO SignalLogs (
                SignalId, Code, SignalTime, Action, Score, StrategyName, Reason,
                AiUsed, AiApproved, AiReason, MlUsed, MlApproved, MlScore,
                SnapshotId, TradeMode, TiRexDirection, TiRexConfidence, TiRexPredictedReturn, CreatedAt
            ) VALUES (
                @SignalId, @Code, @SignalTime, @Action, @Score, @StrategyName, @Reason,
                @AiUsed, @AiApproved, @AiReason, @MlUsed, @MlApproved, @MlScore,
                @SnapshotId, @TradeMode, @TiRexDirection, @TiRexConfidence, @TiRexPredictedReturn, @CreatedAt
            );";

        private const string SqlInsertTradeResult = @"
            INSERT OR REPLACE INTO TradeResults (
                TradeResultId,
                TradeGroupId,
                Code,
                Name,
                StrategyName,
                TradeMode,
                EntryTime,
                ExitTime,
                EntryPrice,
                ExitPrice,
                Quantity,
                ProfitRate,
                ProfitAmount,
                MaxProfitRate,
                MinProfitRate,
                HoldSeconds,
                Label,
                ExitReason,
                TiRexDirection,
                TiRexConfidence,
                TiRexPredictedReturn,
                CreatedAt
            )
            VALUES (
                @TradeResultId,
                @TradeGroupId,
                @Code,
                @Name,
                @StrategyName,
                @TradeMode,
                @EntryTime,
                @ExitTime,
                @EntryPrice,
                @ExitPrice,
                @Quantity,
                @ProfitRate,
                @ProfitAmount,
                @MaxProfitRate,
                @MinProfitRate,
                @HoldSeconds,
                @Label,
                @ExitReason,
                @TiRexDirection,
                @TiRexConfidence,
                @TiRexPredictedReturn,
                @CreatedAt
            );";


        private const string SqlInsertSnapshotOutcomeLabel = @"
            INSERT OR REPLACE INTO SnapshotOutcomeLabels (
                OutcomeId, SnapshotId, Code, SnapshotTime, LabelTime,
                ReturnAfter30s, ReturnAfter1m, ReturnAfter3m, ReturnAfter5m, ReturnAfter10m,
                MaxProfitAfter10m, MaxLossAfter10m, HitProfitFirst, HitStopFirst,
                ProfitHitSeconds, StopHitSeconds, LabelBuyGood, LabelAvoid, CreatedAt
            ) VALUES (
                @OutcomeId, @SnapshotId, @Code, @SnapshotTime, @LabelTime,
                @ReturnAfter30s, @ReturnAfter1m, @ReturnAfter3m, @ReturnAfter5m, @ReturnAfter10m,
                @MaxProfitAfter10m, @MaxLossAfter10m, @HitProfitFirst, @HitStopFirst,
                @ProfitHitSeconds, @StopHitSeconds, @LabelBuyGood, @LabelAvoid, @CreatedAt
            );";
        // ============================================================
        // Performance 요약 집계
        // - 원본 TradeResults/Orders는 그대로 두고 Performance.db에 일별 요약만 UPSERT한다.
        // - 장중/장마감/종료/다음 실행 복구에서 반복 호출해도 같은 날짜는 덮어쓴다.
        // ============================================================
        public DailyPerformanceRecord BuildAndSaveDailyPerformance(DateTime tradeDate)
        {
            return BuildAndSaveDailyPerformance(tradeDate, rebuildTradeResults: false);
        }

        public DailyPerformanceRecord BuildAndSaveDailyPerformance(
            DateTime tradeDate,
            bool rebuildTradeResults)
        {
            var date = tradeDate.Date;
            string dateKey = date.ToString("yyyyMMdd");

            UpsertPerformanceBuildState(dateKey, "Building", null);

            try
            {
                if (rebuildTradeResults)
                    RebuildTradeResultsFromOrders(date);

                var summary = BuildDailyPerformanceFromMainDb(date);
                UpsertDailyPerformance(summary);
                RebuildAndSaveTotalPerformance();
                UpsertPerformanceBuildState(dateKey, "Completed", null);

                return summary;
            }
            catch (Exception ex)
            {
                UpsertPerformanceBuildState(dateKey, "Failed", ex.Message);
                OnLog?.Invoke("[성과집계 실패] " + dateKey + " / " + ex.Message);

                return new DailyPerformanceRecord
                {
                    TradeDate = dateKey,
                    Status = "Failed",
                    ErrorMessage = ex.Message,
                    UpdatedAt = DateTime.Now,
                    CreatedAt = DateTime.Now
                };
            }
        }

        public void RebuildRecentPerformanceDays(int days)
        {
            if (days <= 0)
                days = 7;

            for (int i = days - 1; i >= 0; i--)
                BuildAndSaveDailyPerformance(DateTime.Today.AddDays(-i), rebuildTradeResults: true);
        }

        public DailyPerformanceRecord GetDailyPerformance(DateTime tradeDate)
        {
            string dateKey = tradeDate.Date.ToString("yyyyMMdd");

            using (var conn = new SQLiteConnection(_performanceConnStr))
            {
                conn.Open();
                return conn.QueryFirstOrDefault<DailyPerformanceRecord>(
                    "SELECT * FROM DailyPerformance WHERE TradeDate=@dateKey",
                    new { dateKey });
            }
        }

        public DailyPerformanceRecord GetCumulativePerformance(DateTime? startDate = null, DateTime? endDate = null)
        {
            // 기본 누적은 Performance.db의 TotalPerformance 1행 캐시를 사용한다.
            // UI에서 매번 TradeResults를 직접 SUM하지 않기 위한 구조다.
            if (!startDate.HasValue && !endDate.HasValue)
            {
                var total = GetSavedTotalPerformance();
                if (total != null)
                    return total;

                return RebuildAndSaveTotalPerformance();
            }

            string startKey = (startDate ?? new DateTime(2000, 1, 1)).Date.ToString("yyyyMMdd");
            string endKey = (endDate ?? DateTime.Today).Date.ToString("yyyyMMdd");

            using (var conn = new SQLiteConnection(_performanceConnStr))
            {
                conn.Open();

                var rows = conn.Query<DailyPerformanceRecord>(@"
                    SELECT *
                    FROM DailyPerformance
                    WHERE TradeDate >= @startKey
                      AND TradeDate <= @endKey
                    ORDER BY TradeDate",
                    new { startKey, endKey }).ToList();

                return AggregateDailyRows(rows, startKey + "~" + endKey);
            }
        }

        public DailyPerformanceRecord RebuildAndSaveTotalPerformance()
        {
            using (var conn = new SQLiteConnection(_performanceConnStr))
            {
                conn.Open();

                var rows = conn.Query<DailyPerformanceRecord>(@"
                    SELECT *
                    FROM DailyPerformance
                    ORDER BY TradeDate").ToList();

                var total = AggregateDailyRows(rows, "TOTAL");
                UpsertTotalPerformance(total);
                return total;
            }
        }

        private DailyPerformanceRecord GetSavedTotalPerformance()
        {
            using (var conn = new SQLiteConnection(_performanceConnStr))
            {
                conn.Open();

                return conn.QueryFirstOrDefault<DailyPerformanceRecord>(@"
                    SELECT
                        'TOTAL' AS TradeDate,
                        TradeCount,
                        WinCount,
                        LossCount,
                        FlatCount,
                        GrossProfit,
                        GrossLoss,
                        FeeTax,
                        NetProfit,
                        AvgProfitRate,
                        WinRate,
                        MaxProfit,
                        MaxLoss,
                        BestTradeRate,
                        WorstTradeRate,
                        'Aggregate' AS Status,
                        NULL AS ErrorMessage,
                        UpdatedAt,
                        CreatedAt
                    FROM TotalPerformance
                    WHERE Id = 1");
            }
        }

        private DailyPerformanceRecord AggregateDailyRows(List<DailyPerformanceRecord> rows, string tradeDate)
        {
            rows = rows ?? new List<DailyPerformanceRecord>();

            var result = new DailyPerformanceRecord
            {
                TradeDate = tradeDate,
                TradeCount = rows.Sum(x => x.TradeCount),
                WinCount = rows.Sum(x => x.WinCount),
                LossCount = rows.Sum(x => x.LossCount),
                FlatCount = rows.Sum(x => x.FlatCount),
                GrossProfit = rows.Sum(x => x.GrossProfit),
                GrossLoss = rows.Sum(x => x.GrossLoss),
                FeeTax = rows.Sum(x => x.FeeTax),
                NetProfit = rows.Sum(x => x.NetProfit),
                MaxProfit = rows.Count == 0 ? 0 : rows.Max(x => x.MaxProfit),
                MaxLoss = rows.Count == 0 ? 0 : rows.Min(x => x.MaxLoss),
                BestTradeRate = rows.Count == 0 ? 0 : rows.Max(x => x.BestTradeRate),
                WorstTradeRate = rows.Count == 0 ? 0 : rows.Min(x => x.WorstTradeRate),
                Status = "Aggregate",
                UpdatedAt = DateTime.Now,
                CreatedAt = DateTime.Now
            };

            result.WinRate = result.TradeCount <= 0 ? 0 : (double)result.WinCount / result.TradeCount * 100.0;

            double weightedRateSum = rows.Sum(x => x.AvgProfitRate * x.TradeCount);
            result.AvgProfitRate = result.TradeCount <= 0 ? 0 : weightedRateSum / result.TradeCount;

            return result;
        }

        private void UpsertTotalPerformance(DailyPerformanceRecord record)
        {
            if (record == null)
                return;

            using (var conn = new SQLiteConnection(_performanceConnStr))
            {
                conn.Open();

                conn.Execute(@"
                    INSERT INTO TotalPerformance (
                        Id, TradeCount, WinCount, LossCount, FlatCount,
                        GrossProfit, GrossLoss, FeeTax, NetProfit,
                        AvgProfitRate, WinRate, MaxProfit, MaxLoss,
                        BestTradeRate, WorstTradeRate,
                        UpdatedAt, CreatedAt
                    )
                    VALUES (
                        1, @TradeCount, @WinCount, @LossCount, @FlatCount,
                        @GrossProfit, @GrossLoss, @FeeTax, @NetProfit,
                        @AvgProfitRate, @WinRate, @MaxProfit, @MaxLoss,
                        @BestTradeRate, @WorstTradeRate,
                        @UpdatedAt, @CreatedAt
                    )
                    ON CONFLICT(Id) DO UPDATE SET
                        TradeCount=excluded.TradeCount,
                        WinCount=excluded.WinCount,
                        LossCount=excluded.LossCount,
                        FlatCount=excluded.FlatCount,
                        GrossProfit=excluded.GrossProfit,
                        GrossLoss=excluded.GrossLoss,
                        FeeTax=excluded.FeeTax,
                        NetProfit=excluded.NetProfit,
                        AvgProfitRate=excluded.AvgProfitRate,
                        WinRate=excluded.WinRate,
                        MaxProfit=excluded.MaxProfit,
                        MaxLoss=excluded.MaxLoss,
                        BestTradeRate=excluded.BestTradeRate,
                        WorstTradeRate=excluded.WorstTradeRate,
                        UpdatedAt=excluded.UpdatedAt;",
                    record);
            }
        }

        public List<PerformanceBuildStateRecord> LoadIncompletePerformanceBuildStates(int recentDays = 7)
        {
            string startKey = DateTime.Today.AddDays(-Math.Max(1, recentDays)).ToString("yyyyMMdd");

            using (var conn = new SQLiteConnection(_performanceConnStr))
            {
                conn.Open();
                return conn.Query<PerformanceBuildStateRecord>(@"
                    SELECT *
                    FROM PerformanceBuildState
                    WHERE TradeDate >= @startKey
                      AND Status <> 'Completed'
                    ORDER BY TradeDate DESC",
                    new { startKey }).ToList();
            }
        }

        private DailyPerformanceRecord BuildDailyPerformanceFromMainDb(DateTime date)
        {
            DateTime start = date.Date;
            DateTime end = start.AddDays(1);
            string dateKey = start.ToString("yyyyMMdd");

            using (var conn = new SQLiteConnection(_mainConnStr))
            {
                conn.Open();

                var tradeRows = conn.Query<TradePerformanceRow>(@"
                                SELECT
                                    CAST(IFNULL(ProfitAmount, 0) AS REAL) AS ProfitAmount,
                                    CAST(IFNULL(ProfitRate, 0) AS REAL) AS ProfitRate
                                FROM TradeResults
                                WHERE EntryTime >= @start
                                  AND EntryTime < @end",
                                new { start, end }).ToList();

                int tradeCount = tradeRows.Count;
                int winCount = tradeRows.Count(x => x.ProfitAmount > 0);
                int lossCount = tradeRows.Count(x => x.ProfitAmount < 0);
                int flatCount = tradeCount - winCount - lossCount;

                Debug.WriteLine($"[성과집계] date={date} count={tradeCount} start={start} end={end}");

                double grossProfit = tradeRows
                    .Where(x => x.ProfitAmount > 0)
                    .Sum(x => x.ProfitAmount);

                double grossLoss = tradeRows
                    .Where(x => x.ProfitAmount < 0)
                    .Sum(x => x.ProfitAmount);

                double avgProfitRate = tradeCount == 0 ? 0 : tradeRows.Average(x => x.ProfitRate);
                double bestTradeRate = tradeCount == 0 ? 0 : tradeRows.Max(x => x.ProfitRate);
                double worstTradeRate = tradeCount == 0 ? 0 : tradeRows.Min(x => x.ProfitRate);
                double maxProfit = tradeCount == 0 ? 0 : tradeRows.Max(x => x.ProfitAmount);
                double maxLoss = tradeCount == 0 ? 0 : tradeRows.Min(x => x.ProfitAmount);

                double feeTax = conn.QueryFirstOrDefault<double>(@"
                    SELECT IFNULL(SUM(IFNULL(Fee, 0) + IFNULL(Tax, 0)), 0)
                    FROM Orders
                    WHERE (
                            OrderTime >= @start
                        AND OrderTime < @end
                    )
                    OR SUBSTR(REPLACE(REPLACE(REPLACE(CAST(OrderTime AS TEXT), '-', ''), ':', ''), ' ', ''), 1, 8) = @dateKey",
                    new { start, end, dateKey });

                return new DailyPerformanceRecord
                {
                    TradeDate = dateKey,
                    TradeCount = tradeCount,
                    WinCount = winCount,
                    LossCount = lossCount,
                    FlatCount = flatCount,
                    GrossProfit = grossProfit,
                    GrossLoss = grossLoss,
                    FeeTax = feeTax,
                    NetProfit = grossProfit + grossLoss - feeTax,
                    AvgProfitRate = avgProfitRate,
                    WinRate = tradeCount == 0 ? 0 : (double)winCount / tradeCount * 100.0,
                    MaxProfit = maxProfit,
                    MaxLoss = maxLoss,
                    BestTradeRate = bestTradeRate,
                    WorstTradeRate = worstTradeRate,
                    Status = "Completed",
                    UpdatedAt = DateTime.Now,
                    CreatedAt = DateTime.Now
                };
            }
        }

        private void UpsertDailyPerformance(DailyPerformanceRecord record)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.TradeDate))
                return;

            using (var conn = new SQLiteConnection(_performanceConnStr))
            {
                conn.Open();
                conn.Execute(@"
                    INSERT INTO DailyPerformance (
                        TradeDate, TradeCount, WinCount, LossCount, FlatCount,
                        GrossProfit, GrossLoss, FeeTax, NetProfit,
                        AvgProfitRate, WinRate, MaxProfit, MaxLoss,
                        BestTradeRate, WorstTradeRate,
                        UpdatedAt, CreatedAt
                    )
                    VALUES (
                        @TradeDate, @TradeCount, @WinCount, @LossCount, @FlatCount,
                        @GrossProfit, @GrossLoss, @FeeTax, @NetProfit,
                        @AvgProfitRate, @WinRate, @MaxProfit, @MaxLoss,
                        @BestTradeRate, @WorstTradeRate,
                        @UpdatedAt, @CreatedAt
                    )
                    ON CONFLICT(TradeDate) DO UPDATE SET
                        TradeCount=excluded.TradeCount,
                        WinCount=excluded.WinCount,
                        LossCount=excluded.LossCount,
                        FlatCount=excluded.FlatCount,
                        GrossProfit=excluded.GrossProfit,
                        GrossLoss=excluded.GrossLoss,
                        FeeTax=excluded.FeeTax,
                        NetProfit=excluded.NetProfit,
                        AvgProfitRate=excluded.AvgProfitRate,
                        WinRate=excluded.WinRate,
                        MaxProfit=excluded.MaxProfit,
                        MaxLoss=excluded.MaxLoss,
                        BestTradeRate=excluded.BestTradeRate,
                        WorstTradeRate=excluded.WorstTradeRate,
                        UpdatedAt=excluded.UpdatedAt;",
                    record);
            }
        }

        private void UpsertPerformanceBuildState(string dateKey, string status, string errorMessage)
        {
            using (var conn = new SQLiteConnection(_performanceConnStr))
            {
                conn.Open();
                conn.Execute(@"
                    INSERT INTO PerformanceBuildState (
                        TradeDate, Status, LastAttemptAt, ErrorMessage, UpdatedAt
                    )
                    VALUES (
                        @TradeDate, @Status, @LastAttemptAt, @ErrorMessage, @UpdatedAt
                    )
                    ON CONFLICT(TradeDate) DO UPDATE SET
                        Status=excluded.Status,
                        LastAttemptAt=excluded.LastAttemptAt,
                        ErrorMessage=excluded.ErrorMessage,
                        UpdatedAt=excluded.UpdatedAt;",
                    new
                    {
                        TradeDate = dateKey,
                        Status = status,
                        LastAttemptAt = DateTime.Now,
                        ErrorMessage = errorMessage,
                        UpdatedAt = DateTime.Now
                    });
            }
        }

    }
}
