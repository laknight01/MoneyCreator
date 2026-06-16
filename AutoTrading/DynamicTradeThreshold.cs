using System;

namespace MoneyCreator.AutoTrading
{
    public sealed class DynamicTradeThreshold
    {
        public int MinSignalScore { get; set; }
        public int CooldownSeconds { get; set; }
        public int ReEntryBlockSecondsAfterSell { get; set; }

        public int MaxOrderAmount { get; set; }
        public int MaxPositionAmount { get; set; }
        public int MinCashRemain { get; set; }

        public double StopLossRate { get; set; }
        public double TakeProfitRate { get; set; }
        public double TrailingStartProfitRate { get; set; }
        public double TrailingStopRate { get; set; }

        public double MinBreakoutPriceChange1m { get; set; }
        public double MinBreakoutPriceChange3m { get; set; }
        public double MinPullbackLower1m { get; set; }
        public double MinPullbackUpper1m { get; set; }
        public double MinVolumeRatio { get; set; }
        public double MinPriceMoveRatio { get; set; }
        public double MinStrength { get; set; }
        public double MinStrengthDelta { get; set; }
        public double MinBidAskRatio { get; set; }
        public double MinBidAskRatioRelative { get; set; }
        public double MinBuyPressureScore { get; set; }
        public double MinMinuteTrendScore { get; set; }

        public double WeakStrength { get; set; }
        public double WeakStrengthDelta { get; set; }
        public double WeakBidAskRatio { get; set; }
        public double WeakBidAskRatioRelative { get; set; }
        public double WeakPriceChange1m { get; set; }

        public bool IsSideways { get; set; }
        public bool IsHighLiquidity { get; set; }
        public bool IsVolatile { get; set; }
        public bool StrongContinuation { get; set; }
        public bool EntryAllowed { get; set; }
        public string EntryBlockReason { get; set; }
    }}
