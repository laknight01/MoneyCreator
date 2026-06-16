using MoneyCreator.Models;
using System;
using static MoneyCreator.Models.MarketIndexInfo;

namespace MoneyCreator.AutoTrading
{
    public static class MarketEnvironmentEvaluator
    {
        public static MarketEnvironment Create(MarketIndexInfo kospi, MarketIndexInfo kosdaq)
        {
            double kospiRate = kospi == null ? 0 : kospi.ChangeRate;
            double kosdaqRate = kosdaq == null ? 0 : kosdaq.ChangeRate;

            bool hasKospi = kospi != null && kospi.UpdatedAt != default(DateTime);
            bool hasKosdaq = kosdaq != null && kosdaq.UpdatedAt != default(DateTime);

            double avg;

            if (hasKospi && hasKosdaq)
                avg = (kospiRate + kosdaqRate) / 2.0;
            else if (hasKospi)
                avg = kospiRate;
            else if (hasKosdaq)
                avg = kosdaqRate;
            else
                avg = 0;

            double divergence = Math.Abs(kospiRate - kosdaqRate);
            double downside = Math.Max(0, -avg);
            double upside = Math.Max(0, avg);

            double risk = Clamp(downside * 22.0 + divergence * 4.0, 0, 100);
            double support = Clamp(upside * 18.0 - divergence * 2.0, 0, 100);

            MarketTrendState trend;

            if (risk >= 70)
                trend = MarketTrendState.Panic;
            else if (risk >= 40)
                trend = MarketTrendState.Bear;
            else if (risk >= 18)
                trend = MarketTrendState.Weak;
            else if (support >= 55)
                trend = MarketTrendState.StrongBull;
            else if (support >= 25)
                trend = MarketTrendState.Bull;
            else
                trend = MarketTrendState.Neutral;

            return new MarketEnvironment
            {
                UpdatedAt = DateTime.Now,
                KospiChangeRate = kospiRate,
                KosdaqChangeRate = kosdaqRate,
                AverageChangeRate = avg,
                MarketRiskScore = risk,
                MarketSupportScore = support,
                Trend = trend,
                Description = BuildDescription(trend, avg, risk, support),

                EntryScoreAdjustment = CalculateEntryScoreAdjustment(risk, support),

                PositionSizeMultiplier = Clamp(1.0 - risk / 130.0 + support / 220.0, 0.35, 1.25),
                StopLossMultiplier = Clamp(1.0 - risk / 180.0 + support / 260.0, 0.55, 1.25),
                TakeProfitMultiplier = Clamp(1.0 - risk / 140.0 + support / 110.0, 0.45, 1.70),
                TrailingMultiplier = Clamp(1.0 - risk / 180.0 + support / 180.0, 0.55, 1.35)
            };
        }

        private static int CalculateEntryScoreAdjustment(double risk, double support)
        {
            double value = risk * 0.18 - support * 0.08;

            if (value > 25)
                value = 25;

            if (value < -10)
                value = -10;

            return (int)Math.Round(value);
        }

        private static string BuildDescription(
            MarketTrendState trend,
            double avg,
            double risk,
            double support)
        {
            return $"{trend} / 시장평균={avg:F2}% / 위험={risk:F0} / 지지={support:F0}";
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min)
                return min;

            if (value > max)
                return max;

            return value;
        }
    }
}