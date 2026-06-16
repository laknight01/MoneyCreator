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
        private readonly ConcurrentDictionary<string, string> _orderWindowMap = new ConcurrentDictionary<string, string>();

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
        public event Action<string, List<OrderInfo>> OnOrdersReceived; // 주문내역 정보 전달
        public event Action<string, List<OrderInfo>> OnUnfilledOrdersReceived; // 미체결 주문내역 정보 전달
        public event Action<string> OnGlobalUpdateRequired; //주문
        public event Action<string, string, string, string, int, double> OnOrderFilled;
        public event Action<MarketIndexInfo> OnMarketIndexReceived;
        // code, name, filledQty, filledPrice
        public event Action<TrackedSymbol> OnInventoryTickReceived; //보유주 정보담기
        // ============================================================
        // [추가] 이벤트: 기존 OnInitialHistoryReceived 아래쪽에 추가
        // ============================================================
        public event Action<string, string, List<MinuteCandleRecord>, bool> OnMinuteHistoryReceived;
        public event Action<string, string, List<TrackedSymbol>, bool> OnRecentTicksReceived;

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

        private int _marketIndexRegistered = 0;
        private int _activeTrCount = 0;
        public int ActiveTrCount => _activeTrCount;

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
                   requestType == "RECENT_TICKS";
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
            _api = api;
            _api.OnEventConnect += Api_OnEventConnect;
            _api.OnReceiveTrCondition += Api_OnReceiveTrCondition;
            _api.OnReceiveRealData += Api_OnReceiveRealData;
            _api.OnReceiveRealCondition += Api_OnReceiveRealCondition;
            _api.OnReceiveTrData += Api_OnReceiveTrData;
            _api.OnReceiveChejanData += Api_OnReceiveChejanData;

        }
        private void Api_OnReceiveChejanData(object sender, _DKHOpenAPIEvents_OnReceiveChejanDataEvent e)
        {
            string code = _api.GetChejanData(9001).Replace("A", "").Trim();
            string name = _api.GetChejanData(302).Trim();

            if (e.sGubun == "0")
            {
                string status = _api.GetChejanData(913).Trim();
                string orderType = _api.GetChejanData(905).Trim();

                int.TryParse(_api.GetChejanData(911).Trim(), out int totalFilledQty);
                int.TryParse(_api.GetChejanData(911).Trim(), out int filledQty);
                int.TryParse(_api.GetChejanData(902).Trim(), out int remainQty);
                double.TryParse(_api.GetChejanData(910).Trim(), out double filledPrice);

                string orderNo = _api.GetChejanData(9203).Trim();
                string rawOrderTime = _api.GetChejanData(908).Trim();

                int.TryParse(_api.GetChejanData(900).Trim(), out int orderQty);
                double.TryParse(_api.GetChejanData(901).Trim(), out double orderPrice);

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
                int.TryParse(_api.GetChejanData(930).Trim(), out int qty);
                double.TryParse(_api.GetChejanData(931).Trim(), out double avgPrice);
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

        public void ReplaceSubscription(string windowId, string screenNo, string newCode)
        {
            if (string.IsNullOrWhiteSpace(windowId) ||
                string.IsNullOrWhiteSpace(newCode))
                return;

            string token = GetOrCreateSymbolToken(newCode);

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                if (_api == null || _api.IsDisposed || !_api.IsHandleCreated)
                    return;

                if (_windowSubscriptions.TryGetValue(windowId, out var existing))
                {
                    if (existing.Code == newCode)
                        return;

                    _api.SetRealRemove(existing.ScreenNo, existing.Code);
                    Debug.WriteLine($"🔻 실시간 해제: {existing.Code} (창ID: {windowId}, token:{existing.ScreenNo})");
                }

                // 체결 + 호가 FID 같이 등록
                string fids =
                    "10;11;12;13;15;20;228;" +
                    "41;42;43;44;45;46;47;48;49;50;" +
                    "51;52;53;54;55;56;57;58;59;60;" +
                    "61;62;63;64;65;66;67;68;69;70;" +
                    "71;72;73;74;75;76;77;78;79;80;" +
                    "121;125";

                _api.SetRealReg(token, newCode, fids, "1");

                _windowSubscriptions[windowId] = new RealSubscription
                {
                    WindowId = windowId,
                    Code = newCode,
                    ScreenNo = token
                };

                Debug.WriteLine($"📡 실시간/호가 등록: {newCode} (창ID: {windowId}, token:{token})");
            });
        }
        private readonly ConcurrentDictionary<string, HashSet<(string ScreenNo, string WindowId)>> _realTimeMap = new ConcurrentDictionary<string, HashSet<(string ScreenNo, string WindowId)>>();

        public void RegisterInventoryTracking(string windowId, string code, string screenNo)
        {
            if (string.IsNullOrWhiteSpace(code))
                return;

            string token = GetOrCreateSymbolToken(code);

            // 종목별 1회만 실시간 등록
            if (!_inventoryRegistered.TryAdd(code, true))
            {
                OnLog?.Invoke(windowId, $"[잔고감시] {code} 이미 등록됨 token={token}");
                return;
            }

            string inventoryKey = $"HOLDING:{code}";

            _windowSubscriptions[inventoryKey] = new RealSubscription
            {
                WindowId = "COMMON",
                Code = code,
                ScreenNo = token
            };

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                if (_api == null || _api.IsDisposed || !_api.IsHandleCreated)
                    return;

                string inventoryFids = "10;12;11;13;15;20;121;125;228";
                _api.SetRealReg(token, code, inventoryFids, "1");

                OnLog?.Invoke(windowId, $"[잔고감시] {code} 전역 실시간 등록 완료 token={token}");
            });
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

                OnMarketIndexReceived?.Invoke(index);
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
                            DateTime.Now
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

                foreach (var pair in _windowSubscriptions)
                {
                    var sub = pair.Value;

                    if (sub.Code != code || sub.ScreenNo == "9999")
                        continue;

                    ParseAndSendHoga(sub.WindowId, code, lastPrice);
                }
            }
            if (e.sRealType == "주식예상체결")
            {
                OnLog?.Invoke("COMMON", $"[예상체결] {code} / 단일가 또는 거래중단 구간 예상체결 수신");
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

        // [추가] 최근 틱 윈도우 계산
        // - 09:00~09:10 사이면 전일 15:11~15:20
        // - 그 외 장중이면 현재시간 - N분 ~ 현재시간
        private TickWindowRange GetRecentTickWindow(DateTime now, int minutes)
        {
            DateTime marketOpen = new DateTime(now.Year, now.Month, now.Day, 9, 0, 0);
            DateTime openingLimit = marketOpen.AddMinutes(minutes);

            if (now >= marketOpen && now < openingLimit)
            {
                DateTime prev = GetPreviousTradingDay(now.Date);
                return new TickWindowRange
                {
                    Start = new DateTime(prev.Year, prev.Month, prev.Day, 15, 11, 0),
                    End = new DateTime(prev.Year, prev.Month, prev.Day, 15, 20, 59),
                    IsPreviousCloseWindow = true
                };
            }

            return new TickWindowRange
            {
                Start = now.AddMinutes(-minutes),
                End = now,
                IsPreviousCloseWindow = false
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

        private void ParseAndSendHoga(string windowId, string code, double currentPrice)
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

        private void ProcessOrderHistoryData(string windowId, string accountNo, string screenNo, _DKHOpenAPIEvents_OnReceiveTrDataEvent e)
        {
            var orders = ParseOrderRows(e);

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
                finalOrders = buffer
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.OrderNo))
                    .GroupBy(x => x.OrderNo)
                    .Select(g => g
                        .OrderByDescending(x => x.ExecQty)
                        .ThenByDescending(x => x.UntradedQty)
                        .First())
                    .OrderByDescending(x => x.OrderTime)
                    .ToList();
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

                orders.Add(new OrderInfo
                {
                    OrderNo = rawOrderNo,
                    ParentOrderNo = _api.GetCommData(e.sTrCode, e.sRQName, i, "원주문번호").Trim(),
                    Code = rawCode,
                    Name = name,
                    OrderTime = NormalizeOrderTime(_api.GetCommData(e.sTrCode, e.sRQName, i, "주문시간").Trim()),
                    Status = _api.GetCommData(e.sTrCode, e.sRQName, i, "주문상태").Trim(),
                    OrderType = _api.GetCommData(e.sTrCode, e.sRQName, i, "주문구분").Trim(),

                    Qty = SafeInt(_api.GetCommData(e.sTrCode, e.sRQName, i, "주문수량")),
                    ExecQty = SafeInt(_api.GetCommData(e.sTrCode, e.sRQName, i, "체결수량")),
                    Price = SafeDouble(_api.GetCommData(e.sTrCode, e.sRQName, i, "주문단가")),
                    ExecPrice = SafeDouble(_api.GetCommData(e.sTrCode, e.sRQName, i, "체결단가")),
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

                    var tod = candle.CandleTime.TimeOfDay;

                    // 장중 분봉만 사용
                    if (tod < new TimeSpan(9, 0, 0) ||
                        tod > new TimeSpan(15, 20, 0))
                        continue;

                    candles.Add(candle);
                }
            });

            if (candles.Count > 0)
                _dbManager.EnqueueMinuteCandles(candles);

            bool hasNext = e.sPrevNext == "2";

            bool isMarketOpenNow =
                DateTime.Now.TimeOfDay >= new TimeSpan(9, 0, 0) &&
                DateTime.Now.TimeOfDay <= new TimeSpan(15, 20, 0);

            // 장중에는 2일치, 장마감 후에는 당일 1일치만 수집한다.
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
            int safeMinutes = Math.Max(1, minutes);
            TickWindowRange range = GetRecentTickWindow(now, safeMinutes);

            await Task.Run(() =>
            {
                foreach (var row in rows)
                {
                    if (!TryParseRecentTickRow(row, range, now, out DateTime tickTime, out double tickPrice, out int tickVol))
                        continue;

                    // 원하는 10분 구간 밖은 버린다.
                    if (tickTime > range.End)
                        continue;

                    if (tickTime < range.Start)
                    {
                        shouldStop = true;
                        break;
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
                        Side = tickVol >= 0 ? "B" : "S"
                    });
                }

                ticks = ticks
                    .GroupBy(x => new { x.Code, x.LastUpdated, x.LastPrice, x.LastVolume })
                    .Select(g => g.First())
                    .OrderBy(x => x.LastUpdated)
                    .ToList();
            });

            bool hasNext = e.sPrevNext == "2";
            bool isLast = !hasNext || shouldStop;

            OnRecentTicksReceived?.Invoke(windowId, currentCode, ticks, isLast);

            if (hasNext && !shouldStop)
                RequestRecentTicksCore(windowId, currentCode, screenNo, safeMinutes, priority: 2, next: 2);

            // RECENT_TICKS 요청 1회에 대한 응답 처리 완료.
            _scheduler?.NotifyResponseReceived(windowId, currentCode, "RECENT_TICKS");
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

            await Task.Run(() =>
            {
                DateTime now = DateTime.Now;

                if (!TryParseData(rawDataRows[0], now, out DateTime firstDataTime, out _, out _))
                    return;

                DateTime pivotTime = new DateTime(
                    firstDataTime.Year,
                    firstDataTime.Month,
                    firstDataTime.Day,
                    9, 0, 0);

                TimeSpan endLimit = new TimeSpan(15, 20, 0);

                foreach (var row in rawDataRows)
                {
                    if (!TryParseData(row, now, out DateTime pTime, out double pPrice, out int pVol))
                        continue;

                    // 오늘 장 시작 이전 데이터가 나오면 더 이상 과거 조회 중단
                    if (pTime < pivotTime)
                    {
                        shouldStop = true;
                        break;
                    }

                    // 15:20 이후 데이터는 무시
                    if (pTime.TimeOfDay >= endLimit)
                        continue;

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

                currentTrHistory = currentTrHistory.OrderBy(x => x.LastUpdated).ToList();
            });

            bool hasNext = e.sPrevNext == "2";

            // 중요:
            // islast는 "다음 페이지가 없음" 또는 "오늘 09:00 이전까지 도달해서 중단"이면 true
            bool isLast = !hasNext || shouldStop;

            OnInitialHistoryReceived?.Invoke(windowId, currentTrHistory, isLast);

            if (hasNext && !shouldStop)
            {
                RequestPreviousData(windowId, currentCode, 2, screenNo, 2);
            }

            // PREV 요청 1회에 대한 응답 처리 완료.
            _scheduler?.NotifyResponseReceived(windowId, currentCode, "PREV");
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

            if (!_api.IsHandleCreated)
                _api.CreateControl();

            _api.CommConnect();
            return true;
        }

        public bool IsConnected()
        {
            return _api != null && _api.GetConnectState() == 1;
        }
        private void Api_OnEventConnect(object sender, _DKHOpenAPIEvents_OnEventConnectEvent e) { if (e.nErrCode == 0) { OnLog?.Invoke("SYSTEM", "✅ 로그인 성공!"); _api.GetConditionLoad(); OnLogin?.Invoke(true); } }
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
                .Where(x => x.Value != null && x.Value.WindowId == windowId)
                .ToList();

            foreach (var item in targets)
            {
                var sub = item.Value;

                _api?.SetRealRemove(sub.ScreenNo, sub.Code);
                _windowSubscriptions.TryRemove(item.Key, out _);

                bool stillUsed = _windowSubscriptions.Values.Any(x => x.Code == sub.Code);

                if (!stillUsed)
                {
                    _inventoryRegistered.TryRemove(sub.Code, out _);
                    ReleaseSymbolToken(sub.Code);
                }
            }

            _scheduler?.UnregisterWindow(windowId);

            OnLog?.Invoke(windowId, $"[실시간 해제] 창 종료 정리 완료: {windowId}");
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