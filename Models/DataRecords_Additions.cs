using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MoneyCreator.Models
{
    // ============================================================
    // DB 저장/ML.NET 학습 공용 모델
    // 위치 추천: Models/CommonModel.cs 하단 또는 Models/AutoDataRecords.cs
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

        public string SnapshotId { get; set; }

        // TiRex prediction captured at signal time.
        public string TiRexDirection { get; set; }
        public double TiRexConfidence { get; set; }
        public double TiRexPredictedReturn { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }


    public sealed class SnapshotOutcomeLabelRecord
    {
        public string OutcomeId { get; set; } = Guid.NewGuid().ToString("N");
        public string SnapshotId { get; set; }
        public string Code { get; set; }
        public DateTime SnapshotTime { get; set; }
        public DateTime LabelTime { get; set; }

        public double ReturnAfter30s { get; set; }
        public double ReturnAfter1m { get; set; }
        public double ReturnAfter3m { get; set; }
        public double ReturnAfter5m { get; set; }
        public double ReturnAfter10m { get; set; }

        public double MaxProfitAfter10m { get; set; }
        public double MaxLossAfter10m { get; set; }

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

        // ML.NET 라벨. 성공=1, 실패=0
        public int Label { get; set; }
        public string ExitReason { get; set; }
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
    }

    public sealed class PerformanceBuildStateRecord
    {
        public string TradeDate { get; set; }
        public string Status { get; set; }
        public DateTime LastAttemptAt { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime UpdatedAt { get; set; }
    }


}
