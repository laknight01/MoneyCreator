using Dapper;
using MoneyCreator.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Globalization;
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
        public bool DisablePersistenceForReplay { get; set; }

        private bool ShouldSkipPersistence()
        {
            return DisablePersistenceForReplay;
        }

        private static bool IsReplayOnlyProgramSource(string source)
        {
            string value = (source ?? string.Empty).Trim();

            return string.Equals(value, "Replay", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "REPLAY", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "REPLAY_DB", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "DB", StringComparison.OrdinalIgnoreCase);
        }

        private bool ShouldSkipProgramTradePersistence(ProgramTradeInfo trade)
        {
            if (trade == null)
                return true;

            // Replay 중 RawTick/분봉/체결 결과 저장은 막더라도,
            // REALTIME/TR_SEED 프로그램매매 원천 데이터는 Replay 재현용 원천 DB이므로 보존해야 한다.
            // 단, Replay/DB 재생에서 되먹임된 데이터는 다시 저장하지 않는다.
            return DisablePersistenceForReplay && IsReplayOnlyProgramSource(trade.Source);
        }

        private bool ShouldSkipProgramFlowPersistence(ProgramFlowSnapshotRecord snapshot)
        {
            if (snapshot == null)
                return true;

            return DisablePersistenceForReplay && IsReplayOnlyProgramSource(snapshot.SourceState);
        }

        // RawTick은 용량이 커서 날짜별 DB 권장
        private readonly string _rawConnStr = $"Data Source=F:\\Project\\DB\\RawData_{DateTime.Now:yyyyMMdd}.db;Version=3;Journal Mode=Wal;Synchronous=Off;temp_store=2;";

        // 주문/체결/매매결과/차트용 핵심 데이터는 메인 DB
        private readonly string _mainConnStr = @"Data Source=E:\Project\DB\MoneyCreator_Main.db;Version=3;Journal Mode=Wal;Synchronous=Normal;";

        // 일별/누적 성과 요약 전용 DB
        // 원본 TradeResults/Orders를 기준으로 언제든 재집계 가능한 캐시 성격이다.
        private readonly string _performanceConnStr = @"Data Source=E:\Project\DB\Performance.db;Version=3;Journal Mode=Wal;Synchronous=Normal;";

        // 자동매매 판단 후보/OutcomeLabel 누적 분석 전용 DB
        // 날짜별 MLData_yyyyMMdd.db는 기존 호환/상세 보관용으로 유지하고,
        // DecisionCandidates + SnapshotOutcomeLabels는 이 DB에도 이중 저장한다.
        private readonly string _analysisConnStr = @"Data Source=E:\Project\DB\AutoTradeAnalysis.db;Version=3;Journal Mode=Wal;Synchronous=Normal;";
        private readonly object _analysisInitLock = new object();
        // AutoTradeAnalysis.db는 Replay 종료 후처리/DB worker/Replay 재시작 초기화가 동시에 접근할 수 있다.
        // SQLite는 writer가 1개뿐이므로 프로세스 내부 누적 Analysis DB 쓰기 트랜잭션을 직렬화한다.
        private static readonly object _analysisWriteLock = new object();
        private bool _analysisDatabaseInitialized;
        private volatile bool _startupDatabaseInitialized;
        private volatile bool _fullDatabaseInitialized;
        private volatile bool _deferredStartupInitializationStarted;
        private readonly object _startupDatabaseInitLock = new object();
        private readonly object _fullDatabaseInitLock = new object();
        private readonly ConcurrentDictionary<string, bool> _legacyAnalysisImportQueryCache =
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // ML 학습용 대용량 데이터는 날짜별 DB로 분리
        private readonly string _mlDbDirectory = @"E:\Project\DB\ML";
        private readonly object _mlInitLock = new object();
        private readonly ConcurrentDictionary<string, bool> _initializedMlDatabases = new ConcurrentDictionary<string, bool>();

        private const int RawTickBatchSize = 2000;
        private const int RawTickMaxBatchSize = 4000;

        private const int HogaBatchSize = 1000;
        private const int HogaMaxBatchSize = 3000;

        private const int MinuteBatchSize = 500;
        private const int MinuteMaxBatchSize = 1000;

        private const int FeatureBatchSize = 2000;
        private const int FeatureMaxBatchSize = 5000;

        private const int SignalBatchSize = 2000;
        private const int SignalMaxBatchSize = 5000;

        private const int TradeResultBatchSize = 200;
        private const int TradeResultMaxBatchSize = 500;

        private const int OutcomeLabelBatchSize = 2000;
        private const int OutcomeLabelMaxBatchSize = 5000;

        private const int DecisionCandidateBatchSize = 2000;
        private const int DecisionCandidateMaxBatchSize = 5000;

        private const int LocalMlEvaluationBatchSize = 2000;
        private const int LocalMlEvaluationMaxBatchSize = 5000;

        private const int ProgramTradeBatchSize = 500;
        private const int ProgramTradeMaxBatchSize = 2000;
        private const int ProgramFlowBatchSize = 500;
        private const int ProgramFlowMaxBatchSize = 2000;

        private readonly ConcurrentQueue<RawHogaSnapshotRecord> _hogaQueue = new ConcurrentQueue<RawHogaSnapshotRecord>();
        private readonly ConcurrentQueue<MinuteCandleRecord> _minuteQueue = new ConcurrentQueue<MinuteCandleRecord>();
        private readonly ConcurrentQueue<FeatureSnapshotRecord> _featureQueue = new ConcurrentQueue<FeatureSnapshotRecord>();
        private readonly ConcurrentQueue<SignalLogRecord> _signalQueue = new ConcurrentQueue<SignalLogRecord>();
        private readonly ConcurrentQueue<DecisionCandidateRecord> _decisionCandidateQueue = new ConcurrentQueue<DecisionCandidateRecord>();
        private readonly ConcurrentQueue<TradeResultRecord> _tradeResultQueue = new ConcurrentQueue<TradeResultRecord>();
        private readonly ConcurrentQueue<SnapshotOutcomeLabelRecord> _outcomeLabelQueue = new ConcurrentQueue<SnapshotOutcomeLabelRecord>();
        private readonly ConcurrentQueue<LocalMlEvaluationRecord> _localMlEvaluationQueue = new ConcurrentQueue<LocalMlEvaluationRecord>();
        private readonly ConcurrentQueue<ProgramTradeInfo> _programTradeQueue = new ConcurrentQueue<ProgramTradeInfo>();
        private readonly ConcurrentQueue<ProgramFlowSnapshotRecord> _programFlowQueue = new ConcurrentQueue<ProgramFlowSnapshotRecord>();

        private sealed class ReplayMlAnalysisDeleteRequest
        {
            public string Code { get; set; }
            public DateTime TradingDate { get; set; }
            public DateTime FromTime { get; set; }
            public DateTime ToTime { get; set; }
            public DateTime RequestedAt { get; set; }
        }

        // Replay Auto 최초 시작에서 기존 분석데이터를 지울 때도 UI/AutoTrading 경로에서 DB를 직접 열지 않는다.
        // 삭제 요청을 worker 큐에 넣고 DbSaveWorker가 기존 저장 큐와 같은 순서/lock 안에서 처리한다.
        private readonly ConcurrentQueue<ReplayMlAnalysisDeleteRequest> _replayMlAnalysisDeleteQueue =
            new ConcurrentQueue<ReplayMlAnalysisDeleteRequest>();
        private DateTime _lastReplayMlAnalysisDeleteFlush = DateTime.Now;

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly object _flushLock = new object();
        private volatile bool _forceFlushRequested;
        private static readonly TimeSpan TimedFlushInterval = TimeSpan.FromSeconds(30);

        private const int SqliteBusyTimeoutMilliseconds = 5000;
        private const int SqliteWriteRetryCount = 5;
        private const int SqliteWriteRetryBaseDelayMilliseconds = 80;

        private DateTime _lastRawTickFlush = DateTime.Now;
        private DateTime _lastHogaFlush = DateTime.Now;
        private DateTime _lastMinuteFlush = DateTime.Now;
        private DateTime _lastFeatureFlush = DateTime.Now;
        private DateTime _lastSignalFlush = DateTime.Now;
        private DateTime _lastDecisionCandidateFlush = DateTime.Now;
        private DateTime _lastTradeResultFlush = DateTime.Now;
        private DateTime _lastOutcomeLabelFlush = DateTime.Now;
        private DateTime _lastLocalMlEvaluationFlush = DateTime.Now;
        private DateTime _lastProgramTradeFlush = DateTime.Now;
        private DateTime _lastProgramFlowFlush = DateTime.Now;

        public StockDbManager(ConcurrentQueue<TickData> tickQueue)
        {
            _tickQueue = tickQueue ?? new ConcurrentQueue<TickData>();

            // 프로그램 시작 화면 표시 전에는 주문/분봉/체결 결과 등 즉시 필요한 Main DB와 Raw DB만 준비한다.
            // ML/Analysis/Performance DB 스키마 보강은 StartDeferredStartupInitialization()에서 지연 실행한다.
            InitializeStartupDatabase();
        }

        private string GetRawConnStr(DateTime date)
        {
            DateTime day = date == default(DateTime) ? DateTime.Today : date.Date;
            string path = $@"F:\Project\DB\RawData_{day:yyyyMMdd}.db";
            return $"Data Source={path};Version=3;Journal Mode=Wal;Synchronous=Off;temp_store=2;";
        }

        private bool EnsureRawDatabase(DateTime date)
        {
            string connStr = GetRawConnStr(date);
            EnsureDirectoryExists(connStr);

            return ExecuteSqliteSchemaWriteWithRetry(
                connStr,
                conn => conn.Execute(SqlEnsureRawDatabase),
                "EnsureRawDatabase");
        }

        private void InitializeStartupDatabase()
        {
            if (_startupDatabaseInitialized)
                return;

            lock (_startupDatabaseInitLock)
            {
                if (_startupDatabaseInitialized)
                    return;

                EnsureDirectoryExists(_rawConnStr);
                EnsureDirectoryExists(_mainConnStr);

                // RawTick 복원/실시간 저장은 프로그램 시작 직후에도 사용될 수 있어 Raw DB는 최소 준비한다.
                EnsureRawDatabase(DateTime.Now);

                using (var conn = OpenSqliteConnection(_mainConnStr))
                {
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
                        HasAccumulatedVolume INTEGER NOT NULL DEFAULT 0,
                        HasStrength INTEGER NOT NULL DEFAULT 0,
                        HasHoga INTEGER NOT NULL DEFAULT 0,
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
                        GrossProfitAmount REAL DEFAULT 0,
                        GrossProfitRate REAL DEFAULT 0,
                        BuyFee REAL DEFAULT 0,
                        SellFee REAL DEFAULT 0,
                        SellTax REAL DEFAULT 0,
                        TotalFeeTax REAL DEFAULT 0,
                        NetProfitAmount REAL DEFAULT 0,
                        NetProfitRate REAL DEFAULT 0,
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

                _startupDatabaseInitialized = true;
            }
        }

        public void InitializeDatabase()
        {
            InitializeStartupDatabase();

            if (_fullDatabaseInitialized)
                return;

            lock (_fullDatabaseInitLock)
            {
                if (_fullDatabaseInitialized)
                    return;

                EnsureDirectoryExists(_performanceConnStr);
                EnsureDirectoryExists(_analysisConnStr);
                EnsureMlDatabase(DateTime.Now);
                EnsureAnalysisDatabase();
                InitializePerformanceDatabase();
                _fullDatabaseInitialized = true;
            }
        }

        public void StartDeferredStartupInitialization()
        {
            if (_fullDatabaseInitialized)
                return;

            lock (_fullDatabaseInitLock)
            {
                if (_deferredStartupInitializationStarted || _fullDatabaseInitialized)
                    return;

                _deferredStartupInitializationStarted = true;
            }

            Task.Run(() =>
            {
                try
                {
                    InitializeDatabase();
                }
                catch (Exception ex)
                {
                    lock (_fullDatabaseInitLock)
                    {
                        if (!_fullDatabaseInitialized)
                            _deferredStartupInitializationStarted = false;
                    }

                    Debug.WriteLine("[DB 지연 초기화 실패] " + ex.Message);
                    OnLog?.Invoke("[DB 지연 초기화 실패] " + ex.Message);
                }
            });
        }



        private void InitializePerformanceDatabase()
        {
            EnsureDirectoryExists(_performanceConnStr);

            ExecuteSqliteSchemaWriteWithRetry(
                _performanceConnStr,
                conn => conn.Execute(@"
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
                "),
                "InitializePerformanceDatabase");
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

            EnsureColumnExists(conn, "TradeResults", "GrossProfitAmount", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "TradeResults", "GrossProfitRate", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "TradeResults", "BuyFee", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "TradeResults", "SellFee", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "TradeResults", "SellTax", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "TradeResults", "TotalFeeTax", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "TradeResults", "NetProfitAmount", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "TradeResults", "NetProfitRate", "REAL DEFAULT 0");
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
                    EnsureLocalMlEvaluationColumns(conn);
                    EnsureSnapshotOutcomeLabelCostColumns(conn);
                    EnsureMlSourceColumns(conn);
                    EnsureDecisionCandidateColumns(conn);
                    EnsureSnapshotOutcomeDecisionColumns(conn);
                    EnsureDecisionAnalysisIndexes(conn);
                }

                _initializedMlDatabases[key] = true;
            }
        }
        private void EnsureAnalysisDatabase()
        {
            if (_analysisDatabaseInitialized)
                return;

            lock (_analysisInitLock)
            {
                if (_analysisDatabaseInitialized)
                    return;

                EnsureDirectoryExists(_analysisConnStr);

                using (var conn = new SQLiteConnection(_analysisConnStr))
                {
                    conn.Open();
                    conn.Execute(SqlCreateAnalysisTables);
                    EnsureDecisionCandidateColumns(conn);
                    EnsureSnapshotOutcomeLabelCostColumns(conn);
                    EnsureSnapshotOutcomeDecisionColumns(conn);
                    EnsureSnapshotOutcomeAnalysisSourceColumns(conn);
                    EnsureDecisionAnalysisIndexes(conn);

                    // 누적 Analysis DB는 DecisionCandidate 기반 분석 전용이다.
                    // 기존/전환 중 유입된 CandidateId 없는 라벨과 orphan 라벨은 분석 오염을 막기 위해 정리한다.
                    CleanupAnalysisOutcomeLabelIntegrity(conn);
                }

                _analysisDatabaseInitialized = true;
            }
        }

        private void EnsureSnapshotOutcomeAnalysisSourceColumns(SQLiteConnection conn)
        {
            if (conn == null)
                return;

            EnsureColumnExists(conn, "SnapshotOutcomeLabels", "DataSourceType", "TEXT DEFAULT 'Real'");
            EnsureColumnExists(conn, "SnapshotOutcomeLabels", "IsReplay", "INTEGER DEFAULT 0");
            EnsureColumnExists(conn, "SnapshotOutcomeLabels", "MlSessionId", "TEXT");
            EnsureColumnExists(conn, "SnapshotOutcomeLabels", "CreatedAt", "DATETIME");
        }

        private void EnsureSignalLogTiRexColumns(SQLiteConnection conn)
        {
            if (conn == null)
                return;

            EnsureColumnExists(conn, "SignalLogs", "TradeMode", "TEXT");
            EnsureColumnExists(conn, "SignalLogs", "TiRexDirection", "TEXT");
            EnsureColumnExists(conn, "SignalLogs", "TiRexConfidence", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "SignalLogs", "TiRexPredictedReturn", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "SignalLogs", "LocalMlUsed", "INTEGER DEFAULT 0");
            EnsureColumnExists(conn, "SignalLogs", "LocalMlAvailable", "INTEGER DEFAULT 0");
            EnsureColumnExists(conn, "SignalLogs", "LocalMlAppliedToTrading", "INTEGER DEFAULT 0");
            EnsureColumnExists(conn, "SignalLogs", "LocalMlScore", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "SignalLogs", "LocalMlConfidence", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "SignalLogs", "LocalMlInfluenceRatio", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "SignalLogs", "LocalMlDirection", "TEXT");
            EnsureColumnExists(conn, "SignalLogs", "LocalMlReason", "TEXT");
        }

        private void EnsureLocalMlEvaluationColumns(SQLiteConnection conn)
        {
            if (conn == null)
                return;

            EnsureColumnExists(conn, "LocalMlEvaluationLogs", "CandidateId", "TEXT");
            EnsureColumnExists(conn, "LocalMlEvaluationLogs", "ModelKey", "TEXT");
            EnsureColumnExists(conn, "LocalMlEvaluationLogs", "ModelName", "TEXT");
            EnsureColumnExists(conn, "LocalMlEvaluationLogs", "ModelVersion", "TEXT");
            EnsureColumnExists(conn, "LocalMlEvaluationLogs", "EvaluatorType", "TEXT");
            EnsureColumnExists(conn, "LocalMlEvaluationLogs", "FeatureSchemaVersion", "TEXT");
            EnsureColumnExists(conn, "LocalMlEvaluationLogs", "RuntimeMode", "TEXT");
            EnsureColumnExists(conn, "LocalMlEvaluationLogs", "IsEnabled", "INTEGER DEFAULT 0");
            EnsureColumnExists(conn, "LocalMlEvaluationLogs", "IsAvailable", "INTEGER DEFAULT 0");
            EnsureColumnExists(conn, "LocalMlEvaluationLogs", "IsAppliedToTrading", "INTEGER DEFAULT 0");
            EnsureColumnExists(conn, "LocalMlEvaluationLogs", "InfluenceRatio", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "LocalMlEvaluationLogs", "RuleAction", "TEXT");
            EnsureColumnExists(conn, "LocalMlEvaluationLogs", "FinalAction", "TEXT");
            EnsureColumnExists(conn, "LocalMlEvaluationLogs", "RuleScore", "INTEGER DEFAULT 0");
            EnsureColumnExists(conn, "LocalMlEvaluationLogs", "FinalScore", "INTEGER DEFAULT 0");
            EnsureColumnExists(conn, "LocalMlEvaluationLogs", "StrategyName", "TEXT");
            EnsureColumnExists(conn, "LocalMlEvaluationLogs", "TradeMode", "TEXT");
            EnsureColumnExists(conn, "LocalMlEvaluationLogs", "Direction", "TEXT");
            EnsureColumnExists(conn, "LocalMlEvaluationLogs", "Score", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "LocalMlEvaluationLogs", "Confidence", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "LocalMlEvaluationLogs", "BuyQualityScore", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "LocalMlEvaluationLogs", "SellRiskScore", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "LocalMlEvaluationLogs", "AvoidScore", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "LocalMlEvaluationLogs", "ExpectedReturn", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "LocalMlEvaluationLogs", "ExpectedDrawdown", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "LocalMlEvaluationLogs", "Reason", "TEXT");
            conn.Execute("CREATE INDEX IF NOT EXISTS idx_local_ml_candidate ON LocalMlEvaluationLogs (CandidateId);");
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
            EnsureColumnExists(conn, "FeatureSnapshots", "LocalMlUsed", "INTEGER DEFAULT 0");
            EnsureColumnExists(conn, "FeatureSnapshots", "LocalMlAvailable", "INTEGER DEFAULT 0");
            EnsureColumnExists(conn, "FeatureSnapshots", "LocalMlAppliedToTrading", "INTEGER DEFAULT 0");
            EnsureColumnExists(conn, "FeatureSnapshots", "LocalMlScore", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "FeatureSnapshots", "LocalMlConfidence", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "FeatureSnapshots", "LocalMlInfluenceRatio", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "FeatureSnapshots", "LocalMlDirection", "TEXT");
            EnsureColumnExists(conn, "FeatureSnapshots", "LocalMlReason", "TEXT");
            EnsureColumnExists(conn, "FeatureSnapshots", "EstimatedRoundTripCostRate", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "FeatureSnapshots", "NetProfitRate", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "FeatureSnapshots", "NetMaxProfitRate", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "FeatureSnapshots", "NetDrawdownRate", "REAL DEFAULT 0");

            EnsureSignalLogTiRexColumns(conn);
        }

        private void EnsureSnapshotOutcomeLabelCostColumns(SQLiteConnection conn)
        {
            if (conn == null)
                return;

            EnsureColumnExists(conn, "SnapshotOutcomeLabels", "EstimatedRoundTripCostRate", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "SnapshotOutcomeLabels", "NetReturnAfter30s", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "SnapshotOutcomeLabels", "NetReturnAfter1m", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "SnapshotOutcomeLabels", "NetReturnAfter3m", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "SnapshotOutcomeLabels", "NetReturnAfter5m", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "SnapshotOutcomeLabels", "NetReturnAfter10m", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "SnapshotOutcomeLabels", "NetMaxProfitAfter10m", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "SnapshotOutcomeLabels", "NetMaxLossAfter10m", "REAL DEFAULT 0");
        }

        private void EnsureDecisionCandidateColumns(SQLiteConnection conn)
        {
            if (conn == null)
                return;

            EnsureColumnExists(conn, "DecisionCandidates", "CandidateId", "TEXT");
            EnsureColumnExists(conn, "DecisionCandidates", "SnapshotId", "TEXT");
            EnsureColumnExists(conn, "DecisionCandidates", "SignalId", "TEXT");
            EnsureColumnExists(conn, "DecisionCandidates", "Code", "TEXT");
            EnsureColumnExists(conn, "DecisionCandidates", "DecisionTime", "DATETIME");
            EnsureColumnExists(conn, "DecisionCandidates", "Price", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "DecisionCandidates", "ActionCandidate", "TEXT");
            EnsureColumnExists(conn, "DecisionCandidates", "FinalAction", "TEXT");
            EnsureColumnExists(conn, "DecisionCandidates", "SignalScore", "INTEGER DEFAULT 0");
            EnsureColumnExists(conn, "DecisionCandidates", "StrategyName", "TEXT");
            EnsureColumnExists(conn, "DecisionCandidates", "TradeMode", "TEXT");
            EnsureColumnExists(conn, "DecisionCandidates", "BaseFinal", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "DecisionCandidates", "EntryScore", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "DecisionCandidates", "ExitScore", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "DecisionCandidates", "RawAccelerationScore", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "DecisionCandidates", "AppliedAccelerationScore", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "DecisionCandidates", "EventMaturity", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "DecisionCandidates", "PairCompletedCount", "INTEGER DEFAULT 0");
            EnsureColumnExists(conn, "DecisionCandidates", "PairOppositionPenalty", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "DecisionCandidates", "DecisionEventKey", "TEXT");
            EnsureColumnExists(conn, "DecisionCandidates", "Reason", "TEXT");
            EnsureColumnExists(conn, "DecisionCandidates", "DataSourceType", "TEXT DEFAULT 'Real'");
            EnsureColumnExists(conn, "DecisionCandidates", "IsReplay", "INTEGER DEFAULT 0");
            EnsureColumnExists(conn, "DecisionCandidates", "MlSessionId", "TEXT");
            EnsureColumnExists(conn, "DecisionCandidates", "CreatedAt", "DATETIME");
        }

        private void EnsureDecisionAnalysisIndexes(SQLiteConnection conn)
        {
            if (conn == null)
                return;

            conn.Execute(@"
                CREATE INDEX IF NOT EXISTS idx_decision_candidates_replay_code_time_cid
                    ON DecisionCandidates(IsReplay, Code, DecisionTime, CandidateId);
                CREATE INDEX IF NOT EXISTS idx_decision_candidates_cid
                    ON DecisionCandidates(CandidateId);
                CREATE INDEX IF NOT EXISTS idx_decision_candidates_strategy
                    ON DecisionCandidates(IsReplay, Code, StrategyName, DecisionTime);
                CREATE INDEX IF NOT EXISTS idx_decision_candidates_strategy_action
                    ON DecisionCandidates(IsReplay, Code, StrategyName, ActionCandidate, FinalAction, DecisionTime);
                CREATE INDEX IF NOT EXISTS idx_outcome_labels_replay_cid
                    ON SnapshotOutcomeLabels(IsReplay, CandidateId);
                CREATE INDEX IF NOT EXISTS idx_outcome_labels_cid
                    ON SnapshotOutcomeLabels(CandidateId);
                CREATE INDEX IF NOT EXISTS idx_outcome_labels_replay_action_hit
                    ON SnapshotOutcomeLabels(IsReplay, ActionCandidate, HitProfitFirst, HitStopFirst);
            ");
        }

        private void EnsureSnapshotOutcomeDecisionColumns(SQLiteConnection conn)
        {
            if (conn == null)
                return;

            EnsureColumnExists(conn, "SnapshotOutcomeLabels", "CandidateId", "TEXT");
            EnsureColumnExists(conn, "SnapshotOutcomeLabels", "ActionCandidate", "TEXT");
            EnsureColumnExists(conn, "SnapshotOutcomeLabels", "FinalAction", "TEXT");
            EnsureColumnExists(conn, "SnapshotOutcomeLabels", "ActionReturnAfter30s", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "SnapshotOutcomeLabels", "ActionReturnAfter1m", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "SnapshotOutcomeLabels", "ActionReturnAfter3m", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "SnapshotOutcomeLabels", "ActionReturnAfter5m", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "SnapshotOutcomeLabels", "ActionReturnAfter10m", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "SnapshotOutcomeLabels", "ActionMfeAfter10m", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "SnapshotOutcomeLabels", "ActionMaeAfter10m", "REAL DEFAULT 0");
            EnsureColumnExists(conn, "SnapshotOutcomeLabels", "CostAdjustedOutcome", "REAL DEFAULT 0");

            // Action 방향 기준의 MFE/MAE 선후관계 분석용 기존 컬럼을 누락 DB에도 보강한다.
            // 별도 모델 속성 추가 없이 기존 HitProfitFirst/HitStopFirst/ProfitHitSeconds/StopHitSeconds를
            // Action 기준으로 계산하도록 AutoTradingViewModel에서 보정한다.
            EnsureColumnExists(conn, "SnapshotOutcomeLabels", "HitProfitFirst", "INTEGER DEFAULT 0");
            EnsureColumnExists(conn, "SnapshotOutcomeLabels", "HitStopFirst", "INTEGER DEFAULT 0");
            EnsureColumnExists(conn, "SnapshotOutcomeLabels", "ProfitHitSeconds", "REAL");
            EnsureColumnExists(conn, "SnapshotOutcomeLabels", "StopHitSeconds", "REAL");
        }

        private void EnsureMlSourceColumns(SQLiteConnection conn)
        {
            if (conn == null)
                return;

            EnsureColumnExists(conn, "FeatureSnapshots", "DataSourceType", "TEXT DEFAULT 'Real'");
            EnsureColumnExists(conn, "FeatureSnapshots", "IsReplay", "INTEGER DEFAULT 0");
            EnsureColumnExists(conn, "FeatureSnapshots", "MlSessionId", "TEXT");

            EnsureColumnExists(conn, "SignalLogs", "DataSourceType", "TEXT DEFAULT 'Real'");
            EnsureColumnExists(conn, "SignalLogs", "IsReplay", "INTEGER DEFAULT 0");
            EnsureColumnExists(conn, "SignalLogs", "MlSessionId", "TEXT");

            EnsureColumnExists(conn, "LocalMlEvaluationLogs", "DataSourceType", "TEXT DEFAULT 'Real'");
            EnsureColumnExists(conn, "LocalMlEvaluationLogs", "IsReplay", "INTEGER DEFAULT 0");
            EnsureColumnExists(conn, "LocalMlEvaluationLogs", "MlSessionId", "TEXT");

            EnsureColumnExists(conn, "SnapshotOutcomeLabels", "DataSourceType", "TEXT DEFAULT 'Real'");
            EnsureColumnExists(conn, "SnapshotOutcomeLabels", "IsReplay", "INTEGER DEFAULT 0");
            EnsureColumnExists(conn, "SnapshotOutcomeLabels", "MlSessionId", "TEXT");
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

        private static SQLiteConnection OpenSqliteConnection(string connectionString)
        {
            var conn = new SQLiteConnection(connectionString);
            conn.Open();
            ApplySqliteBusyTimeout(conn);
            return conn;
        }

        private static void ApplySqliteBusyTimeout(SQLiteConnection conn)
        {
            if (conn == null)
                return;

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA busy_timeout={SqliteBusyTimeoutMilliseconds};";
                cmd.ExecuteNonQuery();
            }
        }

        private bool ExecuteSqliteSchemaWriteWithRetry(
            string connectionString,
            Action<SQLiteConnection> action,
            string operationName)
        {
            for (int attempt = 1; attempt <= SqliteWriteRetryCount; attempt++)
            {
                try
                {
                    using (var conn = OpenSqliteConnection(connectionString))
                    {
                        action(conn);
                    }

                    return true;
                }
                catch (SQLiteException ex)
                {
                    if (!IsDatabaseLocked(ex))
                        throw;

                    if (attempt >= SqliteWriteRetryCount)
                    {
                        LogSqliteLockFailure(operationName, ex, attempt);
                        return false;
                    }

                    Thread.Sleep(SqliteWriteRetryBaseDelayMilliseconds * attempt);
                }
            }

            return false;
        }

        private bool ExecuteSqliteTransactionWithRetry(
            string connectionString,
            Action<SQLiteConnection, SQLiteTransaction> action,
            string operationName)
        {
            for (int attempt = 1; attempt <= SqliteWriteRetryCount; attempt++)
            {
                try
                {
                    using (var conn = OpenSqliteConnection(connectionString))
                    using (var tran = conn.BeginTransaction())
                    {
                        action(conn, tran);
                        tran.Commit();
                    }

                    return true;
                }
                catch (SQLiteException ex)
                {
                    if (!IsDatabaseLocked(ex))
                        throw;

                    if (attempt >= SqliteWriteRetryCount)
                    {
                        LogSqliteLockFailure(operationName, ex, attempt);
                        return false;
                    }

                    Thread.Sleep(SqliteWriteRetryBaseDelayMilliseconds * attempt);
                }
            }

            return false;
        }

        private bool ExecuteAnalysisSqliteTransactionWithRetry(
            Action<SQLiteConnection, SQLiteTransaction> action,
            string operationName)
        {
            // 같은 프로세스 안에서 AutoTradeAnalysis.db 쓰기 트랜잭션이 겹치면
            // BeginTransaction 단계에서 database is locked가 발생한다.
            // Retry만으로는 UI/Background/DB worker가 동시에 진입하는 경합을 줄이지 못하므로
            // 누적 Analysis DB 쓰기는 이 정적 lock으로 먼저 직렬화한다.
            lock (_analysisWriteLock)
            {
                return ExecuteSqliteTransactionWithRetry(_analysisConnStr, action, operationName);
            }
        }

        private static bool IsDatabaseLocked(SQLiteException ex)
        {
            string message = ex == null ? string.Empty : ex.Message ?? string.Empty;

            return message.IndexOf("database is locked", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("database is busy", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (message.IndexOf("database", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    message.IndexOf("locked", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private void LogSqliteLockFailure(string operationName, Exception ex, int attempt)
        {
            string message = $"[DB 저장 지연] {operationName} / locked / retry={attempt}/{SqliteWriteRetryCount} / {ex.Message}";
            Debug.WriteLine(message);
            OnLog?.Invoke(message);
        }

        private void RequeueRawTicks(IEnumerable<TickData> batch)
        {
            if (batch == null)
                return;

            foreach (var tick in batch)
            {
                if (tick != null && !string.IsNullOrWhiteSpace(tick.Code))
                    _tickQueue.Enqueue(tick);
            }
        }

        private void RequeueHogas(IEnumerable<RawHogaSnapshotRecord> batch)
        {
            if (batch == null)
                return;

            foreach (var hoga in batch)
            {
                if (hoga != null && !string.IsNullOrWhiteSpace(hoga.Code))
                    _hogaQueue.Enqueue(hoga);
            }
        }
        private static void RequeueItems<T>(ConcurrentQueue<T> queue, IEnumerable<T> batch)
        {
            if (queue == null || batch == null)
                return;

            foreach (var item in batch)
            {
                if (item != null)
                    queue.Enqueue(item);
            }
        }


        // ============================================================
        // Queue 등록 메서드: 실시간 경로에서는 이쪽 사용 권장
        // ============================================================
        public void EnqueueRawTick(TickData tick)
        {
            if (ShouldSkipPersistence())
                return;

            if (tick == null || string.IsNullOrWhiteSpace(tick.Code)) return;
            _tickQueue.Enqueue(tick);
        }

        public void EnqueueHoga(HogaData hoga)
        {
            if (ShouldSkipPersistence())
                return;

            var record = ToRawHogaSnapshotRecord(hoga, DateTime.Now);
            if (record == null)
                return;

            _hogaQueue.Enqueue(record);
        }

        public void EnqueueMinuteCandle(MinuteCandleRecord candle)
        {
            if (ShouldSkipPersistence())
                return;

            if (candle == null || string.IsNullOrWhiteSpace(candle.Code)) return;
            _minuteQueue.Enqueue(candle);
        }

        public void EnqueueMinuteCandles(IEnumerable<MinuteCandleRecord> candles)
        {
            if (ShouldSkipPersistence())
                return;

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

        public void EnqueueDecisionCandidate(DecisionCandidateRecord candidate)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.Code) || string.IsNullOrWhiteSpace(candidate.CandidateId)) return;
            _decisionCandidateQueue.Enqueue(candidate);
        }

        public void EnqueueLocalMlEvaluation(LocalMlEvaluationRecord record)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.Code)) return;
            _localMlEvaluationQueue.Enqueue(record);
        }

        public void EnqueueProgramTrade(ProgramTradeInfo trade)
        {
            if (ShouldSkipProgramTradePersistence(trade))
                return;

            if (trade == null || string.IsNullOrWhiteSpace(trade.Code))
                return;

            if (trade.TradeTime == default(DateTime))
                trade.TradeTime = DateTime.Now;

            if (trade.CreatedAt == default(DateTime))
                trade.CreatedAt = DateTime.Now;

            _programTradeQueue.Enqueue(trade);
        }

        public void EnqueueProgramTrades(IEnumerable<ProgramTradeInfo> trades)
        {
            if (trades == null)
                return;

            foreach (var trade in trades)
                EnqueueProgramTrade(trade);
        }

        public void EnqueueProgramFlowSnapshot(ProgramFlowSnapshotRecord snapshot)
        {
            if (ShouldSkipProgramFlowPersistence(snapshot))
                return;

            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Code))
                return;

            if (snapshot.SnapshotTime == default(DateTime))
                snapshot.SnapshotTime = DateTime.Now;

            if (snapshot.CreatedAt == default(DateTime))
                snapshot.CreatedAt = DateTime.Now;

            _programFlowQueue.Enqueue(snapshot);
        }

        public void EnqueueTradeResult(TradeResultRecord result)
        {
            if (ShouldSkipPersistence())
                return;

            if (result == null || string.IsNullOrWhiteSpace(result.Code)) return;
            _tradeResultQueue.Enqueue(result);
        }

        public void BuildTradeResultFromOrders(string tradeGroupId)
        {
            if (ShouldSkipPersistence())
                return;

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

                double buyAmount;
                double buyFee;
                double buyTax;
                double sellAmount;
                double sellFee;
                double sellTax;

                CalculateMatchedExecutionCost(buys, matchedQty, out buyAmount, out buyFee, out buyTax);
                CalculateMatchedExecutionCost(sells, matchedQty, out sellAmount, out sellFee, out sellTax);

                if (buyAmount <= 0 || sellAmount <= 0)
                    return;

                double avgBuy = buyAmount / matchedQty;
                double avgSell = sellAmount / matchedQty;

                double grossProfitAmount = sellAmount - buyAmount;
                double grossProfitRate = buyAmount <= 0 ? 0 : grossProfitAmount / buyAmount * 100.0;
                double totalFeeTax = buyFee + buyTax + sellFee + sellTax;
                double buyCost = buyAmount + buyFee + buyTax;
                double sellNet = sellAmount - sellFee - sellTax;
                double netProfitAmount = sellNet - buyCost;
                double netProfitRate = buyCost <= 0 ? 0 : netProfitAmount / buyCost * 100.0;

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
                    ProfitRate = netProfitRate,
                    ProfitAmount = netProfitAmount,
                    MaxProfitRate = netProfitRate,
                    MinProfitRate = netProfitRate,
                    HoldSeconds = Math.Max(0, (exitTime - entryTime).TotalSeconds),
                    GrossProfitAmount = grossProfitAmount,
                    GrossProfitRate = grossProfitRate,
                    BuyFee = buyFee + buyTax,
                    SellFee = sellFee,
                    SellTax = sellTax,
                    TotalFeeTax = totalFeeTax,
                    NetProfitAmount = netProfitAmount,
                    NetProfitRate = netProfitRate,
                    Label = netProfitAmount > 0 ? 1 : netProfitAmount < 0 ? -1 : 0,
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

        private void CalculateMatchedExecutionCost(
            List<OrderInfo> orders,
            int targetQty,
            out double amount,
            out double fee,
            out double tax)
        {
            amount = 0;
            fee = 0;
            tax = 0;

            if (orders == null || targetQty <= 0)
                return;

            int remainQty = targetQty;

            foreach (var order in orders)
            {
                if (order == null || remainQty <= 0)
                    break;

                int qty = Math.Abs(order.ExecQty);
                double price = GetExecutionPrice(order);

                if (qty <= 0 || price <= 0)
                    continue;

                int useQty = Math.Min(remainQty, qty);
                double ratio = qty <= 0 ? 0 : (double)useQty / qty;

                amount += price * useQty;
                fee += Math.Max(0, order.Fee) * ratio;
                tax += Math.Max(0, order.Tax) * ratio;
                remainQty -= useQty;
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
            if (ShouldSkipPersistence())
                return;

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
            if (ShouldSkipPersistence())
                return;

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
            if (ShouldSkipPersistence())
                return;

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

        public void InsertDecisionCandidate(DecisionCandidateRecord candidate)
        {
            if (candidate == null) return;

            // 기존 날짜별 ML DB 저장은 유지한다.
            EnsureMlDatabase(candidate.DecisionTime);
            using (var conn = new SQLiteConnection(GetMlConnStr(candidate.DecisionTime)))
                conn.Execute(SqlInsertDecisionCandidate, candidate);

            // 누적 분석 DB에도 이중 저장한다. 실패해도 기존 저장 경로는 깨지지 않게 로그만 남긴다.
            InsertAnalysisDecisionCandidates(new[] { candidate });
        }

        public void InsertLocalMlEvaluation(LocalMlEvaluationRecord record)
        {
            if (record == null) return;
            EnsureMlDatabase(record.EvaluationTime);
            using (var conn = new SQLiteConnection(GetMlConnStr(record.EvaluationTime)))
                conn.Execute(SqlInsertLocalMlEvaluation, record);
        }

        public void InsertTradeResult(TradeResultRecord result)
        {
            if (ShouldSkipPersistence())
                return;

            if (result == null) return;
            using (var conn = new SQLiteConnection(_mainConnStr))
                conn.Execute(SqlInsertTradeResult, result);
        }

        public void InsertSnapshotOutcomeLabel(SnapshotOutcomeLabelRecord label)
        {
            if (label == null) return;

            // 기존 날짜별 ML DB 저장은 유지한다.
            EnsureMlDatabase(label.SnapshotTime);
            using (var conn = new SQLiteConnection(GetMlConnStr(label.SnapshotTime)))
                conn.Execute(SqlInsertSnapshotOutcomeLabel, label);

            // 누적 분석 DB에도 이중 저장한다. 실패해도 기존 저장 경로는 깨지지 않게 로그만 남긴다.
            InsertAnalysisSnapshotOutcomeLabels(new[] { label });
        }

        // ============================================================
        // 조회 보조: 프로그램 시작 시 -5분 RawTick 복원용
        // ============================================================
        public List<TickData> LoadRecentRawTicks(string code, DateTime fromTime)
        {
            using (var conn = OpenSqliteConnection(_rawConnStr))
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


        public sealed class ReplayRawTickCoverage
        {
            public string Code { get; set; }
            public DateTime TradingDate { get; set; }
            public string DatabasePath { get; set; }
            public bool DatabaseExists { get; set; }
            public bool TableExists { get; set; }
            public long Count { get; set; }
            public long ValidSideCount { get; set; }
            public DateTime FirstTime { get; set; }
            public DateTime LastTime { get; set; }
            public string ErrorMessage { get; set; }

            public bool IsUsable =>
                DatabaseExists &&
                TableExists &&
                Count > 0 &&
                ValidSideCount == Count &&
                FirstTime != DateTime.MinValue &&
                LastTime != DateTime.MinValue;
        }

        public sealed class ReplayRawTickPageRecord
        {
            public long Id { get; set; }
            public string Code { get; set; }
            public int Price { get; set; }
            public int Volume { get; set; }
            public double Strength { get; set; }
            public long BidVolume { get; set; }
            public long AskVolume { get; set; }
            public string Side { get; set; }
            public DateTime CreatedAt { get; set; }
        }

        public ReplayRawTickCoverage GetReplayRawTickCoverage(
            string code,
            DateTime tradingDate,
            DateTime fromTime,
            DateTime toTime)
        {
            var day = tradingDate == DateTime.MinValue ? DateTime.Today : tradingDate.Date;
            string normalizedCode = NormalizeReplayRawTickCode(code);
            string prefixedCode = string.IsNullOrWhiteSpace(normalizedCode) ? string.Empty : "A" + normalizedCode;
            string connStr = GetRawConnStr(day);
            string dbPath = new SQLiteConnectionStringBuilder(connStr).DataSource;

            var coverage = new ReplayRawTickCoverage
            {
                Code = normalizedCode,
                TradingDate = day,
                DatabasePath = dbPath,
                DatabaseExists = !string.IsNullOrWhiteSpace(dbPath) && File.Exists(dbPath)
            };

            if (string.IsNullOrWhiteSpace(normalizedCode) || !coverage.DatabaseExists)
                return coverage;

            if (fromTime == DateTime.MinValue)
                fromTime = day.AddHours(9);

            if (toTime == DateTime.MinValue || toTime <= fromTime)
                toTime = day.AddHours(15).AddMinutes(20).AddSeconds(59).AddMilliseconds(999);

            try
            {
                using (var conn = OpenSqliteConnection(connStr))
                {
                    coverage.TableExists = conn.ExecuteScalar<long>(@"
                        SELECT COUNT(1)
                        FROM sqlite_master
                        WHERE type='table' AND name='RawTicks';") > 0;

                    if (!coverage.TableExists)
                        return coverage;

                    const string sql = @"
                        SELECT
                            COUNT(1) AS Count,
                            COALESCE(SUM(
                                CASE
                                    WHEN UPPER(TRIM(COALESCE(Side, ''))) IN
                                         ('BUY', 'SELL', 'B', 'S', '+', '-', '매수', '매도')
                                    THEN 1
                                    ELSE 0
                                END
                            ), 0) AS ValidSideCount,
                            MIN(CreatedAt) AS FirstTime,
                            MAX(CreatedAt) AS LastTime
                        FROM RawTicks
                        WHERE (Code = @code OR Code = @prefixedCode)
                          AND CreatedAt >= @fromTime
                          AND CreatedAt <= @toTime;";

                    var row = conn.QueryFirstOrDefault<ReplayRawTickCoverage>(sql, new
                    {
                        code = normalizedCode,
                        prefixedCode,
                        fromTime = fromTime.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                        toTime = toTime.ToString("yyyy-MM-dd HH:mm:ss.fff")
                    });

                    if (row != null)
                    {
                        coverage.Count = row.Count;
                        coverage.ValidSideCount = row.ValidSideCount;
                        coverage.FirstTime = row.FirstTime;
                        coverage.LastTime = row.LastTime;
                    }
                }
            }
            catch (Exception ex)
            {
                coverage.ErrorMessage = ex.Message;
            }

            return coverage;
        }

        public List<ReplayRawTickCoverage> GetReplayAvailableRawTickDates(
            string code,
            DateTime latestAllowedDate,
            int maxCount = 10)
        {
            var result = new List<ReplayRawTickCoverage>();
            string normalizedCode = NormalizeReplayRawTickCode(code);

            if (string.IsNullOrWhiteSpace(normalizedCode) || maxCount <= 0)
                return result;

            DateTime latestDate = latestAllowedDate == DateTime.MinValue
                ? DateTime.Today
                : latestAllowedDate.Date;

            string currentRawPath = new SQLiteConnectionStringBuilder(_rawConnStr).DataSource;
            string rawDirectory = Path.GetDirectoryName(currentRawPath);

            if (string.IsNullOrWhiteSpace(rawDirectory) || !Directory.Exists(rawDirectory))
                return result;

            var candidateDates = new List<DateTime>();

            try
            {
                foreach (string path in Directory.GetFiles(rawDirectory, "RawData_*.db"))
                {
                    string fileName = Path.GetFileNameWithoutExtension(path);
                    if (string.IsNullOrWhiteSpace(fileName) || !fileName.StartsWith("RawData_", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string rawDate = fileName.Substring("RawData_".Length);
                    DateTime date;
                    if (!DateTime.TryParseExact(rawDate, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                        continue;

                    if (date.Date > latestDate)
                        continue;

                    candidateDates.Add(date.Date);
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke("[Replay 날짜목록 조회 실패] " + ex.Message);
                return result;
            }

            foreach (DateTime date in candidateDates.Distinct().OrderByDescending(x => x))
            {
                DateTime fromTime = date.AddHours(9);
                DateTime toTime = date.AddHours(15).AddMinutes(20).AddSeconds(59).AddMilliseconds(999);
                ReplayRawTickCoverage coverage = GetReplayRawTickCoverage(
                    normalizedCode,
                    date,
                    fromTime,
                    toTime);

                if (coverage != null && coverage.IsUsable)
                    result.Add(coverage);

                if (result.Count >= maxCount)
                    break;
            }

            return result;
        }

        public List<ReplayRawTickPageRecord> LoadReplayRawTickPage(
            string code,
            DateTime tradingDate,
            DateTime fromTime,
            DateTime toTime,
            DateTime cursorTime,
            long cursorId,
            int pageSize)
        {
            var result = new List<ReplayRawTickPageRecord>();
            DateTime day = tradingDate == DateTime.MinValue ? DateTime.Today : tradingDate.Date;
            string normalizedCode = NormalizeReplayRawTickCode(code);
            string prefixedCode = string.IsNullOrWhiteSpace(normalizedCode) ? string.Empty : "A" + normalizedCode;

            if (string.IsNullOrWhiteSpace(normalizedCode))
                return result;

            string connStr = GetRawConnStr(day);
            string dbPath = new SQLiteConnectionStringBuilder(connStr).DataSource;
            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
                return result;

            if (fromTime == DateTime.MinValue)
                fromTime = day.AddHours(9);

            if (toTime == DateTime.MinValue || toTime <= fromTime)
                toTime = day.AddHours(15).AddMinutes(20).AddSeconds(59).AddMilliseconds(999);

            pageSize = Math.Max(1, Math.Min(5000, pageSize));
            bool hasCursor = cursorTime != DateTime.MinValue || cursorId > 0;

            const string sql = @"
                SELECT
                    Id,
                    Code,
                    CAST(Price AS INTEGER) AS Price,
                    Volume,
                    Strength,
                    BidVolume,
                    AskVolume,
                    Side,
                    CreatedAt
                FROM RawTicks
                WHERE (Code = @code OR Code = @prefixedCode)
                  AND CreatedAt >= @fromTime
                  AND CreatedAt <= @toTime
                  AND (@hasCursor = 0 OR Id > @cursorId)
                ORDER BY Id ASC
                LIMIT @pageSize;";

            using (var conn = OpenSqliteConnection(connStr))
            {
                return conn.Query<ReplayRawTickPageRecord>(sql, new
                {
                    code = normalizedCode,
                    prefixedCode,
                    fromTime = fromTime.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    toTime = toTime.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    hasCursor = hasCursor ? 1 : 0,
                    cursorId,
                    pageSize
                }).ToList();
            }
        }

        private static string NormalizeReplayRawTickCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return string.Empty;

            string value = code.Trim();
            if (value.StartsWith("A", StringComparison.OrdinalIgnoreCase) && value.Length > 1)
                value = value.Substring(1);

            return value;
        }

        public sealed class ReplayHogaSnapshotFrameRecord
        {
            public HogaData Hoga { get; set; }
            public DateTime HogaTime { get; set; }
        }

        public List<ProgramTradeInfo> LoadReplayProgramTrades(string code, DateTime tradingDate)
        {
            var result = new List<ProgramTradeInfo>();

            if (string.IsNullOrWhiteSpace(code))
                return result;

            DateTime day = tradingDate == DateTime.MinValue ? DateTime.Today : tradingDate.Date;
            string connStr = GetRawConnStr(day);
            string dbPath = new SQLiteConnectionStringBuilder(connStr).DataSource;
            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
                return result;

            using (var conn = OpenSqliteConnection(connStr))
            {
                if (!TableExists(conn, "ProgramTradeRaw"))
                    return result;

                DateTime fromTime = day.AddHours(9);
                DateTime toTime = day.AddHours(15).AddMinutes(20).AddSeconds(59);

                var rows = conn.Query<ProgramTradeInfo>(@"
                    SELECT
                        Code,
                        Name,
                        TradeTime,
                        CurrentPrice,
                        Volume,
                        ProgramSellAmount,
                        ProgramBuyAmount,
                        ProgramNetAmount,
                        ProgramNetAmountDelta,
                        ProgramSellQuantity,
                        ProgramBuyQuantity,
                        ProgramNetQuantity,
                        ProgramNetQuantityDelta,
                        ArbitrageSell,
                        ArbitrageBuy,
                        ArbitrageNet,
                        NonArbitrageSell,
                        NonArbitrageBuy,
                        NonArbitrageNet,
                        Source,
                        Direction,
                        CreatedAt
                    FROM ProgramTradeRaw
                    WHERE Code = @code
                      AND TradeTime >= @fromTime
                      AND TradeTime <= @toTime
                    ORDER BY TradeTime ASC;", new
                {
                    code = code.Trim(),
                    fromTime,
                    toTime
                }).ToList();

                foreach (var row in rows)
                {
                    if (row == null || row.TradeTime == DateTime.MinValue)
                        continue;

                    if (string.IsNullOrWhiteSpace(row.Source))
                        row.Source = "DB";

                    result.Add(row);
                }
            }

            return result;
        }

        public List<HogaData> LoadHogas(string code, DateTime tradingDate)
        {
            var result = new List<HogaData>();

            if (string.IsNullOrWhiteSpace(code))
                return result;

            DateTime day = tradingDate.Date;
            string connStr = GetRawConnStr(day);
            string dbPath = new SQLiteConnectionStringBuilder(connStr).DataSource;
            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
                return result;

            using (var conn = OpenSqliteConnection(connStr))
            {
                if (!TableExists(conn, "HogaSnapshots"))
                    return result;

                var rows = conn.Query<RawHogaSnapshotRecord>(@"
                    SELECT *
                    FROM HogaSnapshots
                    WHERE Code = @code
                      AND HogaTime >= @fromTime
                      AND HogaTime < @toTime
                    ORDER BY HogaTime ASC;", new
                {
                    code = code.Trim(),
                    fromTime = day,
                    toTime = day.AddDays(1)
                }).ToList();

                foreach (var row in rows)
                {
                    var hoga = ToHogaData(row);
                    if (hoga != null)
                        result.Add(hoga);
                }
            }

            return result;
        }

        public List<ReplayHogaSnapshotFrameRecord> LoadReplayHogaFrames(string code, DateTime tradingDate)
        {
            var result = new List<ReplayHogaSnapshotFrameRecord>();

            if (string.IsNullOrWhiteSpace(code))
                return result;

            DateTime day = tradingDate.Date;
            string connStr = GetRawConnStr(day);
            string dbPath = new SQLiteConnectionStringBuilder(connStr).DataSource;
            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
                return result;

            using (var conn = OpenSqliteConnection(connStr))
            {
                if (!TableExists(conn, "HogaSnapshots"))
                    return result;

                var rows = conn.Query<RawHogaSnapshotRecord>(@"
                    SELECT *
                    FROM HogaSnapshots
                    WHERE Code = @code
                      AND HogaTime >= @fromTime
                      AND HogaTime < @toTime
                    ORDER BY HogaTime ASC;", new
                {
                    code = code.Trim(),
                    fromTime = day,
                    toTime = day.AddDays(1)
                }).ToList();

                foreach (var row in rows)
                {
                    if (row == null || row.HogaTime == DateTime.MinValue)
                        continue;

                    var hoga = ToHogaData(row);
                    if (hoga == null)
                        continue;

                    result.Add(new ReplayHogaSnapshotFrameRecord
                    {
                        Hoga = hoga,
                        HogaTime = row.HogaTime
                    });
                }
            }

            return result;
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

        private static bool TableExists(SQLiteConnection conn, string tableName)
        {
            if (conn == null || string.IsNullOrWhiteSpace(tableName))
                return false;

            string name = conn.ExecuteScalar<string>(@"
                SELECT name
                FROM sqlite_master
                WHERE type = 'table' AND name = @tableName
                LIMIT 1;", new { tableName });

            return !string.IsNullOrWhiteSpace(name);
        }

        private static RawHogaSnapshotRecord ToRawHogaSnapshotRecord(HogaData hoga, DateTime fallbackTime)
        {
            if (hoga == null || string.IsNullOrWhiteSpace(hoga.Code))
                return null;

            DateTime hogaTime = ResolveHogaTime(hoga);
            if (hogaTime == DateTime.MinValue)
                hogaTime = fallbackTime == DateTime.MinValue ? DateTime.Now : fallbackTime;

            var record = new RawHogaSnapshotRecord
            {
                Code = hoga.Code.Trim(),
                HogaTime = hogaTime,
                TotalAskQty = hoga.TotalAskQty,
                TotalBidQty = hoga.TotalBidQty,
                CreatedAt = DateTime.Now
            };

            CopyHogaItemsToRecord(hoga.Asks, true, record);
            CopyHogaItemsToRecord(hoga.Bids, false, record);
            return record;
        }

        private static void CopyHogaItemsToRecord(IEnumerable<HogaItem> items, bool isAsk, RawHogaSnapshotRecord record)
        {
            if (items == null || record == null)
                return;

            var buffer = items
                .Where(x => x != null)
                .Take(10)
                .ToList();

            // Kiwoom/UI HogaData.Asks는 화면 표시 순서(매도10 → 매도1)에 가깝게 들어온다.
            // DB 컬럼은 의미 기준으로 AskPrice1=최우선 매도호가, AskPrice10=10호가가 되도록 저장한다.
            if (isAsk)
                buffer.Reverse();

            int level = 0;
            foreach (var item in buffer)
            {
                level++;
                if (level > 10)
                    break;

                if (isAsk)
                {
                    SetRawHogaValue(record, $"AskPrice{level}", item.Price);
                    SetRawHogaValue(record, $"AskQty{level}", item.Quantity);
                }
                else
                {
                    SetRawHogaValue(record, $"BidPrice{level}", item.Price);
                    SetRawHogaValue(record, $"BidQty{level}", item.Quantity);
                }
            }
        }

        private static HogaData ToHogaData(RawHogaSnapshotRecord row)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.Code))
                return null;

            var hoga = new HogaData
            {
                Code = row.Code,
                TotalAskQty = row.TotalAskQty,
                TotalBidQty = row.TotalBidQty
            };

            // Replay/기존 UI 흐름은 HogaData.Asks를 기존 Kiwoom 수신 순서(매도10 → 매도1)에 맞춰 사용한다.
            // 새 저장 방식(AskPrice1=매도1, AskPrice10=매도10)은 10→1로 복원한다.
            // 이미 저장된 기존 역순 데이터(AskPrice1 > AskPrice10)는 1→10으로 읽어 호환한다.
            if (IsLegacyReversedAskRow(row))
            {
                for (int i = 1; i <= 10; i++)
                    AddAskFromRecord(row, hoga, i);
            }
            else
            {
                for (int i = 10; i >= 1; i--)
                    AddAskFromRecord(row, hoga, i);
            }

            for (int i = 1; i <= 10; i++)
                AddBidFromRecord(row, hoga, i);

            ApplyHogaTime(hoga, row.HogaTime);
            return hoga;
        }

        private static void AddAskFromRecord(RawHogaSnapshotRecord row, HogaData hoga, int level)
        {
            double askPrice = Convert.ToDouble(GetRawHogaValue(row, $"AskPrice{level}"));
            long askQty = Convert.ToInt64(GetRawHogaValue(row, $"AskQty{level}"));

            if (askPrice <= 0 && askQty <= 0)
                return;

            hoga.Asks.Add(new HogaItem
            {
                Price = askPrice,
                Quantity = askQty,
                KrxQuantity = askQty
            });
        }

        private static void AddBidFromRecord(RawHogaSnapshotRecord row, HogaData hoga, int level)
        {
            double bidPrice = Convert.ToDouble(GetRawHogaValue(row, $"BidPrice{level}"));
            long bidQty = Convert.ToInt64(GetRawHogaValue(row, $"BidQty{level}"));

            if (bidPrice <= 0 && bidQty <= 0)
                return;

            hoga.Bids.Add(new HogaItem
            {
                Price = bidPrice,
                Quantity = bidQty,
                KrxQuantity = bidQty
            });
        }

        private static bool IsLegacyReversedAskRow(RawHogaSnapshotRecord row)
        {
            double firstPrice = 0;
            double lastPrice = 0;

            for (int i = 1; i <= 10; i++)
            {
                double price = Convert.ToDouble(GetRawHogaValue(row, $"AskPrice{i}"));
                if (price > 0)
                {
                    firstPrice = price;
                    break;
                }
            }

            for (int i = 10; i >= 1; i--)
            {
                double price = Convert.ToDouble(GetRawHogaValue(row, $"AskPrice{i}"));
                if (price > 0)
                {
                    lastPrice = price;
                    break;
                }
            }

            return firstPrice > 0 && lastPrice > 0 && firstPrice > lastPrice;
        }

        private static DateTime ResolveHogaTime(HogaData hoga)
        {
            if (hoga == null)
                return DateTime.MinValue;

            var type = hoga.GetType();
            foreach (var name in new[] { "HogaTime", "Time", "UpdatedAt", "LastUpdated", "CreatedAt", "ReceivedAt", "ReceiveTime", "ReceivedTime", "Timestamp" })
            {
                var prop = type.GetProperty(name);
                if (prop == null)
                    continue;

                if (prop.PropertyType == typeof(DateTime))
                {
                    var value = (DateTime)prop.GetValue(hoga, null);
                    if (value != DateTime.MinValue)
                        return value;
                }
                else if (prop.PropertyType == typeof(DateTime?))
                {
                    var value = (DateTime?)prop.GetValue(hoga, null);
                    if (value.HasValue && value.Value != DateTime.MinValue)
                        return value.Value;
                }
            }

            return DateTime.MinValue;
        }

        private static void ApplyHogaTime(HogaData hoga, DateTime hogaTime)
        {
            if (hoga == null || hogaTime == DateTime.MinValue)
                return;

            var type = hoga.GetType();
            foreach (var name in new[] { "HogaTime", "Time", "UpdatedAt", "LastUpdated", "CreatedAt", "ReceivedAt", "ReceiveTime", "ReceivedTime", "Timestamp" })
            {
                var prop = type.GetProperty(name);
                if (prop == null || !prop.CanWrite)
                    continue;

                if (prop.PropertyType == typeof(DateTime))
                {
                    prop.SetValue(hoga, hogaTime, null);
                    return;
                }

                if (prop.PropertyType == typeof(DateTime?))
                {
                    prop.SetValue(hoga, (DateTime?)hogaTime, null);
                    return;
                }
            }
        }


        public List<DecisionOutcomeAnalysisSummaryRecord> LoadDecisionOutcomeAnalysisSummary(
            string code,
            DateTime tradingDate,
            bool replayOnly)
        {
            DateTime day = tradingDate == default(DateTime) ? DateTime.Today : tradingDate.Date;
            return LoadDecisionOutcomeAnalysisSummary(code, day, day.AddDays(1), replayOnly);
        }

        public List<DecisionOutcomeAnalysisSummaryRecord> LoadDecisionOutcomeAnalysisSummary(
            string code,
            DateTime fromTime,
            DateTime toTime,
            bool replayOnly)
        {
            var result = new List<DecisionOutcomeAnalysisSummaryRecord>();

            if (string.IsNullOrWhiteSpace(code))
                return result;

            DateTime safeFrom = fromTime == default(DateTime) ? DateTime.Today : fromTime;
            DateTime safeTo = toTime == default(DateTime) || toTime <= safeFrom ? safeFrom.Date.AddDays(1) : toTime;
            string safeCode = code.Trim();

            // 외부 쿼리/이전 실행에서 남은 CandidateId 없는 라벨과 orphan 라벨을 조회 전에 정리한다.
            CleanupCumulativeAnalysisLabelIntegrity();

            // 과거 MLData_yyyyMMdd.db에만 남아 있는 후보/라벨은 조회 전에 누적 DB로 1회 보강한다.
            // 현재 이후 저장분은 이미 이중 저장되므로, 이 경로는 과거 데이터/부분 누락 복구용이다.
            EnsureLegacyMlAnalysisDataImportedForQuery(safeCode, safeFrom, safeTo, replayOnly);

            // 누적 Analysis DB를 우선 사용한다.
            // 기존 날짜별 ML DB만 존재하는 과거 데이터는 fallback으로 유지한다.
            var analysisResult = LoadDecisionOutcomeAnalysisSummaryFromAnalysisDb(safeCode, safeFrom, safeTo, replayOnly);
            if (analysisResult.Count > 0)
                return analysisResult;

            DateTime day = safeFrom.Date;
            while (day < safeTo.Date.AddDays(1))
            {
                DateTime dayFrom = day == safeFrom.Date ? safeFrom : day;
                DateTime dayTo = day == safeTo.Date ? safeTo : day.AddDays(1);

                if (dayTo > dayFrom)
                    result.AddRange(LoadDecisionOutcomeAnalysisSummaryForSingleDb(safeCode, dayFrom, dayTo, replayOnly));

                day = day.AddDays(1);
            }

            return result
                .OrderBy(x => x.CategoryName)
                .ThenBy(x => x.ActionCandidate)
                .ToList();
        }

        private List<DecisionOutcomeAnalysisSummaryRecord> LoadDecisionOutcomeAnalysisSummaryFromAnalysisDb(
            string code,
            DateTime fromTime,
            DateTime toTime,
            bool replayOnly)
        {
            var result = new List<DecisionOutcomeAnalysisSummaryRecord>();

            try
            {
                EnsureAnalysisDatabase();

                using (var conn = OpenSqliteConnection(_analysisConnStr))
                {
                    if (!TableExists(conn, "DecisionCandidates") || !TableExists(conn, "SnapshotOutcomeLabels"))
                        return result;

                    result.AddRange(QueryDecisionOutcomeAnalysisSummary(conn, code, fromTime, toTime, replayOnly));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[누적 라벨분석 조회 실패] " + ex.Message);
                OnLog?.Invoke("[누적 라벨분석 조회 실패] " + ex.Message);
            }

            return result
                .OrderBy(x => x.ActionCandidate)
                .ToList();
        }


        public int MigrateLegacyMlAnalysisDataToCumulativeDb(DateTime fromTime, DateTime toTime)
        {
            return MigrateLegacyMlAnalysisDataToCumulativeDb(null, fromTime, toTime, false);
        }

        public int MigrateLegacyMlAnalysisDataToCumulativeDb(string code, DateTime fromTime, DateTime toTime, bool replayOnly)
        {
            string safeCode = string.IsNullOrWhiteSpace(code) ? null : code.Trim();
            DateTime safeFrom = fromTime == default(DateTime) ? DateTime.Today : fromTime;
            DateTime safeTo = toTime == default(DateTime) || toTime <= safeFrom ? safeFrom.Date.AddDays(1) : toTime;

            int imported = 0;
            DateTime day = safeFrom.Date;
            DateTime lastDay = safeTo.Date;

            while (day < lastDay.AddDays(1))
            {
                DateTime dayFrom = day == safeFrom.Date ? safeFrom : day;
                DateTime dayTo = day == safeTo.Date ? safeTo : day.AddDays(1);

                if (dayTo > dayFrom)
                    imported += MigrateLegacyMlAnalysisDataForSingleDb(safeCode, dayFrom, dayTo, replayOnly, false);

                day = day.AddDays(1);
            }

            return imported;
        }

        private void EnsureLegacyMlAnalysisDataImportedForQuery(string code, DateTime fromTime, DateTime toTime, bool replayOnly)
        {
            DateTime safeFrom = fromTime == default(DateTime) ? DateTime.Today : fromTime;
            DateTime safeTo = toTime == default(DateTime) || toTime <= safeFrom ? safeFrom.Date.AddDays(1) : toTime;
            string safeCode = string.IsNullOrWhiteSpace(code) ? "*" : code.Trim();

            DateTime day = safeFrom.Date;
            DateTime lastDay = safeTo.Date;

            while (day < lastDay.AddDays(1))
            {
                DateTime dayFrom = day == safeFrom.Date ? safeFrom : day;
                DateTime dayTo = day == safeTo.Date ? safeTo : day.AddDays(1);

                if (dayTo > dayFrom)
                {
                    string cacheKey = safeCode + "|" + day.ToString("yyyyMMdd") + "|" + (replayOnly ? "Replay" : "All");
                    if (_legacyAnalysisImportQueryCache.TryAdd(cacheKey, true))
                        MigrateLegacyMlAnalysisDataForSingleDb(string.Equals(safeCode, "*", StringComparison.OrdinalIgnoreCase) ? null : safeCode, dayFrom, dayTo, replayOnly, true);
                }

                day = day.AddDays(1);
            }
        }

        private int MigrateLegacyMlAnalysisDataForSingleDb(string code, DateTime fromTime, DateTime toTime, bool replayOnly, bool quiet)
        {
            DateTime dbDate = fromTime == default(DateTime) ? DateTime.Today : fromTime.Date;
            string connStr = GetMlConnStr(dbDate);
            string dbPath = new SQLiteConnectionStringBuilder(connStr).DataSource;

            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
                return 0;

            try
            {
                EnsureMlDatabase(dbDate);
                EnsureAnalysisDatabase();

                using (var conn = OpenSqliteConnection(connStr))
                {
                    if (!TableExists(conn, "DecisionCandidates") || !TableExists(conn, "SnapshotOutcomeLabels"))
                        return 0;

                    var candidates = conn.Query<DecisionCandidateRecord>(@"
                        SELECT *
                        FROM DecisionCandidates
                        WHERE DecisionTime >= @FromTime
                          AND DecisionTime < @ToTime
                          AND (@Code IS NULL OR @Code = '' OR Code = @Code)
                          AND (@ReplayOnly = 0 OR IsReplay = 1 OR IFNULL(DataSourceType, '') = 'Replay')
                        ORDER BY DecisionTime ASC;", new
                    {
                        Code = string.IsNullOrWhiteSpace(code) ? null : code.Trim(),
                        FromTime = fromTime,
                        ToTime = toTime,
                        ReplayOnly = replayOnly ? 1 : 0
                    }).ToList();

                    var labels = conn.Query<SnapshotOutcomeLabelRecord>(@"
                        SELECT *
                        FROM SnapshotOutcomeLabels
                        WHERE SnapshotTime >= @FromTime
                          AND SnapshotTime < @ToTime
                          AND (@Code IS NULL OR @Code = '' OR Code = @Code)
                          AND TRIM(IFNULL(CandidateId, '')) <> ''
                          AND EXISTS (
                              SELECT 1
                              FROM DecisionCandidates d
                              WHERE TRIM(IFNULL(d.CandidateId, '')) = TRIM(IFNULL(SnapshotOutcomeLabels.CandidateId, ''))
                          )
                          AND (@ReplayOnly = 0 OR IsReplay = 1 OR IFNULL(DataSourceType, '') = 'Replay')
                        ORDER BY SnapshotTime ASC;", new
                    {
                        Code = string.IsNullOrWhiteSpace(code) ? null : code.Trim(),
                        FromTime = fromTime,
                        ToTime = toTime,
                        ReplayOnly = replayOnly ? 1 : 0
                    }).ToList();

                    InsertAnalysisDecisionCandidates(candidates);
                    InsertAnalysisSnapshotOutcomeLabels(labels);

                    int imported = candidates.Count + labels.Count;
                    if (!quiet && imported > 0)
                    {
                        string message = string.Format(
                            "[누적 분석 보강] {0:yyyy-MM-dd} / Code={1} / ReplayOnly={2} / Candidate={3:N0} / Label={4:N0}",
                            dbDate,
                            string.IsNullOrWhiteSpace(code) ? "ALL" : code.Trim(),
                            replayOnly ? "Y" : "N",
                            candidates.Count,
                            labels.Count);
                        Debug.WriteLine(message);
                        OnLog?.Invoke(message);
                    }

                    return imported;
                }
            }
            catch (Exception ex)
            {
                string message = "[누적 분석 보강 실패] " + dbDate.ToString("yyyy-MM-dd") + " / " + ex.Message;
                Debug.WriteLine(message);
                if (!quiet)
                    OnLog?.Invoke(message);
                return 0;
            }
        }

        private List<DecisionOutcomeAnalysisSummaryRecord> LoadDecisionOutcomeAnalysisSummaryForSingleDb(
            string code,
            DateTime fromTime,
            DateTime toTime,
            bool replayOnly)
        {
            var result = new List<DecisionOutcomeAnalysisSummaryRecord>();
            DateTime dbDate = fromTime == default(DateTime) ? DateTime.Today : fromTime.Date;
            string connStr = GetMlConnStr(dbDate);
            string dbPath = new SQLiteConnectionStringBuilder(connStr).DataSource;

            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
                return result;

            EnsureMlDatabase(dbDate);

            using (var conn = OpenSqliteConnection(connStr))
            {
                if (!TableExists(conn, "DecisionCandidates") || !TableExists(conn, "SnapshotOutcomeLabels"))
                    return result;

                result.AddRange(QueryDecisionOutcomeAnalysisSummary(conn, code, fromTime, toTime, replayOnly));
            }

            return result;
        }

        private List<DecisionOutcomeAnalysisSummaryRecord> QueryDecisionOutcomeAnalysisSummary(
            SQLiteConnection conn,
            string code,
            DateTime fromTime,
            DateTime toTime,
            bool replayOnly)
        {
            if (conn == null)
                return new List<DecisionOutcomeAnalysisSummaryRecord>();

            const string sql = @"
                SELECT
                    @Code AS Code,
                    @FromTime AS FromTime,
                    @ToTime AS ToTime,
                    'ALL' AS CategoryName,
                    IFNULL(NULLIF(TRIM(d.ActionCandidate), ''), 'Hold') AS ActionCandidate,
                    COUNT(*) AS CandidateCount,
                    AVG(IFNULL(d.SignalScore, 0)) AS AvgSignalScore,
                    AVG(IFNULL(d.BaseFinal, 0)) AS AvgBaseFinal,
                    AVG(IFNULL(d.EntryScore, 0)) AS AvgEntryScore,
                    AVG(IFNULL(d.ExitScore, 0)) AS AvgExitScore,
                    AVG(IFNULL(d.RawAccelerationScore, 0)) AS AvgRawAccelerationScore,
                    AVG(IFNULL(d.AppliedAccelerationScore, 0)) AS AvgAppliedAccelerationScore,
                    AVG(IFNULL(d.EventMaturity, 0)) AS AvgEventMaturity,
                    AVG(IFNULL(d.PairCompletedCount, 0)) AS AvgPairCompletedCount,
                    AVG(IFNULL(d.PairOppositionPenalty, 0)) AS AvgPairOppositionPenalty,
                    AVG(IFNULL(o.ActionReturnAfter1m, 0)) AS AvgActionReturnAfter1m,
                    AVG(IFNULL(o.ActionReturnAfter3m, 0)) AS AvgActionReturnAfter3m,
                    AVG(IFNULL(o.ActionReturnAfter5m, 0)) AS AvgActionReturnAfter5m,
                    AVG(IFNULL(o.ActionReturnAfter10m, 0)) AS AvgActionReturnAfter10m,
                    AVG(IFNULL(o.ActionMfeAfter10m, 0)) AS AvgActionMfeAfter10m,
                    AVG(IFNULL(o.ActionMaeAfter10m, 0)) AS AvgActionMaeAfter10m,
                    AVG(IFNULL(o.CostAdjustedOutcome, 0)) AS AvgCostAdjustedOutcome,
                    AVG(CASE WHEN IFNULL(o.CostAdjustedOutcome, 0) > 0 THEN 1.0 ELSE 0.0 END) AS PositiveOutcomeRate,
                    AVG(CASE WHEN IFNULL(o.HitProfitFirst, 0) <> 0 THEN 1.0 ELSE 0.0 END) AS ProfitFirstRate,
                    AVG(CASE WHEN IFNULL(o.HitStopFirst, 0) <> 0 THEN 1.0 ELSE 0.0 END) AS StopFirstRate,
                    AVG(CASE WHEN IFNULL(o.LabelBuyGood, 0) <> 0 THEN 1.0 ELSE 0.0 END) AS LabelBuyGoodRate,
                    AVG(CASE WHEN IFNULL(o.LabelAvoid, 0) <> 0 THEN 1.0 ELSE 0.0 END) AS LabelAvoidRate
                FROM DecisionCandidates d
                INNER JOIN SnapshotOutcomeLabels o
                    ON o.CandidateId = d.CandidateId
                WHERE d.Code = @Code
                  AND d.DecisionTime >= @FromTime
                  AND d.DecisionTime < @ToTime
                  AND IFNULL(o.CandidateId, '') <> ''
                  AND (@ReplayOnly = 0 OR d.IsReplay = 1 OR o.IsReplay = 1 OR IFNULL(d.DataSourceType, '') = 'Replay' OR IFNULL(o.DataSourceType, '') = 'Replay')
                GROUP BY IFNULL(NULLIF(TRIM(d.ActionCandidate), ''), 'Hold')
                HAVING COUNT(*) > 0
                ORDER BY ActionCandidate;";

            return conn.Query<DecisionOutcomeAnalysisSummaryRecord>(sql, new
            {
                Code = code,
                FromTime = fromTime,
                ToTime = toTime,
                ReplayOnly = replayOnly ? 1 : 0
            }).ToList();
        }

        private static object GetRawHogaValue(RawHogaSnapshotRecord record, string propertyName)
        {
            var prop = typeof(RawHogaSnapshotRecord).GetProperty(propertyName);
            return prop == null ? 0 : prop.GetValue(record, null) ?? 0;
        }

        private static void SetRawHogaValue(RawHogaSnapshotRecord record, string propertyName, object value)
        {
            var prop = typeof(RawHogaSnapshotRecord).GetProperty(propertyName);
            if (prop != null && prop.CanWrite)
                prop.SetValue(record, Convert.ChangeType(value, prop.PropertyType), null);
        }

        public int DeleteReplayMlAnalysisData(string code, DateTime tradingDate)
        {
            // 이름은 기존 호출부 호환을 위해 유지하지만, 더 이상 UI/AutoTrading 경로에서 DB 트랜잭션을 열지 않는다.
            // Replay 삭제도 DB worker가 처리해야 database locked와 UI 멈춤을 피할 수 있다.
            return EnqueueReplayMlAnalysisDeleteRequest(code, tradingDate);
        }

        public int EnqueueReplayMlAnalysisDeleteRequest(string code, DateTime tradingDate)
        {
            if (string.IsNullOrWhiteSpace(code))
                return 0;

            DateTime day = tradingDate.Date;
            if (day == default(DateTime).Date)
                return 0;

            string safeCode = code.Trim();
            int removedQueued = RemoveQueuedReplayMlAnalysisData(safeCode, day);

            _replayMlAnalysisDeleteQueue.Enqueue(new ReplayMlAnalysisDeleteRequest
            {
                Code = safeCode,
                TradingDate = day,
                FromTime = day,
                ToTime = day.AddDays(1),
                RequestedAt = DateTime.Now
            });

            Debug.WriteLine($"[Replay ML 삭제요청 큐 등록] {safeCode} / {day:yyyy-MM-dd} / QueuedRemoved={removedQueued:N0}");
            return removedQueued;
        }

        private void FlushReplayMlAnalysisDeleteRequests()
        {
            var requests = new List<ReplayMlAnalysisDeleteRequest>();
            ReplayMlAnalysisDeleteRequest request;

            while (_replayMlAnalysisDeleteQueue.TryDequeue(out request))
            {
                if (request == null || string.IsNullOrWhiteSpace(request.Code) || request.TradingDate == default(DateTime))
                    continue;

                requests.Add(request);
            }

            if (requests.Count == 0)
                return;

            var merged = requests
                .GroupBy(x => string.Format("{0}|{1:yyyyMMdd}", (x.Code ?? string.Empty).Trim().ToUpperInvariant(), x.TradingDate.Date))
                .Select(g =>
                {
                    var first = g.First();
                    DateTime day = first.TradingDate.Date;
                    return new ReplayMlAnalysisDeleteRequest
                    {
                        Code = first.Code.Trim(),
                        TradingDate = day,
                        FromTime = day,
                        ToTime = day.AddDays(1),
                        RequestedAt = g.Min(x => x.RequestedAt == default(DateTime) ? DateTime.Now : x.RequestedAt)
                    };
                })
                .OrderBy(x => x.TradingDate)
                .ThenBy(x => x.Code)
                .ToList();

            foreach (var item in merged)
            {
                try
                {
                    int affected = 0;
                    // 삭제 요청 등록 시점의 저장 큐는 EnqueueReplayMlAnalysisDeleteRequest에서 이미 정리했다.
                    // worker 처리 시점에 다시 큐를 훑으면 새 Replay 세션이 시작된 뒤 들어온 신규 분석데이터까지 지울 수 있으므로 DB만 삭제한다.
                    affected += DeleteReplayMlAnalysisDataFromDailyDb(item.Code, item.FromTime, item.ToTime);
                    affected += DeleteReplayAnalysisDataFromCumulativeDb(item.Code, item.FromTime, item.ToTime);

                    _lastReplayMlAnalysisDeleteFlush = DateTime.Now;
                    string message = $"[Replay ML 삭제요청 처리] {item.Code} / {item.TradingDate:yyyy-MM-dd} / {affected:N0}건";
                    Debug.WriteLine(message);
                    OnLog?.Invoke(message);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[Replay ML 삭제요청 처리 실패] " + ex.Message);
                    OnLog?.Invoke("[Replay ML 삭제요청 처리 실패] " + ex.Message);
                }
            }
        }

        private int DeleteReplayMlAnalysisDataFromDailyDb(string code, DateTime fromTime, DateTime toTime)
        {
            if (string.IsNullOrWhiteSpace(code))
                return 0;

            DateTime day = fromTime == default(DateTime) ? DateTime.Today : fromTime.Date;
            int affected = 0;

            EnsureMlDatabase(day);

            bool saved = ExecuteSqliteTransactionWithRetry(
                GetMlConnStr(day),
                (conn, tran) =>
                {
                    int localAffected = 0;

                    localAffected += conn.Execute(@"
                            DELETE FROM SnapshotOutcomeLabels
                            WHERE Code = @Code
                              AND (IsReplay = 1 OR IFNULL(DataSourceType, '') = 'Replay')
                              AND SnapshotTime >= @FromTime
                              AND SnapshotTime < @ToTime;", new { Code = code, FromTime = fromTime, ToTime = toTime }, tran);

                    localAffected += conn.Execute(@"
                            DELETE FROM DecisionCandidates
                            WHERE Code = @Code
                              AND (IsReplay = 1 OR IFNULL(DataSourceType, '') = 'Replay')
                              AND DecisionTime >= @FromTime
                              AND DecisionTime < @ToTime;", new { Code = code, FromTime = fromTime, ToTime = toTime }, tran);

                    localAffected += conn.Execute(@"
                            DELETE FROM LocalMlEvaluationLogs
                            WHERE Code = @Code
                              AND (IsReplay = 1 OR IFNULL(DataSourceType, '') = 'Replay')
                              AND EvaluationTime >= @FromTime
                              AND EvaluationTime < @ToTime;", new { Code = code, FromTime = fromTime, ToTime = toTime }, tran);

                    localAffected += conn.Execute(@"
                            DELETE FROM SignalLogs
                            WHERE Code = @Code
                              AND (IsReplay = 1 OR IFNULL(DataSourceType, '') = 'Replay')
                              AND SignalTime >= @FromTime
                              AND SignalTime < @ToTime;", new { Code = code, FromTime = fromTime, ToTime = toTime }, tran);

                    localAffected += conn.Execute(@"
                            DELETE FROM FeatureSnapshots
                            WHERE Code = @Code
                              AND (IsReplay = 1 OR IFNULL(DataSourceType, '') = 'Replay')
                              AND SnapshotTime >= @FromTime
                              AND SnapshotTime < @ToTime;", new { Code = code, FromTime = fromTime, ToTime = toTime }, tran);

                    affected = localAffected;
                },
                "DeleteReplayMlAnalysisDataFromDailyDb");

            return saved ? affected : 0;
        }

        private int DeleteReplayAnalysisDataFromCumulativeDb(string code, DateTime fromTime, DateTime toTime)
        {
            if (string.IsNullOrWhiteSpace(code))
                return 0;

            try
            {
                EnsureAnalysisDatabase();

                int affected = 0;
                bool saved = ExecuteAnalysisSqliteTransactionWithRetry(
                    (conn, tran) =>
                    {
                        int localAffected = 0;

                        localAffected += conn.Execute(@"
                        DELETE FROM SnapshotOutcomeLabels
                        WHERE Code = @Code
                          AND (IsReplay = 1 OR IFNULL(DataSourceType, '') = 'Replay')
                          AND SnapshotTime >= @FromTime
                          AND SnapshotTime < @ToTime;", new { Code = code, FromTime = fromTime, ToTime = toTime }, tran);

                        localAffected += conn.Execute(@"
                        DELETE FROM DecisionCandidates
                        WHERE Code = @Code
                          AND (IsReplay = 1 OR IFNULL(DataSourceType, '') = 'Replay')
                          AND DecisionTime >= @FromTime
                          AND DecisionTime < @ToTime;", new { Code = code, FromTime = fromTime, ToTime = toTime }, tran);

                        localAffected += CleanupAnalysisOutcomeLabelIntegrity(conn, tran);
                        affected = localAffected;
                    },
                    "DeleteReplayAnalysisDataFromCumulativeDb");

                return saved ? affected : 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[누적 분석 Replay 데이터 삭제 실패] " + ex.Message);
                OnLog?.Invoke("[누적 분석 Replay 데이터 삭제 실패] " + ex.Message);
                return 0;
            }
        }

        private int RemoveQueuedReplayMlAnalysisData(string code, DateTime tradingDate)
        {
            int removed = 0;

            removed += RemoveMatchingQueueItems(_outcomeLabelQueue, x =>
                x != null && IsReplayMlRecord(x.DataSourceType, x.IsReplay) &&
                IsSameCode(x.Code, code) && IsSameDate(x.SnapshotTime, tradingDate));

            removed += RemoveMatchingQueueItems(_decisionCandidateQueue, x =>
                x != null && IsReplayMlRecord(x.DataSourceType, x.IsReplay) &&
                IsSameCode(x.Code, code) && IsSameDate(x.DecisionTime, tradingDate));

            removed += RemoveMatchingQueueItems(_localMlEvaluationQueue, x =>
                x != null && IsReplayMlRecord(x.DataSourceType, x.IsReplay) &&
                IsSameCode(x.Code, code) && IsSameDate(x.EvaluationTime, tradingDate));

            removed += RemoveMatchingQueueItems(_signalQueue, x =>
                x != null && IsReplayMlRecord(x.DataSourceType, x.IsReplay) &&
                IsSameCode(x.Code, code) && IsSameDate(x.SignalTime, tradingDate));

            removed += RemoveMatchingQueueItems(_featureQueue, x =>
                x != null && IsReplayMlRecord(x.DataSourceType, x.IsReplay) &&
                IsSameCode(x.Code, code) && IsSameDate(x.SnapshotTime, tradingDate));

            return removed;
        }

        private static int RemoveMatchingQueueItems<T>(ConcurrentQueue<T> queue, Func<T, bool> shouldRemove)
        {
            if (queue == null || queue.IsEmpty)
                return 0;

            var keep = new List<T>();
            int removed = 0;

            while (queue.TryDequeue(out var item))
            {
                if (shouldRemove != null && shouldRemove(item))
                    removed++;
                else
                    keep.Add(item);
            }

            foreach (var item in keep)
                queue.Enqueue(item);

            return removed;
        }

        private static bool IsReplayMlRecord(string dataSourceType, bool isReplay)
        {
            return isReplay || string.Equals(dataSourceType ?? string.Empty, "Replay", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSameCode(string left, string right)
        {
            return string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSameDate(DateTime value, DateTime tradingDate)
        {
            return value != default(DateTime) && value.Date == tradingDate.Date;
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

                if (_forceFlushRequested)
                {
                    _forceFlushRequested = false;
                    FlushAllPending();
                    return;
                }

                if (!_replayMlAnalysisDeleteQueue.IsEmpty)
                    FlushReplayMlAnalysisDeleteRequests();

                bool shouldFlushRawMarketData =
                    _tickQueue.Count >= RawTickBatchSize ||
                    (!_tickQueue.IsEmpty && now - _lastRawTickFlush >= TimedFlushInterval) ||
                    _hogaQueue.Count >= HogaBatchSize ||
                    (!_hogaQueue.IsEmpty && now - _lastHogaFlush >= TimedFlushInterval);

                if (shouldFlushRawMarketData)
                    FlushRawMarketData();

                if (_programTradeQueue.Count >= ProgramTradeBatchSize ||
                    (!_programTradeQueue.IsEmpty && now - _lastProgramTradeFlush >= TimedFlushInterval))
                    FlushProgramTrades();

                if (_programFlowQueue.Count >= ProgramFlowBatchSize ||
                    (!_programFlowQueue.IsEmpty && now - _lastProgramFlowFlush >= TimedFlushInterval))
                    FlushProgramFlowSnapshots();

                if (_minuteQueue.Count >= MinuteBatchSize ||
                    (!_minuteQueue.IsEmpty && now - _lastMinuteFlush >= TimedFlushInterval))
                    FlushMinuteCandles();

                if (_featureQueue.Count >= FeatureBatchSize ||
                    (!_featureQueue.IsEmpty && now - _lastFeatureFlush >= TimedFlushInterval))
                    FlushFeatureSnapshots();

                if (_signalQueue.Count >= SignalBatchSize ||
                    (!_signalQueue.IsEmpty && now - _lastSignalFlush >= TimedFlushInterval))
                    FlushSignalLogs();

                if (_decisionCandidateQueue.Count >= DecisionCandidateBatchSize ||
                    (!_decisionCandidateQueue.IsEmpty && now - _lastDecisionCandidateFlush >= TimedFlushInterval))
                    FlushDecisionCandidates();

                if (_localMlEvaluationQueue.Count >= LocalMlEvaluationBatchSize ||
                    (!_localMlEvaluationQueue.IsEmpty && now - _lastLocalMlEvaluationFlush >= TimedFlushInterval))
                    FlushLocalMlEvaluations();

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

        public void RequestFlushAllPending()
        {
            // UI/Replay/AutoTrading 경로는 DB를 직접 Flush하지 않고 worker에게 요청만 남긴다.
            _forceFlushRequested = true;
        }

        public void FlushAllPending()
        {
            bool skipNonMlPersistence = ShouldSkipPersistence();

            lock (_flushLock)
            {
                while (!_replayMlAnalysisDeleteQueue.IsEmpty)
                    FlushReplayMlAnalysisDeleteRequests();

                if (!skipNonMlPersistence)
                {
                    while (!_tickQueue.IsEmpty || !_hogaQueue.IsEmpty)
                    {
                        if (!FlushRawMarketData())
                            break;
                    }

                    while (!_minuteQueue.IsEmpty)
                    {
                        if (!FlushMinuteCandles())
                            break;
                    }
                }

                // 프로그램매매 REALTIME/TR_SEED 원천 데이터는 Replay 재현용 핵심 데이터다.
                // DisablePersistenceForReplay가 켜져 있더라도 Replay/DB 되먹임 데이터만 Enqueue 단계에서 거르고,
                // 이미 큐에 들어온 실제 원천 프로그램매매 데이터는 종료/강제Flush 때 반드시 저장한다.
                while (!_programTradeQueue.IsEmpty)
                    FlushProgramTrades();

                while (!_programFlowQueue.IsEmpty)
                    FlushProgramFlowSnapshots();

                while (!_featureQueue.IsEmpty)
                {
                    if (!FlushFeatureSnapshots())
                        break;
                }

                while (!_signalQueue.IsEmpty)
                {
                    if (!FlushSignalLogs())
                        break;
                }

                while (!_decisionCandidateQueue.IsEmpty)
                {
                    if (!FlushDecisionCandidates())
                        break;
                }

                while (!_localMlEvaluationQueue.IsEmpty)
                {
                    if (!FlushLocalMlEvaluations())
                        break;
                }

                if (!skipNonMlPersistence)
                {
                    while (!_tradeResultQueue.IsEmpty)
                    {
                        if (!FlushTradeResults())
                            break;
                    }
                }

                while (!_outcomeLabelQueue.IsEmpty)
                {
                    if (!FlushSnapshotOutcomeLabels())
                        break;
                }
            }
        }
        private bool FlushRawMarketData()
        {
            var tickBatch = new List<TickData>(RawTickMaxBatchSize);
            var hogaBatch = new List<RawHogaSnapshotRecord>(HogaMaxBatchSize);

            while (_tickQueue.TryDequeue(out var tick) &&
                   tickBatch.Count < RawTickMaxBatchSize)
            {
                if (tick != null && !string.IsNullOrWhiteSpace(tick.Code))
                    tickBatch.Add(tick);
            }

            while (_hogaQueue.TryDequeue(out var hoga) &&
                   hogaBatch.Count < HogaMaxBatchSize)
            {
                if (hoga != null && !string.IsNullOrWhiteSpace(hoga.Code))
                    hogaBatch.Add(hoga);
            }

            if (tickBatch.Count == 0 && hogaBatch.Count == 0)
                return true;

            var groups = BuildRawMarketDataGroups(tickBatch, hogaBatch);

            for (int i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                DateTime day = group.Day;
                string connStr = GetRawConnStr(day);

                bool schemaReady = EnsureRawDatabaseForFlush(day);
                if (!schemaReady)
                {
                    RequeueRawMarketDataGroups(groups.Skip(i));
                    UpdateRawMarketDataFlushTimes(tickBatch.Count > 0, hogaBatch.Count > 0);
                    return false;
                }

                bool saved = ExecuteSqliteTransactionWithRetry(
                    connStr,
                    (conn, tran) =>
                    {
                        if (group.Ticks.Count > 0)
                            conn.Execute(SqlInsertRawTick, group.Ticks, tran);

                        if (group.Hogas.Count > 0)
                            conn.Execute(SqlInsertHogaSnapshot, group.Hogas, tran);
                    },
                    "FlushRawMarketData");

                if (!saved)
                {
                    RequeueRawMarketDataGroups(groups.Skip(i));
                    UpdateRawMarketDataFlushTimes(tickBatch.Count > 0, hogaBatch.Count > 0);
                    return false;
                }
            }

            UpdateRawMarketDataFlushTimes(tickBatch.Count > 0, hogaBatch.Count > 0);
            return true;
        }

        private List<RawMarketDataBatchGroup> BuildRawMarketDataGroups(
            List<TickData> ticks,
            List<RawHogaSnapshotRecord> hogas)
        {
            var groups = new Dictionary<DateTime, RawMarketDataBatchGroup>();

            if (ticks != null)
            {
                foreach (var tick in ticks)
                {
                    if (tick == null || string.IsNullOrWhiteSpace(tick.Code))
                        continue;

                    DateTime day = ResolveRawTickDate(tick);
                    GetOrCreateRawMarketDataGroup(groups, day).Ticks.Add(tick);
                }
            }

            if (hogas != null)
            {
                foreach (var hoga in hogas)
                {
                    if (hoga == null || string.IsNullOrWhiteSpace(hoga.Code))
                        continue;

                    DateTime day = hoga.HogaTime == default(DateTime)
                        ? DateTime.Today
                        : hoga.HogaTime.Date;

                    GetOrCreateRawMarketDataGroup(groups, day).Hogas.Add(hoga);
                }
            }

            return groups.Values
                .OrderBy(x => x.Day)
                .ToList();
        }

        private static RawMarketDataBatchGroup GetOrCreateRawMarketDataGroup(
            Dictionary<DateTime, RawMarketDataBatchGroup> groups,
            DateTime day)
        {
            day = day == default(DateTime) ? DateTime.Today : day.Date;

            if (!groups.TryGetValue(day, out var group))
            {
                group = new RawMarketDataBatchGroup { Day = day };
                groups[day] = group;
            }

            return group;
        }

        private static DateTime ResolveRawTickDate(TickData tick)
        {
            if (tick == null || tick.CreatedAt == default(DateTime))
                return DateTime.Today;

            return tick.CreatedAt.Date;
        }

        private void RequeueRawMarketDataGroups(IEnumerable<RawMarketDataBatchGroup> groups)
        {
            if (groups == null)
                return;

            foreach (var group in groups)
            {
                if (group == null)
                    continue;

                RequeueRawTicks(group.Ticks);
                RequeueHogas(group.Hogas);
            }
        }

        private void UpdateRawMarketDataFlushTimes(bool hadTicks, bool hadHogas)
        {
            DateTime now = DateTime.Now;

            if (hadTicks)
                _lastRawTickFlush = now;

            if (hadHogas)
                _lastHogaFlush = now;
        }

        private bool EnsureRawDatabaseForFlush(DateTime day)
        {
            try
            {
                return EnsureRawDatabase(day);
            }
            catch (SQLiteException ex)
            {
                if (!IsDatabaseLocked(ex))
                    throw;

                LogSqliteLockFailure("EnsureRawDatabase", ex, SqliteWriteRetryCount);
                return false;
            }
        }


        private void FlushProgramTrades()
        {
            var batch = new List<ProgramTradeInfo>(ProgramTradeMaxBatchSize);

            while (_programTradeQueue.TryDequeue(out var item) && batch.Count < ProgramTradeMaxBatchSize)
            {
                if (item != null && !string.IsNullOrWhiteSpace(item.Code))
                    batch.Add(item);
            }

            if (batch.Count == 0)
                return;

            var groups = batch
                .GroupBy(x => x.TradeTime == default(DateTime) ? DateTime.Today : x.TradeTime.Date)
                .OrderBy(x => x.Key)
                .ToList();

            foreach (var group in groups)
            {
                DateTime day = group.Key == default(DateTime) ? DateTime.Today : group.Key.Date;
                string connStr = GetRawConnStr(day);

                if (!EnsureRawDatabaseForFlush(day))
                {
                    foreach (var item in group)
                        _programTradeQueue.Enqueue(item);

                    break;
                }

                bool saved = ExecuteSqliteTransactionWithRetry(
                    connStr,
                    (conn, tran) => conn.Execute(SqlInsertProgramTradeRaw, group.ToList(), tran),
                    "FlushProgramTrades");

                if (!saved)
                {
                    foreach (var item in group)
                        _programTradeQueue.Enqueue(item);

                    break;
                }
            }

            _lastProgramTradeFlush = DateTime.Now;
        }

        private void FlushProgramFlowSnapshots()
        {
            var batch = new List<ProgramFlowSnapshotRecord>(ProgramFlowMaxBatchSize);

            while (_programFlowQueue.TryDequeue(out var item) && batch.Count < ProgramFlowMaxBatchSize)
            {
                if (item != null && !string.IsNullOrWhiteSpace(item.Code))
                    batch.Add(item);
            }

            if (batch.Count == 0)
                return;

            var groups = batch
                .GroupBy(x => x.SnapshotTime == default(DateTime) ? DateTime.Today : x.SnapshotTime.Date)
                .OrderBy(x => x.Key)
                .ToList();

            foreach (var group in groups)
            {
                DateTime day = group.Key == default(DateTime) ? DateTime.Today : group.Key.Date;
                string connStr = GetRawConnStr(day);

                if (!EnsureRawDatabaseForFlush(day))
                {
                    foreach (var item in group)
                        _programFlowQueue.Enqueue(item);

                    break;
                }

                bool saved = ExecuteSqliteTransactionWithRetry(
                    connStr,
                    (conn, tran) => conn.Execute(SqlInsertProgramFlowSnapshot, group.ToList(), tran),
                    "FlushProgramFlowSnapshots");

                if (!saved)
                {
                    foreach (var item in group)
                        _programFlowQueue.Enqueue(item);

                    break;
                }
            }

            _lastProgramFlowFlush = DateTime.Now;
        }

        private bool FlushMinuteCandles()
        {
            var batch = new List<MinuteCandleRecord>(MinuteMaxBatchSize);

            MinuteCandleRecord item;
            while (batch.Count < MinuteMaxBatchSize && _minuteQueue.TryDequeue(out item))
            {
                if (item != null && !string.IsNullOrWhiteSpace(item.Code))
                    batch.Add(item);
            }

            if (batch.Count == 0)
                return true;

            bool saved = ExecuteSqliteTransactionWithRetry(
                _mainConnStr,
                (conn, tran) => conn.Execute(SqlInsertMinuteCandle, batch, tran),
                "FlushMinuteCandles");

            if (!saved)
            {
                RequeueItems(_minuteQueue, batch);
                return false;
            }

            _lastMinuteFlush = DateTime.Now;
            return true;
        }

        private bool FlushFeatureSnapshots()
        {
            var batch = new List<FeatureSnapshotRecord>(FeatureMaxBatchSize);

            FeatureSnapshotRecord item;
            while (batch.Count < FeatureMaxBatchSize && _featureQueue.TryDequeue(out item))
            {
                if (item != null && !string.IsNullOrWhiteSpace(item.Code))
                    batch.Add(item);
            }

            if (batch.Count == 0)
                return true;

            var groups = batch
                .GroupBy(x => (x.SnapshotTime == default(DateTime) ? DateTime.Now : x.SnapshotTime).Date)
                .Select(g => new { Day = g.Key, Items = g.ToList() })
                .OrderBy(x => x.Day)
                .ToList();

            for (int i = 0; i < groups.Count; i++)
            {
                var group = groups[i];

                try
                {
                    EnsureMlDatabase(group.Day);

                    bool saved = ExecuteSqliteTransactionWithRetry(
                        GetMlConnStr(group.Day),
                        (conn, tran) => conn.Execute(SqlInsertFeatureSnapshot, group.Items, tran),
                        "FlushFeatureSnapshots");

                    if (!saved)
                    {
                        RequeueItems(_featureQueue, groups.Skip(i).SelectMany(x => x.Items));
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[FeatureSnapshot 저장 실패] " + ex.Message);
                    OnLog?.Invoke("[FeatureSnapshot 저장 실패] " + ex.Message);
                    RequeueItems(_featureQueue, groups.Skip(i).SelectMany(x => x.Items));
                    return false;
                }
            }

            _lastFeatureFlush = DateTime.Now;
            return true;
        }

        private bool FlushSignalLogs()
        {
            var batch = new List<SignalLogRecord>(SignalMaxBatchSize);

            SignalLogRecord item;
            while (batch.Count < SignalMaxBatchSize && _signalQueue.TryDequeue(out item))
            {
                if (item != null && !string.IsNullOrWhiteSpace(item.Code))
                    batch.Add(item);
            }

            if (batch.Count == 0)
                return true;

            var groups = batch
                .GroupBy(x => (x.SignalTime == default(DateTime) ? DateTime.Now : x.SignalTime).Date)
                .Select(g => new { Day = g.Key, Items = g.ToList() })
                .OrderBy(x => x.Day)
                .ToList();

            for (int i = 0; i < groups.Count; i++)
            {
                var group = groups[i];

                try
                {
                    EnsureMlDatabase(group.Day);

                    bool saved = ExecuteSqliteTransactionWithRetry(
                        GetMlConnStr(group.Day),
                        (conn, tran) => conn.Execute(SqlInsertSignalLog, group.Items, tran),
                        "FlushSignalLogs");

                    if (!saved)
                    {
                        RequeueItems(_signalQueue, groups.Skip(i).SelectMany(x => x.Items));
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[SignalLog 저장 실패] " + ex.Message);
                    OnLog?.Invoke("[SignalLog 저장 실패] " + ex.Message);
                    RequeueItems(_signalQueue, groups.Skip(i).SelectMany(x => x.Items));
                    return false;
                }
            }

            _lastSignalFlush = DateTime.Now;
            return true;
        }

        private bool FlushDecisionCandidates()
        {
            var batch = new List<DecisionCandidateRecord>(DecisionCandidateMaxBatchSize);

            DecisionCandidateRecord item;
            while (batch.Count < DecisionCandidateMaxBatchSize && _decisionCandidateQueue.TryDequeue(out item))
            {
                if (item != null && !string.IsNullOrWhiteSpace(item.Code) && !string.IsNullOrWhiteSpace(item.CandidateId))
                    batch.Add(item);
            }

            if (batch.Count == 0)
                return true;

            var groups = batch
                .GroupBy(x => (x.DecisionTime == default(DateTime) ? DateTime.Now : x.DecisionTime).Date)
                .Select(g => new { Day = g.Key, Items = g.ToList() })
                .OrderBy(x => x.Day)
                .ToList();

            for (int i = 0; i < groups.Count; i++)
            {
                var group = groups[i];

                try
                {
                    EnsureMlDatabase(group.Day);

                    bool saved = ExecuteSqliteTransactionWithRetry(
                        GetMlConnStr(group.Day),
                        (conn, tran) => conn.Execute(SqlInsertDecisionCandidate, group.Items, tran),
                        "FlushDecisionCandidates");

                    if (!saved)
                    {
                        RequeueItems(_decisionCandidateQueue, groups.Skip(i).SelectMany(x => x.Items));
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[DecisionCandidate 저장 실패] " + ex.Message);
                    OnLog?.Invoke("[DecisionCandidate 저장 실패] " + ex.Message);
                    RequeueItems(_decisionCandidateQueue, groups.Skip(i).SelectMany(x => x.Items));
                    return false;
                }
            }

            if (!InsertAnalysisDecisionCandidates(batch))
            {
                RequeueItems(_decisionCandidateQueue, batch);
                return false;
            }

            _lastDecisionCandidateFlush = DateTime.Now;
            return true;
        }


        private bool FlushLocalMlEvaluations()
        {
            var batch = new List<LocalMlEvaluationRecord>(LocalMlEvaluationMaxBatchSize);

            LocalMlEvaluationRecord item;
            while (batch.Count < LocalMlEvaluationMaxBatchSize && _localMlEvaluationQueue.TryDequeue(out item))
            {
                if (item != null && !string.IsNullOrWhiteSpace(item.Code))
                    batch.Add(item);
            }

            if (batch.Count == 0)
                return true;

            var groups = batch
                .GroupBy(x => (x.EvaluationTime == default(DateTime) ? DateTime.Now : x.EvaluationTime).Date)
                .Select(g => new { Day = g.Key, Items = g.ToList() })
                .OrderBy(x => x.Day)
                .ToList();

            for (int i = 0; i < groups.Count; i++)
            {
                var group = groups[i];

                try
                {
                    EnsureMlDatabase(group.Day);

                    bool saved = ExecuteSqliteTransactionWithRetry(
                        GetMlConnStr(group.Day),
                        (conn, tran) => conn.Execute(SqlInsertLocalMlEvaluation, group.Items, tran),
                        "FlushLocalMlEvaluations");

                    if (!saved)
                    {
                        RequeueItems(_localMlEvaluationQueue, groups.Skip(i).SelectMany(x => x.Items));
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[LocalMlEvaluation 저장 실패] " + ex.Message);
                    OnLog?.Invoke("[LocalMlEvaluation 저장 실패] " + ex.Message);
                    RequeueItems(_localMlEvaluationQueue, groups.Skip(i).SelectMany(x => x.Items));
                    return false;
                }
            }

            _lastLocalMlEvaluationFlush = DateTime.Now;
            return true;
        }


        private bool FlushTradeResults()
        {
            var batch = new List<TradeResultRecord>(TradeResultMaxBatchSize);

            TradeResultRecord item;
            while (batch.Count < TradeResultMaxBatchSize && _tradeResultQueue.TryDequeue(out item))
            {
                if (item != null && !string.IsNullOrWhiteSpace(item.Code))
                    batch.Add(item);
            }

            if (batch.Count == 0)
                return true;

            bool saved = ExecuteSqliteTransactionWithRetry(
                _mainConnStr,
                (conn, tran) => conn.Execute(SqlInsertTradeResult, batch, tran),
                "FlushTradeResults");

            if (!saved)
            {
                RequeueItems(_tradeResultQueue, batch);
                return false;
            }

            _lastTradeResultFlush = DateTime.Now;
            return true;
        }



        private bool FlushSnapshotOutcomeLabels()
        {
            var batch = new List<SnapshotOutcomeLabelRecord>(OutcomeLabelMaxBatchSize);

            SnapshotOutcomeLabelRecord item;
            while (batch.Count < OutcomeLabelMaxBatchSize && _outcomeLabelQueue.TryDequeue(out item))
            {
                if (item != null && !string.IsNullOrWhiteSpace(item.Code) && !string.IsNullOrWhiteSpace(item.SnapshotId))
                    batch.Add(item);
            }

            if (batch.Count == 0)
                return true;

            var groups = batch
                .GroupBy(x => (x.SnapshotTime == default(DateTime) ? DateTime.Now : x.SnapshotTime).Date)
                .Select(g => new { Day = g.Key, Items = g.ToList() })
                .OrderBy(x => x.Day)
                .ToList();

            for (int i = 0; i < groups.Count; i++)
            {
                var group = groups[i];

                try
                {
                    EnsureMlDatabase(group.Day);

                    bool saved = ExecuteSqliteTransactionWithRetry(
                        GetMlConnStr(group.Day),
                        (conn, tran) => conn.Execute(SqlInsertSnapshotOutcomeLabel, group.Items, tran),
                        "FlushSnapshotOutcomeLabels");

                    if (!saved)
                    {
                        RequeueItems(_outcomeLabelQueue, groups.Skip(i).SelectMany(x => x.Items));
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[SnapshotOutcomeLabel 저장 실패] " + ex.Message);
                    OnLog?.Invoke("[SnapshotOutcomeLabel 저장 실패] " + ex.Message);
                    RequeueItems(_outcomeLabelQueue, groups.Skip(i).SelectMany(x => x.Items));
                    return false;
                }
            }

            if (!InsertAnalysisSnapshotOutcomeLabels(batch))
            {
                RequeueItems(_outcomeLabelQueue, batch);
                return false;
            }

            _lastOutcomeLabelFlush = DateTime.Now;
            return true;
        }


        private bool InsertAnalysisDecisionCandidates(IEnumerable<DecisionCandidateRecord> candidates)
        {
            if (candidates == null)
                return true;

            var batch = candidates
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Code) && !string.IsNullOrWhiteSpace(x.CandidateId))
                .ToList();

            if (batch.Count == 0)
                return true;

            try
            {
                EnsureAnalysisDatabase();

                return ExecuteAnalysisSqliteTransactionWithRetry(
                    (conn, tran) =>
                    {
                        conn.Execute(SqlInsertDecisionCandidate, batch, tran);
                    },
                    "InsertAnalysisDecisionCandidates");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[누적 DecisionCandidate 저장 실패] " + ex.Message);
                OnLog?.Invoke("[누적 DecisionCandidate 저장 실패] " + ex.Message);
                return false;
            }
        }


        private bool InsertAnalysisSnapshotOutcomeLabels(IEnumerable<SnapshotOutcomeLabelRecord> labels)
        {
            if (labels == null)
                return true;

            // AutoTradeAnalysis.db는 DecisionCandidate 기반 분석 전용이다.
            // FeatureSnapshot-only 라벨(CandidateId 없음)은 기존 날짜별 ML DB에만 보관하고 누적 분석 DB에는 넣지 않는다.
            var batch = labels
                .Where(x => x != null &&
                            !string.IsNullOrWhiteSpace(x.Code) &&
                            !string.IsNullOrWhiteSpace(x.OutcomeId) &&
                            !string.IsNullOrWhiteSpace(x.CandidateId))
                .ToList();

            if (batch.Count == 0)
                return true;

            try
            {
                EnsureAnalysisDatabase();

                return ExecuteAnalysisSqliteTransactionWithRetry(
                    (conn, tran) =>
                    {
                        var candidateIds = batch
                            .Select(x => (x.CandidateId ?? string.Empty).Trim())
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        if (candidateIds.Count == 0)
                            return;

                        var validCandidateIds = new HashSet<string>(
                            conn.Query<string>(
                                "SELECT CandidateId FROM DecisionCandidates WHERE CandidateId IN @CandidateIds;",
                                new { CandidateIds = candidateIds },
                                tran)
                                .Where(x => !string.IsNullOrWhiteSpace(x)),
                            StringComparer.OrdinalIgnoreCase);

                        var validBatch = batch
                            .Where(x => validCandidateIds.Contains((x.CandidateId ?? string.Empty).Trim()))
                            .ToList();

                        if (validBatch.Count > 0)
                            conn.Execute(SqlInsertSnapshotOutcomeLabel, validBatch, tran);
                    },
                    "InsertAnalysisSnapshotOutcomeLabels");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[누적 OutcomeLabel 저장 실패] " + ex.Message);
                OnLog?.Invoke("[누적 OutcomeLabel 저장 실패] " + ex.Message);
                return false;
            }
        }


        private int CleanupAnalysisOutcomeLabelIntegrity(SQLiteConnection conn, SQLiteTransaction tran = null)
        {
            if (conn == null)
                return 0;

            int affected = 0;

            // 누적 Analysis DB는 Candidate 기반 분석 DB이므로 CandidateId 없는 라벨은 제외한다.
            affected += conn.Execute(@"
                DELETE FROM SnapshotOutcomeLabels
                WHERE CandidateId IS NULL OR CandidateId = '';",
                transaction: tran);

            // CandidateId는 있는데 대응 DecisionCandidate가 없는 orphan 라벨도 제외한다.
            affected += conn.Execute(@"
                DELETE FROM SnapshotOutcomeLabels
                WHERE CandidateId IS NOT NULL
                  AND CandidateId <> ''
                  AND NOT EXISTS (
                      SELECT 1
                      FROM DecisionCandidates d
                      WHERE d.CandidateId = SnapshotOutcomeLabels.CandidateId
                  );",
                transaction: tran);

            return affected;
        }


        public int CleanupCumulativeAnalysisLabelIntegrity()
        {
            try
            {
                EnsureAnalysisDatabase();

                int affected = 0;
                bool saved = ExecuteAnalysisSqliteTransactionWithRetry(
                    (conn, tran) =>
                    {
                        affected = CleanupAnalysisOutcomeLabelIntegrity(conn, tran);
                    },
                    "CleanupCumulativeAnalysisLabelIntegrity");

                if (!saved)
                    return 0;

                if (affected > 0)
                {
                    string message = "[누적 분석 라벨 정합성 정리] " + affected.ToString("N0") + "건";
                    Debug.WriteLine(message);
                    OnLog?.Invoke(message);
                }

                return affected;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[누적 분석 라벨 정합성 정리 실패] " + ex.Message);
                OnLog?.Invoke("[누적 분석 라벨 정합성 정리 실패] " + ex.Message);
                return 0;
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
                        BuyVolume REAL DEFAULT 0,
                        SellVolume REAL DEFAULT 0,
                        BuyVolumeRatio REAL DEFAULT 0,
                        SellVolumeRatio REAL DEFAULT 0,
                        NetBuyVolume REAL DEFAULT 0,
                        NetBuyVolumeRatio REAL DEFAULT 0,
                        FrameChange30s REAL DEFAULT 0,
                        FrameChange1m REAL DEFAULT 0,
                        FrameChange3m REAL DEFAULT 0,
                        FrameChange5m REAL DEFAULT 0,
                        FrameVolumeRatio REAL DEFAULT 0,
                        FrameBuyPressureScore REAL DEFAULT 0,
                        FrameStrengthDelta REAL DEFAULT 0,
                        MinuteChange15m REAL DEFAULT 0,
                        MinuteChange30m REAL DEFAULT 0,
                        MinuteChange60m REAL DEFAULT 0,
                        TodayRangePosition REAL DEFAULT 0,
                        TwoDayRangePosition REAL DEFAULT 0,
                        MarketRiskScore REAL DEFAULT 0,
                        RelativeStrength REAL DEFAULT 0,
                        HoldingQty INTEGER DEFAULT 0,
                        EntryPrice REAL DEFAULT 0,
                        ProfitRate REAL DEFAULT 0,
                        MaxProfitRate REAL DEFAULT 0,
                        MinProfitRate REAL DEFAULT 0,
                        DrawdownRate REAL DEFAULT 0,
                        HoldingSeconds REAL DEFAULT 0,
                        EstimatedRoundTripCostRate REAL DEFAULT 0,
                        NetProfitRate REAL DEFAULT 0,
                        NetMaxProfitRate REAL DEFAULT 0,
                        NetDrawdownRate REAL DEFAULT 0,
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
                        LocalMlUsed INTEGER DEFAULT 0,
                        LocalMlAvailable INTEGER DEFAULT 0,
                        LocalMlAppliedToTrading INTEGER DEFAULT 0,
                        LocalMlScore REAL DEFAULT 0,
                        LocalMlConfidence REAL DEFAULT 0,
                        LocalMlInfluenceRatio REAL DEFAULT 0,
                        LocalMlDirection TEXT,
                        LocalMlReason TEXT,
                        DataSourceType TEXT DEFAULT 'Real',
                        IsReplay INTEGER DEFAULT 0,
                        MlSessionId TEXT,
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
                        LocalMlUsed INTEGER DEFAULT 0,
                        LocalMlAvailable INTEGER DEFAULT 0,
                        LocalMlAppliedToTrading INTEGER DEFAULT 0,
                        LocalMlScore REAL DEFAULT 0,
                        LocalMlConfidence REAL DEFAULT 0,
                        LocalMlInfluenceRatio REAL DEFAULT 0,
                        LocalMlDirection TEXT,
                        LocalMlReason TEXT,
                        DataSourceType TEXT DEFAULT 'Real',
                        IsReplay INTEGER DEFAULT 0,
                        MlSessionId TEXT,
                        CreatedAt DATETIME NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_signal_code_time ON SignalLogs (Code, SignalTime DESC);

                    CREATE TABLE IF NOT EXISTS LocalMlEvaluationLogs (
                        EvaluationId TEXT PRIMARY KEY,
                        SnapshotId TEXT,
                        SignalId TEXT,
                        CandidateId TEXT,
                        Code TEXT NOT NULL,
                        EvaluationTime DATETIME NOT NULL,
                        ModelKey TEXT,
                        ModelName TEXT,
                        ModelVersion TEXT,
                        EvaluatorType TEXT,
                        FeatureSchemaVersion TEXT,
                        RuntimeMode TEXT,
                        IsEnabled INTEGER DEFAULT 0,
                        IsAvailable INTEGER DEFAULT 0,
                        IsAppliedToTrading INTEGER DEFAULT 0,
                        InfluenceRatio REAL DEFAULT 0,
                        RuleAction TEXT,
                        FinalAction TEXT,
                        RuleScore INTEGER DEFAULT 0,
                        FinalScore INTEGER DEFAULT 0,
                        StrategyName TEXT,
                        TradeMode TEXT,
                        Direction TEXT,
                        Score REAL DEFAULT 0,
                        Confidence REAL DEFAULT 0,
                        BuyQualityScore REAL DEFAULT 0,
                        SellRiskScore REAL DEFAULT 0,
                        AvoidScore REAL DEFAULT 0,
                        ExpectedReturn REAL DEFAULT 0,
                        ExpectedDrawdown REAL DEFAULT 0,
                        Reason TEXT,
                        DataSourceType TEXT DEFAULT 'Real',
                        IsReplay INTEGER DEFAULT 0,
                        MlSessionId TEXT,
                        CreatedAt DATETIME NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_local_ml_code_time ON LocalMlEvaluationLogs (Code, EvaluationTime DESC);
                    CREATE INDEX IF NOT EXISTS idx_local_ml_snapshot ON LocalMlEvaluationLogs (SnapshotId);
                    CREATE INDEX IF NOT EXISTS idx_local_ml_signal ON LocalMlEvaluationLogs (SignalId);
                    CREATE INDEX IF NOT EXISTS idx_local_ml_model_time ON LocalMlEvaluationLogs (ModelKey, ModelVersion, EvaluationTime DESC);

                    CREATE TABLE IF NOT EXISTS DecisionCandidates (
                        CandidateId TEXT PRIMARY KEY,
                        SnapshotId TEXT,
                        SignalId TEXT,
                        Code TEXT NOT NULL,
                        DecisionTime DATETIME NOT NULL,
                        Price REAL DEFAULT 0,
                        ActionCandidate TEXT,
                        FinalAction TEXT,
                        SignalScore INTEGER DEFAULT 0,
                        StrategyName TEXT,
                        TradeMode TEXT,
                        BaseFinal REAL DEFAULT 0,
                        EntryScore REAL DEFAULT 0,
                        ExitScore REAL DEFAULT 0,
                        RawAccelerationScore REAL DEFAULT 0,
                        AppliedAccelerationScore REAL DEFAULT 0,
                        EventMaturity REAL DEFAULT 0,
                        PairCompletedCount INTEGER DEFAULT 0,
                        PairOppositionPenalty REAL DEFAULT 0,
                        DecisionEventKey TEXT,
                        Reason TEXT,
                        DataSourceType TEXT DEFAULT 'Real',
                        IsReplay INTEGER DEFAULT 0,
                        MlSessionId TEXT,
                        CreatedAt DATETIME NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_decision_code_time ON DecisionCandidates (Code, DecisionTime DESC);
                    CREATE INDEX IF NOT EXISTS idx_decision_snapshot ON DecisionCandidates (SnapshotId);
                    CREATE INDEX IF NOT EXISTS idx_decision_signal ON DecisionCandidates (SignalId);
                    CREATE INDEX IF NOT EXISTS idx_decision_action_time ON DecisionCandidates (ActionCandidate, DecisionTime DESC);

                    CREATE TABLE IF NOT EXISTS SnapshotOutcomeLabels (
                        OutcomeId TEXT PRIMARY KEY,
                        SnapshotId TEXT NOT NULL,
                        Code TEXT NOT NULL,
                        SnapshotTime DATETIME NOT NULL,
                        LabelTime DATETIME NOT NULL,
                        CandidateId TEXT,
                        ActionCandidate TEXT,
                        FinalAction TEXT,
                        ReturnAfter30s REAL,
                        ReturnAfter1m REAL,
                        ReturnAfter3m REAL,
                        ReturnAfter5m REAL,
                        ReturnAfter10m REAL,
                        MaxProfitAfter10m REAL,
                        MaxLossAfter10m REAL,
                        EstimatedRoundTripCostRate REAL DEFAULT 0,
                        NetReturnAfter30s REAL DEFAULT 0,
                        NetReturnAfter1m REAL DEFAULT 0,
                        NetReturnAfter3m REAL DEFAULT 0,
                        NetReturnAfter5m REAL DEFAULT 0,
                        NetReturnAfter10m REAL DEFAULT 0,
                        NetMaxProfitAfter10m REAL DEFAULT 0,
                        NetMaxLossAfter10m REAL DEFAULT 0,
                        ActionReturnAfter30s REAL DEFAULT 0,
                        ActionReturnAfter1m REAL DEFAULT 0,
                        ActionReturnAfter3m REAL DEFAULT 0,
                        ActionReturnAfter5m REAL DEFAULT 0,
                        ActionReturnAfter10m REAL DEFAULT 0,
                        ActionMfeAfter10m REAL DEFAULT 0,
                        ActionMaeAfter10m REAL DEFAULT 0,
                        CostAdjustedOutcome REAL DEFAULT 0,
                        HitProfitFirst INTEGER,
                        HitStopFirst INTEGER,
                        ProfitHitSeconds REAL,
                        StopHitSeconds REAL,
                        LabelBuyGood INTEGER,
                        LabelAvoid INTEGER,
                        DataSourceType TEXT DEFAULT 'Real',
                        IsReplay INTEGER DEFAULT 0,
                        MlSessionId TEXT,
                        CreatedAt DATETIME NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_outcome_snapshot ON SnapshotOutcomeLabels (SnapshotId);
                    CREATE INDEX IF NOT EXISTS idx_outcome_candidate ON SnapshotOutcomeLabels (CandidateId);
                    CREATE INDEX IF NOT EXISTS idx_outcome_code_time ON SnapshotOutcomeLabels (Code, SnapshotTime DESC);
                ";

        private sealed class RawMarketDataBatchGroup
        {
            public DateTime Day { get; set; }
            public List<TickData> Ticks { get; } = new List<TickData>();
            public List<RawHogaSnapshotRecord> Hogas { get; } = new List<RawHogaSnapshotRecord>();
        }

        private sealed class RawHogaSnapshotRecord
        {
            public string Code { get; set; }
            public DateTime HogaTime { get; set; }
            public long TotalAskQty { get; set; }
            public long TotalBidQty { get; set; }
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
            public double AskPrice6 { get; set; }
            public long AskQty6 { get; set; }
            public double AskPrice7 { get; set; }
            public long AskQty7 { get; set; }
            public double AskPrice8 { get; set; }
            public long AskQty8 { get; set; }
            public double AskPrice9 { get; set; }
            public long AskQty9 { get; set; }
            public double AskPrice10 { get; set; }
            public long AskQty10 { get; set; }
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
            public double BidPrice6 { get; set; }
            public long BidQty6 { get; set; }
            public double BidPrice7 { get; set; }
            public long BidQty7 { get; set; }
            public double BidPrice8 { get; set; }
            public long BidQty8 { get; set; }
            public double BidPrice9 { get; set; }
            public long BidQty9 { get; set; }
            public double BidPrice10 { get; set; }
            public long BidQty10 { get; set; }
            public DateTime CreatedAt { get; set; }
        }

        private const string SqlCreateAnalysisTables = @"
                    CREATE TABLE IF NOT EXISTS DecisionCandidates (
                        CandidateId TEXT PRIMARY KEY,
                        SnapshotId TEXT,
                        SignalId TEXT,
                        Code TEXT NOT NULL,
                        DecisionTime DATETIME NOT NULL,
                        Price REAL DEFAULT 0,
                        ActionCandidate TEXT,
                        FinalAction TEXT,
                        SignalScore INTEGER DEFAULT 0,
                        StrategyName TEXT,
                        TradeMode TEXT,
                        BaseFinal REAL DEFAULT 0,
                        EntryScore REAL DEFAULT 0,
                        ExitScore REAL DEFAULT 0,
                        RawAccelerationScore REAL DEFAULT 0,
                        AppliedAccelerationScore REAL DEFAULT 0,
                        EventMaturity REAL DEFAULT 0,
                        PairCompletedCount INTEGER DEFAULT 0,
                        PairOppositionPenalty REAL DEFAULT 0,
                        DecisionEventKey TEXT,
                        Reason TEXT,
                        DataSourceType TEXT DEFAULT 'Real',
                        IsReplay INTEGER DEFAULT 0,
                        MlSessionId TEXT,
                        CreatedAt DATETIME NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_analysis_decision_code_time ON DecisionCandidates (Code, DecisionTime DESC);
                    CREATE INDEX IF NOT EXISTS idx_analysis_decision_snapshot ON DecisionCandidates (SnapshotId);
                    CREATE INDEX IF NOT EXISTS idx_analysis_decision_signal ON DecisionCandidates (SignalId);
                    CREATE INDEX IF NOT EXISTS idx_analysis_decision_action_time ON DecisionCandidates (ActionCandidate, DecisionTime DESC);
                    CREATE INDEX IF NOT EXISTS idx_analysis_decision_replay_time ON DecisionCandidates (IsReplay, DataSourceType, DecisionTime DESC);

                    CREATE TABLE IF NOT EXISTS SnapshotOutcomeLabels (
                        OutcomeId TEXT PRIMARY KEY,
                        SnapshotId TEXT NOT NULL,
                        Code TEXT NOT NULL,
                        SnapshotTime DATETIME NOT NULL,
                        LabelTime DATETIME NOT NULL,
                        CandidateId TEXT,
                        ActionCandidate TEXT,
                        FinalAction TEXT,
                        ReturnAfter30s REAL,
                        ReturnAfter1m REAL,
                        ReturnAfter3m REAL,
                        ReturnAfter5m REAL,
                        ReturnAfter10m REAL,
                        MaxProfitAfter10m REAL,
                        MaxLossAfter10m REAL,
                        EstimatedRoundTripCostRate REAL DEFAULT 0,
                        NetReturnAfter30s REAL DEFAULT 0,
                        NetReturnAfter1m REAL DEFAULT 0,
                        NetReturnAfter3m REAL DEFAULT 0,
                        NetReturnAfter5m REAL DEFAULT 0,
                        NetReturnAfter10m REAL DEFAULT 0,
                        NetMaxProfitAfter10m REAL DEFAULT 0,
                        NetMaxLossAfter10m REAL DEFAULT 0,
                        ActionReturnAfter30s REAL DEFAULT 0,
                        ActionReturnAfter1m REAL DEFAULT 0,
                        ActionReturnAfter3m REAL DEFAULT 0,
                        ActionReturnAfter5m REAL DEFAULT 0,
                        ActionReturnAfter10m REAL DEFAULT 0,
                        ActionMfeAfter10m REAL DEFAULT 0,
                        ActionMaeAfter10m REAL DEFAULT 0,
                        CostAdjustedOutcome REAL DEFAULT 0,
                        HitProfitFirst INTEGER,
                        HitStopFirst INTEGER,
                        ProfitHitSeconds REAL,
                        StopHitSeconds REAL,
                        LabelBuyGood INTEGER,
                        LabelAvoid INTEGER,
                        DataSourceType TEXT DEFAULT 'Real',
                        IsReplay INTEGER DEFAULT 0,
                        MlSessionId TEXT,
                        CreatedAt DATETIME NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_analysis_outcome_snapshot ON SnapshotOutcomeLabels (SnapshotId);
                    CREATE INDEX IF NOT EXISTS idx_analysis_outcome_candidate ON SnapshotOutcomeLabels (CandidateId);
                    CREATE INDEX IF NOT EXISTS idx_analysis_outcome_code_time ON SnapshotOutcomeLabels (Code, SnapshotTime DESC);
                    CREATE INDEX IF NOT EXISTS idx_analysis_outcome_replay_time ON SnapshotOutcomeLabels (IsReplay, DataSourceType, SnapshotTime DESC);
                ";

        private const string SqlEnsureRawDatabase = @"
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
                    CREATE INDEX IF NOT EXISTS idx_rawticks_replay_page ON RawTicks (Code, CreatedAt ASC, Id ASC);
                    CREATE INDEX IF NOT EXISTS idx_rawticks_replay_id ON RawTicks (Code, Id ASC);

                    CREATE TABLE IF NOT EXISTS HogaSnapshots (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Code TEXT NOT NULL,
                        HogaTime DATETIME NOT NULL,
                        TotalAskQty INTEGER NOT NULL DEFAULT 0,
                        TotalBidQty INTEGER NOT NULL DEFAULT 0,
                        AskPrice1 REAL, AskQty1 INTEGER,
                        AskPrice2 REAL, AskQty2 INTEGER,
                        AskPrice3 REAL, AskQty3 INTEGER,
                        AskPrice4 REAL, AskQty4 INTEGER,
                        AskPrice5 REAL, AskQty5 INTEGER,
                        AskPrice6 REAL, AskQty6 INTEGER,
                        AskPrice7 REAL, AskQty7 INTEGER,
                        AskPrice8 REAL, AskQty8 INTEGER,
                        AskPrice9 REAL, AskQty9 INTEGER,
                        AskPrice10 REAL, AskQty10 INTEGER,
                        BidPrice1 REAL, BidQty1 INTEGER,
                        BidPrice2 REAL, BidQty2 INTEGER,
                        BidPrice3 REAL, BidQty3 INTEGER,
                        BidPrice4 REAL, BidQty4 INTEGER,
                        BidPrice5 REAL, BidQty5 INTEGER,
                        BidPrice6 REAL, BidQty6 INTEGER,
                        BidPrice7 REAL, BidQty7 INTEGER,
                        BidPrice8 REAL, BidQty8 INTEGER,
                        BidPrice9 REAL, BidQty9 INTEGER,
                        BidPrice10 REAL, BidQty10 INTEGER,
                        CreatedAt DATETIME NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_hogas_code_time ON HogaSnapshots (Code, HogaTime ASC);
                    CREATE INDEX IF NOT EXISTS idx_hogas_time ON HogaSnapshots (HogaTime ASC);


                    CREATE TABLE IF NOT EXISTS ProgramTradeRaw (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Code TEXT NOT NULL,
                        Name TEXT,
                        TradeTime DATETIME NOT NULL,
                        CurrentPrice REAL,
                        Volume INTEGER,
                        ProgramSellAmount INTEGER,
                        ProgramBuyAmount INTEGER,
                        ProgramNetAmount INTEGER,
                        ProgramNetAmountDelta INTEGER,
                        ProgramSellQuantity INTEGER,
                        ProgramBuyQuantity INTEGER,
                        ProgramNetQuantity INTEGER,
                        ProgramNetQuantityDelta INTEGER,
                        ArbitrageSell INTEGER,
                        ArbitrageBuy INTEGER,
                        ArbitrageNet INTEGER,
                        NonArbitrageSell INTEGER,
                        NonArbitrageBuy INTEGER,
                        NonArbitrageNet INTEGER,
                        Source TEXT NOT NULL,
                        Direction TEXT,
                        CreatedAt DATETIME NOT NULL,
                        UNIQUE(Code, TradeTime, Source)
                    );
                    CREATE INDEX IF NOT EXISTS idx_program_trade_code_time ON ProgramTradeRaw (Code, TradeTime ASC);
                    CREATE INDEX IF NOT EXISTS idx_program_trade_time ON ProgramTradeRaw (TradeTime ASC);

                    CREATE TABLE IF NOT EXISTS ProgramFlowSnapshots (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        SnapshotId TEXT NOT NULL UNIQUE,
                        Code TEXT NOT NULL,
                        SnapshotTime DATETIME NOT NULL,
                        LatestNetAmount INTEGER,
                        LatestNetQuantity INTEGER,
                        Delta1mAmount INTEGER,
                        Delta3mAmount INTEGER,
                        Delta5mAmount INTEGER,
                        Delta10mAmount INTEGER,
                        Delta1mQuantity INTEGER,
                        Delta3mQuantity INTEGER,
                        Delta5mQuantity INTEGER,
                        Delta10mQuantity INTEGER,
                        FlowScore REAL,
                        FlowDirection TEXT,
                        IsWarmedUp INTEGER,
                        SourceState TEXT,
                        CreatedAt DATETIME NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_program_flow_code_time ON ProgramFlowSnapshots (Code, SnapshotTime ASC);
                    CREATE INDEX IF NOT EXISTS idx_program_flow_time ON ProgramFlowSnapshots (SnapshotTime ASC);

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
                ";

        private const string SqlInsertRawTick = @"
            INSERT INTO RawTicks (Code, Price, Volume, Strength, AskVolume, BidVolume, Side, CreatedAt)
            VALUES (@Code, @Price, @Volume, @Strength, @AskVolume, @BidVolume, @Side, @CreatedAt);";



        private const string SqlInsertHogaSnapshot = @"
            INSERT INTO HogaSnapshots (
                Code, HogaTime, TotalAskQty, TotalBidQty,
                AskPrice1, AskQty1, AskPrice2, AskQty2, AskPrice3, AskQty3, AskPrice4, AskQty4, AskPrice5, AskQty5,
                AskPrice6, AskQty6, AskPrice7, AskQty7, AskPrice8, AskQty8, AskPrice9, AskQty9, AskPrice10, AskQty10,
                BidPrice1, BidQty1, BidPrice2, BidQty2, BidPrice3, BidQty3, BidPrice4, BidQty4, BidPrice5, BidQty5,
                BidPrice6, BidQty6, BidPrice7, BidQty7, BidPrice8, BidQty8, BidPrice9, BidQty9, BidPrice10, BidQty10,
                CreatedAt
            ) VALUES (
                @Code, @HogaTime, @TotalAskQty, @TotalBidQty,
                @AskPrice1, @AskQty1, @AskPrice2, @AskQty2, @AskPrice3, @AskQty3, @AskPrice4, @AskQty4, @AskPrice5, @AskQty5,
                @AskPrice6, @AskQty6, @AskPrice7, @AskQty7, @AskPrice8, @AskQty8, @AskPrice9, @AskQty9, @AskPrice10, @AskQty10,
                @BidPrice1, @BidQty1, @BidPrice2, @BidQty2, @BidPrice3, @BidQty3, @BidPrice4, @BidQty4, @BidPrice5, @BidQty5,
                @BidPrice6, @BidQty6, @BidPrice7, @BidQty7, @BidPrice8, @BidQty8, @BidPrice9, @BidQty9, @BidPrice10, @BidQty10,
                @CreatedAt
            );";


        private const string SqlInsertProgramTradeRaw = @"
            INSERT INTO ProgramTradeRaw (
                Code, Name, TradeTime, CurrentPrice, Volume,
                ProgramSellAmount, ProgramBuyAmount, ProgramNetAmount, ProgramNetAmountDelta,
                ProgramSellQuantity, ProgramBuyQuantity, ProgramNetQuantity, ProgramNetQuantityDelta,
                ArbitrageSell, ArbitrageBuy, ArbitrageNet,
                NonArbitrageSell, NonArbitrageBuy, NonArbitrageNet,
                Source, Direction, CreatedAt
            ) VALUES (
                @Code, @Name, @TradeTime, @CurrentPrice, @Volume,
                @ProgramSellAmount, @ProgramBuyAmount, @ProgramNetAmount, @ProgramNetAmountDelta,
                @ProgramSellQuantity, @ProgramBuyQuantity, @ProgramNetQuantity, @ProgramNetQuantityDelta,
                @ArbitrageSell, @ArbitrageBuy, @ArbitrageNet,
                @NonArbitrageSell, @NonArbitrageBuy, @NonArbitrageNet,
                @Source, @Direction, @CreatedAt
            )
            ON CONFLICT(Code, TradeTime, Source) DO UPDATE SET
                Name = excluded.Name,
                CurrentPrice = excluded.CurrentPrice,
                Volume = excluded.Volume,
                ProgramSellAmount = excluded.ProgramSellAmount,
                ProgramBuyAmount = excluded.ProgramBuyAmount,
                ProgramNetAmount = excluded.ProgramNetAmount,
                ProgramNetAmountDelta = excluded.ProgramNetAmountDelta,
                ProgramSellQuantity = excluded.ProgramSellQuantity,
                ProgramBuyQuantity = excluded.ProgramBuyQuantity,
                ProgramNetQuantity = excluded.ProgramNetQuantity,
                ProgramNetQuantityDelta = excluded.ProgramNetQuantityDelta,
                ArbitrageSell = excluded.ArbitrageSell,
                ArbitrageBuy = excluded.ArbitrageBuy,
                ArbitrageNet = excluded.ArbitrageNet,
                NonArbitrageSell = excluded.NonArbitrageSell,
                NonArbitrageBuy = excluded.NonArbitrageBuy,
                NonArbitrageNet = excluded.NonArbitrageNet,
                Direction = excluded.Direction,
                CreatedAt = excluded.CreatedAt;";

        private const string SqlInsertProgramFlowSnapshot = @"
            INSERT INTO ProgramFlowSnapshots (
                SnapshotId, Code, SnapshotTime,
                LatestNetAmount, LatestNetQuantity,
                Delta1mAmount, Delta3mAmount, Delta5mAmount, Delta10mAmount,
                Delta1mQuantity, Delta3mQuantity, Delta5mQuantity, Delta10mQuantity,
                FlowScore, FlowDirection, IsWarmedUp, SourceState, CreatedAt
            ) VALUES (
                @SnapshotId, @Code, @SnapshotTime,
                @LatestNetAmount, @LatestNetQuantity,
                @Delta1mAmount, @Delta3mAmount, @Delta5mAmount, @Delta10mAmount,
                @Delta1mQuantity, @Delta3mQuantity, @Delta5mQuantity, @Delta10mQuantity,
                @FlowScore, @FlowDirection, @IsWarmedUp, @SourceState, @CreatedAt
            )
            ON CONFLICT(SnapshotId) DO UPDATE SET
                Code = excluded.Code,
                SnapshotTime = excluded.SnapshotTime,
                LatestNetAmount = excluded.LatestNetAmount,
                LatestNetQuantity = excluded.LatestNetQuantity,
                Delta1mAmount = excluded.Delta1mAmount,
                Delta3mAmount = excluded.Delta3mAmount,
                Delta5mAmount = excluded.Delta5mAmount,
                Delta10mAmount = excluded.Delta10mAmount,
                Delta1mQuantity = excluded.Delta1mQuantity,
                Delta3mQuantity = excluded.Delta3mQuantity,
                Delta5mQuantity = excluded.Delta5mQuantity,
                Delta10mQuantity = excluded.Delta10mQuantity,
                FlowScore = excluded.FlowScore,
                FlowDirection = excluded.FlowDirection,
                IsWarmedUp = excluded.IsWarmedUp,
                SourceState = excluded.SourceState,
                CreatedAt = excluded.CreatedAt;";

        private const string SqlInsertMinuteCandle = @"
            INSERT OR REPLACE INTO MinuteCandles (
                Code, Name, CandleTime, Interval, Open, High, Low, Close,
                Volume, AccumulatedVolume, Strength, TotalAskQty, TotalBidQty,
                Source, HasAccumulatedVolume, HasStrength, HasHoga, CreatedAt
            ) VALUES (
                @Code, @Name, @CandleTime, @Interval, @Open, @High, @Low, @Close,
                @Volume, @AccumulatedVolume, @Strength, @TotalAskQty, @TotalBidQty,
                @Source,
                CASE WHEN @AccumulatedVolume > 0 THEN 1 ELSE 0 END,
                CASE WHEN @Strength > 0 THEN 1 ELSE 0 END,
                CASE WHEN @TotalAskQty > 0 OR @TotalBidQty > 0 THEN 1 ELSE 0 END,
                @CreatedAt
            );";

        private const string SqlInsertFeatureSnapshot = @"
            INSERT OR REPLACE INTO FeatureSnapshots (
                SnapshotId, Code, SnapshotTime, Price, PriceChangeRate, Strength,
                StrengthDelta, VolumeAccel, BidAskRatio, BuyPressureScore,
                BuyVolume, SellVolume, BuyVolumeRatio, SellVolumeRatio, NetBuyVolume, NetBuyVolumeRatio,
                FrameChange30s, FrameChange1m, FrameChange3m, FrameChange5m,
                FrameVolumeRatio, FrameBuyPressureScore, FrameStrengthDelta,
                MinuteChange15m, MinuteChange30m, MinuteChange60m,
                TodayRangePosition, TwoDayRangePosition, MarketRiskScore, RelativeStrength,
                HoldingQty, EntryPrice, ProfitRate, MaxProfitRate, MinProfitRate, DrawdownRate, HoldingSeconds,
                EstimatedRoundTripCostRate, NetProfitRate, NetMaxProfitRate, NetDrawdownRate,
                SecondTrend, MinuteTrend, HourTrend, Phase, IsSideways, TrendStrength,
                VolatilityRate, RangePosition, TickCount5Min, SecondCandleCount,
                MinuteCandleCount, SignalAction, SignalScore, StrategyName, TradeMode,
                LocalMlUsed, LocalMlAvailable, LocalMlAppliedToTrading, LocalMlScore,
                LocalMlConfidence, LocalMlInfluenceRatio, LocalMlDirection, LocalMlReason,
                DataSourceType, IsReplay, MlSessionId, CreatedAt
            ) VALUES (
                @SnapshotId, @Code, @SnapshotTime, @Price, @PriceChangeRate, @Strength,
                @StrengthDelta, @VolumeAccel, @BidAskRatio, @BuyPressureScore,
                @BuyVolume, @SellVolume, @BuyVolumeRatio, @SellVolumeRatio, @NetBuyVolume, @NetBuyVolumeRatio,
                @FrameChange30s, @FrameChange1m, @FrameChange3m, @FrameChange5m,
                @FrameVolumeRatio, @FrameBuyPressureScore, @FrameStrengthDelta,
                @MinuteChange15m, @MinuteChange30m, @MinuteChange60m,
                @TodayRangePosition, @TwoDayRangePosition, @MarketRiskScore, @RelativeStrength,
                @HoldingQty, @EntryPrice, @ProfitRate, @MaxProfitRate, @MinProfitRate, @DrawdownRate, @HoldingSeconds,
                @EstimatedRoundTripCostRate, @NetProfitRate, @NetMaxProfitRate, @NetDrawdownRate,
                @SecondTrend, @MinuteTrend, @HourTrend, @Phase, @IsSideways, @TrendStrength,
                @VolatilityRate, @RangePosition, @TickCount5Min, @SecondCandleCount,
                @MinuteCandleCount, @SignalAction, @SignalScore, @StrategyName, @TradeMode,
                @LocalMlUsed, @LocalMlAvailable, @LocalMlAppliedToTrading, @LocalMlScore,
                @LocalMlConfidence, @LocalMlInfluenceRatio, @LocalMlDirection, @LocalMlReason,
                @DataSourceType, @IsReplay, @MlSessionId, @CreatedAt
            );";

        private const string SqlInsertSignalLog = @"
            INSERT OR REPLACE INTO SignalLogs (
                SignalId, Code, SignalTime, Action, Score, StrategyName, Reason,
                AiUsed, AiApproved, AiReason, MlUsed, MlApproved, MlScore,
                SnapshotId, TradeMode, TiRexDirection, TiRexConfidence, TiRexPredictedReturn,
                LocalMlUsed, LocalMlAvailable, LocalMlAppliedToTrading, LocalMlScore,
                LocalMlConfidence, LocalMlInfluenceRatio, LocalMlDirection, LocalMlReason,
                DataSourceType, IsReplay, MlSessionId, CreatedAt
            ) VALUES (
                @SignalId, @Code, @SignalTime, @Action, @Score, @StrategyName, @Reason,
                @AiUsed, @AiApproved, @AiReason, @MlUsed, @MlApproved, @MlScore,
                @SnapshotId, @TradeMode, @TiRexDirection, @TiRexConfidence, @TiRexPredictedReturn,
                @LocalMlUsed, @LocalMlAvailable, @LocalMlAppliedToTrading, @LocalMlScore,
                @LocalMlConfidence, @LocalMlInfluenceRatio, @LocalMlDirection, @LocalMlReason,
                @DataSourceType, @IsReplay, @MlSessionId, @CreatedAt
            );";

        private const string SqlInsertDecisionCandidate = @"
            INSERT OR REPLACE INTO DecisionCandidates (
                CandidateId, SnapshotId, SignalId, Code, DecisionTime, Price,
                ActionCandidate, FinalAction, SignalScore, StrategyName, TradeMode,
                BaseFinal, EntryScore, ExitScore, RawAccelerationScore, AppliedAccelerationScore,
                EventMaturity, PairCompletedCount, PairOppositionPenalty, DecisionEventKey,
                Reason, DataSourceType, IsReplay, MlSessionId, CreatedAt
            ) VALUES (
                @CandidateId, @SnapshotId, @SignalId, @Code, @DecisionTime, @Price,
                @ActionCandidate, @FinalAction, @SignalScore, @StrategyName, @TradeMode,
                @BaseFinal, @EntryScore, @ExitScore, @RawAccelerationScore, @AppliedAccelerationScore,
                @EventMaturity, @PairCompletedCount, @PairOppositionPenalty, @DecisionEventKey,
                @Reason, @DataSourceType, @IsReplay, @MlSessionId, @CreatedAt
            );";

        private const string SqlInsertLocalMlEvaluation = @"
            INSERT OR REPLACE INTO LocalMlEvaluationLogs (
                EvaluationId, SnapshotId, SignalId, CandidateId, Code, EvaluationTime,
                ModelKey, ModelName, ModelVersion, EvaluatorType, FeatureSchemaVersion, RuntimeMode,
                IsEnabled, IsAvailable, IsAppliedToTrading, InfluenceRatio,
                RuleAction, FinalAction, RuleScore, FinalScore, StrategyName, TradeMode,
                Direction, Score, Confidence, BuyQualityScore, SellRiskScore, AvoidScore,
                ExpectedReturn, ExpectedDrawdown, Reason,
                DataSourceType, IsReplay, MlSessionId, CreatedAt
            ) VALUES (
                @EvaluationId, @SnapshotId, @SignalId, @CandidateId, @Code, @EvaluationTime,
                @ModelKey, @ModelName, @ModelVersion, @EvaluatorType, @FeatureSchemaVersion, @RuntimeMode,
                @IsEnabled, @IsAvailable, @IsAppliedToTrading, @InfluenceRatio,
                @RuleAction, @FinalAction, @RuleScore, @FinalScore, @StrategyName, @TradeMode,
                @Direction, @Score, @Confidence, @BuyQualityScore, @SellRiskScore, @AvoidScore,
                @ExpectedReturn, @ExpectedDrawdown, @Reason,
                @DataSourceType, @IsReplay, @MlSessionId, @CreatedAt
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
                GrossProfitAmount,
                GrossProfitRate,
                BuyFee,
                SellFee,
                SellTax,
                TotalFeeTax,
                NetProfitAmount,
                NetProfitRate,
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
                @GrossProfitAmount,
                @GrossProfitRate,
                @BuyFee,
                @SellFee,
                @SellTax,
                @TotalFeeTax,
                @NetProfitAmount,
                @NetProfitRate,
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
                CandidateId, ActionCandidate, FinalAction,
                ReturnAfter30s, ReturnAfter1m, ReturnAfter3m, ReturnAfter5m, ReturnAfter10m,
                MaxProfitAfter10m, MaxLossAfter10m, EstimatedRoundTripCostRate,
                NetReturnAfter30s, NetReturnAfter1m, NetReturnAfter3m, NetReturnAfter5m, NetReturnAfter10m,
                NetMaxProfitAfter10m, NetMaxLossAfter10m,
                ActionReturnAfter30s, ActionReturnAfter1m, ActionReturnAfter3m, ActionReturnAfter5m, ActionReturnAfter10m,
                ActionMfeAfter10m, ActionMaeAfter10m, CostAdjustedOutcome,
                HitProfitFirst, HitStopFirst,
                ProfitHitSeconds, StopHitSeconds, LabelBuyGood, LabelAvoid,
                DataSourceType, IsReplay, MlSessionId, CreatedAt
            ) VALUES (
                @OutcomeId, @SnapshotId, @Code, @SnapshotTime, @LabelTime,
                @CandidateId, @ActionCandidate, @FinalAction,
                @ReturnAfter30s, @ReturnAfter1m, @ReturnAfter3m, @ReturnAfter5m, @ReturnAfter10m,
                @MaxProfitAfter10m, @MaxLossAfter10m, @EstimatedRoundTripCostRate,
                @NetReturnAfter30s, @NetReturnAfter1m, @NetReturnAfter3m, @NetReturnAfter5m, @NetReturnAfter10m,
                @NetMaxProfitAfter10m, @NetMaxLossAfter10m,
                @ActionReturnAfter30s, @ActionReturnAfter1m, @ActionReturnAfter3m, @ActionReturnAfter5m, @ActionReturnAfter10m,
                @ActionMfeAfter10m, @ActionMaeAfter10m, @CostAdjustedOutcome,
                @HitProfitFirst, @HitStopFirst,
                @ProfitHitSeconds, @StopHitSeconds, @LabelBuyGood, @LabelAvoid,
                @DataSourceType, @IsReplay, @MlSessionId, @CreatedAt
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
            InitializePerformanceDatabase();
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
            if (ShouldSkipPersistence())
                return;

            InitializePerformanceDatabase();

            if (days <= 0)
                days = 7;

            for (int i = days - 1; i >= 0; i--)
                BuildAndSaveDailyPerformance(DateTime.Today.AddDays(-i), rebuildTradeResults: true);
        }

        public DailyPerformanceRecord GetDailyPerformance(DateTime tradeDate)
        {
            InitializePerformanceDatabase();
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
            InitializePerformanceDatabase();
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

        public DailyPerformanceRecord GetSavedCumulativePerformance()
        {
            InitializePerformanceDatabase();
            return GetSavedTotalPerformance();
        }

        public DailyPerformanceRecord RebuildAndSaveTotalPerformance()
        {
            InitializePerformanceDatabase();
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
                                    CASE
                                        WHEN ABS(IFNULL(NetProfitAmount, 0)) > 0 OR ABS(IFNULL(TotalFeeTax, 0)) > 0
                                        THEN CAST(IFNULL(NetProfitAmount, 0) AS REAL)
                                        ELSE CAST(IFNULL(ProfitAmount, 0) AS REAL)
                                    END AS ProfitAmount,
                                    CASE
                                        WHEN ABS(IFNULL(NetProfitRate, 0)) > 0 OR ABS(IFNULL(TotalFeeTax, 0)) > 0
                                        THEN CAST(IFNULL(NetProfitRate, 0) AS REAL)
                                        ELSE CAST(IFNULL(ProfitRate, 0) AS REAL)
                                    END AS ProfitRate,
                                    CASE
                                        WHEN ABS(IFNULL(GrossProfitAmount, 0)) > 0 OR ABS(IFNULL(TotalFeeTax, 0)) > 0
                                        THEN CAST(IFNULL(GrossProfitAmount, 0) AS REAL)
                                        ELSE CAST(IFNULL(ProfitAmount, 0) AS REAL)
                                    END AS GrossProfitAmount,
                                    CAST(IFNULL(TotalFeeTax, 0) AS REAL) AS TotalFeeTax
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
                    .Where(x => x.GrossProfitAmount > 0)
                    .Sum(x => x.GrossProfitAmount);

                double grossLoss = tradeRows
                    .Where(x => x.GrossProfitAmount < 0)
                    .Sum(x => x.GrossProfitAmount);

                double avgProfitRate = tradeCount == 0 ? 0 : tradeRows.Average(x => x.ProfitRate);
                double bestTradeRate = tradeCount == 0 ? 0 : tradeRows.Max(x => x.ProfitRate);
                double worstTradeRate = tradeCount == 0 ? 0 : tradeRows.Min(x => x.ProfitRate);
                double maxProfit = tradeCount == 0 ? 0 : tradeRows.Max(x => x.ProfitAmount);
                double maxLoss = tradeCount == 0 ? 0 : tradeRows.Min(x => x.ProfitAmount);

                double feeTax = tradeRows.Sum(x => x.TotalFeeTax);
                bool hasTradeCost = feeTax > 0;

                if (!hasTradeCost)
                {
                    feeTax = conn.QueryFirstOrDefault<double>(@"
                        SELECT IFNULL(SUM(IFNULL(Fee, 0) + IFNULL(Tax, 0)), 0)
                        FROM Orders
                        WHERE (
                                OrderTime >= @start
                            AND OrderTime < @end
                        )
                        OR SUBSTR(REPLACE(REPLACE(REPLACE(CAST(OrderTime AS TEXT), '-', ''), ':', ''), ' ', ''), 1, 8) = @dateKey",
                        new { start, end, dateKey });
                }

                double netProfit = hasTradeCost
                    ? tradeRows.Sum(x => x.ProfitAmount)
                    : grossProfit + grossLoss - feeTax;

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
                    NetProfit = netProfit,
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
