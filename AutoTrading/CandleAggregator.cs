using MoneyCreator.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MoneyCreator.AutoTrading
{
    public sealed class CandleAggregator
    {
        private readonly object _lock = new object();
        private readonly List<CandleData> _secondCandles = new List<CandleData>();
        private readonly List<CandleData> _minuteCandles = new List<CandleData>();
        private readonly List<CandleData> _hourCandles = new List<CandleData>();

        private readonly int _maxSecondCount;
        private readonly int _maxMinuteCount;
        private readonly int _maxHourCount;

        public CandleAggregator(int maxSecondCount = 300, int maxMinuteCount = 420, int maxHourCount = 48)
        {
            _maxSecondCount = maxSecondCount;
            _maxMinuteCount = maxMinuteCount;
            _maxHourCount = maxHourCount;
        }

        public MarketFrameSnapshot Update(TrackedSymbol tick)
        {
            if (tick == null)
                return null;

            lock (_lock)
            {
                UpdateCandle(_secondCandles, TrimToSecond(tick.LastUpdated), tick, _maxSecondCount);
                UpdateCandle(_minuteCandles, TrimToMinute(tick.LastUpdated), tick, _maxMinuteCount);
                UpdateCandle(_hourCandles, TrimToHour(tick.LastUpdated), tick, _maxHourCount);
                return BuildSnapshot(tick);
            }
        }

        public void SeedMinutes(IEnumerable<TrackedSymbol> minuteCandles)
        {
            if (minuteCandles == null)
                return;

            lock (_lock)
            {
                foreach (var m in minuteCandles.Where(x => x != null).OrderBy(x => x.LastUpdated).ToList())
                {
                    UpdateCandle(_minuteCandles, TrimToMinute(m.LastUpdated), m, _maxMinuteCount);
                    UpdateCandle(_hourCandles, TrimToHour(m.LastUpdated), m, _maxHourCount);
                }
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _secondCandles.Clear();
                _minuteCandles.Clear();
                _hourCandles.Clear();
            }
        }

        private static DateTime TrimToSecond(DateTime t) => new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second);
        private static DateTime TrimToMinute(DateTime t) => new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0);
        private static DateTime TrimToHour(DateTime t) => new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0);

        private static void UpdateCandle(List<CandleData> list, DateTime time, TrackedSymbol tick, int maxCount)
        {
            CandleData candle = list.Count > 0 ? list[list.Count - 1] : null;

            if (candle == null || candle.Time != time || candle.Code != tick.Code)
            {
                candle = new CandleData
                {
                    Code = tick.Code,
                    Time = time,
                    Open = tick.LastPrice,
                    High = tick.HighPrice > 0 ? tick.HighPrice : tick.LastPrice,
                    Low = tick.LowPrice > 0 ? tick.LowPrice : tick.LastPrice,
                    Close = tick.ClosePrice > 0 ? tick.ClosePrice : tick.LastPrice,
                    Volume = (long)Math.Abs(tick.LastVolume),
                    StrengthSum = tick.Strength,
                    StrengthCount = tick.Strength > 0 ? 1 : 0,
                    TotalAskQty = tick.TotalAskQty,
                    TotalBidQty = tick.TotalBidQty
                };
                list.Add(candle);

                while (list.Count > maxCount)
                    list.RemoveAt(0);

                return;
            }

            double high = tick.HighPrice > 0 ? tick.HighPrice : tick.LastPrice;
            double low = tick.LowPrice > 0 ? tick.LowPrice : tick.LastPrice;
            double close = tick.ClosePrice > 0 ? tick.ClosePrice : tick.LastPrice;

            if (high > candle.High) candle.High = high;
            if (low < candle.Low) candle.Low = low;
            candle.Close = close;
            candle.Volume += (long)Math.Abs(tick.LastVolume);

            if (tick.Strength > 0)
            {
                candle.StrengthSum += tick.Strength;
                candle.StrengthCount++;
            }

            candle.TotalAskQty = tick.TotalAskQty;
            candle.TotalBidQty = tick.TotalBidQty;
        }
        public void SeedRecentTicks(IEnumerable<TrackedSymbol> ticks)
        {
            if (ticks == null)
                return;

            lock (_lock)
            {
                foreach (var tick in ticks
                    .Where(x => x != null)
                    .OrderBy(x => x.LastUpdated))
                {
                    UpdateCandle(_secondCandles, TrimToSecond(tick.LastUpdated), tick, _maxSecondCount);
                    UpdateCandle(_minuteCandles, TrimToMinute(tick.LastUpdated), tick, _maxMinuteCount);
                    UpdateCandle(_hourCandles, TrimToHour(tick.LastUpdated), tick, _maxHourCount);
                }
            }
        }
        private MarketFrameSnapshot BuildSnapshot(TrackedSymbol tick)
        {
            var second = _secondCandles.ToList();
            var minute = _minuteCandles.ToList();
            var hour = _hourCandles.ToList();

            return new MarketFrameSnapshot
            {
                Code = tick.Code,
                LastTick = tick,
                SecondCandles = second,
                MinuteCandles = minute,
                HourCandles = hour,
                SecondTrend = AnalyzeTrend(second, 10),
                MinuteTrend = AnalyzeTrend(minute, 5),
                HourTrend = AnalyzeTrend(hour, 3),
                SecondBuyPressure = AnalyzeBuyPressure(second, 10),
                MinuteBuyPressure = AnalyzeBuyPressure(minute, 5),
                IsEnoughData = second.Count >= 5 && minute.Count >= 1
            };
        }

        private static TrendState AnalyzeTrend(List<CandleData> candles, int count)
        {
            if (candles == null || candles.Count < 2)
                return TrendState.Unknown;

            var recent = candles.Skip(Math.Max(0, candles.Count - count)).ToList();
            if (recent.Count < 2)
                return TrendState.Unknown;

            double first = recent[0].Close;
            double last = recent[recent.Count - 1].Close;
            if (first <= 0)
                return TrendState.Unknown;

            double changeRate = ((last - first) / first) * 100.0;
            int upCount = recent.Count(x => x.Close >= x.Open);
            int downCount = recent.Count - upCount;

            if (changeRate >= 0.7 && upCount >= downCount) return TrendState.StrongUp;
            if (changeRate >= 0.2) return TrendState.Up;
            if (changeRate <= -0.7 && downCount >= upCount) return TrendState.StrongDown;
            if (changeRate <= -0.2) return TrendState.Down;
            return TrendState.Flat;
        }

        private static double AnalyzeBuyPressure(List<CandleData> candles, int count)
        {
            if (candles == null || candles.Count == 0)
                return 0;

            var recent = candles.Skip(Math.Max(0, candles.Count - count)).ToList();
            if (recent.Count == 0)
                return 0;

            int up = recent.Count(x => x.Close >= x.Open);
            double upRatio = (double)up / recent.Count;
            double avgBidAsk = recent.Average(x => x.TotalAskQty <= 0 ? 0 : (double)x.TotalBidQty / x.TotalAskQty);
            double avgStrength = recent.Average(x => x.AvgStrength);

            double score = 0;
            score += upRatio * 35.0;
            score += avgBidAsk >= 1.2 ? 30 : avgBidAsk >= 1.0 ? 18 : avgBidAsk >= 0.8 ? 8 : 0;
            score += avgStrength >= 130 ? 35 : avgStrength >= 110 ? 25 : avgStrength >= 100 ? 15 : 0;
            return Math.Min(100, Math.Max(0, score));
        }
    }
}
