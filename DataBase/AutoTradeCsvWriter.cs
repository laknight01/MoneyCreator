using MoneyCreator.AutoTrading;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace MoneyCreator.DataBase
{
    /// <summary>
    /// Compact CSV writer. Keeps the official SideBar/Event/Pair acceleration path,
    /// candidate origin-price state, post-candidate Tick reaction diagnostics,
    /// TotalBar Trend Pivot diagnostics, candidate diagnostics, and operational order history.
    /// v294 keeps the v293 order diagnostics and additionally records same-tick TrendTurnWatch Candidate entry frames
    /// (BarTargetMode=TrendCandidateEntry) so one-shot trend entries can be replay-verified beside ordinary +/-70 decisions.
    /// v289 logs Whole-Leg Anchor/Endpoint/ProtectedSwing/TurnWatch, RelativeQuality + BalancedFast confirmation diagnostics, W30/W45/W60 Shadow diagnostics,
    /// Strong TurnWatch early direction permission, direct official +/-70 execution, CandidateReaction Shadow diagnostics, and split reversal evidence/decision elapsed time.
    /// Legacy shadow axes and disabled entry/exit columns remain excluded.
    /// </summary>
    public sealed class AutoTradeCsvWriter : IDisposable
    {
        public static readonly AutoTradeCsvWriter Shared = new AutoTradeCsvWriter();

        private const int QueueCapacity = 100000;
        private readonly BlockingCollection<WriteRequest> _queue =
            new BlockingCollection<WriteRequest>(new ConcurrentQueue<WriteRequest>(), QueueCapacity);
        private readonly Dictionary<string, StreamWriter> _writers =
            new Dictionary<string, StreamWriter>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _lastOrderFingerprint =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _lastTrendFingerprint =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Thread _thread;
        private readonly string _rootDirectory = @"F:\Project\LOG";
        private int _disposed;
        private long _dropped;

        private static readonly string[] FrameHeader =
        {
            "RecordedAt", "AnalysisTime", "Mode", "Code", "SnapshotId", "SignalId", "CandidateId", "EnginePatch",
            "DecisionBarType", "DecisionBarCloseReason", "DecisionEventKey", "Price",
            "FrameOpen", "FrameHigh", "FrameLow", "FrameVwap", "FrameTickCount", "FrameElapsedSeconds",
            "FrameTradeValue", "FrameBuyValue", "FrameSellValue", "FrameNetBuyValue",
            "DTargetPrevious", "DTargetCurrent", "DTargetNext", "DTargetChangeRatio", "DTargetUpdateReason",
            "BuyTargetRatioToTotal", "SellTargetRatioToTotal", "BuyTargetTradeValue", "SellTargetTradeValue",
            "TotalGeneration", "DecisionBarGeneration", "TotalBarFill", "BuyBarFill", "SellBarFill",
            "BuyBarSpeed", "SellBarSpeed", "BuyValueClosed60s", "SellValueClosed60s",
            "EventOrigin", "EventStartTime", "EventEndTime", "EventDurationSeconds", "EventMeasurementReady",
            "EventMeasurementReason", "EventBaseAccelerationEvidence", "EventTickCount", "EventDistinctTimeCount",
            "EventLargestTickShare", "EventEvidenceMaturity", "EventPhysicalSource",
            "EventResidualDistinctTimeCount", "EventResidualTotalCoverage", "EventResidualTotalBreadth",
            "EventResidualTotalSupport", "EventOfficialSupport", "EventEvidencePenalty",
            "PairMode", "PairWindowSeconds", "PairBuyValueFilledCount", "PairSellValueFilledCount",
            "PairTotalValueFilledCount", "PairSideCompletedCount", "PairEvidenceMaturity", "PairSameDirectionSupport",
            "PairMatureSameDirectionSupport", "PairOuterOppositionRatio", "PairExtendedOpposition", "PairAppliedExtendedOpposition", "EventValidationRetention", "OppositePairPenalty",
            "RawAccelerationEvidence", "AppliedAccelerationAuthority", "AppliedAccelerationEvidence",
            "SignedRelativeAcceleration", "AccelerationAuthority",
            "TrendPointSequence", "TrendPointTime", "TrendPointClose", "TrendMoveDirection",
            "TrendLastPivotType", "TrendLastPivotPrice", "TrendLastPivotTime", "TrendLastPivotConfirmedTime", "TrendLastPivotRelation",
            "TrendPreviousHigh", "TrendLatestHigh", "TrendHighRelation",
            "TrendPreviousLow", "TrendLatestLow", "TrendLowRelation", "TrendStructure", "TrendSummary",
            "추세Pivot상태", "추세Anchor종류", "추세Anchor가격", "추세Anchor시각", "추세Endpoint가격", "추세Endpoint시각",
            "추세ProtectedSwing종류", "추세ProtectedSwing가격", "추세ProtectedSwing시각",
            "추세반전증거종류", "추세반전증거상태", "추세반전증거시각", "추세반전증거가격", "추세반전무효화레벨", "추세Pivot→증거초", "추세증거→판단초",
            "추세TurnWatch활성", "추세TurnWatch종류", "추세TurnWatch가격", "추세TurnWatchPivot시각", "추세TurnWatch시작", "추세Pivot→Watch초", "추세Watch대기초",
            "추세Watch반대Endpoint가격", "추세Watch반대Endpoint시각", "추세Watch반대진행횟수",
            "추세WatchWholeLeg폭", "추세Watch1차반대폭", "추세Watch1차/WholeLeg",
            "추세Watch회복폭", "추세Watch회복률", "추세Watch재진행확장폭", "추세Watch재진행/1차",
            "추세Watch총반전폭", "추세Watch총반전/WholeLeg", "추세Watch구조품질Shadow", "추세Watch국소구조품질Shadow", "추세Watch균형빠른품질Shadow", "추세Watch빠른확정Shadow", "추세Watch품질리셋횟수Shadow", "추세Watch조기방향Shadow",
            "추세WatchW30", "추세WatchW45Shadow", "추세WatchW60Shadow", "추세WatchW30샘플", "추세WatchW45샘플", "추세WatchW60샘플", "추세WatchW30범위초", "추세WatchW45범위초", "추세WatchW60범위초",
            "추세대기Pivot종류", "추세대기Pivot가격", "추세대기Pivot시각", "추세후보Watch시작", "추세후보검증시작", "추세Pivot→후보검증초", "추세Watch→후보검증초", "추세후보판단대기초", "최근추세Pivot확정지연초",
            "추세W30Shadow상태", "추세W30", "추세W45Shadow", "추세W60Shadow",
            "추세W30샘플", "추세W45샘플", "추세W60샘플", "추세W30범위초", "추세W45범위초", "추세W60범위초",
            "추세반대Endpoint가격", "추세반대Endpoint시각", "추세반대진행횟수",
            "최근TurnWatch종류", "최근TurnWatch가격", "최근TurnWatchPivot시각", "최근TurnWatch시작", "최근TurnWatch판단결과", "최근TurnWatch판단시각", "최근TurnWatchPivot→Watch초", "최근TurnWatchWatch→판단초", "최근TurnWatchPivot→판단초",
            "최근추세후보종류", "최근추세후보가격", "최근추세후보Pivot시각", "최근추세후보Watch시작", "최근추세후보검증시작",
            "최근추세후보판단결과", "최근추세후보Pivot→Watch초", "최근추세후보Watch→검증초", "최근추세후보Pivot→검증초", "최근추세후보검증→판단초", "최근추세후보Pivot→판단초", "최근추세후보판단시각",
            "TrendGeneration", "TrendAgeSeconds", "TrendShortReady", "TrendLongReady",
            "TrendShortSamples", "TrendLongSamples", "TrendShortCoverageSeconds", "TrendLongCoverageSeconds",
            "TrendShortRawScore", "TrendLongRawScore", "TrendShortSlopePercentPerMinute", "TrendLongSlopePercentPerMinute",
            "TrendShortEfficiency", "TrendLongEfficiency", "TrendShortConsistency", "TrendLongConsistency",
            "TrendShortNetMovePercent", "TrendLongNetMovePercent",
            "TrendShortHigherHighRatio", "TrendShortHigherLowRatio", "TrendShortLowerHighRatio", "TrendShortLowerLowRatio", "TrendShortStructureScore",
            "TrendLongHigherHighRatio", "TrendLongHigherLowRatio", "TrendLongLowerHighRatio", "TrendLongLowerLowRatio", "TrendLongStructureScore",
            "TrendShortEmaScore", "TrendLongEmaScore", "TrendShortDirection", "TrendLongDirection",
            "TrendShortConfidence", "TrendLongConfidence", "TrendShortPersistence", "TrendLongPersistence",
            "TrendShortAuthority", "TrendLongAuthority", "TrendDominanceGap", "TrendResolvedAuthority", "TrendRequiredBuyAuthority", "ResolvedTrendDirection",
            "TrendMode", "TrendBuyAllowed", "TrendSellAllowed", "TrendCandidateAllowed", "TrendManagerReason",
            "PivotLegMove", "PivotLegNeed", "PivotTurnMove", "PivotTurnNeed", "PivotLegOk", "PivotTurnOk", "PivotDetail",
            "CandidateLifecycleId", "CandidateLifecycleState", "CandidateCreatedAt",
            "CandidateOriginDirection", "CandidateOriginScore", "CandidateOriginEventKey",
            "CandidateStartTotalCloseSequence", "CandidateSameDirectionEvidenceCount",
            "CandidateOriginPriceState", "CandidateOriginMode", "CandidateOriginSignalPrice",
            "PressureOriginTime", "PressureOriginPrice", "PressureOriginRawScore", "CandidateLastEvidenceTime", "CandidateMaxQualifiedScore", "CandidateReactionConfidence",
            "CandidatePreviousTotalCloseSequence", "CandidatePreviousBarClose", "CandidatePreviousBarVwap",
            "CandidateAnchorTotalCloseSequence", "CandidateAnchorTime",
            "CandidateAnchorBarOpen", "CandidateAnchorBarHigh", "CandidateAnchorBarLow",
            "CandidateAnchorBarClose", "CandidateAnchorBarVwap",
            "CandidateConflictOccurred", "CandidateConflictTime", "CandidateConflictDirection", "CandidateConflictEventKey",
            "CandidateConfirmationTime", "CandidateConfirmationTotalCloseSequence",
            "CandidateConfirmationDirection", "CandidateConfirmationMode",
            "CandidateConfirmationBarOpen", "CandidateConfirmationBarHigh", "CandidateConfirmationBarLow",
            "CandidateConfirmationBarClose", "CandidateConfirmationBarVwap", "CandidateResponsePriceState",
            "CandidateWindowElapsedSeconds", "CandidateWindowTickCount", "CandidateWindowDistinctTimeCount",
            "CandidateWindowNetMoveTicks", "CandidateWindowFavorableTicks", "CandidateWindowAdverseTicks",
            "CandidateWindowEfficiency", "CandidateWindowRequiredTicks",
            "CandidateDirectionResult", "CandidateConfirmationResult", "CandidateExecutionResult",
            "RawScore", "FinalScore", "BuyThreshold", "SellThreshold",
            "HasPosition", "HoldingQty", "EntryPrice", "NetProfitRate", "HoldSeconds",
            "HasPendingBuy", "HasPendingSell", "OpenOrderCount", "Cash", "SymbolRemainingBudget",
            "Action", "Strategy", "IsAdditionalBuyCandidate", "DecisionReason", "OrderBlockStage"
        };

        private static readonly string[] OrderHeader =
        {
            "RecordedAt", "EventTime", "Mode", "Code", "Source", "Stage", "Action", "Strategy",
            "SignalPrice", "CurrentPrice", "BestAsk", "BestBid", "OrderPrice", "Quantity", "HogaType",
            "FinalScore", "DecisionEventKey", "CandidateLifecycleId", "OrderNo", "OriginalOrderNo", "OrderType", "OrderStatus",
            "Status", "ExecPrice", "ExecQty", "UntradedQty", "TradeGroupId", "Message"
        };

        // v289: Strict Endpoint/ProtectedSwing/TurnWatch 상대규모 품질/BalancedFast/W Shadow/판단시간이 바뀔 때만 한 줄 기록한다.
        private static readonly string[] TrendPivotHeader =
        {
            "기록시각", "종목코드", "추세상태", "확정방향",
            "Anchor종류", "Anchor시각", "Anchor가격", "Endpoint시각", "Endpoint가격",
            "ProtectedSwing종류", "ProtectedSwing시각", "ProtectedSwing가격",
            "반전증거종류", "반전증거상태", "반전증거시각", "반전증거가격", "반전무효화레벨", "Pivot→증거초", "증거→판단초",
            "TurnWatch활성", "TurnWatch종류", "TurnWatchPivot시각", "TurnWatch가격", "TurnWatch시작시각", "Pivot→Watch초", "Watch대기초",
            "Watch반대Endpoint가격", "Watch반대Endpoint시각", "Watch반대진행횟수",
            "WatchWholeLeg폭", "Watch1차반대폭", "Watch1차/WholeLeg",
            "Watch회복폭", "Watch회복률", "Watch재진행확장폭", "Watch재진행/1차",
            "Watch총반전폭", "Watch총반전/WholeLeg", "Watch구조품질Shadow", "Watch국소구조품질Shadow", "Watch균형빠른품질Shadow", "Watch빠른확정Shadow", "Watch품질리셋횟수Shadow", "Watch조기방향Shadow",
            "WatchW30", "WatchW45Shadow", "WatchW60Shadow", "WatchW30샘플", "WatchW45샘플", "WatchW60샘플", "WatchW30범위초", "WatchW45범위초", "WatchW60범위초",
            "최근Pivot종류", "최근Pivot시각", "최근Pivot가격", "최근Pivot확정시각", "최근Pivot확정구간", "현재관찰구간", "Pivot→확정초",
            "대기Pivot종류", "대기Pivot시각", "대기Pivot가격", "후보Watch시작시각", "후보검증시작시각", "Pivot→후보검증초", "Watch→후보검증초", "후보판단대기초",
            "W30Shadow상태", "W30", "W45Shadow", "W60Shadow",
            "W30샘플", "W45샘플", "W60샘플", "W30범위초", "W45범위초", "W60범위초",
            "반대Endpoint가격", "반대Endpoint시각", "반대진행횟수",
            "최근TurnWatch종류", "최근TurnWatch가격", "최근TurnWatchPivot시각", "최근TurnWatch시작", "최근TurnWatch판단결과", "최근TurnWatch판단시각", "최근TurnWatchPivot→Watch초", "최근TurnWatchWatch→판단초", "최근TurnWatchPivot→판단초",
            "최근후보종류", "최근후보가격", "최근후보Pivot시각", "최근후보Watch시작", "최근후보검증시작",
            "최근후보판단결과", "최근후보판단시각", "최근후보Pivot→Watch초", "최근후보Watch→검증초", "최근후보Pivot→검증초", "최근후보검증→판단초", "최근후보Pivot→판단초", "판단요약"
        };

        private AutoTradeCsvWriter()
        {
            _thread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "AutoTradeCsvWriter"
            };
            _thread.Start();
            AppDomain.CurrentDomain.ProcessExit += delegate { Dispose(); };
            AppDomain.CurrentDomain.UnhandledException += delegate { Flush(TimeSpan.FromSeconds(1)); };
        }

        public long DroppedRowCount { get { return Interlocked.Read(ref _dropped); } }

        public void BeginSession(string code, DateTime tradingDate, bool isReplayMode)
        {
            code = NormalizeCode(code);
            if (string.IsNullOrWhiteSpace(code) || tradingDate == default(DateTime))
                return;

            string mode = isReplayMode ? "Replay" : "Real";
            string ignored;
            _lastTrendFingerprint.TryRemove(mode + "|" + code, out ignored);

            Enqueue(new WriteRequest
            {
                BeginSession = true,
                Code = code,
                Mode = mode,
                EventTime = tradingDate.Date
            });
        }

        public void EnqueueFrame(
            FeatureSnapshot feature,
            TradeSignal signal,
            bool isReplayMode,
            string signalId,
            string candidateId)
        {
            bool candidateTickTerminal =
                feature != null &&
                string.Equals(
                    feature.BarTargetMode,
                    "CandidateTickConfirmation",
                    StringComparison.OrdinalIgnoreCase);
            bool trendCandidateTerminal =
                feature != null &&
                string.Equals(
                    feature.BarTargetMode,
                    "TrendCandidateEntry",
                    StringComparison.OrdinalIgnoreCase);

            if (feature == null ||
                (!feature.IsClosedDecisionBar && !candidateTickTerminal && !trendCandidateTerminal) ||
                string.IsNullOrWhiteSpace(feature.Code))
            {
                return;
            }

            string code = NormalizeCode(feature.Code);
            string mode = isReplayMode ? "Replay" : "Real";
            DateTime time = feature.Time;
            SignalScoreBreakdown b = signal == null ? feature.ScoreBreakdown : signal.ScoreBreakdown;
            string action = signal == null ? Safe(feature.SignalAction) : signal.Action.ToString();
            string strategy = signal == null ? Safe(feature.StrategyName) : Safe(signal.StrategyName);
            string reason = b != null && !string.IsNullOrWhiteSpace(b.OperationalBlockReason)
                ? b.OperationalBlockReason
                : signal == null ? feature.SignalReason : signal.Reason;

            string[] row =
            {
                FormatDateTime(time), FormatDateTime(time), mode, code, Safe(feature.SnapshotId), Safe(signalId), Safe(candidateId),
                b == null ? string.Empty : Safe(b.EnginePatchVersion),
                Safe(feature.DecisionBarType), Safe(feature.DecisionBarCloseReason), b == null ? string.Empty : Safe(b.DecisionEventKey),
                Number(feature.Price), Number(feature.FrameOpenPrice), Number(feature.FrameHighPrice), Number(feature.FrameLowPrice),
                Number(feature.FrameVwap), feature.FrameTickCount.ToString(CultureInfo.InvariantCulture), Number(feature.FrameElapsedSeconds),
                Number(feature.TickTradeValue), Number(feature.FrameBuyTradeValue), Number(feature.FrameSellTradeValue), Number(feature.FrameNetBuyTradeValue),
                Number(feature.BarPreviousTargetTradeValue), Number(feature.BarCurrentTargetTradeValue), Number(feature.BarNextTargetTradeValue), Number(feature.BarDTargetChangeRatio), Safe(feature.BarDTargetUpdateReason),
                Number(feature.BuyDollarBarTargetRatioToTotal), Number(feature.SellDollarBarTargetRatioToTotal),
                Number(feature.BuyDollarBarTargetTradeValue), Number(feature.SellDollarBarTargetTradeValue),
                feature.BarTotalGeneration.ToString(CultureInfo.InvariantCulture), feature.DecisionBarTotalGeneration.ToString(CultureInfo.InvariantCulture),
                Number(feature.DollarBarFillRatio), Number(feature.BuyDollarBarFillRatio), Number(feature.SellDollarBarFillRatio),
                Number(feature.BuyDollarBarSpeedRatio), Number(feature.SellDollarBarSpeedRatio),
                feature.BuyDollarBarValueClosedCount60s.ToString(CultureInfo.InvariantCulture),
                feature.SellDollarBarValueClosedCount60s.ToString(CultureInfo.InvariantCulture),
                b == null ? string.Empty : Safe(b.EventOrigin), b == null ? string.Empty : FormatDateTime(b.EventStartTime),
                b == null ? string.Empty : FormatDateTime(b.EventEndTime), b == null ? "0" : Number(b.EventDurationSeconds),
                Bool(b != null && b.EventMeasurementReady), b == null ? string.Empty : Safe(b.EventMeasurementReason),
                b == null ? "0" : Number(b.EventBaseAccelerationEvidence),
                b == null ? "0" : b.EventTickCount.ToString(CultureInfo.InvariantCulture),
                b == null ? "0" : b.EventDistinctTimeCount.ToString(CultureInfo.InvariantCulture),
                b == null ? "0" : Number(b.EventLargestTickShare), b == null ? "0" : Number(b.EventEvidenceMaturity),
                b == null ? string.Empty : Safe(b.EventPhysicalSource),
                b == null ? "0" : b.EventResidualDistinctTimeCount.ToString(CultureInfo.InvariantCulture),
                b == null ? "0" : Number(b.EventResidualTotalCoverage),
                b == null ? "0" : Number(b.EventResidualTotalBreadth),
                b == null ? "0" : Number(b.EventResidualTotalSupport),
                b == null ? "0" : Number(b.EventOfficialSupport), b == null ? "0" : Number(b.EventEvidencePenalty),
                b == null ? string.Empty : Safe(b.PairMode), b == null ? "0" : Number(b.PairWindowSeconds),
                b == null ? "0" : b.PairBuyValueFilledCount.ToString(CultureInfo.InvariantCulture),
                b == null ? "0" : b.PairSellValueFilledCount.ToString(CultureInfo.InvariantCulture),
                b == null ? "0" : b.PairTotalValueFilledCount.ToString(CultureInfo.InvariantCulture),
                b == null ? "0" : b.PairSideCompletedCount.ToString(CultureInfo.InvariantCulture),
                b == null ? "0" : Number(b.PairEvidenceMaturity),
                b == null ? "0" : Number(b.PairSameDirectionSupport),
                b == null ? "0" : Number(b.PairMatureSameDirectionSupport),
                b == null ? "0" : Number(b.PairOuterOppositionRatio),
                b == null ? "0" : Number(b.PairExtendedOpposition),
                b == null ? "0" : Number(b.PairAppliedExtendedOpposition),
                b == null ? "0" : Number(b.EventValidationRetention),
                b == null ? "0" : Number(b.OppositePairPenalty),
                b == null ? "0" : Number(b.RawAccelerationEvidence), b == null ? "0" : Number(b.AppliedAccelerationAuthority),
                b == null ? "0" : Number(b.AppliedAccelerationEvidence), b == null ? "0" : Number(b.SignedRelativeAcceleration),
                b == null ? "0" : Number(b.AccelerationAuthority),
                b == null ? "0" : b.TrendPointSequence.ToString(CultureInfo.InvariantCulture),
                b == null ? string.Empty : FormatDateTime(b.TrendPointTime),
                b == null ? "0" : Number(b.TrendPointClose),
                b == null ? "0" : b.TrendMoveDirection.ToString(CultureInfo.InvariantCulture),
                b == null ? string.Empty : Safe(b.TrendLastPivotType),
                b == null ? "0" : Number(b.TrendLastPivotPrice),
                b == null ? string.Empty : FormatDateTime(b.TrendLastPivotTime),
                b == null ? string.Empty : FormatDateTime(b.TrendLastPivotConfirmedTime),
                b == null ? string.Empty : Safe(b.TrendLastPivotRelation),
                b == null ? "0" : Number(b.TrendPreviousHigh),
                b == null ? "0" : Number(b.TrendLatestHigh),
                b == null ? string.Empty : Safe(b.TrendHighRelation),
                b == null ? "0" : Number(b.TrendPreviousLow),
                b == null ? "0" : Number(b.TrendLatestLow),
                b == null ? string.Empty : Safe(b.TrendLowRelation),
                b == null ? string.Empty : Safe(b.TrendStructure),
                b == null ? string.Empty : Compact(b.TrendSummary, 700),
                b == null ? string.Empty : Safe(b.TrendPivotState),
                b == null ? string.Empty : Safe(b.TrendAnchorType),
                b == null ? "0" : Number(b.TrendAnchorPrice),
                b == null ? string.Empty : FormatDateTime(b.TrendAnchorTime),
                b == null ? "0" : Number(b.TrendEndpointPrice),
                b == null ? string.Empty : FormatDateTime(b.TrendEndpointTime),
                b == null ? string.Empty : Safe(b.TrendProtectedSwingType),
                b == null ? "0" : Number(b.TrendProtectedSwingPrice),
                b == null ? string.Empty : FormatDateTime(b.TrendProtectedSwingTime),
                b == null ? string.Empty : Safe(b.TrendReversalEvidenceType),
                b == null ? string.Empty : Safe(b.TrendReversalEvidenceState),
                b == null ? string.Empty : FormatDateTime(b.TrendReversalEvidenceTime),
                b == null ? "0" : Number(b.TrendReversalEvidencePrice),
                b == null ? "0" : Number(b.TrendReversalInvalidationLevel),
                b == null ? "0" : Number(b.TrendPivotToEvidenceSeconds),
                b == null ? "0" : Number(b.TrendEvidenceToConfirmSeconds),
                Bool(b != null && b.TrendTurnWatchActive),
                b == null ? string.Empty : Safe(b.TrendTurnWatchType),
                b == null ? "0" : Number(b.TrendTurnWatchPrice),
                b == null ? string.Empty : FormatDateTime(b.TrendTurnWatchPivotTime),
                b == null ? string.Empty : FormatDateTime(b.TrendTurnWatchDetectedTime),
                b == null ? "0" : Number(ElapsedSeconds(b.TrendTurnWatchPivotTime, b.TrendTurnWatchDetectedTime)),
                b == null ? "0" : Number(b.TrendTurnWatchElapsedSeconds),
                b == null ? "0" : Number(b.TrendTurnWatchOppositeExtremePrice),
                b == null ? string.Empty : FormatDateTime(b.TrendTurnWatchOppositeExtremeTime),
                b == null ? "0" : b.TrendTurnWatchOppositeProgressCount.ToString(CultureInfo.InvariantCulture),
                b == null ? "0" : Number(b.TrendTurnWatchWholeLegRange),
                b == null ? "0" : Number(b.TrendTurnWatchFirstOppositeDepth),
                b == null ? "0" : Number(b.TrendTurnWatchFirstOppositeToWholeLegRatio),
                b == null ? "0" : Number(b.TrendTurnWatchRecoveryDepth),
                b == null ? "0" : Number(b.TrendTurnWatchRecoveryRatio),
                b == null ? "0" : Number(b.TrendTurnWatchReextensionDepth),
                b == null ? "0" : Number(b.TrendTurnWatchReextensionRatio),
                b == null ? "0" : Number(b.TrendTurnWatchTotalReversalDepth),
                b == null ? "0" : Number(b.TrendTurnWatchTotalReversalToWholeLegRatio),
                b == null ? "0" : Number(b.TrendTurnWatchStructureQuality),
                b == null ? "0" : Number(b.TrendTurnWatchLocalStructureQuality),
                b == null ? "0" : Number(b.TrendTurnWatchBalancedFastQuality),
                Bool(b != null && b.TrendTurnWatchBalancedFastQualified),
                b == null ? "0" : b.TrendTurnWatchStructureResetCount.ToString(CultureInfo.InvariantCulture),
                Bool(b != null && b.TrendTurnWatchEarlyDirectionReady),
                b == null ? "대기" : TrendDirectionText(b.TrendTurnWatchW30Direction),
                b == null ? "대기" : TrendDirectionText(b.TrendTurnWatchW45Direction),
                b == null ? "대기" : TrendDirectionText(b.TrendTurnWatchW60Direction),
                b == null ? "0" : b.TrendTurnWatchW30SampleCount.ToString(CultureInfo.InvariantCulture),
                b == null ? "0" : b.TrendTurnWatchW45SampleCount.ToString(CultureInfo.InvariantCulture),
                b == null ? "0" : b.TrendTurnWatchW60SampleCount.ToString(CultureInfo.InvariantCulture),
                b == null ? "0" : Number(b.TrendTurnWatchW30CoverageSeconds),
                b == null ? "0" : Number(b.TrendTurnWatchW45CoverageSeconds),
                b == null ? "0" : Number(b.TrendTurnWatchW60CoverageSeconds),
                b == null ? string.Empty : Safe(b.TrendPendingPivotType),
                b == null ? "0" : Number(b.TrendPendingPivotPrice),
                b == null ? string.Empty : FormatDateTime(b.TrendPendingPivotTime),
                b == null ? string.Empty : FormatDateTime(b.TrendPendingTurnWatchDetectedTime),
                b == null ? string.Empty : FormatDateTime(b.TrendUnconfirmedSince),
                b == null ? "0" : Number(ElapsedSeconds(b.TrendPendingPivotTime, b.TrendUnconfirmedSince)),
                b == null ? "0" : Number(b.TrendPendingTurnWatchToValidationSeconds),
                b == null ? "0" : Number(b.TrendPendingDecisionElapsedSeconds),
                b == null ? "0" : Number(b.TrendLastPivotConfirmationDelaySeconds),
                b == null ? "W30Shadow=대기" : TrendWindowStageText(b.TrendPendingWindowStage),
                b == null ? "대기" : TrendDirectionText(b.TrendPendingW30Direction),
                b == null ? "대기" : TrendDirectionText(b.TrendPendingW45Direction),
                b == null ? "대기" : TrendDirectionText(b.TrendPendingW60Direction),
                b == null ? "0" : b.TrendPendingW30SampleCount.ToString(CultureInfo.InvariantCulture),
                b == null ? "0" : b.TrendPendingW45SampleCount.ToString(CultureInfo.InvariantCulture),
                b == null ? "0" : b.TrendPendingW60SampleCount.ToString(CultureInfo.InvariantCulture),
                b == null ? "0" : Number(b.TrendPendingW30CoverageSeconds),
                b == null ? "0" : Number(b.TrendPendingW45CoverageSeconds),
                b == null ? "0" : Number(b.TrendPendingW60CoverageSeconds),
                b == null ? "0" : Number(b.TrendPendingOppositeExtremePrice),
                b == null ? string.Empty : FormatDateTime(b.TrendPendingOppositeExtremeTime),
                b == null ? "0" : b.TrendPendingOppositeProgressCount.ToString(CultureInfo.InvariantCulture),
                b == null ? string.Empty : Safe(b.TrendLastTurnWatchType),
                b == null ? "0" : Number(b.TrendLastTurnWatchPrice),
                b == null ? string.Empty : FormatDateTime(b.TrendLastTurnWatchPivotTime),
                b == null ? string.Empty : FormatDateTime(b.TrendLastTurnWatchDetectedTime),
                b == null ? string.Empty : Safe(b.TrendLastTurnWatchDecisionResult),
                b == null ? string.Empty : FormatDateTime(b.TrendLastTurnWatchDecisionTime),
                b == null ? "0" : Number(ElapsedSeconds(b.TrendLastTurnWatchPivotTime, b.TrendLastTurnWatchDetectedTime)),
                b == null ? "0" : Number(b.TrendLastTurnWatchDecisionSeconds),
                b == null ? "0" : Number(ElapsedSeconds(b.TrendLastTurnWatchPivotTime, b.TrendLastTurnWatchDecisionTime)),
                b == null ? string.Empty : Safe(b.TrendLastCandidateType),
                b == null ? "0" : Number(b.TrendLastCandidatePrice),
                b == null ? string.Empty : FormatDateTime(b.TrendLastCandidatePivotTime),
                b == null ? string.Empty : FormatDateTime(b.TrendLastCandidateWatchDetectedTime),
                b == null ? string.Empty : FormatDateTime(b.TrendLastCandidateDetectedTime),
                b == null ? string.Empty : Safe(b.TrendLastCandidateDecisionResult),
                b == null ? "0" : Number(ElapsedSeconds(b.TrendLastCandidatePivotTime, b.TrendLastCandidateWatchDetectedTime)),
                b == null ? "0" : Number(ElapsedSeconds(b.TrendLastCandidateWatchDetectedTime, b.TrendLastCandidateDetectedTime)),
                b == null ? "0" : Number(ElapsedSeconds(b.TrendLastCandidatePivotTime, b.TrendLastCandidateDetectedTime)),
                b == null ? "0" : Number(b.TrendLastCandidateDecisionSeconds),
                b == null ? "0" : Number(ElapsedSeconds(b.TrendLastCandidatePivotTime, b.TrendLastCandidateDecisionTime)),
                b == null ? string.Empty : FormatDateTime(b.TrendLastCandidateDecisionTime),
                b == null ? "0" : b.TrendGeneration.ToString(CultureInfo.InvariantCulture),
                b == null ? "0" : Number(b.TrendAgeSeconds), Bool(b != null && b.TrendShortDataReady),
                Bool(b != null && b.TrendLongDataReady),
                b == null ? "0" : b.TrendShortSampleCount.ToString(CultureInfo.InvariantCulture),
                b == null ? "0" : b.TrendLongSampleCount.ToString(CultureInfo.InvariantCulture),
                b == null ? "0" : Number(b.TrendShortCoverageSeconds),
                b == null ? "0" : Number(b.TrendLongCoverageSeconds),
                b == null ? "0" : Number(b.TrendShortRawScore),
                b == null ? "0" : Number(b.TrendLongRawScore),
                b == null ? "0" : Number(b.TrendShortSlopePercentPerMinute),
                b == null ? "0" : Number(b.TrendLongSlopePercentPerMinute),
                b == null ? "0" : Number(b.TrendShortEfficiency),
                b == null ? "0" : Number(b.TrendLongEfficiency),
                b == null ? "0" : Number(b.TrendShortConsistency),
                b == null ? "0" : Number(b.TrendLongConsistency),
                b == null ? "0" : Number(b.TrendShortNetMovePercent),
                b == null ? "0" : Number(b.TrendLongNetMovePercent),
                b == null ? "0" : Number(b.TrendShortHigherHighRatio),
                b == null ? "0" : Number(b.TrendShortHigherLowRatio),
                b == null ? "0" : Number(b.TrendShortLowerHighRatio),
                b == null ? "0" : Number(b.TrendShortLowerLowRatio),
                b == null ? "0" : Number(b.TrendShortStructureScore),
                b == null ? "0" : Number(b.TrendLongHigherHighRatio),
                b == null ? "0" : Number(b.TrendLongHigherLowRatio),
                b == null ? "0" : Number(b.TrendLongLowerHighRatio),
                b == null ? "0" : Number(b.TrendLongLowerLowRatio),
                b == null ? "0" : Number(b.TrendLongStructureScore),
                b == null ? "0" : Number(b.TrendShortEmaScore),
                b == null ? "0" : Number(b.TrendLongEmaScore),
                b == null ? "0" : b.TrendShortDirection.ToString(CultureInfo.InvariantCulture),
                b == null ? "0" : b.TrendLongDirection.ToString(CultureInfo.InvariantCulture),
                b == null ? "0" : Number(b.TrendShortConfidence),
                b == null ? "0" : Number(b.TrendLongConfidence),
                b == null ? "0" : Number(b.TrendShortPersistence),
                b == null ? "0" : Number(b.TrendLongPersistence),
                b == null ? "0" : Number(b.TrendShortAuthority),
                b == null ? "0" : Number(b.TrendLongAuthority),
                b == null ? "0" : Number(b.TrendDominanceGap),
                b == null ? "0" : Number(b.TrendResolvedAuthority),
                b == null ? "0" : Number(b.TrendRequiredBuyAuthority),
                b == null ? "0" : b.ResolvedTrendDirection.ToString(CultureInfo.InvariantCulture),
                b == null ? string.Empty : Safe(b.TrendMode),
                Bool(b != null && b.TrendBuyAllowed), Bool(b != null && b.TrendSellAllowed),
                Bool(b != null && b.TrendCandidateAllowed),
                b == null ? string.Empty : Safe(b.TrendManagerReason),
                b == null ? "0" : Number(b.TrendPivotLegMovePercent),
                b == null ? "0" : Number(b.TrendPivotLegRequiredPercent),
                b == null ? "0" : Number(b.TrendPivotTurnMovePercent),
                b == null ? "0" : Number(b.TrendPivotTurnRequiredPercent),
                Bool(b != null && b.TrendPivotLegQualified),
                Bool(b != null && b.TrendPivotTurnQualified),
                b == null ? string.Empty : Safe(b.TrendPivotDetail),
                b == null ? string.Empty : Safe(b.CandidateLifecycleId),
                b == null ? string.Empty : Safe(b.CandidateLifecycleState),
                b == null ? string.Empty : FormatDateTime(b.CandidateCreatedAt),
                b == null ? "0" : b.CandidateOriginDirection.ToString(CultureInfo.InvariantCulture),
                b == null ? "0" : Number(b.CandidateOriginScore),
                b == null ? string.Empty : Safe(b.CandidateOriginEventKey),
                b == null ? "0" : b.CandidateStartTotalCloseSequence.ToString(CultureInfo.InvariantCulture),
                b == null ? "0" : b.CandidateSameDirectionEvidenceCount.ToString(CultureInfo.InvariantCulture),
                b == null ? string.Empty : Safe(b.CandidateOriginPriceState),
                b == null ? string.Empty : Safe(b.CandidateOriginMode),
                b == null ? "0" : Number(b.CandidateOriginSignalPrice),
                b == null ? string.Empty : FormatDateTime(b.PressureOriginTime),
                b == null ? "0" : Number(b.PressureOriginPrice),
                b == null ? "0" : Number(b.PressureOriginRawScore),
                b == null ? string.Empty : FormatDateTime(b.CandidateLastEvidenceTime),
                b == null ? "0" : Number(b.CandidateMaxQualifiedScore),
                b == null ? "0" : Number(b.CandidateReactionConfidence),
                b == null ? "0" : b.CandidatePreviousTotalCloseSequence.ToString(CultureInfo.InvariantCulture),
                b == null ? "0" : Number(b.CandidatePreviousBarClose),
                b == null ? "0" : Number(b.CandidatePreviousBarVwap),
                b == null ? "0" : b.CandidateAnchorTotalCloseSequence.ToString(CultureInfo.InvariantCulture),
                b == null ? string.Empty : FormatDateTime(b.CandidateAnchorTime),
                b == null ? "0" : Number(b.CandidateAnchorBarOpen),
                b == null ? "0" : Number(b.CandidateAnchorBarHigh),
                b == null ? "0" : Number(b.CandidateAnchorBarLow),
                b == null ? "0" : Number(b.CandidateAnchorBarClose),
                b == null ? "0" : Number(b.CandidateAnchorBarVwap),
                Bool(b != null && b.CandidateConflictOccurred),
                b == null ? string.Empty : FormatDateTime(b.CandidateConflictTime),
                b == null ? "0" : b.CandidateConflictDirection.ToString(CultureInfo.InvariantCulture),
                b == null ? string.Empty : Safe(b.CandidateConflictEventKey),
                b == null ? string.Empty : FormatDateTime(b.CandidateConfirmationTime),
                b == null ? "0" : b.CandidateConfirmationTotalCloseSequence.ToString(CultureInfo.InvariantCulture),
                b == null ? "0" : b.CandidateConfirmationDirection.ToString(CultureInfo.InvariantCulture),
                b == null ? string.Empty : Safe(b.CandidateConfirmationMode),
                b == null ? "0" : Number(b.CandidateConfirmationBarOpen),
                b == null ? "0" : Number(b.CandidateConfirmationBarHigh),
                b == null ? "0" : Number(b.CandidateConfirmationBarLow),
                b == null ? "0" : Number(b.CandidateConfirmationBarClose),
                b == null ? "0" : Number(b.CandidateConfirmationBarVwap),
                b == null ? string.Empty : Safe(b.CandidateResponsePriceState),
                b == null ? "0" : Number(b.CandidateWindowElapsedSeconds),
                b == null ? "0" : b.CandidateWindowTickCount.ToString(CultureInfo.InvariantCulture),
                b == null ? "0" : b.CandidateWindowDistinctTimeCount.ToString(CultureInfo.InvariantCulture),
                b == null ? "0" : Number(b.CandidateWindowNetMoveTicks),
                b == null ? "0" : Number(b.CandidateWindowFavorableTicks),
                b == null ? "0" : Number(b.CandidateWindowAdverseTicks),
                b == null ? "0" : Number(b.CandidateWindowEfficiency),
                b == null ? "0" : Number(b.CandidateWindowRequiredTicks),
                b == null ? string.Empty : Safe(b.CandidateDirectionResult),
                b == null ? string.Empty : Safe(b.CandidateConfirmationResult),
                b == null ? string.Empty : Safe(b.CandidateExecutionResult),
                b == null ? "0" : Number(b.RawBarPressureScore),
                b == null ? (signal == null ? "0" : signal.Score.ToString(CultureInfo.InvariantCulture)) : Number(b.FinalScore),
                b == null ? "70" : b.BuyThreshold.ToString(CultureInfo.InvariantCulture),
                b == null ? "-70" : b.SellThreshold.ToString(CultureInfo.InvariantCulture),
                Bool(feature.HasPosition), feature.HoldingQty.ToString(CultureInfo.InvariantCulture), Number(feature.EntryPrice),
                Number(feature.NetProfitRate), Number(feature.HoldSeconds), Bool(feature.HasPendingBuy), Bool(feature.HasPendingSell),
                feature.OpenOrderCount.ToString(CultureInfo.InvariantCulture), feature.Cash.ToString(CultureInfo.InvariantCulture),
                Number(feature.SymbolRemainingBudget), string.IsNullOrWhiteSpace(action) ? "Hold" : action, strategy,
                Bool(signal != null && signal.IsAdditionalBuyCandidate), Compact(reason, 700),
                b == null ? string.Empty : Safe(b.OperationalBlockStage)
            };

            Enqueue(new WriteRequest
            {
                Category = "FrameAnalysis",
                Code = code,
                Mode = mode,
                EventTime = time,
                Header = FrameHeader,
                Row = row
            });

            EnqueueTrendPivotSummary(code, mode, time, b);
        }

        private void EnqueueTrendPivotSummary(
            string code,
            string mode,
            DateTime time,
            SignalScoreBreakdown b)
        {
            if (b == null || string.IsNullOrWhiteSpace(code))
                return;

            string fingerprint = string.Join("|",
                Safe(b.TrendPivotState),
                b.ResolvedTrendDirection.ToString(CultureInfo.InvariantCulture),
                Safe(b.TrendLastPivotType),
                FormatDateTime(b.TrendLastPivotTime),
                FormatDateTime(b.TrendLastPivotConfirmedTime),
                Safe(b.TrendProtectedSwingType),
                FormatDateTime(b.TrendProtectedSwingTime),
                Number(b.TrendProtectedSwingPrice),
                Safe(b.TrendReversalEvidenceType),
                Safe(b.TrendReversalEvidenceState),
                FormatDateTime(b.TrendReversalEvidenceTime),
                Number(b.TrendReversalEvidencePrice),
                Number(b.TrendReversalInvalidationLevel),
                Bool(b.TrendTurnWatchActive),
                Safe(b.TrendTurnWatchType),
                FormatDateTime(b.TrendTurnWatchPivotTime),
                Number(b.TrendTurnWatchPrice),
                FormatDateTime(b.TrendTurnWatchDetectedTime),
                b.TrendTurnWatchOppositeProgressCount.ToString(CultureInfo.InvariantCulture),
                Number(b.TrendTurnWatchStructureQuality),
                Number(b.TrendTurnWatchLocalStructureQuality),
                Number(b.TrendTurnWatchBalancedFastQuality),
                Bool(b.TrendTurnWatchBalancedFastQualified),
                Number(b.TrendTurnWatchRecoveryRatio),
                Number(b.TrendTurnWatchReextensionRatio),
                Number(b.TrendTurnWatchTotalReversalToWholeLegRatio),
                b.TrendTurnWatchStructureResetCount.ToString(CultureInfo.InvariantCulture),
                Bool(b.TrendTurnWatchEarlyDirectionReady),
                b.TrendTurnWatchW30Direction.ToString(CultureInfo.InvariantCulture),
                b.TrendTurnWatchW45Direction.ToString(CultureInfo.InvariantCulture),
                b.TrendTurnWatchW60Direction.ToString(CultureInfo.InvariantCulture),
                Safe(b.TrendPendingPivotType),
                FormatDateTime(b.TrendPendingPivotTime),
                Number(b.TrendPendingPivotPrice),
                FormatDateTime(b.TrendUnconfirmedSince),
                FormatDateTime(b.TrendPendingTurnWatchDetectedTime),
                Number(b.TrendPendingTurnWatchToValidationSeconds),
                b.TrendPendingWindowStage.ToString(CultureInfo.InvariantCulture),
                b.TrendPendingW30Direction.ToString(CultureInfo.InvariantCulture),
                b.TrendPendingW45Direction.ToString(CultureInfo.InvariantCulture),
                b.TrendPendingW60Direction.ToString(CultureInfo.InvariantCulture),
                b.TrendPendingOppositeProgressCount.ToString(CultureInfo.InvariantCulture),
                Safe(b.TrendLastTurnWatchType),
                Number(b.TrendLastTurnWatchPrice),
                FormatDateTime(b.TrendLastTurnWatchPivotTime),
                FormatDateTime(b.TrendLastTurnWatchDetectedTime),
                Safe(b.TrendLastTurnWatchDecisionResult),
                FormatDateTime(b.TrendLastTurnWatchDecisionTime),
                Safe(b.TrendLastCandidateType),
                Number(b.TrendLastCandidatePrice),
                FormatDateTime(b.TrendLastCandidatePivotTime),
                FormatDateTime(b.TrendLastCandidateWatchDetectedTime),
                FormatDateTime(b.TrendLastCandidateDetectedTime),
                Safe(b.TrendLastCandidateDecisionResult),
                FormatDateTime(b.TrendLastCandidateDecisionTime),
                Number(b.TrendLastCandidateDecisionSeconds));

            string key = mode + "|" + code;
            string previous;
            if (_lastTrendFingerprint.TryGetValue(key, out previous) &&
                string.Equals(previous, fingerprint, StringComparison.Ordinal))
            {
                return;
            }
            _lastTrendFingerprint[key] = fingerprint;

            string direction = b.ResolvedTrendDirection > 0
                ? "상승"
                : b.ResolvedTrendDirection < 0
                    ? "하락"
                    : "미확정";

            string lastPivotConfirmationRange =
                b.TrendLastPivotTime != default(DateTime) &&
                b.TrendLastPivotConfirmedTime != default(DateTime)
                    ? FormatDateTime(b.TrendLastPivotTime) + " ~ " +
                      FormatDateTime(b.TrendLastPivotConfirmedTime)
                    : string.Empty;
            string currentObservationRange =
                string.Equals(b.TrendPivotState, "꺾임관찰", StringComparison.Ordinal) &&
                b.TrendTurnWatchDetectedTime != default(DateTime)
                    ? FormatDateTime(b.TrendTurnWatchDetectedTime) + " ~ " +
                      FormatDateTime(time)
                    : string.Equals(b.TrendPivotState, "전환검증", StringComparison.Ordinal) &&
                      b.TrendUnconfirmedSince != default(DateTime)
                        ? FormatDateTime(b.TrendUnconfirmedSince) + " ~ " +
                          FormatDateTime(time)
                        : string.Empty;

            string[] row =
            {
                FormatDateTime(time), code, Safe(b.TrendPivotState), direction,
                Safe(b.TrendAnchorType), FormatDateTime(b.TrendAnchorTime), Number(b.TrendAnchorPrice),
                FormatDateTime(b.TrendEndpointTime), Number(b.TrendEndpointPrice),
                Safe(b.TrendProtectedSwingType), FormatDateTime(b.TrendProtectedSwingTime), Number(b.TrendProtectedSwingPrice),
                Safe(b.TrendReversalEvidenceType), Safe(b.TrendReversalEvidenceState),
                FormatDateTime(b.TrendReversalEvidenceTime), Number(b.TrendReversalEvidencePrice),
                Number(b.TrendReversalInvalidationLevel), Number(b.TrendPivotToEvidenceSeconds), Number(b.TrendEvidenceToConfirmSeconds),
                Bool(b.TrendTurnWatchActive), Safe(b.TrendTurnWatchType), FormatDateTime(b.TrendTurnWatchPivotTime), Number(b.TrendTurnWatchPrice),
                FormatDateTime(b.TrendTurnWatchDetectedTime), Number(ElapsedSeconds(b.TrendTurnWatchPivotTime, b.TrendTurnWatchDetectedTime)), Number(b.TrendTurnWatchElapsedSeconds),
                Number(b.TrendTurnWatchOppositeExtremePrice), FormatDateTime(b.TrendTurnWatchOppositeExtremeTime), b.TrendTurnWatchOppositeProgressCount.ToString(CultureInfo.InvariantCulture),
                Number(b.TrendTurnWatchWholeLegRange), Number(b.TrendTurnWatchFirstOppositeDepth), Number(b.TrendTurnWatchFirstOppositeToWholeLegRatio),
                Number(b.TrendTurnWatchRecoveryDepth), Number(b.TrendTurnWatchRecoveryRatio), Number(b.TrendTurnWatchReextensionDepth), Number(b.TrendTurnWatchReextensionRatio),
                Number(b.TrendTurnWatchTotalReversalDepth), Number(b.TrendTurnWatchTotalReversalToWholeLegRatio), Number(b.TrendTurnWatchStructureQuality),
                Number(b.TrendTurnWatchLocalStructureQuality), Number(b.TrendTurnWatchBalancedFastQuality), Bool(b.TrendTurnWatchBalancedFastQualified),
                b.TrendTurnWatchStructureResetCount.ToString(CultureInfo.InvariantCulture), Bool(b.TrendTurnWatchEarlyDirectionReady),
                TrendDirectionText(b.TrendTurnWatchW30Direction), TrendDirectionText(b.TrendTurnWatchW45Direction), TrendDirectionText(b.TrendTurnWatchW60Direction),
                b.TrendTurnWatchW30SampleCount.ToString(CultureInfo.InvariantCulture), b.TrendTurnWatchW45SampleCount.ToString(CultureInfo.InvariantCulture), b.TrendTurnWatchW60SampleCount.ToString(CultureInfo.InvariantCulture),
                Number(b.TrendTurnWatchW30CoverageSeconds), Number(b.TrendTurnWatchW45CoverageSeconds), Number(b.TrendTurnWatchW60CoverageSeconds),
                Safe(b.TrendLastPivotType), FormatDateTime(b.TrendLastPivotTime), Number(b.TrendLastPivotPrice),
                FormatDateTime(b.TrendLastPivotConfirmedTime), lastPivotConfirmationRange, currentObservationRange,
                Number(b.TrendLastPivotConfirmationDelaySeconds),
                Safe(b.TrendPendingPivotType), FormatDateTime(b.TrendPendingPivotTime), Number(b.TrendPendingPivotPrice),
                FormatDateTime(b.TrendPendingTurnWatchDetectedTime), FormatDateTime(b.TrendUnconfirmedSince),
                Number(ElapsedSeconds(b.TrendPendingPivotTime, b.TrendUnconfirmedSince)), Number(b.TrendPendingTurnWatchToValidationSeconds), Number(b.TrendPendingDecisionElapsedSeconds),
                TrendWindowStageText(b.TrendPendingWindowStage),
                TrendDirectionText(b.TrendPendingW30Direction), TrendDirectionText(b.TrendPendingW45Direction), TrendDirectionText(b.TrendPendingW60Direction),
                b.TrendPendingW30SampleCount.ToString(CultureInfo.InvariantCulture),
                b.TrendPendingW45SampleCount.ToString(CultureInfo.InvariantCulture),
                b.TrendPendingW60SampleCount.ToString(CultureInfo.InvariantCulture),
                Number(b.TrendPendingW30CoverageSeconds), Number(b.TrendPendingW45CoverageSeconds), Number(b.TrendPendingW60CoverageSeconds),
                Number(b.TrendPendingOppositeExtremePrice), FormatDateTime(b.TrendPendingOppositeExtremeTime),
                b.TrendPendingOppositeProgressCount.ToString(CultureInfo.InvariantCulture),
                Safe(b.TrendLastTurnWatchType), Number(b.TrendLastTurnWatchPrice), FormatDateTime(b.TrendLastTurnWatchPivotTime), FormatDateTime(b.TrendLastTurnWatchDetectedTime),
                Safe(b.TrendLastTurnWatchDecisionResult), FormatDateTime(b.TrendLastTurnWatchDecisionTime),
                Number(ElapsedSeconds(b.TrendLastTurnWatchPivotTime, b.TrendLastTurnWatchDetectedTime)), Number(b.TrendLastTurnWatchDecisionSeconds),
                Number(ElapsedSeconds(b.TrendLastTurnWatchPivotTime, b.TrendLastTurnWatchDecisionTime)),
                Safe(b.TrendLastCandidateType), Number(b.TrendLastCandidatePrice),
                FormatDateTime(b.TrendLastCandidatePivotTime), FormatDateTime(b.TrendLastCandidateWatchDetectedTime), FormatDateTime(b.TrendLastCandidateDetectedTime),
                Safe(b.TrendLastCandidateDecisionResult), FormatDateTime(b.TrendLastCandidateDecisionTime),
                Number(ElapsedSeconds(b.TrendLastCandidatePivotTime, b.TrendLastCandidateWatchDetectedTime)),
                Number(ElapsedSeconds(b.TrendLastCandidateWatchDetectedTime, b.TrendLastCandidateDetectedTime)),
                Number(ElapsedSeconds(b.TrendLastCandidatePivotTime, b.TrendLastCandidateDetectedTime)),
                Number(b.TrendLastCandidateDecisionSeconds),
                Number(ElapsedSeconds(b.TrendLastCandidatePivotTime, b.TrendLastCandidateDecisionTime)),
                Compact(b.TrendSummary, 700)
            };

            Enqueue(new WriteRequest
            {
                Category = "추세Pivot",
                Code = code,
                Mode = mode,
                EventTime = time,
                Header = TrendPivotHeader,
                Row = row
            });
        }

        public void EnqueueOrderStage(
            DateTime eventTime,
            bool isReplayMode,
            string stage,
            string actionName,
            string code,
            int quantity,
            int orderPrice,
            int currentPrice,
            int bestAsk,
            int bestBid,
            string hogaType,
            TradeSignal signal,
            string message)
        {
            if (!ShouldCaptureOrderStage(stage))
                return;

            code = NormalizeCode(string.IsNullOrWhiteSpace(code) && signal != null ? signal.Code : code);
            if (string.IsNullOrWhiteSpace(code))
                return;
            string mode = isReplayMode ? "Replay" : "Real";
            SignalScoreBreakdown b = signal == null ? null : signal.ScoreBreakdown;
            string[] row =
            {
                FormatDateTime(eventTime), FormatDateTime(eventTime), mode, code, "OrderFlow", Safe(stage), Safe(actionName),
                signal == null ? string.Empty : Safe(signal.StrategyName), signal == null ? "0" : Number(signal.Price),
                currentPrice.ToString(CultureInfo.InvariantCulture), bestAsk.ToString(CultureInfo.InvariantCulture),
                bestBid.ToString(CultureInfo.InvariantCulture), orderPrice.ToString(CultureInfo.InvariantCulture),
                quantity.ToString(CultureInfo.InvariantCulture), Safe(hogaType),
                signal == null ? "0" : signal.Score.ToString(CultureInfo.InvariantCulture),
                b == null ? string.Empty : Safe(b.DecisionEventKey),
                b == null ? string.Empty : Safe(b.CandidateLifecycleId),
                string.Empty, string.Empty, string.Empty,
                string.Empty, string.Empty, "0", "0", "0", string.Empty, Compact(message, 1000)
            };
            Enqueue(new WriteRequest { Category = "OrderTrade", Code = code, Mode = mode, EventTime = eventTime, Header = OrderHeader, Row = row });
        }

        public void EnqueueOrderSnapshots(IEnumerable orders, bool isReplayMode, DateTime fallbackTime)
        {
            if (orders == null)
                return;
            string mode = isReplayMode ? "Replay" : "Real";

            foreach (object order in orders)
            {
                if (order == null)
                    continue;
                string code = NormalizeCode(GetString(order, "Code"));
                if (string.IsNullOrWhiteSpace(code))
                    continue;
                string orderNo = First(GetString(order, "OrderNo"), GetString(order, "OrderNumber"));
                string originalNo = First(GetString(order, "OriginalOrderNo"), GetString(order, "OrgOrderNo"));
                string orderType = First(GetString(order, "OrderType"), GetString(order, "Side"));
                string orderStatus = ConvertInvariant(GetValue(order, "OrderStatus"));
                string status = GetString(order, "Status");
                int qty = GetInt(order, "Qty", "Quantity");
                int orderPrice = GetInt(order, "Price", "OrderPrice");
                int execQty = GetInt(order, "ExecQty", "FilledQty");
                int untraded = GetInt(order, "UntradedQty", "RemainingQty");
                double execPrice = GetDouble(order, "ExecPrice", "FilledPrice");
                DateTime time = GetDate(order, "OrderTime", "Time", "CreatedAt");
                if (time == default(DateTime)) time = fallbackTime;
                string tradeGroup = GetString(order, "TradeGroupId");
                string fingerprint = string.Join("|", orderNo, originalNo, orderType, orderStatus, status, qty, orderPrice, execQty, untraded, execPrice, tradeGroup);
                string key = mode + "|" + code + "|" + orderNo;
                string previous;
                if (_lastOrderFingerprint.TryGetValue(key, out previous) && previous == fingerprint)
                    continue;
                _lastOrderFingerprint[key] = fingerprint;

                string[] row =
                {
                    FormatDateTime(time), FormatDateTime(time), mode, code, "OrderHistory", "Snapshot", orderType,
                    First(GetString(order, "StrategyName"), GetString(order, "Strategy")), "0", "0", "0", "0", string.Empty,
                    orderPrice.ToString(CultureInfo.InvariantCulture), qty.ToString(CultureInfo.InvariantCulture), string.Empty,
                    "0", string.Empty, orderNo, originalNo, orderType, orderStatus, status, Number(execPrice),
                    execQty.ToString(CultureInfo.InvariantCulture), untraded.ToString(CultureInfo.InvariantCulture),
                    tradeGroup, string.Empty
                };
                Enqueue(new WriteRequest { Category = "OrderTrade", Code = code, Mode = mode, EventTime = time, Header = OrderHeader, Row = row });
            }
        }

        public void Flush(TimeSpan timeout)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            var done = new ManualResetEventSlim(false);
            if (!Enqueue(new WriteRequest { Flush = true, Completion = done }))
                return;
            try { done.Wait(timeout); } catch { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            try { _queue.CompleteAdding(); } catch { }
            try { if (_thread.IsAlive) _thread.Join(2000); } catch { }
            CloseWriters();
            _queue.Dispose();
        }

        private bool Enqueue(WriteRequest request)
        {
            if (request == null || Volatile.Read(ref _disposed) != 0)
                return false;
            try
            {
                if (_queue.TryAdd(request)) return true;
            }
            catch { }
            Interlocked.Increment(ref _dropped);
            return false;
        }

        private void WriterLoop()
        {
            try
            {
                foreach (WriteRequest request in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        if (request.BeginSession)
                        {
                            CloseWritersFor(request.Code, request.Mode);
                            continue;
                        }
                        if (request.Flush)
                        {
                            foreach (StreamWriter writer in _writers.Values) writer.Flush();
                            if (request.Completion != null) request.Completion.Set();
                            continue;
                        }
                        Write(request);
                    }
                    catch
                    {
                        Interlocked.Increment(ref _dropped);
                        if (request.Completion != null) request.Completion.Set();
                    }
                }
            }
            finally { CloseWriters(); }
        }

        private void Write(WriteRequest request)
        {
            if (request.Header == null || request.Row == null || request.Header.Length != request.Row.Length)
                return;
            string key = request.Mode + "|" + request.Code + "|" + request.Category + "|" + request.EventTime.Date.ToString("yyyyMMdd");
            StreamWriter writer;
            if (!_writers.TryGetValue(key, out writer))
            {
                string directory = Path.Combine(_rootDirectory, request.Code);
                Directory.CreateDirectory(directory);
                string baseName = request.Code + "_" + request.EventTime.ToString("yyyyMMdd") + "_" + request.Mode + "_" + request.Category;
                string path = NextAvailablePath(directory, baseName, ".csv");
                writer = new StreamWriter(path, false, new UTF8Encoding(true))
                {
                    // 추세Pivot은 Replay 검증 중간에도 완전한 파일을 확보할 수 있게 즉시 Flush한다.
                    AutoFlush = string.Equals(request.Category, "추세Pivot", StringComparison.Ordinal)
                };
                writer.WriteLine(ToCsv(request.Header));
                _writers[key] = writer;
            }
            writer.WriteLine(ToCsv(request.Row));
        }

        private void CloseWritersFor(string code, string mode)
        {
            var keys = new List<string>();
            foreach (string key in _writers.Keys)
                if (key.StartsWith(mode + "|" + code + "|", StringComparison.OrdinalIgnoreCase)) keys.Add(key);
            foreach (string key in keys)
            {
                try { _writers[key].Flush(); _writers[key].Dispose(); } catch { }
                _writers.Remove(key);
            }
        }

        private void CloseWriters()
        {
            foreach (StreamWriter writer in _writers.Values)
                try { writer.Flush(); writer.Dispose(); } catch { }
            _writers.Clear();
        }

        private static string NextAvailablePath(string directory, string baseName, string extension)
        {
            string path = Path.Combine(directory, baseName + extension);
            if (!File.Exists(path)) return path;
            for (int i = 1; i < int.MaxValue; i++)
            {
                path = Path.Combine(directory, baseName + "(" + i + ")" + extension);
                if (!File.Exists(path)) return path;
            }
            return Path.Combine(directory, baseName + "_" + Guid.NewGuid().ToString("N") + extension);
        }

        private static bool ShouldCaptureOrderStage(string stage)
        {
            if (string.IsNullOrWhiteSpace(stage)) return false;
            string s = stage.ToUpperInvariant();
            return s.Contains("SEND") || s.Contains("ORDER") || s.Contains("CANCEL") ||
                   s.Contains("REPLACE") || s.Contains("EXEC") || s.Contains("FILL") || s.Contains("CHEJAN");
        }

        private static string ToCsv(IEnumerable<string> values)
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (string value in values)
            {
                if (!first) sb.Append(',');
                first = false;
                string text = value ?? string.Empty;
                if (text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
                    sb.Append('"').Append(text.Replace("\"", "\"\"")).Append('"');
                else sb.Append(text);
            }
            return sb.ToString();
        }

        private static string TrendWindowStageText(int stage)
        {
            return stage >= 1 ? "W30Shadow=방향관측" : "W30Shadow=대기";
        }

        private static string TrendDirectionText(int direction)
        {
            return direction > 0 ? "상승" : direction < 0 ? "하락" : "대기";
        }

        private static string NormalizeCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return string.Empty;
            code = code.Trim();
            return code.StartsWith("A", StringComparison.OrdinalIgnoreCase) && code.Length > 1 ? code.Substring(1) : code;
        }
        private static string Safe(string value) { return value ?? string.Empty; }
        private static string Bool(bool value) { return value ? "Y" : "N"; }
        private static string Number(double value) { return double.IsNaN(value) || double.IsInfinity(value) ? "0" : value.ToString("0.########", CultureInfo.InvariantCulture); }
        private static string FormatDateTime(DateTime value) { return value == default(DateTime) ? string.Empty : value.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture); }
        private static string Compact(string value, int max) { value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " "); return value.Length <= max ? value : value.Substring(0, max); }
        private static string First(string a, string b) { return !string.IsNullOrWhiteSpace(a) ? a : b ?? string.Empty; }
        private static object GetValue(object target, string name) { try { PropertyInfo p = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase); return p == null ? null : p.GetValue(target, null); } catch { return null; } }
        private static string GetString(object target, string name) { return ConvertInvariant(GetValue(target, name)); }
        private static string ConvertInvariant(object value) { return value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty; }
        private static int GetInt(object target, params string[] names) { foreach (string n in names) { object v = GetValue(target, n); if (v == null) continue; int x; if (int.TryParse(ConvertInvariant(v), NumberStyles.Any, CultureInfo.InvariantCulture, out x)) return x; } return 0; }
        private static double ElapsedSeconds(DateTime from, DateTime to) { return from == default(DateTime) || to == default(DateTime) ? 0.0 : Math.Max(0.0, (to - from).TotalSeconds); }
        private static double GetDouble(object target, params string[] names) { foreach (string n in names) { object v = GetValue(target, n); if (v == null) continue; double x; if (double.TryParse(ConvertInvariant(v), NumberStyles.Any, CultureInfo.InvariantCulture, out x)) return x; } return 0.0; }
        private static DateTime GetDate(object target, params string[] names) { foreach (string n in names) { object v = GetValue(target, n); if (v is DateTime) return (DateTime)v; DateTime x; if (DateTime.TryParse(ConvertInvariant(v), CultureInfo.InvariantCulture, DateTimeStyles.None, out x)) return x; } return default(DateTime); }

        private sealed class WriteRequest
        {
            public bool BeginSession;
            public bool Flush;
            public string Category;
            public string Code;
            public string Mode;
            public DateTime EventTime;
            public string[] Header;
            public string[] Row;
            public ManualResetEventSlim Completion;
        }
    }
}
