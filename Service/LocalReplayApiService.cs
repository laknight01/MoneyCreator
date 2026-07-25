using AxKHOpenAPILib;
using MoneyCreator.DataBase;
using MoneyCreator.Models;
using MoneyCreator.Service.Replay;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace MoneyCreator.Service
{
    public sealed class LocalReplayApiService : IMarketApiService
    {
        public event Action<bool> OnLogin;
        public event Action<string, string> OnLog;
        public event Action<string, string, string> OnSymbolDetected;
        public event Action<string, Symbol> OnSymbolDetailUpdated;
        public event Action<string, string, string> OnAiSymbolDetected;
        public event Action<string, string, double, int, DateTime> OnRealPriceReceived;
        public event Action<string, List<TrackedSymbol>, bool> OnInitialHistoryReceived;
        public event Action<string, TrackedSymbol> OnTickReceived;
        public event Action<string, HogaData> OnHogaReceived;
        public event Action<string, long> OnBalanceUpdated;
        public event Action<string, List<HoldingInfo>> OnHoldingsReceived;
        public event Action<string, string, int, double> OnPositionBalanceChanged;
        public event Action<string, List<OrderInfo>> OnOrdersReceived;
        public event Action<string, List<OrderInfo>> OnUnfilledOrdersReceived;
        public event Action<string> OnGlobalUpdateRequired;
        public event Action<string, string, string, string, int, double> OnOrderFilled;
        public event Action<TrackedSymbol> OnInventoryTickReceived;
        public event Action<string, string, List<MinuteCandleRecord>, bool> OnMinuteHistoryReceived;
        public event Action<string, string, List<TrackedSymbol>, bool> OnRecentTicksReceived;
        public event Action<MarketIndexInfo> OnMarketIndexReceived;
        public event Action<string, ProgramTradeInfo, ProgramFlowSnapshotRecord> OnProgramTradeReceived;

        private readonly IMarketApiService _liveApi;
        private readonly ConcurrentDictionary<string, string> _subscriptions = new ConcurrentDictionary<string, string>();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _replayTokens = new ConcurrentDictionary<string, CancellationTokenSource>();
        private readonly ConcurrentDictionary<string, bool> _replayDispatchEnabledByWindow = new ConcurrentDictionary<string, bool>();
        private readonly ReplayMemoryDataStore _memoryStore;
        private readonly ReplaySessionOptions _baseOptions;
        private readonly ReplayTimeController _clock;
        private readonly ReplayAccountBook _accountBook;
        private readonly ReplayOrderSimulator _orderSimulator;
        private readonly ReplayMarketEventPlayer _player;
        private readonly ReplayDataPrepareService _prepareService;
        private readonly object _lastMarketDataLock = new object();
        private readonly object _replayPositionBalanceLock = new object();
        private readonly Dictionary<string, HoldingInfo> _lastReplayPositionBalanceByCode =
            new Dictionary<string, HoldingInfo>(StringComparer.OrdinalIgnoreCase);

        private int _screenSeed = 9000;
        private TrackedSymbol _lastTick;
        private HogaData _lastHoga;
        private readonly object _replayRandomLock = new object();
        private readonly Random _replayRandom = new Random();
        private string _lastReplayRandomTickKey = string.Empty;
        private bool _replayStrengthReady;
        private double _replayStrength;
        private bool _replayMarketIndexReady;
        private double _replayKospiChangeRate;
        private double _replayKosdaqChangeRate;
        private DateTime _lastReplayMarketIndexPublishedAt = DateTime.MinValue;
        private readonly ConcurrentDictionary<string, List<ProgramTradeInfo>> _replayProgramTradesByKey = new ConcurrentDictionary<string, List<ProgramTradeInfo>>();
        private readonly ConcurrentDictionary<string, int> _replayProgramIndexByKey = new ConcurrentDictionary<string, int>();
        private readonly ConcurrentDictionary<string, string> _lastReplayProgramPublishedKeyByWindow = new ConcurrentDictionary<string, string>();

        public StockDbManager DbManager { get; }
        public bool IsReplayRunning => _replayTokens.Count > 0;
        public DateTime ReplayNow => _clock.Now;
        public DateTime ReplayTradingDate => _baseOptions.TradingDate.Date;

        private void SetReplayPersistenceDisabled(bool disabled)
        {
            if (DbManager != null)
                DbManager.DisablePersistenceForReplay = disabled;
        }

        public LocalReplayApiService()
            : this(KiwoomApiService.Instance, null)
        {
        }

        public LocalReplayApiService(IMarketApiService liveApi)
            : this(liveApi, null)
        {
        }

        public LocalReplayApiService(IMarketApiService liveApi, ReplaySessionOptions options)
        {
            _liveApi = liveApi ?? KiwoomApiService.Instance;
            _baseOptions = options ?? new ReplaySessionOptions();
            _baseOptions.Normalize();

            DbManager = _liveApi?.DbManager ?? new StockDbManager(new ConcurrentQueue<TickData>());
            SetReplayPersistenceDisabled(true);
            _memoryStore = new ReplayMemoryDataStore();
            _clock = new ReplayTimeController();
            _accountBook = new ReplayAccountBook(_baseOptions.InitialCash);
            _orderSimulator = new ReplayOrderSimulator(_accountBook);
            _player = new ReplayMarketEventPlayer(_memoryStore, _clock);
            _prepareService = new ReplayDataPrepareService(_liveApi, _memoryStore);

            WireReplayModules();
        }

        public string GetScreenNo()
        {
            return Interlocked.Increment(ref _screenSeed).ToString("D4");
        }

        public bool Login()
        {
            if (_liveApi != null && !_liveApi.IsConnected())
                _liveApi.Login();

            _accountBook.Reset(_baseOptions.InitialCash);
            OnLog?.Invoke("SYSTEM", "🧪 LocalReplayApiService 연결 완료 / 원천데이터=메모리 / 종목검색=Kiwoom");
            OnLogin?.Invoke(true);
            PublishAccountState("SYSTEM");
            PublishMarketIndexSnapshot();
            return true;
        }

        public bool IsConnected()
        {
            return true;
        }

        public void SetApiInstance(AxKHOpenAPI api)
        {
            _liveApi?.SetApiInstance(api);
        }

        public void ReplaceSubscription(string windowId, string screenNo, string newCode)
        {
            if (string.IsNullOrWhiteSpace(windowId) || string.IsNullOrWhiteSpace(newCode))
                return;

            if (_subscriptions.TryGetValue(windowId, out var oldCode) &&
                !string.Equals(oldCode, newCode, StringComparison.OrdinalIgnoreCase))
            {
                ResetReplaySession(windowId);
                OnLog?.Invoke(windowId, $"🧪 리플레이 종목 변경 내부 초기화: {oldCode} → {newCode}");
            }

            _subscriptions[windowId] = newCode;
            OnLog?.Invoke(windowId, $"🧪 리플레이 구독 등록: {newCode} / 자동매매 ON 전까지 재생 대기");
        }

        public void RegisterInventoryTracking(string windowId, string code, string screenNo)
        {
            if (string.IsNullOrWhiteSpace(code))
                return;

            _subscriptions[$"HOLDING:{code}"] = code;
            OnLog?.Invoke(windowId, $"🧪 리플레이 보유주 감시 등록: {code}");
        }

        public void UnregisterWindow(string windowId)
        {
            _subscriptions.TryRemove(windowId, out _);
            StopReplay(windowId);
        }

        public void RequestMinuteCandles(
            string windowId,
            string code,
            string screenNo,
            int daysToLoad = 2,
            int maxRows = 0,
            int priority = 1,
            int next = 0)
        {
            var options = ResolveOptions(code);
            var candles = _memoryStore.LoadMinuteCandles(code, options.TradingDate)
                .Where(x => x != null && ReplaySessionOptions.IsReplayDataTimeAllowed(x.CandleTime, options.TradingDate, true))
                .ToList();

            if (maxRows > 0 && candles.Count > maxRows)
                candles = candles.Skip(Math.Max(0, candles.Count - maxRows)).ToList();

            OnMinuteHistoryReceived?.Invoke(windowId, code, candles, true);
            OnLog?.Invoke(windowId, $"🧪 리플레이 분봉 메모리 로드: {code} / {candles.Count:N0}개");
        }

        public void RequestRecentTicks(string windowId, string code, string screenNo, int minutes = 10, int priority = 1)
        {
            var options = ResolveOptions(code);

            // Replay 분석 seed도 live 분석 seed와 동일하게 15:20 종가 구간 -5분만 사용한다.
            // 하루치 원천 tick 재생은 PlayAsync 전용 리스트에서 1개씩 전달한다.
            DateTime seedStart = ReplaySessionOptions.ResolveAnalysisTickSeedStartTime(options.TradingDate);
            DateTime seedEnd = ReplaySessionOptions.ResolveSessionEndInclusiveTime(options.TradingDate);
            var ticks = _memoryStore.LoadTicks(code, options.TradingDate)
                .Where(x => x != null && x.LastUpdated >= seedStart && x.LastUpdated <= seedEnd)
                .ToList();

            OnRecentTicksReceived?.Invoke(windowId, code, ticks, true);
            OnLog?.Invoke(windowId, $"🧪 리플레이 최근틱 메모리 로드: {code} / {ticks.Count:N0}개 / seed=15:15~15:20");
        }

        public void RequestProgramTradeHistory(string windowId, string code, string screenNo, int minutes = 30, int priority = 1, int next = 0)
        {
            if (string.IsNullOrWhiteSpace(code))
                return;

            var options = ResolveOptions(code);
            var trades = _memoryStore.LoadProgramTrades(code, options.TradingDate);
            if (trades.Count == 0)
            {
                OnLog?.Invoke(windowId, $"🧪 리플레이 프로그램매매 저장데이터 없음: {code} / 분석 제외");
                return;
            }

            DateTime from = ReplaySessionOptions.ResolveSessionEndTime(options.TradingDate).AddMinutes(-Math.Max(1, minutes));
            DateTime to = ReplaySessionOptions.ResolveSessionEndInclusiveTime(options.TradingDate);
            var seed = trades
                .Where(x => x != null && x.TradeTime >= from && x.TradeTime <= to)
                .OrderBy(x => x.TradeTime)
                .ToList();

            if (seed.Count == 0)
            {
                var orderedAll = trades.OrderBy(x => x.TradeTime).ToList();
                seed = orderedAll.Skip(Math.Max(0, orderedAll.Count - 300)).ToList();
            }

            foreach (var trade in seed)
            {
                var flow = ReplayProgramFlowCalculator.BuildSnapshot(
                    code,
                    trade.TradeTime,
                    trades.Where(x => x != null && x.TradeTime <= trade.TradeTime),
                    "REPLAY_DB");

                var clone = ReplayMemoryDataStore.CloneProgramTrade(trade);
                if (clone != null)
                    clone.Source = "REPLAY_DB";

                OnProgramTradeReceived?.Invoke(windowId, clone, flow);
            }

            OnLog?.Invoke(windowId, $"🧪 리플레이 프로그램매매 메모리 로드: {code} / seed={seed.Count:N0}개 / total={trades.Count:N0}개");
        }

        public void RequestAccountHoldings(string windowId, string accountNo, string screenNo, int priority = 1)
        {
            OnHoldingsReceived?.Invoke(windowId, _accountBook.GetHoldings());
        }

        public void RequestCashInfo(string windowId, string accountNo, string screenNo = "1000")
        {
            OnBalanceUpdated?.Invoke(windowId, _accountBook.Cash);
        }

        public void RequestOrderHistory(string windowId, string accountNo, string screenNo, int next = 0)
        {
            OnOrdersReceived?.Invoke(windowId, _accountBook.GetOrders());
        }

        public void RequestUnfilledOrders(string windowId, string accountNo, string screenNo)
        {
            OnUnfilledOrdersReceived?.Invoke(windowId, _accountBook.GetUnfilledOrders());
        }

        public void RegisterMarketIndexRealtime(string windowId)
        {
            PublishMarketIndexSnapshot();
        }

        public int SendOrder(
            string windowId,
            string screenNo,
            string accountNo,
            int orderType,
            string code,
            int qty,
            int price,
            string hogaType,
            string orgOrderNo = "")
        {
            var now = _clock.Now == DateTime.MinValue ? DateTime.Now : _clock.Now;
            int result = _orderSimulator.SendOrder(accountNo, orderType, code, qty, price, hogaType, now, orgOrderNo);

            if (result == 0)
            {
                lock (_lastMarketDataLock)
                {
                    if (_lastTick != null && _lastTick.Code == code)
                        _orderSimulator.TryFillPendingOrders(_lastTick, _lastHoga);
                }
            }

            return result;
        }

        public List<Symbol> GetTotalSymbolList()
        {
            return _liveApi?.GetTotalSymbolList() ?? new List<Symbol>();
        }

        public Task<ReplayDataPrepareResult> PrepareReplayDataAsync(string windowId, string code)
        {
            var options = ResolveOptions(code);
            var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));

            return Task.Run(async () =>
            {
                var result = await _prepareService.PrepareAsync(windowId, code, options.TradingDate, cts.Token).ConfigureAwait(false);

                if (result != null && result.TradingDate != DateTime.MinValue)
                {
                    _baseOptions.TradingDate = result.TradingDate.Date;
                    _baseOptions.StartTime = result.TradingDate.Date.AddHours(9);
                    _baseOptions.EndTime = ReplaySessionOptions.ResolveSessionEndTime(result.TradingDate);
                    _baseOptions.Normalize();
                }

                if (result == null || !result.IsReady)
                {
                    OnLog?.Invoke(windowId, $"[REPLAY_READY_FAIL] {code} / {result?.Message ?? "Replay 원천 데이터 없음"}");
                    return result;
                }

                LoadReplayProgramTradesToCache(code, result.TradingDate);

                OnLog?.Invoke(windowId,
                    $"[REPLAY_READY] {code} / {result.TradingDate:yyyy-MM-dd} / " +
                    $"Tick={result.TickCount:N0}, Minute={result.MinuteCandleCount:N0}, Hoga={result.HogaCount:N0}, Program={result.ProgramTradeCount:N0} / MemoryOnly");

                return result;
            });
        }

        public void ResetReplaySession(string windowId)
        {
            StopReplay(windowId);
            SetReplayPersistenceDisabled(true);
            _memoryStore.Clear();
            _accountBook.Reset(_baseOptions.InitialCash);

            lock (_lastMarketDataLock)
            {
                _lastTick = null;
                _lastHoga = null;
            }

            ResetReplayRandomState();
            _replayProgramTradesByKey.Clear();
            _replayProgramIndexByKey.Clear();
            _lastReplayProgramPublishedKeyByWindow.Clear();

            PublishAccountState(string.IsNullOrWhiteSpace(windowId) ? "SYSTEM" : windowId);
            OnLog?.Invoke(string.IsNullOrWhiteSpace(windowId) ? "SYSTEM" : windowId,
                $"🧪 리플레이 세션 초기화 / 예수금={_accountBook.Cash:N0} / 메모리 원천데이터 초기화");
        }

        public void StartOrResumeReplay(string windowId, string code)
        {
            if (string.IsNullOrWhiteSpace(windowId) || string.IsNullOrWhiteSpace(code))
            {
                OnLog?.Invoke(windowId ?? "SYSTEM", "[REPLAY_BLOCK] 종목코드 없음");
                return;
            }

            SetReplayPersistenceDisabled(true);
            _subscriptions[windowId] = code;
            _replayDispatchEnabledByWindow[windowId] = true;

            if (_replayTokens.ContainsKey(windowId))
            {
                _clock.Resume();
                OnLog?.Invoke(windowId, $"[REPLAY] 재개: {code} / 분석틱 전달 ON");
                return;
            }

            StartReplay(windowId, code);
        }

        public void PauseReplay(string windowId)
        {
            if (!string.IsNullOrWhiteSpace(windowId))
                _replayDispatchEnabledByWindow[windowId] = false;

            _clock.Pause();
            OnLog?.Invoke(string.IsNullOrWhiteSpace(windowId) ? "SYSTEM" : windowId, "[REPLAY] 일시정지 / 분석틱 전달 OFF");
        }

        public void StartReplay(string windowId, string code)
        {
            if (string.IsNullOrWhiteSpace(windowId) || string.IsNullOrWhiteSpace(code))
                return;

            SetReplayPersistenceDisabled(true);
            StopReplay(windowId);
            SetReplayPersistenceDisabled(true);
            ResetReplayRandomState();

            var cts = new CancellationTokenSource();
            _replayTokens[windowId] = cts;
            _replayDispatchEnabledByWindow[windowId] = true;

            var options = ResolveOptions(code);
            LoadReplayProgramTradesToCache(code, options.TradingDate);

            _ = Task.Run(async () =>
            {
                try
                {
                    await _player.PlayAsync(options, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    OnLog?.Invoke(windowId, $"[REPLAY] 중지: {code}");
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke(windowId, $"[REPLAY_ERROR] {code} / {ex.Message}");
                }
                finally
                {
                    if (_replayTokens.TryGetValue(windowId, out var currentToken) &&
                        ReferenceEquals(currentToken, cts))
                    {
                        _replayTokens.TryRemove(windowId, out _);
                        _replayDispatchEnabledByWindow.TryRemove(windowId, out _);
                    }
                }
            });
        }

        public void StopReplay(string windowId)
        {
            if (string.IsNullOrWhiteSpace(windowId))
                return;

            SetReplayPersistenceDisabled(false);

            _replayDispatchEnabledByWindow.TryRemove(windowId, out _);

            if (_replayTokens.TryRemove(windowId, out var cts))
            {
                try
                {
                    cts.Cancel();
                }
                finally
                {
                    cts.Dispose();
                }
            }
        }

        public void StopAllReplays()
        {
            foreach (var key in _replayTokens.Keys.ToList())
                StopReplay(key);
        }

        public void SetReplaySpeed(double speed)
        {
            _baseOptions.Speed = speed;
            _baseOptions.Normalize();
            _clock.Speed = _baseOptions.Speed;
        }

        private bool IsReplayWindowActive(string windowId)
        {
            if (string.IsNullOrWhiteSpace(windowId))
                return false;

            if (!_replayTokens.ContainsKey(windowId))
                return false;

            return _replayDispatchEnabledByWindow.TryGetValue(windowId, out var enabled) && enabled;
        }

        private List<KeyValuePair<string, string>> GetActiveReplaySubscriptions(string code)
        {
            var result = new List<KeyValuePair<string, string>>();

            if (string.IsNullOrWhiteSpace(code))
                return result;

            foreach (var pair in _subscriptions.ToList())
            {
                if (pair.Key.StartsWith("HOLDING:", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!AreSameReplayCode(pair.Value, code))
                    continue;

                if (!IsReplayWindowActive(pair.Key))
                    continue;

                result.Add(pair);
            }

            return result;
        }

        private bool HasActiveReplaySubscription(string code)
        {
            return GetActiveReplaySubscriptions(code).Count > 0;
        }

        private ReplaySessionOptions ResolveOptions(string code)
        {
            var options = _baseOptions.CloneForCode(code);
            var cutoff = ReplaySessionOptions.ResolveSessionEndTime(options.TradingDate);
            if (options.EndTime == DateTime.MinValue || options.EndTime > cutoff)
                options.EndTime = cutoff;
            options.Normalize();
            return options;
        }

        private void WireReplayModules()
        {
            _player.Log += msg => OnLog?.Invoke("SYSTEM", msg);
            _player.Hoga += hoga =>
            {
                if (hoga == null)
                    return;

                lock (_lastMarketDataLock)
                    _lastHoga = hoga;

                var targets = GetActiveReplaySubscriptions(hoga.Code);
                if (targets.Count == 0)
                    return;

                foreach (var pair in targets)
                    OnHogaReceived?.Invoke(pair.Key, hoga);
            };
            _player.Tick += tick =>
            {
                if (tick == null)
                    return;

                var replayTick = CreateReplayDispatchTick(tick, advanceRandomState: true);
                if (replayTick == null)
                    return;

                var targets = GetActiveReplaySubscriptions(replayTick.Code);
                if (targets.Count == 0)
                    return;

                HogaData hogaForTick;
                lock (_lastMarketDataLock)
                {
                    hogaForTick = _lastHoga;
                    ApplyReplayHogaToTick(replayTick, hogaForTick);
                    _lastTick = replayTick;
                }

                _accountBook.UpdateCurrentPrice(replayTick.Code, replayTick.LastPrice);
                _orderSimulator.TryFillPendingOrders(replayTick, hogaForTick);
                PublishReplayMarketIndexForAnalysis(replayTick.LastUpdated);
                PublishReplayProgramFlowForTick(replayTick.Code, replayTick.LastUpdated, targets);

                foreach (var pair in targets)
                    OnTickReceived?.Invoke(pair.Key, replayTick);

                // Replay에서는 Tick 이벤트 하나로 현재가/분석/차트 흐름을 처리한다.
                // InventoryTick/RealPrice를 별도로 다시 발행하면 같은 tick이 자동매매와 UI에 중복 전달된다.
            };

            _orderSimulator.Log += msg => OnLog?.Invoke("SYSTEM", msg);
            _orderSimulator.OrdersChanged += orders => PublishToWindows((windowId) => OnOrdersReceived?.Invoke(windowId, orders));
            _orderSimulator.UnfilledOrdersChanged += orders => PublishToWindows((windowId) => OnUnfilledOrdersReceived?.Invoke(windowId, orders));
            _orderSimulator.HoldingsChanged += holdings =>
            {
                PublishReplayPositionBalanceChanges(holdings);
                PublishToWindows((windowId) => OnHoldingsReceived?.Invoke(windowId, holdings));
            };
            _orderSimulator.CashChanged += cash =>
            {
                PublishToWindows((windowId) => OnBalanceUpdated?.Invoke(windowId, cash));
                PublishToWindows((windowId) => OnGlobalUpdateRequired?.Invoke(windowId));
            };
            _orderSimulator.OrderFilled += order =>
            {
                if (order == null)
                    return;

                PublishToWindows((windowId) =>
                {
                    OnOrderFilled?.Invoke(
                        windowId,
                        order.Code,
                        string.IsNullOrWhiteSpace(order.Name) ? order.Code : order.Name,
                        order.OrderType,
                        order.ExecQty,
                        order.ExecPrice);
                });
            };
        }


        private void PublishReplayPositionBalanceChanges(List<HoldingInfo> holdings)
        {
            var current = (holdings ?? new List<HoldingInfo>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Code))
                .GroupBy(x => x.Code.Replace("A", string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

            List<Tuple<string, string, int, double>> changes = new List<Tuple<string, string, int, double>>();

            lock (_replayPositionBalanceLock)
            {
                var allCodes = new HashSet<string>(_lastReplayPositionBalanceByCode.Keys, StringComparer.OrdinalIgnoreCase);
                foreach (string code in current.Keys)
                    allCodes.Add(code);

                foreach (string code in allCodes)
                {
                    HoldingInfo now;
                    HoldingInfo before;
                    current.TryGetValue(code, out now);
                    _lastReplayPositionBalanceByCode.TryGetValue(code, out before);

                    int nowQty = now == null ? 0 : Math.Max(0, now.Quantity);
                    int beforeQty = before == null ? 0 : Math.Max(0, before.Quantity);
                    double nowEntry = now == null ? 0.0 : Math.Max(0.0, now.EntryPrice);
                    double beforeEntry = before == null ? 0.0 : Math.Max(0.0, before.EntryPrice);

                    if (nowQty != beforeQty || Math.Abs(nowEntry - beforeEntry) > 0.0001)
                    {
                        changes.Add(Tuple.Create(
                            code,
                            now == null || string.IsNullOrWhiteSpace(now.Name) ? code : now.Name,
                            nowQty,
                            nowEntry));
                    }
                }

                _lastReplayPositionBalanceByCode.Clear();
                foreach (var pair in current)
                    _lastReplayPositionBalanceByCode[pair.Key] = pair.Value;
            }

            foreach (var change in changes)
                OnPositionBalanceChanged?.Invoke(change.Item1, change.Item2, change.Item3, change.Item4);
        }

        private void LoadReplayProgramTradesToCache(string code, DateTime tradingDate)
        {
            string key = BuildReplayProgramKey(code, tradingDate);
            if (string.IsNullOrWhiteSpace(key))
                return;

            var trades = _memoryStore.LoadProgramTrades(code, tradingDate)
                .Where(x => x != null && x.TradeTime != DateTime.MinValue)
                .OrderBy(x => x.TradeTime)
                .ToList();

            _replayProgramTradesByKey[key] = trades;
            _replayProgramIndexByKey[key] = -1;
        }

        private void PublishReplayProgramFlowForTick(string code, DateTime tickTime, List<KeyValuePair<string, string>> targets)
        {
            if (string.IsNullOrWhiteSpace(code) || tickTime == DateTime.MinValue || targets == null || targets.Count == 0)
                return;

            string key = BuildReplayProgramKey(code, tickTime.Date);
            if (string.IsNullOrWhiteSpace(key))
                return;

            List<ProgramTradeInfo> trades;
            if (!_replayProgramTradesByKey.TryGetValue(key, out trades) || trades == null || trades.Count == 0)
            {
                LoadReplayProgramTradesToCache(code, tickTime.Date);
                _replayProgramTradesByKey.TryGetValue(key, out trades);
            }

            if (trades == null || trades.Count == 0)
                return;

            int index;
            _replayProgramIndexByKey.TryGetValue(key, out index);
            if (index < -1)
                index = -1;

            while (index + 1 < trades.Count && trades[index + 1].TradeTime <= tickTime)
                index++;

            if (index < 0 || index >= trades.Count)
                return;

            _replayProgramIndexByKey[key] = index;
            var latestTrade = trades[index];
            if (latestTrade == null)
                return;

            bool hasTargetToPublish = false;
            foreach (var pair in targets)
            {
                string publishKey = $"{pair.Key}_{code}_{latestTrade.TradeTime:yyyyMMddHHmmss}";
                string lastKey;
                if (!_lastReplayProgramPublishedKeyByWindow.TryGetValue(pair.Key, out lastKey) ||
                    !string.Equals(lastKey, publishKey, StringComparison.OrdinalIgnoreCase))
                {
                    hasTargetToPublish = true;
                    break;
                }
            }

            if (!hasTargetToPublish)
                return;

            var basis = trades.Take(index + 1).ToList();
            var flow = ReplayProgramFlowCalculator.BuildSnapshot(code, latestTrade.TradeTime, basis, "REPLAY_DB");
            if (flow == null)
                return;

            foreach (var pair in targets)
            {
                string publishKey = $"{pair.Key}_{code}_{latestTrade.TradeTime:yyyyMMddHHmmss}";
                string lastKey;
                if (_lastReplayProgramPublishedKeyByWindow.TryGetValue(pair.Key, out lastKey) &&
                    string.Equals(lastKey, publishKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                _lastReplayProgramPublishedKeyByWindow[pair.Key] = publishKey;

                var trade = ReplayMemoryDataStore.CloneProgramTrade(latestTrade);
                if (trade != null)
                    trade.Source = "REPLAY_DB";

                OnProgramTradeReceived?.Invoke(pair.Key, trade, flow);
            }
        }

        private static string BuildReplayProgramKey(string code, DateTime tradingDate)
        {
            string normalizedCode = NormalizeReplayCode(code);
            if (string.IsNullOrWhiteSpace(normalizedCode))
                return string.Empty;

            DateTime day = tradingDate == DateTime.MinValue ? DateTime.Today : tradingDate.Date;
            return $"{normalizedCode}_{day:yyyyMMdd}";
        }

        private static void ApplyReplayHogaToTick(TrackedSymbol tick, HogaData hoga)
        {
            if (tick == null || hoga == null)
                return;

            if (!AreSameReplayCode(tick.Code, hoga.Code))
                return;

            long askQty = ResolveReplayHogaAskQty(hoga);
            long bidQty = ResolveReplayHogaBidQty(hoga);

            if (askQty > 0)
                tick.TotalAskQty = askQty;

            if (bidQty > 0)
                tick.TotalBidQty = bidQty;
        }

        private static long ResolveReplayHogaAskQty(HogaData hoga)
        {
            if (hoga == null)
                return 0;

            if (hoga.TotalAskQty > 0)
                return hoga.TotalAskQty;

            long total = 0;
            if (hoga.Asks != null)
            {
                foreach (var item in hoga.Asks)
                {
                    if (item != null && item.Quantity > 0)
                        total += item.Quantity;
                }
            }

            return total;
        }

        private static long ResolveReplayHogaBidQty(HogaData hoga)
        {
            if (hoga == null)
                return 0;

            if (hoga.TotalBidQty > 0)
                return hoga.TotalBidQty;

            long total = 0;
            if (hoga.Bids != null)
            {
                foreach (var item in hoga.Bids)
                {
                    if (item != null && item.Quantity > 0)
                        total += item.Quantity;
                }
            }

            return total;
        }

        private static bool AreSameReplayCode(string left, string right)
        {
            return string.Equals(
                NormalizeReplayCode(left),
                NormalizeReplayCode(right),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeReplayCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return string.Empty;

            code = code.Trim();

            if (code.StartsWith("A", StringComparison.OrdinalIgnoreCase) && code.Length > 1)
                code = code.Substring(1);

            return code;
        }

        private void ResetReplayRandomState()
        {
            lock (_replayRandomLock)
            {
                _lastReplayRandomTickKey = string.Empty;
                _replayStrengthReady = false;
                _replayStrength = 0;
                _replayMarketIndexReady = false;
                _replayKospiChangeRate = 0;
                _replayKosdaqChangeRate = 0;
                _lastReplayMarketIndexPublishedAt = DateTime.MinValue;
            }
        }

        private TrackedSymbol CreateReplayDispatchTick(TrackedSymbol source, bool advanceRandomState)
        {
            if (source == null)
                return null;

            double strength;
            string tickKey = BuildReplayRandomTickKey(source);

            lock (_replayRandomLock)
            {
                if (advanceRandomState && (!_replayStrengthReady || _lastReplayRandomTickKey != tickKey))
                {
                    if (!_replayStrengthReady)
                    {
                        _replayStrength = NextReplayRandomDouble(60.0, 120.0);
                        _replayStrengthReady = true;
                    }
                    else
                    {
                        _replayStrength += _replayRandom.Next(0, 2) == 0 ? -0.1 : 0.1;
                    }

                    _replayStrength = Clamp(_replayStrength, 0.0, 160.0);
                    _lastReplayRandomTickKey = tickKey;
                }

                strength = _replayStrengthReady ? _replayStrength : source.Strength;
            }

            var clone = CloneTrackedSymbol(source);
            clone.Strength = strength;
            return clone;
        }

        private void PublishReplayMarketIndexForAnalysis(DateTime updatedAt)
        {
            if (updatedAt == DateTime.MinValue)
                updatedAt = _clock.Now == DateTime.MinValue ? DateTime.Now : _clock.Now;

            double kospi;
            double kosdaq;
            bool shouldPublish;

            lock (_replayRandomLock)
            {
                shouldPublish = _lastReplayMarketIndexPublishedAt == DateTime.MinValue ||
                    updatedAt >= _lastReplayMarketIndexPublishedAt.AddSeconds(1);

                if (!shouldPublish)
                    return;

                _lastReplayMarketIndexPublishedAt = updatedAt;

                if (!_replayMarketIndexReady)
                {
                    _replayKospiChangeRate = NextReplayRandomDouble(-3.0, 3.0);
                    _replayKosdaqChangeRate = NextReplayRandomDouble(-3.0, 3.0);
                    _replayMarketIndexReady = true;
                }
                else
                {
                    _replayKospiChangeRate += _replayRandom.Next(0, 2) == 0 ? -0.1 : 0.1;
                    _replayKosdaqChangeRate += _replayRandom.Next(0, 2) == 0 ? -0.1 : 0.1;
                }

                _replayKospiChangeRate = Clamp(_replayKospiChangeRate, -5.0, 5.0);
                _replayKosdaqChangeRate = Clamp(_replayKosdaqChangeRate, -5.0, 5.0);

                kospi = _replayKospiChangeRate;
                kosdaq = _replayKosdaqChangeRate;
            }

            PublishReplayMarketIndexSnapshot(updatedAt, kospi, kosdaq);
        }

        private void PublishReplayMarketIndexSnapshot(DateTime updatedAt, double kospiChangeRate, double kosdaqChangeRate)
        {
            if (updatedAt == DateTime.MinValue)
                updatedAt = _clock.Now == DateTime.MinValue ? DateTime.Now : _clock.Now;

            OnMarketIndexReceived?.Invoke(new MarketIndexInfo
            {
                Code = "001",
                Name = "KOSPI",
                Price = 0,
                Change = kospiChangeRate,
                ChangeRate = kospiChangeRate,
                Volume = 0,
                UpdatedAt = updatedAt
            });

            OnMarketIndexReceived?.Invoke(new MarketIndexInfo
            {
                Code = "101",
                Name = "KOSDAQ",
                Price = 0,
                Change = kosdaqChangeRate,
                ChangeRate = kosdaqChangeRate,
                Volume = 0,
                UpdatedAt = updatedAt
            });
        }

        private static TrackedSymbol CloneTrackedSymbol(TrackedSymbol source)
        {
            var clone = new TrackedSymbol();
            if (source == null)
                return clone;

            foreach (var prop in typeof(TrackedSymbol).GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!prop.CanRead || !prop.CanWrite || prop.GetIndexParameters().Length > 0)
                    continue;

                prop.SetValue(clone, prop.GetValue(source, null), null);
            }

            return clone;
        }

        private static string BuildReplayRandomTickKey(TrackedSymbol tick)
        {
            if (tick == null)
                return string.Empty;

            return $"{tick.Code}|{tick.LastUpdated:yyyyMMddHHmmssfff}|{tick.LastPrice}|{tick.LastVolume}";
        }

        private double NextReplayRandomDouble(double min, double max)
        {
            return min + _replayRandom.NextDouble() * (max - min);
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min)
                return min;

            if (value > max)
                return max;

            return value;
        }

        private void PublishAccountState(string windowId)
        {
            OnBalanceUpdated?.Invoke(windowId, _accountBook.Cash);
            OnHoldingsReceived?.Invoke(windowId, _accountBook.GetHoldings());
            OnOrdersReceived?.Invoke(windowId, _accountBook.GetOrders());
            OnUnfilledOrdersReceived?.Invoke(windowId, _accountBook.GetUnfilledOrders());
        }

        private void PublishToWindows(Action<string> publisher)
        {
            if (publisher == null)
                return;

            var windowIds = _subscriptions.Keys
                .Where(x => !string.IsNullOrWhiteSpace(x) && !x.StartsWith("HOLDING:", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (windowIds.Count == 0)
            {
                publisher("SYSTEM");
                return;
            }

            foreach (var windowId in windowIds)
                publisher(windowId);
        }

        private void PublishMarketIndexSnapshot()
        {
            var now = _clock.Now == DateTime.MinValue ? DateTime.Now : _clock.Now;

            OnMarketIndexReceived?.Invoke(new MarketIndexInfo
            {
                Code = "001",
                Name = "KOSPI",
                Price = 0,
                Change = 0,
                ChangeRate = 0,
                Volume = 0,
                UpdatedAt = now
            });

            OnMarketIndexReceived?.Invoke(new MarketIndexInfo
            {
                Code = "101",
                Name = "KOSDAQ",
                Price = 0,
                Change = 0,
                ChangeRate = 0,
                Volume = 0,
                UpdatedAt = now
            });
        }
    }
}
