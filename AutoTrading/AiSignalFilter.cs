using MoneyCreator.Service;
using Newtonsoft.Json.Linq;
using System;
using System.Threading.Tasks;

namespace MoneyCreator.AutoTrading
{
    public interface IAiSignalFilter
    {
        Task<TradeSignal> ReviewAsync(TradeSignal signal, FeatureSnapshot feature, AutoTradeSettings settings);
    }

    public sealed class AiSignalFilter : IAiSignalFilter
    {
        private readonly AiJudgeService _ai;
        private DateTime _lastCall = DateTime.MinValue;

        public AiSignalFilter(AiJudgeService ai)
        {
            _ai = ai;
        }

        public async Task<TradeSignal> ReviewAsync(TradeSignal signal, FeatureSnapshot feature, AutoTradeSettings settings)
        {
            if (_ai == null || signal == null || feature == null || settings == null || !settings.UseAi)
                return signal;

            if (signal.Action == TradeActionType.Hold)
                return signal;

            if ((DateTime.Now - _lastCall).TotalSeconds < 3)
                return signal;

            bool shouldAsk =
                signal.Score < 85 ||
                feature.Phase == MarketPhase.Unstable ||
                feature.Phase == MarketPhase.Overheated;

            if (!shouldAsk)
                return signal;

            try
            {
                _lastCall = DateTime.Now;

                string system = "You are a stock scalping risk filter. Return JSON only: {\"approve\":true|false,\"score\":0-100,\"reason\":\"short Korean reason\"}.";
                string user =
                    $"Code={feature.Code}, Action={signal.Action}, RuleScore={signal.Score}, Price={feature.Price}, " +
                    $"R1m={feature.PriceChangeRate1m:F3}, R3m={feature.PriceChangeRate3m:F3}, R5m={feature.PriceChangeRate5m:F3}, " +
                    $"Strength={feature.Strength:F2}, StrengthDelta={feature.StrengthDelta:F2}, VolumeAccel={feature.VolumeAccel:F2}, " +
                    $"BidAskRatio={feature.BidAskRatio:F3}, BuyPressure={feature.BuyPressureScore:F1}, " +
                    $"MinuteTrend={feature.MinuteTrendScore:F1}, HourTrend={feature.HourTrendScore:F1}, ProfitRate={feature.ProfitRate:F2}, Phase={feature.Phase}.";

                string json = await _ai.ExecuteAsync(settings.AiModelFilter, system, user);
                var obj = JObject.Parse(json);

                bool approve = obj["approve"] == null ? true : obj["approve"].Value<bool>();
                int aiScore = obj["score"] == null ? signal.Score : obj["score"].Value<int>();
                string reason = obj["reason"] == null ? "AI 판단 없음" : obj["reason"].ToString();

                if (!approve)
                {
                    var hold = TradeSignal.Hold(signal.Code, "AI 보류: " + reason);
                    hold.StrategyName = "AiFilter";
                    hold.SnapshotFeature = null;
                    return hold;
                }

                signal.Score = Math.Min(100, Math.Max(signal.Score, aiScore));
                signal.Reason = signal.Reason + " / AI승인: " + reason;
                signal.SnapshotFeature = null;
                return signal;
            }
            catch
            {
                signal.SnapshotFeature = null;
                return signal;
            }
        }
    }
}
