using MoneyCreator.Models;
using System;
using System.Collections.Generic;
using static MoneyCreator.Models.MarketIndexInfo;

namespace MoneyCreator.AutoTrading
{
    public enum TradeActionType { Hold = 0, Buy = 1, Sell = 2 }

    public enum MarketPhase
    {
        Unknown = 0,
        Quiet = 1,
        Accumulation = 2,
        Breakout = 3,
        Pullback = 4,
        Overheated = 5,
        DownTrend = 6,
        Unstable = 7
    }
    public enum AutoTradeAnalysisLogMode
    {
        TradeOnly = 0,
        All = 1
    }
    public enum TrendState
    {
        Unknown = 0,
        Flat = 1,
        Up = 2,
        Down = 3,
        StrongUp = 4,
        StrongDown = 5
    }

    public enum OrderLifecycleState
    {
        None = 0,
        Requested = 1,
        Submitted = 2,
        PartiallyFilled = 3,
        Filled = 4,
        Cancelled = 5,
        Rejected = 6,
        Timeout = 7
    }

    public sealed class AutoTradeSettings
    {
        public bool UseFullCashSizingForTest { get; set; } = false;
        public bool Enabled { get; set; }
        public bool DryRun { get; set; } = true;
        public bool AllowBuy { get; set; } = true;
        public bool AllowSell { get; set; } = true;
        public bool UseAi { get; set; } = false;
        public bool UseMlNet { get; set; } = false;

        // ML.NET 학습 데이터 수집
        // - 매매하지 않은 HOLD 판단도 일정 간격으로 FeatureSnapshot에 저장
        // - SnapshotOutcomeLabels는 이후 30초/1분/3분/5분/10분 결과를 라벨로 저장
        public bool EnableMlDataCollection { get; set; } = true;
        public int FeatureSnapshotIntervalSeconds { get; set; } = 2;
        public int OutcomeLabelMaxHorizonSeconds { get; set; } = 600;

        public int DefaultOrderQty { get; set; } = 1;

        // 아래 값들은 이제 직접 매매 기준이라기보다 DynamicThresholdProvider의 안전 기본값/폴백값이다.
        public int MinSignalScore { get; set; } = 70;
        public int CooldownSeconds { get; set; } = 20;

        public int MaxOrderAmount { get; set; } = 100000;
        public int MaxPositionAmount { get; set; } = 300000;
        public int MaxTotalPositionAmount { get; set; } = 1000000;
        public int MinCashRemain { get; set; } = 30000;

        // true면 주문금액/진입점수/쿨다운/익절손절을 시장상황과 종목 특성에 맞춰 계산한다.
        public bool UseDynamicMarketThresholds { get; set; } = true;
        public double DynamicFirstEntryCashRatio { get; set; } = 0.12;
        public double DynamicMaxPositionCashRatio { get; set; } = 0.25;
        public double DynamicMinCashRemainRatio { get; set; } = 0.05;

        public double DailyLossLimitAmount { get; set; } = 50000;
        public int MaxDailyOrderCountPerCode { get; set; } = 100;
        public int MaxConsecutiveLossCount { get; set; } = 3;

        public double StopLossRate { get; set; } = 1.0;
        public double TakeProfitRate { get; set; } = 1.5;
        public double TrailingStopRate { get; set; } = 0.7;
        public double TrailingStartProfitRate { get; set; } = 0.8;
        public int ReEntryBlockSecondsAfterSell { get; set; } = 180;
        public double TakeProfitStrongHoldStrength { get; set; } = 130;
        public double TakeProfitStrongHoldBidAskRelative { get; set; } = 1.2;
        public double TakeProfitStrongHoldVolumeRatio { get; set; } = 1.5;
        public int MaxHoldSeconds { get; set; } = 1800;

        // ============================================================
        // 분할 익절 설정
        // - 익절 신호에서는 일부만 매도하고 잔여 물량은 트레일링/약화/손절 신호까지 보유한다.
        // - 손절/트레일링/약화/시간초과 신호는 전량 매도한다.
        // ============================================================
        public bool UsePartialTakeProfitSell { get; set; } = true;
        public double PartialTakeProfitSellRatio { get; set; } = 0.5;
        public int MinRemainQtyAfterPartialTakeProfit { get; set; } = 1;
        public int MinQtyForPartialTakeProfit { get; set; } = 2;


        public string HogaType { get; set; } = "00";
        public string AiModelFilter { get; set; } = "llama-3-8b";
        public double MlPassScore { get; set; } = 0.70;
        public double MlAiReviewScore { get; set; } = 0.55;

        public bool RequireHogaForBuy { get; set; } = true;
        public bool RequireEnoughTickData { get; set; } = true;
        public bool AllowTradeWithoutMinuteData { get; set; } = false;

        public bool AllowAdditionalBuy { get; set; } = true;

        // 추가매수는 보수적으로
        public int MaxAdditionalBuyCountPerCode { get; set; } = 2;
        public double AdditionalBuyMinProfitRate { get; set; } = -1.5;
        public double AdditionalBuyMaxProfitRate { get; set; } = 2.0;
        public double AdditionalBuyCashRatio { get; set; } = 0.15;
        public double AdditionalBuyPositionRatio { get; set; } = 0.5;
        public double AdditionalBuyMinVolumeRatio { get; set; } = 1.3;
        public double AdditionalBuyMinBidAskRatioRelative { get; set; } = 1.1;
        public double AdditionalBuyMinStrength { get; set; } = 100;

        // 자동매매 ON 종목 기준 분산
        public bool UsePortfolioCashSizing { get; set; } = true;

        // 전체 예수금 중 자동매매 최대 사용 비율
        public double MaxPortfolioCashUseRatio { get; set; } = 0.80;

        // 종목당 기본 최대 비율.
        // 자동매매 ON 창이 4개면 보통 0.25와 동일하게 동작.
        public double MaxCashRatioPerSymbol { get; set; } = 0.25;

        // 점수 가중 최대 배율
        public double MaxScorePositionMultiplier { get; set; } = 1.5;

        // 최소 주문 금액
        public int MinOrderAmount { get; set; } = 30000;

        // 자동매매 분석 로그 출력 범위
        // TradeOnly: 주문/차단/신호 등 매매 관련 로그만
        // All: 테스트용으로 매틱 Feature/Signal 로그까지
        public AutoTradeAnalysisLogMode AnalysisLogMode { get; set; } = AutoTradeAnalysisLogMode.TradeOnly;
    }

    public sealed class CandleData
    {
        public string Code { get; set; }
        public DateTime Time { get; set; }
        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Close { get; set; }
        public long Volume { get; set; }
        public double StrengthSum { get; set; }
        public int StrengthCount { get; set; }
        public long TotalAskQty { get; set; }
        public long TotalBidQty { get; set; }
        public double AvgStrength => StrengthCount <= 0 ? 0 : StrengthSum / StrengthCount;
    }

    public sealed class MarketFrameSnapshot
    {
        public string Code { get; set; }
        public TrackedSymbol LastTick { get; set; }
        public List<CandleData> SecondCandles { get; set; } = new List<CandleData>();
        public List<CandleData> MinuteCandles { get; set; } = new List<CandleData>();
        public List<CandleData> HourCandles { get; set; } = new List<CandleData>();
        public TrendState SecondTrend { get; set; }
        public TrendState MinuteTrend { get; set; }
        public TrendState HourTrend { get; set; }
        public double SecondBuyPressure { get; set; }
        public double MinuteBuyPressure { get; set; }
        public bool IsEnoughData { get; set; }
    }

    public sealed class MarketSnapshot
    {
        public string Code { get; set; }
        public DateTime Time { get; set; }
        public double CurrentPrice { get; set; }
        public TrackedSymbol LastTick { get; set; }
        public List<TrackedSymbol> RecentTicks { get; set; } = new List<TrackedSymbol>();
        public List<TrackedSymbol> MinuteCandles { get; set; } = new List<TrackedSymbol>();
        public List<CandleData> HourCandles { get; set; } = new List<CandleData>();
        public HogaData Hoga { get; set; }
        public int HoldingQty { get; set; }
        public double EntryPrice { get; set; }
        public double ProfitRate { get; set; }

        // ============================================================
        // 보유 중 분석/로그용 상태
        // - 자동매매가 왜 보유/청산 판단을 했는지 역추적하기 위한 값들
        // - AutoTradeController.UpdateHoldingAnalysis()에서 실시간 계산된다.
        // ============================================================
        public double HoldSeconds { get; set; }
        public int HoldTickCount { get; set; }
        public double MaxProfitRate { get; set; }
        public double MinProfitRate { get; set; }
        public double DrawdownRate { get; set; }
        public int ExitScore { get; set; }
        public string HoldReason { get; set; }

        // 동적 기준값 로그용
        public double DynamicStopLossRate { get; set; }
        public double DynamicTakeProfitRate { get; set; }
        public double DynamicTrailingStopRate { get; set; }

        public long Cash { get; set; }
        public List<OrderInfo> OpenOrders { get; set; } = new List<OrderInfo>();
        public DateTime? LastOrderTime { get; set; }
        public double DailyRealizedProfit { get; set; }
        public int DailyOrderCountForCode { get; set; }
        public int ConsecutiveLossCount { get; set; }
        public MarketFrameSnapshot Frame { get; set; }
        public MarketEnvironment MarketEnvironment { get; set; }
    }

    public sealed class FeatureSnapshot
    {
        public string SnapshotId { get; set; } = Guid.NewGuid().ToString("N");
        public string Code { get; set; }
        public DateTime Time { get; set; }
        public double Price { get; set; }
        public double PriceChangeRate1m { get; set; }
        public double PriceChangeRate3m { get; set; }
        public double PriceChangeRate5m { get; set; }
        public double VolumeAccel { get; set; }
        public double AvgTickVolume { get; set; }
        public double CurrentTickVolume { get; set; }
        public double VolumeRatio { get; set; }
        public double TickDensityPerMinute { get; set; }
        public double PriceMoveRatio { get; set; }
        public double Strength { get; set; }
        public double StrengthDelta { get; set; }
        public double BidAskRatio { get; set; }
        public double BuyPressureScore { get; set; }
        public double MinuteTrendScore { get; set; }
        public double HourTrendScore { get; set; }
        public double VolatilityScore { get; set; }
        public double OverheatScore { get; set; }
        public bool HasPosition { get; set; }
        public int HoldingQty { get; set; }
        public double EntryPrice { get; set; }
        public double ProfitRate { get; set; }

        // ============================================================
        // 보유 중 분석/로그용 상태
        // - 자동매매가 왜 보유/청산 판단을 했는지 역추적하기 위한 값들
        // - AutoTradeController.UpdateHoldingAnalysis()에서 실시간 계산된다.
        // ============================================================
        public double HoldSeconds { get; set; }
        public int HoldTickCount { get; set; }
        public double MaxProfitRate { get; set; }
        public double MinProfitRate { get; set; }
        public double DrawdownRate { get; set; }
        public int ExitScore { get; set; }
        public string HoldReason { get; set; }

        // 동적 기준값 로그용
        public double DynamicStopLossRate { get; set; }
        public double DynamicTakeProfitRate { get; set; }
        public double DynamicTrailingStopRate { get; set; }

        public long Cash { get; set; }
        public int OpenOrderCount { get; set; }
        public bool HasPendingBuy { get; set; }
        public bool HasPendingSell { get; set; }
        public double AvgBidAskRatio { get; set; }
        public double BidAskRatioRelative { get; set; }
        public MarketPhase Phase { get; set; }

        // ML 학습용 시장 상태 수치
        public bool IsSideways { get; set; }
        public double TrendStrength { get; set; }
        public double VolatilityRate { get; set; }
        public double RangePosition { get; set; }

        public string SignalAction { get; set; }
        public int SignalScore { get; set; }
        public string StrategyName { get; set; }
        public string SignalReason { get; set; }

        public MarketSnapshot Snapshot { get; set; }

        public MarketTrendState MarketTrend { get; set; }

        public double KospiChangeRate { get; set; }
        public double KosdaqChangeRate { get; set; }
        public double MarketAverageChangeRate { get; set; }

        public double MarketRiskScore { get; set; }
        public double MarketSupportScore { get; set; }

        public double RelativeStrength { get; set; }

        public int MarketEntryScoreAdjustment { get; set; }

        public double MarketPositionSizeMultiplier { get; set; } = 1.0;
        public double MarketStopLossMultiplier { get; set; } = 1.0;
        public double MarketTakeProfitMultiplier { get; set; } = 1.0;
        public double MarketTrailingMultiplier { get; set; } = 1.0;
    }

    public sealed class TickFeature
    {
        public string Code { get; set; }
        public DateTime Time { get; set; }
        public double Price { get; set; }
        public double PrevPrice { get; set; }
        public double Volume { get; set; }
        public double PrevVolume { get; set; }
        public double Strength { get; set; }
        public double PrevStrength { get; set; }
        public long TotalAskQty { get; set; }
        public long TotalBidQty { get; set; }
        public double BidAskRatio { get; set; }
        public double StrengthDelta { get; set; }
        public double VolumeAccel { get; set; }
        public double PriceChangeRate { get; set; }
        public double BuyPressureScore { get; set; }
        public MarketPhase Phase { get; set; }
        public TrackedSymbol Raw { get; set; }
    }

    public sealed class TradeSignal
    {
        public string Code { get; set; }
        public TradeActionType Action { get; set; }
        public int Score { get; set; }
        public string Reason { get; set; }
        public string StrategyName { get; set; }
        public double Price { get; set; }
        public int Quantity { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public TickFeature Feature { get; set; }
        public string TiRexDirection { get; set; }
        public double TiRexConfidence { get; set; }
        public double TiRexPredictedReturn { get; set; }
        public FeatureSnapshot SnapshotFeature { get; set; }

        public static TradeSignal Hold(string code, string reason, TickFeature feature = null)
        {
            return new TradeSignal
            {
                Code = code,
                Action = TradeActionType.Hold,
                Score = 0,
                Reason = reason,
                StrategyName = "HOLD",
                Feature = feature,
                SnapshotFeature = null,
                Price = feature == null ? 0 : feature.Price
            };
        }

        public static TradeSignal Hold(string code, string reason, TickFeature feature, FeatureSnapshot snapshotFeature)
        {
            return new TradeSignal
            {
                Code = code,
                Action = TradeActionType.Hold,
                Score = 0,
                Reason = reason,
                StrategyName = "HOLD",
                Feature = feature,
                SnapshotFeature = null,
                Price = snapshotFeature != null
                    ? snapshotFeature.Price
                    : feature == null ? 0 : feature.Price
            };
        }
    }

    public sealed class OrderRequest
    {
        public string WindowId { get; set; }
        public string ScreenNo { get; set; }
        public string AccountNo { get; set; }
        public TradeSignal Signal { get; set; }
        public int OrderType { get; set; }
        public string HogaType { get; set; }
        public bool DryRun { get; set; }
        public string DedupKey { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // 예수금 초과 방지용 스냅샷
        public long CashSnapshot { get; set; }
        public long MinCashRemainSnapshot { get; set; }
        public long ReservedBuyAmount { get; set; }

        public long OrderAmount
        {
            get
            {
                if (Signal == null || Signal.Price <= 0 || Signal.Quantity <= 0)
                    return 0;

                return (long)Math.Ceiling(Signal.Price * Signal.Quantity);
            }
        }
    }

    public sealed class GateResult
    {
        public bool Passed { get; private set; }
        public string Reason { get; private set; }
        public static GateResult Pass() => new GateResult { Passed = true, Reason = "PASS" };
        public static GateResult Block(string reason) => new GateResult { Passed = false, Reason = reason };
    }

    public sealed class RiskResult
    {
        public bool Passed { get; private set; }
        public string Reason { get; private set; }
        public static RiskResult Pass() => new RiskResult { Passed = true, Reason = "PASS" };
        public static RiskResult Block(string reason) => new RiskResult { Passed = false, Reason = reason };
    }
}
