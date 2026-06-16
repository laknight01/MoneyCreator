using MoneyCreator.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using static MoneyCreator.Models.MarketIndexInfo;

namespace MoneyCreator.AutoTrading
{
    public sealed class FeatureEngine
    {
        private readonly ConcurrentDictionary<string, TrackedSymbol> _lastTick = new ConcurrentDictionary<string, TrackedSymbol>();
        private readonly MarketPhaseDetector _phaseDetector = new MarketPhaseDetector();

        public TickFeature GenerateFeature(TrackedSymbol tick)
        {
            if (tick == null || string.IsNullOrWhiteSpace(tick.Code))
                return null;

            TrackedSymbol prev;
            _lastTick.TryGetValue(tick.Code, out prev);

            double prevPrice = prev == null ? tick.LastPrice : prev.LastPrice;
            double prevVolume = prev == null ? tick.LastVolume : prev.LastVolume;
            double prevStrength = prev == null ? tick.Strength : prev.Strength;

            double bidAskRatio = tick.TotalAskQty <= 0 ? 0 : (double)tick.TotalBidQty / tick.TotalAskQty;
            double priceChangeRate = prevPrice <= 0 ? 0 : ((tick.LastPrice - prevPrice) / prevPrice) * 100.0;
            double strengthDelta = tick.Strength - prevStrength;
            double volumeAccel = tick.LastVolume - prevVolume;

            var feature = new TickFeature
            {
                Code = tick.Code,
                Time = tick.LastUpdated,
                Price = tick.LastPrice,
                PrevPrice = prevPrice,
                Volume = tick.LastVolume,
                PrevVolume = prevVolume,
                Strength = tick.Strength,
                PrevStrength = prevStrength,
                TotalAskQty = tick.TotalAskQty,
                TotalBidQty = tick.TotalBidQty,
                BidAskRatio = bidAskRatio,
                StrengthDelta = strengthDelta,
                VolumeAccel = volumeAccel,
                PriceChangeRate = priceChangeRate,
                BuyPressureScore = CalculateBuyPressure(bidAskRatio, tick.Strength, strengthDelta, volumeAccel, priceChangeRate),
                Raw = tick
            };

            _lastTick.AddOrUpdate(tick.Code, tick, (k, old) => tick);
            return feature;
        }

        public FeatureSnapshot GenerateSnapshotFeature(MarketSnapshot snapshot)
        {
            if (snapshot == null || snapshot.LastTick == null)
                return null;

            var recentTicks = SafeTrackedSymbols(snapshot.RecentTicks);
            var minutes = SafeTrackedSymbols(snapshot.MinuteCandles);
            var frame = snapshot.Frame;

            double price = snapshot.CurrentPrice > 0 ? snapshot.CurrentPrice : snapshot.LastTick.LastPrice;
            double bidAskRatio = snapshot.Hoga == null || snapshot.Hoga.TotalAskQty <= 0
                ? snapshot.LastTick.TotalAskQty <= 0 ? 0 : (double)snapshot.LastTick.TotalBidQty / snapshot.LastTick.TotalAskQty
                : (double)snapshot.Hoga.TotalBidQty / snapshot.Hoga.TotalAskQty;

            double avgBidAskRatio = CalculateAverageBidAskRatio(recentTicks);
            double bidAskRatioRelative = avgBidAskRatio > 0 ? bidAskRatio / avgBidAskRatio : 1.0;

            double strength = snapshot.LastTick.Strength;
            double strengthDelta = CalculateStrengthDelta(recentTicks);
            double volumeAccel = CalculateVolumeAccel(recentTicks);
            double currentTickVolume = recentTicks.Skip(Math.Max(0, recentTicks.Count - 5)).Select(x => Math.Abs(x.LastVolume)).DefaultIfEmpty(0).Average();
            double avgTickVolume = CalculateAverageTickVolume(recentTicks);
            double volumeRatio = CalculateVolumeRatio(currentTickVolume, avgTickVolume);
            double tickDensity = CalculateTickDensityPerMinute(recentTicks);
            double priceMoveRatio = CalculatePriceMoveRatio(recentTicks, price);
            double volatilityRate = CalculateVolatilityRate(recentTicks, price);
            double rangePosition = CalculateRangePosition(recentTicks, price);
            double trendStrength = CalculateTrendStrength(recentTicks, minutes, price, frame);
            bool isSideways = CalculateIsSideways(volatilityRate, trendStrength, priceMoveRatio);

            var market = snapshot.MarketEnvironment;
            double marketAverageRate = market == null ? 0 : market.AverageChangeRate;

            double relativeStrength = ChangeRateFromTicks(
                recentTicks,
                TimeSpan.FromMinutes(5),
                price) - marketAverageRate;

            var f = new FeatureSnapshot
            {
                Code = snapshot.Code,
                Time = snapshot.Time,
                Price = price,
                PriceChangeRate1m = ChangeRateFromTicks(recentTicks, TimeSpan.FromMinutes(1), price),
                PriceChangeRate3m = ChangeRateFromTicks(recentTicks, TimeSpan.FromMinutes(3), price),
                PriceChangeRate5m = ChangeRateFromTicks(recentTicks, TimeSpan.FromMinutes(5), price),
                VolumeAccel = volumeAccel,
                CurrentTickVolume = currentTickVolume,
                AvgTickVolume = avgTickVolume,
                VolumeRatio = volumeRatio,
                TickDensityPerMinute = tickDensity,
                PriceMoveRatio = priceMoveRatio,
                Strength = strength,
                StrengthDelta = strengthDelta,
                BidAskRatio = bidAskRatio,
                BuyPressureScore = CalculateBuyPressure(bidAskRatio, strength, strengthDelta, volumeAccel, 0),
                MinuteTrendScore = CalculateMinuteTrendScore(minutes, frame),
                HourTrendScore = CalculateHourTrendScore(frame),
                VolatilityScore = CalculateVolatilityScore(recentTicks),
                OverheatScore = CalculateOverheatScore(recentTicks, minutes),
                HasPosition = snapshot.HoldingQty > 0,
                HoldingQty = snapshot.HoldingQty,
                EntryPrice = snapshot.EntryPrice,
                ProfitRate = snapshot.ProfitRate,
                AvgBidAskRatio = avgBidAskRatio,
                BidAskRatioRelative = bidAskRatioRelative,
                IsSideways = isSideways,
                TrendStrength = trendStrength,
                VolatilityRate = volatilityRate,
                RangePosition = rangePosition,
                Cash = snapshot.Cash,
                OpenOrderCount = snapshot.OpenOrders == null ? 0 : snapshot.OpenOrders.Count,
                HasPendingBuy = HasPending(snapshot.OpenOrders, "매수"),
                HasPendingSell = HasPending(snapshot.OpenOrders, "매도"),

                // 중요: 원본 MarketSnapshot 참조 금지
                Snapshot = null,

                MarketTrend = market == null ? MarketTrendState.Unknown : market.Trend,
                KospiChangeRate = market == null ? 0 : market.KospiChangeRate,
                KosdaqChangeRate = market == null ? 0 : market.KosdaqChangeRate,
                MarketAverageChangeRate = marketAverageRate,
                MarketRiskScore = market == null ? 0 : market.MarketRiskScore,
                MarketSupportScore = market == null ? 0 : market.MarketSupportScore,
                RelativeStrength = relativeStrength,
                MarketEntryScoreAdjustment = market == null ? 0 : market.EntryScoreAdjustment,
                MarketPositionSizeMultiplier = market == null ? 1.0 : market.PositionSizeMultiplier,
                MarketStopLossMultiplier = market == null ? 1.0 : market.StopLossMultiplier,
                MarketTakeProfitMultiplier = market == null ? 1.0 : market.TakeProfitMultiplier,
                MarketTrailingMultiplier = market == null ? 1.0 : market.TrailingMultiplier
            };

            f.Phase = _phaseDetector.Detect(f, snapshot);
            return f;
        }
        private static double CalculateAverageBidAskRatio(List<TrackedSymbol> ticks)
        {
            ticks = SafeTrackedSymbols(ticks);

            var ratios = ticks
                .Where(x => x.TotalAskQty > 0)
                .Select(x => (double)x.TotalBidQty / x.TotalAskQty)
                .Where(x => x > 0)
                .ToList();

            if (ratios.Count == 0)
                return 0;

            return ratios.Average();
        }
        private static double CalculateAverageTickVolume(List<TrackedSymbol> ticks)
        {
            ticks = SafeTrackedSymbols(ticks);

            if (ticks.Count < 3)
                return 0;

            // 마지막 틱은 현재틱일 가능성이 높으니 제외하고 평균 계산
            var sample = ticks
                .Take(Math.Max(1, ticks.Count - 1))
                .Select(x => Math.Abs(x.LastVolume))
                .Where(x => x > 0)
                .ToList();

            if (sample.Count == 0)
                return 0;

            return sample.Average();
        }

        private static double CalculateVolumeRatio(double currentVolume, double avgVolume)
        {
            if (currentVolume <= 0 || avgVolume <= 0)
                return 1.0;

            return currentVolume / avgVolume;
        }

        private static double CalculateTickDensityPerMinute(List<TrackedSymbol> ticks)
        {
            ticks = SafeTrackedSymbols(ticks);

            if (ticks.Count < 2)
                return 0;

            double minutes = (ticks[ticks.Count - 1].LastUpdated - ticks[0].LastUpdated).TotalMinutes;

            if (minutes <= 0)
                return ticks.Count;

            return ticks.Count / minutes;
        }

        private static double CalculatePriceMoveRatio(List<TrackedSymbol> ticks, double currentPrice)
        {
            ticks = SafeTrackedSymbols(ticks);

            if (ticks.Count < 5 || currentPrice <= 0)
                return 1.0;

            double currentMove = Math.Abs(ChangeRateFromTicks(ticks, TimeSpan.FromMinutes(1), currentPrice));

            var minuteGroups = ticks
                .GroupBy(x => new DateTime(
                    x.LastUpdated.Year,
                    x.LastUpdated.Month,
                    x.LastUpdated.Day,
                    x.LastUpdated.Hour,
                    x.LastUpdated.Minute,
                    0))
                .Select(g =>
                {
                    var list = g.OrderBy(x => x.LastUpdated).ToList();
                    double first = list.First().LastPrice;
                    double last = list.Last().LastPrice;

                    if (first <= 0)
                        return 0;

                    return Math.Abs((last - first) / first * 100.0);
                })
                .Where(x => x > 0)
                .ToList();

            if (minuteGroups.Count == 0)
                return 1.0;

            double avgMove = minuteGroups.Average();

            if (avgMove <= 0)
                return 1.0;

            return currentMove / avgMove;
        }

        private static double CalculateVolatilityRate(List<TrackedSymbol> ticks, double currentPrice)
        {
            ticks = SafeTrackedSymbols(ticks);

            if (ticks.Count < 3 || currentPrice <= 0)
                return 0;

            double min = ticks.Min(x => x.LastPrice);
            double max = ticks.Max(x => x.LastPrice);

            if (min <= 0 || max <= 0 || max < min)
                return 0;

            return ((max - min) / currentPrice) * 100.0;
        }

        private static double CalculateRangePosition(List<TrackedSymbol> ticks, double currentPrice)
        {
            ticks = SafeTrackedSymbols(ticks);

            if (ticks.Count < 3 || currentPrice <= 0)
                return 0.5;

            double min = ticks.Min(x => x.LastPrice);
            double max = ticks.Max(x => x.LastPrice);
            double range = max - min;

            if (min <= 0 || max <= 0 || range <= 0)
                return 0.5;

            double position = (currentPrice - min) / range;

            if (position < 0) return 0;
            if (position > 1) return 1;
            return position;
        }

        private static double CalculateTrendStrength(
            List<TrackedSymbol> ticks,
            List<TrackedSymbol> minutes,
            double currentPrice,
            MarketFrameSnapshot frame)
        {
            ticks = SafeTrackedSymbols(ticks);
            minutes = SafeTrackedSymbols(minutes);

            double score = 0;

            // 단타용: 틱 흐름 60%
            double r1 = Math.Abs(ChangeRateFromTicks(ticks, TimeSpan.FromMinutes(1), currentPrice));
            double r3 = Math.Abs(ChangeRateFromTicks(ticks, TimeSpan.FromMinutes(3), currentPrice));
            double r5 = Math.Abs(ChangeRateFromTicks(ticks, TimeSpan.FromMinutes(5), currentPrice));

            score += Math.Min(25, r1 * 30.0);
            score += Math.Min(20, r3 * 14.0);
            score += Math.Min(10, r5 * 6.0);

            // 분봉 방향성 35%
            if (minutes.Count >= 3)
            {
                var recent = minutes
                    .Skip(Math.Max(0, minutes.Count - 5))
                    .ToList();

                double first = recent[0].LastPrice;
                double last = recent[recent.Count - 1].LastPrice;

                if (first > 0)
                {
                    double minuteRate = Math.Abs((last - first) / first * 100.0);

                    score += Math.Min(25, minuteRate * 18.0);

                    int sameDirectionCount = 0;

                    for (int i = 1; i < recent.Count; i++)
                    {
                        if ((last >= first && recent[i].LastPrice >= recent[i - 1].LastPrice) ||
                            (last < first && recent[i].LastPrice <= recent[i - 1].LastPrice))
                        {
                            sameDirectionCount++;
                        }
                    }

                    if (sameDirectionCount >= 3)
                        score += 10;
                    else if (sameDirectionCount >= 2)
                        score += 5;
                }
            }

            // frame은 보조 5~10%
            if (frame != null)
            {
                if (frame.MinuteTrend == TrendState.StrongUp || frame.MinuteTrend == TrendState.StrongDown)
                    score += 10;
                else if (frame.MinuteTrend == TrendState.Up || frame.MinuteTrend == TrendState.Down)
                    score += 5;
            }

            if (score < 0) return 0;
            if (score > 100) return 100;
            return score;
        }

        private static bool CalculateIsSideways(double volatilityRate, double trendStrength, double priceMoveRatio)
        {
            // 최근 5분 변동폭이 작고 방향성도 약하면 횡보로 본다.
            // priceMoveRatio는 현재 1분 움직임이 평소보다 큰지 판단하는 보조값이다.
            return volatilityRate <= 0.45 && trendStrength <= 35 && priceMoveRatio <= 1.25;
        }

        public void Reset()
        {
            _lastTick.Clear();
        }

        private static bool HasPending(List<OrderInfo> orders, string keyword)
        {
            if (orders == null)
                return false;

            return orders.Any(x =>
                x != null &&
                (x.UntradedQty > 0 || string.Equals(x.Status, "접수", StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrEmpty(x.OrderType) &&
                x.OrderType.Contains(keyword));
        }

        private static double CalculateBuyPressure(double bidAskRatio, double strength, double strengthDelta, double volumeAccel, double priceChangeRate)
        {
            double pressure = 0;
            pressure += bidAskRatio >= 1.3 ? 30 : bidAskRatio >= 1.1 ? 22 : bidAskRatio >= 1.0 ? 12 : 0;
            pressure += strength >= 130 ? 30 : strength >= 115 ? 22 : strength >= 100 ? 12 : 0;
            pressure += strengthDelta > 5 ? 15 : strengthDelta > 0 ? 9 : 0;
            pressure += volumeAccel > 0 ? 15 : 0;
            pressure += priceChangeRate > 0 ? 10 : 0;
            return Math.Min(100, Math.Max(0, pressure));
        }

        private static double ChangeRateFromTicks(List<TrackedSymbol> ticks, TimeSpan span, double currentPrice)
        {
            ticks = SafeTrackedSymbols(ticks);

            if (ticks.Count == 0 || currentPrice <= 0)
                return 0;

            DateTime cutoff = ticks[ticks.Count - 1].LastUpdated - span;
            var baseTick = ticks.FirstOrDefault(x => x.LastUpdated >= cutoff) ?? ticks[0];

            if (baseTick == null || baseTick.LastPrice <= 0)
                return 0;

            return ((currentPrice - baseTick.LastPrice) / baseTick.LastPrice) * 100.0;
        }

        private static double CalculateStrengthDelta(List<TrackedSymbol> ticks)
        {
            ticks = SafeTrackedSymbols(ticks);

            if (ticks.Count < 2)
                return 0;

            int half = Math.Max(1, ticks.Count / 2);
            double oldAvg = ticks.Take(half).Average(x => x.Strength);
            double newAvg = ticks.Skip(half).Average(x => x.Strength);
            return newAvg - oldAvg;
        }

        private static double CalculateVolumeAccel(List<TrackedSymbol> ticks)
        {
            ticks = SafeTrackedSymbols(ticks);

            if (ticks.Count < 4)
                return 0;

            int half = Math.Max(1, ticks.Count / 2);
            double oldAvg = ticks.Take(half).Average(x => Math.Abs(x.LastVolume));
            double newAvg = ticks.Skip(half).Average(x => Math.Abs(x.LastVolume));
            return newAvg - oldAvg;
        }

        private static double CalculateMinuteTrendScore(List<TrackedSymbol> minutes, MarketFrameSnapshot frame)
        {
            minutes = SafeTrackedSymbols(minutes);

            // 1. 실제 과거/실시간 분봉이 충분하면 분봉 우선
            if (minutes.Count >= 3)
            {
                var recent = minutes
                    .Skip(Math.Max(0, minutes.Count - 5))
                    .ToList();

                double first = recent[0].LastPrice;
                double last = recent[recent.Count - 1].LastPrice;

                if (first <= 0)
                    return 50;

                double rate = ((last - first) / first) * 100.0;

                int upCount = 0;
                int downCount = 0;

                for (int i = 1; i < recent.Count; i++)
                {
                    if (recent[i].LastPrice > recent[i - 1].LastPrice)
                        upCount++;
                    else if (recent[i].LastPrice < recent[i - 1].LastPrice)
                        downCount++;
                }

                double score = 50;

                if (rate >= 1.0) score = 85;
                else if (rate >= 0.45) score = 72;
                else if (rate >= 0.15) score = 60;
                else if (rate > -0.15) score = 50;
                else if (rate > -0.45) score = 38;
                else if (rate > -0.90) score = 25;
                else score = 12;

                if (upCount >= downCount + 2)
                    score += 8;
                else if (downCount >= upCount + 2)
                    score -= 8;

                // 2. frame은 보조값으로만 반영
                if (frame != null)
                {
                    if (frame.MinuteTrend == TrendState.StrongUp)
                        score += 5;
                    else if (frame.MinuteTrend == TrendState.Up)
                        score += 3;
                    else if (frame.MinuteTrend == TrendState.Down)
                        score -= 3;
                    else if (frame.MinuteTrend == TrendState.StrongDown)
                        score -= 5;
                }

                if (score < 0) return 0;
                if (score > 100) return 100;
                return score;
            }

            // 3. 분봉이 부족할 때만 frame 사용
            if (frame != null)
            {
                if (frame.MinuteTrend == TrendState.StrongUp) return 90;
                if (frame.MinuteTrend == TrendState.Up) return 70;
                if (frame.MinuteTrend == TrendState.Flat) return 50;
                if (frame.MinuteTrend == TrendState.Down) return 30;
                if (frame.MinuteTrend == TrendState.StrongDown) return 10;
            }

            return 50;
        }

        private static double CalculateHourTrendScore(MarketFrameSnapshot frame)
        {
            if (frame == null)
                return 0;

            if (frame.HourTrend == TrendState.StrongUp) return 85;
            if (frame.HourTrend == TrendState.Up) return 65;
            if (frame.HourTrend == TrendState.Flat) return 50;
            if (frame.HourTrend == TrendState.Down) return 30;
            if (frame.HourTrend == TrendState.StrongDown) return 10;
            return 0;
        }

        private static double CalculateVolatilityScore(List<TrackedSymbol> ticks)
        {
            ticks = SafeTrackedSymbols(ticks);

            if (ticks.Count < 3)
                return 0;

            double min = ticks.Min(x => x.LastPrice);
            double max = ticks.Max(x => x.LastPrice);
            double last = ticks[ticks.Count - 1].LastPrice;
            if (last <= 0)
                return 0;

            return Math.Min(100, Math.Abs((max - min) / last) * 10000.0);
        }

        private static double CalculateOverheatScore(List<TrackedSymbol> ticks, List<TrackedSymbol> minutes)
        {
            double score = 0;

            ticks = SafeTrackedSymbols(ticks);
            minutes = SafeTrackedSymbols(minutes);

            if (ticks.Count > 0)
            {
                double lastPrice = ticks[ticks.Count - 1].LastPrice;
                double r5 = ChangeRateFromTicks(ticks, TimeSpan.FromMinutes(5), lastPrice);

                if (r5 >= 3.0) score += 60;
                else if (r5 >= 2.0) score += 40;
                else if (r5 >= 1.0) score += 20;
            }

            if (minutes.Count >= 3)
            {
                var recent = minutes.Skip(Math.Max(0, minutes.Count - 3)).ToList();

                // TrackedSymbol 분봉에는 OpenPrice가 없으므로,
                // 분봉 저가 대비 종가/현재가가 무너지지 않았는지만 본다.
                if (recent.All(x =>
                    x != null &&
                    x.LowPrice > 0 &&
                    x.ClosePrice >= x.LowPrice &&
                    x.LastPrice >= x.LowPrice))
                {
                    score += 25;
                }
            }

            return Math.Min(100, score);
        }

        private static List<TrackedSymbol> SafeTrackedSymbols(IEnumerable<TrackedSymbol> source)
        {
            if (source == null)
                return new List<TrackedSymbol>();

            return source
                .Where(x => x != null)
                .OrderBy(x => x.LastUpdated)
                .ToList();
        }
    }
}

