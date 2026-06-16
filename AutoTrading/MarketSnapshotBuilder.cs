using MoneyCreator.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using static MoneyCreator.Models.MarketIndexInfo;

namespace MoneyCreator.AutoTrading
{
    public sealed class MarketSnapshotBuilder
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, Queue<TrackedSymbol>> _recentTicks = new Dictionary<string, Queue<TrackedSymbol>>();
        private readonly Dictionary<string, HogaData> _latestHoga = new Dictionary<string, HogaData>();
        private readonly Dictionary<string, DateTime> _lastOrderTime = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, int> _dailyOrderCount = new Dictionary<string, int>();

        private long _cash;
        private double _dailyRealizedProfit;
        private int _consecutiveLossCount;

        public void UpdateCash(long cash)
        {
            lock (_lock) _cash = cash;
        }

        public void UpdateHoga(HogaData hoga)
        {
            if (hoga == null || string.IsNullOrWhiteSpace(hoga.Code))
                return;

            lock (_lock) _latestHoga[hoga.Code] = hoga;
        }

        public void MarkOrder(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return;

            lock (_lock)
            {
                _lastOrderTime[code] = DateTime.Now;
                int count;
                _dailyOrderCount.TryGetValue(code, out count);
                _dailyOrderCount[code] = count + 1;
            }
        }

        public void UpdateDailyResult(double realizedProfit, bool isLoss)
        {
            lock (_lock)
            {
                _dailyRealizedProfit += realizedProfit;
                _consecutiveLossCount = isLoss ? _consecutiveLossCount + 1 : 0;
            }
        }

        private MarketEnvironment _marketEnvironment = new MarketEnvironment
        {
            Trend = MarketTrendState.Unknown,
            PositionSizeMultiplier = 1.0,
            StopLossMultiplier = 1.0,
            TakeProfitMultiplier = 1.0,
            TrailingMultiplier = 1.0
        };

        public void UpdateMarketEnvironment(MarketEnvironment environment)
        {
            if (environment == null)
                return;

            lock (_lock)
            {
                _marketEnvironment = environment;
            }
        }

        public MarketSnapshot Build(
            TrackedSymbol tick,
            MarketFrameSnapshot frame,
            IEnumerable<TrackedSymbol> minuteCandles,
            int holdingQty,
            double entryPrice,
            IEnumerable<OrderInfo> openOrders)
        {
            if (tick == null)
                return null;

            lock (_lock)
            {
                Queue<TrackedSymbol> q;
                if (!_recentTicks.TryGetValue(tick.Code, out q))
                {
                    q = new Queue<TrackedSymbol>();
                    _recentTicks[tick.Code] = q;
                }

                q.Enqueue(CloneTick(tick));
                DateTime cutoff = tick.LastUpdated.AddMinutes(-5);
                while (q.Count > 0 && q.Peek().LastUpdated < cutoff)
                    q.Dequeue();

                HogaData hoga;
                _latestHoga.TryGetValue(tick.Code, out hoga);

                DateTime lastOrder;
                DateTime? lastOrderNullable = _lastOrderTime.TryGetValue(tick.Code, out lastOrder) ? (DateTime?)lastOrder : null;

                int orderCount;
                _dailyOrderCount.TryGetValue(tick.Code, out orderCount);

                double profitRate = 0;
                if (holdingQty > 0 && entryPrice > 0 && tick.LastPrice > 0)
                    profitRate = ((tick.LastPrice - entryPrice) / entryPrice) * 100.0;

                return new MarketSnapshot
                {
                    Code = tick.Code,
                    Time = tick.LastUpdated,
                    CurrentPrice = tick.LastPrice,
                    LastTick = CloneTick(tick),
                    RecentTicks = q.Where(x => x != null).OrderBy(x => x.LastUpdated).ToList(),
                    MinuteCandles = minuteCandles == null ? new List<TrackedSymbol>() : minuteCandles.Where(x => x != null).OrderBy(x => x.LastUpdated).ToList(),
                    HourCandles = frame == null ? new List<CandleData>() : frame.HourCandles,
                    Hoga = hoga,
                    HoldingQty = holdingQty,
                    EntryPrice = entryPrice,
                    ProfitRate = profitRate,
                    Cash = _cash,
                    OpenOrders = openOrders == null ? new List<OrderInfo>() : openOrders.ToList(),
                    LastOrderTime = lastOrderNullable,
                    DailyRealizedProfit = _dailyRealizedProfit,
                    DailyOrderCountForCode = orderCount,
                    ConsecutiveLossCount = _consecutiveLossCount,
                    Frame = frame,
                    MarketEnvironment = _marketEnvironment
                };
            }
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

        public void SeedRecentTicks(IEnumerable<TrackedSymbol> ticks)
        {
            if (ticks == null)
                return;

            lock (_lock)
            {
                foreach (var tick in ticks
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Code))
                    .OrderBy(x => x.LastUpdated))
                {
                    Queue<TrackedSymbol> q;

                    if (!_recentTicks.TryGetValue(tick.Code, out q))
                    {
                        q = new Queue<TrackedSymbol>();
                        _recentTicks[tick.Code] = q;
                    }

                    q.Enqueue(CloneTick(tick));

                    DateTime cutoff = tick.LastUpdated.AddMinutes(-5);

                    while (q.Count > 0 && q.Peek().LastUpdated < cutoff)
                        q.Dequeue();
                }
            }
        }

        public void Reset(string code = null)
        {
            lock (_lock)
            {
                if (string.IsNullOrWhiteSpace(code))
                {
                    _recentTicks.Clear();
                    _latestHoga.Clear();
                    _lastOrderTime.Clear();
                    _dailyOrderCount.Clear();
                }
                else
                {
                    _recentTicks.Remove(code);
                    _latestHoga.Remove(code);
                    _lastOrderTime.Remove(code);
                    _dailyOrderCount.Remove(code);
                }
            }
        }
    }
}
