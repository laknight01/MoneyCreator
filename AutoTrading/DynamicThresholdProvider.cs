using System;
using static MoneyCreator.Models.MarketIndexInfo;

namespace MoneyCreator.AutoTrading
{
    /// <summary>
    /// 고정 매매값을 한 곳에 모으고, 현재 종목/시장 상태에 따라 동적으로 변환한다.
    /// Rules/Risk/Order 쪽에서는 AutoTradeSettings의 숫자를 직접 쓰기보다 이 값을 사용한다.
    /// </summary>
    public static class DynamicThresholdProvider
    {
        public static DynamicTradeThreshold Create(FeatureSnapshot f, AutoTradeSettings settings)
        {
            settings = settings ?? new AutoTradeSettings();

            var t = new DynamicTradeThreshold();

            if (f == null)
            {
                t.MinSignalScore = settings.MinSignalScore;
                t.CooldownSeconds = settings.CooldownSeconds;
                t.ReEntryBlockSecondsAfterSell = settings.ReEntryBlockSecondsAfterSell;
                t.MaxOrderAmount = settings.MaxOrderAmount;
                t.MaxPositionAmount = settings.MaxPositionAmount;
                t.MinCashRemain = settings.MinCashRemain;
                t.StopLossRate = Math.Abs(settings.StopLossRate);
                t.TakeProfitRate = Math.Abs(settings.TakeProfitRate);
                t.TrailingStartProfitRate = Math.Abs(settings.TrailingStartProfitRate);
                t.TrailingStopRate = Math.Abs(settings.TrailingStopRate);
                t.EntryAllowed = true;
                return t;
            }

            double move1 = Math.Abs(f.PriceChangeRate1m);
            double move3 = Math.Abs(f.PriceChangeRate3m);
            double move5 = Math.Abs(f.PriceChangeRate5m);

            double rangeVol = Math.Max(move1, Math.Max(move3 / 2.0, move5 / 3.0));
            double scoreVol = Math.Max(0, f.VolatilityScore) / 100.0 * 1.5;
            double volatility = Clamp(Math.Max(rangeVol, scoreVol), 0.15, 3.5);

            double marketSizeMultiplier = f.MarketPositionSizeMultiplier <= 0 ? 1.0 : f.MarketPositionSizeMultiplier;
            double stopMultiplier = f.MarketStopLossMultiplier <= 0 ? 1.0 : f.MarketStopLossMultiplier;
            double takeMultiplier = f.MarketTakeProfitMultiplier <= 0 ? 1.0 : f.MarketTakeProfitMultiplier;
            double trailingMultiplier = f.MarketTrailingMultiplier <= 0 ? 1.0 : f.MarketTrailingMultiplier;

            t.IsHighLiquidity =
                f.TickDensityPerMinute >= 90 ||
                f.AvgTickVolume >= 1000 ||
                f.CurrentTickVolume >= 3000;

            t.IsVolatile =
                volatility >= 0.9 ||
                f.Phase == MarketPhase.Unstable ||
                f.Phase == MarketPhase.Overheated;

            bool lowMove = move1 < 0.18 && move3 < 0.35 && move5 < 0.55;
            bool weakTrend = f.MinuteTrendScore < 55 && f.HourTrendScore < 55;
            bool priceNotMovingWithPressure =
                f.BuyPressureScore >= 60 &&
                f.PriceMoveRatio < 0.85 &&
                move1 < 0.20;

            t.IsSideways =
                f.Phase == MarketPhase.Quiet ||
                (lowMove && weakTrend && f.VolumeRatio < 1.35) ||
                priceNotMovingWithPressure;

            int minScore = settings.MinSignalScore <= 0 ? 70 : settings.MinSignalScore;

            if (t.IsSideways) minScore += 15;
            if (f.Phase == MarketPhase.Unstable) minScore += 12;
            if (f.Phase == MarketPhase.Overheated) minScore += 8;
            if (f.Phase == MarketPhase.Breakout &&
                f.MinuteTrendScore >= 65 &&
                f.PriceMoveRatio >= 1.0)
                minScore -= 5;
            if (t.IsHighLiquidity) minScore += 3;

            minScore += f.MarketEntryScoreAdjustment;
            t.MinSignalScore = ClampInt(minScore, 60, 95);

            long cash = Math.Max(0, f.Cash);

            int minCashRemain = settings.MinCashRemain;
            if (settings.UseDynamicMarketThresholds)
            {
                minCashRemain = (int)Math.Max(
                    settings.MinCashRemain,
                    cash * settings.DynamicMinCashRemainRatio);
            }

            t.MinCashRemain = Math.Max(0, minCashRemain);

            long available = Math.Max(0, cash - t.MinCashRemain);

            int dynamicOrderAmount = 0;
            int dynamicPositionAmount = 0;

            if (settings.UseDynamicMarketThresholds && available > 0)
            {
                double entryRatio = settings.DynamicFirstEntryCashRatio;

                if (t.IsVolatile || f.Phase == MarketPhase.Overheated)
                    entryRatio *= 0.65;

                if (t.IsSideways)
                    entryRatio *= 0.55;

                if (f.Phase == MarketPhase.Breakout && f.MinuteTrendScore >= 65)
                    entryRatio *= 1.15;

                dynamicOrderAmount =
                    (int)Math.Max(0, available * Clamp(entryRatio, 0.02, 0.30));

                dynamicPositionAmount =
                    (int)Math.Max(
                        dynamicOrderAmount,
                        cash * Clamp(settings.DynamicMaxPositionCashRatio, 0.05, 0.60));

                if (f.Price > 0 && f.Price <= available)
                {
                    dynamicOrderAmount = (int)Math.Max(dynamicOrderAmount, f.Price);
                    dynamicPositionAmount = (int)Math.Max(dynamicPositionAmount, f.Price);
                }

                //if (settings.MaxTotalPositionAmount > 0)
                //{
                //    dynamicOrderAmount = Math.Min(dynamicOrderAmount, settings.MaxTotalPositionAmount);
                //    dynamicPositionAmount = Math.Min(dynamicPositionAmount, settings.MaxTotalPositionAmount);
                //}
            }

            dynamicOrderAmount = (int)(dynamicOrderAmount * marketSizeMultiplier);
            dynamicPositionAmount = (int)(dynamicPositionAmount * marketSizeMultiplier);

            if (f.Price > 0 && available >= f.Price && dynamicOrderAmount > 0)
                dynamicOrderAmount = (int)Math.Max(dynamicOrderAmount, f.Price);

            if (f.Price > 0 && available >= f.Price && dynamicPositionAmount > 0)
                dynamicPositionAmount = (int)Math.Max(dynamicPositionAmount, f.Price);

            t.MaxOrderAmount = Math.Max(0, dynamicOrderAmount);
            t.MaxPositionAmount = Math.Max(0, dynamicPositionAmount);

            if (settings.UseDynamicMarketThresholds)
            {
                t.StopLossRate = Clamp(volatility * 1.25, 0.35, 2.8);
                t.TakeProfitRate = Clamp(t.StopLossRate * 1.7, 0.65, 4.5);
                t.TrailingStartProfitRate = Clamp(t.TakeProfitRate * 0.55, 0.35, 2.0);
                t.TrailingStopRate = Clamp(t.StopLossRate * 0.75, 0.25, 2.2);

                if (t.IsVolatile)
                {
                    t.StopLossRate *= 1.15;
                    t.TrailingStopRate *= 1.15;
                }

                if (t.IsSideways)
                {
                    t.TakeProfitRate *= 0.85;
                    t.TrailingStartProfitRate *= 0.80;
                }
            }
            else
            {
                t.StopLossRate = Math.Abs(settings.StopLossRate);
                t.TakeProfitRate = Math.Abs(settings.TakeProfitRate);
                t.TrailingStartProfitRate = Math.Abs(settings.TrailingStartProfitRate);
                t.TrailingStopRate = Math.Abs(settings.TrailingStopRate);
            }

            t.StopLossRate = Clamp(t.StopLossRate * stopMultiplier, 0.25, 3.5);
            t.TakeProfitRate = Clamp(t.TakeProfitRate * takeMultiplier, 0.35, 5.5);
            t.TrailingStartProfitRate = Clamp(t.TrailingStartProfitRate * takeMultiplier, 0.25, 3.0);
            t.TrailingStopRate = Clamp(t.TrailingStopRate * trailingMultiplier, 0.18, 3.0);

            t.MinBreakoutPriceChange1m = Clamp(0.12 + volatility * 0.12, 0.12, 0.60);
            t.MinBreakoutPriceChange3m = Clamp(0.30 + volatility * 0.22, 0.30, 1.20);
            t.MinPullbackLower1m = -Clamp(0.18 + volatility * 0.20, 0.18, 0.80);
            t.MinPullbackUpper1m = Clamp(0.08 + volatility * 0.10, 0.08, 0.35);
            t.MinVolumeRatio = t.IsHighLiquidity ? 1.12 : Clamp(1.25 + volatility * 0.25, 1.25, 2.20);
            t.MinPriceMoveRatio = t.IsHighLiquidity ? 0.90 : Clamp(1.05 + volatility * 0.15, 1.05, 1.60);
            t.MinStrength = t.IsHighLiquidity ? 103 : Clamp(106 + volatility * 8.0, 106, 125);
            t.MinStrengthDelta = t.IsHighLiquidity ? 0.5 : Clamp(1.0 + volatility * 1.5, 1.0, 6.0);
            t.MinBidAskRatio = t.IsHighLiquidity ? 1.03 : Clamp(1.06 + volatility * 0.08, 1.06, 1.30);
            t.MinBidAskRatioRelative = t.IsHighLiquidity ? 1.04 : Clamp(1.08 + volatility * 0.10, 1.08, 1.35);
            t.MinBuyPressureScore = Clamp(56 + volatility * 8.0, 56, 75);
            t.MinMinuteTrendScore = t.IsHighLiquidity ? 55 : 58;

            t.WeakStrength = Clamp(96 - volatility * 4.0, 82, 96);
            t.WeakStrengthDelta = -Clamp(3.0 + volatility * 2.0, 3.0, 8.0);
            t.WeakBidAskRatio = Clamp(0.92 - volatility * 0.04, 0.78, 0.92);
            t.WeakBidAskRatioRelative = Clamp(0.96 - volatility * 0.03, 0.86, 0.96);
            t.WeakPriceChange1m = -Clamp(0.18 + volatility * 0.12, 0.18, 0.55);

            t.StrongContinuation =
                f.MinuteTrendScore >= t.MinMinuteTrendScore + 8 &&
                f.Strength >= t.MinStrength + 8 &&
                (f.BidAskRatioRelative >= t.MinBidAskRatioRelative ||
                 f.BidAskRatio >= t.MinBidAskRatio + 0.08) &&
                f.PriceChangeRate1m > -0.05 &&
                f.PriceMoveRatio >= t.MinPriceMoveRatio;

            t.EntryAllowed = true;

            if (!f.HasPosition)
            {
                bool highRange = f.RangePosition >= 0.88;
                bool extremeHighRange = f.RangePosition >= 0.94;

                bool strongFreshBreakout =
                    f.Phase == MarketPhase.Breakout &&
                    f.MinuteTrendScore >= t.MinMinuteTrendScore + 10 &&
                    f.Strength >= t.MinStrength + 8 &&
                    f.StrengthDelta >= t.MinStrengthDelta &&
                    f.PriceMoveRatio >= t.MinPriceMoveRatio &&
                    f.PriceChangeRate1m <= t.MinBreakoutPriceChange1m * 2.2;

                if (f.MarketTrend == MarketTrendState.Panic && f.RelativeStrength < 1.0)
                {
                    t.EntryAllowed = false;
                    t.EntryBlockReason =
                        $"시장 급락장 약세종목 차단: trend={f.MarketTrend}, RS={f.RelativeStrength:F2}, market={f.MarketAverageChangeRate:F2}%";
                }
                else if (f.MarketTrend == MarketTrendState.Bear && f.RelativeStrength < 0.3)
                {
                    t.EntryAllowed = false;
                    t.EntryBlockReason =
                        $"약세장 상대강도 부족: trend={f.MarketTrend}, RS={f.RelativeStrength:F2}, market={f.MarketAverageChangeRate:F2}%";
                }
                else if (t.IsSideways)
                {
                    t.EntryAllowed = false;
                    t.EntryBlockReason = "횡보/가격진행 부족: 순간압력 무시";
                }
                else if (f.Phase == MarketPhase.DownTrend || f.Phase == MarketPhase.Unstable)
                {
                    t.EntryAllowed = false;
                    t.EntryBlockReason = "하락/불안정 국면 매수 차단";
                }
                else if (f.Phase == MarketPhase.Overheated && f.RangePosition >= 0.75)
                {
                    t.EntryAllowed = false;
                    t.EntryBlockReason = $"과열 고점권 매수 차단: range={f.RangePosition:F2}";
                }
                else if (extremeHighRange && !strongFreshBreakout)
                {
                    t.EntryAllowed = false;
                    t.EntryBlockReason = $"극단 고점권 추격매수 차단: range={f.RangePosition:F2}";
                }
                else if (highRange &&
                         f.PriceChangeRate3m >= t.MinBreakoutPriceChange3m * 2.0 &&
                         !strongFreshBreakout)
                {
                    t.EntryAllowed = false;
                    t.EntryBlockReason =
                        $"고점권 상승확인 후 진입 차단: range={f.RangePosition:F2}, 3m={f.PriceChangeRate3m:F2}%";
                }
                else if (f.BuyPressureScore >= t.MinBuyPressureScore &&
                         f.PriceMoveRatio < t.MinPriceMoveRatio * 0.75)
                {
                    t.EntryAllowed = false;
                    t.EntryBlockReason = "체결압력 대비 가격진행 부족";
                }
            }

            t.CooldownSeconds = settings.CooldownSeconds;

            if (settings.UseDynamicMarketThresholds)
            {
                double liquidityFactor =
                    t.IsHighLiquidity
                        ? 0.70
                        : 1.00;

                double sidewaysFactor =
                    t.IsSideways
                        ? 1.60
                        : 1.00;

                double volatilityFactor =
                    t.IsVolatile
                        ? 1.25
                        : 1.00;

                double breakoutFactor =
                    f.Phase == MarketPhase.Breakout && f.TickDensityPerMinute >= 120
                        ? 0.80
                        : 1.00;

                double marketFactor =
                    f.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.StrongBull ? 0.75 :
                    f.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Bull ? 0.85 :
                    f.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Panic ? 1.50 :
                    f.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Bear ? 1.25 :
                    1.00;

                double baseCooldown =
                    settings.CooldownSeconds > 0
                        ? settings.CooldownSeconds
                        : Math.Max(8.0, 60.0 / Math.Max(1.0, f.TickDensityPerMinute / 20.0));

                int cd = (int)Math.Round(
                    baseCooldown *
                    liquidityFactor *
                    sidewaysFactor *
                    volatilityFactor *
                    breakoutFactor *
                    marketFactor);

                t.CooldownSeconds = ClampInt(cd, 8, 90);
            }

            t.ReEntryBlockSecondsAfterSell = settings.ReEntryBlockSecondsAfterSell;

            if (settings.UseDynamicMarketThresholds)
            {
                double baseReEntry =
                    settings.ReEntryBlockSecondsAfterSell > 0
                        ? settings.ReEntryBlockSecondsAfterSell
                        : t.CooldownSeconds * 5.0;

                double phaseFactor =
                    t.IsSideways ? 1.70 :
                    f.Phase == MarketPhase.Breakout && f.MinuteTrendScore >= 70 ? 0.70 :
                    f.Phase == MarketPhase.Pullback && f.MinuteTrendScore >= 55 ? 0.85 :
                    1.00;

                double volatilityFactor =
                    t.IsVolatile ? 1.30 : 1.00;

                double marketFactor =
                    f.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.StrongBull ? 0.75 :
                    f.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Bull ? 0.85 :
                    f.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Panic ? 1.60 :
                    f.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Bear ? 1.30 :
                    1.00;

                int re = (int)Math.Round(
                    baseReEntry *
                    phaseFactor *
                    volatilityFactor *
                    marketFactor);

                t.ReEntryBlockSecondsAfterSell = ClampInt(re, 45, 420);
            }

            return t;
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static int ClampInt(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
