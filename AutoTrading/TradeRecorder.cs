using MoneyCreator.DataBase;
using MoneyCreator.Models;
using ScottPlot.Plottables;
using System;
using System.Data.Entity.Core.Common.CommandTrees.ExpressionBuilder;

namespace MoneyCreator.AutoTrading
{
    public sealed class TradeRecorder
    {
        private readonly StockDbManager _db;
        private readonly Action<string> _log;

        public TradeRecorder(StockDbManager db = null, Action<string> log = null)
        {
            _db = db;
            _log = log;
        }

        public void RecordFeature(FeatureSnapshot feature)
        {
            if (feature == null || string.IsNullOrWhiteSpace(feature.Code))
                return;

            if (string.IsNullOrWhiteSpace(feature.SnapshotId))
                feature.SnapshotId = Guid.NewGuid().ToString("N");

            var record = new FeatureSnapshotRecord
            {
                SnapshotId = feature.SnapshotId,
                Code = feature.Code,
                SnapshotTime = feature.Time == default(DateTime) ? DateTime.Now : feature.Time,
                Price = feature.Price,
                PriceChangeRate = feature.PriceChangeRate1m,
                Strength = feature.Strength,
                StrengthDelta = feature.StrengthDelta,
                VolumeAccel = feature.VolumeAccel,
                BidAskRatio = feature.BidAskRatio,
                BuyPressureScore = feature.BuyPressureScore,
                SecondTrend = feature.Phase.ToString(),
                MinuteTrend = feature.MinuteTrendScore.ToString("F2"),
                HourTrend = feature.HourTrendScore.ToString("F2"),
                Phase = feature.Phase.ToString(),
                IsSideways = feature.IsSideways,
                TrendStrength = feature.TrendStrength,
                VolatilityRate = feature.VolatilityRate,
                RangePosition = feature.RangePosition,
                TickCount5Min = (int)Math.Round(feature.TickDensityPerMinute * 5.0),
                SecondCandleCount = 0,
                MinuteCandleCount = feature.Snapshot == null || feature.Snapshot.MinuteCandles == null
                    ? 0
                    : feature.Snapshot.MinuteCandles.Count,
                SignalAction = feature.SignalAction,
                SignalScore = feature.SignalScore,
                StrategyName = feature.StrategyName,
                CreatedAt = DateTime.Now
            };
            //_log?.Invoke($"[ML수집] Feature 저장 {feature.Code} {DateTime.Now:HH:mm:ss} signal={feature.SignalScore}?.{feature.SignalAction}");
            _db?.EnqueueFeatureSnapshot(record);
        }

        public void RecordSignal(TradeSignal signal, FeatureSnapshot feature = null)
        {
            if (signal == null || signal.Action == TradeActionType.Hold)
                return;

            string snapshotId = null;

            if (feature != null)
                snapshotId = feature.SnapshotId;
            else if (signal.SnapshotFeature != null)
                snapshotId = signal.SnapshotFeature.SnapshotId;

            _db?.EnqueueSignalLog(new SignalLogRecord
            {
                Code = signal.Code,
                SignalTime = signal.CreatedAt,
                Action = signal.Action.ToString(),
                Score = signal.Score,
                StrategyName = signal.StrategyName,
                Reason = signal.Reason,
                AiUsed = false,
                AiApproved = false,
                AiReason = null,
                MlUsed = false,
                MlApproved = false,
                MlScore = signal.TiRexConfidence,
                SnapshotId = snapshotId,
                TiRexDirection = signal.TiRexDirection,
                TiRexConfidence = signal.TiRexConfidence,
                TiRexPredictedReturn = signal.TiRexPredictedReturn,
                CreatedAt = DateTime.Now
            });

            _log?.Invoke($"[Signal] {signal.Action} {signal.Code} score={signal.Score} / {signal.StrategyName} / {signal.Reason}");
        }

        public void RecordOutcomeLabel(SnapshotOutcomeLabelRecord label)
        {
            if (label == null)
                return;

            _db?.EnqueueSnapshotOutcomeLabel(label);
        }

        public void RecordOrderRequest(OrderRequest request)
        {
            if (request == null || request.Signal == null)
                return;

            _log?.Invoke($"[OrderRequest] {request.Signal.Action} {request.Signal.Code} qty={request.Signal.Quantity} dry={request.DryRun}");
        }

        public void RecordOrderResult(OrderRequest request, int result)
        {
            if (request == null || request.Signal == null)
                return;

            _log?.Invoke($"[OrderResult] {request.Signal.Code} result={result}");
        }
    }
}
