using AxKHOpenAPILib;
using MoneyCreator.DataBase;
using MoneyCreator.Models;
using MoneyCreator.ViewModels;
using ScottPlot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using System.Xml.Linq;
using static OpenTK.Graphics.OpenGL.GL;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.TrackBar;

namespace MoneyCreator.Service
{
    public class KiwoomApiService : IMarketApiService
    {
        private static readonly Lazy<KiwoomApiService> _instance = new Lazy<KiwoomApiService>(() => new KiwoomApiService());
        public static KiwoomApiService Instance => _instance.Value;

        private AxKHOpenAPI _api;
        private StockDbManager _dbManager;
        private readonly AiJudgeService _aiJudge = new AiJudgeService();
        private TrScheduler _scheduler; // [추가] 스케줄러 클래스

        private ConcurrentDictionary<string, (double strength, double change)> _snapshot = new ConcurrentDictionary<string, (double, double)>();
        private ConcurrentDictionary<string, TrackedSymbol> _trackedSymbols = new ConcurrentDictionary<string, TrackedSymbol>();
        private readonly ConcurrentDictionary<string, RealSubscription> _windowSubscriptions = new ConcurrentDictionary<string, RealSubscription>();
        private readonly object _lock = new object();
        private readonly ConcurrentDictionary<string, DateTime> _lastOrderHistoryRequestAt = new ConcurrentDictionary<string, DateTime>();
        private readonly ConcurrentDictionary<string, string> _symbolScreenTokens = new ConcurrentDictionary<string, string>();
        private readonly ConcurrentDictionary<string, int> _minuteRequestCycleMap = new ConcurrentDictionary<string, int>();
        private readonly ConcurrentDictionary<string, int> _programTradeRequestCycleMap = new ConcurrentDictionary<string, int>();
        private readonly ConcurrentDictionary<string, string> _orderWindowMap = new ConcurrentDictionary<string, string>();
        private readonly ConcurrentDictionary<string, List<ProgramTradeInfo>> _programTradeBuffers = new ConcurrentDictionary<string, List<ProgramTradeInfo>>();
        private readonly object _programTradeLock = new object();

        private const string FullSymbolRealtimeFids =
            "10;11;12;13;15;20;228;" +
            "41;42;43;44;45;46;47;48;49;50;" +
            "51;52;53;54;55;56;57;58;59;60;" +
            "61;62;63;64;65;66;67;68;69;70;" +
            "71;72;73;74;75;76;77;78;79;80;" +
            "121;125;" +
            "202;204;206;208;210;211;212;213;214";
        // 일반 창/잔고 감시 모두 종목별 통합 등록부의 FullSymbolRealtimeFids 하나만 사용한다.
        private const int RealtimeRegistrationRetryDelayMilliseconds = 1000;
        private const int RealtimeRegistrationMaxDelayedRetries = 2;
        private const int ProgramRealtimeInitialHealthCheckSeconds = 120;

        private readonly ConcurrentDictionary<string, bool> _realtimeRegistrationRetryPending =
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> _realtimeRegistrationRetryCount =
            new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> _realtimeRegistrationProgramHealthWatchRequested =
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _fullRealtimeRegisteredAtByCode =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> _programRealtimeSeenSinceRegistration =
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> _programRealtimeHealthRecoveryAttempted =
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        // 실제 키움 SetRealReg 상태는 종목별 1개만 유지한다.
        // _windowSubscriptions는 일반 창/HOLDING 소비자 목록이고, 이 맵은 서버 등록 성공 상태다.
        private readonly ConcurrentDictionary<string, bool> _unifiedSymbolRealtimeRegistered =
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public event Action<bool> OnLogin;
        public event Action<string, string> OnLog;
        public event Action<string, string, string> OnSymbolDetected;
        public event Action<string, Symbol> OnSymbolDetailUpdated;
        public event Action<string, string, string> OnAiSymbolDetected;
        public event Action<string, string, double, int, DateTime> OnRealPriceReceived;
        public event Action<string, List<TrackedSymbol>, bool> OnInitialHistoryReceived;
        public event Action<string, TrackedSymbol> OnTickReceived;
        public event Action<string, HogaData> OnHogaReceived;
        public event Action<string, long> OnBalanceUpdated; // windowId와 balance를 전달
        public event Action<string, List<HoldingInfo>> OnHoldingsReceived; //보유주식 정보 전달
        public event Action<string, string, int, double> OnPositionBalanceChanged; // code, name, absoluteQty, averagePrice
        public event Action<string, List<OrderInfo>> OnOrdersReceived; // 주문내역 정보 전달
        public event Action<string, List<OrderInfo>> OnUnfilledOrdersReceived; // 미체결 주문내역 정보 전달
        public event Action<string> OnGlobalUpdateRequired; //주문
        public event Action<string, string, string, string, int, double> OnOrderFilled;
        public event Action<MarketIndexInfo> OnMarketIndexReceived;
        public event Action OnConnectionLost;
        public event Action OnReconnected;
        // code, name, filledQty, filledPrice
        public event Action<TrackedSymbol> OnInventoryTickReceived; //보유주 정보담기
        // ============================================================
        // [추가] 이벤트: 기존 OnInitialHistoryReceived 아래쪽에 추가
        // ============================================================
        public event Action<string, string, List<MinuteCandleRecord>, bool> OnMinuteHistoryReceived;
        public event Action<string, string, List<TrackedSymbol>, bool> OnRecentTicksReceived;
        public event Action<string, ProgramTradeInfo, ProgramFlowSnapshotRecord> OnProgramTradeReceived;

        // ============================================================
        // [추가] DB Manager 외부 공유용. ViewModel에서 별도 StockDbManager를 만들지 않게 하기 위함.
        // ============================================================
        public StockDbManager DbManager => _dbManager;

        private readonly ConcurrentDictionary<string, HogaSnapshot> _latestHoga = new ConcurrentDictionary<string, HogaSnapshot>();
        private readonly ConcurrentDictionary<string, TickData> _tickBuffer = new ConcurrentDictionary<string, TickData>();
        private readonly ConcurrentDictionary<string, bool> _inventoryRegistered = new ConcurrentDictionary<string, bool>();
        private readonly ConcurrentDictionary<string, List<OrderInfo>> _orderHistoryBuffer = new ConcurrentDictionary<string, List<OrderInfo>>();
        //private System.Timers.Timer _flushTimer;

        public TrScheduler GetScheduler() => _scheduler;

        public ConcurrentQueue<TickData> _tickQueue = new ConcurrentQueue<TickData>();
        public enum MarketStatus { None, PreMarket, MarketOpen, AfterMarket }
        public MarketStatus CurrentMarketStatus { get; private set; } = MarketStatus.None;

        public enum MarketSafetyState
        {
            Normal,
            HaltSuspected,
            CircuitBreaker,
            RecoveryCooldown
        }

        private readonly object _marketSafetyLock = new object();
        private readonly ConcurrentDictionary<string, DateTime> _lastStockTickReceivedAt = new ConcurrentDictionary<string, DateTime>();
        private DateTime _lastAnyStockTickReceivedAt = DateTime.MinValue;
        private DateTime _marketSafetyBlockBuyUntil = DateTime.MinValue;
        private DateTime _marketSafetyUpdatedAt = DateTime.MinValue;
        private DateTime _lastMarketSafetyLogAt = DateTime.MinValue;
        private DateTime _lastExpectedAuctionLogAt = DateTime.MinValue;
        private DateTime _lastMarketSafetyRegularOpenResetDate = DateTime.MinValue;
        private double _latestKospiChangeRate = 0;
        private double _latestKosdaqChangeRate = 0;

        private const int MarketSafetyTickStopSeconds = 20;
        private const int MarketSafetyRecoveryCooldownMinutes = 5;
        private const int MarketSafetyCircuitBreakerBlockMinutes = 40;
        private const int MarketSafetyExpectedAuctionCooldownMinutes = 5;
        private static readonly TimeSpan RegularStockRealtimeStartTime = new TimeSpan(9, 0, 0);
        private static readonly TimeSpan RegularStockRealtimeEndTime = new TimeSpan(15, 20, 0);

        public MarketSafetyState CurrentMarketSafetyState { get; private set; } = MarketSafetyState.Normal;
        public string CurrentMarketSafetyReason { get; private set; } = string.Empty;
        public DateTime MarketSafetyBlockBuyUntil => _marketSafetyBlockBuyUntil;

        private int _marketIndexRegistered = 0;
        private int _activeTrCount = 0;
        public int ActiveTrCount => _activeTrCount;

        private System.Threading.Timer _connectionMonitorTimer;
        private readonly object _connectionMonitorLock = new object();
        private int _connectionMonitorStarted;
        private int _connectionMonitorStopped;
        private int _kiwoomComShutdownInProgress;
        private int _shutdownGuardAttached;
        private int _connectionLostRaised;
        private int _hasConnectedOnce;
        private int _reconnectInProgress;
        private int _disconnectHitCount;
        private DateTime _lastReconnectAttemptAt = DateTime.MinValue;

        private const int ConnectionMonitorIntervalMilliseconds = 5000;
        private const int ConnectionLostConfirmCount = 2;
        private const int ReconnectRetrySeconds = 10;

        private int _screenSeed = 1000;
        private int SafeInt(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 0;

            value = value.Replace(",", "").Trim();

            int.TryParse(value, out int result);
            return Math.Abs(result);
        }

        private double SafeDouble(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 0;

            value = value.Replace(",", "").Trim();

            double.TryParse(value, out double result);
            return Math.Abs(result);
        }

        public string GetScreenNo()
        {
            return Interlocked.Increment(ref _screenSeed).ToString("D4");
        }

        private string GetOrCreateSymbolToken(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return GetScreenNo();

            return _symbolScreenTokens.GetOrAdd(code, _ => GetScreenNo());
        }

        private void ReleaseSymbolToken(string code)
        {
            if (!string.IsNullOrWhiteSpace(code))
                _symbolScreenTokens.TryRemove(code, out _);
        }

        public string GetSymbolToken(string code)
        {
            return GetOrCreateSymbolToken(code);
        }

        private bool IsSymbolTrType(string requestType)
        {
            return requestType == "PREV" ||
                   requestType == "DETAIL" ||
                   requestType == "MINUTE" ||
                   requestType == "RECENT_TICKS" ||
                   requestType == "PROGRAM_TRADE";
        }

        private string BuildOrderRouteKey(string code, int orderType)
        {
            string side;

            switch (orderType)
            {
                case 1:
                case 3:
                case 5:
                    side = "BUY";
                    break;

                case 2:
                case 4:
                case 6:
                    side = "SELL";
                    break;

                default:
                    side = "UNKNOWN";
                    break;
            }

            return $"{code}_{side}";
        }

        private string BuildOrderRouteKey(string code, string orderType)
        {
            orderType = orderType ?? string.Empty;

            string side;

            if (orderType.Contains("매수"))
                side = "BUY";
            else if (orderType.Contains("매도"))
                side = "SELL";
            else
                side = "UNKNOWN";

            return $"{code}_{side}";
        }

        private KiwoomApiService()
        {
            _dbManager = new StockDbManager(_tickQueue);
            Task.Run(() => _dbManager.DbSaveWorker());

            // [추가] 스케줄러 초기화: 실제 실행 로직(ExecuteTr)을 주입
            _scheduler = new TrScheduler(ExecuteTr, () => this.ActiveTrCount);
        }



        public void SetApiInstance(AxKHOpenAPI api)
        {
            if (_api != null) return;

            Interlocked.Exchange(ref _kiwoomComShutdownInProgress, 0);
            Interlocked.Exchange(ref _connectionMonitorStopped, 0);
            AttachApplicationShutdownGuard();

            _api = api;
            _api.OnEventConnect += Api_OnEventConnect;
            _api.OnReceiveTrCondition += Api_OnReceiveTrCondition;
            _api.OnReceiveRealData += Api_OnReceiveRealData;
            _api.OnReceiveRealCondition += Api_OnReceiveRealCondition;
            _api.OnReceiveTrData += Api_OnReceiveTrData;
            _api.OnReceiveChejanData += Api_OnReceiveChejanData;
            _api.OnReceiveMsg += Api_OnReceiveMsg;

            StartConnectionMonitor();
        }
        private void Api_OnReceiveChejanData(object sender, _DKHOpenAPIEvents_OnReceiveChejanDataEvent e)
        {
            string code = _api.GetChejanData(9001).Replace("A", "").Trim();
            string name = _api.GetChejanData(302).Trim();

            if (e.sGubun == "0")
            {
                string status = _api.GetChejanData(913).Trim();
                string orderType = _api.GetChejanData(905).Trim();

                int totalFilledQty = SafeInt(_api.GetChejanData(911));
                int filledQty = SafeInt(_api.GetChejanData(911));
                int remainQty = SafeInt(_api.GetChejanData(902));
                double filledPrice = SafeDouble(_api.GetChejanData(910));

                string orderNo = _api.GetChejanData(9203).Trim();
                string rawOrderTime = _api.GetChejanData(908).Trim();

                int orderQty = SafeInt(_api.GetChejanData(900));
                double orderPrice = SafeDouble(_api.GetChejanData(901));

                OrderStatus orderStatus = ParseOrderStatus(status, orderType, filledQty, remainQty);

                string routeKey = BuildOrderRouteKey(code, orderType);

                string targetWindowId;
                if (!_orderWindowMap.TryGetValue(routeKey, out targetWindowId))
                    targetWindowId = "COMMON";

                var order = new OrderInfo
                {
                    OrderNo = string.IsNullOrWhiteSpace(orderNo)
                        ? $"CHEJAN_{DateTime.Now:yyyyMMddHHmmssfff}_{code}"
                        : orderNo,
                    Code = code,
                    Name = name,
                    OrderTime = NormalizeOrderTime(rawOrderTime),
                    Status = status,
                    OrderType = orderType,
                    Qty = orderQty,
                    ExecQty = filledQty,
                    Price = Math.Abs(orderPrice),
                    ExecPrice = Math.Abs(filledPrice),
                    UntradedQty = remainQty,
                    OrderStatus = orderStatus,
                    StrategyName = "CHEJAN"
                };

                OnOrdersReceived?.Invoke("COMMON", new List<OrderInfo> { order });

                if (filledQty > 0)
                {
                    OnOrderFilled?.Invoke(
                        targetWindowId,
                        code,
                        name,
                        orderType,
                        filledQty,
                        Math.Abs(filledPrice));
                }

                if (remainQty <= 0 ||
                    orderStatus == OrderStatus.FullyFilled ||
                    orderStatus == OrderStatus.Cancelled ||
                    orderStatus == OrderStatus.Rejected)
                {
                    string removed;
                    _orderWindowMap.TryRemove(routeKey, out removed);
                }
            }
            else if (e.sGubun == "1")
            {
                // Balance Chejan is the fastest authoritative source for the absolute position.
                // Do not wait for the serial account TR queue before clearing a fully sold holding.
                int qty = SafeInt(_api.GetChejanData(930));
                double avgPrice = SafeDouble(_api.GetChejanData(931));

                if (!string.IsNullOrWhiteSpace(code))
                    OnPositionBalanceChanged?.Invoke(code, name, qty, avgPrice);
            }
        }
        private string NormalizeOrderTime(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return DateTime.Now.ToString("yyyyMMddHHmmss");

            raw = raw.Trim();

            DateTime dt;

            if (DateTime.TryParseExact(raw, "HH:mm:ss", null,
                System.Globalization.DateTimeStyles.None, out dt))
                return DateTime.Today.Add(dt.TimeOfDay).ToString("yyyyMMddHHmmss");

            if (DateTime.TryParseExact(raw, "HHmmss", null,
                System.Globalization.DateTimeStyles.None, out dt))
                return DateTime.Today.Add(dt.TimeOfDay).ToString("yyyyMMddHHmmss");

            return raw;
        }


        private OrderStatus ParseOrderStatus(
      string status,
      string orderType,
      int execQty,
      int untradedQty)
        {
            status = status?.Trim() ?? string.Empty;
            orderType = orderType?.Trim() ?? string.Empty;

            if (orderType.Contains("취소"))
                return OrderStatus.Cancelled;

            if (orderType.Contains("정정") && status == "확인")
                return OrderStatus.Corrected;

            if (execQty > 0 && untradedQty > 0)
                return OrderStatus.PartiallyFilled;

            if (execQty > 0 && untradedQty <= 0)
                return OrderStatus.FullyFilled;

            if (untradedQty > 0)
            {
                if (status == "접수")
                    return OrderStatus.Received;

                return OrderStatus.Confirmed;
            }

            if (status == "접수")
                return OrderStatus.Received;

            if (status == "확인")
                return OrderStatus.Confirmed;

            if (status == "체결")
                return OrderStatus.FullyFilled;

            if (status == "부분체결")
                return OrderStatus.PartiallyFilled;

            if (status == "정정")
                return OrderStatus.Corrected;

            if (status == "취소")
                return OrderStatus.Cancelled;

            if (status == "거부")
                return OrderStatus.Rejected;

            return OrderStatus.None;
        }

        private bool IsRealtimeApiReady()
        {
            return _api != null &&
                   !_api.IsDisposed &&
                   _api.IsHandleCreated &&
                   !IsKiwoomComShutdownInProgress();
        }

        private static string BuildRealtimeRegistrationKey(string screenNo, string code)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}",
                string.IsNullOrWhiteSpace(screenNo) ? "-" : screenNo.Trim(),
                string.IsNullOrWhiteSpace(code) ? "-" : code.Trim());
        }

        private void ClearProgramRealtimeHealthState(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return;

            _fullRealtimeRegisteredAtByCode.TryRemove(code, out _);
            _programRealtimeSeenSinceRegistration.TryRemove(code, out _);
            _programRealtimeHealthRecoveryAttempted.TryRemove(code, out _);
        }

        private void StartProgramRealtimeHealthWatch(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return;

            _fullRealtimeRegisteredAtByCode[code] = DateTime.Now;
            _programRealtimeSeenSinceRegistration[code] = false;
            _programRealtimeHealthRecoveryAttempted.TryRemove(code, out _);
        }

        private void MarkProgramRealtimeReceived(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return;

            _programRealtimeSeenSinceRegistration[code] = true;
        }

        private bool HasRealtimeConsumers(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return false;

            string normalizedCode = code.Trim();
            return _windowSubscriptions.Values.Any(x =>
                x != null &&
                string.Equals(x.Code, normalizedCode, StringComparison.OrdinalIgnoreCase));
        }

        private bool HasNormalWindowRealtimeConsumer(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return false;

            string normalizedCode = code.Trim();
            return _windowSubscriptions.Any(x =>
                x.Value != null &&
                string.Equals(x.Value.Code, normalizedCode, StringComparison.OrdinalIgnoreCase) &&
                !x.Key.StartsWith("HOLDING:", StringComparison.OrdinalIgnoreCase));
        }

        private RealSubscription GetUnifiedRealtimeRepresentative(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return null;

            string normalizedCode = code.Trim();

            var normal = _windowSubscriptions
                .Where(x =>
                    x.Value != null &&
                    string.Equals(x.Value.Code, normalizedCode, StringComparison.OrdinalIgnoreCase) &&
                    !x.Key.StartsWith("HOLDING:", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Value)
                .FirstOrDefault();

            if (normal != null)
                return normal;

            return _windowSubscriptions
                .Where(x =>
                    x.Value != null &&
                    string.Equals(x.Value.Code, normalizedCode, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Value)
                .FirstOrDefault();
        }

        private int EnsureUnifiedSymbolRealtimeRegistration(
            string windowId,
            string code,
            string reason,
            bool forceReset = false,
            bool startProgramHealthWatch = true)
        {
            string safeWindowId = string.IsNullOrWhiteSpace(windowId) ? "COMMON" : windowId;
            string normalizedCode = string.IsNullOrWhiteSpace(code) ? string.Empty : code.Trim();

            if (string.IsNullOrWhiteSpace(normalizedCode))
                return -101;

            if (!HasRealtimeConsumers(normalizedCode))
            {
                OnLog?.Invoke(
                    safeWindowId,
                    $"[통합 실시간등록 취소] 소비자 없음 / code={normalizedCode} / reason={reason}");
                return -102;
            }

            string token = GetOrCreateSymbolToken(normalizedCode);
            bool hasNormalWindow = HasNormalWindowRealtimeConsumer(normalizedCode);
            bool shouldStartProgramHealthWatch = startProgramHealthWatch && hasNormalWindow;

            bool alreadyRegistered = false;
            _unifiedSymbolRealtimeRegistered.TryGetValue(normalizedCode, out alreadyRegistered);

            if (!forceReset && alreadyRegistered)
            {
                // 잔고 감시만 먼저 등록된 뒤 일반 창이 붙은 경우에도 실제 FID는 처음부터 전체 58개다.
                // 따라서 재등록하지 않고 프로그램매매 Health Watch만 활성화한다.
                if (shouldStartProgramHealthWatch && !_fullRealtimeRegisteredAtByCode.ContainsKey(normalizedCode))
                    StartProgramRealtimeHealthWatch(normalizedCode);

                Debug.WriteLine(
                    $"[통합 실시간등록 유지] code={normalizedCode} / screen={token} / reason={reason}");
                return 0;
            }

            // 종목별 screen token은 이 종목 전용이므로 최초 실제 등록은 항상 "0" 신규등록으로 시작한다.
            // 이후 일반 창/잔고 소비자 추가는 실제 SetRealReg를 다시 호출하지 않는다.
            bool registrationReset = forceReset || !alreadyRegistered;

            int result = RegisterRealtimeWithRecovery(
                safeWindowId,
                normalizedCode,
                token,
                FullSymbolRealtimeFids,
                registrationReset,
                reason,
                startProgramHealthWatch: shouldStartProgramHealthWatch);

            if (result == 0)
                _unifiedSymbolRealtimeRegistered[normalizedCode] = true;
            else
                _unifiedSymbolRealtimeRegistered.TryRemove(normalizedCode, out _);

            return result;
        }

        private void ReleaseUnifiedSymbolRealtimeIfUnused(string code, string reason)
        {
            if (string.IsNullOrWhiteSpace(code))
                return;

            string normalizedCode = code.Trim();

            if (HasRealtimeConsumers(normalizedCode))
            {
                if (!HasNormalWindowRealtimeConsumer(normalizedCode))
                    ClearProgramRealtimeHealthState(normalizedCode);

                return;
            }

            string token;
            _symbolScreenTokens.TryGetValue(normalizedCode, out token);

            try
            {
                if (!string.IsNullOrWhiteSpace(token) && IsRealtimeApiReady())
                    _api.SetRealRemove(token, normalizedCode);
            }
            catch (System.Runtime.InteropServices.InvalidComObjectException)
            {
                MarkKiwoomComShutdown("ReleaseUnifiedRealtimeInvalidComObject");
            }
            catch (ObjectDisposedException)
            {
                MarkKiwoomComShutdown("ReleaseUnifiedRealtimeObjectDisposed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[통합 실시간해제 예외] code={normalizedCode} / screen={token} / reason={reason} / {ex.Message}");
            }

            _unifiedSymbolRealtimeRegistered.TryRemove(normalizedCode, out _);
            _inventoryRegistered.TryRemove(normalizedCode, out _);
            ClearProgramRealtimeHealthState(normalizedCode);

            if (!string.IsNullOrWhiteSpace(token))
            {
                string registrationKey = BuildRealtimeRegistrationKey(token, normalizedCode);
                _realtimeRegistrationRetryPending.TryRemove(registrationKey, out _);
                _realtimeRegistrationRetryCount.TryRemove(registrationKey, out _);
                _realtimeRegistrationProgramHealthWatchRequested.TryRemove(registrationKey, out _);
            }

            ReleaseSymbolToken(normalizedCode);

            OnLog?.Invoke(
                "COMMON",
                $"[통합 실시간해제] 마지막 소비자 제거 / code={normalizedCode} / screen={token} / reason={reason}");
        }

        private int RegisterRealtimeWithRecovery(
            string windowId,
            string code,
            string screenNo,
            string fids,
            bool forceReset,
            string reason,
            bool startProgramHealthWatch,
            bool allowDelayedRetry = true)
        {
            string safeWindowId = string.IsNullOrWhiteSpace(windowId) ? "COMMON" : windowId;
            string safeCode = string.IsNullOrWhiteSpace(code) ? string.Empty : code.Trim();
            string safeScreenNo = string.IsNullOrWhiteSpace(screenNo) ? string.Empty : screenNo.Trim();
            string safeReason = string.IsNullOrWhiteSpace(reason) ? "RealtimeRegistration" : reason.Trim();

            if (string.IsNullOrWhiteSpace(safeCode) ||
                string.IsNullOrWhiteSpace(safeScreenNo) ||
                string.IsNullOrWhiteSpace(fids))
            {
                OnLog?.Invoke(
                    safeWindowId,
                    $"[실시간등록 실패] 필수값 누락 / code={safeCode} / screen={safeScreenNo} / reason={safeReason}");
                return -101;
            }

            if (!IsRealtimeApiReady())
            {
                OnLog?.Invoke(
                    safeWindowId,
                    $"[실시간등록 보류] API 준비 안됨 / code={safeCode} / screen={safeScreenNo} / reason={safeReason}");

                if (allowDelayedRetry)
                {
                    ScheduleRealtimeRegistrationRetry(
                        safeWindowId,
                        safeCode,
                        safeScreenNo,
                        fids,
                        safeReason,
                        startProgramHealthWatch);
                }

                return -997;
            }

            int result = -999;

            try
            {
                if (forceReset)
                {
                    try
                    {
                        _api.SetRealRemove(safeScreenNo, safeCode);
                    }
                    catch (Exception removeEx)
                    {
                        Debug.WriteLine(
                            $"[실시간등록 복구] 사전 해제 예외 / code={safeCode} / screen={safeScreenNo} / {removeEx.Message}");
                    }

                    result = _api.SetRealReg(safeScreenNo, safeCode, fids, "0");
                }
                else
                {
                    result = _api.SetRealReg(safeScreenNo, safeCode, fids, "1");

                    // 추가등록 실패 시 해당 screen/code를 완전히 비우고 전체 FID 신규등록으로 즉시 복구한다.
                    if (result != 0)
                    {
                        OnLog?.Invoke(
                            safeWindowId,
                            $"[실시간등록 복구] 추가등록 실패 result={result} / code={safeCode} / screen={safeScreenNo} / reason={safeReason}");

                        try
                        {
                            _api.SetRealRemove(safeScreenNo, safeCode);
                        }
                        catch (Exception removeEx)
                        {
                            Debug.WriteLine(
                                $"[실시간등록 복구] 실패 후 해제 예외 / code={safeCode} / screen={safeScreenNo} / {removeEx.Message}");
                        }

                        result = _api.SetRealReg(safeScreenNo, safeCode, fids, "0");
                    }
                }
            }
            catch (System.Runtime.InteropServices.InvalidComObjectException)
            {
                result = -997;
            }
            catch (ObjectDisposedException)
            {
                result = -997;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                result = -997;
            }
            catch (Exception ex)
            {
                result = -998;
                Debug.WriteLine(
                    $"[실시간등록 예외] code={safeCode} / screen={safeScreenNo} / reason={safeReason} / {ex.Message}");
            }

            string registrationKey = BuildRealtimeRegistrationKey(safeScreenNo, safeCode);

            if (result == 0)
            {
                _realtimeRegistrationRetryPending.TryRemove(registrationKey, out _);
                _realtimeRegistrationRetryCount.TryRemove(registrationKey, out _);

                bool pendingProgramHealthWatch = false;
                _realtimeRegistrationProgramHealthWatchRequested.TryRemove(
                    registrationKey,
                    out pendingProgramHealthWatch);

                _unifiedSymbolRealtimeRegistered[safeCode] = true;

                if (startProgramHealthWatch || pendingProgramHealthWatch)
                    StartProgramRealtimeHealthWatch(safeCode);

                Debug.WriteLine(
                    $"[실시간등록 성공] code={safeCode} / screen={safeScreenNo} / reset={(forceReset ? "Y" : "N")} / reason={safeReason}");
                OnLog?.Invoke(
                    safeWindowId,
                    $"[실시간등록 성공] {safeCode} / screen={safeScreenNo} / reset={(forceReset ? "Y" : "N")} / reason={safeReason}");
                return 0;
            }

            _unifiedSymbolRealtimeRegistered.TryRemove(safeCode, out _);

            Debug.WriteLine(
                $"[실시간등록 실패] result={result} / code={safeCode} / screen={safeScreenNo} / reason={safeReason}");
            OnLog?.Invoke(
                safeWindowId,
                $"[실시간등록 실패] result={result} / code={safeCode} / screen={safeScreenNo} / reason={safeReason}");

            if (allowDelayedRetry)
            {
                ScheduleRealtimeRegistrationRetry(
                    safeWindowId,
                    safeCode,
                    safeScreenNo,
                    fids,
                    safeReason,
                    startProgramHealthWatch);
            }

            return result;
        }

        private void ScheduleRealtimeRegistrationRetry(
            string windowId,
            string code,
            string screenNo,
            string fids,
            string reason,
            bool startProgramHealthWatch)
        {
            string key = BuildRealtimeRegistrationKey(screenNo, code);

            if (startProgramHealthWatch)
                _realtimeRegistrationProgramHealthWatchRequested[key] = true;

            if (!_realtimeRegistrationRetryPending.TryAdd(key, true))
                return;

            int attempt = _realtimeRegistrationRetryCount.AddOrUpdate(
                key,
                1,
                (_, current) => current + 1);

            if (attempt > RealtimeRegistrationMaxDelayedRetries)
            {
                _realtimeRegistrationRetryPending.TryRemove(key, out _);
                _realtimeRegistrationRetryCount.TryRemove(key, out _);
                _realtimeRegistrationProgramHealthWatchRequested.TryRemove(key, out _);
                OnLog?.Invoke(
                    string.IsNullOrWhiteSpace(windowId) ? "COMMON" : windowId,
                    $"[실시간등록 재시도 종료] code={code} / screen={screenNo} / attempts={attempt - 1} / reason={reason}");
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(
                        RealtimeRegistrationRetryDelayMilliseconds * attempt)
                        .ConfigureAwait(false);

                    if (!HasRealtimeConsumers(code))
                    {
                        _realtimeRegistrationRetryPending.TryRemove(key, out _);
                        _realtimeRegistrationRetryCount.TryRemove(key, out _);
                        _realtimeRegistrationProgramHealthWatchRequested.TryRemove(key, out _);
                        return;
                    }

                    bool alreadyRegistered;
                    if (_unifiedSymbolRealtimeRegistered.TryGetValue(code, out alreadyRegistered) &&
                        alreadyRegistered)
                    {
                        bool pendingProgramHealthWatch = false;
                        _realtimeRegistrationProgramHealthWatchRequested.TryRemove(
                            key,
                            out pendingProgramHealthWatch);

                        if ((startProgramHealthWatch || pendingProgramHealthWatch) &&
                            HasNormalWindowRealtimeConsumer(code) &&
                            !_fullRealtimeRegisteredAtByCode.ContainsKey(code))
                        {
                            StartProgramRealtimeHealthWatch(code);
                        }

                        _realtimeRegistrationRetryPending.TryRemove(key, out _);
                        _realtimeRegistrationRetryCount.TryRemove(key, out _);
                        return;
                    }

                    var dispatcher = System.Windows.Application.Current == null
                        ? null
                        : System.Windows.Application.Current.Dispatcher;

                    if (dispatcher == null)
                    {
                        _realtimeRegistrationRetryPending.TryRemove(key, out _);
                        _realtimeRegistrationRetryCount.TryRemove(key, out _);
                        _realtimeRegistrationProgramHealthWatchRequested.TryRemove(key, out _);
                        return;
                    }

                    await dispatcher.InvokeAsync(() =>
                    {
                        _realtimeRegistrationRetryPending.TryRemove(key, out _);

                        bool pendingProgramHealthWatch = false;
                        _realtimeRegistrationProgramHealthWatchRequested.TryGetValue(
                            key,
                            out pendingProgramHealthWatch);

                        int retryResult = RegisterRealtimeWithRecovery(
                            windowId,
                            code,
                            screenNo,
                            fids,
                            forceReset: true,
                            reason: string.Format(
                                CultureInfo.InvariantCulture,
                                "{0}/DelayedRetry{1}",
                                reason,
                                attempt),
                            startProgramHealthWatch: startProgramHealthWatch || pendingProgramHealthWatch,
                            allowDelayedRetry: false);

                        if (retryResult != 0)
                        {
                            ScheduleRealtimeRegistrationRetry(
                                windowId,
                                code,
                                screenNo,
                                fids,
                                reason,
                                startProgramHealthWatch);
                        }
                    });
                }
                catch (Exception ex)
                {
                    _realtimeRegistrationRetryPending.TryRemove(key, out _);
                    _realtimeRegistrationRetryCount.TryRemove(key, out _);
                    _realtimeRegistrationProgramHealthWatchRequested.TryRemove(key, out _);
                    OnLog?.Invoke(
                        string.IsNullOrWhiteSpace(windowId) ? "COMMON" : windowId,
                        $"[실시간등록 재시도 예외] code={code} / screen={screenNo} / attempt={attempt} / {ex.Message}");
                }
            });
        }

        private void TryRecoverProgramRealtimeRegistration(string code, DateTime stockTradeTime)
        {
            if (string.IsNullOrWhiteSpace(code) ||
                !IsRegularStockRealtimeTickTime(stockTradeTime))
                return;

            DateTime registeredAt;
            if (!_fullRealtimeRegisteredAtByCode.TryGetValue(code, out registeredAt))
                return;

            if ((stockTradeTime - registeredAt).TotalSeconds < ProgramRealtimeInitialHealthCheckSeconds)
                return;

            bool programRealtimeSeen;
            if (_programRealtimeSeenSinceRegistration.TryGetValue(code, out programRealtimeSeen) &&
                programRealtimeSeen)
                return;

            if (!_programRealtimeHealthRecoveryAttempted.TryAdd(code, true))
                return;

            var target = GetUnifiedRealtimeRepresentative(code);

            if (target == null || string.IsNullOrWhiteSpace(target.ScreenNo))
            {
                _programRealtimeHealthRecoveryAttempted.TryRemove(code, out _);
                return;
            }

            OnLog?.Invoke(
                target.WindowId ?? "COMMON",
                $"[프로그램매매 실시간 복구] 주식체결 정상 + 등록 후 {ProgramRealtimeInitialHealthCheckSeconds}초 동안 Program RealType 미수신 / code={code} / screen={target.ScreenNo}");

            // OnReceiveRealData 콜백 안에서 SetRealRemove/SetRealReg를 직접 호출하지 않는다.
            // 현재 이벤트 처리가 끝난 뒤 UI Dispatcher에서 복구 등록을 실행해 COM 재진입을 피한다.
            var dispatcher = System.Windows.Application.Current == null
                ? null
                : System.Windows.Application.Current.Dispatcher;

            if (dispatcher == null)
            {
                _programRealtimeHealthRecoveryAttempted.TryRemove(code, out _);
                return;
            }

            dispatcher.BeginInvoke(new Action(() =>
            {
                EnsureUnifiedSymbolRealtimeRegistration(
                    target.WindowId ?? "COMMON",
                    code,
                    reason: "ProgramRealTypeInitialHealthCheck",
                    forceReset: true,
                    startProgramHealthWatch: false);
            }), DispatcherPriority.Background);
        }

        public void ReplaceSubscription(string windowId, string screenNo, string newCode)
        {
            if (string.IsNullOrWhiteSpace(windowId) ||
                string.IsNullOrWhiteSpace(newCode))
                return;

            string normalizedCode = newCode.Trim();

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                RealSubscription existing;
                bool hasExisting = _windowSubscriptions.TryGetValue(windowId, out existing);
                bool sameCode = hasExisting &&
                    existing != null &&
                    string.Equals(existing.Code, normalizedCode, StringComparison.OrdinalIgnoreCase);

                string oldCode = hasExisting && existing != null
                    ? existing.Code
                    : string.Empty;

                if (hasExisting && !sameCode)
                {
                    // 실제 키움 해제는 여기서 하지 않는다.
                    // 먼저 이 창 소비자만 제거하고, 해당 종목의 마지막 소비자인지 통합 등록부가 판단한다.
                    _windowSubscriptions.TryRemove(windowId, out _);
                    ReleaseUnifiedSymbolRealtimeIfUnused(oldCode, "ReplaceSubscription/OldCodeDetached");
                }

                string token = GetOrCreateSymbolToken(normalizedCode);

                _windowSubscriptions[windowId] = new RealSubscription
                {
                    WindowId = windowId,
                    Code = normalizedCode,
                    ScreenNo = token
                };

                int result = EnsureUnifiedSymbolRealtimeRegistration(
                    windowId,
                    normalizedCode,
                    sameCode ? "ReplaceSubscription/SameCodeConsumerRefresh" : "ReplaceSubscription/NewConsumer",
                    forceReset: false);

                Debug.WriteLine(
                    $"[통합 실시간 소비자] 일반창 등록 / code={normalizedCode} / window={windowId} / screen={token} / sameCode={(sameCode ? "Y" : "N")} / result={result}");
            });
        }
        private readonly ConcurrentDictionary<string, HashSet<(string ScreenNo, string WindowId)>> _realTimeMap = new ConcurrentDictionary<string, HashSet<(string ScreenNo, string WindowId)>>();

        public void RegisterInventoryTracking(string windowId, string code, string screenNo)
        {
            if (string.IsNullOrWhiteSpace(code))
                return;

            string normalizedCode = code.Trim();
            string token = GetOrCreateSymbolToken(normalizedCode);
            bool firstConsumer = _inventoryRegistered.TryAdd(normalizedCode, true);
            string inventoryKey = $"HOLDING:{normalizedCode}";

            // 잔고 감시도 소비자 정보만 추가한다.
            // 실제 SetRealReg는 EnsureUnifiedSymbolRealtimeRegistration 한 곳에서 전체 FID 58개로만 실행된다.
            _windowSubscriptions[inventoryKey] = new RealSubscription
            {
                WindowId = "COMMON",
                Code = normalizedCode,
                ScreenNo = token
            };

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                int result = EnsureUnifiedSymbolRealtimeRegistration(
                    windowId,
                    normalizedCode,
                    firstConsumer
                        ? "InventoryTracking/NewConsumer"
                        : "InventoryTracking/ConsumerRefresh",
                    forceReset: false);

                OnLog?.Invoke(
                    windowId,
                    $"[잔고감시] {normalizedCode} 통합 실시간 소비자 {(firstConsumer ? "등록" : "갱신")} / token={token} / actualSetRealReg={(result == 0 ? "정상" : "실패")} / result={result}");
            });
        }


        private void Api_OnReceiveMsg(object sender, _DKHOpenAPIEvents_OnReceiveMsgEvent e)
        {
            string msg = e == null ? string.Empty : (e.sMsg ?? string.Empty);
            ApplyMarketSafetyMessage(msg);
        }

        private void ApplyMarketSafetyMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            string msg = message.Trim();
            string compact = msg.Replace(" ", string.Empty);
            DateTime now = DateTime.Now;

            if (msg.Contains("서킷브레이커") ||
                compact.Contains("CB1") ||
                compact.Contains("CB2") ||
                compact.Contains("CB3") ||
                msg.Contains("CB 1") ||
                msg.Contains("CB 2") ||
                msg.Contains("CB 3") ||
                msg.Contains("매매거래 중단") ||
                msg.Contains("거래가 중단") ||
                msg.Contains("거래 중단"))
            {
                SetMarketSafetyState(
                    MarketSafetyState.CircuitBreaker,
                    "KIWOOM_MSG",
                    msg,
                    now.AddMinutes(MarketSafetyCircuitBreakerBlockMinutes));
                return;
            }

            if ((msg.Contains("거래") || msg.Contains("매매")) && msg.Contains("재개"))
            {
                SetMarketSafetyState(
                    MarketSafetyState.RecoveryCooldown,
                    "KIWOOM_MSG_RESUME",
                    msg,
                    now.AddMinutes(MarketSafetyRecoveryCooldownMinutes));
            }
        }

        private static bool IsBuyOrderTypeForMarketSafety(int orderType)
        {
            return orderType == 1 || orderType == 5;
        }

        public bool IsBuyBlockedByMarketSafety(out string reason)
        {
            reason = null;
            EvaluateMarketSafetyForOrderGate();

            if (CurrentMarketSafetyState == MarketSafetyState.Normal)
                return false;

            reason = BuildMarketSafetyBlockReason();
            return true;
        }

        private string BuildMarketSafetyBlockReason()
        {
            string state = CurrentMarketSafetyState.ToString();
            string reason = string.IsNullOrWhiteSpace(CurrentMarketSafetyReason)
                ? state
                : CurrentMarketSafetyReason;

            if (_marketSafetyBlockBuyUntil > DateTime.Now)
                return $"시장안전차단({state}) / {_marketSafetyBlockBuyUntil:HH:mm:ss}까지 신규매수 차단 / {reason}";

            return $"시장안전차단({state}) / {reason}";
        }

        private void EvaluateMarketSafetyForOrderGate()
        {
            DateTime now = DateTime.Now;
            ResetMarketSafetyAtRegularOpenIfNeeded(now);
            RefreshMarketSafetyStateByTime(now);

            if (CurrentMarketSafetyState != MarketSafetyState.Normal)
                return;

            if (!IsMarketOpenForSafety(now))
                return;

            if (!IsIndexCrashForCircuitBreakerSuspicion())
                return;

            if (!IsStockTickStarved(now))
                return;

            SetMarketSafetyState(
                MarketSafetyState.HaltSuspected,
                "TICK_STARVATION",
                $"장중 다종목 틱 공백 {MarketSafetyTickStopSeconds}초 이상 + 지수 급락(KOSPI={_latestKospiChangeRate:F2}%, KOSDAQ={_latestKosdaqChangeRate:F2}%)",
                now.AddMinutes(MarketSafetyRecoveryCooldownMinutes));
        }

        private void ResetMarketSafetyAtRegularOpenIfNeeded(DateTime now)
        {
            if (now.TimeOfDay < RegularStockRealtimeStartTime)
                return;

            if (_lastMarketSafetyRegularOpenResetDate == now.Date)
                return;

            _lastMarketSafetyRegularOpenResetDate = now.Date;

            _lastStockTickReceivedAt.Clear();
            _lastAnyStockTickReceivedAt = DateTime.MinValue;
            _marketSafetyBlockBuyUntil = DateTime.MinValue;

            SetMarketSafetyState(
                MarketSafetyState.Normal,
                "REGULAR_OPEN_RESET",
                "09:00 정규장 시작 - 장전 예상체결/단일가 상태 초기화",
                DateTime.MinValue);
        }

        private void RefreshMarketSafetyStateByTime(DateTime now)
        {
            if (CurrentMarketSafetyState == MarketSafetyState.Normal)
                return;

            if (_marketSafetyBlockBuyUntil != DateTime.MinValue && now < _marketSafetyBlockBuyUntil)
                return;

            SetMarketSafetyState(
                MarketSafetyState.Normal,
                "TIMEOUT",
                "시장안전 차단 시간 경과",
                DateTime.MinValue);
        }

        private static bool IsMarketOpenForSafety(DateTime now)
        {
            TimeSpan tod = now.TimeOfDay;
            return tod >= new TimeSpan(9, 0, 0) && tod <= new TimeSpan(15, 30, 0);
        }

        private bool IsIndexCrashForCircuitBreakerSuspicion()
        {
            return _latestKospiChangeRate <= -7.5 || _latestKosdaqChangeRate <= -7.5;
        }

        private bool IsStockTickStarved(DateTime now)
        {
            var monitoredCodes = _windowSubscriptions.Values
                .Where(x => x != null && IsNormalStockCode(x.Code))
                .Select(x => x.Code)
                .Distinct()
                .ToList();

            if (monitoredCodes.Count < 3)
                return false;

            int silentCount = 0;

            foreach (var code in monitoredCodes)
            {
                DateTime last;
                if (!_lastStockTickReceivedAt.TryGetValue(code, out last) ||
                    (now - last).TotalSeconds >= MarketSafetyTickStopSeconds)
                {
                    silentCount++;
                }
            }

            return silentCount >= 3 &&
                   (_lastAnyStockTickReceivedAt == DateTime.MinValue ||
                    (now - _lastAnyStockTickReceivedAt).TotalSeconds >= MarketSafetyTickStopSeconds);
        }

        private bool IsNormalStockCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return false;

            code = code.Trim();
            return code.Length == 6 && !IsMarketIndexCode(code);
        }

        private void ObserveMarketIndexForSafety(MarketIndexInfo index)
        {
            if (index == null)
                return;

            if (index.Code == "001")
                _latestKospiChangeRate = index.ChangeRate;
            else if (index.Code == "101")
                _latestKosdaqChangeRate = index.ChangeRate;

            DateTime now = DateTime.Now;
            ResetMarketSafetyAtRegularOpenIfNeeded(now);

            // 장전 지수/예상체결 데이터가 서킷브레이커 방어엔진을 오염시키면
            // 09:00 이후 정상 Buy 신호가 RecoveryCooldown에 막힐 수 있다.
            if (!IsMarketOpenForSafety(now))
                return;

            EvaluateMarketSafetyForOrderGate();
        }

        private void ObserveStockTickForSafety(string code, DateTime tradeTime)
        {
            if (string.IsNullOrWhiteSpace(code))
                return;

            if (!IsRegularStockRealtimeTickTime(tradeTime))
                return;

            ResetMarketSafetyAtRegularOpenIfNeeded(tradeTime);

            DateTime now = tradeTime;
            _lastAnyStockTickReceivedAt = now;
            _lastStockTickReceivedAt[code.Trim()] = now;

            if (CurrentMarketSafetyState == MarketSafetyState.CircuitBreaker ||
                CurrentMarketSafetyState == MarketSafetyState.HaltSuspected)
            {
                SetMarketSafetyState(
                    MarketSafetyState.RecoveryCooldown,
                    "TICK_RESUME",
                    $"시장중단 이후 정규장 체결 재개 감지: {code}",
                    now.AddMinutes(MarketSafetyRecoveryCooldownMinutes));
                return;
            }

            if (CurrentMarketSafetyState == MarketSafetyState.RecoveryCooldown)
                RefreshMarketSafetyStateByTime(now);
        }

        private void ObserveExpectedAuctionForSafety(string code)
        {
            DateTime now = DateTime.Now;
            ResetMarketSafetyAtRegularOpenIfNeeded(now);
            RefreshMarketSafetyStateByTime(now);

            // 예상체결은 장전/단일가성 데이터라서 신규매수 차단용 buyBlockUntil을 만들면 안 된다.
            // 실제 CircuitBreaker/Halt 판단은 키움 메시지 또는 09:00 이후 정규장 실체결 tick으로만 한다.
            if (!IsMarketOpenForSafety(now))
            {
                LogExpectedAuction(code, "장중 아님 - 시장안전 상태 전환 없음");
                return;
            }

            LogExpectedAuction(code, "예상체결 수신 - 시장안전 상태 전환 없음");
        }

        private void LogExpectedAuction(string code, string detail)
        {
            DateTime now = DateTime.Now;

            if ((now - _lastExpectedAuctionLogAt).TotalSeconds < 30)
                return;

            _lastExpectedAuctionLogAt = now;

            OnLog?.Invoke(
                "COMMON",
                $"[예상체결] {code} / {detail}");
        }

        private void SetMarketSafetyState(
            MarketSafetyState state,
            string source,
            string reason,
            DateTime blockBuyUntil)
        {
            bool shouldLog;
            string logMessage;

            lock (_marketSafetyLock)
            {
                bool changed = CurrentMarketSafetyState != state ||
                               !string.Equals(CurrentMarketSafetyReason, reason, StringComparison.Ordinal);

                CurrentMarketSafetyState = state;
                CurrentMarketSafetyReason = reason ?? string.Empty;
                _marketSafetyBlockBuyUntil = blockBuyUntil;
                _marketSafetyUpdatedAt = DateTime.Now;

                shouldLog = changed || (DateTime.Now - _lastMarketSafetyLogAt).TotalSeconds >= 30;
                if (shouldLog)
                    _lastMarketSafetyLogAt = DateTime.Now;

                logMessage = state == MarketSafetyState.Normal
                    ? $"[시장안전] Normal 복귀 / source={source} / {CurrentMarketSafetyReason}"
                    : $"[시장안전] {state} / source={source} / buyBlockUntil={(_marketSafetyBlockBuyUntil == DateTime.MinValue ? "-" : _marketSafetyBlockBuyUntil.ToString("HH:mm:ss"))} / {CurrentMarketSafetyReason}";
            }

            if (shouldLog)
                OnLog?.Invoke("COMMON", logMessage);
        }

        public int SendOrder(string windowId, string screenNo, string accountNo, int orderType, string code, int qty, int price, string hogaType, string orgOrderNo = "")
        {
            if (_api == null)
                return -100;

            if (string.IsNullOrWhiteSpace(windowId) ||
                string.IsNullOrWhiteSpace(screenNo) ||
                string.IsNullOrWhiteSpace(accountNo) ||
                string.IsNullOrWhiteSpace(code))
            {
                OnLog?.Invoke(windowId, $"⛔ 주문 전송 차단: 필수값 누락 / code={code}");
                return -101;
            }

            if (qty <= 0)
            {
                OnLog?.Invoke(windowId, $"⛔ 주문 전송 차단: 수량 오류 / {code} / qty={qty}");
                return -102;
            }

            if ((orderType == 1 || orderType == 2 || orderType == 5 || orderType == 6) && hogaType == "00" && price <= 0)
            {
                OnLog?.Invoke(windowId, $"⛔ 주문 전송 차단: 지정가 가격 오류 / {code} / price={price}");
                return -103;
            }

            if (IsBuyOrderTypeForMarketSafety(orderType))
            {
                string marketSafetyReason;
                if (IsBuyBlockedByMarketSafety(out marketSafetyReason))
                {
                    OnLog?.Invoke(windowId, $"⛔ 주문 전송 차단: 시장안전 / {code} / {marketSafetyReason}");
                    return -180;
                }
            }

            int result = _api.SendOrder(
                $"주문_{windowId}",
                screenNo,
                accountNo,
                orderType,
                code,
                qty,
                price,
                hogaType,
                orgOrderNo ?? "");

            if (result == 0)
            {
                string routeKey = BuildOrderRouteKey(code, orderType);
                _orderWindowMap[routeKey] = windowId;

                OnLog?.Invoke(windowId, $"✈️ 주문 전송 성공: {code} / type={orderType} / {qty}주 / {price}원");
            }
            else
            {
                OnLog?.Invoke(windowId, $"❌ 주문 전송 실패: {code} / 에러코드 {result}");
            }

            return result;
        }

        public void CheckMarketStatus()
        {
            var now = DateTime.Now.TimeOfDay;
            if (now >= new TimeSpan(9, 0, 0) && now <= new TimeSpan(15, 30, 0))
                CurrentMarketStatus = MarketStatus.MarketOpen;
            else
                CurrentMarketStatus = MarketStatus.AfterMarket;
        }

        private DateTime ParseRealTradeTime(string rawTradeTime)
        {
            DateTime parsedTime = DateTime.ParseExact(
                rawTradeTime,
                "HHmmss",
                CultureInfo.InvariantCulture);

            return DateTime.Today
                .AddHours(parsedTime.Hour)
                .AddMinutes(parsedTime.Minute)
                .AddSeconds(parsedTime.Second);
        }

        private static bool IsRegularStockRealtimeTickTime(DateTime tradeTime)
        {
            if (tradeTime == default(DateTime))
                return false;

            TimeSpan tod = tradeTime.TimeOfDay;
            return tod >= RegularStockRealtimeStartTime && tod < RegularStockRealtimeEndTime;
        }

        private static bool IsBeforeRegularStockRealtimeOpen(DateTime now)
        {
            return now.TimeOfDay < RegularStockRealtimeStartTime;
        }

        private void Api_OnReceiveRealData(object sender, _DKHOpenAPIEvents_OnReceiveRealDataEvent e)
        {
            if (DateTime.Now.TimeOfDay >= new TimeSpan(15, 20, 0))
                return;

            string code = e.sRealKey.Trim();

            if (IsMarketIndexCode(code) && e.sRealType.Contains("업종"))
            {
                string rawPrice = _api.GetCommRealData(code, 10).Trim();
                string rawChange = _api.GetCommRealData(code, 11).Trim();
                string rawRate = _api.GetCommRealData(code, 12).Trim();
                string rawVolume = _api.GetCommRealData(code, 13).Trim();
                string rawTime = _api.GetCommRealData(code, 20).Trim();

                var index = new MarketIndexInfo
                {
                    Code = code,
                    Name = GetMarketIndexName(code),
                    Price = SafeSignedDouble(rawPrice),
                    Change = SafeSignedDouble(rawChange),
                    ChangeRate = SafeSignedDouble(rawRate),
                    Volume = Math.Abs(SafeSignedLong(rawVolume)),
                    UpdatedAt = DateTime.Now
                };

                ObserveMarketIndexForSafety(index);
                OnMarketIndexReceived?.Invoke(index);
                return;
            }

            if (e.sRealType == "종목프로그램매매")
            {
                MarkProgramRealtimeReceived(code);
                ParseAndSendProgramTrade(windowId: "COMMON", code: code, source: "REALTIME");
                return;
            }

            if (e.sRealType == "주식체결" || e.sRealType == "주식단일가체결")
            {
                string rawPrice = _api.GetCommRealData(code, 10).Trim();
                string rawVol = _api.GetCommRealData(code, 15).Trim();
                string rawAccVol = _api.GetCommRealData(code, 13).Trim();
                string rawStrength = _api.GetCommRealData(code, 228).Trim();
                string rawTradeTime = _api.GetCommRealData(code, 20).Trim();

                if (!double.TryParse(rawPrice.Replace("+", "").Replace("-", ""), out double priceStr) ||
                    !int.TryParse(rawVol, out int volume))
                    return;

                DateTime tradeTime = ParseRealTradeTime(rawTradeTime);

                // 08:30 예상체결/단일가성 1틱이 실시간 틱/자동매매/시장안전 상태를 오염시키면 안 된다.
                // 정규장 실시간 체결 tick은 09:00:00부터만 하위 데이터 파이프라인으로 전달한다.
                if (!IsRegularStockRealtimeTickTime(tradeTime))
                    return;

                double strength = double.TryParse(rawStrength, out double st) ? st : 0;
                long accVol = long.TryParse(rawAccVol, out long av) ? av : 0;

                long totalaskqty = 0;
                long totalbidqty = 0;

                if (TryGetLatestHoga(code, out var hoga))
                {
                    totalaskqty = hoga.TotalAskQty;
                    totalbidqty = hoga.TotalBidQty;
                }

                string side = volume > 0 ? "B" : "S";

                var currentTick = new TrackedSymbol
                {
                    Code = code,
                    LastPrice = priceStr,
                    LastVolume = Math.Abs(volume),
                    AccumulatedVolume = accVol,
                    Strength = strength,
                    LastUpdated = tradeTime,
                    Side = side,
                    TotalAskQty = totalaskqty,
                    TotalBidQty = totalbidqty,
                    ClosePrice = priceStr,
                    HighPrice = priceStr,
                    LowPrice = priceStr
                };

                ObserveStockTickForSafety(code, tradeTime);
                TryRecoverProgramRealtimeRegistration(code, tradeTime);

                _tickQueue.Enqueue(new TickData
                {
                    Code = code,
                    Price = (int)priceStr,
                    Volume = Math.Abs(volume),
                    Strength = strength,
                    AskVolume = totalaskqty,
                    BidVolume = totalbidqty,
                    Side = side,
                    CreatedAt = tradeTime
                });

                UpdateTradeData(currentTick);

                foreach (var pair in _windowSubscriptions)
                {
                    var sub = pair.Value;

                    if (sub.Code != code)
                        continue;

                    if (sub.WindowId == "COMMON" || sub.ScreenNo == "9999")
                    {
                        OnRealPriceReceived?.Invoke(
                            code,
                            sub.WindowId,
                            priceStr,
                            Math.Abs(volume),
                            tradeTime
                        );

                        OnInventoryTickReceived?.Invoke(currentTick);
                    }
                    else
                    {
                        OnTickReceived?.Invoke(sub.WindowId, currentTick);
                    }
                }
            }
            else if (e.sRealType == "주식호가잔량")
            {
                double lastPrice = _trackedSymbols.TryGetValue(code, out var tracked)
                    ? tracked.LastPrice
                    : 0;

                bool savedHogaToDb = false;

                foreach (var pair in _windowSubscriptions)
                {
                    var sub = pair.Value;

                    if (sub.Code != code || sub.ScreenNo == "9999")
                        continue;

                    ParseAndSendHoga(sub.WindowId, code, lastPrice, !savedHogaToDb);
                    savedHogaToDb = true;
                }
            }
            if (e.sRealType == "주식예상체결")
            {
                if (IsBeforeRegularStockRealtimeOpen(DateTime.Now))
                {
                    LogExpectedAuction(code, "09:00 전 예상체결 무시 - 시장안전/실시간틱 반영 없음");
                    return;
                }

                ObserveExpectedAuctionForSafety(code);
                return;
            }
        }

        // --- [수정] 외부에서 요청 시 스케줄러 큐에 삽입 ---
        public void RequestPreviousData(string windowId, string code, int nPrevNext = 0, string screenNo = "1000", int priority = 1)
        {
            if (string.IsNullOrEmpty(code)) return;

            string token = GetOrCreateSymbolToken(code);

            _scheduler.Enqueue(
            new TrRequest
            {
                WindowId = windowId,
                Code = code,
                Next = nPrevNext,
                ScreenNo = token,
                Priority = priority,
                RequestType = "PREV",
                Inputs = new Dictionary<string, string>
                    {
                        { "종목코드", code },
                        { "틱단위", "1" },
                        { "수정주가구분", "1" }
                    }
            });
        }

        public void RequestStockDetail(string windowId, string code, string screenNo, int priority = 1)
        {
            if (string.IsNullOrEmpty(code)) return;

            string token = GetOrCreateSymbolToken(code);

            _scheduler.Enqueue(
              new TrRequest
              {
                  WindowId = windowId,
                  Code = code,
                  Next = 0,
                  ScreenNo = token,
                  Priority = priority,
                  RequestType = "DETAIL"
              });
        }
        public void RequestAccountHoldings(string windowId, string accountNo, string screenNo, int priority = 1)
        {
            if (string.IsNullOrEmpty(accountNo)) return;
            _scheduler.Enqueue(
               new TrRequest
               {
                   WindowId = windowId,
                   Code = accountNo, // 계좌번호를 Code 필드에 담아서 전달
                   Next = 0,
                   ScreenNo = screenNo,
                   Priority = priority, // 최우선으로 처리
                   RequestType = "HOLDINGS",
                   Inputs = new Dictionary<string, string>
                    {
                        { "계좌번호", accountNo },
                        { "비밀번호", "" },
                        { "비밀번호입력매체구분", "00" },
                        { "조회구분", "1" }
                    }
               }
           );
        }
        public void RequestCashInfo(string windowId, string accountNo, string screenNo = "1000")
        {
            if (string.IsNullOrEmpty(accountNo)) return;
            _scheduler.Enqueue(new TrRequest
            {
                WindowId = windowId,
                Code = accountNo, // 계좌번호를 Code 필드에 담아서 전달
                ScreenNo = screenNo,
                RequestType = "CASH", // 예수금 전용 타입
                Priority = 1, // 중간 혹은 높은 우선순위
                Inputs = new Dictionary<string, string>
                {
                    { "계좌번호", accountNo },
                    { "비밀번호", "" }, // 비밀번호는 공백 가능 (설정에 따라 다름)
                    { "비밀번호분류", "00" },
                    { "조회구분", "2" } // 2: 일반예수금
                }
            });
        }
        public void RequestOrderHistory(string windowId, string accountNo, string screenNo, int next = 0)
        {
            string orderKey = $"{accountNo}_ORDERS";

            if (next == 0)
            {
                DateTime now = DateTime.Now;

                if (_lastOrderHistoryRequestAt.TryGetValue(orderKey, out var last) &&
                    (now - last).TotalMilliseconds < 3000)
                {
                    OnLog?.Invoke(windowId, $"[주문내역] 중복 요청 차단: {accountNo}");
                    return;
                }

                _lastOrderHistoryRequestAt[orderKey] = now;
            }

            if (string.IsNullOrWhiteSpace(accountNo))
                return;

            string trScreenNo = next == 0 ? GetScreenNo() : screenNo;

            _scheduler.Enqueue(
                new TrRequest
                {
                    WindowId = windowId,
                    Code = accountNo,
                    ScreenNo = trScreenNo,
                    Next = next,
                    Priority = 2,
                    RequestType = "ORDERS",
                    Inputs = new Dictionary<string, string>
                    {
                { "주문일자", DateTime.Now.ToString("yyyyMMdd") },
                { "계좌번호", accountNo },
                { "비밀번호", "" },
                { "비밀번호입력매체구분", "00" },
                { "조회구분", "1" },
                { "주식채권구분", "0" },
                { "매도수구분", "0" },
                { "종목코드", "" },
                { "시작주문번호", "" },
                { "거래소구분", "" }
                    }
                }
            );
        }
        public void RequestUnfilledOrders(string windowId, string accountNo, string screenNo)
        {
            _scheduler.Enqueue(
                new TrRequest
                {
                    WindowId = windowId,
                    Code = accountNo,
                    ScreenNo = screenNo,
                    Priority = 0,
                    RequestType = "UNFILLED",
                    Inputs = new Dictionary<string, string>
                    {
                        { "계좌번호", accountNo },
                        { "전체종목구분", "0" },
                        { "매매구분", "0" },
                        { "체결구분", "1" } // 미체결만
                    }
                }
            );
        }


        private static readonly SemaphoreSlim _apiSemaphore = new SemaphoreSlim(1, 1);
        private const int TrContinuousRequestDelayMilliseconds = 300;

        private static Task DelayBeforeContinuousTrRequestAsync()
        {
            return Task.Delay(TrContinuousRequestDelayMilliseconds);
        }


        // ============================================================
        // [추가] 과거 1분봉 요청
        // daysToLoad: 전일+당일이면 2 권장
        // maxRows: 0이면 날짜 기준까지 전체, 10이면 최신 10개만 가져오고 다음 페이지 중단
        // ============================================================
        public void RequestMinuteCandles(
            string windowId,
            string code,
            string screenNo,
            int daysToLoad = 2,
            int maxRows = 0,
            int priority = 1,
            int next = 0)
        {
            if (string.IsNullOrWhiteSpace(code))
                return;

            // 종목별 고정 token을 ScreenNo로 사용한다.
            // next=2 연속조회도 같은 token으로 이어받아야 응답 식별이 안정적이다.
            string token = GetOrCreateSymbolToken(code);

            if (next == 0)
            {
                string cycleKey = $"{windowId}_{code}_MINUTE";
                _minuteRequestCycleMap.TryRemove(cycleKey, out _);
            }

            _scheduler.Enqueue(new TrRequest
            {
                WindowId = windowId,
                Code = code,
                Next = next,
                ScreenNo = token,
                Priority = priority,
                RequestType = "MINUTE",
                Inputs = new Dictionary<string, string>
                {
                    { "종목코드", code },
                    { "틱범위", "1" },
                    { "수정주가구분", "1" },
                    { "__DAYS__", daysToLoad.ToString() }
                }
            });
        }

        // ============================================================
        // [추가] 프로그램 시작 시 최근 N분 RawTick 보강 요청
        // opt10079 기반. 현재 시각 - minutes 까지만 가져오고 중단.
        // ============================================================
        // [수정] 최근 틱 요청 minutes 기본값 10으로 변경
        // 외부 호출은 항상 최초 조회(next=0)로 시작한다.
        public void RequestRecentTicks(string windowId, string code, string screenNo, int minutes = 10, int priority = 1)
        {
            RequestRecentTicksCore(windowId, code, screenNo, minutes, priority, next: 0);
        }

        // 최근틱 전용 내부 요청.
        // 중요: opt10079 연속조회는 next=2를 넘겨야 다음 페이지를 가져온다.
        // 기존 구조처럼 RequestRecentTicks(..., priority:2)만 호출하면 Next가 계속 0이라
        // 데이터 많은 종목에서 첫 페이지를 반복 요청하며 분봉 2차 조회를 밀어버릴 수 있다.
        private void RequestRecentTicksCore(string windowId, string code, string screenNo, int minutes, int priority, int next)
        {
            if (string.IsNullOrWhiteSpace(code)) return;

            // 최근틱(opt10079)도 해당 종목 token을 사용한다.
            string token = GetOrCreateSymbolToken(code);

            _scheduler.Enqueue(new TrRequest
            {
                WindowId = windowId,
                Code = code,
                Next = next,
                ScreenNo = token,
                Priority = priority,
                RequestType = "RECENT_TICKS",
                Inputs = new Dictionary<string, string>
                    {
                        { "종목코드", code },
                        { "틱단위", "1" },
                        { "수정주가구분", "1" },
                        { "__MINUTES__", minutes.ToString() }
                    }
            });
        }


        public void RequestProgramTradeHistory(string windowId, string code, string screenNo, int minutes = ProgramTradeDefaultSeedMinutes, int priority = 1, int next = 0)
        {
            if (string.IsNullOrWhiteSpace(code))
                return;

            // 15:20 이후에도 TR 히스토리는 요청한다.
            // 노이즈 컷은 요청 차단이 아니라 ProcessProgramTradeHistoryData에서 시간 필터로 처리한다.
            string token = GetOrCreateSymbolToken(code);
            int safeMinutes = Math.Max(1, minutes);

            if (next == 0)
            {
                string cycleKey = $"{windowId}_{code}_PROGRAM";
                _programTradeRequestCycleMap.TryRemove(cycleKey, out _);
            }

            _scheduler.Enqueue(new TrRequest
            {
                WindowId = windowId,
                Code = code,
                Next = next,
                ScreenNo = token,
                Priority = priority,
                RequestType = "PROGRAM_TRADE",
                Inputs = new Dictionary<string, string>
                {
                    { "시간일자구분", "1" },
                    { "금액수량구분", "1" },
                    { "종목코드", code },
                    { "날짜", DateTime.Now.ToString("yyyyMMdd") },
                    { "__MINUTES__", safeMinutes.ToString() }
                }
            });

            OnLog?.Invoke(windowId, $"[프로그램매매] 히스토리 요청 등록: {code} / minutes={safeMinutes} / next={next} / token={token}");
        }

        private static readonly TimeSpan AnalysisMinuteStartTime = new TimeSpan(9, 0, 0);
        private static readonly TimeSpan AnalysisMinuteEndTime = new TimeSpan(15, 20, 59);
        private static readonly TimeSpan AnalysisCloseTickStartTime = new TimeSpan(15, 15, 0);
        private static readonly TimeSpan AnalysisCloseTickEndTime = new TimeSpan(15, 20, 59);
        private static readonly TimeSpan ProgramTradeStartTime = new TimeSpan(9, 0, 0);
        private static readonly TimeSpan ProgramTradeEndTime = new TimeSpan(15, 20, 0);
        private const int ProgramTradeDefaultSeedMinutes = 30;
        private const int ProgramTradeUiWarmupMinutes = 10;

        // KOA Studio RealType: 종목프로그램매매 기준 FID.
        // 값이 0으로만 들어오면 KOA Studio의 실시간 탭에서 FID를 먼저 확인해야 한다.
        private const int FidProgramTime = 20;
        private const int FidProgramCurrentPrice = 10;
        private const int FidProgramVolume = 13;
        private const int FidProgramSellAmount = 202;
        private const int FidProgramBuyAmount = 204;
        private const int FidProgramNetAmount = 206;
        private const int FidProgramNetAmountDelta = 208;
        private const int FidProgramSellQuantity = 210;
        private const int FidProgramBuyQuantity = 211;
        private const int FidProgramNetQuantity = 212;
        private const int FidProgramNetQuantityDelta = 213;

        private static bool IsAnalysisMinuteTime(DateTime time)
        {
            if (time == DateTime.MinValue)
                return false;

            TimeSpan tod = time.TimeOfDay;
            return tod >= AnalysisMinuteStartTime && tod <= AnalysisMinuteEndTime;
        }

        private DateTime ResolveAnalysisCloseTickDate(DateTime now)
        {
            DateTime date = now == DateTime.MinValue ? DateTime.Today : now.Date;

            // 장중 또는 15:20:59 이전에는 아직 당일 종가 구간이 완성되지 않았으므로
            // 분석용 과거틱 seed는 직전 거래일 15:15~15:20 구간을 사용한다.
            if (now.TimeOfDay <= AnalysisCloseTickEndTime)
                return GetPreviousTradingDay(date);

            return date;
        }

        // 분석용 최근틱 seed는 현재시각 기준이 아니라 15:20 종가 구간 -5분으로 통일한다.
        // Replay 원천 수집용 음수 날짜 요청은 하루 재생을 위해 09:00~15:20:59 전체를 유지한다.
        private TickWindowRange GetRecentTickWindow(DateTime now, int minutes)
        {
            if (TryResolveReplayTickDateRequest(minutes, out var replayTradingDate))
                return GetReplayFullDayTickWindow(replayTradingDate);

            DateTime seedDate = ResolveAnalysisCloseTickDate(now);

            return new TickWindowRange
            {
                Start = seedDate.Date.Add(AnalysisCloseTickStartTime),
                End = seedDate.Date.Add(AnalysisCloseTickEndTime),
                IsPreviousCloseWindow = seedDate.Date != now.Date,
                UseRangeDate = false
            };
        }

        private static bool TryResolveReplayTickDateRequest(int minutes, out DateTime tradingDate)
        {
            tradingDate = DateTime.MinValue;

            if (minutes >= 0)
                return false;

            int rawDate = Math.Abs(minutes);
            if (!DateTime.TryParseExact(rawDate.ToString(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out tradingDate))
                return false;

            return true;
        }

        private static TickWindowRange GetReplayFullDayTickWindow(DateTime tradingDate)
        {
            DateTime date = tradingDate == DateTime.MinValue ? DateTime.Today : tradingDate.Date;

            return new TickWindowRange
            {
                Start = date.Add(AnalysisMinuteStartTime),
                End = date.Add(AnalysisMinuteEndTime),
                IsPreviousCloseWindow = false,
                UseRangeDate = true
            };
        }

        private DateTime GetPreviousTradingDay(DateTime date)
        {
            DateTime d = date.AddDays(-1);

            while (d.DayOfWeek == DayOfWeek.Saturday || d.DayOfWeek == DayOfWeek.Sunday)
                d = d.AddDays(-1);

            return d;
        }

        private sealed class TickWindowRange
        {
            public DateTime Start { get; set; }
            public DateTime End { get; set; }
            public bool IsPreviousCloseWindow { get; set; }
            public bool UseRangeDate { get; set; }
        }


        // [추가] 최근틱 전용 시간 파싱
        // 기존 TryParseData는 HHmmss를 무조건 오늘 날짜로 붙이기 때문에
        // 장초 전일 15:11~15:20 데이터를 제대로 해석하기 어렵다.
        private bool TryParseRecentTickRow(
            string[] row,
            TickWindowRange range,
            DateTime now,
            out DateTime tickTime,
            out double price,
            out int volume)
        {
            tickTime = default;
            price = 0;
            volume = 0;

            if (row == null || row.Length < 3)
                return false;

            string rawPrice = row[0];
            string rawVol = row[1];
            string rawTime = row[2];

            if (string.IsNullOrWhiteSpace(rawTime))
                return false;

            rawTime = rawTime.Trim();

            if (rawTime.Length == 14)
            {
                if (!DateTime.TryParseExact(rawTime, "yyyyMMddHHmmss", null, DateTimeStyles.None, out tickTime))
                    return false;
            }
            else if (rawTime.Length == 6)
            {
                if (!TimeSpan.TryParseExact(rawTime, "hhmmss", CultureInfo.InvariantCulture, out var tod))
                    return false;

                DateTime date;

                if (range.IsPreviousCloseWindow)
                {
                    // 장초 보강 모드:
                    // opt10079는 장 시작 직후에도 오늘 09:00 틱과 전일 15:xx 틱이 섞여 내려올 수 있다.
                    // 15:11~15:20 범위에 해당하는 HHmmss만 전일 날짜로 붙이고,
                    // 오늘 장초 HHmmss는 오늘 날짜로 붙여서 아래 range 필터에서 자연스럽게 제외한다.
                    if (tod >= range.Start.TimeOfDay && tod <= range.End.TimeOfDay)
                        date = range.Start.Date;
                    else
                        date = now.Date;
                }
                else if (range.UseRangeDate)
                {
                    date = range.Start.Date;
                }
                else
                {
                    date = now.Date;
                }

                tickTime = date.Add(tod);
            }
            else
            {
                if (!DateTime.TryParse(rawTime, out tickTime))
                    return false;
            }

            if (!double.TryParse(rawPrice.Replace("+", "").Replace("-", "").Replace(",", "").Trim(), out price))
                return false;

            if (!int.TryParse(rawVol.Replace("+", "").Replace(",", "").Trim(), out volume))
                return false;

            price = Math.Abs(price);
            volume = Math.Abs(volume);
            return true;
        }


        // --- [추가] 스케줄러에 의해 호출되는 실제 키움 API 실행부 ---
        private async Task ExecuteTr(TrRequest req)
        {
            await _apiSemaphore.WaitAsync();
            Interlocked.Increment(ref _activeTrCount);

            int requestResult = -999;
            string requestTrCode = string.Empty;
            string requestType = req == null ? string.Empty : (req.RequestType ?? string.Empty);

            try
            {
                if (req == null)
                {
                    OnLog?.Invoke("SYSTEM", "❌ TR 요청 실패: 요청 객체가 없습니다.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(req.ScreenNo))
                {
                    OnLog?.Invoke(req.WindowId ?? "SYSTEM", "❌ TR 요청 실패: ScreenNo가 비어있습니다.");
                    _scheduler?.NotifyResponseFailed(req.WindowId, req.Code, req.RequestType, -101);
                    return;
                }

                var dispatcher = System.Windows.Application.Current == null
                    ? null
                    : System.Windows.Application.Current.Dispatcher;

                if (dispatcher == null)
                {
                    OnLog?.Invoke(req.WindowId ?? "SYSTEM", "❌ TR 요청 실패: WPF Dispatcher가 없습니다.");
                    _scheduler?.NotifyResponseFailed(req.WindowId, req.Code, req.RequestType, -996);
                    return;
                }

                await dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        if (_api == null || _api.IsDisposed || !_api.IsHandleCreated)
                        {
                            requestResult = -997;
                            return;
                        }

                        if (req.Inputs != null)
                        {
                            foreach (var kv in req.Inputs)
                            {
                                if (kv.Key.StartsWith("__"))
                                    continue;

                                _api.SetInputValue(kv.Key, kv.Value);
                            }
                        }

                        string rqName = string.Empty;
                        string trCode = string.Empty;

                        switch (req.RequestType)
                        {
                            case "HOLDINGS":
                                rqName = $"보유종목조회_{req.Code}_{req.WindowId}_{req.ScreenNo}";
                                trCode = "opw00018";
                                break;

                            case "PREV":
                                rqName = $"과거데이터_{req.Code}_{req.WindowId}_{req.ScreenNo}_{req.Token}";
                                trCode = "opt10079";
                                break;

                            case "DETAIL":
                                rqName = $"상세조회_{req.Code}_{req.WindowId}_{req.ScreenNo}_{req.Token}";
                                trCode = "opt10001";
                                break;

                            case "ORDERS":
                                rqName = $"주문내역조회_{req.Code}_{req.WindowId}_{req.ScreenNo}_{req.Next}";
                                trCode = "opw00007";
                                break;

                            case "CASH":
                                rqName = $"계좌잔고조회_{req.Code}_{req.WindowId}_{req.ScreenNo}";
                                trCode = "opw00001";
                                break;

                            case "UNFILLED":
                                rqName = $"미체결조회_{req.Code}_{req.WindowId}_{req.ScreenNo}";
                                trCode = "opt10075";
                                break;

                            case "MINUTE":
                                {
                                    string days = req.Inputs != null && req.Inputs.ContainsKey("__DAYS__")
                                        ? req.Inputs["__DAYS__"]
                                        : "2";

                                    // 분봉은 req.Next를 페이지 번호처럼 사용하지 않는다.
                                    // req.Next는 키움의 최초/연속조회 플래그일 뿐이고,
                                    // 종료 판단은 ProcessMinuteCandleData에서 실제 캔들 날짜로 처리한다.
                                    rqName = $"분봉데이터_{req.Code}_{req.WindowId}_{req.ScreenNo}_{days}_{req.Token}";
                                    trCode = "opt10080";
                                    break;
                                }

                            case "RECENT_TICKS":
                                {
                                    string minutes = req.Inputs != null && req.Inputs.ContainsKey("__MINUTES__")
                                        ? req.Inputs["__MINUTES__"]
                                        : "5";

                                    rqName = $"최근틱_{req.Code}_{req.WindowId}_{req.ScreenNo}_{minutes}_{req.Token}";
                                    trCode = "opt10079";
                                    break;
                                }

                            case "PROGRAM_TRADE":
                                {
                                    string minutes = req.Inputs != null && req.Inputs.ContainsKey("__MINUTES__")
                                        ? req.Inputs["__MINUTES__"]
                                        : ProgramTradeDefaultSeedMinutes.ToString();

                                    rqName = $"프로그램매매_{req.Code}_{req.WindowId}_{req.ScreenNo}_{minutes}_{req.Token}";
                                    trCode = "OPT90008";
                                    break;
                                }
                        }

                        requestTrCode = trCode;

                        if (string.IsNullOrWhiteSpace(trCode))
                        {
                            requestResult = -999;
                            return;
                        }

                        requestResult = _api.CommRqData(
                            rqName,
                            trCode,
                            req.Next,
                            req.ScreenNo);
                    }
                    catch (System.Runtime.InteropServices.InvalidComObjectException)
                    {
                        requestResult = -997;
                    }
                    catch (System.Runtime.InteropServices.COMException)
                    {
                        requestResult = -997;
                    }
                    catch
                    {
                        requestResult = -998;
                    }
                });

                if (requestResult < 0)
                {
                    OnLog?.Invoke(
                        req.WindowId ?? "SYSTEM",
                        $"❌ {requestTrCode} 요청 실패: {requestResult} / type={requestType} / code={req.Code}");

                    // 실패한 TR은 반드시 스케줄러에 실패 완료를 알려서 큐가 멈추지 않게 한다.
                    _scheduler?.NotifyResponseFailed(req.WindowId, req.Code, requestType, requestResult);

                    // 주문내역/잔고/예수금/미체결 실패 때 빈 이벤트를 쏘면 ViewModel 후속 요청 루프가 생길 수 있으므로 호출하지 않는다.
                    // 분봉/최근틱/과거틱은 화면 로딩 상태 해제를 위해 실패 완료 이벤트만 최소 전달한다.
                    if (requestType == "MINUTE")
                    {
                        OnMinuteHistoryReceived?.Invoke(
                            req.WindowId,
                            req.Code,
                            new List<MinuteCandleRecord>(),
                            true);
                    }
                    else if (requestType == "RECENT_TICKS")
                    {
                        OnRecentTicksReceived?.Invoke(
                            req.WindowId,
                            req.Code,
                            new List<TrackedSymbol>(),
                            true);
                    }
                    else if (requestType == "PREV")
                    {
                        OnInitialHistoryReceived?.Invoke(
                            req.WindowId,
                            new List<TrackedSymbol>(),
                            true);
                    }
                    else if (requestType == "PROGRAM_TRADE")
                    {
                        // 프로그램매매 실패/상태 행은 실제 데이터가 아니므로 UI/DB로 보내지 않는다.
                        // 히스토리/분석 버퍼는 09:00~15:20 실제 수신 데이터만 유지한다.
                        OnLog?.Invoke(
                            req.WindowId ?? "SYSTEM",
                            $"[프로그램매매] 히스토리 요청 실패 - UI 데이터 미추가: {req.Code} / result={requestResult}");
                    }

                    return;
                }
            }
            catch (System.Runtime.InteropServices.InvalidComObjectException ex)
            {
                OnLog?.Invoke(req?.WindowId ?? "SYSTEM", $"❌ 키움 COM 객체 분리: {requestType} / {ex.Message}");

                if (req != null)
                    _scheduler?.NotifyResponseFailed(req.WindowId, req.Code, req.RequestType, -997);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke(req?.WindowId ?? "SYSTEM", $"⚠️ TR 요청 중 예외 발생: {requestType} / {ex.Message}");

                if (req != null)
                    _scheduler?.NotifyResponseFailed(req.WindowId, req.Code, req.RequestType, -998);
            }
            finally
            {
                Interlocked.Decrement(ref _activeTrCount);
                _apiSemaphore.Release();
            }
        }


        //private void ParseAndSendHoga(string windowId, string code, double currentPrice)
        //{
        //    try
        //    {
        //        var hoga = new HogaData { Code = code };
        //        for (int i = 10; i >= 1; i--) // 매도
        //        {
        //            int pFid = (i == 10) ? 50 : 40 + i;
        //            double price = Math.Abs(double.Parse(_api.GetCommRealData(code, pFid)));
        //            hoga.Asks.Add(new HogaItem { Price = price, IsCurrentPrice = (price == currentPrice), Quantity = long.Parse(_api.GetCommRealData(code, 60 + i)) });
        //        }
        //        for (int i = 1; i <= 10; i++) // 매수
        //        {
        //            double price = Math.Abs(double.Parse(_api.GetCommRealData(code, 50 + i)));
        //            hoga.Bids.Add(new HogaItem { Price = price, IsCurrentPrice = (price == currentPrice), Quantity = long.Parse(_api.GetCommRealData(code, 70 + i)) });
        //        }
        //        hoga.TotalAskQty = long.Parse(_api.GetCommRealData(code, 121));
        //        hoga.TotalBidQty = long.Parse(_api.GetCommRealData(code, 125));
        //        OnHogaReceived?.Invoke(windowId, hoga);
        //    }
        //    catch (Exception ex) { Debug.WriteLine($"호가 에러: {ex.Message}"); }
        //}
        private bool TryGetLatestHoga(string code, out HogaSnapshot snapshot)
        {
            return _latestHoga.TryGetValue(code, out snapshot);
        }

        private void ParseAndSendHoga(string windowId, string code, double currentPrice, bool saveToDb = true)
        {
            try
            {
                var hoga = new HogaData { Code = code };

                for (int i = 10; i >= 1; i--)// 매도
                {
                    int pFid = (i == 10) ? 50 : 40 + i;

                    double price = Math.Abs(double.Parse(_api.GetCommRealData(code, pFid)));

                    hoga.Asks.Add(new HogaItem
                    {
                        Price = price,
                        IsCurrentPrice = (price == currentPrice),
                        Quantity = long.Parse(_api.GetCommRealData(code, 60 + i))
                    });
                }

                for (int i = 1; i <= 10; i++)// 매수
                {
                    double price = Math.Abs(double.Parse(_api.GetCommRealData(code, 50 + i)));

                    hoga.Bids.Add(new HogaItem
                    {
                        Price = price,
                        IsCurrentPrice = (price == currentPrice),
                        Quantity = long.Parse(_api.GetCommRealData(code, 70 + i))
                    });
                }
                hoga.TotalAskQty = long.Parse(_api.GetCommRealData(code, 121));
                hoga.TotalBidQty = long.Parse(_api.GetCommRealData(code, 125));

                // 추가: 종목별 최신 호가 저장
                _latestHoga[code] = new HogaSnapshot
                {
                    Code = code,
                    TotalAskQty = hoga.TotalAskQty,
                    TotalBidQty = hoga.TotalBidQty,
                    Time = DateTime.Now
                };

                if (saveToDb)
                    _dbManager?.EnqueueHoga(hoga);

                OnHogaReceived?.Invoke(windowId, hoga);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"호가 에러: {ex.Message}");
            }
        }


        // --- [기존 로직 유지] ---
        private void Api_OnReceiveTrData(object sender, _DKHOpenAPIEvents_OnReceiveTrDataEvent e)
        {
            //Debug.WriteLine($"TR 수신: {e.sRQName}, TRCode: {e.sTrCode}");
            //Debug.WriteLine($"RepeatCnt: {_api.GetRepeatCnt(e.sTrCode, e.sRQName)}");

            var parts = e.sRQName.Split('_');
            if (parts.Length < 4) return;

            string category = parts[0];
            string currentCode = parts[1];
            string windowId = parts[2];
            string screenNo = parts[3];
            // parts[4]는 토큰

            if (category == "상세조회")
            {
                string name = _api.GetCommData(e.sTrCode, e.sRQName, 0, "종목명").Trim();
                string price = _api.GetCommData(e.sTrCode, e.sRQName, 0, "현재가").Trim();
                string change = _api.GetCommData(e.sTrCode, e.sRQName, 0, "대비").Trim();
                string rate = _api.GetCommData(e.sTrCode, e.sRQName, 0, "등락율").Trim();

                OnSymbolDetailUpdated?.Invoke(windowId, new Symbol
                {
                    Code = currentCode,
                    Name = name,
                    LastPrice = price,
                    Change = $"{change} ({rate}%)"
                });
            }
            else if (category == "분봉데이터")
            {
                int daysToLoad = parts.Length > 4 && int.TryParse(parts[4], out var d)
                    ? d
                    : 2;

                ProcessMinuteCandleData(
                    windowId,
                    currentCode,
                    screenNo,
                    daysToLoad,
                    e);
            }
            else if (category == "최근틱")
            {
                int minutes = parts.Length > 4 && int.TryParse(parts[4], out var m) ? m : 5;
                ProcessRecentTickData(windowId, currentCode, screenNo, minutes, e);
            }
            else if (category == "프로그램매매")
            {
                int minutes = parts.Length > 4 && int.TryParse(parts[4], out var m) ? m : ProgramTradeDefaultSeedMinutes;
                ProcessProgramTradeHistoryData(windowId, currentCode, screenNo, minutes, e);
            }
            else if (category == "보유종목조회")
            {
                int count = _api.GetRepeatCnt(e.sTrCode, e.sRQName);
                var holdings = new List<HoldingInfo>();

                for (int i = 0; i < count; i++)
                {
                    string code = _api.GetCommData(e.sTrCode, e.sRQName, i, "종목번호").Trim().Replace("A", "");
                    string name = _api.GetCommData(e.sTrCode, e.sRQName, i, "종목명").Trim(); // opw00018에도 종목명은 있어!

                    double.TryParse(_api.GetCommData(e.sTrCode, e.sRQName, i, "매입가").Trim(), out double entryPrice);
                    double.TryParse(_api.GetCommData(e.sTrCode, e.sRQName, i, "현재가").Trim(), out double currentPrice);
                    int.TryParse(_api.GetCommData(e.sTrCode, e.sRQName, i, "보유수량").Trim(), out int qty);

                    holdings.Add(new HoldingInfo
                    {
                        Code = code,
                        Name = name, // 이름 추가
                        EntryPrice = entryPrice,
                        CurrentPrice = Math.Abs(currentPrice), // 현재가는 가끔 -로 오니 절대값 처리
                        Quantity = qty
                    });
                }

                // 수신된 리스트를 뷰모델로 전달
                OnHoldingsReceived?.Invoke(windowId, holdings);
                _scheduler?.NotifyResponseReceived(windowId, currentCode, "HOLDINGS");
            }
            else if (e.sRQName.StartsWith("계좌잔고조회"))
            {
                // API에서 예수금(또는 잔고) 수치 가져오기
                long deposit = long.Parse(_api.GetCommData(e.sTrCode, e.sRQName, 0, "주문가능금액").Trim());

                // 이벤트를 구독 중인 ViewModel들에게 알림
                OnBalanceUpdated?.Invoke(windowId, deposit);
                _scheduler?.NotifyResponseReceived(windowId, currentCode, "CASH");
            }
            else if (category == "과거데이터")
            {
                ProcessHistoryData(windowId, currentCode, screenNo, e);
            }
            else if (category == "주문내역조회")
            {
                ProcessOrderHistoryData(
                    windowId,
                    currentCode,
                    screenNo,
                    e);
            }
            else if (category == "미체결조회")
            {
                ProcessOrderListData(
                    windowId,
                    currentCode,
                    screenNo,
                    e,
                    isOrderHistory: false);
            }
            //if (category == "주문내역조회")
            //{
            //    OnOrdersReceived?.Invoke(windowId, orders);
            //}
            //else
            //{
            //    OnUnfilledOrdersReceived?.Invoke(windowId, orders);
            //}
        }

        private async void ProcessOrderHistoryData(string windowId, string accountNo, string screenNo, _DKHOpenAPIEvents_OnReceiveTrDataEvent e)
        {
            var orders = ParseOrderRows(e);
            if (orders.Count > 1)
                orders.Reverse();

            string bufferKey = $"{windowId}_{accountNo}_ORDERS";

            var buffer = _orderHistoryBuffer.GetOrAdd(
                bufferKey,
                _ => new List<OrderInfo>());

            lock (buffer)
            {
                buffer.AddRange(orders);
            }

            bool hasNext = e.sPrevNext == "2";

            if (hasNext)
            {
                await DelayBeforeContinuousTrRequestAsync().ConfigureAwait(false);

                RequestOrderHistory(
                    windowId,
                    accountNo,
                    screenNo,
                    next: 2);

                _scheduler?.NotifyResponseReceived(windowId, accountNo, "ORDERS");
                return;
            }

            List<OrderInfo> finalOrders;

            lock (buffer)
            {
                finalOrders = new List<OrderInfo>(buffer.Count);
                foreach (var order in buffer)
                {
                    if (order == null || string.IsNullOrWhiteSpace(order.OrderNo))
                        continue;

                    finalOrders.Add(order);
                }
            }

            _orderHistoryBuffer.TryRemove(bufferKey, out _);

            // 주문내역은 창 전용이 아니라 계좌 공통 데이터다.
            // 한 창에서 조회해도 모든 창의 주문내역 리스트/차트 마커가 같이 갱신되어야 한다.
            OnOrdersReceived?.Invoke("COMMON", finalOrders);

            _scheduler?.NotifyResponseReceived(windowId, accountNo, "ORDERS");
        }
        private List<OrderInfo> ParseOrderRows(_DKHOpenAPIEvents_OnReceiveTrDataEvent e)
        {
            int count = _api.GetRepeatCnt(e.sTrCode, e.sRQName);
            var orders = new List<OrderInfo>();

            for (int i = 0; i < count; i++)
            {
                string rawOrderNo = _api.GetCommData(e.sTrCode, e.sRQName, i, "주문번호").Trim();

                string rawCode = _api.GetCommData(e.sTrCode, e.sRQName, i, "종목번호").Trim();
                if (string.IsNullOrWhiteSpace(rawCode))
                    rawCode = _api.GetCommData(e.sTrCode, e.sRQName, i, "종목코드").Trim();

                rawCode = rawCode
                    .Replace("A", "")
                    .Replace(" ", "")
                    .Trim();

                string name = _api.GetCommData(e.sTrCode, e.sRQName, i, "종목명").Trim();

                if (string.IsNullOrWhiteSpace(rawCode) && !string.IsNullOrWhiteSpace(name))
                {
                    var symbol = GetTotalSymbolList()
                        .FirstOrDefault(x => x != null && x.Name == name);

                    if (symbol != null)
                        rawCode = symbol.Code;
                }

                string rawOrderTime = _api.GetCommData(e.sTrCode, e.sRQName, i, "주문시간").Trim();
                if (string.IsNullOrWhiteSpace(rawOrderTime))
                    rawOrderTime = _api.GetCommData(e.sTrCode, e.sRQName, i, "시간").Trim();

                int execQty = SafeInt(_api.GetCommData(e.sTrCode, e.sRQName, i, "체결수량"));
                if (execQty <= 0)
                    execQty = SafeInt(_api.GetCommData(e.sTrCode, e.sRQName, i, "체결량"));

                double orderPrice = SafeDouble(_api.GetCommData(e.sTrCode, e.sRQName, i, "주문단가"));
                if (orderPrice <= 0)
                    orderPrice = SafeDouble(_api.GetCommData(e.sTrCode, e.sRQName, i, "주문가격"));
                if (orderPrice <= 0)
                    orderPrice = SafeDouble(_api.GetCommData(e.sTrCode, e.sRQName, i, "주문가"));

                double execPrice = SafeDouble(_api.GetCommData(e.sTrCode, e.sRQName, i, "체결단가"));
                if (execPrice <= 0)
                    execPrice = SafeDouble(_api.GetCommData(e.sTrCode, e.sRQName, i, "체결가"));

                orders.Add(new OrderInfo
                {
                    OrderNo = rawOrderNo,
                    ParentOrderNo = _api.GetCommData(e.sTrCode, e.sRQName, i, "원주문번호").Trim(),
                    Code = rawCode,
                    Name = name,
                    OrderTime = NormalizeOrderTime(rawOrderTime),
                    Status = _api.GetCommData(e.sTrCode, e.sRQName, i, "주문상태").Trim(),
                    OrderType = _api.GetCommData(e.sTrCode, e.sRQName, i, "주문구분").Trim(),

                    Qty = SafeInt(_api.GetCommData(e.sTrCode, e.sRQName, i, "주문수량")),
                    ExecQty = execQty,
                    Price = orderPrice,
                    ExecPrice = execPrice,
                    UntradedQty = SafeInt(_api.GetCommData(e.sTrCode, e.sRQName, i, "미체결수량")),

                    StrategyName = "ORDER_TR"
                });
            }

            return orders;
        }


        private void ProcessOrderListData(string windowId, string accountNo, string screenNo, _DKHOpenAPIEvents_OnReceiveTrDataEvent e, bool isOrderHistory)
        {
            var orders = ParseOrderRows(e);

            if (isOrderHistory)
            {
                OnOrdersReceived?.Invoke(windowId, orders);
                _scheduler?.NotifyResponseReceived(windowId, accountNo, "ORDERS");
            }
            else
            {
                OnUnfilledOrdersReceived?.Invoke(windowId, orders);
                _scheduler?.NotifyResponseReceived(windowId, accountNo, "UNFILLED");
            }
        }


        // ============================================================
        // [추가] KiwoomApiService 클래스 내부 메서드
        // ============================================================
        private async void ProcessMinuteCandleData(
            string windowId,
            string currentCode,
            string screenNo,
            int daysToLoad,
            _DKHOpenAPIEvents_OnReceiveTrDataEvent e)
        {
            int count = 0;
            var rows = new List<string[]>();

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                count = _api.GetRepeatCnt(e.sTrCode, e.sRQName);

                for (int i = 0; i < count; i++)
                {
                    rows.Add(new[]
                    {
                        _api.GetCommData(e.sTrCode, e.sRQName, i, "체결시간").Trim(),
                        _api.GetCommData(e.sTrCode, e.sRQName, i, "시가").Trim(),
                        _api.GetCommData(e.sTrCode, e.sRQName, i, "고가").Trim(),
                        _api.GetCommData(e.sTrCode, e.sRQName, i, "저가").Trim(),
                        _api.GetCommData(e.sTrCode, e.sRQName, i, "현재가").Trim(),
                        _api.GetCommData(e.sTrCode, e.sRQName, i, "거래량").Trim()
                    });
                }
            });

            string cycleKey = $"{windowId}_{currentCode}_MINUTE";
            int cycle = _minuteRequestCycleMap.AddOrUpdate(
                cycleKey,
                1,
                (_, old) => old + 1);

            if (count == 0)
            {
                _minuteRequestCycleMap.TryRemove(cycleKey, out _);

                OnMinuteHistoryReceived?.Invoke(
                    windowId,
                    currentCode,
                    new List<MinuteCandleRecord>(),
                    true);

                OnLog?.Invoke(windowId, $"[MINUTE_END] count=0 code={currentCode}, cycle={cycle}, screen={screenNo}");
                _scheduler?.NotifyResponseReceived(windowId, currentCode, "MINUTE");
                return;
            }

            var candles = new List<MinuteCandleRecord>(count);

            await Task.Run(() =>
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    if (!TryParseMinuteRow(currentCode, rows[i], out var candle))
                        continue;

                    // 분석용 과거분봉은 전일/당일 각각 09:00:00~15:20:59만 사용한다.
                    if (!IsAnalysisMinuteTime(candle.CandleTime))
                        continue;

                    candles.Add(candle);
                }
            });

            // 키움 히스토리 TR은 최신→과거 역순으로 내려온다.
            // TR 1회 수신분만 1회 Reverse 후 이후 단계에서는 재정렬하지 않는다.
            if (candles.Count > 1)
                candles.Reverse();

            if (candles.Count > 0)
                _dbManager.EnqueueMinuteCandles(candles);

            bool hasNext = e.sPrevNext == "2";

            bool isMarketOpenNow =
                DateTime.Now.TimeOfDay >= AnalysisMinuteStartTime &&
                DateTime.Now.TimeOfDay <= AnalysisMinuteEndTime;

            // 차트/분봉 TR 연속조회 범위는 기존 동작을 유지한다.
            // 장중에는 요청 daysToLoad 기준, 장마감 후에는 최신 거래일 1일치만 수집한다.
            // 15:20 컷은 분석/히스토리 시간 필터 역할만 하고, 차트 날짜 기준을 daysToLoad=2로 밀지 않는다.
            int effectiveDaysToLoad = isMarketOpenNow ? Math.Max(1, daysToLoad) : 1;
            DateTime targetDate = DateTime.Today.AddDays(-(effectiveDaysToLoad - 1));

            DateTime? oldestDate = candles.Count > 0
                ? (DateTime?)candles.Min(x => x.CandleTime.Date)
                : null;

            bool reachedTargetDate =
                oldestDate.HasValue &&
                oldestDate.Value <= targetDate;

            // 같은 날짜 데이터가 계속 들어오는 종목에서 무한 연속조회를 막는 최종 안전장치.
            // 정상 종료 기준은 날짜 도달이고, maxCycle은 장애 방어용이다.
            int maxCycle = isMarketOpenNow ? 20 : 5;

            bool canRequestNext =
                hasNext &&
                !reachedTargetDate &&
                cycle < maxCycle;

            bool isLast = !canRequestNext;

            OnMinuteHistoryReceived?.Invoke(
                windowId,
                currentCode,
                candles,
                isLast);

            OnLog?.Invoke(
                windowId,
                $"[MINUTE_DEBUG] code={currentCode}, cycle={cycle}/{maxCycle}, prevNext={e.sPrevNext}, " +
                $"target={targetDate:yyyyMMdd}, oldest={(oldestDate.HasValue ? oldestDate.Value.ToString("yyyyMMdd") : "NONE")}, " +
                $"reached={reachedTargetDate}, canNext={canRequestNext}, rows={candles.Count}, screen={screenNo}");

            if (canRequestNext)
            {
                await DelayBeforeContinuousTrRequestAsync().ConfigureAwait(false);

                RequestMinuteCandles(
                    windowId,
                    currentCode,
                    screenNo,
                    daysToLoad: daysToLoad,
                    maxRows: 0,
                    priority: 0,
                    next: 2);
            }
            else
            {
                _minuteRequestCycleMap.TryRemove(cycleKey, out _);
            }

            _scheduler?.NotifyResponseReceived(windowId, currentCode, "MINUTE");
        }

        private bool TryParseMinuteRow(string code, string[] row, out MinuteCandleRecord candle)
        {
            candle = null;

            if (row == null || row.Length < 6) return false;
            if (!DateTime.TryParseExact(row[0], "yyyyMMddHHmmss", null, DateTimeStyles.None, out var time))
                return false;

            candle = new MinuteCandleRecord
            {
                Code = code,
                CandleTime = new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0),
                Interval = "1m",
                Open = CleanDouble(row[1]),
                High = CleanDouble(row[2]),
                Low = CleanDouble(row[3]),
                Close = CleanDouble(row[4]),
                Volume = Math.Abs((long)CleanDouble(row[5])),
                Source = "HIST",
                CreatedAt = DateTime.Now
            };

            return true;
        }

        // [교체] ProcessRecentTickData 전체 교체
        private async void ProcessRecentTickData(
            string windowId,
            string currentCode,
            string screenNo,
            int minutes,
            _DKHOpenAPIEvents_OnReceiveTrDataEvent e)
        {
            int count = 0;
            var rows = new List<string[]>();

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                count = _api.GetRepeatCnt(e.sTrCode, e.sRQName);

                for (int i = 0; i < count; i++)
                {
                    rows.Add(new[]
                    {
                _api.GetCommData(e.sTrCode, e.sRQName, i, "현재가").Trim(),
                _api.GetCommData(e.sTrCode, e.sRQName, i, "거래량").Trim(),
                _api.GetCommData(e.sTrCode, e.sRQName, i, "체결시간").Trim()
            });
                }
            });

            if (count == 0)
            {
                OnRecentTicksReceived?.Invoke(windowId, currentCode, new List<TrackedSymbol>(), true);
                _scheduler?.NotifyResponseReceived(windowId, currentCode, "RECENT_TICKS");
                return;
            }

            var ticks = new List<TrackedSymbol>();
            bool shouldStop = false;
            DateTime now = DateTime.Now;
            int safeMinutes = TryResolveReplayTickDateRequest(minutes, out _)
                ? minutes
                : Math.Max(1, minutes);
            TickWindowRange range = GetRecentTickWindow(now, safeMinutes);

            await Task.Run(() =>
            {
                foreach (var row in rows)
                {
                    if (!TryParseRecentTickRow(row, range, now, out DateTime tickTime, out double tickPrice, out int tickVol))
                        continue;

                    if (range.UseRangeDate)
                    {
                        DateTime targetDate = range.Start.Date;
                        DateTime tickDate = tickTime.Date;

                        // Replay 원천틱 수집은 날짜와 09:00~15:20:59 시간대를 모두 확인한다.
                        if (tickDate > targetDate)
                            continue;

                        if (tickDate < targetDate)
                        {
                            shouldStop = true;
                            break;
                        }

                        if (tickTime > range.End)
                            continue;

                        if (tickTime < range.Start)
                        {
                            shouldStop = true;
                            break;
                        }
                    }
                    else
                    {
                        // 분석용 과거틱 seed는 15:15:00~15:20:59 구간만 사용한다.
                        if (tickTime > range.End)
                            continue;

                        if (tickTime < range.Start)
                        {
                            shouldStop = true;
                            break;
                        }
                    }

                    ticks.Add(new TrackedSymbol
                    {
                        Code = currentCode,
                        LastPrice = tickPrice,
                        LastVolume = Math.Abs(tickVol),
                        ClosePrice = tickPrice,
                        HighPrice = tickPrice,
                        LowPrice = tickPrice,
                        LastUpdated = tickTime,
                        Side = "U"
                    });
                }

                // 키움 opt10079는 최신→과거 역순이다.
                // 현재 TR 배치만 1회 Reverse하고, 이후 병합/정렬은 하지 않는다.
                if (ticks.Count > 1)
                    ticks.Reverse();

                // RECENT_TICKS도 opt10079 히스토리 tick이다.
                // 거래량 부호는 TryParseRecentTickRow에서 절대값 처리되므로 여기서 Side를 만들면 항상 B가 된다.
                // Reverse 이후 시간 오름차순 상태에서 이전 체결가와 비교해 B/S를 보정한다.
                ApplyHistoryTickSideByAdjacentPrice(ticks);
            });

            bool hasNext = e.sPrevNext == "2";
            bool isLast = !hasNext || shouldStop;

            OnRecentTicksReceived?.Invoke(windowId, currentCode, ticks, isLast);

            if (hasNext && !shouldStop)
            {
                await DelayBeforeContinuousTrRequestAsync().ConfigureAwait(false);
                RequestRecentTicksCore(windowId, currentCode, screenNo, safeMinutes, priority: 2, next: 2);
            }

            // RECENT_TICKS 요청 1회에 대한 응답 처리 완료.
            _scheduler?.NotifyResponseReceived(windowId, currentCode, "RECENT_TICKS");
        }

        private string GetCommDataAny(string trCode, string rqName, int index, params string[] fieldNames)
        {
            if (fieldNames == null || fieldNames.Length == 0)
                return string.Empty;

            foreach (var fieldName in fieldNames)
            {
                if (string.IsNullOrWhiteSpace(fieldName))
                    continue;

                try
                {
                    string value = _api.GetCommData(trCode, rqName, index, fieldName).Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
                catch
                {
                }
            }

            return string.Empty;
        }

        private DateTime ParseProgramTradeTime(string rawTime, DateTime baseDate)
        {
            DateTime date = baseDate == default(DateTime) ? DateTime.Today : baseDate.Date;

            if (string.IsNullOrWhiteSpace(rawTime))
                return DateTime.MinValue;

            rawTime = rawTime.Trim();

            DateTime parsed;
            if (DateTime.TryParseExact(rawTime, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                return parsed;

            if (DateTime.TryParseExact(rawTime, "HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                return date.Add(parsed.TimeOfDay);

            if (DateTime.TryParseExact(rawTime, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                return date.Add(parsed.TimeOfDay);

            if (DateTime.TryParseExact(rawTime, "HHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                return date.Add(parsed.TimeOfDay);

            return DateTime.MinValue;
        }

        private static bool IsValidProgramTradeTime(DateTime time)
        {
            if (time == default(DateTime) || time == DateTime.MinValue)
                return false;

            TimeSpan tod = time.TimeOfDay;
            return tod >= ProgramTradeStartTime && tod <= ProgramTradeEndTime;
        }

        private static string ResolveProgramTradeDirection(long netAmountDelta, long netQuantityDelta, long netAmount)
        {
            long basis = netAmountDelta != 0 ? netAmountDelta : netQuantityDelta;

            if (basis > 0)
                return "Buy";

            if (basis < 0)
                return "Sell";

            if (netAmount > 0)
                return "BuyHold";

            if (netAmount < 0)
                return "SellHold";

            return "Flat";
        }

        private ProgramFlowSnapshotRecord BuildProgramFlowSnapshot(string code, DateTime snapshotTime, List<ProgramTradeInfo> buffer, string sourceState)
        {
            if (string.IsNullOrWhiteSpace(code) || buffer == null || buffer.Count == 0)
                return null;

            var ordered = buffer
                .Where(x => x != null && x.TradeTime != default(DateTime))
                .OrderBy(x => x.TradeTime)
                .ToList();

            if (ordered.Count == 0)
                return null;

            var latest = ordered.LastOrDefault(x => x.TradeTime <= snapshotTime) ?? ordered.Last();

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
            string direction = ResolveProgramFlowDirection(score, warmedUp);

            return new ProgramFlowSnapshotRecord
            {
                SnapshotId = $"PF_{code}_{latest.TradeTime:yyyyMMddHHmmss}_{NormalizeProgramSourceKey(sourceState)}",
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
                FlowDirection = direction,
                IsWarmedUp = warmedUp,
                SourceState = sourceState,
                CreatedAt = DateTime.Now
            };
        }

        private static long ResolveProgramDeltaAmount(List<ProgramTradeInfo> ordered, ProgramTradeInfo latest, TimeSpan span)
        {
            if (ordered == null || latest == null)
                return 0;

            DateTime target = latest.TradeTime - span;
            var baseline = ordered.LastOrDefault(x => x.TradeTime <= target) ?? ordered.FirstOrDefault();

            if (baseline == null)
                return 0;

            return latest.ProgramNetAmount - baseline.ProgramNetAmount;
        }

        private static long ResolveProgramDeltaQuantity(List<ProgramTradeInfo> ordered, ProgramTradeInfo latest, TimeSpan span)
        {
            if (ordered == null || latest == null)
                return 0;

            DateTime target = latest.TradeTime - span;
            var baseline = ordered.LastOrDefault(x => x.TradeTime <= target) ?? ordered.FirstOrDefault();

            if (baseline == null)
                return 0;

            return latest.ProgramNetQuantity - baseline.ProgramNetQuantity;
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

        private ProgramFlowSnapshotRecord AddProgramTradeAndPublish(string windowId, ProgramTradeInfo trade)
        {
            if (trade == null || string.IsNullOrWhiteSpace(trade.Code))
                return null;

            if (!IsValidProgramTradeTime(trade.TradeTime))
                return null;

            if (trade.CreatedAt == default(DateTime))
                trade.CreatedAt = DateTime.Now;

            if (string.IsNullOrWhiteSpace(trade.Direction))
                trade.Direction = ResolveProgramTradeDirection(trade.ProgramNetAmountDelta, trade.ProgramNetQuantityDelta, trade.ProgramNetAmount);

            List<ProgramTradeInfo> snapshotBuffer;

            lock (_programTradeLock)
            {
                var buffer = _programTradeBuffers.GetOrAdd(trade.Code, _ => new List<ProgramTradeInfo>());

                buffer.RemoveAll(x =>
                    x != null &&
                    x.TradeTime == trade.TradeTime &&
                    string.Equals(x.Source, trade.Source, StringComparison.OrdinalIgnoreCase));

                buffer.Add(trade);
                buffer.Sort((a, b) => a.TradeTime.CompareTo(b.TradeTime));

                DateTime keepFrom = trade.TradeTime.AddMinutes(-Math.Max(30, ProgramTradeDefaultSeedMinutes + 10));
                buffer.RemoveAll(x => x == null || x.TradeTime < keepFrom);

                snapshotBuffer = buffer.ToList();
            }

            var flow = BuildProgramFlowSnapshot(trade.Code, trade.TradeTime, snapshotBuffer, trade.Source);

            // REALTIME뿐 아니라 TR_SEED도 Replay/분석 재현용 원천 데이터다.
            // DB 쪽은 Source와 결정적 SnapshotId 기준으로 중복을 upsert하므로 저장 경로에서 제거하지 않는다.
            if (IsPersistableProgramTradeSource(trade.Source))
            {
                _dbManager?.EnqueueProgramTrade(trade);
                if (flow != null)
                    _dbManager?.EnqueueProgramFlowSnapshot(flow);
            }

            OnProgramTradeReceived?.Invoke(windowId ?? "COMMON", trade, flow);
            return flow;
        }

        private static string NormalizeProgramSourceKey(string source)
        {
            string value = string.IsNullOrWhiteSpace(source) ? "UNKNOWN" : source.Trim().ToUpperInvariant();
            var chars = value
                .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
                .ToArray();

            return new string(chars);
        }

        private static bool IsPersistableProgramTradeSource(string source)
        {
            string value = (source ?? string.Empty).Trim();

            return string.Equals(value, "REALTIME", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "TR_SEED", StringComparison.OrdinalIgnoreCase);
        }

        private void ParseAndSendProgramTrade(string windowId, string code, string source)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(code))
                    return;

                string rawTime = _api.GetCommRealData(code, FidProgramTime).Trim();
                DateTime tradeTime = ParseProgramTradeTime(rawTime, DateTime.Today);

                if (!IsValidProgramTradeTime(tradeTime))
                {
                    DateTime now = DateTime.Now;
                    if (!IsValidProgramTradeTime(now))
                        return;

                    tradeTime = now;
                    Debug.WriteLine($"[프로그램매매 실시간] 시간 FID 누락/오류 - 현재시각 대체: {code} / raw={rawTime}");
                }

                var trade = new ProgramTradeInfo
                {
                    Code = code,
                    Name = string.Empty,
                    TradeTime = tradeTime,
                    CurrentPrice = Math.Abs(SafeSignedDouble(_api.GetCommRealData(code, FidProgramCurrentPrice).Trim())),
                    Volume = Math.Abs(SafeSignedLong(_api.GetCommRealData(code, FidProgramVolume).Trim())),
                    ProgramSellAmount = Math.Abs(SafeSignedLong(_api.GetCommRealData(code, FidProgramSellAmount).Trim())),
                    ProgramBuyAmount = Math.Abs(SafeSignedLong(_api.GetCommRealData(code, FidProgramBuyAmount).Trim())),
                    ProgramNetAmount = SafeSignedLong(_api.GetCommRealData(code, FidProgramNetAmount).Trim()),
                    ProgramNetAmountDelta = SafeSignedLong(_api.GetCommRealData(code, FidProgramNetAmountDelta).Trim()),
                    ProgramSellQuantity = Math.Abs(SafeSignedLong(_api.GetCommRealData(code, FidProgramSellQuantity).Trim())),
                    ProgramBuyQuantity = Math.Abs(SafeSignedLong(_api.GetCommRealData(code, FidProgramBuyQuantity).Trim())),
                    ProgramNetQuantity = SafeSignedLong(_api.GetCommRealData(code, FidProgramNetQuantity).Trim()),
                    ProgramNetQuantityDelta = SafeSignedLong(_api.GetCommRealData(code, FidProgramNetQuantityDelta).Trim()),
                    Source = string.IsNullOrWhiteSpace(source) ? "REALTIME" : source,
                    CreatedAt = DateTime.Now
                };

                trade.Direction = ResolveProgramTradeDirection(
                    trade.ProgramNetAmountDelta,
                    trade.ProgramNetQuantityDelta,
                    trade.ProgramNetAmount);

                AddProgramTradeAndPublish(windowId, trade);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[프로그램매매 실시간 파싱 오류] {code} / {ex.Message}");
                OnLog?.Invoke(windowId ?? "COMMON", $"[프로그램매매 실시간 파싱 오류] {code} / {ex.Message}");
            }
        }

        private async void ProcessProgramTradeHistoryData(
            string windowId,
            string currentCode,
            string screenNo,
            int minutes,
            _DKHOpenAPIEvents_OnReceiveTrDataEvent e)
        {
            int count = 0;
            var rows = new List<ProgramTradeInfo>();
            DateTime now = DateTime.Now;
            int safeMinutes = Math.Max(1, minutes);
            DateTime effectiveEndTime = now;
            DateTime programCutoffTime = now.Date.Add(ProgramTradeEndTime);

            // 장 마감 노이즈 컷 기준은 15:20이다.
            // 15:20 이후 실행해도 최근 N분 기준은 현재 시간이 아니라 15:20을 기준으로 잡아야 한다.
            if (effectiveEndTime.TimeOfDay > ProgramTradeEndTime)
                effectiveEndTime = programCutoffTime;

            DateTime fromTime = effectiveEndTime.AddMinutes(-safeMinutes);
            bool reachedWindowStart = false;

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                count = _api.GetRepeatCnt(e.sTrCode, e.sRQName);

                for (int i = 0; i < count; i++)
                {
                    string rawTime = GetCommDataAny(e.sTrCode, e.sRQName, i, "시간", "체결시간", "일자시간");
                    DateTime tradeTime = ParseProgramTradeTime(rawTime, now.Date);

                    if (!IsValidProgramTradeTime(tradeTime))
                        continue;

                    if (tradeTime > effectiveEndTime)
                        continue;

                    if (tradeTime < fromTime)
                    {
                        reachedWindowStart = true;
                        continue;
                    }

                    rows.Add(new ProgramTradeInfo
                    {
                        Code = currentCode,
                        Name = GetCommDataAny(e.sTrCode, e.sRQName, i, "종목명"),
                        TradeTime = tradeTime,
                        CurrentPrice = Math.Abs(SafeSignedDouble(GetCommDataAny(e.sTrCode, e.sRQName, i, "현재가"))),
                        Volume = Math.Abs(SafeSignedLong(GetCommDataAny(e.sTrCode, e.sRQName, i, "거래량", "누적거래량"))),
                        ProgramSellAmount = Math.Abs(SafeSignedLong(GetCommDataAny(e.sTrCode, e.sRQName, i, "프로그램매도금액", "매도금액"))),
                        ProgramBuyAmount = Math.Abs(SafeSignedLong(GetCommDataAny(e.sTrCode, e.sRQName, i, "프로그램매수금액", "매수금액"))),
                        ProgramNetAmount = SafeSignedLong(GetCommDataAny(e.sTrCode, e.sRQName, i, "프로그램순매수금액", "순매수금액")),
                        ProgramNetAmountDelta = SafeSignedLong(GetCommDataAny(e.sTrCode, e.sRQName, i, "프로그램순매수금액증감", "순매수금액증감")),
                        ProgramSellQuantity = Math.Abs(SafeSignedLong(GetCommDataAny(e.sTrCode, e.sRQName, i, "프로그램매도수량", "매도수량"))),
                        ProgramBuyQuantity = Math.Abs(SafeSignedLong(GetCommDataAny(e.sTrCode, e.sRQName, i, "프로그램매수수량", "매수수량"))),
                        ProgramNetQuantity = SafeSignedLong(GetCommDataAny(e.sTrCode, e.sRQName, i, "프로그램순매수수량", "순매수수량")),
                        ProgramNetQuantityDelta = SafeSignedLong(GetCommDataAny(e.sTrCode, e.sRQName, i, "프로그램순매수수량증감", "순매수수량증감")),
                        Source = "TR_SEED",
                        CreatedAt = DateTime.Now
                    });
                }
            });

            OnLog?.Invoke(windowId, $"[프로그램매매] TR 응답: {currentCode} / count={count} / rows={rows.Count} / window={fromTime:HH:mm:ss}~{effectiveEndTime:HH:mm:ss} / prevNext={e.sPrevNext}");

            if (count == 0)
            {
                // 실제 프로그램매매 데이터가 아니므로 UI/DB에 상태 행을 추가하지 않는다.
                OnLog?.Invoke(windowId, $"[프로그램매매] TR 응답 0건 - UI 데이터 미추가: {currentCode}");

                _scheduler?.NotifyResponseReceived(windowId, currentCode, "PROGRAM_TRADE");
                return;
            }

            if (rows.Count == 0)
            {
                // 15:20 이후/분석창 밖 데이터는 노이즈로 컷한다.
                // 컷 결과는 로그로만 남기고 ProgramTrades/UI/DB에는 넣지 않는다.
                OnLog?.Invoke(windowId, $"[프로그램매매] TR 필터 후 0건 - UI 데이터 미추가: {currentCode} / window={fromTime:HH:mm:ss}~{effectiveEndTime:HH:mm:ss}");

                bool hasNextForFiltered = e.sPrevNext == "2";
                if (hasNextForFiltered && !reachedWindowStart)
                {
                    await DelayBeforeContinuousTrRequestAsync().ConfigureAwait(false);
                    RequestProgramTradeHistory(windowId, currentCode, screenNo, safeMinutes, priority: 2, next: 2);
                }

                _scheduler?.NotifyResponseReceived(windowId, currentCode, "PROGRAM_TRADE");
                return;
            }

            foreach (var trade in rows.OrderBy(x => x.TradeTime))
            {
                trade.Direction = ResolveProgramTradeDirection(
                    trade.ProgramNetAmountDelta,
                    trade.ProgramNetQuantityDelta,
                    trade.ProgramNetAmount);

                AddProgramTradeAndPublish(windowId, trade);
            }

            bool hasNext = e.sPrevNext == "2";

            if (hasNext && !reachedWindowStart)
            {
                await DelayBeforeContinuousTrRequestAsync().ConfigureAwait(false);
                RequestProgramTradeHistory(windowId, currentCode, screenNo, safeMinutes, priority: 2, next: 2);
            }

            _scheduler?.NotifyResponseReceived(windowId, currentCode, "PROGRAM_TRADE");
        }

        private double CleanDouble(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            value = value.Replace("+", "").Replace("-", "").Replace(",", "").Trim();
            double.TryParse(value, out double result);
            return Math.Abs(result);
        }

        private async void ProcessHistoryData(string windowId, string currentCode, string screenNo, _DKHOpenAPIEvents_OnReceiveTrDataEvent e)
        {
            int count = 0;
            var rawDataRows = new List<string[]>();

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                count = _api.GetRepeatCnt(e.sTrCode, e.sRQName);

                for (int i = 0; i < count; i++)
                {
                    rawDataRows.Add(new string[]
                    {
                _api.GetCommData(e.sTrCode, e.sRQName, i, "현재가").Trim(),
                _api.GetCommData(e.sTrCode, e.sRQName, i, "거래량").Trim(),
                _api.GetCommData(e.sTrCode, e.sRQName, i, "체결시간").Trim()
                    });
                }
            });

            if (count == 0)
            {
                OnInitialHistoryReceived?.Invoke(windowId, new List<TrackedSymbol>(), true);
                _scheduler?.NotifyResponseReceived(windowId, currentCode, "PREV");
                return;
            }

            List<TrackedSymbol> currentTrHistory = new List<TrackedSymbol>();
            bool shouldStop = false;
            DateTime now = DateTime.Now;
            TickWindowRange range = GetRecentTickWindow(now, 5);

            await Task.Run(() =>
            {
                foreach (var row in rawDataRows)
                {
                    if (!TryParseRecentTickRow(row, range, now, out DateTime pTime, out double pPrice, out int pVol))
                        continue;

                    // 분석용 과거틱은 15:20 종가 구간 -5분으로 통일한다.
                    if (pTime > range.End)
                        continue;

                    if (pTime < range.Start)
                    {
                        shouldStop = true;
                        break;
                    }

                    currentTrHistory.Add(new TrackedSymbol
                    {
                        Code = currentCode,
                        LastPrice = pPrice,
                        LastVolume = Math.Abs(pVol),
                        ClosePrice = pPrice,
                        HighPrice = pPrice,
                        LowPrice = pPrice,
                        LastUpdated = pTime,
                        Side = "U"
                    });
                }

                // 키움 과거틱 TR 1회 수신분만 1회 Reverse한다.
                // 정렬 순서는 기존 그대로 유지한다.
                if (currentTrHistory.Count > 1)
                    currentTrHistory.Reverse();

                // opt10079 과거틱은 거래량만 내려오는 구간이 있어 Side를 원천에서 확정할 수 없다.
                // Reverse 이후 시간 오름차순 상태에서 이전 체결가와 비교해 B/S를 보정한다.
                ApplyHistoryTickSideByAdjacentPrice(currentTrHistory);
            });

            bool hasNext = e.sPrevNext == "2";

            // 중요:
            // islast는 "다음 페이지가 없음" 또는 "15:15 이전까지 도달해서 중단"이면 true
            bool isLast = !hasNext || shouldStop;

            OnInitialHistoryReceived?.Invoke(windowId, currentTrHistory, isLast);

            if (hasNext && !shouldStop)
            {
                await DelayBeforeContinuousTrRequestAsync().ConfigureAwait(false);
                RequestPreviousData(windowId, currentCode, 2, screenNo, 2);
            }

            // PREV 요청 1회에 대한 응답 처리 완료.
            _scheduler?.NotifyResponseReceived(windowId, currentCode, "PREV");
        }

        private static void ApplyHistoryTickSideByAdjacentPrice(List<TrackedSymbol> ticks)
        {
            if (ticks == null || ticks.Count == 0)
                return;

            double? previousPrice = null;
            string previousSide = null;

            for (int i = 0; i < ticks.Count; i++)
            {
                var tick = ticks[i];
                if (tick == null)
                    continue;

                string side = null;

                if (previousPrice.HasValue)
                {
                    if (tick.LastPrice > previousPrice.Value)
                        side = "B";
                    else if (tick.LastPrice < previousPrice.Value)
                        side = "S";
                    else
                        side = previousSide;
                }

                // TR 배치의 첫 tick은 이전가격이 없다.
                // 이 경우에만 다음 tick 방향으로 보정하고, 전부 같은 가격이면 B로 둔다.
                if (string.IsNullOrWhiteSpace(side))
                    side = ResolveFirstHistoryTickSide(ticks, i);

                tick.Side = side;
                previousPrice = tick.LastPrice;
                previousSide = side;
            }
        }

        private static string ResolveFirstHistoryTickSide(List<TrackedSymbol> ticks, int index)
        {
            var current = ticks == null || index < 0 || index >= ticks.Count
                ? null
                : ticks[index];

            if (current == null)
                return "B";

            for (int i = index + 1; i < ticks.Count; i++)
            {
                var next = ticks[i];
                if (next == null)
                    continue;

                if (next.LastPrice > current.LastPrice)
                    return "B";

                if (next.LastPrice < current.LastPrice)
                    return "S";
            }

            return "B";
        }

        private bool TryParseData(string[] row, DateTime now, out DateTime pTime, out double pPrice, out int pVol)
        {
            pTime = default;
            pPrice = 0;
            pVol = 0;

            string rawPrice = row[0];
            string rawVol = row[1];
            string rawTime = row[2];

            if (string.IsNullOrWhiteSpace(rawTime)) return false;

            // 1. 시간 파싱 로직
            try
            {
                if (rawTime.Length == 14) // yyyyMMddHHmmss
                {
                    if (!DateTime.TryParseExact(rawTime, "yyyyMMddHHmmss", null, System.Globalization.DateTimeStyles.None, out pTime))
                        return false;
                }
                else if (rawTime.Length == 6) // HHmmss (당일 데이터)
                {
                    // 장 시작 전(09시 이전)이면 전일 날짜로 계산
                    string dataDate = now.Hour < 9
                        ? now.AddDays(-1).ToString("yyyyMMdd")
                        : now.ToString("yyyyMMdd");

                    if (!DateTime.TryParseExact(dataDate + rawTime, "yyyyMMddHHmmss", null, System.Globalization.DateTimeStyles.None, out pTime))
                        return false;
                }
                else
                {
                    if (!DateTime.TryParse(rawTime, out pTime))
                        return false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 시간 파싱 실패: {rawTime}, 에러: {ex.Message}");
                return false;
            }

            // 2. 가격 및 거래량 파싱 (부호 제거 및 절대값 처리)
            if (!double.TryParse(rawPrice.Replace("+", "").Replace("-", ""), out pPrice)) return false;
            if (!int.TryParse(rawVol, out pVol)) return false;

            pPrice = Math.Abs(pPrice);
            pVol = Math.Abs(pVol);

            return true;
        }
        private void StartConnectionMonitor()
        {
            AttachApplicationShutdownGuard();

            if (IsKiwoomComShutdownInProgress())
                return;

            if (Interlocked.Exchange(ref _connectionMonitorStarted, 1) == 1)
                return;

            Interlocked.Exchange(ref _connectionMonitorStopped, 0);

            lock (_connectionMonitorLock)
            {
                if (_connectionMonitorTimer != null)
                {
                    try
                    {
                        _connectionMonitorTimer.Change(
                            ConnectionMonitorIntervalMilliseconds,
                            ConnectionMonitorIntervalMilliseconds);
                        return;
                    }
                    catch
                    {
                        _connectionMonitorTimer = null;
                    }
                }

                _connectionMonitorTimer = new System.Threading.Timer(
                    CheckConnectionState,
                    null,
                    ConnectionMonitorIntervalMilliseconds,
                    ConnectionMonitorIntervalMilliseconds);
            }
        }

        private void AttachApplicationShutdownGuard()
        {
            if (Interlocked.Exchange(ref _shutdownGuardAttached, 1) == 1)
                return;

            try
            {
                var app = System.Windows.Application.Current;
                if (app != null)
                {
                    app.Exit += (s, e) => MarkKiwoomComShutdown("ApplicationExit");

                    var dispatcher = app.Dispatcher;
                    if (dispatcher != null)
                        dispatcher.ShutdownStarted += (s, e) => MarkKiwoomComShutdown("DispatcherShutdownStarted");
                }
            }
            catch
            {
            }

            try
            {
                AppDomain.CurrentDomain.ProcessExit += (s, e) => MarkKiwoomComShutdown("ProcessExit");
            }
            catch
            {
            }
        }

        public void BeginShutdown(string reason = "ManualShutdown")
        {
            MarkKiwoomComShutdown(reason);
        }

        private void MarkKiwoomComShutdown(string reason)
        {
            Interlocked.Exchange(ref _kiwoomComShutdownInProgress, 1);
            StopConnectionMonitorForDetachedCom(reason);
        }

        private bool IsKiwoomComShutdownInProgress()
        {
            if (Interlocked.CompareExchange(ref _kiwoomComShutdownInProgress, 0, 0) == 1)
                return true;

            return IsWpfDispatcherShutdownStarted();
        }

        private static bool IsWpfDispatcherShutdownStarted()
        {
            try
            {
                var app = System.Windows.Application.Current;
                var dispatcher = app == null ? null : app.Dispatcher;
                return dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished;
            }
            catch
            {
                return true;
            }
        }

        private void StopConnectionMonitorForDetachedCom(string reason)
        {
            Interlocked.Exchange(ref _connectionMonitorStopped, 1);

            lock (_connectionMonitorLock)
            {
                var timer = _connectionMonitorTimer;
                _connectionMonitorTimer = null;

                if (timer != null)
                {
                    try
                    {
                        timer.Change(Timeout.Infinite, Timeout.Infinite);
                    }
                    catch
                    {
                    }

                    try
                    {
                        timer.Dispose();
                    }
                    catch
                    {
                    }
                }
            }

            Interlocked.Exchange(ref _connectionMonitorStarted, 0);
            Interlocked.Exchange(ref _disconnectHitCount, 0);
            Interlocked.Exchange(ref _connectionLostRaised, 0);
            Interlocked.Exchange(ref _reconnectInProgress, 0);

            if (!string.IsNullOrWhiteSpace(reason) &&
                !reason.Equals("ApplicationExit", StringComparison.OrdinalIgnoreCase) &&
                !reason.Equals("ProcessExit", StringComparison.OrdinalIgnoreCase) &&
                !reason.Equals("DispatcherShutdownStarted", StringComparison.OrdinalIgnoreCase))
            {
                OnLog?.Invoke("SYSTEM", "[키움 연결감시] COM 객체 분리로 감시 중지: " + reason);
            }
        }

        private int GetConnectStateOnDispatcher(AxKHOpenAPI api)
        {
            if (api == null || IsKiwoomComShutdownInProgress())
                return 0;

            var app = System.Windows.Application.Current;
            var dispatcher = app == null ? null : app.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                MarkKiwoomComShutdown("DispatcherShutdown");
                return 0;
            }

            int connectState = 0;

            if (dispatcher.CheckAccess())
            {
                if (api.IsDisposed || !api.IsHandleCreated)
                    return 0;

                return api.GetConnectState();
            }

            dispatcher.Invoke(
                new Action(() =>
                {
                    if (IsKiwoomComShutdownInProgress())
                        return;

                    if (api == null || api.IsDisposed || !api.IsHandleCreated)
                        return;

                    connectState = api.GetConnectState();
                }),
                DispatcherPriority.Send);

            return connectState;
        }

        private void CheckConnectionState(object state)
        {
            if (IsKiwoomComShutdownInProgress())
                return;

            if (Interlocked.CompareExchange(ref _connectionMonitorStopped, 0, 0) == 1)
                return;

            var api = _api;
            if (api == null)
                return;

            int connectState = 0;

            try
            {
                connectState = GetConnectStateOnDispatcher(api);
            }
            catch (System.Runtime.InteropServices.InvalidComObjectException ex)
            {
                Debug.WriteLine($"[키움 연결감시] COM 객체 분리 감지 - 감시 중지: {ex.Message}");
                MarkKiwoomComShutdown("InvalidComObjectException");
                return;
            }
            catch (ObjectDisposedException ex)
            {
                Debug.WriteLine($"[키움 연결감시] ActiveX Dispose 감지 - 감시 중지: {ex.Message}");
                MarkKiwoomComShutdown("ObjectDisposedException");
                return;
            }
            catch (InvalidOperationException ex)
            {
                if (IsKiwoomComShutdownInProgress() || IsWpfDispatcherShutdownStarted())
                {
                    Debug.WriteLine($"[키움 연결감시] Dispatcher 종료 감지 - 감시 중지: {ex.Message}");
                    MarkKiwoomComShutdown("DispatcherInvalidOperation");
                    return;
                }

                Debug.WriteLine($"[키움 연결감시] GetConnectState 실패: {ex.Message}");
                connectState = 0;
            }
            catch (Exception ex)
            {
                if (IsKiwoomComShutdownInProgress() || IsWpfDispatcherShutdownStarted())
                    return;

                Debug.WriteLine($"[키움 연결감시] GetConnectState 실패: {ex.Message}");
                connectState = 0;
            }

            if (IsKiwoomComShutdownInProgress())
                return;

            if (connectState == 1)
            {
                Interlocked.Exchange(ref _hasConnectedOnce, 1);
                Interlocked.Exchange(ref _disconnectHitCount, 0);
                return;
            }

            if (Interlocked.CompareExchange(ref _hasConnectedOnce, 0, 0) == 0)
                return;

            int lostCount = Interlocked.Increment(ref _disconnectHitCount);
            if (lostCount < ConnectionLostConfirmCount)
                return;

            if (Interlocked.CompareExchange(ref _connectionLostRaised, 1, 0) == 0)
            {
                OnLog?.Invoke("SYSTEM", "[재연결] 키움 연결 끊김 감지 - 현재 창 상태 저장/초기화 요청");
                OnConnectionLost?.Invoke();
            }

            TryReconnect();
        }

        private void TryReconnect()
        {
            if (IsKiwoomComShutdownInProgress())
                return;

            if (Interlocked.CompareExchange(ref _reconnectInProgress, 1, 0) != 0)
                return;

            DateTime now = DateTime.Now;
            if ((now - _lastReconnectAttemptAt).TotalSeconds < ReconnectRetrySeconds)
            {
                Interlocked.Exchange(ref _reconnectInProgress, 0);
                return;
            }

            _lastReconnectAttemptAt = now;
            OnLog?.Invoke("SYSTEM", "[재연결] 키움 CommConnect 재시도");

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                Interlocked.Exchange(ref _reconnectInProgress, 0);
                return;
            }

            dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (IsKiwoomComShutdownInProgress())
                    {
                        Interlocked.Exchange(ref _reconnectInProgress, 0);
                        return;
                    }

                    if (_api == null || _api.IsDisposed)
                    {
                        Interlocked.Exchange(ref _reconnectInProgress, 0);
                        return;
                    }

                    if (!_api.IsHandleCreated)
                        _api.CreateControl();

                    _api.CommConnect();
                }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref _reconnectInProgress, 0);
                    OnLog?.Invoke("SYSTEM", $"[재연결] CommConnect 실패: {ex.Message}");
                }
            }));
        }

        public void RestoreRealtimeSubscriptionsAfterReconnect()
        {
            if (_api == null)
                return;

            var codes = _windowSubscriptions.Values
                .Where(x =>
                    x != null &&
                    !string.IsNullOrWhiteSpace(x.Code))
                .Select(x => x.Code.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                if (!IsRealtimeApiReady())
                    return;

                Interlocked.Exchange(ref _marketIndexRegistered, 0);
                RegisterMarketIndexRealtime("SYSTEM");

                // 재연결 후 키움 서버의 실시간 등록은 소실된 상태로 본다.
                // 종목별 소비자 수와 무관하게 각 종목을 전체 FID 58개로 정확히 1회 복원한다.
                _unifiedSymbolRealtimeRegistered.Clear();

                foreach (string code in codes)
                {
                    var representative = GetUnifiedRealtimeRepresentative(code);
                    if (representative == null)
                        continue;

                    int result = EnsureUnifiedSymbolRealtimeRegistration(
                        representative.WindowId ?? "COMMON",
                        code,
                        reason: "ReconnectRestore/UnifiedSymbol",
                        forceReset: true);

                    OnLog?.Invoke(
                        representative.WindowId ?? "COMMON",
                        $"[재연결] 종목별 통합 실시간 재등록: {code} / screen={representative.ScreenNo} / fullFids=Y / result={result}");
                }
            });
        }


        public void RegisterMarketIndexRealtime(string windowId)
        {
            if (Interlocked.Exchange(ref _marketIndexRegistered, 1) == 1)
            {
                OnLog?.Invoke(windowId, "[시장지수] 이미 등록됨");
                return;
            }

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                if (_api == null || _api.IsDisposed || !_api.IsHandleCreated)
                    return;

                string screenNo = "9997";
                string codes = "001;101";        // 001=코스피, 101=코스닥
                string fids = "10;11;12;13;15;20";

                _api.SetRealReg(screenNo, codes, fids, "0");

                OnLog?.Invoke(windowId, "[시장지수] 코스피/코스닥 실시간 등록 완료");
            });
        }
        private DateTime GetPivotTime(DateTime now)
        {
            // 기본적으로 오늘 오전 9시로 설정
            DateTime pivotTime = new DateTime(now.Year, now.Month, now.Day, 9, 0, 0);

            // 1. 장 시작 전(09:00 이전)인 경우 기준을 '전일'로 변경
            if (now.Hour < 7)
            {
                pivotTime = pivotTime.AddDays(-1);
            }

            // 2. 기준일이 주말(토/일)인 경우, 금요일이 나올 때까지 하루씩 뒤로 후퇴
            // (월요일 아침 8시에 켜면 토요일이 아닌 금요일 데이터를 찾아야 하니까!)
            while (pivotTime.DayOfWeek == DayOfWeek.Saturday || pivotTime.DayOfWeek == DayOfWeek.Sunday)
            {
                pivotTime = pivotTime.AddDays(-1);
            }

            return pivotTime;
        }


        public bool Login()
        {
            if (_api == null)
            {
                OnLog?.Invoke("SYSTEM", "❌ 키움 API가 초기화되지 않았습니다.");
                return false;
            }

            if (IsKiwoomComShutdownInProgress())
                return false;

            if (!_api.IsHandleCreated)
                _api.CreateControl();

            _api.CommConnect();
            return true;
        }

        public bool IsConnected()
        {
            if (_api == null || IsKiwoomComShutdownInProgress())
                return false;

            try
            {
                return GetConnectStateOnDispatcher(_api) == 1;
            }
            catch (System.Runtime.InteropServices.InvalidComObjectException)
            {
                MarkKiwoomComShutdown("IsConnectedInvalidComObject");
                return false;
            }
            catch (ObjectDisposedException)
            {
                MarkKiwoomComShutdown("IsConnectedObjectDisposed");
                return false;
            }
            catch
            {
                return false;
            }
        }
        private void Api_OnEventConnect(object sender, _DKHOpenAPIEvents_OnEventConnectEvent e)
        {
            if (IsKiwoomComShutdownInProgress())
                return;

            if (e.nErrCode == 0)
            {
                bool wasConnectionLost = Interlocked.Exchange(ref _connectionLostRaised, 0) == 1;
                bool wasReconnectInProgress = Interlocked.Exchange(ref _reconnectInProgress, 0) == 1;

                Interlocked.Exchange(ref _hasConnectedOnce, 1);
                Interlocked.Exchange(ref _disconnectHitCount, 0);

                OnLog?.Invoke("SYSTEM", wasConnectionLost || wasReconnectInProgress
                    ? "✅ 키움 재연결 성공"
                    : "✅ 로그인 성공!");

                _api.GetConditionLoad();

                if (wasConnectionLost || wasReconnectInProgress)
                    RestoreRealtimeSubscriptionsAfterReconnect();

                OnLogin?.Invoke(true);

                if (wasConnectionLost || wasReconnectInProgress)
                    OnReconnected?.Invoke();

                return;
            }

            Interlocked.Exchange(ref _reconnectInProgress, 0);
            OnLog?.Invoke("SYSTEM", $"❌ 로그인 실패 / 오류코드={e.nErrCode}");
            OnLogin?.Invoke(false);
        }
        private void UpdateTradeData(TrackedSymbol tick) { _trackedSymbols.AddOrUpdate(tick.Code, (k) => tick, (k, existing) => { existing.LastPrice = tick.LastPrice; existing.LastVolume = tick.LastVolume; existing.AccumulatedVolume = tick.AccumulatedVolume; existing.Strength = tick.Strength; existing.LastUpdated = tick.LastUpdated; return existing; }); }
        private void Api_OnReceiveRealCondition(object sender, _DKHOpenAPIEvents_OnReceiveRealConditionEvent e) { OnSymbolDetected?.Invoke(e.strConditionName, e.sTrCode, _api.GetMasterCodeName(e.sTrCode)); }
        private void Api_OnReceiveTrCondition(object sender, _DKHOpenAPIEvents_OnReceiveTrConditionEvent e) { string[] codes = e.strCodeList.Split(';').Where(x => !string.IsNullOrEmpty(x)).ToArray(); foreach (var code in codes) OnSymbolDetected?.Invoke(e.sScrNo, code.Trim(), _api.GetMasterCodeName(code.Trim())); }
        public List<Symbol> GetTotalSymbolList()
        {
            var list = new List<Symbol>();
            string kse = _api.GetCodeListByMarket("0");
            string kdq = _api.GetCodeListByMarket("10");
            var codes = (kse + kdq).Split(';');

            foreach (var code in codes)
            {
                if (string.IsNullOrEmpty(code)) continue;
                string LastPrice = _api.GetMasterLastPrice(code).Trim();
                list.Add(new Symbol { Code = code, Name = _api.GetMasterCodeName(code), LastPrice = LastPrice });
            }
            return list;
        }
        public void UnregisterWindow(string windowId)
        {
            if (string.IsNullOrWhiteSpace(windowId))
                return;

            var targets = _windowSubscriptions
                .Where(x =>
                    x.Value != null &&
                    string.Equals(x.Value.WindowId, windowId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var affectedCodes = targets
                .Where(x => x.Value != null && !string.IsNullOrWhiteSpace(x.Value.Code))
                .Select(x => x.Value.Code.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 먼저 이 창 소비자만 제거한다.
            // 실제 SetRealRemove는 해당 종목의 일반 창/HOLDING 소비자가 모두 사라졌을 때만 실행한다.
            foreach (var item in targets)
                _windowSubscriptions.TryRemove(item.Key, out _);

            foreach (string code in affectedCodes)
                ReleaseUnifiedSymbolRealtimeIfUnused(code, "UnregisterWindow/ConsumerDetached");

            _scheduler?.UnregisterWindow(windowId);

            OnLog?.Invoke(
                windowId,
                $"[실시간 해제] 창 소비자 정리 완료: {windowId} / affectedCodes={affectedCodes.Count}");
        }



        private double SafeSignedDouble(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 0;

            value = value.Replace(",", "").Replace("%", "").Trim();

            double.TryParse(value, out double result);
            return result;
        }

        private long SafeSignedLong(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 0;

            value = value.Replace(",", "").Trim();

            long.TryParse(value, out long result);
            return result;
        }

        private bool IsMarketIndexCode(string code)
        {
            return code == "001" || code == "101";
        }

        private string GetMarketIndexName(string code)
        {
            if (code == "001")
                return "KOSPI";

            if (code == "101")
                return "KOSDAQ";

            return code;
        }
    }
}