namespace MoneyCreator.AutoTrading
{
    public sealed class MarketPhaseDetector
    {
        public MarketPhase Detect(FeatureSnapshot f, MarketSnapshot snapshot)
        {
            if (f == null)
                return MarketPhase.Unknown;

            if (f.Price <= 0)
                return MarketPhase.Unknown;

            if (f.OverheatScore >= 70 && f.StrengthDelta < 0)
                return MarketPhase.Overheated;

            if (f.PriceChangeRate3m <= -0.8 || (f.MinuteTrendScore <= 25 && f.Strength < 90))
                return MarketPhase.DownTrend;

            if (f.PriceChangeRate1m >= 0.25 && f.PriceChangeRate3m >= 0.5 && f.BuyPressureScore >= 65)
                return MarketPhase.Breakout;

            if (f.MinuteTrendScore >= 60 && f.PriceChangeRate1m > -0.25 && f.PriceChangeRate1m < 0.15 && f.StrengthDelta >= 0)
                return MarketPhase.Pullback;

            if (f.BuyPressureScore >= 60 && f.PriceChangeRate3m >= 0)
                return MarketPhase.Accumulation;

            if (f.VolatilityScore < 20 && f.BuyPressureScore < 55)
                return MarketPhase.Quiet;

            return MarketPhase.Unstable;
        }
    }
}
