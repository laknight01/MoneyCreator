using System;
using System.Collections.Generic;
using System.Linq;

namespace MoneyCreator.AutoTrading
{
    public interface IEntryRule
    {
        string Name { get; }
        TradeSignal Evaluate(FeatureSnapshot f, AutoTradeSettings settings);
    }

    public interface IExitRule
    {
        string Name { get; }
        TradeSignal Evaluate(FeatureSnapshot f, AutoTradeSettings settings, RiskManager riskManager);
    }

    public abstract class BaseRule
    {
        protected TradeSignal Buy(FeatureSnapshot f, int score, string reason, string strategyName)
        {
            return new TradeSignal
            {
                Code = f.Code,
                Action = TradeActionType.Buy,
                Score = score,
                Reason = reason,
                StrategyName = strategyName,
                Price = f.Price,
                Quantity = 0,
                SnapshotFeature = null
            };
        }

        public static TradeSignal Sell(FeatureSnapshot f, int score, string reason, string name)
        {
            return new TradeSignal
            {
                Code = f.Code,
                Action = TradeActionType.Sell,
                Score = score,
                Reason = reason,
                StrategyName = name,
                Price = f.Price,
                SnapshotFeature = null
            };
        }

        protected TradeSignal Hold(FeatureSnapshot f, string reason, string strategyName)
        {
            return new TradeSignal
            {
                Code = f.Code,
                Action = TradeActionType.Hold,
                Score = 0,
                Reason = reason,
                StrategyName = strategyName,
                Price = f.Price,
                Quantity = 0,
                SnapshotFeature = null
            };
        }

        protected static int ClampScore(int value)
        {
            if (value < 0) return 0;
            if (value > 100) return 100;
            return value;
        }

        protected static int GetHighRangePenalty(FeatureSnapshot f)
        {
            if (f == null)
                return 0;

            if (f.RangePosition >= 0.92)
                return 35;

            if (f.RangePosition >= 0.85)
                return 22;

            if (f.RangePosition >= 0.78)
                return 12;

            return 0;
        }

        protected static int GetPullbackRangeBonus(FeatureSnapshot f)
        {
            if (f == null)
                return 0;

            if (f.RangePosition >= 0.25 && f.RangePosition <= 0.62)
                return 18;

            if (f.RangePosition > 0.62 && f.RangePosition <= 0.75)
                return 8;

            return 0;
        }
    }

    public sealed class BreakoutEntryRule : BaseRule, IEntryRule
    {
        public string Name => "BreakoutEntry";

        public TradeSignal Evaluate(FeatureSnapshot f, AutoTradeSettings settings)
        {
            var t = DynamicThresholdProvider.Create(f, settings);
            if (!t.EntryAllowed)
                return Hold(f, t.EntryBlockReason, Name);

            int score = 0;

            if (f.Phase == MarketPhase.Breakout) score += 14;
            if (f.MinuteTrendScore >= t.MinMinuteTrendScore) score += 12;
            if (f.PriceChangeRate1m >= t.MinBreakoutPriceChange1m) score += 10;
            if (f.PriceChangeRate3m >= t.MinBreakoutPriceChange3m) score += 10;
            if (f.Strength >= t.MinStrength) score += 10;
            if (f.StrengthDelta >= t.MinStrengthDelta) score += 8;
            if (f.BidAskRatio >= t.MinBidAskRatio || f.BidAskRatioRelative >= t.MinBidAskRatioRelative) score += 11;
            if (f.VolumeRatio >= t.MinVolumeRatio) score += 9;
            else if (f.VolumeAccel > 0 && t.IsHighLiquidity) score += 4;
            if (f.PriceMoveRatio >= t.MinPriceMoveRatio) score += 10;

            int highRangePenalty = GetHighRangePenalty(f);

            // 돌파매수는 허용하되, 이미 고점권이면 강하게 감점한다.
            if (highRangePenalty > 0)
                score -= highRangePenalty;

            if (f.Phase == MarketPhase.Overheated)
                score -= 20;

            // 3분 상승이 이미 많이 나온 뒤 고점권이면 추격매수로 본다.
            if (f.RangePosition >= 0.80 &&
                f.PriceChangeRate3m >= t.MinBreakoutPriceChange3m * 2.0)
            {
                score -= 18;
            }

            // 순간압력만 튄 경우는 감점. 가격이 따라와야 진입한다.
            if (f.BuyPressureScore >= t.MinBuyPressureScore && f.PriceMoveRatio < t.MinPriceMoveRatio)
                score -= 10;

            score = ClampScore(score);

            string reason =
                $"동적돌파: score={score}/{t.MinSignalScore}, " +
                $"range={f.RangePosition:F2}, highPenalty={highRangePenalty}, " +
                $"1m={f.PriceChangeRate1m:F2}%>={t.MinBreakoutPriceChange1m:F2}, " +
                $"3m={f.PriceChangeRate3m:F2}%>={t.MinBreakoutPriceChange3m:F2}, " +
                $"vol={f.VolumeRatio:F2}>={t.MinVolumeRatio:F2}, " +
                $"move={f.PriceMoveRatio:F2}>={t.MinPriceMoveRatio:F2}, " +
                $"str={f.Strength:F1}>={t.MinStrength:F1}, phase={f.Phase}";

            return score >= t.MinSignalScore
                ? Buy(f, score, reason, Name)
                : Hold(f, reason, Name);
        }
    }

    public sealed class PullbackEntryRule : BaseRule, IEntryRule
    {
        public string Name => "PullbackEntry";

        public TradeSignal Evaluate(FeatureSnapshot f, AutoTradeSettings settings)
        {
            var t = DynamicThresholdProvider.Create(f, settings);
            if (!t.EntryAllowed)
                return Hold(f, t.EntryBlockReason, Name);

            int score = 0;

            if (f.Phase == MarketPhase.Pullback) score += 22;
            if (f.Phase == MarketPhase.Accumulation) score += 10;

            if (f.MinuteTrendScore >= t.MinMinuteTrendScore - 5) score += 16;

            if (f.PriceChangeRate1m >= t.MinPullbackLower1m &&
                f.PriceChangeRate1m <= t.MinPullbackUpper1m)
            {
                score += 18;
            }

            if (f.PriceChangeRate3m >= -t.MinBreakoutPriceChange3m * 0.7)
                score += 9;

            if (f.Strength >= t.MinStrength - 8) score += 11;
            if (f.StrengthDelta >= 0) score += 15;

            if (f.BidAskRatio >= t.MinBidAskRatio ||
                f.BidAskRatioRelative >= t.MinBidAskRatioRelative)
            {
                score += 12;
            }

            if (f.BuyPressureScore >= t.MinBuyPressureScore - 5) score += 11;

            if (f.PriceMoveRatio >= t.MinPriceMoveRatio * 0.75)
                score += 9;

            int rangeBonus = GetPullbackRangeBonus(f);
            score += rangeBonus;

            int highRangePenalty = GetHighRangePenalty(f);
            if (highRangePenalty > 0)
                score -= highRangePenalty;

            if (f.Phase == MarketPhase.Overheated)
                score -= 25;

            score = ClampScore(score);

            string reason =
                $"동적눌림: score={score}/{t.MinSignalScore}, " +
                $"range={f.RangePosition:F2}, rangeBonus={rangeBonus}, highPenalty={highRangePenalty}, " +
                $"추세={f.MinuteTrendScore:F0}>={t.MinMinuteTrendScore:F0}, " +
                $"1m={f.PriceChangeRate1m:F2}% [{t.MinPullbackLower1m:F2},{t.MinPullbackUpper1m:F2}], " +
                $"3m={f.PriceChangeRate3m:F2}%, " +
                $"압력={f.BuyPressureScore:F0}>={t.MinBuyPressureScore:F0}, " +
                $"상대호가={f.BidAskRatioRelative:F2}, phase={f.Phase}";

            return score >= t.MinSignalScore
                ? Buy(f, score, reason, Name)
                : Hold(f, reason, Name);
        }
    }

    public sealed class VolumeSpikeEntryRule : BaseRule, IEntryRule
    {
        public string Name => "VolumeSpikeEntry";

        public TradeSignal Evaluate(FeatureSnapshot f, AutoTradeSettings settings)
        {
            var t = DynamicThresholdProvider.Create(f, settings);
            if (!t.EntryAllowed)
                return Hold(f, t.EntryBlockReason, Name);

            int score = 0;

            if (f.VolumeRatio >= t.MinVolumeRatio * 1.4) score += 16;
            else if (f.VolumeRatio >= t.MinVolumeRatio) score += 11;
            else if (t.IsHighLiquidity && f.VolumeAccel > 0) score += 6;

            if (f.PriceMoveRatio >= t.MinPriceMoveRatio * 1.15) score += 15;
            else if (f.PriceMoveRatio >= t.MinPriceMoveRatio) score += 10;

            if (f.BuyPressureScore >= t.MinBuyPressureScore + 8) score += 15;
            else if (f.BuyPressureScore >= t.MinBuyPressureScore) score += 9;

            if (f.Strength >= t.MinStrength + 8) score += 10;
            else if (f.Strength >= t.MinStrength) score += 7;

            if (f.BidAskRatioRelative >= t.MinBidAskRatioRelative) score += 10;
            else if (f.BidAskRatio >= t.MinBidAskRatio) score += 7;

            if (f.Phase == MarketPhase.Accumulation)
                score += 12;
            else if (f.Phase == MarketPhase.Breakout)
                score += 6;

            int rangeBonus = 0;
            if (f.RangePosition >= 0.25 && f.RangePosition <= 0.70)
                rangeBonus = 10;

            score += rangeBonus;

            int highRangePenalty = GetHighRangePenalty(f);
            if (highRangePenalty > 0)
                score -= highRangePenalty;

            if (f.Phase == MarketPhase.Overheated)
                score -= 25;

            // 거래량만 튀고 가격이 못 가면 오탐 가능성이 높다.
            if (f.VolumeRatio >= t.MinVolumeRatio && f.PriceMoveRatio < t.MinPriceMoveRatio * 0.80)
                score -= 15;

            score = ClampScore(score);

            string reason =
                $"동적거래량: score={score}/{t.MinSignalScore}, " +
                $"range={f.RangePosition:F2}, rangeBonus={rangeBonus}, highPenalty={highRangePenalty}, " +
                $"vol={f.VolumeRatio:F2}>={t.MinVolumeRatio:F2}, " +
                $"move={f.PriceMoveRatio:F2}>={t.MinPriceMoveRatio:F2}, " +
                $"틱밀도={f.TickDensityPerMinute:F0}/분, " +
                $"압력={f.BuyPressureScore:F0}>={t.MinBuyPressureScore:F0}, " +
                $"고유동={t.IsHighLiquidity}, phase={f.Phase}";

            return score >= t.MinSignalScore
                ? Buy(f, score, reason, Name)
                : Hold(f, reason, Name);
        }
    }

    public sealed class StopLossExitRule : BaseRule, IExitRule
    {
        public string Name => "StopLossExit";
        public TradeSignal Evaluate(FeatureSnapshot f, AutoTradeSettings settings, RiskManager riskManager)
        {
            if (!riskManager.ShouldStopLoss(f, settings))
                return TradeSignal.Hold(f.Code, "손절 조건 아님", null, f);

            var t = DynamicThresholdProvider.Create(f, settings);
            return Sell(f, 100, $"동적손절: 수익률={f.ProfitRate:F2}% <= -{t.StopLossRate:F2}%, 시장상태={f.Phase}, 강도변화={f.StrengthDelta:F1}", Name);
        }
    }

    public sealed class RiskExitRule : BaseRule, IExitRule
    {
        public string Name => "RiskExit";

        public TradeSignal Evaluate(FeatureSnapshot f, AutoTradeSettings settings, RiskManager riskManager)
        {
            if (f == null || settings == null || !f.HasPosition)
                return TradeSignal.Hold(f?.Code, "보유 없음");

            int minuteScore = riskManager.CalculateMinuteExitScore(f, settings);
            int tickScore = riskManager.CalculateTickExitScore(f, settings);
            int holdScore = riskManager.CalculateHoldScore(f, settings);

            double minuteWeight;
            double tickWeight;

            ResolveExitWeights(f, out minuteWeight, out tickWeight);

            int exitScore = (int)Math.Round(
                (minuteScore * minuteWeight) +
                (tickScore * tickWeight));

            if (holdScore >= 65 && exitScore < 90)
            {
                return TradeSignal.Hold(
                    f.Code,
                    $"분봉보유 우선: exit={exitScore}, hold={holdScore}, " +
                    $"minute={minuteScore}, tick={tickScore}, " +
                    $"w={minuteWeight:F2}/{tickWeight:F2}, " +
                    $"수익률={f.ProfitRate:F2}%, 보유={f.HoldSeconds:F0}s, " +
                    $"phase={f.Phase}, minuteTrend={f.MinuteTrendScore:F1}, " +
                    $"trend={f.TrendStrength:F1}, strength={f.Strength:F1}, " +
                    $"dStrength={f.StrengthDelta:F1}");
            }

            int threshold = GetSellThreshold(f);

            if (exitScore < threshold)
            {
                return TradeSignal.Hold(
                    f.Code,
                    $"상태청산 조건 부족: exit={exitScore}/{threshold}, hold={holdScore}, " +
                    $"minute={minuteScore}, tick={tickScore}, " +
                    $"w={minuteWeight:F2}/{tickWeight:F2}, " +
                    $"수익률={f.ProfitRate:F2}%, 보유={f.HoldSeconds:F0}s");
            }

            return Sell(
                f,
                exitScore,
                $"상태청산: exit={exitScore}/{threshold}, hold={holdScore}, " +
                $"minute={minuteScore}, tick={tickScore}, " +
                $"w={minuteWeight:F2}/{tickWeight:F2}, " +
                $"수익률={f.ProfitRate:F2}%, 고점이탈={f.DrawdownRate:F2}%, " +
                $"보유={f.HoldSeconds:F0}s, phase={f.Phase}, " +
                $"minuteTrend={f.MinuteTrendScore:F1}, trend={f.TrendStrength:F1}, " +
                $"strength={f.Strength:F1}, dStrength={f.StrengthDelta:F1}, " +
                $"pressure={f.BuyPressureScore:F0}, relHoga={f.BidAskRatioRelative:F2}",
                Name);
        }

        private static void ResolveExitWeights(
      FeatureSnapshot f,
      out double minuteWeight,
      out double tickWeight)
        {
            minuteWeight = 0.70;
            tickWeight = 0.30;

            if (f == null)
                return;

            bool strongMarket =
                f.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Bull ||
                f.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.StrongBull;

            bool weakMarket =
                f.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Bear ||
                f.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Panic;

            bool strongMinute =
                f.MinuteTrendScore >= 60 ||
                f.TrendStrength >= 55 ||
                f.Phase == MarketPhase.Breakout;

            bool weakMinute =
                f.MinuteTrendScore <= 40 ||
                f.TrendStrength <= 35 ||
                f.Phase == MarketPhase.DownTrend ||
                f.Phase == MarketPhase.Unstable;

            if (strongMarket && strongMinute)
            {
                minuteWeight = 0.85;
                tickWeight = 0.15;
                return;
            }

            if (strongMinute)
            {
                minuteWeight = 0.80;
                tickWeight = 0.20;
                return;
            }

            if (weakMarket && weakMinute)
            {
                minuteWeight = 0.60;
                tickWeight = 0.40;
                return;
            }

            if (weakMinute)
            {
                minuteWeight = 0.65;
                tickWeight = 0.35;
            }
        }

        private static int GetSellThreshold(FeatureSnapshot f)
        {
            if (f == null)
                return 84;

            bool strongMarket =
                f.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Bull ||
                f.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.StrongBull;

            bool weakMarket =
                f.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Bear ||
                f.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Panic;

            bool strongMinute =
                f.MinuteTrendScore >= 60 ||
                f.TrendStrength >= 55 ||
                f.Phase == MarketPhase.Breakout;

            bool weakMinute =
                f.MinuteTrendScore <= 40 ||
                f.TrendStrength <= 35 ||
                f.Phase == MarketPhase.DownTrend ||
                f.Phase == MarketPhase.Unstable;

            int threshold = 84;

            if (strongMarket && strongMinute)
                threshold += 6;
            else if (strongMinute)
                threshold += 3;

            if (weakMarket && weakMinute)
                threshold -= 6;
            else if (weakMinute)
                threshold -= 3;

            if (f.ProfitRate < 0 && weakMinute)
                threshold -= 2;

            if (threshold < 70) threshold = 70;
            if (threshold > 95) threshold = 95;

            return threshold;
        }
    }



    public sealed class TimeoutExitRule : BaseRule, IExitRule
    {
        public string Name => "TimeoutExit";
        public TradeSignal Evaluate(FeatureSnapshot f, AutoTradeSettings settings, RiskManager riskManager)
        {
            // 진입시간 추적은 OrderStateManager/TradeRecorder에서 확장한다.
            return TradeSignal.Hold(f.Code, "보유시간 청산 미적용", null, f);
        }
    }

    internal static class RuleUtil
    {
        public static TradeSignal Best(IEnumerable<TradeSignal> signals)
        {
            if (signals == null)
                return null;

            return signals
                .Where(x => x != null && x.Action != TradeActionType.Hold)
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();
        }

        public static TradeSignal Sell(FeatureSnapshot f, int score, string reason, string name)
        {
            return new TradeSignal
            {
                Code = f.Code,
                Action = TradeActionType.Sell,
                Score = score,
                Reason = reason,
                StrategyName = name,
                Price = f.Price,
                SnapshotFeature = f
            };
        }
    }

    public sealed class EmergencyExitRule : BaseRule, IExitRule
    {
        public string Name => "EmergencyExit";

        public TradeSignal Evaluate(FeatureSnapshot f, AutoTradeSettings settings, RiskManager riskManager)
        {
            if (f == null || settings == null || !f.HasPosition)
                return TradeSignal.Hold(f?.Code, "보유 없음", null, f);

            int score = riskManager.CalculateEmergencyExitScore(f, settings);

            if (score < 85)
            {
                return TradeSignal.Hold(
                    f.Code,
                    $"긴급청산 조건 부족: emergency={score}/85, " +
                    $"수익률={f.ProfitRate:F2}%, 1m={f.PriceChangeRate1m:F2}%, " +
                    $"strength={f.Strength:F1}, dStrength={f.StrengthDelta:F1}, " +
                    $"pressure={f.BuyPressureScore:F0}, relHoga={f.BidAskRatioRelative:F2}, " +
                    $"market={f.MarketTrend}",
                    null,
                    f);
            }

            return Sell(
                f,
                score,
                $"긴급청산: emergency={score}/85, 수익률={f.ProfitRate:F2}%, " +
                $"1m={f.PriceChangeRate1m:F2}%, 3m={f.PriceChangeRate3m:F2}%, " +
                $"strength={f.Strength:F1}, dStrength={f.StrengthDelta:F1}, " +
                $"pressure={f.BuyPressureScore:F0}, relHoga={f.BidAskRatioRelative:F2}, " +
                $"market={f.MarketTrend}, phase={f.Phase}",
                Name);
        }
    }
}
