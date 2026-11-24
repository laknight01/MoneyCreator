using System;
using MoneyCreator.Models;

namespace MoneyCreator.ML
{
    internal static class ExecutionRiskFeatureMapper
    {
        public static bool TryGetValue(FeatureSnapshotRecord feature, string featureName, out double value)
        {
            value = 0.0;
            if (feature == null || string.IsNullOrWhiteSpace(featureName))
                return false;

            switch (featureName)
            {
                case "F_BidAskRatio": value = feature.BidAskRatio; return true;
                case "F_BuyPressureScore": value = feature.BuyPressureScore; return true;
                case "F_BuyVolume": value = feature.BuyVolume; return true;
                case "F_BuyVolumeRatio": value = feature.BuyVolumeRatio; return true;
                case "F_FrameBuyPressureScore": value = feature.FrameBuyPressureScore; return true;
                case "F_FrameVolumeRatio": value = feature.FrameVolumeRatio; return true;
                case "F_IsSideways": value = feature.IsSideways ? 1.0 : 0.0; return true;
                case "F_MinuteCandleCount": value = feature.MinuteCandleCount; return true;
                case "F_NetBuyVolume": value = feature.NetBuyVolume; return true;
                case "F_NetBuyVolumeRatio": value = feature.NetBuyVolumeRatio; return true;
                case "F_PriceChangeRate": value = feature.PriceChangeRate; return true;
                case "F_RangePosition": value = feature.RangePosition; return true;
                case "F_SecondCandleCount": value = feature.SecondCandleCount; return true;
                case "F_SellVolume": value = feature.SellVolume; return true;
                case "F_SellVolumeRatio": value = feature.SellVolumeRatio; return true;
                case "F_Strength": value = feature.Strength; return true;
                case "F_StrengthDelta": value = feature.StrengthDelta; return true;
                case "F_TickCount5Min": value = feature.TickCount5Min; return true;
                case "F_TodayRangePosition": value = feature.TodayRangePosition; return true;
                case "F_TrendStrength": value = feature.TrendStrength; return true;
                case "F_TwoDayRangePosition": value = feature.TwoDayRangePosition; return true;
                case "F_VolatilityRate": value = feature.VolatilityRate; return true;
                case "F_VolumeAccel": value = feature.VolumeAccel; return true;
                default: return false;
            }
        }
    }
}
