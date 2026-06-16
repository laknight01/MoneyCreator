using System;
using System.Collections.Generic;
using System.Linq;

namespace MoneyCreator.AutoTrading
{
    public sealed class SignalEngine
    {
        private readonly TradeGate _tradeGate;
        private readonly RiskManager _riskManager;
        private readonly List<IEntryRule> _entryRules;
        private readonly List<IExitRule> _exitRules;

        public SignalEngine(
            TradeGate tradeGate,
            RiskManager riskManager,
            IEnumerable<IEntryRule> entryRules,
            IEnumerable<IExitRule> exitRules)
        {
            _tradeGate = tradeGate ?? new TradeGate();
            _riskManager = riskManager ?? new RiskManager();
            _entryRules = entryRules == null ? new List<IEntryRule>() : entryRules.ToList();
            _exitRules = exitRules == null ? new List<IExitRule>() : exitRules.ToList();
        }

        public TradeSignal Evaluate(FeatureSnapshot feature, AutoTradeSettings settings)
        {
            if (feature == null)
                return null;

            var gate = _tradeGate.Check(feature, settings);
            if (!gate.Passed)
                return TradeSignal.Hold(feature.Code, gate.Reason);

            var riskBefore = _riskManager.CheckBeforeSignal(feature, settings);
            if (!riskBefore.Passed)
                return TradeSignal.Hold(feature.Code, riskBefore.Reason);

            TradeSignal signal;

            if (feature.HasPosition)
            {
                signal = EvaluateExit(feature, settings);

                if (signal == null || signal.Action == TradeActionType.Hold)
                    signal = EvaluateEntry(feature, settings);
            }
            else
            {
                signal = EvaluateEntry(feature, settings);
            }

            if (signal == null)
                return TradeSignal.Hold(feature.Code, "신호 없음");

            signal.SnapshotFeature = null;

            if (signal.Action == TradeActionType.Hold)
                return signal;

            var risk = _riskManager.CheckSignal(signal, feature, settings);
            if (!risk.Passed)
                return TradeSignal.Hold(feature.Code, risk.Reason);

            signal.SnapshotFeature = null;
            return signal;
        }

        private TradeSignal EvaluateEntry(FeatureSnapshot f, AutoTradeSettings settings)
        {
            if (settings == null || !settings.AllowBuy)
                return TradeSignal.Hold(f.Code, "매수 비허용");

            var candidates = _entryRules
                .Select(x => SafeEvaluateEntry(x, f, settings))
                .Where(x => x != null)
                .ToList();

            TradeSignal best = RuleUtil.Best(candidates);

            if (best == null)
                return TradeSignal.Hold(f.Code, "매수 조건 부족");

            best.SnapshotFeature = null;
            return best;
        }

        private TradeSignal EvaluateExit(FeatureSnapshot f, AutoTradeSettings settings)
        {
            if (settings == null || !settings.AllowSell)
                return TradeSignal.Hold(f.Code, "매도 비허용");

            foreach (var rule in _exitRules)
            {
                TradeSignal s = SafeEvaluateExit(rule, f, settings);

                if (s != null)
                    s.SnapshotFeature = null;

                if (s != null && s.Action == TradeActionType.Sell)
                    return s;
            }

            return TradeSignal.Hold(f.Code, "청산 조건 부족");
        }

        private TradeSignal SafeEvaluateEntry(IEntryRule rule, FeatureSnapshot f, AutoTradeSettings settings)
        {
            try
            {
                var signal = rule.Evaluate(f, settings);
                if (signal != null)
                    signal.SnapshotFeature = null;

                return signal;
            }
            catch (Exception ex)
            {
                return TradeSignal.Hold(f.Code, rule.Name + " 오류: " + ex.Message);
            }
        }

        private TradeSignal SafeEvaluateExit(IExitRule rule, FeatureSnapshot f, AutoTradeSettings settings)
        {
            try
            {
                var signal = rule.Evaluate(f, settings, _riskManager);
                if (signal != null)
                    signal.SnapshotFeature = null;

                return signal;
            }
            catch (Exception ex)
            {
                return TradeSignal.Hold(f.Code, rule.Name + " 오류: " + ex.Message);
            }
        }

        public void MarkSellCompleted(string code)
        {
            _riskManager.MarkSellCompleted(code);
        }

        public void Reset(string code = null)
        {
            _riskManager.Reset(code);
        }
    }
}
