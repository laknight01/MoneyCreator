using System;

namespace MoneyCreator.AutoTrading
{
    public sealed class TradeGate
    {
        public GateResult Check(FeatureSnapshot f, AutoTradeSettings settings)
        {
            if (settings == null || !settings.Enabled)
                return GateResult.Block("자동매매 OFF");

            if (f == null)
                return GateResult.Block("Feature 없음");

            if (string.IsNullOrWhiteSpace(f.Code))
                return GateResult.Block("종목코드 없음");

            if (f.Price <= 0)
                return GateResult.Block("현재가 없음");

            if (settings.RequireEnoughTickData)
            {
                bool hasTickData =
                    f.TickDensityPerMinute > 0 ||
                    f.CurrentTickVolume > 0 ||
                    f.AvgTickVolume > 0 ||
                    f.VolumeRatio > 0;

                if (!hasTickData)
                    return GateResult.Block("최근틱 가공데이터 부족");
            }

            if (!settings.AllowTradeWithoutMinuteData)
            {
                bool hasMinuteData =
                    f.MinuteTrendScore > 0 ||
                    f.PriceChangeRate1m != 0 ||
                    f.PriceChangeRate3m != 0 ||
                    f.PriceChangeRate5m != 0 ||
                    f.Phase != MarketPhase.Unknown;

                if (!hasMinuteData)
                    return GateResult.Block("분봉 가공데이터 부족");
            }

            if (settings.RequireHogaForBuy && !f.HasPosition)
            {
                bool hasHogaData =
                    f.BidAskRatio > 0 ||
                    f.BidAskRatioRelative > 0 ||
                    f.BuyPressureScore > 0;

                if (!hasHogaData)
                    return GateResult.Block("호가 가공데이터 부족");
            }

            if (f.HasPendingBuy || f.HasPendingSell || f.OpenOrderCount > 0)
                return GateResult.Block("미체결 주문 존재");

            var threshold = DynamicThresholdProvider.Create(f, settings);

            if (!f.HasPosition && !threshold.EntryAllowed)
                return GateResult.Block(threshold.EntryBlockReason);

            return GateResult.Pass();
        }
    }
}
