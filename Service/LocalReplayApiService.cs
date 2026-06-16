using AxKHOpenAPILib;
using MoneyCreator.DataBase;
using MoneyCreator.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
        public event Action<string, List<OrderInfo>> OnOrdersReceived;
        public event Action<string, List<OrderInfo>> OnUnfilledOrdersReceived;
        public event Action<string> OnGlobalUpdateRequired;
        public event Action<string, string, string, string, int, double> OnOrderFilled;
        public event Action<TrackedSymbol> OnInventoryTickReceived;
        public event Action<string, string, List<MinuteCandleRecord>, bool> OnMinuteHistoryReceived;
        public event Action<string, string, List<TrackedSymbol>, bool> OnRecentTicksReceived;
        public event Action<MarketIndexInfo> OnMarketIndexReceived;

        private readonly ConcurrentDictionary<string, string> _subscriptions = new ConcurrentDictionary<string, string>();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _replayTokens = new ConcurrentDictionary<string, CancellationTokenSource>();

        private int _screenSeed = 9000;

        public StockDbManager DbManager { get; }

        public LocalReplayApiService(StockDbManager dbManager = null)
        {
            DbManager = dbManager;
        }

        public string GetScreenNo()
        {
            return Interlocked.Increment(ref _screenSeed).ToString("D4");
        }

        public bool Login()
        {
            OnLog?.Invoke("SYSTEM", "🧪 LocalReplayApiService 연결 완료");
            OnLogin?.Invoke(true);
            return true;
        }

        public bool IsConnected()
        {
            return true;
        }

        public void SetApiInstance(AxKHOpenAPI api)
        {
            // 테스트 모드는 OCX 사용 안 함
        }

        public void ReplaceSubscription(string windowId, string screenNo, string newCode)
        {
            if (string.IsNullOrWhiteSpace(windowId) || string.IsNullOrWhiteSpace(newCode))
                return;

            _subscriptions[windowId] = newCode;

            OnLog?.Invoke(windowId, $"🧪 테스트 실시간 구독 등록: {newCode}");

            // 나중에 여기서 실시간 재생 시작 가능
            // StartReplay(windowId, newCode);
        }

        public void RegisterInventoryTracking(string windowId, string code, string screenNo)
        {
            if (string.IsNullOrWhiteSpace(code))
                return;

            _subscriptions[$"HOLDING:{code}"] = code;
            OnLog?.Invoke(windowId, $"🧪 테스트 보유주 감시 등록: {code}");
        }

        public void UnregisterWindow(string windowId)
        {
            _subscriptions.TryRemove(windowId, out _);

            if (_replayTokens.TryRemove(windowId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
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
            _ = Task.Run(async () =>
            {
                var candles = await LoadDailyMinuteCandlesAsync(code);

                OnMinuteHistoryReceived?.Invoke(
                    windowId,
                    code,
                    candles,
                    true);

                OnLog?.Invoke(windowId, $"🧪 테스트 분봉 로드 완료: {code} / {candles.Count}개");
            });
        }

        public void RequestRecentTicks(string windowId, string code, string screenNo, int minutes = 10, int priority = 1)
        {
            _ = Task.Run(async () =>
            {
                var ticks = await LoadRecentTicksAsync(code, minutes);

                OnRecentTicksReceived?.Invoke(
                    windowId,
                    code,
                    ticks,
                    true);

                OnLog?.Invoke(windowId, $"🧪 테스트 최근틱 로드 완료: {code} / {ticks.Count}개");
            });
        }

        public void RequestAccountHoldings(string windowId, string accountNo, string screenNo, int priority = 1)
        {
            OnHoldingsReceived?.Invoke(windowId, new List<HoldingInfo>());
        }

        public void RequestCashInfo(string windowId, string accountNo, string screenNo = "1000")
        {
            OnBalanceUpdated?.Invoke(windowId, 10_000_000);
        }

        public void RequestOrderHistory(string windowId, string accountNo, string screenNo, int next = 0)
        {
            OnOrdersReceived?.Invoke(windowId, new List<OrderInfo>());
        }

        public void RequestUnfilledOrders(string windowId, string accountNo, string screenNo)
        {
            OnUnfilledOrdersReceived?.Invoke(windowId, new List<OrderInfo>());
        }

        public int SendOrder(string windowId, string screenNo, string accountNo, int orderType, string code, int qty, int price, string hogaType, string orgOrderNo = "")
        {
            OnLog?.Invoke(windowId, $"🧪 테스트 주문 수신: {code} / {qty}주 / {price}원");
            return 0;
        }

        public List<Symbol> GetTotalSymbolList()
        {
            // TODO: DB 또는 JSON에서 종목 리스트 로드
            return new List<Symbol>();
        }

        private Task<List<MinuteCandleRecord>> LoadDailyMinuteCandlesAsync(string code)
        {
            // TODO:
            // 1. DB에서 해당 종목의 최신 거래일 분봉 전체 조회
            // 2. Source = "HIST"
            // 3. CandleTime 오름차순 반환

            return Task.FromResult(new List<MinuteCandleRecord>());
        }

        private Task<List<TrackedSymbol>> LoadRecentTicksAsync(string code, int minutes)
        {
            // TODO:
            // 1. DB에서 해당 종목 최신 거래일 실시간틱 전체 또는 최근 N분 조회
            // 2. TrackedSymbol로 변환
            // 3. LastUpdated 오름차순 반환

            return Task.FromResult(new List<TrackedSymbol>());
        }

        private async Task StartReplayAsync(string windowId, string code, int delayMs = 100)
        {
            if (_replayTokens.TryRemove(windowId, out var old))
            {
                old.Cancel();
                old.Dispose();
            }

            var cts = new CancellationTokenSource();
            _replayTokens[windowId] = cts;

            var ticks = await LoadDailyTicksAsync(code);

            foreach (var tick in ticks.OrderBy(x => x.LastUpdated))
            {
                if (cts.IsCancellationRequested)
                    break;

                OnTickReceived?.Invoke(windowId, tick);

                OnRealPriceReceived?.Invoke(
                    code,
                    windowId,
                    tick.LastPrice,
                    (int)tick.LastVolume,
                    tick.LastUpdated);

                await Task.Delay(delayMs, cts.Token);
            }
        }

        private Task<List<TrackedSymbol>> LoadDailyTicksAsync(string code)
        {
            // TODO:
            // 최초 테스트 재생용 하루치 실시간틱 전체 로드
            return Task.FromResult(new List<TrackedSymbol>());
        }

        public void RegisterMarketIndexRealtime(string windowId)
        {
            // TODO: 시장 지수 실시간 등록 로직 구현
        }
    }
}