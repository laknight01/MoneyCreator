using MoneyCreator.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace MoneyCreator.AutoTrading
{
    /// <summary>
    /// FeatureSnapshot 시점 이후 가격 흐름을 추적해서 ML.NET 라벨을 만든다.
    /// 핵심 원칙:
    /// - Feature = 판단 당시 데이터
    /// - Label = 판단 이후 결과
    /// </summary>
    public sealed class OutcomeLabelTracker
    {
        private readonly TradeRecorder _recorder;
        private readonly Func<AutoTradeSettings> _settingsProvider;
        private readonly ConcurrentDictionary<string, List<PendingOutcome>> _pendingByCode =
            new ConcurrentDictionary<string, List<PendingOutcome>>();

        public OutcomeLabelTracker(TradeRecorder recorder, Func<AutoTradeSettings> settingsProvider)
        {
            _recorder = recorder;
            _settingsProvider = settingsProvider;
        }

        public void Track(FeatureSnapshot feature)
        {
            if (feature == null || string.IsNullOrWhiteSpace(feature.Code) || feature.Price <= 0)
                return;

            var settings = _settingsProvider == null ? null : _settingsProvider();
            if (settings != null && !settings.EnableMlDataCollection)
                return;

            var list = _pendingByCode.GetOrAdd(feature.Code, _ => new List<PendingOutcome>());
            lock (list)
            {
                list.Add(new PendingOutcome(feature));
            }
        }

        public void OnTick(TrackedSymbol tick)
        {
            if (tick == null || string.IsNullOrWhiteSpace(tick.Code) || tick.LastPrice <= 0)
                return;

            if (!_pendingByCode.TryGetValue(tick.Code, out var list))
                return;

            var settings = _settingsProvider == null ? null : _settingsProvider();
            int maxHorizon = settings == null || settings.OutcomeLabelMaxHorizonSeconds <= 0
                ? 600
                : settings.OutcomeLabelMaxHorizonSeconds;

            List<SnapshotOutcomeLabelRecord> completed = null;

            lock (list)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var p = list[i];
                    p.Update(tick);

                    if ((tick.LastUpdated - p.SnapshotTime).TotalSeconds >= maxHorizon)
                    {
                        if (completed == null)
                            completed = new List<SnapshotOutcomeLabelRecord>();

                        completed.Add(p.ToRecord());
                        list.RemoveAt(i);
                    }
                }
            }

            if (completed == null)
                return;

            foreach (var record in completed)
                _recorder?.RecordOutcomeLabel(record);
        }

        public void FlushPending(string code = null)
        {
            List<SnapshotOutcomeLabelRecord> records = new List<SnapshotOutcomeLabelRecord>();

            if (string.IsNullOrWhiteSpace(code))
            {
                foreach (var key in _pendingByCode.Keys.ToList())
                    FlushPendingCore(key, records);
            }
            else
            {
                FlushPendingCore(code, records);
            }

            foreach (var record in records)
                _recorder?.RecordOutcomeLabel(record);
        }

        private void FlushPendingCore(string code, List<SnapshotOutcomeLabelRecord> records)
        {
            if (string.IsNullOrWhiteSpace(code) || records == null)
                return;

            if (!_pendingByCode.TryRemove(code, out var list) || list == null)
                return;

            lock (list)
            {
                foreach (var pending in list)
                    records.Add(pending.ToRecord());

                list.Clear();
            }
        }

        public void Reset(string code = null)
        {
            // 종목 전환/창 종료/전체 리셋 때 pending 라벨을 그냥 버리지 않고,
            // 현재까지 관측된 가격 기준으로 부분 라벨을 저장한 뒤 정리한다.
            FlushPending(code);
        }

        private sealed class PendingOutcome
        {
            private static readonly int[] Horizons = { 30, 60, 180, 300, 600 };
            private readonly Dictionary<int, double?> _returnByHorizon = Horizons.ToDictionary(x => x, _ => (double?)null);

            public string SnapshotId { get; }
            public string Code { get; }
            public DateTime SnapshotTime { get; }
            public double EntryPrice { get; }
            public double LastPrice { get; private set; }
            public double MaxPrice { get; private set; }
            public double MinPrice { get; private set; }
            public DateTime LastTickTime { get; private set; }
            public double? ProfitHitSeconds { get; private set; }
            public double? StopHitSeconds { get; private set; }

            public PendingOutcome(FeatureSnapshot f)
            {
                SnapshotId = f.SnapshotId;
                Code = f.Code;
                SnapshotTime = f.Time == default(DateTime) ? DateTime.Now : f.Time;
                EntryPrice = f.Price;
                LastPrice = f.Price;
                MaxPrice = f.Price;
                MinPrice = f.Price;
                LastTickTime = SnapshotTime;
            }

            public void Update(TrackedSymbol tick)
            {
                if (tick.LastUpdated < SnapshotTime)
                    return;

                LastPrice = tick.LastPrice;
                LastTickTime = tick.LastUpdated;
                if (tick.LastPrice > MaxPrice) MaxPrice = tick.LastPrice;
                if (tick.LastPrice < MinPrice) MinPrice = tick.LastPrice;

                double elapsed = (tick.LastUpdated - SnapshotTime).TotalSeconds;
                double ret = Rate(tick.LastPrice);

                foreach (int h in Horizons)
                {
                    if (!_returnByHorizon[h].HasValue && elapsed >= h)
                        _returnByHorizon[h] = ret;
                }

                if (!ProfitHitSeconds.HasValue && ret >= 1.0)
                    ProfitHitSeconds = elapsed;

                if (!StopHitSeconds.HasValue && ret <= -0.8)
                    StopHitSeconds = elapsed;
            }

            public SnapshotOutcomeLabelRecord ToRecord()
            {
                double maxProfit = Rate(MaxPrice);
                double maxLoss = Rate(MinPrice);
                double return10m = _returnByHorizon[600] ?? Rate(LastPrice);

                bool hitProfitFirst = ProfitHitSeconds.HasValue &&
                                      (!StopHitSeconds.HasValue || ProfitHitSeconds.Value <= StopHitSeconds.Value);
                bool hitStopFirst = StopHitSeconds.HasValue &&
                                    (!ProfitHitSeconds.HasValue || StopHitSeconds.Value < ProfitHitSeconds.Value);

                int labelBuyGood = maxProfit >= 1.0 && maxLoss > -0.8 ? 1 : 0;
                int labelAvoid = hitStopFirst || return10m <= 0 ? 1 : 0;

                return new SnapshotOutcomeLabelRecord
                {
                    OutcomeId = Guid.NewGuid().ToString("N"),
                    SnapshotId = SnapshotId,
                    Code = Code,
                    SnapshotTime = SnapshotTime,
                    LabelTime = LastTickTime,
                    ReturnAfter30s = _returnByHorizon[30] ?? 0,
                    ReturnAfter1m = _returnByHorizon[60] ?? 0,
                    ReturnAfter3m = _returnByHorizon[180] ?? 0,
                    ReturnAfter5m = _returnByHorizon[300] ?? 0,
                    ReturnAfter10m = return10m,
                    MaxProfitAfter10m = maxProfit,
                    MaxLossAfter10m = maxLoss,
                    HitProfitFirst = hitProfitFirst,
                    HitStopFirst = hitStopFirst,
                    ProfitHitSeconds = ProfitHitSeconds,
                    StopHitSeconds = StopHitSeconds,
                    LabelBuyGood = labelBuyGood,
                    LabelAvoid = labelAvoid,
                    CreatedAt = DateTime.Now
                };
            }

            private double Rate(double price)
            {
                if (EntryPrice <= 0)
                    return 0;

                return (price - EntryPrice) / EntryPrice * 100.0;
            }
        }
    }
}
