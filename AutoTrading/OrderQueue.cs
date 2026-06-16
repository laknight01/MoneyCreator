using MoneyCreator.Service;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace MoneyCreator.AutoTrading
{
    public sealed class OrderQueue : IDisposable
    {
        private readonly ConcurrentQueue<OrderRequest> _queue = new ConcurrentQueue<OrderRequest>();
        private readonly ConcurrentDictionary<string, DateTime> _dedup = new ConcurrentDictionary<string, DateTime>();
        private readonly AutoOrderExecutor _executor;
        private readonly OrderStateManager _stateManager;
        private readonly TradeRecorder _recorder;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private int _running;

        private const int PendingAutoCancelSeconds = 3;

        public OrderQueue(AutoOrderExecutor executor, OrderStateManager stateManager, TradeRecorder recorder)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _stateManager = stateManager ?? new OrderStateManager();
            _recorder = recorder ?? new TradeRecorder();
        }

        public bool Enqueue(OrderRequest request)
        {
            if (request == null || request.Signal == null)
                return false;

            if (string.IsNullOrWhiteSpace(request.DedupKey))
                request.DedupKey = request.Signal.Code + ":" + request.Signal.Action + ":" + request.Signal.StrategyName;

            DateTime now = DateTime.Now;

            DateTime old;
            if (_dedup.TryGetValue(request.DedupKey, out old) &&
                (now - old).TotalSeconds < 3)
            {
                return false;
            }

            _dedup[request.DedupKey] = now;

            // 중요:
            // CanAccept + MarkRequested 분리 금지.
            // 여기서 예수금 예약까지 원자적으로 처리한다.
            if (!_stateManager.TryAcceptAndReserve(request))
                return false;

            _recorder.RecordOrderRequest(request);

            _queue.Enqueue(request);
            StartIfNeeded();

            return true;
        }

        private void StartIfNeeded()
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) == 0)
                Task.Run(ProcessLoop);
        }

        private async Task ProcessLoop()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    OrderRequest req;
                    if (!_queue.TryDequeue(out req))
                        break;

                    int result = await _executor.ExecuteAsync(req);

                    _stateManager.MarkSubmitted(req, result);
                    _recorder.RecordOrderResult(req, result);

                    if (result == 0)
                    {
                        if (req.DryRun)
                        {
                            _stateManager.MarkCompleted(req);
                        }
                        else
                        {
                            StartPendingAutoCancelWatch(req);
                        }
                    }

                    await Task.Delay(250);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);

                if (!_queue.IsEmpty)
                    StartIfNeeded();
            }
        }

        private void StartPendingAutoCancelWatch(OrderRequest request)
        {
            if (request == null || request.Signal == null)
                return;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(PendingAutoCancelSeconds), _cts.Token);

                    if (_cts.IsCancellationRequested)
                        return;

                    string orderNo;
                    int untradedQty;

                    if (!_stateManager.TryGetCancelableOrder(request, out orderNo, out untradedQty))
                        return;

                    int cancelResult = await _executor.CancelAsync(request, orderNo, untradedQty);

                    if (cancelResult == 0)
                    {
                        _stateManager.MarkCancelRequested(request);
                        _recorder.RecordOrderResult(request, cancelResult);
                    }
                }
                catch (TaskCanceledException)
                {
                }
                catch
                {
                }
            });
        }

        public void Clear()
        {
            OrderRequest dummy;
            while (_queue.TryDequeue(out dummy))
            {
            }

            _dedup.Clear();
            _stateManager.Clear();
        }

        public void Dispose()
        {
            _cts.Cancel();
        }
    }
    public sealed class AutoOrderExecutor
    {
        private readonly IMarketApiService _marketApi;
        private readonly Action<string> _log;
        private readonly Func<AutoTradeSettings> _settingsProvider;

        public AutoOrderExecutor(
            IMarketApiService marketApi,
            Action<string> log,
            Func<AutoTradeSettings> settingsProvider)
        {
            _marketApi = marketApi;
            _log = log;
            _settingsProvider = settingsProvider;
        }

        public Task<int> ExecuteAsync(OrderRequest request)
        {
            return Task.FromResult(Execute(request));
        }

        public int Execute(OrderRequest request)
        {
            if (request == null)
            {
                _log?.Invoke("⛔ 자동주문 차단: request null");
                return -9001;
            }

            if (request.Signal == null)
            {
                _log?.Invoke("⛔ 자동주문 차단: signal null");
                return -9002;
            }

            var settings = _settingsProvider?.Invoke();

            if (settings == null)
            {
                _log?.Invoke("⛔ 자동주문 차단: AutoTradeSettings 없음");
                return -9003;
            }

            if (!settings.Enabled)
            {
                _log?.Invoke($"⛔ 자동주문 차단: 자동매매 OFF / {request.Signal.Code}");
                return -9004;
            }

            if (string.IsNullOrWhiteSpace(request.WindowId) ||
                string.IsNullOrWhiteSpace(request.ScreenNo) ||
                string.IsNullOrWhiteSpace(request.AccountNo) ||
                string.IsNullOrWhiteSpace(request.Signal.Code))
            {
                _log?.Invoke("⛔ 자동주문 차단: 주문 필수값 누락");
                return -9005;
            }

            if (request.Signal.Quantity <= 0)
            {
                _log?.Invoke($"⛔ 자동주문 차단: 수량 오류 / {request.Signal.Code} / Qty={request.Signal.Quantity}");
                return -9006;
            }

            int orderPrice = (int)Math.Round(request.Signal.Price);

            if (orderPrice <= 0)
            {
                _log?.Invoke(
                    $"⛔ 자동주문 차단: 가격 0 이하 / {request.Signal.Code} / Action={request.Signal.Action} / Price={orderPrice} / Hoga={request.HogaType}");
                return -9007;
            }

            // 자동매매는 지금 단계에서 무조건 지정가만 허용한다.
            string hogaType = string.IsNullOrWhiteSpace(request.HogaType)
                ? settings.HogaType
                : request.HogaType;

            if (hogaType != "00")
            {
                _log?.Invoke(
                    $"⛔ 자동주문 차단: 자동매매 지정가 외 호가 금지 / {request.Signal.Code} / Hoga={hogaType} / Price={orderPrice}");
                return -9008;
            }

            if (settings.DryRun || request.DryRun)
            {
                _log?.Invoke(
                    $"🧪 DRY RUN 차단: 실제 주문 전송 안 함 / {request.Signal.Code} / {request.Signal.Action} / {request.Signal.Quantity}주 / {orderPrice:N0}원");
                return 0;
            }

            int orderType = request.Signal.Action == TradeActionType.Buy ? 1 : 2;

            _log?.Invoke(
                $"🚀 자동주문 전송 직전: {request.Signal.Code} / {request.Signal.Action} / {request.Signal.Quantity}주 / {orderPrice:N0}원 / Hoga={hogaType}");

            return _marketApi.SendOrder(
                request.WindowId,
                request.ScreenNo,
                request.AccountNo,
                orderType,
                request.Signal.Code,
                request.Signal.Quantity,
                orderPrice,
                hogaType,
                "");
        }

        public Task<int> CancelAsync(OrderRequest request, string orderNo, int untradedQty)
        {
            return Task.FromResult(Cancel(request, orderNo, untradedQty));
        }

        public int Cancel(OrderRequest request, string orderNo, int untradedQty)
        {
            if (request == null || request.Signal == null)
                return -9101;

            var settings = _settingsProvider?.Invoke();

            if (settings == null || !settings.Enabled)
                return -9102;

            if (settings.DryRun || request.DryRun)
                return 0;

            if (string.IsNullOrWhiteSpace(orderNo))
            {
                _log?.Invoke($"⛔ 자동취소 실패: 원주문번호 없음 / {request.Signal.Code}");
                return -9103;
            }

            int qty = untradedQty > 0 ? untradedQty : request.Signal.Quantity;

            if (qty <= 0)
                return -9104;

            int cancelType = request.Signal.Action == TradeActionType.Buy ? 3 : 4;

            _log?.Invoke(
                $"🧯 미체결 자동취소 요청: {request.Signal.Code} / {request.Signal.Action} / 미체결 {qty}주 / 원주문={orderNo}");

            return _marketApi.SendOrder(
                request.WindowId,
                request.ScreenNo,
                request.AccountNo,
                cancelType,
                request.Signal.Code,
                qty,
                0,
                "00",
                orderNo);
        }
    }
}