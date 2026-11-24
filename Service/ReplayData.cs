// ------------------------------------------------------------------------------
// ReplayData.cs
// 리플레이 테스트용 메모리 데이터/직렬 TR 준비/이벤트 재생/가상계좌/가상주문 통합 파일
// 목적: 파일 난립 방지. ViewModel/AutoTrading 기존 흐름은 그대로 두고 IMarketApiService 이벤트 데이터만 대체한다.
// ------------------------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MoneyCreator.DataBase;
using MoneyCreator.Models;
using MoneyCreator.Service;

namespace MoneyCreator.Service.Replay
{

    // ============================================================
    // Source: ReplaySessionOptions.cs
    // ============================================================
    public sealed class ReplaySessionOptions
    {
        public string Code { get; set; }
        public DateTime TradingDate { get; set; } = DateTime.Today;
        public DateTime StartTime { get; set; } = DateTime.Today.AddHours(9);
        public DateTime EndTime { get; set; } = DateTime.Today.AddHours(15).AddMinutes(20);
        public double Speed { get; set; } = 300.0;
        public long InitialCash { get; set; } = 10_000_000;
        public bool AutoStartOnSubscription { get; set; } = false;

        public static DateTime ResolveSessionEndTime(DateTime tradingDate)
        {
            DateTime date = tradingDate == DateTime.MinValue ? DateTime.Today : tradingDate.Date;
            return date.AddHours(15).AddMinutes(20);
        }

        public static DateTime ResolveSessionEndInclusiveTime(DateTime tradingDate)
        {
            return ResolveSessionEndTime(tradingDate).AddSeconds(59);
        }

        public static DateTime ResolveAnalysisTickSeedStartTime(DateTime tradingDate)
        {
            DateTime date = tradingDate == DateTime.MinValue ? DateTime.Today : tradingDate.Date;
            return date.AddHours(15).AddMinutes(15);
        }

        public static bool IsReplayDataTimeAllowed(DateTime time, DateTime tradingDate, bool allowPreviousTradingDay)
        {
            if (time == DateTime.MinValue)
                return false;

            DateTime date = tradingDate == DateTime.MinValue ? time.Date : tradingDate.Date;
            bool dateAllowed = time.Date == date;

            if (!dateAllowed && allowPreviousTradingDay)
                dateAllowed = time.Date == PreviousBusinessDayForReplayData(date);

            if (!dateAllowed)
                return false;

            return time.TimeOfDay >= new TimeSpan(9, 0, 0) &&
                   time.TimeOfDay <= new TimeSpan(15, 20, 59);
        }

        private static DateTime PreviousBusinessDayForReplayData(DateTime date)
        {
            DateTime d = date.Date.AddDays(-1);
            while (d.DayOfWeek == DayOfWeek.Saturday || d.DayOfWeek == DayOfWeek.Sunday)
                d = d.AddDays(-1);
            return d.Date;
        }

        public void Normalize()
        {
            if (TradingDate == DateTime.MinValue)
                TradingDate = DateTime.Today;

            TradingDate = TradingDate.Date;

            if (StartTime == DateTime.MinValue || StartTime.Date != TradingDate)
                StartTime = TradingDate.AddHours(9);

            if (EndTime == DateTime.MinValue || EndTime.Date != TradingDate)
                EndTime = ResolveSessionEndTime(TradingDate);

            if (EndTime <= StartTime)
                EndTime = ResolveSessionEndTime(TradingDate);

            DateTime cutoff = ResolveSessionEndTime(TradingDate);
            if (EndTime > cutoff)
                EndTime = cutoff;

            if (Speed <= 0 || double.IsNaN(Speed) || double.IsInfinity(Speed))
                Speed = 30.0;

            if (InitialCash <= 0)
                InitialCash = 10_000_000;
        }

        public ReplaySessionOptions CloneForCode(string code)
        {
            var clone = new ReplaySessionOptions
            {
                Code = code,
                TradingDate = TradingDate,
                StartTime = StartTime,
                EndTime = EndTime,
                Speed = Speed,
                InitialCash = InitialCash,
                AutoStartOnSubscription = AutoStartOnSubscription
            };

            clone.Normalize();
            return clone;
        }
    }

    // ============================================================
    // Source: ReplayTimeController.cs
    // ============================================================
    public sealed class ReplayTimeController
    {
        private readonly object _syncRoot = new object();
        private double _speed = 30.0;
        private DateTime _now = DateTime.MinValue;
        private bool _isPaused;

        public double Speed
        {
            get
            {
                lock (_syncRoot)
                    return _speed;
            }
            set
            {
                lock (_syncRoot)
                    _speed = NormalizeSpeed(value);
            }
        }

        public DateTime Now
        {
            get
            {
                lock (_syncRoot)
                    return _now;
            }
        }

        public bool IsPaused
        {
            get
            {
                lock (_syncRoot)
                    return _isPaused;
            }
        }

        public void Reset(DateTime startTime, double speed)
        {
            lock (_syncRoot)
            {
                _now = startTime;
                _speed = NormalizeSpeed(speed);
                _isPaused = false;
            }
        }

        public void Pause()
        {
            lock (_syncRoot)
                _isPaused = true;
        }

        public void Resume()
        {
            lock (_syncRoot)
                _isPaused = false;
        }

        public async Task<bool> WaitWhilePausedAsync(CancellationToken token)
        {
            while (IsPaused)
            {
                if (token.IsCancellationRequested)
                    return false;

                await Task.Delay(50).ConfigureAwait(false);
            }

            return !token.IsCancellationRequested;
        }

        public void AdvanceTo(DateTime replayTime)
        {
            lock (_syncRoot)
            {
                if (replayTime > _now)
                    _now = replayTime;
            }
        }

        public int CalculateDelayMilliseconds(DateTime previousTime, DateTime currentTime)
        {
            if (previousTime == DateTime.MinValue || currentTime <= previousTime)
                return 0;

            double speed;
            lock (_syncRoot)
                speed = _speed;

            var sourceMs = (currentTime - previousTime).TotalMilliseconds;
            var delay = sourceMs / Math.Max(1.0, speed);

            // Replay 스트레스 테스트에서는 같은 초/촘촘한 tick에 1ms 강제 delay를 걸면
            // 실제 장 시간보다 재생이 느려질 수 있다. 1ms 미만은 즉시 흘려보낸다.
            if (delay < 1)
                return 0;

            if (delay > 1000)
                return 1000;

            return (int)Math.Round(delay);
        }

        private static double NormalizeSpeed(double speed)
        {
            if (speed <= 0 || double.IsNaN(speed) || double.IsInfinity(speed))
                return 30.0;

            if (speed > 1000)
                return 1000;

            return speed;
        }
    }


    public static class ReplayTickSourceNames
    {
        public const string TrMemory = "TR_MEMORY";
        public const string DbPaged = "DB_PAGED";
    }

    public interface IReplayTickSource : IDisposable
    {
        string SourceName { get; }
        int TotalCount { get; }
        DateTime FirstTime { get; }
        DateTime LastTime { get; }
        Task InitializeAsync(CancellationToken token);
        Task<TrackedSymbol> ReadNextAsync(CancellationToken token);
    }

    public sealed class ReplayMemoryTickSource : IReplayTickSource
    {
        private readonly List<TrackedSymbol> _ticks;
        private int _index;
        private bool _disposed;

        public ReplayMemoryTickSource(List<TrackedSymbol> ticks)
        {
            _ticks = ticks ?? new List<TrackedSymbol>();
            TotalCount = _ticks.Count(x => x != null);

            foreach (var tick in _ticks)
            {
                if (tick == null || tick.LastUpdated == DateTime.MinValue)
                    continue;

                if (FirstTime == DateTime.MinValue || tick.LastUpdated < FirstTime)
                    FirstTime = tick.LastUpdated;

                if (LastTime == DateTime.MinValue || tick.LastUpdated > LastTime)
                    LastTime = tick.LastUpdated;
            }
        }

        public string SourceName => ReplayTickSourceNames.TrMemory;
        public int TotalCount { get; }
        public DateTime FirstTime { get; }
        public DateTime LastTime { get; }

        public Task InitializeAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<TrackedSymbol> ReadNextAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            if (_disposed)
                return Task.FromResult<TrackedSymbol>(null);

            while (_index < _ticks.Count)
            {
                int currentIndex = _index++;
                var tick = _ticks[currentIndex];
                _ticks[currentIndex] = null;

                if (tick != null)
                    return Task.FromResult(tick);
            }

            return Task.FromResult<TrackedSymbol>(null);
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }

    public sealed class ReplayDbPagedTickSource : IReplayTickSource
    {
        private const int PageSize = 1000;
        private const int PrefetchThreshold = 500;

        private readonly StockDbManager _db;
        private readonly string _code;
        private readonly DateTime _tradingDate;
        private readonly DateTime _fromTime;
        private readonly DateTime _toTime;
        private readonly object _syncRoot = new object();
        private readonly Queue<TrackedSymbol> _queue = new Queue<TrackedSymbol>();

        private DateTime _cursorTime = DateTime.MinValue;
        private long _cursorId;
        private long _accumulatedVolume;
        private Task _prefetchTask;
        private Exception _prefetchError;
        private bool _endOfSource;
        private bool _initialized;
        private bool _disposed;

        public ReplayDbPagedTickSource(
            StockDbManager db,
            string code,
            DateTime tradingDate,
            DateTime fromTime,
            DateTime toTime,
            StockDbManager.ReplayRawTickCoverage coverage)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _code = code;
            _tradingDate = tradingDate.Date;
            _fromTime = fromTime;
            _toTime = toTime;

            if (coverage != null)
            {
                TotalCount = coverage.Count > int.MaxValue
                    ? int.MaxValue
                    : Math.Max(0, (int)coverage.Count);
                FirstTime = coverage.FirstTime;
                LastTime = coverage.LastTime;
            }
        }

        public string SourceName => ReplayTickSourceNames.DbPaged;
        public int TotalCount { get; }
        public DateTime FirstTime { get; }
        public DateTime LastTime { get; }

        public async Task InitializeAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            lock (_syncRoot)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(ReplayDbPagedTickSource));

                if (_initialized)
                    return;
            }

            var page = await Task.Run(
                () => _db.LoadReplayRawTickPage(
                    _code,
                    _tradingDate,
                    _fromTime,
                    _toTime,
                    DateTime.MinValue,
                    0,
                    PageSize),
                token).ConfigureAwait(false);

            lock (_syncRoot)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(ReplayDbPagedTickSource));

                AppendPageLocked(page);
                _initialized = true;
            }
        }

        public async Task<TrackedSymbol> ReadNextAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            while (true)
            {
                TrackedSymbol next = null;
                Task waitTask = null;
                Exception error = null;

                lock (_syncRoot)
                {
                    if (_disposed)
                        return null;

                    if (_prefetchTask != null && _prefetchTask.IsCompleted)
                        _prefetchTask = null;

                    error = _prefetchError;

                    if (error == null && _queue.Count > 0)
                    {
                        next = _queue.Dequeue();

                        if (_queue.Count <= PrefetchThreshold &&
                            !_endOfSource &&
                            _prefetchTask == null)
                        {
                            StartPrefetchLocked(token);
                        }
                    }
                    else if (error == null && !_endOfSource)
                    {
                        if (_prefetchTask == null)
                            StartPrefetchLocked(token);

                        waitTask = _prefetchTask;
                    }
                }

                if (error != null)
                    throw new InvalidOperationException("Replay DB Tick 페이지 조회 실패", error);

                if (next != null)
                    return next;

                if (waitTask == null)
                    return null;

                await waitTask.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
        }

        private void StartPrefetchLocked(CancellationToken token)
        {
            if (_disposed || _endOfSource || _prefetchTask != null)
                return;

            DateTime cursorTime = _cursorTime;
            long cursorId = _cursorId;

            var loadTask = Task.Run(
                () => _db.LoadReplayRawTickPage(
                    _code,
                    _tradingDate,
                    _fromTime,
                    _toTime,
                    cursorTime,
                    cursorId,
                    PageSize),
                token);

            _prefetchTask = loadTask.ContinueWith(
                completed =>
                {
                    lock (_syncRoot)
                    {
                        try
                        {
                            if (_disposed)
                                return;

                            if (completed.IsCanceled)
                            {
                                _prefetchError = new OperationCanceledException();
                                return;
                            }

                            if (completed.IsFaulted)
                            {
                                _prefetchError = completed.Exception == null
                                    ? new InvalidOperationException("Replay DB Tick 페이지 조회 실패")
                                    : completed.Exception.GetBaseException();
                                return;
                            }

                            AppendPageLocked(completed.Result);
                        }
                        finally
                        {
                            // 완료 Task는 ReadNextAsync 진입 시 정리한다.
                            // ContinueWith가 동기 실행된 뒤 대입되는 경합에서도 다음 페이지 선조회가 막히지 않게 한다.
                        }
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void AppendPageLocked(IList<StockDbManager.ReplayRawTickPageRecord> page)
        {
            if (page == null || page.Count == 0)
            {
                _endOfSource = true;
                return;
            }

            foreach (var row in page)
            {
                if (row == null || row.CreatedAt == DateTime.MinValue)
                    continue;

                // MarketBarAggregator의 기존 Side 규격은 B/S다.
                // DB의 BUY/SELL/매수/매도 표기는 여기서 B/S로만 정규화하고,
                // 변환되지 않는 값은 조용히 재생하지 않고 즉시 중단한다.
                string normalizedSide = NormalizeSide(row.Side);
                if (!string.Equals(normalizedSide, "B", StringComparison.Ordinal) &&
                    !string.Equals(normalizedSide, "S", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(string.Format(
                        "Replay DB Tick Side 매핑 실패 / Code={0} / Time={1:yyyy-MM-dd HH:mm:ss.fff} / Id={2} / RawSide={3}",
                        NormalizeCode(row.Code, _code),
                        row.CreatedAt,
                        row.Id,
                        string.IsNullOrWhiteSpace(row.Side) ? "<EMPTY>" : row.Side));
                }

                long volume = Math.Abs((long)row.Volume);
                _accumulatedVolume += volume;
                double price = row.Price > 0 ? row.Price : 1;

                _queue.Enqueue(new TrackedSymbol
                {
                    Code = NormalizeCode(row.Code, _code),
                    LastPrice = price,
                    LastVolume = volume,
                    ClosePrice = price,
                    HighPrice = price,
                    LowPrice = price,
                    AccumulatedVolume = _accumulatedVolume,
                    TotalAskQty = Math.Max(0, row.AskVolume),
                    TotalBidQty = Math.Max(0, row.BidVolume),
                    Strength = row.Strength,
                    Side = normalizedSide,
                    LastUpdated = row.CreatedAt
                });
            }

            var last = page[page.Count - 1];
            if (last != null)
            {
                _cursorTime = last.CreatedAt;
                _cursorId = last.Id;
            }

            if (page.Count < PageSize)
                _endOfSource = true;
        }

        private static string NormalizeCode(string rowCode, string fallbackCode)
        {
            string value = string.IsNullOrWhiteSpace(rowCode) ? fallbackCode : rowCode;
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            value = value.Trim();
            if (value.StartsWith("A", StringComparison.OrdinalIgnoreCase) && value.Length > 1)
                value = value.Substring(1);

            return value;
        }

        private static string NormalizeSide(string side)
        {
            string value = (side ?? string.Empty).Trim();
            string upper = value.ToUpperInvariant();

            if (upper == "BUY" || upper == "B" || value == "+" || value == "매수")
                return "B";

            if (upper == "SELL" || upper == "S" || value == "-" || value == "매도")
                return "S";

            return string.Empty;
        }

        public void Dispose()
        {
            lock (_syncRoot)
            {
                _disposed = true;
                _queue.Clear();
            }
        }
    }

    // ============================================================
    // Source: ReplayMemoryDataStore.cs
    // ============================================================
    public sealed class ReplayMemoryDataSet
    {
        public string Code { get; set; }
        public DateTime TradingDate { get; set; }
        public List<MinuteCandleRecord> MinuteCandles { get; } = new List<MinuteCandleRecord>();
        public List<TrackedSymbol> Ticks { get; } = new List<TrackedSymbol>();
        public List<TrackedSymbol> AnalysisSeedTicks { get; } = new List<TrackedSymbol>();
        public List<ReplayHogaDataFrame> Hogas { get; } = new List<ReplayHogaDataFrame>();
        public List<ProgramTradeInfo> ProgramTrades { get; } = new List<ProgramTradeInfo>();
        public IReplayTickSource TickSource { get; set; }
        public string TickSourceName { get; set; } = ReplayTickSourceNames.TrMemory;
        public int TickCount { get; set; }
        public DateTime PreparedAt { get; set; }
    }

    public sealed class ReplayHogaDataFrame
    {
        public HogaData Hoga { get; set; }
        public DateTime Time { get; set; }
    }

    public sealed class ReplayMemoryDataStore
    {
        private readonly object _syncRoot = new object();
        private readonly Dictionary<string, ReplayMemoryDataSet> _dataByKey =
            new Dictionary<string, ReplayMemoryDataSet>(StringComparer.OrdinalIgnoreCase);

        public void Clear()
        {
            List<ReplayMemoryDataSet> removed;

            lock (_syncRoot)
            {
                removed = _dataByKey.Values.ToList();
                _dataByKey.Clear();
            }

            foreach (var set in removed)
                DisposeDataSet(set);
        }

        public void Clear(string code, DateTime tradingDate)
        {
            if (string.IsNullOrWhiteSpace(code))
                return;

            ReplayMemoryDataSet removed = null;

            lock (_syncRoot)
            {
                string key = BuildKey(code, tradingDate);
                if (_dataByKey.TryGetValue(key, out removed))
                    _dataByKey.Remove(key);
            }

            DisposeDataSet(removed);
        }

        public void Save(
            string code,
            DateTime tradingDate,
            IEnumerable<MinuteCandleRecord> minuteCandles,
            IEnumerable<TrackedSymbol> ticks,
            IEnumerable<ReplayHogaDataFrame> hogas,
            IEnumerable<ProgramTradeInfo> programTrades = null,
            IReplayTickSource tickSource = null,
            IEnumerable<TrackedSymbol> analysisSeedTicks = null)
        {
            if (string.IsNullOrWhiteSpace(code))
                return;

            var set = new ReplayMemoryDataSet
            {
                Code = code,
                TradingDate = tradingDate.Date,
                PreparedAt = DateTime.Now
            };

            if (minuteCandles != null)
            {
                foreach (var candle in minuteCandles)
                {
                    if (candle != null && ReplaySessionOptions.IsReplayDataTimeAllowed(candle.CandleTime, tradingDate, true))
                        set.MinuteCandles.Add(candle);
                }
            }

            if (ticks != null)
            {
                foreach (var tick in ticks)
                {
                    if (tick != null && ReplaySessionOptions.IsReplayDataTimeAllowed(tick.LastUpdated, tradingDate, false))
                        set.Ticks.Add(tick);
                }
            }

            if (analysisSeedTicks != null)
            {
                foreach (var tick in analysisSeedTicks)
                {
                    if (tick != null && ReplaySessionOptions.IsReplayDataTimeAllowed(tick.LastUpdated, tradingDate, false))
                        set.AnalysisSeedTicks.Add(tick);
                }
            }
            else if (set.Ticks.Count > 0)
            {
                DateTime seedStart = ReplaySessionOptions.ResolveAnalysisTickSeedStartTime(tradingDate);
                DateTime seedEnd = ReplaySessionOptions.ResolveSessionEndInclusiveTime(tradingDate);

                foreach (var tick in set.Ticks)
                {
                    if (tick != null && tick.LastUpdated >= seedStart && tick.LastUpdated <= seedEnd)
                        set.AnalysisSeedTicks.Add(tick);
                }
            }

            set.TickSource = tickSource ?? new ReplayMemoryTickSource(set.Ticks);
            set.TickSourceName = set.TickSource == null
                ? ReplayTickSourceNames.TrMemory
                : set.TickSource.SourceName;
            set.TickCount = set.TickSource == null ? set.Ticks.Count : set.TickSource.TotalCount;

            if (hogas != null)
            {
                foreach (var frame in hogas)
                {
                    if (frame != null && frame.Hoga != null && ReplaySessionOptions.IsReplayDataTimeAllowed(frame.Time, tradingDate, false))
                        set.Hogas.Add(frame);
                }
            }

            if (programTrades != null)
            {
                foreach (var trade in programTrades)
                {
                    if (trade == null || string.IsNullOrWhiteSpace(trade.Code) || trade.TradeTime == DateTime.MinValue)
                        continue;

                    if (!AreSameCode(trade.Code, code))
                        continue;

                    if (!ReplaySessionOptions.IsReplayDataTimeAllowed(trade.TradeTime, tradingDate, false))
                        continue;

                    set.ProgramTrades.Add(CloneProgramTrade(trade));
                }

                set.ProgramTrades.Sort((a, b) => a.TradeTime.CompareTo(b.TradeTime));
            }

            ReplayMemoryDataSet previous = null;

            lock (_syncRoot)
            {
                string key = BuildKey(code, tradingDate);
                _dataByKey.TryGetValue(key, out previous);
                _dataByKey[key] = set;
            }

            if (!ReferenceEquals(previous, set))
                DisposeDataSet(previous);
        }

        public ReplayMemoryDataSet GetDataSet(string code, DateTime tradingDate)
        {
            if (string.IsNullOrWhiteSpace(code))
                return null;

            lock (_syncRoot)
            {
                _dataByKey.TryGetValue(BuildKey(code, tradingDate), out var set);
                return Clone(set);
            }
        }

        public List<MinuteCandleRecord> LoadMinuteCandles(string code, DateTime tradingDate)
        {
            if (string.IsNullOrWhiteSpace(code))
                return new List<MinuteCandleRecord>();

            lock (_syncRoot)
            {
                _dataByKey.TryGetValue(BuildKey(code, tradingDate), out var set);
                return set == null
                    ? new List<MinuteCandleRecord>()
                    : new List<MinuteCandleRecord>(set.MinuteCandles);
            }
        }

        public List<MinuteCandleRecord> LoadMinuteCandlesUpTo(string code, DateTime tradingDate, DateTime replayTime)
        {
            if (string.IsNullOrWhiteSpace(code))
                return new List<MinuteCandleRecord>();

            if (replayTime == DateTime.MinValue || replayTime == default(DateTime))
                return LoadMinuteCandles(code, tradingDate);

            lock (_syncRoot)
            {
                _dataByKey.TryGetValue(BuildKey(code, tradingDate), out var set);
                return set == null
                    ? new List<MinuteCandleRecord>()
                    : set.MinuteCandles
                        .Where(x => x != null && x.CandleTime != DateTime.MinValue && x.CandleTime <= replayTime)
                        .OrderBy(x => x.CandleTime)
                        .ToList();
            }
        }

        public List<TrackedSymbol> LoadTicks(string code, DateTime tradingDate)
        {
            if (string.IsNullOrWhiteSpace(code))
                return new List<TrackedSymbol>();

            lock (_syncRoot)
            {
                _dataByKey.TryGetValue(BuildKey(code, tradingDate), out var set);
                return set == null
                    ? new List<TrackedSymbol>()
                    : new List<TrackedSymbol>(set.Ticks);
            }
        }

        // Replay 재생 전용: 재생 중 ticks[i] = null로 참조를 제거하기 위해 실제 저장 리스트를 반환한다.
        // 일반 조회용 LoadTicks는 복사본을 유지한다.
        public List<TrackedSymbol> LoadTicksForPlayback(string code, DateTime tradingDate)
        {
            if (string.IsNullOrWhiteSpace(code))
                return new List<TrackedSymbol>();

            lock (_syncRoot)
            {
                _dataByKey.TryGetValue(BuildKey(code, tradingDate), out var set);
                return set == null ? new List<TrackedSymbol>() : set.Ticks;
            }
        }

        public IReplayTickSource LoadTickSourceForPlayback(string code, DateTime tradingDate)
        {
            if (string.IsNullOrWhiteSpace(code))
                return null;

            lock (_syncRoot)
            {
                _dataByKey.TryGetValue(BuildKey(code, tradingDate), out var set);
                return set == null ? null : set.TickSource;
            }
        }

        public List<TrackedSymbol> LoadAnalysisSeedTicks(string code, DateTime tradingDate)
        {
            if (string.IsNullOrWhiteSpace(code))
                return new List<TrackedSymbol>();

            lock (_syncRoot)
            {
                _dataByKey.TryGetValue(BuildKey(code, tradingDate), out var set);
                return set == null
                    ? new List<TrackedSymbol>()
                    : new List<TrackedSymbol>(set.AnalysisSeedTicks);
            }
        }

        public string GetTickSourceName(string code, DateTime tradingDate)
        {
            if (string.IsNullOrWhiteSpace(code))
                return string.Empty;

            lock (_syncRoot)
            {
                _dataByKey.TryGetValue(BuildKey(code, tradingDate), out var set);
                return set == null ? string.Empty : set.TickSourceName;
            }
        }

        public bool IsDatabaseTickSource(string code, DateTime tradingDate)
        {
            return string.Equals(
                GetTickSourceName(code, tradingDate),
                ReplayTickSourceNames.DbPaged,
                StringComparison.OrdinalIgnoreCase);
        }

        public List<HogaData> LoadHogas(string code, DateTime tradingDate)
        {
            if (string.IsNullOrWhiteSpace(code))
                return new List<HogaData>();

            lock (_syncRoot)
            {
                _dataByKey.TryGetValue(BuildKey(code, tradingDate), out var set);
                return set == null
                    ? new List<HogaData>()
                    : set.Hogas
                        .Where(x => x != null && x.Hoga != null)
                        .Select(x => x.Hoga)
                        .ToList();
            }
        }

        public List<ReplayHogaDataFrame> LoadHogaFrames(string code, DateTime tradingDate)
        {
            if (string.IsNullOrWhiteSpace(code))
                return new List<ReplayHogaDataFrame>();

            lock (_syncRoot)
            {
                _dataByKey.TryGetValue(BuildKey(code, tradingDate), out var set);
                return set == null
                    ? new List<ReplayHogaDataFrame>()
                    : set.Hogas
                        .Where(x => x != null && x.Hoga != null && x.Time != DateTime.MinValue)
                        .Select(x => new ReplayHogaDataFrame { Hoga = x.Hoga, Time = x.Time })
                        .ToList();
            }
        }

        public List<ProgramTradeInfo> LoadProgramTrades(string code, DateTime tradingDate)
        {
            if (string.IsNullOrWhiteSpace(code))
                return new List<ProgramTradeInfo>();

            lock (_syncRoot)
            {
                _dataByKey.TryGetValue(BuildKey(code, tradingDate), out var set);
                return set == null
                    ? new List<ProgramTradeInfo>()
                    : set.ProgramTrades
                        .Where(x => x != null && x.TradeTime != DateTime.MinValue)
                        .OrderBy(x => x.TradeTime)
                        .Select(CloneProgramTrade)
                        .ToList();
            }
        }

        public List<ProgramTradeInfo> LoadProgramTradesUpTo(string code, DateTime tradingDate, DateTime replayTime)
        {
            var trades = LoadProgramTrades(code, tradingDate);
            if (replayTime == DateTime.MinValue || trades.Count == 0)
                return trades;

            return trades
                .Where(x => x != null && x.TradeTime <= replayTime)
                .OrderBy(x => x.TradeTime)
                .ToList();
        }

        public List<TrackedSymbol> LoadRecentTicks(string code, DateTime tradingDate, DateTime replayTime, int minutes)
        {
            List<TrackedSymbol> ticks;

            if (string.IsNullOrWhiteSpace(code))
                return new List<TrackedSymbol>();

            lock (_syncRoot)
            {
                _dataByKey.TryGetValue(BuildKey(code, tradingDate), out var set);
                ticks = set == null ? null : set.Ticks;
            }

            if (ticks == null || ticks.Count == 0)
                return new List<TrackedSymbol>();

            if (minutes <= 0)
                minutes = 10;

            DateTime firstTickTime = DateTime.MinValue;
            foreach (var tick in ticks)
            {
                if (tick != null && tick.LastUpdated != DateTime.MinValue)
                {
                    firstTickTime = tick.LastUpdated;
                    break;
                }
            }

            if (firstTickTime == DateTime.MinValue)
                return new List<TrackedSymbol>();

            if (replayTime == DateTime.MinValue)
                replayTime = firstTickTime;

            DateTime from = replayTime.AddMinutes(-minutes);
            var recent = new List<TrackedSymbol>();

            foreach (var tick in ticks)
            {
                if (tick == null)
                    continue;

                if (tick.LastUpdated >= from && tick.LastUpdated <= replayTime)
                    recent.Add(tick);
            }

            if (recent.Count > 0)
                return recent;

            DateTime firstTo = firstTickTime.AddMinutes(minutes);
            var fallback = new List<TrackedSymbol>();

            foreach (var tick in ticks)
            {
                if (tick == null)
                    continue;

                if (tick.LastUpdated >= firstTickTime && tick.LastUpdated <= firstTo)
                    fallback.Add(tick);
            }

            return fallback;
        }

        public HogaData FindNearestHoga(string code, DateTime tradingDate, DateTime time)
        {
            var frames = LoadHogaFrames(code, tradingDate);
            if (frames.Count == 0)
                return null;

            HogaData best = null;
            double bestGap = double.MaxValue;

            foreach (var frame in frames)
            {
                if (frame == null || frame.Hoga == null || frame.Time == DateTime.MinValue)
                    continue;

                double gap = Math.Abs((time - frame.Time).TotalMilliseconds);
                if (gap >= bestGap)
                    continue;

                best = frame.Hoga;
                bestGap = gap;
            }

            return best;
        }

        private static void DisposeDataSet(ReplayMemoryDataSet set)
        {
            if (set == null || set.TickSource == null)
                return;

            try
            {
                set.TickSource.Dispose();
            }
            catch
            {
            }
        }

        private static ReplayMemoryDataSet Clone(ReplayMemoryDataSet source)
        {
            if (source == null)
                return null;

            var clone = new ReplayMemoryDataSet
            {
                Code = source.Code,
                TradingDate = source.TradingDate,
                TickSourceName = source.TickSourceName,
                TickCount = source.TickCount,
                PreparedAt = source.PreparedAt
            };

            clone.MinuteCandles.AddRange(source.MinuteCandles);
            clone.Ticks.AddRange(source.Ticks);
            clone.AnalysisSeedTicks.AddRange(source.AnalysisSeedTicks);
            foreach (var frame in source.Hogas)
            {
                if (frame != null && frame.Hoga != null && frame.Time != DateTime.MinValue)
                    clone.Hogas.Add(new ReplayHogaDataFrame { Hoga = frame.Hoga, Time = frame.Time });
            }
            foreach (var trade in source.ProgramTrades)
            {
                if (trade != null)
                    clone.ProgramTrades.Add(CloneProgramTrade(trade));
            }
            return clone;
        }

        public static ProgramTradeInfo CloneProgramTrade(ProgramTradeInfo source)
        {
            if (source == null)
                return null;

            return new ProgramTradeInfo
            {
                Code = source.Code,
                Name = source.Name,
                TradeTime = source.TradeTime,
                CurrentPrice = source.CurrentPrice,
                Volume = source.Volume,
                ProgramSellAmount = source.ProgramSellAmount,
                ProgramBuyAmount = source.ProgramBuyAmount,
                ProgramNetAmount = source.ProgramNetAmount,
                ProgramNetAmountDelta = source.ProgramNetAmountDelta,
                ProgramSellQuantity = source.ProgramSellQuantity,
                ProgramBuyQuantity = source.ProgramBuyQuantity,
                ProgramNetQuantity = source.ProgramNetQuantity,
                ProgramNetQuantityDelta = source.ProgramNetQuantityDelta,
                ArbitrageSell = source.ArbitrageSell,
                ArbitrageBuy = source.ArbitrageBuy,
                ArbitrageNet = source.ArbitrageNet,
                NonArbitrageSell = source.NonArbitrageSell,
                NonArbitrageBuy = source.NonArbitrageBuy,
                NonArbitrageNet = source.NonArbitrageNet,
                Source = source.Source,
                Direction = source.Direction,
                CreatedAt = source.CreatedAt
            };
        }

        private static bool AreSameCode(string left, string right)
        {
            return string.Equals(NormalizeCode(left), NormalizeCode(right), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return string.Empty;

            code = code.Trim();
            if (code.StartsWith("A", StringComparison.OrdinalIgnoreCase) && code.Length > 1)
                code = code.Substring(1);
            return code;
        }

        private static string BuildKey(string code, DateTime tradingDate)
        {
            return $"{code.Trim()}_{tradingDate.Date:yyyyMMdd}";
        }

        public static DateTime ResolveHogaTime(HogaData hoga)
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
    }

    public static class ReplayProgramFlowCalculator
    {
        private const int ProgramTradeUiWarmupMinutes = 10;

        public static ProgramFlowSnapshotRecord BuildSnapshot(string code, DateTime snapshotTime, IEnumerable<ProgramTradeInfo> trades, string sourceState)
        {
            if (string.IsNullOrWhiteSpace(code) || trades == null)
                return null;

            var ordered = trades
                .Where(x => x != null && x.TradeTime != DateTime.MinValue)
                .OrderBy(x => x.TradeTime)
                .ToList();

            if (ordered.Count == 0)
                return null;

            var latest = ordered.LastOrDefault(x => x.TradeTime <= snapshotTime) ?? ordered.Last();
            if (latest == null)
                return null;

            long amount1 = ResolveProgramDeltaAmount(ordered, latest, TimeSpan.FromMinutes(1));
            long amount3 = ResolveProgramDeltaAmount(ordered, latest, TimeSpan.FromMinutes(3));
            long amount5 = ResolveProgramDeltaAmount(ordered, latest, TimeSpan.FromMinutes(5));
            long amount10 = ResolveProgramDeltaAmount(ordered, latest, TimeSpan.FromMinutes(10));

            long qty1 = ResolveProgramDeltaQuantity(ordered, latest, TimeSpan.FromMinutes(1));
            long qty3 = ResolveProgramDeltaQuantity(ordered, latest, TimeSpan.FromMinutes(3));
            long qty5 = ResolveProgramDeltaQuantity(ordered, latest, TimeSpan.FromMinutes(5));
            long qty10 = ResolveProgramDeltaQuantity(ordered, latest, TimeSpan.FromMinutes(10));

            bool warmedUp = ordered.Any(x => x.TradeTime <= latest.TradeTime.AddMinutes(-ProgramTradeUiWarmupMinutes));
            double score = CalculateProgramFlowScore(amount1, amount3, amount5, amount10, qty1, qty3, qty5, qty10);

            return new ProgramFlowSnapshotRecord
            {
                SnapshotId = $"RPF_{code}_{latest.TradeTime:yyyyMMddHHmmss}",
                Code = code,
                SnapshotTime = latest.TradeTime,
                LatestNetAmount = latest.ProgramNetAmount,
                LatestNetQuantity = latest.ProgramNetQuantity,
                Delta1mAmount = amount1,
                Delta3mAmount = amount3,
                Delta5mAmount = amount5,
                Delta10mAmount = amount10,
                Delta1mQuantity = qty1,
                Delta3mQuantity = qty3,
                Delta5mQuantity = qty5,
                Delta10mQuantity = qty10,
                FlowScore = score,
                FlowDirection = ResolveProgramFlowDirection(score, warmedUp),
                IsWarmedUp = warmedUp,
                SourceState = string.IsNullOrWhiteSpace(sourceState) ? "REPLAY_DB" : sourceState,
                CreatedAt = DateTime.Now
            };
        }

        private static long ResolveProgramDeltaAmount(List<ProgramTradeInfo> ordered, ProgramTradeInfo latest, TimeSpan span)
        {
            if (ordered == null || latest == null)
                return 0;

            DateTime target = latest.TradeTime - span;
            var baseline = ordered.LastOrDefault(x => x.TradeTime <= target) ?? ordered.FirstOrDefault();
            return baseline == null ? 0 : latest.ProgramNetAmount - baseline.ProgramNetAmount;
        }

        private static long ResolveProgramDeltaQuantity(List<ProgramTradeInfo> ordered, ProgramTradeInfo latest, TimeSpan span)
        {
            if (ordered == null || latest == null)
                return 0;

            DateTime target = latest.TradeTime - span;
            var baseline = ordered.LastOrDefault(x => x.TradeTime <= target) ?? ordered.FirstOrDefault();
            return baseline == null ? 0 : latest.ProgramNetQuantity - baseline.ProgramNetQuantity;
        }

        private static double CalculateProgramFlowScore(long amount1, long amount3, long amount5, long amount10, long qty1, long qty3, long qty5, long qty10)
        {
            double amountScore = 0;
            amountScore += NormalizeProgramAmountScore(amount1) * 0.35;
            amountScore += NormalizeProgramAmountScore(amount3) * 0.30;
            amountScore += NormalizeProgramAmountScore(amount5) * 0.20;
            amountScore += NormalizeProgramAmountScore(amount10) * 0.15;

            double quantityScore = 0;
            quantityScore += NormalizeProgramQuantityScore(qty1) * 0.35;
            quantityScore += NormalizeProgramQuantityScore(qty3) * 0.30;
            quantityScore += NormalizeProgramQuantityScore(qty5) * 0.20;
            quantityScore += NormalizeProgramQuantityScore(qty10) * 0.15;

            double score = amountScore * 0.7 + quantityScore * 0.3;
            if (score > 100) score = 100;
            if (score < -100) score = -100;
            return score;
        }

        private static double NormalizeProgramAmountScore(long amount)
        {
            const double oneHundredMillion = 100000000.0;
            return Math.Max(-100, Math.Min(100, amount / oneHundredMillion * 10.0));
        }

        private static double NormalizeProgramQuantityScore(long quantity)
        {
            const double baseQuantity = 10000.0;
            return Math.Max(-100, Math.Min(100, quantity / baseQuantity * 10.0));
        }

        private static string ResolveProgramFlowDirection(double score, bool warmedUp)
        {
            if (!warmedUp)
                return "WarmUp";
            if (score >= 20)
                return "BuyFlow";
            if (score <= -20)
                return "SellFlow";
            return "Flat";
        }
    }

    // ============================================================
    // Source: ReplayDataPrepareService.cs
    // ============================================================
    public sealed class ReplayDataPrepareResult
    {
        public string Code { get; set; }
        public DateTime TradingDate { get; set; }
        public int TickCount { get; set; }
        public int MinuteCandleCount { get; set; }
        public int HogaCount { get; set; }
        public int ProgramTradeCount { get; set; }
        public string TickSourceName { get; set; }
        public DateTime FirstTickTime { get; set; }
        public DateTime LastTickTime { get; set; }
        public string Message { get; set; }
        public bool IsReady => TickCount > 0 && MinuteCandleCount > 0;
    }

    public sealed class ReplayDataPrepareService
    {
        // Replay 선택 날짜 전달을 기존 IMarketApiService 계약 변경 없이 이 준비 서비스 안에서 공통 관리한다.
        // 별도 최상위 Registry 형식을 두지 않아 MainWindowViewModel/KiwoomApiService의 컴파일 참조가 분리되지 않는다.
        private static readonly ConcurrentDictionary<string, DateTime> RequestedTradingDateByWindowCode =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, DateTime> RequestedMinuteTargetDateByWindowCode =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public static void SetRequestedTradingDate(string windowId, string code, DateTime tradingDate)
        {
            if (string.IsNullOrWhiteSpace(windowId) ||
                string.IsNullOrWhiteSpace(code) ||
                tradingDate == DateTime.MinValue)
            {
                return;
            }

            RequestedTradingDateByWindowCode[BuildRequestedDateKey(windowId, code)] = tradingDate.Date;
        }

        private static bool TryTakeRequestedTradingDate(
            string windowId,
            string code,
            out DateTime tradingDate)
        {
            tradingDate = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(windowId) || string.IsNullOrWhiteSpace(code))
                return false;

            return RequestedTradingDateByWindowCode.TryRemove(
                BuildRequestedDateKey(windowId, code),
                out tradingDate);
        }

        private static void SetRequestedMinuteTargetDate(
            string windowId,
            string code,
            DateTime tradingDate)
        {
            if (string.IsNullOrWhiteSpace(windowId) ||
                string.IsNullOrWhiteSpace(code) ||
                tradingDate == DateTime.MinValue)
            {
                return;
            }

            RequestedMinuteTargetDateByWindowCode[BuildRequestedDateKey(windowId, code)] = tradingDate.Date;
        }

        public static bool TryGetRequestedMinuteTargetDate(
            string windowId,
            string code,
            out DateTime tradingDate)
        {
            tradingDate = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(windowId) || string.IsNullOrWhiteSpace(code))
                return false;

            return RequestedMinuteTargetDateByWindowCode.TryGetValue(
                BuildRequestedDateKey(windowId, code),
                out tradingDate);
        }

        private static void RemoveRequestedMinuteTargetDate(string windowId, string code)
        {
            if (string.IsNullOrWhiteSpace(windowId) || string.IsNullOrWhiteSpace(code))
                return;

            DateTime ignored;
            RequestedMinuteTargetDateByWindowCode.TryRemove(
                BuildRequestedDateKey(windowId, code),
                out ignored);
        }

        private static string BuildRequestedDateKey(string windowId, string code)
        {
            return (windowId ?? string.Empty).Trim() + "|" +
                   (code ?? string.Empty).Trim();
        }

        private readonly IMarketApiService _liveApi;
        private readonly ReplayMemoryDataStore _memoryStore;
        private readonly object _prepareLock = new object();
        private bool _isPreparing;

        public ReplayDataPrepareService(IMarketApiService liveApi, ReplayMemoryDataStore memoryStore)
        {
            _liveApi = liveApi ?? throw new ArgumentNullException(nameof(liveApi));
            _memoryStore = memoryStore ?? throw new ArgumentNullException(nameof(memoryStore));
        }

        public async Task<ReplayDataPrepareResult> PrepareAsync(
            string windowId,
            string code,
            DateTime tradingDate,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return new ReplayDataPrepareResult
                {
                    Code = code,
                    TradingDate = tradingDate.Date,
                    Message = "종목코드 없음"
                };
            }

            lock (_prepareLock)
            {
                if (_isPreparing)
                {
                    return new ReplayDataPrepareResult
                    {
                        Code = code,
                        TradingDate = tradingDate.Date,
                        Message = "Replay 데이터 준비가 이미 실행 중"
                    };
                }

                _isPreparing = true;
            }

            IReplayTickSource preparedTickSource = null;

            try
            {
                DateTime requestedDate = tradingDate == DateTime.MinValue
                    ? DateTime.Today
                    : tradingDate.Date;

                DateTime selectedDate;
                if (TryTakeRequestedTradingDate(windowId, code, out selectedDate))
                    requestedDate = selectedDate.Date;

                string requestKey = Guid.NewGuid().ToString("N").Substring(0, 8);

                // 중요:
                // KiwoomApiService.Api_OnReceiveTrData는 sRQName을 '_'로 Split해서
                // parts[2]를 windowId로 사용한다. windowId에 '_'가 들어가면 응답 라우팅이 깨진다.
                // 따라서 Replay 수집용 임시 windowId에는 '_'를 절대 넣지 않는다.
                string minuteWindowId = $"RPMIN{windowId}{requestKey}";
                string tickWindowId = $"RPTICK{windowId}{requestKey}";
                string hogaWindowId = $"RPHOGA{windowId}{requestKey}";

                // 사용자가 선택한 날짜가 Replay 거래일의 최종 권한이다.
                // 키움 분봉은 최신 페이지부터 목표일까지 연속조회하되 응답 날짜로 선택일을 덮어쓰지 않는다.
                var minuteResult = await CollectMinuteCandlesAsync(
                    minuteWindowId,
                    code,
                    requestedDate,
                    token).ConfigureAwait(false);

                var minuteCandles = minuteResult.Candles;
                tradingDate = requestedDate.Date;

                _memoryStore.Clear(code, tradingDate);

                DateTime replayFrom = tradingDate.AddHours(9);
                DateTime replayTo = ReplaySessionOptions.ResolveSessionEndInclusiveTime(tradingDate);
                DateTime seedFrom = ReplaySessionOptions.ResolveAnalysisTickSeedStartTime(tradingDate);
                DateTime seedTo = replayTo;

                var db = _liveApi == null ? null : _liveApi.DbManager;
                StockDbManager.ReplayRawTickCoverage dbCoverage = null;
                bool useDatabaseTicks = false;
                string dbSelectionMessage;

                if (db == null)
                {
                    dbSelectionMessage = "DBManager 없음";
                }
                else if (minuteCandles.Count == 0)
                {
                    dbSelectionMessage = "선택일 분봉 없음";
                }
                else
                {
                    dbCoverage = db.GetReplayRawTickCoverage(
                        code,
                        tradingDate,
                        replayFrom,
                        replayTo);

                    useDatabaseTicks = dbCoverage != null && dbCoverage.IsUsable;

                    if (useDatabaseTicks)
                    {
                        dbSelectionMessage =
                            $"DB 사용 Count={dbCoverage.Count:N0}/ValidSide={dbCoverage.ValidSideCount:N0}";
                    }
                    else if (dbCoverage == null)
                    {
                        dbSelectionMessage = "DB Coverage 없음";
                    }
                    else if (!string.IsNullOrWhiteSpace(dbCoverage.ErrorMessage))
                    {
                        dbSelectionMessage = $"DB 조회 오류={dbCoverage.ErrorMessage}";
                    }
                    else
                    {
                        dbSelectionMessage =
                            $"DB 사용불가 File={dbCoverage.DatabaseExists}/Table={dbCoverage.TableExists}/" +
                            $"Count={dbCoverage.Count:N0}/ValidSide={dbCoverage.ValidSideCount:N0}";
                    }
                }

                var ticks = new List<TrackedSymbol>();
                var analysisSeedTicks = new List<TrackedSymbol>();
                string tickSourceName;
                int tickCount;
                DateTime firstTickTime;
                DateTime lastTickTime;

                if (useDatabaseTicks)
                {
                    var dbTickSource = new ReplayDbPagedTickSource(
                        db,
                        code,
                        tradingDate,
                        replayFrom,
                        replayTo,
                        dbCoverage);

                    await dbTickSource.InitializeAsync(token).ConfigureAwait(false);
                    preparedTickSource = dbTickSource;

                    analysisSeedTicks = await LoadDatabaseTicksForRangeAsync(
                        db,
                        code,
                        tradingDate,
                        seedFrom,
                        seedTo,
                        token).ConfigureAwait(false);

                    tickSourceName = dbTickSource.SourceName;
                    tickCount = dbTickSource.TotalCount;
                    firstTickTime = dbTickSource.FirstTime;
                    lastTickTime = dbTickSource.LastTime;
                }
                else
                {
                    token.ThrowIfCancellationRequested();

                    DateTime latestCompletedTradingDate = ResolveReplayTradingDate(DateTime.Now, DateTime.MinValue);
                    DateTime previousBusinessDate = ResolvePreviousBusinessDay(DateTime.Now.Date);
                    bool allowKiwoomTickFallback =
                        tradingDate.Date == latestCompletedTradingDate.Date ||
                        tradingDate.Date == previousBusinessDate.Date;

                    if (minuteCandles.Count == 0)
                    {
                        ticks = new List<TrackedSymbol>();
                        dbSelectionMessage = "선택일 분봉 없음 - Tick TR 생략";
                    }
                    else if (!allowKiwoomTickFallback)
                    {
                        // 최신 완료 거래일보다 과거인 날짜는 RawData_yyyyMMdd DB 전용이다.
                        // 키움 opt10079는 과거 날짜 완전수집을 보장하지 않으므로 호출 자체를 하지 않는다.
                        ticks = new List<TrackedSymbol>();
                        dbSelectionMessage =
                            $"과거일 DB 전용 - RawData_{tradingDate:yyyyMMdd}.db에 사용 가능한 종목 Tick 없음";
                    }
                    else
                    {
                        await DelayBeforeNextReplayTrAsync(token).ConfigureAwait(false);

                        // 최신 완료 거래일과 직전 거래일만 DB가 없거나 Side가 온전하지 않을 때 opt10079로 대체한다.
                        // 한 Replay 세션 안에서 DB와 TR Tick을 섞지 않는다.
                        ticks = await CollectTicksAsync(
                            tickWindowId,
                            code,
                            tradingDate,
                            token).ConfigureAwait(false);
                    }

                    var replayTicksForSeed = ticks ?? new List<TrackedSymbol>();
                    analysisSeedTicks = replayTicksForSeed
                        .Where(x => x != null && x.LastUpdated >= seedFrom && x.LastUpdated <= seedTo)
                        .ToList();

                    tickSourceName = ReplayTickSourceNames.TrMemory;
                    tickCount = replayTicksForSeed.Count;
                    firstTickTime = replayTicksForSeed.Count > 0
                        ? replayTicksForSeed[0].LastUpdated
                        : DateTime.MinValue;
                    lastTickTime = replayTicksForSeed.Count > 0
                        ? replayTicksForSeed[replayTicksForSeed.Count - 1].LastUpdated
                        : DateTime.MinValue;
                }

                token.ThrowIfCancellationRequested();
                await DelayBeforeNextReplayTrAsync(token).ConfigureAwait(false);

                var replayTicks = BuildReplayTicks(code, tradingDate, minuteCandles, ticks);

                var hogas = await CollectHogasAsync(
                    hogaWindowId,
                    code,
                    tradingDate,
                    token).ConfigureAwait(false);

                var programTrades = LoadSavedProgramTrades(code, tradingDate);

                _memoryStore.Save(
                    code,
                    tradingDate,
                    minuteCandles,
                    replayTicks,
                    hogas,
                    programTrades,
                    preparedTickSource,
                    analysisSeedTicks);

                // MemoryStore가 소유권을 넘겨받았으므로 catch/finally에서 Dispose하지 않는다.
                preparedTickSource = null;

                string tickMessage;
                if (string.Equals(tickSourceName, ReplayTickSourceNames.DbPaged, StringComparison.OrdinalIgnoreCase))
                {
                    tickMessage =
                        $"DB 페이지 Tick 사용 / Count={tickCount:N0} / " +
                        $"First={firstTickTime:HH:mm:ss.fff}, Last={lastTickTime:HH:mm:ss.fff} / " +
                        "Page=5,000 / PrefetchAt=2,500";
                }
                else if (tickCount > 0)
                {
                    tickMessage =
                        $"TR Tick 사용 / First={firstTickTime:HH:mm:ss}, Last={lastTickTime:HH:mm:ss} / " +
                        $"Fallback={dbSelectionMessage}";
                }
                else
                {
                    tickMessage = $"Tick 원천데이터 없음 / {dbSelectionMessage}";
                }

                string hogaMessage = hogas.Count > 0
                    ? "호가 원천데이터 수집 완료"
                    : "호가 TR 수집 메서드 없음 또는 응답 없음 - 재생 시 틱 기반 fallback 사용";

                string programMessage = programTrades.Count > 0
                    ? $"프로그램매매 저장데이터 사용 / Program={programTrades.Count:N0}개"
                    : "프로그램매매 저장데이터 없음 - Replay 분석에서 제외";

                bool replayReady = tickCount > 0 && minuteCandles.Count > 0;
                string minuteMessage = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "MinuteTarget={0:yyyyMMdd} / Minute={1:N0}개 / Pages={2:N0} / ReplayNext={3:N0} / MinuteStop={4}",
                    tradingDate,
                    minuteCandles.Count,
                    minuteResult.ReceivedPageCount,
                    minuteResult.ReplayContinuationRequestCount,
                    string.IsNullOrWhiteSpace(minuteResult.StopReason) ? "-" : minuteResult.StopReason);

                return new ReplayDataPrepareResult
                {
                    Code = code,
                    TradingDate = tradingDate,
                    MinuteCandleCount = minuteCandles.Count,
                    TickCount = tickCount,
                    HogaCount = hogas.Count,
                    ProgramTradeCount = programTrades.Count,
                    TickSourceName = tickSourceName,
                    FirstTickTime = firstTickTime,
                    LastTickTime = lastTickTime,
                    Message = $"{(replayReady ? "Replay 준비 완료" : "Replay 준비 실패")} / {minuteMessage} / Source={tickSourceName} / {tickMessage} / {hogaMessage} / {programMessage}"
                };
            }
            catch (OperationCanceledException)
            {
                return new ReplayDataPrepareResult
                {
                    Code = code,
                    TradingDate = tradingDate.Date,
                    Message = "Replay 데이터 준비 취소"
                };
            }
            catch (Exception ex)
            {
                return new ReplayDataPrepareResult
                {
                    Code = code,
                    TradingDate = tradingDate.Date,
                    Message = ex.Message
                };
            }
            finally
            {
                if (preparedTickSource != null)
                {
                    try
                    {
                        preparedTickSource.Dispose();
                    }
                    catch
                    {
                    }
                }

                lock (_prepareLock)
                    _isPreparing = false;
            }
        }

        private static async Task<List<TrackedSymbol>> LoadDatabaseTicksForRangeAsync(
            StockDbManager db,
            string code,
            DateTime tradingDate,
            DateTime fromTime,
            DateTime toTime,
            CancellationToken token)
        {
            var result = new List<TrackedSymbol>();
            if (db == null || string.IsNullOrWhiteSpace(code))
                return result;

            var coverage = db.GetReplayRawTickCoverage(
                code,
                tradingDate,
                fromTime,
                toTime);

            if (coverage == null || !coverage.IsUsable)
                return result;

            var source = new ReplayDbPagedTickSource(
                db,
                code,
                tradingDate,
                fromTime,
                toTime,
                coverage);

            try
            {
                await source.InitializeAsync(token).ConfigureAwait(false);

                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var tick = await source.ReadNextAsync(token).ConfigureAwait(false);
                    if (tick == null)
                        break;

                    result.Add(tick);
                }
            }
            finally
            {
                source.Dispose();
            }

            return result;
        }

        private List<ProgramTradeInfo> LoadSavedProgramTrades(string code, DateTime tradingDate)
        {
            try
            {
                var db = _liveApi == null ? null : _liveApi.DbManager;
                if (db == null)
                    return new List<ProgramTradeInfo>();

                var trades = db.LoadReplayProgramTrades(code, tradingDate);
                if (trades == null || trades.Count == 0)
                    return new List<ProgramTradeInfo>();

                return trades
                    .Where(x => x != null && ReplaySessionOptions.IsReplayDataTimeAllowed(x.TradeTime, tradingDate, false))
                    .OrderBy(x => x.TradeTime)
                    .Select(ReplayMemoryDataStore.CloneProgramTrade)
                    .ToList();
            }
            catch
            {
                return new List<ProgramTradeInfo>();
            }
        }

        private static List<TrackedSymbol> BuildReplayTicks(
            string code,
            DateTime tradingDate,
            List<MinuteCandleRecord> minuteCandles,
            List<TrackedSymbol> rawTicks)
        {
            // CollectTicksAsync에서 이미 날짜 필터 + 페이지 순서 보정이 끝난 리스트를 만든다.
            // 여기서 다시 Normalize하면 70만+ tick 대량 리스트를 한 번 더 복제한다.
            // Replay 분석차트는 실제 opt10079 원천틱만 사용한다.
            return rawTicks ?? new List<TrackedSymbol>();
        }

        private static List<TrackedSymbol> NormalizeCollectedTicks(
            string code,
            DateTime tradingDate,
            IEnumerable<TrackedSymbol> rawTicks)
        {
            var buffer = new ReplayTickMergeBuffer();
            buffer.Merge(code, rawTicks, tradingDate.Date);
            return buffer.ToAscendingList();
        }

        private static List<TrackedSymbol> BuildTicksFromMinuteCandles(
            string code,
            DateTime tradingDate,
            IEnumerable<MinuteCandleRecord> minuteCandles)
        {
            // Replay 분석차트는 실제 opt10079 틱만 사용한다.
            // 분봉 기반 가상틱 생성은 15/35초 단위 차트를 만들기 때문에 사용하지 않는다.
            return new List<TrackedSymbol>();
        }

        private static double ResolvePositivePrice(params double[] values)
        {
            foreach (double value in values)
            {
                if (value > 0)
                    return value;
            }

            return 1;
        }

        private sealed class ReplayMinuteCollectResult
        {
            public DateTime TradingDate { get; set; }
            public List<MinuteCandleRecord> Candles { get; set; } = new List<MinuteCandleRecord>();
            public int ReceivedPageCount { get; set; }
            public int ReplayContinuationRequestCount { get; set; }
            public string StopReason { get; set; }
        }

        private const int ReplayMinuteMaxContinuationRequests = 40;
        private const int ReplayMinuteContinuationDelayMilliseconds = 350;
        private const int ReplayMinuteMaxRepeatedTerminalPages = 3;

        private async Task<ReplayMinuteCollectResult> CollectMinuteCandlesAsync(
            string windowId,
            string code,
            DateTime targetTradingDate,
            CancellationToken token)
        {
            DateTime targetDate = targetTradingDate == DateTime.MinValue
                ? ResolveReplayTradingDate(DateTime.Now, DateTime.MinValue)
                : targetTradingDate.Date;

            var rawCandles = new List<MinuteCandleRecord>();
            var stateLock = new object();
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            string minuteScreenNo = _liveApi.GetScreenNo();
            string lastTerminalPageSignature = string.Empty;
            string stopReason = "Waiting";
            int receivedPageCount = 0;
            int continuationRequestCount = 0;
            int repeatedTerminalPageCount = 0;
            int continuationRequestPending = 0;
            bool targetDateObserved = false;

            void Complete(string reason)
            {
                lock (stateLock)
                    stopReason = string.IsNullOrWhiteSpace(reason) ? "Completed" : reason;

                tcs.TrySetResult(true);
            }

            void RequestReplayContinuation()
            {
                if (tcs.Task.IsCompleted || token.IsCancellationRequested)
                    return;

                if (Interlocked.CompareExchange(ref continuationRequestPending, 1, 0) != 0)
                    return;

                int requestNo = Interlocked.Increment(ref continuationRequestCount);
                if (requestNo > ReplayMinuteMaxContinuationRequests)
                {
                    Interlocked.Exchange(ref continuationRequestPending, 0);
                    Complete("ReplayContinuationLimit");
                    return;
                }

                Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(
                            ReplayMinuteContinuationDelayMilliseconds,
                            token).ConfigureAwait(false);

                        if (tcs.Task.IsCompleted || token.IsCancellationRequested)
                            return;

                        // KiwoomApiService의 날짜 종료조건은 변경하지 않는다.
                        // 기본 수집이 isLast로 끝났는데 선택일에 도달하지 못한 경우에만
                        // Replay 준비 서비스가 같은 요청 식별자로 다음 페이지(next=2)를 이어간다.
                        _liveApi.RequestMinuteCandles(
                            windowId,
                            code,
                            minuteScreenNo,
                            daysToLoad: 10,
                            maxRows: 0,
                            priority: 0,
                            next: 2);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref continuationRequestPending, 0);
                    }
                });
            }

            void Handler(string wId, string receivedCode, List<MinuteCandleRecord> candles, bool isLast)
            {
                if (!string.Equals(wId, windowId, StringComparison.OrdinalIgnoreCase))
                    return;

                if (!string.Equals(receivedCode, code, StringComparison.OrdinalIgnoreCase))
                    return;

                var pageCandles = candles == null
                    ? new List<MinuteCandleRecord>()
                    : candles
                        .Where(x => x != null && x.CandleTime != DateTime.MinValue)
                        .ToList();

                DateTime oldestPageTime = pageCandles.Count > 0
                    ? pageCandles.Min(x => x.CandleTime)
                    : DateTime.MinValue;
                DateTime newestPageTime = pageCandles.Count > 0
                    ? pageCandles.Max(x => x.CandleTime)
                    : DateTime.MinValue;
                bool pageContainsTargetDate = pageCandles.Any(x => x.CandleTime.Date == targetDate);

                lock (stateLock)
                {
                    receivedPageCount++;

                    if (pageCandles.Count > 0)
                        rawCandles.AddRange(pageCandles);

                    if (pageContainsTargetDate)
                        targetDateObserved = true;
                }

                // Kiwoom 기본 연속조회가 아직 진행 중이면 Replay가 중복 next=2를 요청하지 않는다.
                if (!isLast)
                    return;

                bool observedTarget;
                lock (stateLock)
                    observedTarget = targetDateObserved;

                if (observedTarget)
                {
                    Complete("TargetDateObserved");
                    return;
                }

                // 목표일보다 과거 페이지까지 내려갔다면 선택일 자체에 분봉이 없는 경우다.
                if (oldestPageTime != DateTime.MinValue && oldestPageTime.Date < targetDate)
                {
                    Complete("TargetDatePassedWithoutRows");
                    return;
                }

                string terminalPageSignature = pageCandles.Count == 0
                    ? "EMPTY"
                    : string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "{0}|{1:yyyyMMddHHmmss}|{2:yyyyMMddHHmmss}",
                        pageCandles.Count,
                        newestPageTime,
                        oldestPageTime);

                int repeatedCount;
                lock (stateLock)
                {
                    if (string.Equals(
                        lastTerminalPageSignature,
                        terminalPageSignature,
                        StringComparison.Ordinal))
                    {
                        repeatedTerminalPageCount++;
                    }
                    else
                    {
                        lastTerminalPageSignature = terminalPageSignature;
                        repeatedTerminalPageCount = 1;
                    }

                    repeatedCount = repeatedTerminalPageCount;
                }

                // 장전의 빈 최초 응답도 바로 실패로 확정하지 않고 next=2를 시도한다.
                // 다만 동일한 종료 페이지가 반복되면 API 커서가 진행되지 않는 상태이므로 중단한다.
                if (repeatedCount >= ReplayMinuteMaxRepeatedTerminalPages)
                {
                    Complete("TerminalPageRepeated");
                    return;
                }

                RequestReplayContinuation();
            }

            // 이전 중단 요청이 남겼을 수 있는 Replay 대상일 공유값을 정리한다.
            // 이번 수집은 KiwoomApiService의 내부 날짜분기를 사용하지 않고 Replay가 직접 next=2를 관리한다.
            RemoveRequestedMinuteTargetDate(windowId, code);
            _liveApi.OnMinuteHistoryReceived += Handler;
            try
            {
                _liveApi.RequestMinuteCandles(
                    windowId,
                    code,
                    minuteScreenNo,
                    daysToLoad: 10,
                    maxRows: 0,
                    priority: 0,
                    next: 0);

                bool completed = await WaitUntilCompletedAsync(tcs.Task, token).ConfigureAwait(false);
                if (!completed)
                {
                    lock (stateLock)
                        stopReason = "Cancelled";
                }
            }
            finally
            {
                RemoveRequestedMinuteTargetDate(windowId, code);
                _liveApi.OnMinuteHistoryReceived -= Handler;
                _liveApi.UnregisterWindow(windowId);
            }

            List<MinuteCandleRecord> oneDayCandles;
            int finalPageCount;
            string finalStopReason;

            lock (stateLock)
            {
                oneDayCandles = rawCandles
                    .Where(candle =>
                        candle != null &&
                        candle.CandleTime != DateTime.MinValue &&
                        candle.CandleTime.Date == targetDate &&
                        ReplaySessionOptions.IsReplayDataTimeAllowed(candle.CandleTime, targetDate, false))
                    .GroupBy(candle => candle.CandleTime)
                    .Select(group => group.Last())
                    .OrderBy(candle => candle.CandleTime)
                    .ToList();

                finalPageCount = receivedPageCount;
                finalStopReason = stopReason;
            }

            return new ReplayMinuteCollectResult
            {
                TradingDate = targetDate,
                Candles = oneDayCandles,
                ReceivedPageCount = finalPageCount,
                ReplayContinuationRequestCount = Math.Min(
                    continuationRequestCount,
                    ReplayMinuteMaxContinuationRequests),
                StopReason = finalStopReason
            };
        }

        private int _tempcnt = 0;
        private async Task<List<TrackedSymbol>> CollectTicksAsync(
            string windowId,
            string code,
            DateTime tradingDate,
            CancellationToken token)
        {
            var buffer = new ReplayTickMergeBuffer();
            var tcs = new TaskCompletionSource<bool>();
            DateTime targetDate = tradingDate.Date;

            void Handler(string wId, string receivedCode, List<TrackedSymbol> ticks, bool isLast)
            {
                if (!string.Equals(wId, windowId, StringComparison.OrdinalIgnoreCase))
                    return;

                if (!string.Equals(receivedCode, code, StringComparison.OrdinalIgnoreCase))
                    return;

                bool reachedBeforeRange = buffer.Merge(code, ticks, targetDate);

                if (isLast || reachedBeforeRange)
                    tcs.TrySetResult(true);

                if (ticks != null && ticks.Count > 0)
                {
                    _tempcnt += ticks.Count;
                    Debug.WriteLine($"[tickCount] {_tempcnt} [tickDate] {ticks[0].LastUpdated}");
                }
            }

            _liveApi.OnRecentTicksReceived += Handler;
            try
            {
                _liveApi.RequestRecentTicks(
                    windowId,
                    code,
                    _liveApi.GetScreenNo(),
                    minutes: BuildReplayTickDateRequestValue(tradingDate),
                    priority: 0);

                await WaitUntilCompletedAsync(tcs.Task, token).ConfigureAwait(false);
            }
            finally
            {
                _liveApi.OnRecentTicksReceived -= Handler;
                _liveApi.UnregisterWindow(windowId);
            }

            return buffer.ToAscendingList();
        }

        private async Task<List<ReplayHogaDataFrame>> CollectHogasAsync(
            string windowId,
            string code,
            DateTime tradingDate,
            CancellationToken token)
        {
            var savedFrames = _liveApi?.DbManager?.LoadReplayHogaFrames(code, tradingDate);
            if (savedFrames != null && savedFrames.Count > 0)
            {
                var frames = new List<ReplayHogaDataFrame>(savedFrames.Count);
                foreach (var saved in savedFrames)
                {
                    if (saved == null || saved.Hoga == null ||
                        !ReplaySessionOptions.IsReplayDataTimeAllowed(saved.HogaTime, tradingDate, false))
                        continue;

                    frames.Add(new ReplayHogaDataFrame
                    {
                        Hoga = saved.Hoga,
                        Time = saved.HogaTime
                    });
                }

                if (frames.Count > 0)
                    return frames;
            }

            var result = new List<ReplayHogaDataFrame>();
            var tcs = new TaskCompletionSource<bool>();

            void Handler(string wId, HogaData hoga)
            {
                if (!string.Equals(wId, windowId, StringComparison.OrdinalIgnoreCase))
                    return;

                if (hoga == null || !string.Equals(hoga.Code, code, StringComparison.OrdinalIgnoreCase))
                    return;

                DateTime hogaTime = ReplayMemoryDataStore.ResolveHogaTime(hoga);
                if (hogaTime != DateTime.MinValue)
                {
                    result.Add(new ReplayHogaDataFrame
                    {
                        Hoga = hoga,
                        Time = hogaTime
                    });
                }

                tcs.TrySetResult(true);
            }

            _liveApi.OnHogaReceived += Handler;
            try
            {
                bool requestStarted = TryInvokeHogaHistoryRequest(windowId, code, tradingDate);
                if (requestStarted)
                    await WaitUntilCompletedAsync(tcs.Task, token).ConfigureAwait(false);
                else
                    await Task.Delay(1, token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // 호가 과거 TR이 없는 환경에서는 정상적으로 빈 목록을 반환한다.
            }
            finally
            {
                _liveApi.OnHogaReceived -= Handler;
                _liveApi.UnregisterWindow(windowId);
            }

            var filtered = new List<ReplayHogaDataFrame>();
            foreach (var frame in result)
            {
                if (frame != null && frame.Hoga != null &&
                    ReplaySessionOptions.IsReplayDataTimeAllowed(frame.Time, tradingDate, false))
                    filtered.Add(frame);
            }

            return filtered;
        }

        private bool TryInvokeHogaHistoryRequest(string windowId, string code, DateTime tradingDate)
        {
            var apiType = _liveApi.GetType();
            var names = new[]
            {
                "RequestHogaHistory",
                "RequestHistoricalHoga",
                "RequestReplayHoga",
                "RequestHogaData"
            };

            foreach (string name in names)
            {
                var method = apiType.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (method == null)
                    continue;

                var args = BuildHogaRequestArguments(method, windowId, code, tradingDate);
                if (args == null)
                    continue;

                method.Invoke(_liveApi, args);
                return true;
            }

            return false;
        }

        private static object[] BuildHogaRequestArguments(MethodInfo method, string windowId, string code, DateTime tradingDate)
        {
            var parameters = method.GetParameters();
            var args = new object[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                string name = p.Name == null ? string.Empty : p.Name.ToLowerInvariant();

                if (p.ParameterType == typeof(string))
                {
                    if (name.Contains("window"))
                        args[i] = windowId;
                    else if (name.Contains("screen"))
                        args[i] = "9999";
                    else
                        args[i] = code;
                }
                else if (p.ParameterType == typeof(DateTime))
                {
                    if (name.Contains("end"))
                        args[i] = ReplaySessionOptions.ResolveSessionEndTime(tradingDate);
                    else
                        args[i] = tradingDate.Date.AddHours(9);
                }
                else if (p.ParameterType == typeof(int))
                {
                    args[i] = 0;
                }
                else if (p.HasDefaultValue)
                {
                    args[i] = p.DefaultValue;
                }
                else
                {
                    return null;
                }
            }

            return args;
        }

        private static Task DelayBeforeNextReplayTrAsync(CancellationToken token)
        {
            return Task.CompletedTask;
        }

        private static int BuildReplayTickDateRequestValue(DateTime tradingDate)
        {
            if (int.TryParse(tradingDate.Date.ToString("yyyyMMdd"), out var value))
                return -value;

            return -390;
        }

        private sealed class ReplayTickMergeBuffer
        {
            private readonly List<List<TrackedSymbol>> _batches = new List<List<TrackedSymbol>>();

            public bool Merge(string code, IEnumerable<TrackedSymbol> ticks, DateTime targetDate)
            {
                if (ticks == null)
                    return false;

                targetDate = targetDate.Date;
                bool reachedBeforeRange = false;
                var batch = new List<TrackedSymbol>();

                foreach (var tick in ticks)
                {
                    if (tick == null)
                        continue;

                    if (!string.IsNullOrWhiteSpace(code) &&
                        !string.Equals(tick.Code, code, StringComparison.OrdinalIgnoreCase))
                        continue;

                    DateTime tickTime = tick.LastUpdated;
                    if (tickTime == DateTime.MinValue)
                        continue;

                    DateTime tickDate = tickTime.Date;

                    if (tickDate > targetDate)
                        continue;

                    if (tickDate < targetDate)
                    {
                        reachedBeforeRange = true;
                        continue;
                    }

                    if (!ReplaySessionOptions.IsReplayDataTimeAllowed(tickTime, targetDate, false))
                        continue;

                    batch.Add(tick);
                }

                if (batch.Count > 0)
                    _batches.Add(batch);

                return reachedBeforeRange;
            }

            public List<TrackedSymbol> ToAscendingList()
            {
                int count = 0;
                for (int i = 0; i < _batches.Count; i++)
                    count += _batches[i].Count;

                var result = new List<TrackedSymbol>(count);

                // Kiwoom 연속조회는 최신 페이지부터 과거 페이지 순서로 온다.
                // 각 페이지는 KiwoomApiService에서 1회 Reverse되어 페이지 내부 오름차순이다.
                // 여기서는 정렬하지 않고 페이지 순서만 뒤에서 앞으로 붙인다.
                for (int i = _batches.Count - 1; i >= 0; i--)
                {
                    var batch = _batches[i];
                    for (int j = 0; j < batch.Count; j++)
                        result.Add(batch[j]);
                }

                return result;
            }
        }

        private static async Task<bool> WaitUntilCompletedAsync(Task task, CancellationToken token)
        {
            if (task == null)
                return true;

            if (task.IsCompleted)
            {
                await task.ConfigureAwait(false);
                return true;
            }

            var cancelTask = new TaskCompletionSource<bool>();

            using (token.Register(() => cancelTask.TrySetResult(true)))
            {
                var completed = await Task.WhenAny(task, cancelTask.Task).ConfigureAwait(false);

                if (completed == cancelTask.Task)
                    return false;
            }

            await task.ConfigureAwait(false);
            return true;
        }

        public static DateTime ResolveReplayTradingDate(DateTime now, DateTime configuredDate)
        {
            if (configuredDate != DateTime.MinValue && configuredDate.Date != DateTime.Today)
                return configuredDate.Date;

            if (now.TimeOfDay >= new TimeSpan(15, 40, 0))
                return now.Date;

            return ResolvePreviousBusinessDay(now.Date);
        }

        public static DateTime ResolvePreviousBusinessDay(DateTime date)
        {
            DateTime d = date.Date.AddDays(-1);
            while (d.DayOfWeek == DayOfWeek.Saturday || d.DayOfWeek == DayOfWeek.Sunday)
                d = d.AddDays(-1);
            return d.Date;
        }
    }

    // ============================================================
    // Source: ReplayMarketEventPlayer.cs
    // ============================================================
    public sealed class ReplayMarketEventPlayer
    {
        private readonly ReplayMemoryDataStore _memoryStore;
        private readonly ReplayTimeController _clock;

        public ReplayMarketEventPlayer(ReplayMemoryDataStore memoryStore, ReplayTimeController clock)
        {
            _memoryStore = memoryStore ?? throw new ArgumentNullException(nameof(memoryStore));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public event Action<string> Log;
        public event Action<TrackedSymbol> Tick;
        public event Action<TrackedSymbol> InventoryTick;
        public event Action<TrackedSymbol> RealPrice;
        public event Action<HogaData> Hoga;

        public async Task PlayAsync(ReplaySessionOptions options, CancellationToken token)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            options.Normalize();

            try
            {
                var tickSource = _memoryStore.LoadTickSourceForPlayback(options.Code, options.TradingDate);
                var rawHogaFrames = _memoryStore.LoadHogaFrames(options.Code, options.TradingDate)
                    .Where(x => x != null && x.Hoga != null &&
                        ReplaySessionOptions.IsReplayDataTimeAllowed(x.Time, options.TradingDate, false))
                    .ToList();

                if (tickSource == null)
                {
                    Log?.Invoke($"[REPLAY_BLOCK] Tick 공급기 없음: {options.Code} / {options.TradingDate:yyyy-MM-dd}");
                    return;
                }

                await tickSource.InitializeAsync(token).ConfigureAwait(false);

                DateTime replayTo = options.EndTime <= ReplaySessionOptions.ResolveSessionEndTime(options.TradingDate)
                    ? options.EndTime.AddSeconds(59)
                    : ReplaySessionOptions.ResolveSessionEndInclusiveTime(options.TradingDate);

                TrackedSymbol firstTick = null;

                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var candidate = await tickSource.ReadNextAsync(token).ConfigureAwait(false);
                    if (candidate == null)
                        break;

                    if (candidate.LastUpdated < options.StartTime)
                        continue;

                    if (candidate.LastUpdated > replayTo)
                        break;

                    firstTick = candidate;
                    break;
                }

                if (firstTick == null)
                {
                    Log?.Invoke($"[REPLAY_BLOCK] 재생할 틱 이벤트 없음: {options.Code} / {options.TradingDate:yyyy-MM-dd}");
                    return;
                }

                var hogaFrames = rawHogaFrames
                    .Select(x =>
                    {
                        EnsureReplayHogaCode(x.Hoga, options.Code);
                        return new ReplayHogaFrame
                        {
                            Hoga = x.Hoga,
                            Time = x.Time,
                            IsSyntheticTime = false
                        };
                    })
                    .OrderBy(x => x.Time)
                    .ToList();

                int hogaCount = hogaFrames.Count;
                int hogaNoTimeCount = 0;
                int hogaEventCount = 0;
                int dbHogaEventCount = 0;
                int fallbackHogaEventCount = 0;
                int publishedTickCount = 0;

                _clock.Reset(firstTick.LastUpdated, options.Speed);
                Log?.Invoke(
                    $"[REPLAY] 시작: {options.Code} / {options.TradingDate:yyyy-MM-dd} / " +
                    $"Source={tickSource.SourceName} / TickTotal={tickSource.TotalCount:N0} / " +
                    $"Hoga={hogaCount:N0} / HogaNoTime={hogaNoTimeCount:N0} / " +
                    $"HogaLookup=PerTick±300ms / Speed={options.Speed:F1}x");

                TrackedSymbol tick = firstTick;

                while (tick != null)
                {
                    if (token.IsCancellationRequested)
                    {
                        Log?.Invoke($"[REPLAY] 중지: {options.Code} / {options.TradingDate:yyyy-MM-dd}");
                        return;
                    }

                    bool canContinue = await _clock.WaitWhilePausedAsync(token).ConfigureAwait(false);
                    if (!canContinue || token.IsCancellationRequested)
                    {
                        Log?.Invoke($"[REPLAY] 중지: {options.Code} / {options.TradingDate:yyyy-MM-dd}");
                        return;
                    }

                    _clock.AdvanceTo(tick.LastUpdated);

                    if (Hoga != null)
                    {
                        // DB 호가는 매 tick마다 현재 tick 시간 기준 ±300ms 범위를 직접 조회한다.
                        // 이전 tick의 cursor/last DB 호가를 재사용하지 않는다.
                        // 따라서 중간에 DB 호가가 끊긴 구간은 즉시 fallback 호가를 사용한다.
                        var dbHoga = hogaCount > 0
                            ? FindNearbyHoga(hogaFrames, tick.LastUpdated, 180000)
                            : null;

                        bool usedFallbackHoga = dbHoga == null;
                        var hogaToPublish = dbHoga ?? CreateFallbackHoga(tick);

                        if (hogaToPublish != null)
                        {
                            EnsureReplayHogaCode(hogaToPublish, options.Code);
                            Hoga.Invoke(hogaToPublish);
                            hogaEventCount++;

                            if (usedFallbackHoga)
                                fallbackHogaEventCount++;
                            else
                                dbHogaEventCount++;
                        }
                    }

                    Tick?.Invoke(tick);
                    publishedTickCount++;

                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        tick = await tickSource.ReadNextAsync(token).ConfigureAwait(false);

                        if (tick == null || tick.LastUpdated > replayTo)
                        {
                            tick = null;
                            break;
                        }

                        if (tick.LastUpdated >= options.StartTime)
                            break;
                    }
                }

                Log?.Invoke(
                    $"[REPLAY] 종료: {options.Code} / {options.TradingDate:yyyy-MM-dd} / " +
                    $"Source={tickSource.SourceName} / TickPublished={publishedTickCount:N0} / " +
                    $"HogaEvent={hogaEventCount:N0} / DbHogaEvent={dbHogaEventCount:N0} / " +
                    $"FallbackHoga={fallbackHogaEventCount:N0}");
            }
            finally
            {
                _memoryStore.Clear(options.Code, options.TradingDate);
                Log?.Invoke($"[REPLAY] 원천데이터 해제: {options.Code} / {options.TradingDate:yyyy-MM-dd}");
            }
        }

        private sealed class ReplayHogaFrame
        {
            public HogaData Hoga { get; set; }
            public DateTime Time { get; set; }
            public bool IsSyntheticTime { get; set; }
        }

        private static List<ReplayHogaFrame> BuildReplayHogaFrames(
            List<HogaData> hogas,
            string code,
            DateTime startTime,
            DateTime endTime)
        {
            var frames = new List<ReplayHogaFrame>();
            if (hogas == null || hogas.Count == 0)
                return frames;

            for (int i = 0; i < hogas.Count; i++)
            {
                var hoga = hogas[i];
                if (hoga == null)
                    continue;

                EnsureReplayHogaCode(hoga, code);

                DateTime hogaTime = ReplayMemoryDataStore.ResolveHogaTime(hoga);
                if (hogaTime == DateTime.MinValue)
                    continue;

                frames.Add(new ReplayHogaFrame
                {
                    Hoga = hoga,
                    Time = hogaTime,
                    IsSyntheticTime = false
                });
            }

            return frames
                .OrderBy(x => x.Time)
                .ToList();
        }

        private static DateTime ResolveSyntheticHogaTime(DateTime startTime, DateTime endTime, int index, int count)
        {
            if (startTime == DateTime.MinValue)
                startTime = DateTime.Today.AddHours(9);

            if (endTime == DateTime.MinValue || endTime <= startTime)
                endTime = startTime.AddHours(6).AddMinutes(20);

            if (count <= 1)
                return startTime;

            double ratio = (double)Math.Max(0, index) / Math.Max(1, count - 1);
            long ticks = (long)((endTime - startTime).Ticks * ratio);
            return startTime.AddTicks(ticks);
        }

        private static void EnsureReplayHogaCode(HogaData hoga, string code)
        {
            if (hoga == null || string.IsNullOrWhiteSpace(code))
                return;

            if (!string.IsNullOrWhiteSpace(hoga.Code))
                return;

            hoga.Code = code.Trim();
        }

        private static HogaData FindNearbyHoga(List<ReplayHogaFrame> hogas, DateTime time, int toleranceMilliseconds)
        {
            if (hogas == null || hogas.Count == 0 || time == DateTime.MinValue)
                return null;

            if (toleranceMilliseconds < 0)
                toleranceMilliseconds = 0;

            DateTime from = time.AddMilliseconds(-toleranceMilliseconds);
            DateTime to = time.AddMilliseconds(toleranceMilliseconds);

            // 매 tick마다 현재 tick 시간 기준으로 직접 조회한다.
            // cursor를 유지하면 tick 순서가 미세하게 어긋나거나 같은 시간대 재조회가 필요한 경우
            // DB 호가를 지나쳐 버릴 수 있으므로 lower-bound로 독립 조회한다.
            int startIndex = FindFirstHogaFrameAtOrAfter(hogas, from);

            HogaData best = null;
            double bestGap = double.MaxValue;

            for (int i = startIndex; i < hogas.Count; i++)
            {
                var frame = hogas[i];
                if (frame == null || frame.Hoga == null)
                    continue;

                if (frame.Time > to)
                    break;

                double gap = Math.Abs((time - frame.Time).TotalMilliseconds);
                if (gap < bestGap)
                {
                    best = frame.Hoga;
                    bestGap = gap;
                }
            }

            return best;
        }

        private static int FindFirstHogaFrameAtOrAfter(List<ReplayHogaFrame> hogas, DateTime time)
        {
            if (hogas == null || hogas.Count == 0)
                return 0;

            int low = 0;
            int high = hogas.Count;

            while (low < high)
            {
                int mid = low + ((high - low) / 2);
                var frame = hogas[mid];

                if (frame == null || frame.Time < time)
                    low = mid + 1;
                else
                    high = mid;
            }

            return low;
        }

        private static HogaData CreateFallbackHoga(TrackedSymbol tick)
        {
            if (tick == null)
                return null;

            double price = tick.LastPrice > 0 ? tick.LastPrice : tick.ClosePrice;
            if (price <= 0)
                price = 1;

            long askQty = tick.TotalAskQty > 0 ? tick.TotalAskQty : 1;
            long bidQty = tick.TotalBidQty > 0 ? tick.TotalBidQty : 1;

            var hoga = new HogaData
            {
                Code = tick.Code,
                TotalAskQty = askQty,
                TotalBidQty = bidQty,
                Asks = new List<HogaItem>
                {
                    new HogaItem { Price = price, Quantity = askQty, KrxQuantity = askQty, IsCurrentPrice = true }
                },
                Bids = new List<HogaItem>
                {
                    new HogaItem { Price = price, Quantity = bidQty, KrxQuantity = bidQty, IsCurrentPrice = true }
                }
            };

            ApplyHogaTime(hoga, tick.LastUpdated);
            return hoga;
        }

        private static void ApplyHogaTime(HogaData hoga, DateTime time)
        {
            if (hoga == null || time == DateTime.MinValue)
                return;

            var type = hoga.GetType();
            foreach (var name in new[] { "HogaTime", "Time", "UpdatedAt", "LastUpdated", "CreatedAt", "ReceivedAt", "ReceiveTime", "ReceivedTime", "Timestamp" })
            {
                var prop = type.GetProperty(name);
                if (prop == null || !prop.CanWrite)
                    continue;

                if (prop.PropertyType == typeof(DateTime))
                {
                    prop.SetValue(hoga, time, null);
                    return;
                }

                if (prop.PropertyType == typeof(DateTime?))
                {
                    prop.SetValue(hoga, (DateTime?)time, null);
                    return;
                }
            }
        }
    }

    // ============================================================
    // Source: ReplayAccountBook.cs
    // ============================================================
    public sealed class ReplayAccountBook
    {
        private readonly object _syncRoot = new object();
        private readonly Dictionary<string, HoldingInfo> _holdingsByCode = new Dictionary<string, HoldingInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, OrderInfo> _ordersByNo = new Dictionary<string, OrderInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _buyReservedCashByOrderNo = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private long _cash;
        private long _orderSeed;

        public ReplayAccountBook(long initialCash)
        {
            Reset(initialCash);
        }

        public long Cash
        {
            get
            {
                lock (_syncRoot)
                    return _cash;
            }
        }

        public void Reset(long initialCash)
        {
            lock (_syncRoot)
            {
                _cash = initialCash > 0 ? initialCash : 10_000_000;
                _holdingsByCode.Clear();
                _ordersByNo.Clear();
                _buyReservedCashByOrderNo.Clear();
                _orderSeed = 0;
            }
        }

        public List<HoldingInfo> GetHoldings()
        {
            lock (_syncRoot)
            {
                return _holdingsByCode.Values
                    .Where(x => x != null && x.Quantity > 0)
                    .Select(CloneHolding)
                    .ToList();
            }
        }

        public List<OrderInfo> GetOrders()
        {
            lock (_syncRoot)
                return _ordersByNo.Values.Select(CloneOrder).ToList();
        }

        public List<OrderInfo> GetUnfilledOrders()
        {
            lock (_syncRoot)
            {
                return _ordersByNo.Values
                    .Where(x => x != null && x.UntradedQty > 0)
                    .Select(CloneOrder)
                    .ToList();
            }
        }

        public bool TryAcceptOrder(
            string accountNo,
            int orderType,
            string code,
            int qty,
            int price,
            string hogaType,
            DateTime orderTime,
            out OrderInfo order,
            out string blockReason)
        {
            order = null;
            blockReason = string.Empty;

            if (string.IsNullOrWhiteSpace(code))
            {
                blockReason = "종목코드 없음";
                return false;
            }

            if (qty <= 0)
            {
                blockReason = "주문수량 0";
                return false;
            }

            bool isBuy = orderType == 1;
            bool isSell = orderType == 2;

            if (!isBuy && !isSell)
            {
                blockReason = "지원하지 않는 주문유형";
                return false;
            }

            lock (_syncRoot)
            {
                long buyReserveAmount = 0;

                if (isBuy)
                {
                    var referencePrice = price > 0 ? price : 0;
                    if (referencePrice > 0)
                    {
                        buyReserveAmount = TradingCostPolicy.EstimateBuyRequiredAmount(qty, referencePrice);
                        if (buyReserveAmount > _cash)
                        {
                            blockReason = $"리플레이 예수금 부족: 필요={buyReserveAmount:N0}, 가능={_cash:N0}";
                            return false;
                        }
                    }
                }
                else
                {
                    if (!_holdingsByCode.TryGetValue(code, out var holding) || holding.Quantity < qty)
                    {
                        blockReason = "리플레이 매도 가능 수량 부족";
                        return false;
                    }
                }

                string orderNo = $"RP{DateTime.Now:HHmmss}{++_orderSeed:D5}";
                order = new OrderInfo
                {
                    OrderNo = orderNo,
                    TradeGroupId = orderNo,
                    OrderTime = orderTime.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    Code = code,
                    Name = code,
                    OrderType = isBuy ? "매수" : "매도",
                    Qty = qty,
                    ExecQty = 0,
                    Price = price,
                    ExecPrice = 0,
                    Status = "접수",
                    OrderStatus = OrderStatus.Received,
                    UntradedQty = qty,
                    StrategyName = "Replay"
                };

                _ordersByNo[orderNo] = CloneOrder(order);

                if (isBuy && buyReserveAmount > 0)
                {
                    _cash -= buyReserveAmount;
                    _buyReservedCashByOrderNo[orderNo] = buyReserveAmount;
                }

                return true;
            }
        }

        public bool TryCancelOrder(
            string orgOrderNo,
            int qty,
            DateTime cancelTime,
            out OrderInfo canceledOrder,
            out string blockReason)
        {
            canceledOrder = null;
            blockReason = string.Empty;

            if (string.IsNullOrWhiteSpace(orgOrderNo))
            {
                blockReason = "원주문번호 없음";
                return false;
            }

            lock (_syncRoot)
            {
                if (!_ordersByNo.TryGetValue(orgOrderNo, out var stored) || stored == null)
                {
                    blockReason = "원주문 없음";
                    return false;
                }

                if (stored.UntradedQty <= 0)
                {
                    // Replay 내부 시간 기준 자동취소가 먼저 처리된 뒤
                    // ViewModel의 기존 취소 요청이 늦게 들어올 수 있다.
                    // 이 경우 실패로 반환하면 실거래 취소 실패처럼 보이므로 성공 처리한다.
                    canceledOrder = CloneOrder(stored);
                    return true;
                }

                int beforeUntradedQty = stored.UntradedQty;
                int cancelQty = qty > 0 ? Math.Min(qty, stored.UntradedQty) : stored.UntradedQty;
                if (cancelQty <= 0)
                {
                    canceledOrder = CloneOrder(stored);
                    return true;
                }

                if (IsBuyOrder(stored))
                    ReleaseBuyReservation(stored.OrderNo, cancelQty, beforeUntradedQty);

                stored.UntradedQty = Math.Max(0, stored.UntradedQty - cancelQty);
                stored.Status = stored.UntradedQty <= 0 ? "취소" : "부분취소";
                stored.OrderStatus = stored.UntradedQty <= 0 ? OrderStatus.Cancelled : OrderStatus.Corrected;
                stored.OrderType = IsBuyOrder(stored) ? "매수취소" : IsSellOrder(stored) ? "매도취소" : stored.OrderType;
                stored.OrderTime = cancelTime.ToString("yyyy-MM-dd HH:mm:ss.fff");

                _ordersByNo[stored.OrderNo] = stored;
                canceledOrder = CloneOrder(stored);
                return true;
            }
        }

        public bool TryFillOrder(OrderInfo order, double fillPrice, DateTime fillTime, out OrderInfo filledOrder)
        {
            filledOrder = null;

            if (order == null || string.IsNullOrWhiteSpace(order.OrderNo) || fillPrice <= 0)
                return false;

            lock (_syncRoot)
            {
                if (!_ordersByNo.TryGetValue(order.OrderNo, out var stored))
                    return false;

                if (stored.UntradedQty <= 0)
                    return false;

                int fillQty = stored.UntradedQty;
                double amount = fillQty * fillPrice;
                bool isBuy = IsBuyOrder(stored);
                bool isSell = IsSellOrder(stored);

                if (isBuy)
                {
                    double fee = amount * TradingCostPolicy.BuyFeeRate;
                    long required = (long)Math.Ceiling(amount + fee);
                    long reserved = GetBuyReservation(stored.OrderNo);
                    long additionalRequired = Math.Max(0, required - reserved);

                    if (additionalRequired > _cash)
                        return false;

                    if (additionalRequired > 0)
                        _cash -= additionalRequired;

                    if (reserved > required)
                        _cash += reserved - required;

                    ClearBuyReservation(stored.OrderNo);
                    ApplyBuyHolding(stored.Code, stored.Name, fillQty, fillPrice);
                    stored.Fee = fee;
                    stored.Tax = 0;
                }
                else if (isSell)
                {
                    if (!_holdingsByCode.TryGetValue(stored.Code, out var holding) || holding.Quantity < fillQty)
                        return false;

                    double fee = amount * TradingCostPolicy.SellFeeRate;
                    double tax = amount * TradingCostPolicy.SellTaxRate;
                    long receivable = (long)Math.Max(0, Math.Floor(amount - fee - tax));

                    _cash += receivable;
                    ApplySellHolding(stored.Code, fillQty, fillPrice);
                    stored.Fee = fee;
                    stored.Tax = tax;
                }
                else
                {
                    return false;
                }

                stored.ExecQty = stored.Qty;
                stored.UntradedQty = 0;
                stored.ExecPrice = fillPrice;
                stored.Status = "체결";
                stored.OrderStatus = OrderStatus.FullyFilled;
                stored.OrderTime = fillTime.ToString("yyyy-MM-dd HH:mm:ss.fff");

                _ordersByNo[stored.OrderNo] = stored;
                filledOrder = CloneOrder(stored);
                return true;
            }
        }

        private long GetBuyReservation(string orderNo)
        {
            if (string.IsNullOrWhiteSpace(orderNo))
                return 0;

            return _buyReservedCashByOrderNo.TryGetValue(orderNo, out var reserved) ? Math.Max(0, reserved) : 0;
        }

        private void ClearBuyReservation(string orderNo)
        {
            if (string.IsNullOrWhiteSpace(orderNo))
                return;

            _buyReservedCashByOrderNo.Remove(orderNo);
        }

        private void ReleaseBuyReservation(string orderNo, int releaseQty, int beforeUntradedQty)
        {
            if (string.IsNullOrWhiteSpace(orderNo) || releaseQty <= 0 || beforeUntradedQty <= 0)
                return;

            if (!_buyReservedCashByOrderNo.TryGetValue(orderNo, out var reserved) || reserved <= 0)
                return;

            long releaseAmount = releaseQty >= beforeUntradedQty
                ? reserved
                : (long)Math.Ceiling(reserved * (releaseQty / (double)beforeUntradedQty));

            if (releaseAmount <= 0)
                return;

            if (releaseAmount >= reserved)
                _buyReservedCashByOrderNo.Remove(orderNo);
            else
                _buyReservedCashByOrderNo[orderNo] = reserved - releaseAmount;

            _cash += releaseAmount;
        }

        private void ApplyBuyHolding(string code, string name, int qty, double price)
        {
            if (!_holdingsByCode.TryGetValue(code, out var holding))
            {
                holding = new HoldingInfo
                {
                    Code = code,
                    Name = string.IsNullOrWhiteSpace(name) ? code : name,
                    Quantity = qty,
                    EntryPrice = price,
                    CurrentPrice = price
                };
                _holdingsByCode[code] = holding;
                return;
            }

            int oldQty = holding.Quantity;
            double oldAmount = oldQty * holding.EntryPrice;
            int newQty = oldQty + qty;
            double newAmount = oldAmount + qty * price;

            holding.Quantity = newQty;
            holding.EntryPrice = newQty > 0 ? newAmount / newQty : 0;
            holding.CurrentPrice = price;
        }

        private void ApplySellHolding(string code, int qty, double price)
        {
            if (!_holdingsByCode.TryGetValue(code, out var holding))
                return;

            holding.Quantity = Math.Max(0, holding.Quantity - qty);
            holding.CurrentPrice = price;

            if (holding.Quantity <= 0)
                _holdingsByCode.Remove(code);
        }

        public void UpdateCurrentPrice(string code, double price)
        {
            if (string.IsNullOrWhiteSpace(code) || price <= 0)
                return;

            lock (_syncRoot)
            {
                if (_holdingsByCode.TryGetValue(code, out var holding))
                    holding.CurrentPrice = price;
            }
        }

        private static bool IsBuyOrder(OrderInfo order)
        {
            return order != null && !string.IsNullOrWhiteSpace(order.OrderType) && order.OrderType.Contains("매수");
        }

        private static bool IsSellOrder(OrderInfo order)
        {
            return order != null && !string.IsNullOrWhiteSpace(order.OrderType) && order.OrderType.Contains("매도");
        }

        private static HoldingInfo CloneHolding(HoldingInfo source)
        {
            if (source == null)
                return null;

            return new HoldingInfo
            {
                Code = source.Code,
                Name = source.Name,
                Quantity = source.Quantity,
                EntryPrice = source.EntryPrice,
                CurrentPrice = source.CurrentPrice,
                IsHistoryLoaded = source.IsHistoryLoaded,
                IsHistoryLoading = source.IsHistoryLoading
            };
        }

        private static OrderInfo CloneOrder(OrderInfo source)
        {
            if (source == null)
                return null;

            return new OrderInfo
            {
                OrderNo = source.OrderNo,
                TradeGroupId = source.TradeGroupId,
                OrderTime = source.OrderTime,
                Code = source.Code,
                Name = source.Name,
                OrderType = source.OrderType,
                Qty = source.Qty,
                ExecQty = source.ExecQty,
                Price = source.Price,
                ExecPrice = source.ExecPrice,
                Status = source.Status,
                StrategyName = source.StrategyName,
                Fee = source.Fee,
                Tax = source.Tax,
                ParentOrderNo = source.ParentOrderNo,
                TiRexDirection = source.TiRexDirection,
                TiRexConfidence = source.TiRexConfidence,
                TiRexPredictedReturn = source.TiRexPredictedReturn,
                UntradedQty = source.UntradedQty,
                OrderStatus = source.OrderStatus
            };
        }
    }

    // ============================================================
    // Source: ReplayOrderSimulator.cs
    // ============================================================
    public sealed class ReplayOrderSimulator
    {
        private readonly ReplayAccountBook _accountBook;
        private readonly object _syncRoot = new object();
        private readonly List<OrderInfo> _pendingOrders = new List<OrderInfo>();
        private readonly Dictionary<string, string> _lastFillSkipLogKeyByOrderNo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private const int ReplayBuyLimitToleranceTicks = 1;
        private const int ReplayPendingBuyCancelSeconds = 3;
        // Real의 일반 Sell 첫 미체결 확인(4초)과 의미를 맞추되,
        // Replay에서는 wall-clock/Task.Delay를 사용하지 않고 Tick 실제시간으로만 만료시킨다.
        private const int ReplayPendingSellCancelSeconds = 4;

        public ReplayOrderSimulator(ReplayAccountBook accountBook)
        {
            _accountBook = accountBook ?? throw new ArgumentNullException(nameof(accountBook));
        }

        public event Action<List<OrderInfo>> OrdersChanged;
        public event Action<List<OrderInfo>> UnfilledOrdersChanged;
        public event Action<List<HoldingInfo>> HoldingsChanged;
        public event Action<long> CashChanged;
        public event Action<OrderInfo> OrderFilled;
        public event Action<string> Log;

        public int SendOrder(string accountNo, int orderType, string code, int qty, int price, string hogaType, DateTime orderTime, string orgOrderNo = "")
        {
            if (orderType == 3 || orderType == 4)
                return CancelOrder(code, orgOrderNo, qty, orderTime, orderType == 3 ? "매수취소" : "매도취소");

            if (!_accountBook.TryAcceptOrder(accountNo, orderType, code, qty, price, hogaType, orderTime, out var order, out var reason))
            {
                Log?.Invoke($"[REPLAY_ORDER_BLOCK] {code} / {reason}");
                return -1;
            }

            lock (_syncRoot)
                _pendingOrders.Add(order);

            PublishState();
            Log?.Invoke($"[REPLAY_ORDER_ACCEPT] {code} / {order.OrderType} / {qty}주 / 기준가={price:N0}");
            return 0;
        }

        private int CancelOrder(string code, string orgOrderNo, int qty, DateTime cancelTime, string cancelType)
        {
            if (!_accountBook.TryCancelOrder(orgOrderNo, qty, cancelTime, out var canceled, out var reason))
            {
                Log?.Invoke($"[REPLAY_CANCEL_BLOCK] {code} / {cancelType} / 원주문={orgOrderNo} / {reason}");
                return -1;
            }

            lock (_syncRoot)
            {
                if (!string.IsNullOrWhiteSpace(orgOrderNo))
                {
                    if (canceled.UntradedQty <= 0)
                    {
                        _pendingOrders.RemoveAll(x => x != null && x.OrderNo == orgOrderNo);
                        _lastFillSkipLogKeyByOrderNo.Remove(orgOrderNo);
                    }
                    else
                    {
                        var pending = _pendingOrders.FirstOrDefault(x => x != null && x.OrderNo == orgOrderNo);
                        if (pending != null)
                        {
                            pending.UntradedQty = canceled.UntradedQty;
                            pending.Status = canceled.Status;
                            pending.OrderType = canceled.OrderType;
                        }
                    }
                }
            }

            PublishState();
            Log?.Invoke(
                $"[REPLAY_CANCEL] {canceled.Code} / {cancelType} / 원주문={orgOrderNo} / " +
                $"취소후잔량={canceled.UntradedQty:N0}");
            return 0;
        }

        public void TryFillPendingOrders(TrackedSymbol tick, HogaData hoga)
        {
            if (tick == null || string.IsNullOrWhiteSpace(tick.Code) || tick.LastPrice <= 0)
                return;

            List<OrderInfo> candidates;

            lock (_syncRoot)
            {
                candidates = _pendingOrders
                    .Where(x => x != null && x.Code == tick.Code && x.UntradedQty > 0)
                    .ToList();
            }

            if (candidates.Count == 0)
                return;

            foreach (var order in candidates)
            {
                if (ShouldCancelReplayPendingBuy(order, tick.LastUpdated))
                {
                    CancelOrder(order.Code, order.OrderNo, order.UntradedQty, tick.LastUpdated, "매수취소(ReplayTime)");
                    continue;
                }

                string skipReason;
                double fillPrice = ResolveFillPrice(order, tick, hoga, out skipReason);
                if (fillPrice <= 0)
                {
                    // Replay Sell은 Real의 Task.Delay 기반 감시를 사용하지 않는다.
                    // 주문 후 4초가 지났고 현재 Tick/호가에서도 여전히 체결 불가능한 경우에만
                    // Replay Tick 시간으로 원주문을 취소해 장시간 PendingSell 고착을 막는다.
                    if (ShouldCancelReplayPendingSell(order, tick.LastUpdated))
                    {
                        CancelOrder(order.Code, order.OrderNo, order.UntradedQty, tick.LastUpdated, "매도취소(ReplayTime)");
                        continue;
                    }

                    LogFillSkip(order, tick, hoga, skipReason);
                    continue;
                }

                if (!_accountBook.TryFillOrder(order, fillPrice, tick.LastUpdated, out var filled))
                {
                    LogFillSkip(order, tick, hoga, $"ACCOUNT_REJECT fill={fillPrice:N0}");
                    continue;
                }

                lock (_syncRoot)
                {
                    _pendingOrders.RemoveAll(x => x != null && x.OrderNo == order.OrderNo);
                    if (!string.IsNullOrWhiteSpace(order.OrderNo))
                        _lastFillSkipLogKeyByOrderNo.Remove(order.OrderNo);
                }

                Log?.Invoke($"[REPLAY_FILL] {filled.Code} / {filled.OrderType} / {filled.ExecQty}주 / {filled.ExecPrice:N0}");
                OrderFilled?.Invoke(filled);
                PublishState();
            }
        }

        private static bool ShouldCancelReplayPendingBuy(OrderInfo order, DateTime replayTime)
        {
            if (order == null || replayTime == DateTime.MinValue)
                return false;

            if (string.IsNullOrWhiteSpace(order.OrderType) || !order.OrderType.Contains("매수"))
                return false;

            if (order.UntradedQty <= 0)
                return false;

            if (!DateTime.TryParse(order.OrderTime, out var orderTime) || orderTime == DateTime.MinValue)
                return false;

            return (replayTime - orderTime).TotalSeconds >= ReplayPendingBuyCancelSeconds;
        }

        private static bool ShouldCancelReplayPendingSell(OrderInfo order, DateTime replayTime)
        {
            if (order == null || replayTime == DateTime.MinValue)
                return false;

            if (string.IsNullOrWhiteSpace(order.OrderType) || !order.OrderType.Contains("매도"))
                return false;

            if (order.UntradedQty <= 0)
                return false;

            if (!DateTime.TryParse(order.OrderTime, out var orderTime) || orderTime == DateTime.MinValue)
                return false;

            return (replayTime - orderTime).TotalSeconds >= ReplayPendingSellCancelSeconds;
        }

        private void LogFillSkip(OrderInfo order, TrackedSymbol tick, HogaData hoga, string reason)
        {
            if (order == null || tick == null || string.IsNullOrWhiteSpace(order.OrderNo))
                return;

            if (string.IsNullOrWhiteSpace(reason))
                reason = "UNKNOWN";

            double askPrice = ResolveBestAskPrice(hoga);
            double bidPrice = ResolveBestBidPrice(hoga);
            double orderPrice = order.Price > 0 ? order.Price : 0;
            double allowedBuyPrice = orderPrice > 0
                ? orderPrice + ResolvePriceTickSize(orderPrice) * ReplayBuyLimitToleranceTicks
                : 0;

            string logKey = $"{reason}|{orderPrice:F0}|{tick.LastPrice:F0}|{askPrice:F0}|{bidPrice:F0}|{allowedBuyPrice:F0}";

            lock (_syncRoot)
            {
                if (_lastFillSkipLogKeyByOrderNo.TryGetValue(order.OrderNo, out var lastKey) &&
                    string.Equals(lastKey, logKey, StringComparison.Ordinal))
                {
                    return;
                }

                _lastFillSkipLogKeyByOrderNo[order.OrderNo] = logKey;
            }

            Log?.Invoke(
                $"[REPLAY_FILL_SKIP] {order.Code} / {order.OrderType} / " +
                $"order={orderPrice:N0} / tick={tick.LastPrice:N0} / " +
                $"ask={askPrice:N0} / bid={bidPrice:N0} / allowedBuy={allowedBuyPrice:N0} / {reason}");
        }

        private static double ResolveFillPrice(OrderInfo order, TrackedSymbol tick, HogaData hoga, out string skipReason)
        {
            skipReason = string.Empty;

            if (order == null || tick == null)
            {
                skipReason = "INVALID_ORDER_OR_TICK";
                return 0;
            }

            bool isBuy = !string.IsNullOrWhiteSpace(order.OrderType) && order.OrderType.Contains("매수");
            bool isSell = !string.IsNullOrWhiteSpace(order.OrderType) && order.OrderType.Contains("매도");
            double orderPrice = order.Price > 0 ? order.Price : 0;

            // Replay 지정가 체결 원칙:
            // 매수는 Replay 전용 1틱 허용범위 안의 매도호가만 체결한다.
            // 매도는 매수호가가 주문가 이상일 때만 체결한다.
            // 이전 로직은 ask/bid를 그대로 체결가로 사용해서 62,200 지정가 매수가
            // 63,200 평단으로 반영될 수 있었다.
            if (isBuy)
            {
                double askPrice = ResolveBestAskPrice(hoga);
                double tradePrice = tick.LastPrice > 0 ? tick.LastPrice : 0;
                double referencePrice = tradePrice > 0 ? tradePrice : askPrice;

                if (orderPrice > 0)
                {
                    double allowedBuyPrice = orderPrice + ResolvePriceTickSize(orderPrice) * ReplayBuyLimitToleranceTicks;
                    bool tradeMatched = tradePrice > 0 && tradePrice <= allowedBuyPrice;
                    bool askMatched = askPrice > 0 && askPrice <= allowedBuyPrice;

                    if (!tradeMatched && !askMatched)
                    {
                        double missReference = referencePrice > 0 ? referencePrice : askPrice;
                        skipReason = $"BUY_LIMIT_MISS reference={missReference:N0} > allowed={allowedBuyPrice:N0}";
                        return 0;
                    }

                    if (tradeMatched)
                        return tradePrice;

                    return askPrice;
                }

                if (referencePrice > 0)
                    return referencePrice;

                return orderPrice;
            }

            if (isSell)
            {
                double bidPrice = ResolveBestBidPrice(hoga);
                double tradePrice = tick.LastPrice > 0 ? tick.LastPrice : 0;
                double referencePrice = tradePrice > 0 ? tradePrice : bidPrice;

                if (orderPrice > 0)
                {
                    bool tradeMatched = tradePrice > 0 && tradePrice >= orderPrice;
                    bool bidMatched = bidPrice > 0 && bidPrice >= orderPrice;

                    if (!tradeMatched && !bidMatched)
                    {
                        double missReference = referencePrice > 0 ? referencePrice : bidPrice;
                        skipReason = $"SELL_LIMIT_MISS reference={missReference:N0} < order={orderPrice:N0}";
                        return 0;
                    }

                    if (tradeMatched)
                        return tradePrice;

                    return bidPrice;
                }

                if (referencePrice > 0)
                    return referencePrice;

                return orderPrice;
            }

            if (orderPrice > 0)
                return orderPrice;

            return tick.LastPrice;
        }

        private static double ResolvePriceTickSize(double price)
        {
            if (price < 1000)
                return 1;

            if (price < 5000)
                return 5;

            if (price < 20000)
                return 10;

            if (price < 50000)
                return 50;

            if (price < 200000)
                return 100;

            if (price < 500000)
                return 500;

            return 1000;
        }

        private static double ResolveBestAskPrice(HogaData hoga)
        {
            if (hoga == null || hoga.Asks == null)
                return 0;

            var ask = hoga.Asks.FirstOrDefault(x => x != null && x.Price > 0);
            return ask == null ? 0 : ask.Price;
        }

        private static double ResolveBestBidPrice(HogaData hoga)
        {
            if (hoga == null || hoga.Bids == null)
                return 0;

            var bid = hoga.Bids.FirstOrDefault(x => x != null && x.Price > 0);
            return bid == null ? 0 : bid.Price;
        }

        public void PublishState()
        {
            OrdersChanged?.Invoke(_accountBook.GetOrders());
            UnfilledOrdersChanged?.Invoke(_accountBook.GetUnfilledOrders());
            HoldingsChanged?.Invoke(_accountBook.GetHoldings());
            CashChanged?.Invoke(_accountBook.Cash);
        }
    }
}
