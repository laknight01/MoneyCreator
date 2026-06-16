using System;
using System.Collections.Concurrent;
using System.Linq;

namespace MoneyCreator.AutoTrading
{
    public sealed class OrderRequestFactory
    {
        private static readonly ConcurrentDictionary<string, bool> _partialTakeProfitDone
            = new ConcurrentDictionary<string, bool>();

        private readonly Func<string> _getWindowId;
        private readonly Func<string> _getScreenNo;
        private readonly Func<string> _getAccountNo;
        private readonly Func<int> _getActiveAutoTradeSlotCount;

        public OrderRequestFactory(Func<string> getWindowId, Func<string> getScreenNo, Func<string> getAccountNo, Func<int> getActiveAutoTradeSlotCount)
        {
            _getWindowId = getWindowId;
            _getScreenNo = getScreenNo;
            _getAccountNo = getAccountNo;
            _getActiveAutoTradeSlotCount = getActiveAutoTradeSlotCount;
        }
        public OrderRequest Create(TradeSignal signal, FeatureSnapshot feature, AutoTradeSettings settings)
        {
            if (signal == null || feature == null || settings == null)
                return null;

            if (signal.Action == TradeActionType.Hold)
                return null;

            int orderType = signal.Action == TradeActionType.Buy ? 1 : 2;
            int activeSlotCount = GetActiveSlotCount();

            int quantity = signal.Action == TradeActionType.Buy
                ? CalculateBuyQuantity(feature, settings, activeSlotCount)
                : CalculateSellQuantity(signal, feature, settings);

            if (signal.Action == TradeActionType.Buy)
                ClearPartialTakeProfitState(signal.Code);

            if (quantity <= 0)
                return null;

            signal.Quantity = quantity;

            var threshold = DynamicThresholdProvider.Create(feature, settings);

            long cash = Math.Max(0, feature.Cash);
            long minRemain = threshold == null ? settings.MinCashRemain : threshold.MinCashRemain;
            long orderAmount = (long)Math.Ceiling(signal.Price * signal.Quantity);

            return new OrderRequest
            {
                WindowId = Safe(_getWindowId),
                ScreenNo = Safe(_getScreenNo),
                AccountNo = Safe(_getAccountNo),
                Signal = signal,
                OrderType = orderType,
                HogaType = settings.HogaType,
                DryRun = settings.DryRun,
                DedupKey = signal.Code + ":" + signal.Action + ":" + signal.StrategyName,

                CashSnapshot = cash,
                MinCashRemainSnapshot = Math.Max(0, minRemain),
                ReservedBuyAmount = signal.Action == TradeActionType.Buy ? orderAmount : 0
            };
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min)
                return min;

            if (value > max)
                return max;

            return value;
        }

        private static double GetScoreMultiplier(int score, AutoTradeSettings settings)
        {
            double max = settings == null ? 1.5 : settings.MaxScorePositionMultiplier;

            if (score >= 90)
                return Math.Min(max, 1.5);

            if (score >= 80)
                return Math.Min(max, 1.2);

            return 1.0;
        }

        private int GetActiveSlotCount()
        {
            try
            {
                if (_getActiveAutoTradeSlotCount == null)
                    return 1;

                return Math.Max(1, _getActiveAutoTradeSlotCount());
            }
            catch
            {
                return 1;
            }
        }

        private static int CalculateBuyQuantity(
     FeatureSnapshot feature,
     AutoTradeSettings settings,
     int activeSlotCount)
        {
            if (feature == null || settings == null || feature.Price <= 0)
                return 0;

            var threshold = DynamicThresholdProvider.Create(feature, settings);

            long cash = Math.Max(0, feature.Cash);
            long minRemain = Math.Max(0, threshold.MinCashRemain);
            long available = Math.Max(0, cash - minRemain);

            if (available < feature.Price)
                return 0;

            int currentPositionAmount = (int)Math.Max(0, feature.HoldingQty * feature.Price);

            int amount = settings.UseFullCashSizingForTest
                ? (int)available
                : CalculateDynamicPortfolioBuyAmount(
                    feature,
                    settings,
                    threshold,
                    cash,
                    available,
                    currentPositionAmount,
                    activeSlotCount);

            int dynamicMinOrderAmount = CalculateDynamicMinOrderAmount(feature, settings, threshold, cash);

            if (amount < dynamicMinOrderAmount)
                return 0;

            amount = Math.Min(amount, (int)available);

            int qty = (int)(amount / feature.Price);

            return qty <= 0 ? 0 : qty;
        }

        private static int CalculateLegacyDynamicBuyAmount(FeatureSnapshot feature, AutoTradeSettings settings, DynamicTradeThreshold threshold, long available, int currentPositionAmount)
        {
            if (feature == null || settings == null || threshold == null || feature.Price <= 0)
                return 0;

            int targetPositionAmount = threshold.MaxPositionAmount;

            if (targetPositionAmount <= 0)
                targetPositionAmount = (int)(available * 0.25);

            int remainPositionAmount = Math.Max(0, targetPositionAmount - currentPositionAmount);

            if (remainPositionAmount <= 0)
                return 0;

            int amount;

            if (feature.HasPosition)
            {
                int byCash = (int)(available * settings.AdditionalBuyCashRatio);
                int byPosition = (int)(currentPositionAmount * settings.AdditionalBuyPositionRatio);

                amount = Math.Min(byCash, byPosition);
                amount = Math.Min(amount, remainPositionAmount);
            }
            else
            {
                amount = Math.Min((int)available, remainPositionAmount);
            }

            return Math.Max(0, amount);
        }

        private static int CalculateDynamicPortfolioBuyAmount(
       FeatureSnapshot feature,
       AutoTradeSettings settings,
       DynamicTradeThreshold threshold,
       long cash,
       long available,
       int currentPositionAmount,
       int activeSlotCount)
        {
            if (feature == null || settings == null || threshold == null || feature.Price <= 0)
                return 0;

            int slotCount = Math.Max(1, activeSlotCount);

            double marketRisk =
                feature.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Panic ? 0.25 :
                feature.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Bear ? 0.45 :
                feature.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Weak ? 0.65 :
                feature.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Bull ? 1.05 :
                feature.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.StrongBull ? 1.20 :
                0.85;

            double totalUseRatio = Clamp(
                settings.MaxPortfolioCashUseRatio * marketRisk,
                0.10,
                settings.MaxPortfolioCashUseRatio <= 0 ? 0.80 : settings.MaxPortfolioCashUseRatio);

            double baseRatio = totalUseRatio / slotCount;

            double quality = CalculatePositionQualityMultiplier(feature, settings, threshold);
            double risk = CalculatePositionRiskMultiplier(feature, threshold);

            double finalRatio = baseRatio * quality * risk;

            double capRatio = CalculateDynamicSingleSymbolCapRatio(
                feature,
                settings,
                threshold,
                slotCount);

            finalRatio = Clamp(finalRatio, 0.0, capRatio);

            int targetPositionAmount = (int)(cash * finalRatio);
            int buyAmount = Math.Max(0, targetPositionAmount - currentPositionAmount);

            buyAmount = Math.Min(buyAmount, (int)available);

            return Math.Max(0, buyAmount);
        }

        private static double CalculatePositionQualityMultiplier(FeatureSnapshot f, AutoTradeSettings settings, DynamicTradeThreshold threshold)
        {
            if (f == null)
                return 1.0;

            int minScore = threshold == null || threshold.MinSignalScore <= 0
                ? settings.MinSignalScore
                : threshold.MinSignalScore;

            int score = Math.Max(0, f.SignalScore);

            // 기준점 이하면 축소
            if (score <= minScore)
                return 0.70;

            int diff = score - minScore;

            double multiplier = 1.0;

            // 점수가 높을수록 주문금액 확대.
            // 고정 구간 80/90이 아니라 현재 시장 기준 minScore에서 상대 계산.
            multiplier += diff * 0.025;

            if (f.RelativeStrength > 0)
                multiplier += Math.Min(0.25, f.RelativeStrength * 0.08);

            if (f.MinuteTrendScore >= 70)
                multiplier += 0.15;
            else if (f.MinuteTrendScore >= 60)
                multiplier += 0.08;

            if (f.TrendStrength >= 70)
                multiplier += 0.15;
            else if (f.TrendStrength >= 55)
                multiplier += 0.08;

            if (f.Phase == MarketPhase.Breakout)
                multiplier += 0.15;
            else if (f.Phase == MarketPhase.Accumulation)
                multiplier += 0.08;
            else if (f.Phase == MarketPhase.Pullback)
                multiplier += 0.05;

            double max = settings.MaxScorePositionMultiplier <= 0
                ? 1.6
                : settings.MaxScorePositionMultiplier;

            return Clamp(multiplier, 0.50, max);
        }

        private static double CalculateMarketPositionMultiplier(FeatureSnapshot f)
        {
            if (f == null)
                return 1.0;

            double m = f.MarketPositionSizeMultiplier <= 0
                ? 1.0
                : f.MarketPositionSizeMultiplier;

            switch (f.MarketTrend)
            {
                case MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Panic:
                    m *= 0.25;
                    break;

                case MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Bear:
                    m *= 0.55;
                    break;

                case MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Weak:
                    m *= 0.75;
                    break;

                case MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Bull:
                    m *= 1.05;
                    break;

                case MoneyCreator.Models.MarketIndexInfo.MarketTrendState.StrongBull:
                    m *= 1.20;
                    break;
            }

            if (f.RelativeStrength > 1.5)
                m += 0.15;
            else if (f.RelativeStrength < -0.5)
                m -= 0.15;

            return Clamp(m, 0.15, 1.35);
        }

        private static double CalculatePositionRiskMultiplier(FeatureSnapshot f, DynamicTradeThreshold threshold)
        {
            if (f == null)
                return 1.0;

            double m = 1.0;

            if (threshold != null && threshold.IsSideways)
                m *= 0.55;

            if (threshold != null && threshold.IsVolatile)
                m *= 0.70;

            if (f.Phase == MarketPhase.Overheated)
                m *= 0.65;
            else if (f.Phase == MarketPhase.Unstable)
                m *= 0.45;
            else if (f.Phase == MarketPhase.DownTrend)
                m *= 0.25;

            if (f.RangePosition >= 0.90)
                m *= 0.60;
            else if (f.RangePosition >= 0.80)
                m *= 0.75;

            if (f.VolatilityRate >= 3.0)
                m *= 0.60;
            else if (f.VolatilityRate >= 2.0)
                m *= 0.75;

            return Clamp(m, 0.15, 1.10);
        }
        private static double CalculateDynamicSingleSymbolCapRatio(
      FeatureSnapshot f,
      AutoTradeSettings settings,
      DynamicTradeThreshold threshold,
      int activeSlotCount)
        {
            int slotCount = Math.Max(1, activeSlotCount);

            double baseCap =
                settings.MaxPortfolioCashUseRatio > 0
                    ? settings.MaxPortfolioCashUseRatio / slotCount
                    : 0.80 / slotCount;

            double scorePower = 1.0;

            if (f != null)
            {
                int minScore = threshold != null && threshold.MinSignalScore > 0
                    ? threshold.MinSignalScore
                    : settings.MinSignalScore;

                int diff = Math.Max(0, f.SignalScore - minScore);
                scorePower += diff / 100.0;

                if (f.RelativeStrength > 0)
                    scorePower += Math.Min(0.20, f.RelativeStrength * 0.05);

                if (f.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Panic)
                    scorePower *= 0.35;
                else if (f.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Bear)
                    scorePower *= 0.55;
                else if (f.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.StrongBull)
                    scorePower *= 1.15;

                if (threshold != null && threshold.IsSideways)
                    scorePower *= 0.60;

                if (f.Phase == MarketPhase.Unstable || f.Phase == MarketPhase.DownTrend)
                    scorePower *= 0.45;
                else if (f.Phase == MarketPhase.Breakout)
                    scorePower *= 1.10;
            }

            double cap = baseCap * scorePower;

            double totalUseRatio = settings.MaxPortfolioCashUseRatio <= 0
                ? 0.80
                : settings.MaxPortfolioCashUseRatio;

            return Clamp(cap, 0.02, totalUseRatio);
        }
        private static int CalculateDynamicMinOrderAmount(
    FeatureSnapshot f,
    AutoTradeSettings settings,
    DynamicTradeThreshold threshold,
    long cash)
        {
            if (f == null || settings == null || f.Price <= 0)
                return 0;

            double baseRatio = 0.003;

            if (f.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Panic)
                baseRatio *= 0.5;
            else if (f.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.StrongBull)
                baseRatio *= 1.2;

            if (threshold != null && threshold.IsSideways)
                baseRatio *= 0.6;

            int dynamicAmount = (int)(cash * baseRatio);

            // 최소 1주는 살 수 있게 현재가 기준 보정
            dynamicAmount = Math.Min(dynamicAmount, (int)f.Price);

            // 기존 설정값은 절대값이 아니라 하한 참고값으로만 사용
            if (settings.MinOrderAmount > 0)
                dynamicAmount = Math.Min(dynamicAmount, settings.MinOrderAmount);

            return Math.Max(1, dynamicAmount);
        }
        private static int CalculateSellQuantity(TradeSignal signal, FeatureSnapshot feature, AutoTradeSettings settings)
        {
            if (signal == null || feature == null || settings == null)
                return 0;

            int holdingQty = Math.Max(0, feature.HoldingQty);
            if (holdingQty <= 0)
                return 0;

            // 손절/트레일링/약화/시간초과는 방어 매도이므로 전량 청산한다.
            if (IsHardExitSignal(signal))
                return holdingQty;

            // 분할 익절을 사용하지 않으면 기존처럼 전량 매도한다.
            if (!settings.UsePartialTakeProfitSell || !IsTakeProfitSignal(signal))
                return holdingQty;

            if (holdingQty < Math.Max(1, settings.MinQtyForPartialTakeProfit))
                return holdingQty;

            string positionKey = GetPositionKey(feature);

            // 같은 포지션에서 익절 분할매도는 1회만 수행한다.
            // 이후 잔여 물량은 트레일링/약화/손절 신호에서 전량 청산된다.
            if (_partialTakeProfitDone.ContainsKey(positionKey))
                return 0;

            int minRemain = Math.Max(0, settings.MinRemainQtyAfterPartialTakeProfit);
            int maxSellQty = Math.Max(0, holdingQty - minRemain);

            if (maxSellQty <= 0)
                return holdingQty;

            double ratio = settings.PartialTakeProfitSellRatio;
            if (ratio <= 0)
                return 0;
            if (ratio > 1)
                ratio = 1;

            int partialQty = (int)Math.Ceiling(holdingQty * ratio);
            partialQty = Math.Max(1, partialQty);
            partialQty = Math.Min(partialQty, maxSellQty);

            if (partialQty <= 0)
                return 0;

            _partialTakeProfitDone[positionKey] = true;

            signal.Reason = (signal.Reason ?? string.Empty) +
                $" / 분할익절 {partialQty}/{holdingQty}주, 잔여 {holdingQty - partialQty}주 트레일링";

            return partialQty;
        }

        private static bool IsTakeProfitSignal(TradeSignal signal)
        {
            string text = ((signal.StrategyName ?? string.Empty) + " " + (signal.Reason ?? string.Empty)).ToLowerInvariant();

            return text.Contains("익절") ||
                   text.Contains("takeprofit") ||
                   text.Contains("take_profit") ||
                   text.Contains("profit") ||
                   text.Contains("목표") ||
                   text.Contains("수익권");
        }

        private static bool IsHardExitSignal(TradeSignal signal)
        {
            if (signal == null)
                return false;

            string text = ((signal.StrategyName ?? string.Empty) + " " + (signal.Reason ?? string.Empty)).ToLowerInvariant();

            return text.Contains("stoploss") ||
                   text.Contains("손절") ||
                   text.Contains("trailing") ||
                   text.Contains("트레일링") ||
                   text.Contains("weakness") ||
                   text.Contains("약세") ||
                   text.Contains("riskexit") ||
                   text.Contains("상태청산") ||
                   text.Contains("timeout") ||
                   text.Contains("시간초과");
        }

        private static string GetPositionKey(FeatureSnapshot feature)
        {
            string entry = feature.EntryPrice > 0
                ? feature.EntryPrice.ToString("F2")
                : "NO_ENTRY";

            return feature.Code + ":" + entry;
        }

        private static void ClearPartialTakeProfitState(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return;

            foreach (var key in _partialTakeProfitDone.Keys.Where(x => x.StartsWith(code + ":")).ToList())
                _partialTakeProfitDone.TryRemove(key, out _);
        }

        private static string Safe(Func<string> getter)
        {
            try { return getter == null ? string.Empty : getter() ?? string.Empty; }
            catch { return string.Empty; }
        }
    }
}
