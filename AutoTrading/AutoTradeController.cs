using MoneyCreator.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static MoneyCreator.Models.MarketIndexInfo;

namespace MoneyCreator.AutoTrading
{
    public sealed class AutoTradeController : IDisposable
    {
        private readonly MarketSnapshotBuilder _snapshotBuilder;
        private readonly FeatureEngine _featureEngine;
        private readonly CandleAggregator _candleAggregator;
        private readonly SignalEngine _signalEngine;
        private readonly IAiSignalFilter _aiFilter;
        private readonly IMlNetSignalFilter _mlFilter;
        private readonly OrderRequestFactory _orderFactory;
        private readonly OrderQueue _orderQueue;
        private readonly OrderStateManager _orderStateManager;
        private readonly TradeRecorder _recorder;
        private readonly OutcomeLabelTracker _outcomeLabelTracker;
        private readonly Func<string, int> _getHoldingQty;
        private readonly Func<string, double> _getEntryPrice;
        private readonly Func<string, IEnumerable<TrackedSymbol>> _getMinuteCandles;
        private readonly Func<IEnumerable<OrderInfo>> _getOpenOrders;
        private readonly Action<string> _log;
        private const int MaxAutoTradeTickQueueCount = 3;

        private readonly ConcurrentQueue<TrackedSymbol> _tickQueue = new ConcurrentQueue<TrackedSymbol>();
        private readonly ConcurrentDictionary<string, HoldingAnalysisState> _holdingAnalysisStates = new ConcurrentDictionary<string, HoldingAnalysisState>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private int _running;
        private readonly ConcurrentDictionary<string, bool> _seededMinuteCodes = new ConcurrentDictionary<string, bool>();
        private readonly ConcurrentDictionary<string, DateTime> _lastTiRexPredictionTimes = new ConcurrentDictionary<string, DateTime>();
        private readonly ConcurrentDictionary<string, DateTime> _lastTiRexMinuteTimes = new ConcurrentDictionary<string, DateTime>();

        private sealed class HoldingAnalysisState
        {
            public DateTime EntryDetectedTime { get; set; }
            public double MaxProfitRate { get; set; }
            public double MinProfitRate { get; set; }
            public int HoldTickCount { get; set; }
        }

        private sealed class MinuteCacheState
        {
            public List<TrackedSymbol> Minutes { get; set; }
            public int Count { get; set; }
            public DateTime LastMinuteTime { get; set; }
        }

        private readonly ConcurrentDictionary<string, MinuteCacheState> _minuteCache = new ConcurrentDictionary<string, MinuteCacheState>();

        public AutoTradeSettings Settings { get; private set; }

        public AutoTradeController(
            AutoTradeSettings settings,
            MarketSnapshotBuilder snapshotBuilder,
            FeatureEngine featureEngine,
            CandleAggregator candleAggregator,
            SignalEngine signalEngine,
            IAiSignalFilter aiFilter,
            IMlNetSignalFilter mlFilter,
            OrderRequestFactory orderFactory,
            OrderQueue orderQueue,
            OrderStateManager orderStateManager,
            TradeRecorder recorder,
            Func<string, int> getHoldingQty,
            Func<string, double> getEntryPrice,
            Func<string, IEnumerable<TrackedSymbol>> getMinuteCandles,
            Func<IEnumerable<OrderInfo>> getOpenOrders,
            Action<string> log)
        {
            Settings = settings ?? new AutoTradeSettings();
            _snapshotBuilder = snapshotBuilder ?? new MarketSnapshotBuilder();
            _featureEngine = featureEngine ?? new FeatureEngine();
            _candleAggregator = candleAggregator ?? new CandleAggregator();
            _signalEngine = signalEngine;
            _aiFilter = aiFilter;
            _mlFilter = mlFilter;
            _orderFactory = orderFactory;
            _orderQueue = orderQueue;
            _orderStateManager = orderStateManager;
            _recorder = recorder;
            _outcomeLabelTracker = new OutcomeLabelTracker(_recorder, () => Settings);
            _getHoldingQty = getHoldingQty;
            _getEntryPrice = getEntryPrice;
            _getMinuteCandles = getMinuteCandles;
            _getOpenOrders = getOpenOrders;
            _log = log;
        }
        private async Task TryUpdateTiRexPredictionAsync(
            FeatureSnapshot feature,
            List<TrackedSymbol> minuteList)
        {
            if (feature == null || string.IsNullOrWhiteSpace(feature.Code))
                return;

            if (minuteList == null || minuteList.Count == 0)
                return;

            if (Settings == null || !Settings.Enabled || !Settings.UseMlNet)
                return;

            var tirex = _mlFilter as TiRexHttpSignalFilter;
            if (tirex == null)
                return;

            var lastMinute = minuteList
                .Where(x => x != null && x.LastPrice > 0)
                .OrderBy(x => x.LastUpdated)
                .LastOrDefault();

            if (lastMinute == null)
                return;

            DateTime minuteKey = new DateTime(
                lastMinute.LastUpdated.Year,
                lastMinute.LastUpdated.Month,
                lastMinute.LastUpdated.Day,
                lastMinute.LastUpdated.Hour,
                lastMinute.LastUpdated.Minute,
                0);

            DateTime prevMinute;
            if (_lastTiRexMinuteTimes.TryGetValue(feature.Code, out prevMinute))
            {
                if (prevMinute == minuteKey)
                    return;
            }

            _lastTiRexMinuteTimes[feature.Code] = minuteKey;

            await tirex.PredictOnlyAsync(feature.Code, minuteList);
        }
        public void UpdateSettings(AutoTradeSettings settings)
        {
            if (settings != null)
                Settings = settings;
        }

        public void UpdateCash(long cash)
        {
            if (cash < 0)
                cash = 0;

            // 중요:
            // 예수금은 자동매매 OFF 상태에서도 항상 갱신되어야 한다.
            // Settings.Enabled 체크 금지.
            _snapshotBuilder?.UpdateCash(cash);
        }

        public void UpdateHoga(HogaData hoga)
        {
            if (Settings == null || !Settings.Enabled)
                return;

            _snapshotBuilder.UpdateHoga(hoga);
        }

        public void UpdateOrders(IEnumerable<OrderInfo> orders)
        {
            if (Settings == null || !Settings.Enabled)
                return;

            _orderStateManager?.UpdateFromOrders(orders);
        }

        public void MarkSellCompleted(string code)
        {
            _signalEngine?.MarkSellCompleted(code);
        }

        public void Reset(string code = null)
        {
            TrackedSymbol dummy;
            while (_tickQueue.TryDequeue(out dummy))
            {
            }

            _orderQueue?.Clear();

            _featureEngine.Reset();
            _candleAggregator.Reset();
            _snapshotBuilder.Reset(code);
            _signalEngine?.Reset(code);

            if (!string.IsNullOrWhiteSpace(code))
            {
                _holdingAnalysisStates.TryRemove(code, out _);
                _lastMlSnapshotTimes.TryRemove(code, out _);

                MinuteCacheState removedCache;
                _minuteCache.TryRemove(code, out removedCache);

                _outcomeLabelTracker?.Reset(code);
            }
            else
            {
                _holdingAnalysisStates.Clear();
                _lastMlSnapshotTimes.Clear();
                _minuteCache.Clear();

                _outcomeLabelTracker?.Reset();
            }

            if (!string.IsNullOrWhiteSpace(code))
                _log?.Invoke("[자동매매 초기화] " + code);
        }

        public void SeedMinuteCandles(IEnumerable<TrackedSymbol> minutes)
        {
            _candleAggregator.SeedMinutes(minutes);
        }

        public void EnqueueTick(TrackedSymbol tick)
        {
            if (tick == null || Settings == null || !Settings.Enabled)
                return;

            // 자동매매는 모든 틱을 순서대로 처리할 필요가 없다.
            // 처리 지연 시 오래된 틱은 버리고 최신 틱 중심으로 판단한다.
            while (_tickQueue.Count >= MaxAutoTradeTickQueueCount)
            {
                Debug.WriteLine($"[자동매매큐] 큐 초과 정리: count={_tickQueue.Count}");
                TrackedSymbol old;
                _tickQueue.TryDequeue(out old);
            }
            _tickQueue.Enqueue(CloneTick(tick));
            StartIfNeeded();
        }
        public void UpdateMarketEnvironment(MarketEnvironment environment)
        {
            if (environment == null)
                return;

            _snapshotBuilder.UpdateMarketEnvironment(environment);
        }
        public void SeedRecentTicks(IEnumerable<TrackedSymbol> ticks)
        {
            if (ticks == null)
                return;

            var list = ticks
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Code))
                .OrderBy(x => x.LastUpdated)
                .ToList();

            if (list.Count == 0)
                return;

            _snapshotBuilder.SeedRecentTicks(list);
            _candleAggregator.SeedRecentTicks(list);

            _log?.Invoke($"[자동매매] 최근틱 시드 완료: {list[0].Code} / {list.Count}개");
        }
        private static TrackedSymbol CloneTick(TrackedSymbol x)
        {
            if (x == null)
                return null;

            return new TrackedSymbol
            {
                Code = x.Code,
                LastPrice = x.LastPrice,
                LastVolume = x.LastVolume,
                ClosePrice = x.ClosePrice,
                HighPrice = x.HighPrice,
                LowPrice = x.LowPrice,
                AccumulatedVolume = x.AccumulatedVolume,
                TotalAskQty = x.TotalAskQty,
                TotalBidQty = x.TotalBidQty,
                Strength = x.Strength,
                Side = x.Side,
                LastUpdated = x.LastUpdated
            };
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
                    if (Settings == null || !Settings.Enabled)
                    {
                        TrackedSymbol dummy;
                        while (_tickQueue.TryDequeue(out dummy))
                        {
                        }

                        break;
                    }

                    TrackedSymbol tick;
                    if (!_tickQueue.TryDequeue(out tick))
                        break;

                    await ProcessTickAsync(tick);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);

                if (Settings != null &&
                    Settings.Enabled &&
                    !_tickQueue.IsEmpty)
                {
                    StartIfNeeded();
                }
            }
        }

        private async Task ProcessTickAsync(TrackedSymbol tick)
        {
            if (tick == null || string.IsNullOrWhiteSpace(tick.Code))
                return;

            if (Settings == null || !Settings.Enabled)
                return;

            _outcomeLabelTracker?.OnTick(tick);

            IEnumerable<TrackedSymbol> minuteCandles = SafeMinutes(tick.Code);
            List<TrackedSymbol> minuteList = minuteCandles == null
                ? new List<TrackedSymbol>()
                : minuteCandles
                    .Where(x => x != null && x.Code == tick.Code)
                    .OrderBy(x => x.LastUpdated)
                    .ToList();

            if (Settings == null || !Settings.Enabled)
                return;

            if (minuteList.Count > 0 && !_seededMinuteCodes.ContainsKey(tick.Code))
            {
                _candleAggregator.SeedMinutes(minuteList);
                _seededMinuteCodes[tick.Code] = true;

                _log?.Invoke($"[자동매매] 과거분봉 시드 완료: {tick.Code} / {minuteList.Count}개");
            }

            MarketFrameSnapshot frame = _candleAggregator.Update(tick);
            if (frame == null || !frame.IsEnoughData)
            {
                LogTickWaiting(tick, $"초/분봉 누적 부족 / minute={minuteList.Count}");
                return;
            }

            if (Settings == null || !Settings.Enabled)
                return;

            int holdingQty = SafeHoldingQty(tick.Code);
            double entryPrice = SafeEntryPrice(tick.Code);
            IEnumerable<OrderInfo> openOrders = SafeOrders();

            MarketSnapshot snapshot = _snapshotBuilder.Build(
                tick,
                frame,
                minuteList,
                holdingQty,
                entryPrice,
                openOrders);

            FeatureSnapshot feature = _featureEngine.GenerateSnapshotFeature(snapshot);
            if (feature == null)
            {
                LogTickWaiting(tick, "Feature 생성 실패");
                return;
            }

            // 원본 snapshot 객체는 feature 생성 이후 바로 참조 해제한다.
            snapshot = null;
            DetachHeavySnapshot(feature);

            UpdateHoldingAnalysis(feature);

            await TryUpdateTiRexPredictionAsync(
                feature,
                minuteList);

            if (Settings == null || !Settings.Enabled)
                return;

            TradeSignal signal = _signalEngine?.Evaluate(feature, Settings);
            ApplySignalToFeature(feature, signal);
            RecordMlSnapshotIfNeeded(feature, signal);

            LogTickAnalysis(feature, signal, "SignalEngine 평가");

            if (signal == null || signal.Action == TradeActionType.Hold)
                return;

            if (Settings == null || !Settings.Enabled)
                return;

            if (!IsSignalAllowedByPosition(signal, feature))
                return;

            if (_mlFilter != null && Settings.UseMlNet)
            {
                signal = await _mlFilter.ReviewAsync(signal, feature, Settings);

                if (Settings == null || !Settings.Enabled)
                    return;

                ApplySignalToFeature(feature, signal);

                if (signal == null || signal.Action == TradeActionType.Hold)
                {
                    RecordMlSnapshotIfNeeded(feature, signal);
                    return;
                }

                if (!IsSignalAllowedByPosition(signal, feature))
                    return;
            }

            if (_aiFilter != null && Settings.UseAi)
            {
                signal = await _aiFilter.ReviewAsync(signal, feature, Settings);

                if (Settings == null || !Settings.Enabled)
                    return;

                ApplySignalToFeature(feature, signal);

                if (signal == null || signal.Action == TradeActionType.Hold)
                    return;

                if (!IsSignalAllowedByPosition(signal, feature))
                    return;
            }

            if (Settings == null || !Settings.Enabled)
                return;

            ApplySignalToFeature(feature, signal);

            NormalizeSignalQuantity(signal, feature);

            _recorder?.RecordSignal(signal, feature);

            // 주문 요청으로 넘어가기 전에 FeatureSnapshot 참조를 반드시 제거한다.
            signal.SnapshotFeature = null;

            OrderRequest request = _orderFactory?.Create(signal, feature, Settings);
            if (request == null)
                return;

            if (Settings == null || !Settings.Enabled)
                return;

            bool enqueued = _orderQueue.Enqueue(request);
            if (enqueued)
                _snapshotBuilder.MarkOrder(signal.Code);
        }

        private void DetachHeavySnapshot(FeatureSnapshot feature)
        {
            if (feature == null)
                return;

            feature.Snapshot = null;
        }

        private readonly ConcurrentDictionary<string, DateTime> _lastMlSnapshotTimes =
            new ConcurrentDictionary<string, DateTime>();

        private void ApplySignalToFeature(FeatureSnapshot feature, TradeSignal signal)
        {
            if (feature == null)
                return;

            feature.SignalAction = signal == null ? "NULL" : signal.Action.ToString();
            feature.SignalScore = signal == null ? 0 : signal.Score;
            feature.StrategyName = string.IsNullOrWhiteSpace(signal?.StrategyName) ? "-" : signal.StrategyName;
            feature.SignalReason = signal == null ? null : signal.Reason;

            // 중요:
            // TradeSignal이 FeatureSnapshot을 들고 다니면
            // OrderRequest/OrderStateManager/자동취소 Task까지 원본 분석 객체가 살아남을 수 있다.
            if (signal != null)
                signal.SnapshotFeature = null;
        }

        private void RecordMlSnapshotIfNeeded(FeatureSnapshot feature, TradeSignal signal)
        {
            if (feature == null || string.IsNullOrWhiteSpace(feature.Code))
                return;

            if (Settings != null && !Settings.EnableMlDataCollection)
            {
                _log?.Invoke("[ML수집] EnableMlDataCollection=false");
                return;
            }

            DateTime now = feature.Time == default(DateTime) ? DateTime.Now : feature.Time;
            int interval = Settings == null || Settings.FeatureSnapshotIntervalSeconds <= 0
                ? 2
                : Settings.FeatureSnapshotIntervalSeconds;

            bool isTradeSignal = signal != null && signal.Action != TradeActionType.Hold;
            bool isHolding = feature.HasPosition || feature.HoldingQty > 0;
            bool shouldRecord = isTradeSignal || isHolding;

            if (!shouldRecord)
            {
                DateTime last;
                shouldRecord = !_lastMlSnapshotTimes.TryGetValue(feature.Code, out last) ||
                               (now - last).TotalSeconds >= interval;
            }

            if (!shouldRecord)
                return;

            _lastMlSnapshotTimes[feature.Code] = now;
            _recorder?.RecordFeature(feature);
            _outcomeLabelTracker?.Track(feature);
        }

        private void UpdateHoldingAnalysis(FeatureSnapshot feature)
        {
            if (feature == null || string.IsNullOrWhiteSpace(feature.Code))
                return;

            var t = DynamicThresholdProvider.Create(feature, Settings);
            feature.DynamicStopLossRate = t.StopLossRate;
            feature.DynamicTakeProfitRate = t.TakeProfitRate;
            feature.DynamicTrailingStopRate = t.TrailingStopRate;

            if (!feature.HasPosition || feature.HoldingQty <= 0)
            {
                _holdingAnalysisStates.TryRemove(feature.Code, out _);

                feature.HoldSeconds = 0;
                feature.HoldTickCount = 0;
                feature.MaxProfitRate = 0;
                feature.MinProfitRate = 0;
                feature.DrawdownRate = 0;
                feature.ExitScore = 0;
                feature.HoldReason = "미보유";
                return;
            }

            DateTime now = feature.Time == default(DateTime) ? DateTime.Now : feature.Time;

            var state = _holdingAnalysisStates.GetOrAdd(feature.Code, _ => new HoldingAnalysisState
            {
                EntryDetectedTime = now,
                MaxProfitRate = feature.ProfitRate,
                MinProfitRate = feature.ProfitRate,
                HoldTickCount = 0
            });

            state.HoldTickCount++;
            state.MaxProfitRate = Math.Max(state.MaxProfitRate, feature.ProfitRate);
            state.MinProfitRate = Math.Min(state.MinProfitRate, feature.ProfitRate);

            double holdSeconds = (now - state.EntryDetectedTime).TotalSeconds;
            if (holdSeconds < 0)
                holdSeconds = 0;

            feature.HoldSeconds = holdSeconds;
            feature.HoldTickCount = state.HoldTickCount;
            feature.MaxProfitRate = state.MaxProfitRate;
            feature.MinProfitRate = state.MinProfitRate;
            feature.DrawdownRate = feature.ProfitRate - state.MaxProfitRate; // 최고수익 대비 이탈률. 보통 0 이하.
            feature.ExitScore = CalculateExitPressureScore(feature, t);
            feature.HoldReason = BuildHoldReason(feature, t);
        }

        private static int CalculateExitPressureScore(FeatureSnapshot f, DynamicTradeThreshold t)
        {
            if (f == null || t == null || !f.HasPosition)
                return 0;

            double score = 0;

            bool strongMarket =
                f.MarketTrend == MarketTrendState.Bull ||
                f.MarketTrend == MarketTrendState.StrongBull;

            bool weakMarket =
                f.MarketTrend == MarketTrendState.Bear ||
                f.MarketTrend == MarketTrendState.Panic;

            bool strongMinute =
                f.Phase == MarketPhase.Breakout ||
                f.MinuteTrendScore >= 60 ||
                f.TrendStrength >= 55;

            bool weakMinute =
                f.Phase == MarketPhase.DownTrend ||
                f.Phase == MarketPhase.Unstable ||
                f.MinuteTrendScore <= 40 ||
                f.TrendStrength <= 35;

            double stopLoss = Math.Abs(t.StopLossRate);
            double takeProfit = Math.Abs(t.TakeProfitRate);
            double trailing = Math.Abs(t.TrailingStopRate);

            if (stopLoss <= 0) stopLoss = Math.Max(0.3, f.VolatilityRate);
            if (takeProfit <= 0) takeProfit = Math.Max(0.5, f.VolatilityRate * 1.5);
            if (trailing <= 0) trailing = Math.Max(0.25, f.VolatilityRate * 0.8);

            // 손절권 진입: 동적 손절폭 기준
            if (f.ProfitRate <= -stopLoss)
                score += 45;

            // 고점 이탈: 상승장/분봉강세에서는 더 여유, 약세장에서는 더 빠르게
            double trailingTrigger = trailing;

            if (strongMarket && strongMinute)
                trailingTrigger *= 1.45;
            else if (weakMarket || weakMinute)
                trailingTrigger *= 0.85;

            if (f.MaxProfitRate >= takeProfit &&
                f.DrawdownRate <= -trailingTrigger)
            {
                score += weakMarket || weakMinute ? 38 : 28;
            }

            // 수익권인데 체결강도 약화
            if (f.ProfitRate > 0 &&
                f.MaxProfitRate >= takeProfit &&
                f.Strength < t.MinStrength)
            {
                score += strongMinute ? 8 : 15;
            }

            // 호가 약화
            if (f.BidAskRatio > 0 && f.BidAskRatio < t.MinBidAskRatio)
                score += strongMinute ? 6 : 12;

            // 단기 하락은 분봉이 살아있으면 노이즈로 완화
            if (f.PriceChangeRate1m < 0)
                score += strongMinute ? 4 : 10;

            if (f.PriceChangeRate3m < 0)
                score += strongMinute ? 5 : 12;

            if (weakMinute)
                score += weakMarket ? 22 : 16;

            // 상승장 + 분봉강세면 조기 익절/흔들림 청산을 완화
            if (strongMarket && strongMinute && f.ProfitRate > -stopLoss * 0.6)
                score *= 0.75;

            // 패닉이면 빠르게
            if (f.MarketTrend == MarketTrendState.Panic)
                score *= 1.20;

            if (score < 0) score = 0;
            if (score > 100) score = 100;

            return (int)Math.Round(score);
        }

        private static string BuildHoldReason(FeatureSnapshot f, DynamicTradeThreshold t)
        {
            if (f == null || t == null || !f.HasPosition)
                return "미보유";

            bool strongMarket =
                f.MarketTrend == MarketTrendState.Bull ||
                f.MarketTrend == MarketTrendState.StrongBull;

            bool weakMarket =
                f.MarketTrend == MarketTrendState.Bear ||
                f.MarketTrend == MarketTrendState.Panic;

            bool strongMinute =
                f.Phase == MarketPhase.Breakout ||
                f.MinuteTrendScore >= 60 ||
                f.TrendStrength >= 55;

            bool weakMinute =
                f.Phase == MarketPhase.DownTrend ||
                f.Phase == MarketPhase.Unstable ||
                f.MinuteTrendScore <= 40 ||
                f.TrendStrength <= 35;

            double stopLoss = Math.Abs(t.StopLossRate);
            double takeProfit = Math.Abs(t.TakeProfitRate);
            double trailing = Math.Abs(t.TrailingStopRate);

            if (f.ProfitRate <= -stopLoss)
            {
                if (weakMarket || weakMinute)
                    return "동적 손절권 진입 + 약세흐름";

                return "동적 손절권 진입";
            }

            if (f.MaxProfitRate >= takeProfit)
            {
                double trailingTrigger = trailing;

                if (strongMarket && strongMinute)
                    trailingTrigger *= 1.45;
                else if (weakMarket || weakMinute)
                    trailingTrigger *= 0.85;

                if (f.DrawdownRate <= -trailingTrigger)
                    return weakMarket || weakMinute
                        ? "약세 전환 고점이탈"
                        : "동적 고점이탈";
            }

            if (strongMarket && strongMinute)
                return "상승장 분봉추세 유지";

            if (strongMinute && f.PriceChangeRate1m >= 0)
                return "분봉추세 유지";

            if (f.ProfitRate > 0 && f.Strength < t.MinStrength)
                return "수익권 체결강도 약화";

            if (weakMarket && weakMinute)
                return "시장/종목 약세 관찰";

            if (f.PriceChangeRate1m >= 0 && f.PriceChangeRate3m >= 0)
                return "단기 상승흐름 유지";

            if (f.BidAskRatio > 0 && f.BidAskRatio < t.MinBidAskRatio)
                return "호가 매수우위 약화";

            return "보유 관찰";
        }

        private bool IsFullAnalysisLogEnabled()
        {
            return Settings != null && Settings.AnalysisLogMode == AutoTradeAnalysisLogMode.All;
        }

        private readonly ConcurrentDictionary<string, DateTime> _lastTickAnalysisLogTimes = new ConcurrentDictionary<string, DateTime>();
        private void LogTickAnalysis(FeatureSnapshot feature, TradeSignal signal, string stage)
        {
            if (Settings == null || !Settings.Enabled)
                return;

            if (!IsFullAnalysisLogEnabled() || feature == null)
                return;

            bool isTradeSignal =
                signal != null &&
                signal.Action != TradeActionType.Hold;

            if (!isTradeSignal)
            {
                DateTime now = DateTime.Now;

                DateTime lastTime;

                if (_lastTickAnalysisLogTimes.TryGetValue(
                    feature.Code,
                    out lastTime))
                {
                    if ((now - lastTime).TotalMilliseconds < 1000)
                        return;
                }

                _lastTickAnalysisLogTimes[feature.Code] = now;
            }

            string action =
                signal == null
                    ? "NULL"
                    : signal.Action.ToString();

            int score =
                signal == null
                    ? 0
                    : signal.Score;

            string strategy =
                string.IsNullOrWhiteSpace(signal?.StrategyName)
                    ? "-"
                    : signal.StrategyName;

            string reason =
                string.IsNullOrWhiteSpace(signal?.Reason)
                    ? stage
                    : signal.Reason;

            _log?.Invoke(
                $"[틱분석] " +
                $"종목={feature.Code} | " +
                $"판단={action} | " +
                $"점수={score} | " +
                $"시장={feature.MarketTrend} | " +
                $"KOSPI={feature.KospiChangeRate:F2}% | " +
                $"KOSDAQ={feature.KosdaqChangeRate:F2}% | " +
                $"시장평균={feature.MarketAverageChangeRate:F2}% | " +
                $"시장위험={feature.MarketRiskScore:F0} | " +
                $"시장지지={feature.MarketSupportScore:F0} | " +
                $"상대강도={feature.RelativeStrength:F2}% | " +
                $"진입점수보정={feature.MarketEntryScoreAdjustment:+0;-0;0} | " +
                $"수량배율={feature.MarketPositionSizeMultiplier:F2} | " +
                $"현재가={feature.Price:N0}원 | " +
                $"보유수량={feature.HoldingQty}주 | " +
                $"수익률={feature.ProfitRate:F2}% | " +
                $"최고수익={feature.MaxProfitRate:F2}% | " +
                $"최저수익={feature.MinProfitRate:F2}% | " +
                $"고점이탈={feature.DrawdownRate:F2}% | " +
                $"보유시간={TimeSpan.FromSeconds(feature.HoldSeconds):mm\\:ss} | " +
                $"청산점수={feature.ExitScore} | " +
                $"보유사유={feature.HoldReason ?? "-"} | " +
                $"손절기준={feature.DynamicStopLossRate:F2}% | " +
                $"익절기준={feature.DynamicTakeProfitRate:F2}% | " +
                $"트레일링={feature.DynamicTrailingStopRate:F2}% | " +
                $"1분등락={feature.PriceChangeRate1m:F2}% | " +
                $"3분등락={feature.PriceChangeRate3m:F2}% | " +
                $"체결강도={feature.Strength:F1} | " +
                $"체결강도변화={feature.StrengthDelta:F1} | " +
                $"거래량비율={feature.VolumeRatio:F2} | " +
                $"변동비율={feature.PriceMoveRatio:F2} | " +
                $"틱밀도={feature.TickDensityPerMinute:F0}/분 | " +
                $"매수우위={feature.BidAskRatio:F2} | " +
                $"종목국면={feature.Phase} | " +
                $"전략={strategy} | " +
                $"판단사유={reason}");
        }
        private void LogTickWaiting(TrackedSymbol tick, string reason)
        {
            if (Settings == null || !Settings.Enabled)
                return;

            if (!IsFullAnalysisLogEnabled() || tick == null)
                return;

            _log?.Invoke(
                $"[틱분석대기] {tick.Code} " +
                $"p={tick.LastPrice:N0} " +
                $"v={tick.LastVolume:N0} " +
                $"str={tick.Strength:F1} / {reason}");
        }

        private bool IsSignalAllowedByPosition(TradeSignal signal, FeatureSnapshot feature)
        {
            if (signal == null || feature == null)
                return false;

            int holdingQty = feature.HoldingQty;

            if (signal.Action == TradeActionType.Sell && holdingQty <= 0)
            {
                _log?.Invoke($"[자동매매 차단] 미보유 매도 신호 차단: {signal.Code} / {signal.StrategyName}");
                return false;
            }

            if (signal.Action == TradeActionType.Buy)
            {
                if (!Settings.AllowBuy)
                    return false;

                // 미보유 매수와 보유 중 추가매수 모두 허용한다.
                // 추가매수 한도/쿨다운/미체결 차단은 RiskManager, TradeGate, OrderQueue에서 처리한다.
                return true;
            }

            if (signal.Action == TradeActionType.Sell)
            {
                if (!Settings.AllowSell)
                    return false;

                return true;
            }

            return false;
        }

        private void NormalizeSignalQuantity(TradeSignal signal, FeatureSnapshot feature)
        {
            if (signal == null || feature == null)
                return;

            if (signal.Action == TradeActionType.Sell)
            {
                // 매도는 무조건 현재 보유수량 기준으로 고정한다.
                signal.Quantity = Math.Max(0, feature.HoldingQty);
                return;
            }

            if (signal.Action == TradeActionType.Buy)
            {
                // 매수 수량은 OrderRequestFactory/RiskManager에서 최종 계산하는 구조를 유지한다.
                // 단, 룰에서 0으로 들어오면 기본수량을 임시값으로 넣어 후속 계산이 가능하게 한다.
                if (signal.Quantity <= 0)
                    signal.Quantity = Settings.DefaultOrderQty;
            }
        }

        private int SafeHoldingQty(string code)
        {
            try { return _getHoldingQty == null ? 0 : _getHoldingQty(code); }
            catch { return 0; }
        }

        private double SafeEntryPrice(string code)
        {
            try { return _getEntryPrice == null ? 0 : _getEntryPrice(code); }
            catch { return 0; }
        }

        private IEnumerable<TrackedSymbol> SafeMinutes(string code)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(code) || _getMinuteCandles == null)
                    return Enumerable.Empty<TrackedSymbol>();

                var raw = _getMinuteCandles(code);
                if (raw == null)
                    return Enumerable.Empty<TrackedSymbol>();

                var minutes = raw
                    .Where(x =>
                        x != null &&
                        x.Code == code &&
                        x.LastUpdated != DateTime.MinValue &&
                        x.LastPrice > 0)
                    .OrderBy(x => x.LastUpdated)
                    .ToList();

                if (minutes.Count == 0)
                    return Enumerable.Empty<TrackedSymbol>();

                DateTime lastTime = minutes[minutes.Count - 1].LastUpdated;

                MinuteCacheState cached;
                if (_minuteCache.TryGetValue(code, out cached) &&
                    cached.Count == minutes.Count &&
                    cached.LastMinuteTime == lastTime &&
                    cached.Minutes != null)
                {
                    return cached.Minutes;
                }

                _minuteCache[code] = new MinuteCacheState
                {
                    Minutes = minutes,
                    Count = minutes.Count,
                    LastMinuteTime = lastTime
                };

                return minutes;
            }
            catch
            {
                return Enumerable.Empty<TrackedSymbol>();
            }
        }

        private IEnumerable<OrderInfo> SafeOrders()
        {
            try { return _getOpenOrders == null ? Enumerable.Empty<OrderInfo>() : _getOpenOrders() ?? Enumerable.Empty<OrderInfo>(); }
            catch { return Enumerable.Empty<OrderInfo>(); }
        }

        public void FlushPendingOutcomes(string code = null)
        {
            _outcomeLabelTracker?.FlushPending(code);
        }

        public void Dispose()
        {
            // Dispose 시점에 아직 10분 Horizon이 끝나지 않은 ML 라벨도
            // 현재까지 관측된 가격 기준으로 저장한다.
            _outcomeLabelTracker?.FlushPending();
            _cts.Cancel();
            _orderQueue?.Dispose();
        }
    }
}
