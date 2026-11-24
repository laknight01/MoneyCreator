using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MoneyCreator.Models
{
    // ============================================================
    // DB 저장/ML.NET 학습 공용 모델
    // 위치: MoneyCreator.Shared/Models/DataRecords.cs
    // ============================================================


    public static class MinuteCandleSource
    {
        public const string Hist = "HIST";
        public const string Real = "REAL";
        public const string RealMinute = "REAL_MIN";
        public const string HistReal = "HIST+REAL";
    }

    public sealed class MinuteCandleRecord
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public DateTime CandleTime { get; set; }
        public string Interval { get; set; } = "1m";

        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Close { get; set; }
        public long Volume { get; set; }
        public long AccumulatedVolume { get; set; }

        public double Strength { get; set; }
        public long TotalAskQty { get; set; }
        public long TotalBidQty { get; set; }

        // HIST = TR 과거분봉, REAL = 실시간틱으로 확정한 분봉
        public string Source { get; set; } = "HIST";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }



    public sealed class ProgramTradeInfo
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public DateTime TradeTime { get; set; }
        public double CurrentPrice { get; set; }
        public long Volume { get; set; }

        public long ProgramSellAmount { get; set; }
        public long ProgramBuyAmount { get; set; }
        public long ProgramNetAmount { get; set; }
        public long ProgramNetAmountDelta { get; set; }

        public long ProgramSellQuantity { get; set; }
        public long ProgramBuyQuantity { get; set; }
        public long ProgramNetQuantity { get; set; }
        public long ProgramNetQuantityDelta { get; set; }

        // opt90008은 종목 시간별 프로그램 추이 기준으로 차익/비차익 분해값을 주지 않는다.
        // 추후 별도 TR이 붙으면 이 필드를 채우고, 현재는 0으로 보존한다.
        public long ArbitrageSell { get; set; }
        public long ArbitrageBuy { get; set; }
        public long ArbitrageNet { get; set; }
        public long NonArbitrageSell { get; set; }
        public long NonArbitrageBuy { get; set; }
        public long NonArbitrageNet { get; set; }

        public string Source { get; set; } = "REALTIME";
        public string Direction { get; set; } = "Flat";
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public long DisplayNetAmount => ProgramNetAmount;
        public long DisplayNetAmountDelta => ProgramNetAmountDelta;
        public long DisplayNetQuantity => ProgramNetQuantity;
        public long DisplayNetQuantityDelta => ProgramNetQuantityDelta;
    }

    public sealed class ProgramFlowSnapshotRecord
    {
        public string SnapshotId { get; set; } = Guid.NewGuid().ToString("N");
        public string Code { get; set; }
        public DateTime SnapshotTime { get; set; }

        public long LatestNetAmount { get; set; }
        public long LatestNetQuantity { get; set; }
        public long Delta1mAmount { get; set; }
        public long Delta3mAmount { get; set; }
        public long Delta5mAmount { get; set; }
        public long Delta10mAmount { get; set; }
        public long Delta1mQuantity { get; set; }
        public long Delta3mQuantity { get; set; }
        public long Delta5mQuantity { get; set; }
        public long Delta10mQuantity { get; set; }

        public double FlowScore { get; set; }
        public string FlowDirection { get; set; } = "WarmUp";
        public bool IsWarmedUp { get; set; }
        public string SourceState { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    public sealed class FeatureSnapshotRecord
    {
        public string SnapshotId { get; set; } = Guid.NewGuid().ToString("N");
        public string Code { get; set; }
        public DateTime SnapshotTime { get; set; }
        public double Price { get; set; }

        public double PriceChangeRate { get; set; }
        public double Strength { get; set; }
        public double StrengthDelta { get; set; }
        public double VolumeAccel { get; set; }
        public double BidAskRatio { get; set; }
        public double BuyPressureScore { get; set; }

        public double BuyVolume { get; set; }
        public double SellVolume { get; set; }
        public double BuyVolumeRatio { get; set; }
        public double SellVolumeRatio { get; set; }
        public double NetBuyVolume { get; set; }
        public double NetBuyVolumeRatio { get; set; }

        public double FrameChange30s { get; set; }
        public double FrameChange1m { get; set; }
        public double FrameChange3m { get; set; }
        public double FrameChange5m { get; set; }
        public double FrameVolumeRatio { get; set; }
        public double FrameBuyPressureScore { get; set; }
        public double FrameStrengthDelta { get; set; }

        public double MinuteChange15m { get; set; }
        public double MinuteChange30m { get; set; }
        public double MinuteChange60m { get; set; }
        public double TodayRangePosition { get; set; }
        public double TwoDayRangePosition { get; set; }
        public double MarketRiskScore { get; set; }
        public double RelativeStrength { get; set; }

        public int HoldingQty { get; set; }
        public double EntryPrice { get; set; }
        public double ProfitRate { get; set; }
        public double MaxProfitRate { get; set; }
        public double MinProfitRate { get; set; }
        public double DrawdownRate { get; set; }
        public double HoldingSeconds { get; set; }

        // 예상 수수료/세금 차감 후 판단/학습용 순수익 지표
        public double EstimatedRoundTripCostRate { get; set; }
        public double NetProfitRate { get; set; }
        public double NetMaxProfitRate { get; set; }
        public double NetDrawdownRate { get; set; }

        public string SecondTrend { get; set; }
        public string MinuteTrend { get; set; }
        public string HourTrend { get; set; }

        // 시장 국면/횡보/변동성 ML 학습용 수치
        public string Phase { get; set; }
        public bool IsSideways { get; set; }
        public double TrendStrength { get; set; }
        public double VolatilityRate { get; set; }
        public double RangePosition { get; set; }

        public int TickCount5Min { get; set; }
        public int SecondCandleCount { get; set; }
        public int MinuteCandleCount { get; set; }

        public string SignalAction { get; set; }
        public int SignalScore { get; set; }
        public string StrategyName { get; set; }
        public string TradeMode { get; set; }

        // ML 데이터 출처 구분값. Real/Replay 학습 데이터를 같은 DB에 저장해도 분리할 수 있게 한다.
        public string DataSourceType { get; set; } = "Real";
        public bool IsReplay { get; set; }
        public string MlSessionId { get; set; }

        // Local ML shadow 평가 결과 요약값.
        // 실제 세부 평가 로그는 LocalMlEvaluationLogs 테이블에 별도 저장한다.
        public bool LocalMlUsed { get; set; }
        public bool LocalMlAvailable { get; set; }
        public bool LocalMlAppliedToTrading { get; set; }
        public double LocalMlScore { get; set; }
        public double LocalMlConfidence { get; set; }
        public double LocalMlInfluenceRatio { get; set; }
        public string LocalMlDirection { get; set; }
        public string LocalMlReason { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    public sealed class SignalLogRecord
    {
        public string SignalId { get; set; } = Guid.NewGuid().ToString("N");
        public string Code { get; set; }
        public DateTime SignalTime { get; set; }
        public string Action { get; set; }
        public int Score { get; set; }
        public string StrategyName { get; set; }
        public string Reason { get; set; }

        public bool AiUsed { get; set; }
        public bool AiApproved { get; set; }
        public string AiReason { get; set; }

        public bool MlUsed { get; set; }
        public bool MlApproved { get; set; }
        public double MlScore { get; set; }

        // Local ML shadow 평가 결과 요약값.
        public bool LocalMlUsed { get; set; }
        public bool LocalMlAvailable { get; set; }
        public bool LocalMlAppliedToTrading { get; set; }
        public double LocalMlScore { get; set; }
        public double LocalMlConfidence { get; set; }
        public double LocalMlInfluenceRatio { get; set; }
        public string LocalMlDirection { get; set; }
        public string LocalMlReason { get; set; }

        public string SnapshotId { get; set; }
        public string TradeMode { get; set; }

        // TiRex prediction captured at signal time.
        public string TiRexDirection { get; set; }
        public double TiRexConfidence { get; set; }
        public double TiRexPredictedReturn { get; set; }

        // ML 데이터 출처 구분값.
        public string DataSourceType { get; set; } = "Real";
        public bool IsReplay { get; set; }
        public string MlSessionId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }


    public sealed class LocalMlEvaluationRecord
    {
        public string EvaluationId { get; set; } = Guid.NewGuid().ToString("N");
        public string SnapshotId { get; set; }
        public string SignalId { get; set; }
        public string Code { get; set; }
        public DateTime EvaluationTime { get; set; }

        // 모델/스키마 식별. 모델 교체 후에도 같은 테이블에 같이 저장하기 위한 확장 필드.
        public string ModelKey { get; set; }
        public string ModelName { get; set; }
        public string ModelVersion { get; set; }
        public string EvaluatorType { get; set; }
        public string FeatureSchemaVersion { get; set; }
        public string RuntimeMode { get; set; }

        // ML 데이터 출처 구분값. RuntimeMode(Trading/Shadow)와 분리한다.
        public string DataSourceType { get; set; } = "Real";
        public bool IsReplay { get; set; }
        public string MlSessionId { get; set; }

        // 실제 매매 반영 여부와 영향도.
        public bool IsEnabled { get; set; }
        public bool IsAvailable { get; set; }
        public bool IsAppliedToTrading { get; set; }
        public double InfluenceRatio { get; set; }

        // 룰 판단 전후 비교용.
        public string RuleAction { get; set; }
        public string FinalAction { get; set; }
        public int RuleScore { get; set; }
        public int FinalScore { get; set; }
        public string StrategyName { get; set; }
        public string TradeMode { get; set; }

        // ML 출력값. 현재는 shadow 로그용이며, 추후 모델이 늘어나도 같은 구조로 비교한다.
        public string Direction { get; set; }
        public double Score { get; set; }
        public double Confidence { get; set; }
        public double BuyQualityScore { get; set; }
        public double SellRiskScore { get; set; }
        public double AvoidScore { get; set; }
        public double ExpectedReturn { get; set; }
        public double ExpectedDrawdown { get; set; }
        public string Reason { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    public sealed class DecisionCandidateRecord
    {
        public string CandidateId { get; set; } = Guid.NewGuid().ToString("N");
        public string SnapshotId { get; set; }
        public string SignalId { get; set; }
        public string Code { get; set; }
        public DateTime DecisionTime { get; set; }
        public double Price { get; set; }

        public string ActionCandidate { get; set; }
        public string FinalAction { get; set; }
        public int SignalScore { get; set; }
        public string StrategyName { get; set; }
        public string TradeMode { get; set; }

        public double BaseFinal { get; set; }
        public double EntryScore { get; set; }
        public double ExitScore { get; set; }
        public double ExpectedMoveScore { get; set; }
        public double WeakFlowPenalty { get; set; }
        public double HighChasePenalty { get; set; }
        public double CostRecoveryHoldScore { get; set; }
        public double AdversePersist { get; set; }
        public double EffectiveCostRate { get; set; }

        public string Reason { get; set; }
        public string DataSourceType { get; set; } = "Real";
        public bool IsReplay { get; set; }
        public string MlSessionId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }


    public sealed class SnapshotOutcomeLabelRecord
    {
        public string OutcomeId { get; set; } = Guid.NewGuid().ToString("N");
        public string SnapshotId { get; set; }
        public string Code { get; set; }
        public DateTime SnapshotTime { get; set; }
        public DateTime LabelTime { get; set; }

        // ML 데이터 출처 구분값. FeatureSnapshot과 같은 세션으로 묶는다.
        public string DataSourceType { get; set; } = "Real";
        public bool IsReplay { get; set; }
        public string MlSessionId { get; set; }

        // DecisionCandidate와 연결되는 경우에만 채운다. 기존 FeatureSnapshot 라벨은 null/empty로 유지한다.
        public string CandidateId { get; set; }
        public string ActionCandidate { get; set; }
        public string FinalAction { get; set; }

        public double ReturnAfter30s { get; set; }
        public double ReturnAfter1m { get; set; }
        public double ReturnAfter3m { get; set; }
        public double ReturnAfter5m { get; set; }
        public double ReturnAfter10m { get; set; }

        public double MaxProfitAfter10m { get; set; }
        public double MaxLossAfter10m { get; set; }

        // 예상 왕복비용 차감 후 학습 라벨 판단용 순수익 지표
        public double EstimatedRoundTripCostRate { get; set; }
        public double NetReturnAfter30s { get; set; }
        public double NetReturnAfter1m { get; set; }
        public double NetReturnAfter3m { get; set; }
        public double NetReturnAfter5m { get; set; }
        public double NetReturnAfter10m { get; set; }
        public double NetMaxProfitAfter10m { get; set; }
        public double NetMaxLossAfter10m { get; set; }

        // ActionCandidate 방향으로 환산한 라벨. Buy는 상승, Sell은 하락을 유리 움직임으로 본다.
        public double ActionReturnAfter30s { get; set; }
        public double ActionReturnAfter1m { get; set; }
        public double ActionReturnAfter3m { get; set; }
        public double ActionReturnAfter5m { get; set; }
        public double ActionReturnAfter10m { get; set; }
        public double ActionMfeAfter10m { get; set; }
        public double ActionMaeAfter10m { get; set; }
        public double CostAdjustedOutcome { get; set; }

        public bool HitProfitFirst { get; set; }
        public bool HitStopFirst { get; set; }
        public double? ProfitHitSeconds { get; set; }
        public double? StopHitSeconds { get; set; }

        // ML.NET 기본 라벨 후보
        // LabelBuyGood = 1: 이 시점에 매수했으면 10분 안에 수익 기회가 좋았음
        // LabelAvoid   = 1: 이 시점은 피하는 게 좋았음
        public int LabelBuyGood { get; set; }
        public int LabelAvoid { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    public sealed class TradeResultRecord
    {
        public string TradeResultId { get; set; } = Guid.NewGuid().ToString("N");
        public string TradeGroupId { get; set; }
        public string Code { get; set; }
        public string Name { get; set; }
        public string StrategyName { get; set; }

        public DateTime EntryTime { get; set; }
        public DateTime ExitTime { get; set; }
        public double EntryPrice { get; set; }
        public double ExitPrice { get; set; }
        public int Quantity { get; set; }

        public double ProfitRate { get; set; }
        public double ProfitAmount { get; set; }
        public double MaxProfitRate { get; set; }
        public double MinProfitRate { get; set; }
        public double HoldSeconds { get; set; }

        // 실제 체결 수수료/세금 기준 손익.
        // ProfitAmount/ProfitRate는 기존 화면/성과 호환을 위해 순손익 기준으로 저장한다.
        public double GrossProfitAmount { get; set; }
        public double GrossProfitRate { get; set; }
        public double BuyFee { get; set; }
        public double SellFee { get; set; }
        public double SellTax { get; set; }
        public double TotalFeeTax { get; set; }
        public double NetProfitAmount { get; set; }
        public double NetProfitRate { get; set; }

        // ML.NET 라벨. 성공=1, 실패=0
        public int Label { get; set; }
        public string ExitReason { get; set; }
        public string TradeMode { get; set; }
        public string TiRexDirection { get; set; }
        public double TiRexConfidence { get; set; }
        public double TiRexPredictedReturn { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
    public sealed class DailyPerformanceRecord
    {
        public string TradeDate { get; set; }
        public int TradeCount { get; set; }
        public int WinCount { get; set; }
        public int LossCount { get; set; }
        public int FlatCount { get; set; }
        public double GrossProfit { get; set; }
        public double GrossLoss { get; set; }
        public double FeeTax { get; set; }
        public double NetProfit { get; set; }
        public double AvgProfitRate { get; set; }
        public double WinRate { get; set; }
        public double MaxProfit { get; set; }
        public double MaxLoss { get; set; }
        public double BestTradeRate { get; set; }
        public double WorstTradeRate { get; set; }
        public string Status { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    public sealed class TradePerformanceRow
    {
        public double ProfitAmount { get; set; }
        public double ProfitRate { get; set; }
        public double GrossProfitAmount { get; set; }
        public double TotalFeeTax { get; set; }
    }

    public sealed class PerformanceBuildStateRecord
    {
        public string TradeDate { get; set; }
        public string Status { get; set; }
        public DateTime LastAttemptAt { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime UpdatedAt { get; set; }
    }



    public sealed class DecisionOutcomeAnalysisSummaryRecord
    {
        public string Code { get; set; }
        public DateTime FromTime { get; set; }
        public DateTime ToTime { get; set; }
        public string CategoryName { get; set; }
        public string ActionCandidate { get; set; }
        public int CandidateCount { get; set; }

        public double AvgSignalScore { get; set; }
        public double AvgBaseFinal { get; set; }
        public double AvgEntryScore { get; set; }
        public double AvgExitScore { get; set; }
        public double AvgExpectedMoveScore { get; set; }
        public double AvgWeakFlowPenalty { get; set; }
        public double AvgHighChasePenalty { get; set; }
        public double AvgCostRecoveryHoldScore { get; set; }
        public double AvgAdversePersist { get; set; }

        public double AvgActionReturnAfter1m { get; set; }
        public double AvgActionReturnAfter3m { get; set; }
        public double AvgActionReturnAfter5m { get; set; }
        public double AvgActionReturnAfter10m { get; set; }
        public double AvgActionMfeAfter10m { get; set; }
        public double AvgActionMaeAfter10m { get; set; }
        public double AvgCostAdjustedOutcome { get; set; }

        public double PositiveOutcomeRate { get; set; }
        public double ProfitFirstRate { get; set; }
        public double StopFirstRate { get; set; }
        public double LabelBuyGoodRate { get; set; }
        public double LabelAvoidRate { get; set; }
    }

}
