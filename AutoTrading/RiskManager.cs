using System;
using System.Collections.Concurrent;

namespace MoneyCreator.AutoTrading
{
    public sealed class RiskManager
    {
        private readonly ConcurrentDictionary<string, double> _highestProfitRate = new ConcurrentDictionary<string, double>();
        private readonly ConcurrentDictionary<string, DateTime> _lastSellTime = new ConcurrentDictionary<string, DateTime>();

        public RiskResult CheckBeforeSignal(FeatureSnapshot f, AutoTradeSettings settings)
        {
            if (f == null || settings == null)
                return RiskResult.Block("리스크 입력 없음");

            if (f.HasPosition)
            {
                _highestProfitRate.AddOrUpdate(f.Code, f.ProfitRate, (k, old) => Math.Max(old, f.ProfitRate));
                return RiskResult.Pass();
            }

            var t = DynamicThresholdProvider.Create(f, settings);
            if (f.Cash <= t.MinCashRemain)
                return RiskResult.Block("예수금 부족");

            return RiskResult.Pass();
        }

        public RiskResult CheckSignal(TradeSignal signal, FeatureSnapshot f, AutoTradeSettings settings)
        {
            if (signal == null || signal.Action == TradeActionType.Hold)
                return RiskResult.Block("주문 신호 없음");

            if (f == null || settings == null)
                return RiskResult.Block("리스크 입력 없음");

            var t = DynamicThresholdProvider.Create(f, settings);

            if (signal.Action == TradeActionType.Buy)
            {
                if (!settings.AllowBuy)
                    return RiskResult.Block("매수 비허용");

                if (!t.EntryAllowed && !f.HasPosition)
                    return RiskResult.Block(t.EntryBlockReason);

                if (_lastSellTime.TryGetValue(f.Code, out var lastSellTime))
                {
                    double elapsed = (DateTime.Now - lastSellTime).TotalSeconds;
                    if (elapsed < t.ReEntryBlockSecondsAfterSell)
                        return RiskResult.Block($"매도 후 재진입 대기 {elapsed:F0}/{t.ReEntryBlockSecondsAfterSell}s");
                }

                if (f.HasPosition && !settings.UseFullCashSizingForTest)
                {
                    var addBuyRisk = CheckAdditionalBuy(signal, f, settings, t);
                    if (!addBuyRisk.Passed)
                        return addBuyRisk;
                }

                if (f.Cash <= t.MinCashRemain)
                    return RiskResult.Block($"예수금 부족: 현금={f.Cash:N0}, 최소유지={t.MinCashRemain:N0}");

                long available = Math.Max(0, f.Cash - t.MinCashRemain);

                int plannedAmount;

                if (settings.UseFullCashSizingForTest)
                    plannedAmount = (int)available;
                else
                    plannedAmount = Math.Min(t.MaxOrderAmount, t.MaxPositionAmount);

                if (plannedAmount <= 0)
                    return RiskResult.Block("주문 한도 0");

                if (plannedAmount > available)
                    return RiskResult.Block($"현금 잔량 제한: 계획={plannedAmount:N0}, 가능={available:N0}");

                if (f.Price > 0 && plannedAmount < f.Price)
                    return RiskResult.Block($"1주 매수금액 부족: 계획={plannedAmount:N0}, 현재가={f.Price:N0}");
            }
            else if (signal.Action == TradeActionType.Sell)
            {
                if (!settings.AllowSell)
                    return RiskResult.Block("매도 비허용");

                if (!f.HasPosition || f.HoldingQty <= 0)
                    return RiskResult.Block("매도 가능 수량 없음");
            }

            return RiskResult.Pass();
        }

        public int CalculateHoldScore(FeatureSnapshot f, AutoTradeSettings settings)
        {
            if (f == null || settings == null || !f.HasPosition)
                return 0;

            int score = 0;

            if (f.Phase == MarketPhase.Breakout) score += 18;
            if (f.Phase == MarketPhase.Accumulation) score += 12;
            if (f.Phase == MarketPhase.Pullback) score += 10;

            if (f.TrendStrength >= 65) score += 18;
            else if (f.TrendStrength >= 45) score += 10;

            if (f.MinuteTrendScore >= 65) score += 14;
            else if (f.MinuteTrendScore >= 50) score += 8;

            if (f.Strength >= 130) score += 16;
            else if (f.Strength >= 110) score += 10;
            else if (f.Strength >= 100) score += 5;

            if (f.StrengthDelta >= 5) score += 12;
            else if (f.StrengthDelta >= 0) score += 6;

            if (f.BuyPressureScore >= 70) score += 14;
            else if (f.BuyPressureScore >= 55) score += 8;

            if (f.BidAskRatioRelative >= 1.25) score += 12;
            else if (f.BidAskRatioRelative >= 1.05) score += 7;

            if (f.VolumeRatio >= 1.5) score += 8;
            else if (f.VolumeAccel > 0) score += 5;

            if (f.RelativeStrength > 0) score += 8;

            if (f.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Bull ||
                f.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.StrongBull)
            {
                score += 8;
            }

            if (f.ProfitRate > 0 && f.DrawdownRate < 0.4)
                score += 6;

            return ClampScore(score);
        }

        public int CalculateExitScore(FeatureSnapshot f, AutoTradeSettings settings)
        {
            if (f == null || settings == null || !f.HasPosition)
                return 0;

            int minuteScore = CalculateMinuteExitScore(f, settings);
            int tickScore = CalculateTickExitScore(f, settings);

            double minuteWeight = 0.70;
            double tickWeight = 0.30;

            bool strongMarket =
                f.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Bull ||
                f.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.StrongBull;

            bool weakMarket =
                f.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Bear ||
                f.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Panic;

            bool strongMinute =
                f.MinuteTrendScore >= 60 ||
                f.TrendStrength >= 55 ||
                f.Phase == MarketPhase.Breakout;

            bool weakMinute =
                f.MinuteTrendScore <= 40 ||
                f.TrendStrength <= 35 ||
                f.Phase == MarketPhase.DownTrend ||
                f.Phase == MarketPhase.Unstable;

            bool tickNoiseLikely =
                strongMinute &&
                f.PriceChangeRate1m >= -0.25 &&
                f.BidAskRatioRelative >= 0.90;

            if (strongMarket && strongMinute)
            {
                minuteWeight = 0.85;
                tickWeight = 0.15;
            }
            else if (strongMinute)
            {
                minuteWeight = 0.80;
                tickWeight = 0.20;
            }
            else if (weakMarket && weakMinute)
            {
                minuteWeight = 0.60;
                tickWeight = 0.40;
            }
            else if (weakMinute)
            {
                minuteWeight = 0.65;
                tickWeight = 0.35;
            }

            if (tickNoiseLikely)
            {
                minuteWeight = Math.Max(minuteWeight, 0.85);
                tickWeight = 1.0 - minuteWeight;
            }

            if (f.ProfitRate < 0)
            {
                // 손실권이라고 무조건 틱 비중을 키우지 않는다.
                // 상승장/분봉 생존 상태에서는 흔들림 손절을 줄인다.
                if (strongMarket && strongMinute)
                {
                    minuteWeight = Math.Max(minuteWeight, 0.80);
                    tickWeight = 1.0 - minuteWeight;
                }
                else if (weakMarket || weakMinute)
                {
                    minuteWeight = Math.Min(minuteWeight, 0.60);
                    tickWeight = 1.0 - minuteWeight;
                }
            }

            double score =
                (minuteScore * minuteWeight) +
                (tickScore * tickWeight);

            // 상승장 + 분봉 생존 + 약손실이면 조기청산 점수 완화
            if (strongMarket &&
                strongMinute &&
                f.ProfitRate > -Math.Abs(DynamicThresholdProvider.Create(f, settings).StopLossRate) * 0.55)
            {
                score *= 0.80;
            }

            // 패닉/하락장에서는 청산 점수 강화
            if (weakMarket && weakMinute)
                score *= 1.12;

            return ClampScore((int)Math.Round(score));
        }

        private static int ClampScore(int value)
        {
            if (value < 0)
                return 0;

            if (value > 100)
                return 100;

            return value;
        }

        private RiskResult CheckAdditionalBuy(TradeSignal signal, FeatureSnapshot f, AutoTradeSettings settings, DynamicTradeThreshold t)
        {
            if (!settings.AllowAdditionalBuy)
                return RiskResult.Block("추가매수 비허용");

            if (f.HasPendingBuy)
                return RiskResult.Block("매수 미체결 존재");

            if (f.Cash <= t.MinCashRemain)
                return RiskResult.Block($"추가매수 현금 부족: 현금={f.Cash:N0}, 최소유지={t.MinCashRemain:N0}");

            double currentPositionAmount = f.HoldingQty * f.Price;

            if (currentPositionAmount <= 0)
                return RiskResult.Block("보유금액 계산 실패");

            if (currentPositionAmount >= t.MaxPositionAmount)
                return RiskResult.Block("종목 최대 보유금액 초과");

            // 추가매수도 고정 수익률 구간 대신 동적 손절폭 안에서만 허용한다.
            if (f.ProfitRate <= -t.StopLossRate * 0.8)
                return RiskResult.Block("손실 확대 구간 추가매수 금지");

            if (f.ProfitRate >= t.TakeProfitRate * 0.9)
                return RiskResult.Block("수익권 추격 추가매수 금지");

            bool phaseOk =
                f.Phase == MarketPhase.Accumulation ||
                f.Phase == MarketPhase.Pullback ||
                f.Phase == MarketPhase.Breakout;

            if (!phaseOk)
                return RiskResult.Block("추가매수 부적합 국면");

            if (f.VolumeRatio < t.MinVolumeRatio && !t.IsHighLiquidity)
                return RiskResult.Block("거래량 증가 부족");

            if (f.BidAskRatioRelative < t.MinBidAskRatioRelative && f.BidAskRatio < t.MinBidAskRatio)
                return RiskResult.Block("호가 우세 부족");

            if (f.Strength < t.MinStrength)
                return RiskResult.Block("체결강도 부족");

            if (signal.Score < t.MinSignalScore)
                return RiskResult.Block("추가매수 점수 부족");

            return RiskResult.Pass();
        }

        public bool ShouldStopLoss(FeatureSnapshot f, AutoTradeSettings settings)
        {
            if (f == null || !f.HasPosition || settings == null)
                return false;

            var t = DynamicThresholdProvider.Create(f, settings);
            double effectiveStopLoss = Math.Abs(t.StopLossRate);

            // 하락/불안정 국면에서 체결강도까지 꺾이면 손절을 조금 더 빠르게 한다.
            if ((f.Phase == MarketPhase.DownTrend || f.Phase == MarketPhase.Unstable) &&
                f.StrengthDelta < 0 &&
                f.BidAskRatioRelative < 1.0)
            {
                effectiveStopLoss *= 0.80;
            }

            return f.ProfitRate <= -effectiveStopLoss;
        }

        public bool ShouldTakeProfit(FeatureSnapshot f, AutoTradeSettings settings)
        {
            // 고정 익절 제거.
            // 수익권 청산은 RiskExitRule에서 상태 점수 기반으로만 처리한다.
            return false;
        }

        public bool ShouldTrailingStop(FeatureSnapshot f, AutoTradeSettings settings)
        {
            // 고정 트레일링 제거.
            // 고점 대비 반납은 RiskExitRule의 ExitScore에만 반영한다.
            return false;
        }
        public int CalculateEmergencyExitScore(FeatureSnapshot f, AutoTradeSettings settings)
        {
            if (f == null || settings == null || !f.HasPosition)
                return 0;

            int score = 0;

            // 급락
            if (f.PriceChangeRate1m <= -0.80) score += 35;
            else if (f.PriceChangeRate1m <= -0.50) score += 24;

            if (f.PriceChangeRate3m <= -1.20) score += 25;
            else if (f.PriceChangeRate3m <= -0.80) score += 15;

            // 체결강도 붕괴
            if (f.Strength < 80) score += 22;
            else if (f.Strength < 90) score += 12;

            if (f.StrengthDelta <= -25) score += 25;
            else if (f.StrengthDelta <= -15) score += 14;

            // 호가/매수압 붕괴
            if (f.BuyPressureScore < 25) score += 22;
            else if (f.BuyPressureScore < 35) score += 12;

            if (f.BidAskRatioRelative > 0 && f.BidAskRatioRelative < 0.65) score += 22;
            else if (f.BidAskRatioRelative > 0 && f.BidAskRatioRelative < 0.80) score += 12;

            // 시장 패닉
            if (f.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Panic)
                score += 20;

            // 손실권이면 긴급청산 민감도 증가
            if (f.ProfitRate <= -0.8)
                score += 18;
            else if (f.ProfitRate <= -0.5)
                score += 10;

            // 단, 분봉이 강하게 살아있으면 긴급 점수 일부 억제
            if (f.MinuteTrendScore >= 70 && f.TrendStrength >= 60 && f.ProfitRate >= 0)
                score -= 20;

            return ClampScore(score);
        }
        public int CalculateMinuteExitScore(FeatureSnapshot f, AutoTradeSettings settings)
        {
            if (f == null || settings == null || !f.HasPosition)
                return 0;

            int score = 0;

            if (f.Phase == MarketPhase.DownTrend) score += 28;
            else if (f.Phase == MarketPhase.Unstable) score += 20;
            else if (f.Phase == MarketPhase.Overheated) score += 8;

            if (f.MinuteTrendScore <= 30) score += 28;
            else if (f.MinuteTrendScore <= 40) score += 18;
            else if (f.MinuteTrendScore <= 50) score += 8;

            if (f.TrendStrength <= 20) score += 24;
            else if (f.TrendStrength <= 35) score += 14;
            else if (f.TrendStrength <= 45) score += 6;

            if (f.RangePosition <= 0.20 && f.PriceChangeRate3m < 0)
                score += 12;

            if (f.VolatilityRate >= 2.0 && f.PriceChangeRate3m < 0)
                score += 10;

            if (f.RelativeStrength < -0.5)
                score += 8;

            // 고점 대비 반납.
            // DrawdownRate = 현재수익률 - 최고수익률 이므로 음수 방향이 맞다.
            if (f.ProfitRate > 0)
            {
                if (f.DrawdownRate <= -1.2) score += 18;
                else if (f.DrawdownRate <= -0.7) score += 10;
            }

            if (f.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Panic)
                score += 12;
            else if (f.MarketTrend == MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Bear)
                score += 7;

            return ClampScore(score);
        }

        public int CalculateTickExitScore(FeatureSnapshot f, AutoTradeSettings settings)
        {
            if (f == null || settings == null || !f.HasPosition)
                return 0;

            int score = 0;

            if (f.Strength < 85) score += 20;
            else if (f.Strength < 95) score += 10;

            if (f.StrengthDelta <= -20) score += 22;
            else if (f.StrengthDelta <= -10) score += 12;
            else if (f.StrengthDelta < 0) score += 5;

            if (f.BuyPressureScore < 35) score += 18;
            else if (f.BuyPressureScore < 50) score += 8;

            if (f.BidAskRatioRelative > 0 && f.BidAskRatioRelative < 0.75) score += 18;
            else if (f.BidAskRatioRelative > 0 && f.BidAskRatioRelative < 0.90) score += 8;

            if (f.PriceChangeRate1m <= -0.45) score += 18;
            else if (f.PriceChangeRate1m <= -0.20) score += 8;

            if (f.VolumeAccel < 0 && f.PriceChangeRate1m < 0)
                score += 8;

            // 분봉이 살아있으면 틱 노이즈 감점
            if (f.MinuteTrendScore >= 60 && f.TrendStrength >= 50)
                score -= 12;

            return ClampScore(score);
        }

        public void MarkSellCompleted(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return;

            _lastSellTime[code] = DateTime.Now;
            _highestProfitRate.TryRemove(code, out _);
        }

        public void Reset(string code = null)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                _highestProfitRate.Clear();
                _lastSellTime.Clear();
            }
            else
            {
                _highestProfitRate.TryRemove(code, out _);
                _lastSellTime.TryRemove(code, out _);
            }
        }
    }
}
