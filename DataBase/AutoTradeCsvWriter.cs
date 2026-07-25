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
    /// 자동매매 분석 전용 CSV 비동기 저장기.
    /// 매매/분석 스레드는 CSV 문자열만 Queue에 넣고 실제 파일 I/O는 전용 Writer Thread가 처리한다.
    /// 저장 위치: F:\Project\LOG\{종목코드}\
    /// 같은 실행 세션의 Frame/Pivot/Order CSV는 동일한 중복 번호를 공유하며 기존 파일을 덮어쓰지 않는다.
    /// </summary>
    public sealed class AutoTradeCsvWriter : IDisposable
    {
        public static readonly AutoTradeCsvWriter Shared = new AutoTradeCsvWriter();

        private const int QueueCapacity = 100000;
        private const int BatchSize = 512;

        private readonly BlockingCollection<CsvWriteRequest> _queue;
        private readonly Thread _writerThread;
        private readonly Dictionary<string, StreamWriter> _writers =
            new Dictionary<string, StreamWriter>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, PriceRegimeSnapshot> _lastPriceRegimeByCode =
            new ConcurrentDictionary<string, PriceRegimeSnapshot>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _lastOrderFingerprintByKey =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _sessionFilePathLock = new object();
        private readonly Dictionary<string, int> _sessionSuffixByKey =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _sessionDataDateByKey =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private readonly string _rootDirectory;
        private int _disposed;
        private long _droppedRowCount;

        private static readonly string[] FrameHeader =
        {
            "RecordedAt", "AnalysisTime", "Mode", "Code", "SnapshotId", "SignalId", "CandidateId", "EnginePatch",
            "DecisionBarType", "DecisionBarCloseReason",
            "Price", "FrameOpen", "FrameHigh", "FrameLow", "FrameClose", "FrameVwap", "FrameTickCount",
            "FrameElapsedSeconds", "FrameTradeValue", "FrameBuyValue", "FrameSellValue", "FrameNeutralValue",
            "FrameNetBuyValue", "FramePurity", "FrameStrength", "FrameHasTimeGap",
            "FrameExpectedTradeValue", "FrameValueRatio", "FrameInformationConfidence",
            "BootstrapElapsedSeconds", "BootstrapTradeValue", "BootstrapCoverageRatio",
            "BootstrapLargestTickShare", "BootstrapSource",
            "DTargetPrevious", "DTargetCurrent", "DTargetRawEventObserved", "DTargetObserved",
            "DTargetObservationWindowSeconds", "DTargetObservationCoverageSeconds",
            "DTargetObservationEventCount", "DTargetObservationUsedBootstrap",
            "DTargetRawAppliedLogChange", "DTargetAppliedLogChange",
            "DTargetNext", "DTargetChangeRatio",
            "DTargetUpdateReason", "DTargetSourceDurationSeconds", "DTargetEffectiveDurationSeconds",
            "DTargetInternalTrend", "DTargetConfidence", "DTargetTimeReliability",
            "DTargetSampleReliability", "DTargetRecentConsistency", "DTargetLargestTickShare",
            "DTargetSourceWasTimeout", "DTargetTimeoutStreak", "DTargetMaintenanceReason",
            "BuyTargetRatioToTotal", "SellTargetRatioToTotal", "SideTargetRatioSum",
            "BuyTargetTradeValue", "SellTargetTradeValue",
            "TotalGeneration", "DecisionBarGeneration",
            "BuySideTargetEma", "SellSideTargetEma",
            "BuyEmaAppliedGeneration", "SellEmaAppliedGeneration",
            "TotalBarFill", "BuyBarFill", "SellBarFill", "BuyBarSpeed", "SellBarSpeed",
            "BuyValueClosed60s", "SellValueClosed60s", "TotalBarValueClosed", "BuyBarValueClosed", "SellBarValueClosed",
            "DollarBarDPos", "ClosedEvidence", "LiveEvidence", "OfficialPressure",
            "PressureStateEvidence", "PressureStatePart",
            "PressureTransitionEvidence", "PressureTransitionPart",
            "PressureAccelerationEvidence", "PressureAccelerationPart",
            "RawPressureAgreementPart", "AgreementIndependentSupport", "PressureAgreementPart",
            "HistoricalPressureEvidence",
            "BuyRawVelocity", "BuyPreviousRawVelocity", "SellRawVelocity", "SellPreviousRawVelocity",
            "BuyTargetNormalizedRate", "BuyPreviousTargetNormalizedRate",
            "SellTargetNormalizedRate", "SellPreviousTargetNormalizedRate",
            "BuyRawMoneyAcceleration", "SellRawMoneyAcceleration",
            "BuyTargetNormalizedAcceleration", "SellTargetNormalizedAcceleration",
            "BuyTargetDriftContribution", "SellTargetDriftContribution",
            "OfficialBuyAcceleration", "OfficialSellAcceleration",
            "OfficialRawAccelerationDifference", "SignedRelativeAcceleration", "AccelerationAuthority",
            "BuyEvidenceAuthority", "SellEvidenceAuthority",
            "BuyPairTimingReliability", "SellPairTimingReliability",
            "BuyPressureObservationCount", "SellPressureObservationCount",
            "TransitionObservationCount", "TransitionObservationMaturity",
            "BuyCurrentCloseSequence", "BuyPreviousCloseSequence",
            "SellCurrentCloseSequence", "SellPreviousCloseSequence",
            "BuyValueFilledThisTick", "SellValueFilledThisTick",
            "BuyTimedOutThisTick", "SellTimedOutThisTick",
            "RawBarPressure", "PressureTurn", "CurrentPricePath", "PriceAuthority",
            "TrendTransitionAcceptance", "ExistingTrendAuthority", "EmergingTrendAuthority",
            "MaturityRecovery", "AuthorityTransfer",
            "UpTransitionMemory", "UpTransitionSupportSeconds", "UpTransitionMemoryProgress",
            "UpTransitionPermissionAuthority", "UpTransitionCurrentCompatibility",
            "PeakRelation", "TroughRelation", "PressurePersistence", "PreviousFrameDirection",
            "ClosedAlignment", "EffectivePressure",
            "PriceResponse", "Absorption", "PressureConfidence", "BaseFinal", "EntryScore", "ExitScore",
            "StructureDataReady", "StructureDirectionalSupport", "StructureAuthority",
            "StructureContribution", "StructureAdjustedScore", "StructureRawBaseScore",
            "StructureGenerationSampleCount", "StructureEmergingMean3", "StructureMaturityRecoveryMean3",
            "StructureRecoveryAuthority", "StructureConflictAuthority", "StructureAccelerationConflictSeverity", "StructureChaseRisk",
            "StructureAccelerationAuthority", "StructureAccelerationContribution",
            "StructurePriceResponseAuthority", "StructurePriceResponseContribution",
            "EntryRiskScore", "InitialEntryRisk", "AdditionalEntryRisk",
            "EntryRiskContribution", "EntryRiskAdjustedScore",
            "UnifiedExitEvidence", "UnifiedExitContribution", "UnifiedExitAdjustedScore",
            "OfficialFinal", "CandidateStrength", "CurrentOfficialStrength", "CurrentSupportAuthority",
            "ContinuityFinal", "StrengthContinuityPassed",
            "ProfitDrawdownContribution", "ProfitMarketBreakEvidence", "ProfitMarketAcceptance",
            "PersistentExitEvidence", "PersistentExitContribution", "PersistentCurrentApplicationAuthority",
            "RecentWorstOfficialFinal", "ResidualExitReEntryRisk", "ResidualExitPenalty", "ExitMemoryResetReason",
            "FinalScore", "BuyThreshold", "SellThreshold",
            "PivotThresholdPercent", "LastConfirmedPeak", "LastConfirmedTrough", "StructureRangePercent",
            "StructureRangePosition", "PeakHeadroomPercent", "PeakChaseRisk", "EntryLocationAcceptance",
            "BreakoutAcceptance", "BreakoutDepth", "BreakoutHoldPersistence",
            "BreakoutClosedAlignment", "BreakoutRetestAcceptance",
            "PriceRegime", "ContractionBreakInstantSupport",
            "ContractionBreakPersistence", "ContractionBreakSupport", "ContractionBreakReady", "UptrendBreakoutReady",
            "FlatBuyPermission", "AdditionalBuyPermission", "ActiveBuyPermission",
            "HasPosition", "HoldingQty", "EntryPrice", "NetProfitRate", "NetMaxProfitRate", "NetDrawdownRate", "HoldSeconds",
            "HasPendingBuy", "HasPendingSell", "OpenOrderCount", "Cash", "SymbolRemainingBudget",
            "Action", "Strategy", "IsAdditionalBuyCandidate", "DecisionReason", "OrderBlockReason",
            "TiRexDirection", "TiRexConfidence", "TiRexPredictedReturn"
        };

        private static readonly string[] PivotHeader =
        {
            "RecordedAt", "AnalysisTime", "Mode", "Code", "SnapshotId", "SignalId", "CandidateId", "EnginePatch",
            "EventType", "CurrentPrice", "PreviousPeak", "CurrentPeak", "PreviousTrough", "CurrentTrough",
            "PreviousRegime", "CurrentRegime", "PivotThresholdPercent", "StructureRangePercent",
            "StructureIntegrity", "StructureMaturity", "EffectiveStructureIntegrity",
            "TrendTransitionDirection", "TrendTransitionProgress", "TrendTransitionPersistence",
            "TrendTransitionAcceptance", "ExistingTrendAuthority", "EmergingTrendAuthority",
            "MaturityRecovery", "AuthorityTransfer",
            "UpTransitionMemory", "UpTransitionSupportSeconds", "UpTransitionMemoryProgress",
            "UpTransitionPermissionAuthority", "UpTransitionCurrentCompatibility",
            "PeakRelation", "TroughRelation", "PressurePersistence",
            "PreviousFrameDirection", "ClosedAlignment", "StructureRangePosition", "PeakHeadroomPercent",
            "PeakChaseRisk", "EntryLocationAcceptance", "BreakoutAcceptance",
            "BreakoutDepth", "BreakoutHoldPersistence", "BreakoutClosedAlignment",
            "BreakoutRetestAcceptance", "ContractionBreakInstantSupport",
            "ContractionBreakPersistence", "ContractionBreakSupport",
            "ContractionBreakReady", "UptrendBreakoutReady",
            "FlatBuyPermission", "AdditionalBuyPermission", "DecisionReason"
        };

        private static readonly string[] OrderHeader =
        {
            "RecordedAt", "EventTime", "Mode", "Code", "Source", "EventType", "Side", "Strategy",
            "SnapshotId", "SignalPrice", "CurrentPrice", "BestAsk", "BestBid", "OrderPrice", "Quantity", "HogaType",
            "FinalScore", "PriceRegime", "BuyPermission", "IsAdditionalBuyCandidate",
            "OrderNo", "OriginalOrderNo", "OrderStatus", "Status", "ExecPrice", "ExecQty", "UntradedQty",
            "Fee", "Tax", "TradeGroupId", "Message"
        };

        private AutoTradeCsvWriter()
        {
            _rootDirectory = @"F:\Project\LOG";

            _queue = new BlockingCollection<CsvWriteRequest>(
                new ConcurrentQueue<CsvWriteRequest>(),
                QueueCapacity);

            _writerThread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "AutoTradeCsvWriter"
            };
            _writerThread.Start();

            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        }

        public long DroppedRowCount
        {
            get { return Interlocked.Read(ref _droppedRowCount); }
        }

        /// <summary>
        /// 새 Replay/Real 자동매매 세션을 시작한다.
        /// Writer Thread에서 기존 Writer를 Flush/Close하고 다음 중복 번호 세트를 다시 선택한다.
        /// </summary>
        public void BeginSession(string code, DateTime tradingDate, bool isReplayMode)
        {
            string safeCode = NormalizeCode(code);
            if (string.IsNullOrWhiteSpace(safeCode) ||
                tradingDate == default(DateTime) ||
                tradingDate == DateTime.MinValue)
            {
                return;
            }

            string mode = isReplayMode ? "Replay" : "Real";
            DateTime dataDate = tradingDate.Date;
            _sessionDataDateByKey[BuildSessionDataDateKey(safeCode, mode)] = dataDate;

            Enqueue(new CsvWriteRequest
            {
                BeginSession = true,
                Code = safeCode,
                EventTime = dataDate,
                Mode = mode
            });
        }

        public void EnqueueFrame(
            FeatureSnapshot feature,
            TradeSignal signal,
            bool isReplayMode,
            string signalId,
            string candidateId)
        {
            if (feature == null ||
                !feature.IsClosedDecisionBar ||
                string.IsNullOrWhiteSpace(feature.Code))
            {
                return;
            }

            string mode = isReplayMode ? "Replay" : "Real";
            string code = NormalizeCode(feature.Code);
            DateTime analysisTime = ResolveDataTime(
                code,
                mode,
                feature.Time,
                default(DateTime));
            SignalScoreBreakdown breakdown = signal == null ? null : signal.ScoreBreakdown;
            string reason = signal == null ? feature.SignalReason : signal.Reason;
            string flatBuyPermission = ExtractDecisionValue(reason, "FlatBuyPermission");
            string additionalBuyPermission = ExtractDecisionValue(reason, "AdditionalBuyPermission");
            string activeBuyPermission = signal != null && signal.IsAdditionalBuyCandidate
                ? additionalBuyPermission
                : flatBuyPermission;

            string action = signal == null
                ? Safe(feature.SignalAction)
                : signal.Action.ToString();
            if (string.IsNullOrWhiteSpace(action))
                action = "Hold";

            string strategy = signal == null
                ? Safe(feature.StrategyName)
                : Safe(signal.StrategyName);

            string decisionReason = ExtractDecisionValue(reason, "Reason");
            if (string.IsNullOrWhiteSpace(decisionReason))
                decisionReason = Compact(reason, 500);

            string orderBlockReason = ResolveOrderBlockReason(reason);
            string priceRegime = breakdown == null ? string.Empty : Safe(breakdown.PriceRegime);

            string[] row =
            {
                FormatDateTime(ResolveRecordedAt(code, mode, analysisTime)),
                FormatDateTime(analysisTime),
                mode,
                code,
                Safe(feature.SnapshotId),
                Safe(signalId),
                Safe(candidateId),
                Safe(ReadBreakdownString(breakdown, "EnginePatchVersion")),
                Safe(feature.DecisionBarType),
                Safe(feature.DecisionBarCloseReason),
                Number(feature.Price),
                Number(feature.FrameOpenPrice),
                Number(feature.FrameHighPrice),
                Number(feature.FrameLowPrice),
                Number(feature.Price),
                Number(feature.FrameVwap),
                Integer(feature.FrameTickCount),
                Number(feature.FrameElapsedSeconds),
                Number(feature.TickTradeValue),
                Number(feature.FrameBuyTradeValue),
                Number(feature.FrameSellTradeValue),
                Number(feature.FrameNeutralTradeValue),
                Number(feature.FrameNetBuyTradeValue),
                Number(feature.FrameDirectionalPurity),
                Number(feature.FrameValueWeightedStrength),
                Bool(feature.FrameHasTimeGap),
                Number(feature.FrameExpectedTradeValue),
                Number(feature.FrameValueRatio),
                Number(feature.FrameInformationConfidence),
                Number(feature.BarBootstrapElapsedSeconds),
                Number(feature.BarBootstrapTradeValue),
                Number(feature.BarBootstrapCoverageRatio),
                Number(feature.BarBootstrapLargestTickShare),
                Safe(feature.BarBootstrapSource),
                Number(feature.BarPreviousTargetTradeValue),
                Number(feature.BarCurrentTargetTradeValue),
                Number(feature.BarRawEventObservedTargetTradeValue),
                Number(feature.BarObservedTargetTradeValue),
                Number(feature.BarDTargetObservationWindowSeconds),
                Number(feature.BarDTargetObservationCoverageSeconds),
                Integer(feature.BarDTargetObservationEventCount),
                Bool(feature.BarDTargetObservationUsedBootstrap),
                Number(feature.BarDTargetRawAppliedLogChange),
                Number(feature.BarDTargetAppliedLogChange),
                Number(feature.BarNextTargetTradeValue),
                Number(feature.BarDTargetChangeRatio),
                Safe(feature.BarDTargetUpdateReason),
                Number(feature.BarDTargetSourceDurationSeconds),
                Number(feature.BarDTargetEffectiveDurationSeconds),
                Number(feature.BarDTargetInternalTrend),
                Number(feature.BarDTargetConfidence),
                Number(feature.BarDTargetTimeReliability),
                Number(feature.BarDTargetSampleReliability),
                Number(feature.BarDTargetRecentConsistency),
                Number(feature.BarDTargetSourceOvershootRatio),
                Bool(feature.BarDTargetSourceWasTimeout),
                Integer(feature.BarNoLiquidityTimeoutCount),
                // v135 SignalEngine은 FinalScore trace를 DTargetMaintenanceReason에 중복 삽입하지 않는다.
                // 이 컬럼은 DTarget 자체 진단만 보존한다.
                Compact(feature.BarDTargetMaintenanceReason, 2000),
                Number(feature.BuyDollarBarTargetRatioToTotal),
                Number(feature.SellDollarBarTargetRatioToTotal),
                Number(feature.BuyDollarBarTargetRatioToTotal + feature.SellDollarBarTargetRatioToTotal),
                Number(feature.BuyDollarBarTargetTradeValue),
                Number(feature.SellDollarBarTargetTradeValue),
                Integer(feature.BarTotalGeneration),
                Integer(feature.DecisionBarTotalGeneration),
                Number(feature.BuySideTargetPressureEma),
                Number(feature.SellSideTargetPressureEma),
                Integer(feature.BuySidePressureAppliedGeneration),
                Integer(feature.SellSidePressureAppliedGeneration),
                Number(feature.DollarBarFillRatio),
                Number(feature.BuyDollarBarFillRatio),
                Number(feature.SellDollarBarFillRatio),
                Number(feature.BuyDollarBarSpeedRatio),
                Number(feature.SellDollarBarSpeedRatio),
                Integer(feature.BuyDollarBarValueClosedCount60s),
                Integer(feature.SellDollarBarValueClosedCount60s),
                Bool(feature.DollarBarIsValueFilledClosed),
                Bool(feature.BuyDollarBarIsValueFilledClosed),
                Bool(feature.SellDollarBarIsValueFilledClosed),
                Number(feature.DollarBarDecisionDPos),
                Number(feature.BarSidePressureValueBias * 100.0),
                Number(feature.BarSidePressureSpeedBias * 100.0),
                Number(feature.BarSidePressureBias * 100.0),
                Number(ReadBreakdown(breakdown, "PressureStateEvidence")),
                Number(ReadBreakdown(breakdown, "PressureStatePart")),
                Number(ReadBreakdown(breakdown, "PressureTransitionEvidence")),
                Number(ReadBreakdown(breakdown, "PressureTransitionPart")),
                Number(ReadBreakdown(breakdown, "PressureAccelerationEvidence")),
                Number(ReadBreakdown(breakdown, "PressureAccelerationPart")),
                Number(ReadBreakdown(breakdown, "RawPressureAgreementPart")),
                Number(ReadBreakdown(breakdown, "AgreementIndependentSupport")),
                Number(ReadBreakdown(breakdown, "PressureAgreementPart")),
                Number(ReadBreakdown(breakdown, "HistoricalPressureEvidence")),
                Number(ReadBreakdown(breakdown, "BuyRawVelocity")),
                Number(ReadBreakdown(breakdown, "BuyPreviousRawVelocity")),
                Number(ReadBreakdown(breakdown, "SellRawVelocity")),
                Number(ReadBreakdown(breakdown, "SellPreviousRawVelocity")),
                Number(ReadBreakdown(breakdown, "BuyTargetNormalizedRate")),
                Number(ReadBreakdown(breakdown, "BuyPreviousTargetNormalizedRate")),
                Number(ReadBreakdown(breakdown, "SellTargetNormalizedRate")),
                Number(ReadBreakdown(breakdown, "SellPreviousTargetNormalizedRate")),
                Number(ReadBreakdown(breakdown, "BuyRawMoneyAcceleration")),
                Number(ReadBreakdown(breakdown, "SellRawMoneyAcceleration")),
                Number(ReadBreakdown(breakdown, "BuyTargetNormalizedAcceleration")),
                Number(ReadBreakdown(breakdown, "SellTargetNormalizedAcceleration")),
                Number(ReadBreakdown(breakdown, "BuyTargetDriftContribution")),
                Number(ReadBreakdown(breakdown, "SellTargetDriftContribution")),
                Number(ReadBreakdown(breakdown, "OfficialBuyAcceleration")),
                Number(ReadBreakdown(breakdown, "OfficialSellAcceleration")),
                Number(ReadBreakdown(breakdown, "OfficialRawAccelerationDifference")),
                Number(ReadBreakdown(breakdown, "SignedRelativeAcceleration")),
                Number(ReadBreakdown(breakdown, "AccelerationAuthority")),
                Number(ReadBreakdown(breakdown, "BuyEvidenceAuthority")),
                Number(ReadBreakdown(breakdown, "SellEvidenceAuthority")),
                Number(ReadBreakdown(breakdown, "BuyPairTimingReliability")),
                Number(ReadBreakdown(breakdown, "SellPairTimingReliability")),
                Integer(ReadBreakdownInt(breakdown, "BuyPressureObservationCount")),
                Integer(ReadBreakdownInt(breakdown, "SellPressureObservationCount")),
                Integer(ReadBreakdownInt(breakdown, "TransitionObservationCount")),
                Number(ReadBreakdown(breakdown, "TransitionObservationMaturity")),
                Number(ReadBreakdown(breakdown, "BuyCurrentCloseSequence")),
                Number(ReadBreakdown(breakdown, "BuyPreviousCloseSequence")),
                Number(ReadBreakdown(breakdown, "SellCurrentCloseSequence")),
                Number(ReadBreakdown(breakdown, "SellPreviousCloseSequence")),
                Bool(ReadBreakdownBool(breakdown, "BuyValueFilledThisTick")),
                Bool(ReadBreakdownBool(breakdown, "SellValueFilledThisTick")),
                Bool(ReadBreakdownBool(breakdown, "BuyTimedOutThisTick")),
                Bool(ReadBreakdownBool(breakdown, "SellTimedOutThisTick")),
                Number(ReadBreakdown(breakdown, "RawBarPressureScore")),
                Number(ReadBreakdown(breakdown, "PressureTurnScore")),
                Number(ReadBreakdown(breakdown, "PricePathScore")),
                Number(ReadBreakdown(breakdown, "PriceAuthorityScore")),
                Number(ReadBreakdown(breakdown, "TrendTransitionAcceptance")),
                Number(ReadBreakdown(breakdown, "ExistingTrendAuthority")),
                Number(ReadBreakdown(breakdown, "EmergingTrendAuthority")),
                Number(ReadBreakdown(breakdown, "MaturityRecovery")),
                Number(ReadBreakdown(breakdown, "AuthorityTransfer")),
                Number(ReadBreakdown(breakdown, "UpTransitionMemory")),
                Number(ReadBreakdown(breakdown, "UpTransitionSupportSeconds")),
                Number(ReadBreakdown(breakdown, "UpTransitionMemoryProgress")),
                Number(ReadBreakdown(breakdown, "UpTransitionPermissionAuthority")),
                Number(ReadBreakdown(breakdown, "UpTransitionCurrentCompatibility")),
                Safe(ReadBreakdownString(breakdown, "PeakRelation")),
                Safe(ReadBreakdownString(breakdown, "TroughRelation")),
                Number(ReadBreakdown(breakdown, "PressurePersistence")),
                Number(ReadBreakdown(breakdown, "PreviousFrameDirection")),
                Number(ReadBreakdown(breakdown, "ClosedAlignment")),
                Number(ReadBreakdown(breakdown, "EffectiveBarPressureScore")),
                Number(ReadBreakdown(breakdown, "BarPriceResponseScore")),
                Number(ReadBreakdown(breakdown, "BarAbsorptionScore")),
                Number(feature.BarSidePressureConfidence),
                Number(ReadBreakdown(breakdown, "BaseFinal")),
                Number(ReadBreakdown(breakdown, "EntryScore")),
                Number(ReadBreakdown(breakdown, "ExitScore")),
                Bool(ReadBreakdownBool(breakdown, "StructureDataReady")),
                Number(ReadBreakdown(breakdown, "StructureDirectionalSupport")),
                Number(ReadBreakdown(breakdown, "StructureAuthority")),
                Number(ReadBreakdown(breakdown, "StructureContribution")),
                Number(ReadBreakdown(breakdown, "StructureAdjustedScore")),
                Number(ReadBreakdown(breakdown, "StructureRawBaseScore")),
                Number(ReadBreakdown(breakdown, "StructureGenerationSampleCount")),
                Number(ReadBreakdown(breakdown, "StructureEmergingMean3")),
                Number(ReadBreakdown(breakdown, "StructureMaturityRecoveryMean3")),
                Number(ReadBreakdown(breakdown, "StructureRecoveryAuthority")),
                Number(ReadBreakdown(breakdown, "StructureConflictAuthority")),
                Number(ReadBreakdown(breakdown, "StructureAccelerationConflictSeverity")),
                Number(ReadBreakdown(breakdown, "StructureChaseRisk")),
                Number(ReadBreakdown(breakdown, "StructureAccelerationAuthority")),
                Number(ReadBreakdown(breakdown, "StructureAccelerationContribution")),
                Number(ReadBreakdown(breakdown, "StructurePriceResponseAuthority")),
                Number(ReadBreakdown(breakdown, "StructurePriceResponseContribution")),
                Number(ReadBreakdown(breakdown, "EntryRiskScore")),
                Number(ReadBreakdown(breakdown, "InitialEntryRisk")),
                Number(ReadBreakdown(breakdown, "AdditionalEntryRisk")),
                Number(ReadBreakdown(breakdown, "EntryRiskContribution")),
                Number(ReadBreakdown(breakdown, "EntryRiskAdjustedScore")),
                Number(ReadBreakdown(breakdown, "UnifiedExitEvidence")),
                Number(ReadBreakdown(breakdown, "UnifiedExitContribution")),
                Number(ReadBreakdown(breakdown, "UnifiedExitAdjustedScore")),
                Number(ReadBreakdown(breakdown, "StableFinalScore")),
                Number(ReadBreakdown(breakdown, "CandidateStrength")),
                Number(ReadBreakdown(breakdown, "CurrentOfficialStrength")),
                Number(ReadBreakdown(breakdown, "CurrentSupportAuthority")),
                Number(ReadBreakdown(breakdown, "ContinuityFinalScore")),
                Bool(ReadBreakdownBool(breakdown, "StrengthContinuityPassed")),
                Number(ReadBreakdown(breakdown, "ProfitDrawdownContribution")),
                Number(ReadBreakdown(breakdown, "ProfitMarketBreakEvidence")),
                Number(ReadBreakdown(breakdown, "ProfitMarketAcceptance")),
                Number(ReadBreakdown(breakdown, "PersistentExitEvidence")),
                Number(ReadBreakdown(breakdown, "PersistentExitContribution")),
                Number(ReadBreakdown(breakdown, "PersistentCurrentApplicationAuthority")),
                Number(ReadBreakdown(breakdown, "RecentWorstOfficialFinal")),
                Number(ReadBreakdown(breakdown, "ResidualExitReEntryRisk")),
                Number(ReadBreakdown(breakdown, "ResidualExitPenalty")),
                Safe(ReadBreakdownString(breakdown, "ExitMemoryResetReason")),
                Integer(signal == null ? feature.SignalScore : signal.Score),
                Integer(ReadBreakdownInt(breakdown, "BuyThreshold")),
                Integer(ReadBreakdownInt(breakdown, "SellThreshold")),
                Number(ReadBreakdown(breakdown, "PivotThresholdPercent")),
                Number(ReadBreakdown(breakdown, "LastConfirmedPeak")),
                Number(ReadBreakdown(breakdown, "LastConfirmedTrough")),
                Number(ReadBreakdown(breakdown, "PriceStructureRangePercent")),
                Number(ReadBreakdown(breakdown, "StructureRangePosition")),
                Number(ReadBreakdown(breakdown, "PeakHeadroomPercent")),
                Number(ReadBreakdown(breakdown, "PeakChaseRisk")),
                Number(ReadBreakdown(breakdown, "EntryLocationAcceptance")),
                Number(ReadBreakdown(breakdown, "BreakoutAcceptance")),
                Number(ReadBreakdown(breakdown, "BreakoutDepth")),
                Number(ReadBreakdown(breakdown, "BreakoutHoldPersistence")),
                Number(ReadBreakdown(breakdown, "BreakoutClosedAlignment")),
                Number(ReadBreakdown(breakdown, "BreakoutRetestAcceptance")),
                priceRegime,
                Number(ReadBreakdown(breakdown, "ContractionBreakInstantSupport")),
                Number(ReadBreakdown(breakdown, "ContractionBreakPersistence")),
                Number(ReadBreakdown(breakdown, "ContractionBreakSupport")),
                Bool(ReadBreakdownBool(breakdown, "ContractionBreakReady")),
                Bool(ReadBreakdownBool(breakdown, "UptrendBreakoutReady")),
                Safe(flatBuyPermission),
                Safe(additionalBuyPermission),
                Safe(activeBuyPermission),
                Bool(feature.HasPosition),
                Integer(feature.HoldingQty),
                Number(feature.EntryPrice),
                Number(feature.NetProfitRate),
                Number(feature.NetMaxProfitRate),
                Number(feature.NetDrawdownRate),
                Number(feature.HoldSeconds),
                Bool(feature.HasPendingBuy),
                Bool(feature.HasPendingSell),
                Integer(feature.OpenOrderCount),
                LongInteger(feature.Cash),
                Number(feature.SymbolRemainingBudget),
                action,
                strategy,
                Bool(signal != null && signal.IsAdditionalBuyCandidate),
                Compact(decisionReason, 500),
                Compact(orderBlockReason, 500),
                Safe(signal == null ? feature.TiRexDirection : signal.TiRexDirection),
                Number(signal == null ? feature.TiRexConfidence : signal.TiRexConfidence),
                Number(signal == null ? feature.TiRexPredictedReturn : signal.TiRexPredictedReturn)
            };

            Enqueue(new CsvWriteRequest
            {
                Category = "FrameAnalysis",
                Code = code,
                EventTime = analysisTime,
                Mode = mode,
                Header = FrameHeader,
                Row = row
            });

            EnqueuePivotIfChanged(
                feature,
                breakdown,
                analysisTime,
                mode,
                code,
                signalId,
                candidateId,
                flatBuyPermission,
                additionalBuyPermission,
                decisionReason);
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

            string safeCode = NormalizeCode(code);
            if (string.IsNullOrWhiteSpace(safeCode))
                safeCode = signal == null ? "UNKNOWN" : NormalizeCode(signal.Code);

            string mode = isReplayMode ? "Replay" : "Real";
            DateTime signalDataTime =
                signal != null &&
                signal.SnapshotFeature != null
                    ? signal.SnapshotFeature.Time
                    : default(DateTime);
            DateTime safeEventTime = ResolveDataTime(
                safeCode,
                mode,
                eventTime,
                signalDataTime);
            SignalScoreBreakdown breakdown = signal == null ? null : signal.ScoreBreakdown;
            string reason = signal == null ? string.Empty : signal.Reason;
            string flatPermission = ExtractDecisionValue(reason, "FlatBuyPermission");
            string additionalPermission = ExtractDecisionValue(reason, "AdditionalBuyPermission");
            string activePermission = signal != null && signal.IsAdditionalBuyCandidate
                ? additionalPermission
                : flatPermission;

            string[] row = CreateOrderRow(
                safeEventTime,
                mode,
                safeCode,
                "OrderFlow",
                Safe(stage),
                Safe(actionName),
                signal == null ? string.Empty : Safe(signal.StrategyName),
                signal == null || signal.SnapshotFeature == null ? string.Empty : Safe(signal.SnapshotFeature.SnapshotId),
                signal == null ? 0.0 : signal.Price,
                currentPrice,
                bestAsk,
                bestBid,
                orderPrice,
                quantity,
                Safe(hogaType),
                signal == null ? 0 : signal.Score,
                breakdown == null ? string.Empty : Safe(breakdown.PriceRegime),
                activePermission,
                signal != null && signal.IsAdditionalBuyCandidate,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                0.0,
                0,
                0,
                0.0,
                0.0,
                string.Empty,
                Compact(message, 1000));

            Enqueue(new CsvWriteRequest
            {
                Category = "OrderTrade",
                Code = safeCode,
                EventTime = safeEventTime,
                Mode = mode,
                Header = OrderHeader,
                Row = row
            });
        }

        /// <summary>
        /// UI의 OrderHistory를 1초 단위로 스냅샷하여 실제 주문번호/체결수량/체결가/미체결 변화를 기록한다.
        /// OrderInfo 구현에 강하게 결합하지 않도록 reflection으로 공개 속성만 읽는다.
        /// </summary>
        public void EnqueueOrderSnapshots(IEnumerable orders, bool isReplayMode, DateTime fallbackTime)
        {
            if (orders == null)
                return;

            string mode = isReplayMode ? "Replay" : "Real";
            DateTime safeFallback = fallbackTime;

            foreach (object order in orders)
            {
                if (order == null)
                    continue;

                string code = NormalizeCode(GetString(order, "Code"));
                if (string.IsNullOrWhiteSpace(code))
                    continue;

                string orderNo = FirstNotEmpty(
                    GetString(order, "OrderNo"),
                    GetString(order, "OrderNumber"));
                string originalOrderNo = FirstNotEmpty(
                    GetString(order, "OriginalOrderNo"),
                    GetString(order, "OrgOrderNo"));
                string orderType = FirstNotEmpty(
                    GetString(order, "OrderType"),
                    GetString(order, "Side"));
                string status = GetString(order, "Status");
                string orderStatus = ConvertToInvariantString(GetPropertyValue(order, "OrderStatus"));
                string strategy = FirstNotEmpty(
                    GetString(order, "StrategyName"),
                    GetString(order, "Strategy"));
                string tradeGroupId = GetString(order, "TradeGroupId");

                int qty = FirstNonZeroInt(
                    GetInt(order, "Qty"),
                    GetInt(order, "Quantity"));
                int execQty = FirstNonZeroInt(
                    GetInt(order, "ExecQty"),
                    GetInt(order, "ExecutedQty"),
                    GetInt(order, "FilledQty"));
                int untradedQty = FirstNonZeroInt(
                    GetInt(order, "UntradedQty"),
                    GetInt(order, "RemainingQty"));
                double orderPrice = FirstPositive(
                    GetDouble(order, "Price"),
                    GetDouble(order, "OrderPrice"));
                double execPrice = FirstPositive(
                    GetDouble(order, "ExecPrice"),
                    GetDouble(order, "ExecutedPrice"),
                    GetDouble(order, "FilledPrice"));
                double fee = GetDouble(order, "Fee");
                double tax = GetDouble(order, "Tax");
                DateTime orderTime = FirstValidDateTime(
                    GetDateTime(order, "UpdatedAt"),
                    GetDateTime(order, "OrderTime"),
                    GetDateTime(order, "Time"),
                    GetDateTime(order, "CreatedAt"),
                    safeFallback);
                DateTime eventTime = ResolveDataTime(
                    code,
                    mode,
                    orderTime,
                    safeFallback);

                string orderKey = mode + "|" + code + "|" +
                                  (string.IsNullOrWhiteSpace(orderNo)
                                      ? orderType + "|" + orderPrice.ToString("0.####", CultureInfo.InvariantCulture)
                                      : orderNo);
                string fingerprint = string.Join("|", new[]
                {
                    orderType,
                    status,
                    orderStatus,
                    qty.ToString(CultureInfo.InvariantCulture),
                    execQty.ToString(CultureInfo.InvariantCulture),
                    untradedQty.ToString(CultureInfo.InvariantCulture),
                    orderPrice.ToString("0.####", CultureInfo.InvariantCulture),
                    execPrice.ToString("0.####", CultureInfo.InvariantCulture),
                    fee.ToString("0.####", CultureInfo.InvariantCulture),
                    tax.ToString("0.####", CultureInfo.InvariantCulture),
                    strategy,
                    tradeGroupId
                });

                string previousFingerprint;
                bool existed = _lastOrderFingerprintByKey.TryGetValue(orderKey, out previousFingerprint);
                if (existed && string.Equals(previousFingerprint, fingerprint, StringComparison.Ordinal))
                    continue;

                _lastOrderFingerprintByKey[orderKey] = fingerprint;
                string eventType = existed ? "OrderChanged" : "OrderAdded";
                if (execQty > 0)
                    eventType = "ExecutionUpdated";

                string message = "OrderType=" + Safe(orderType) +
                                 ", Status=" + Safe(status) +
                                 ", OrderStatus=" + Safe(orderStatus);

                string[] row = CreateOrderRow(
                    eventTime,
                    mode,
                    code,
                    "OrderHistory",
                    eventType,
                    orderType,
                    strategy,
                    string.Empty,
                    0.0,
                    0,
                    0,
                    0,
                    (int)Math.Round(orderPrice),
                    qty,
                    string.Empty,
                    0,
                    string.Empty,
                    string.Empty,
                    false,
                    orderNo,
                    originalOrderNo,
                    orderStatus,
                    status,
                    execPrice,
                    execQty,
                    untradedQty,
                    fee,
                    tax,
                    tradeGroupId,
                    message);

                Enqueue(new CsvWriteRequest
                {
                    Category = "OrderTrade",
                    Code = code,
                    EventTime = eventTime,
                    Mode = mode,
                    Header = OrderHeader,
                    Row = row
                });
            }
        }

        public bool Flush(TimeSpan timeout)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return false;

            var barrier = new ManualResetEventSlim(false);
            if (!Enqueue(new CsvWriteRequest { FlushSignal = barrier }))
            {
                barrier.Dispose();
                return false;
            }

            bool completed = barrier.Wait(timeout);
            barrier.Dispose();
            return completed;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
                AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
                _queue.CompleteAdding();
                if (!_writerThread.Join(TimeSpan.FromSeconds(3)))
                    _writerThread.Interrupt();
            }
            catch
            {
            }
            finally
            {
                CloseWriters();
                _queue.Dispose();
            }
        }

        private void EnqueuePivotIfChanged(
            FeatureSnapshot feature,
            SignalScoreBreakdown breakdown,
            DateTime analysisTime,
            string mode,
            string code,
            string signalId,
            string candidateId,
            string flatBuyPermission,
            string additionalBuyPermission,
            string decisionReason)
        {
            if (breakdown == null || !breakdown.IsReady)
                return;

            var current = new PriceRegimeSnapshot
            {
                Peak = breakdown.LastConfirmedPeak,
                Trough = breakdown.LastConfirmedTrough,
                EnginePatch = Safe(breakdown.EnginePatchVersion),
                Regime = Safe(breakdown.PriceRegime),
                PivotThresholdPercent = breakdown.PivotThresholdPercent,
                StructureRangePercent = breakdown.PriceStructureRangePercent,
                StructureIntegrity = breakdown.StructureIntegrity,
                StructureMaturity = breakdown.StructureMaturity,
                EffectiveStructureIntegrity = breakdown.EffectiveStructureIntegrity,
                TrendTransitionDirection = Safe(breakdown.TrendTransitionDirection),
                TrendTransitionProgress = breakdown.TrendTransitionProgress,
                TrendTransitionPersistence = breakdown.TrendTransitionPersistence,
                TrendTransitionAcceptance = breakdown.TrendTransitionAcceptance,
                ExistingTrendAuthority = breakdown.ExistingTrendAuthority,
                EmergingTrendAuthority = breakdown.EmergingTrendAuthority,
                MaturityRecovery = breakdown.MaturityRecovery,
                AuthorityTransfer = breakdown.AuthorityTransfer,
                UpTransitionMemory = breakdown.UpTransitionMemory,
                UpTransitionSupportSeconds = breakdown.UpTransitionSupportSeconds,
                UpTransitionMemoryProgress = breakdown.UpTransitionMemoryProgress,
                UpTransitionPermissionAuthority = breakdown.UpTransitionPermissionAuthority,
                UpTransitionCurrentCompatibility = breakdown.UpTransitionCurrentCompatibility,
                PeakRelation = Safe(breakdown.PeakRelation),
                TroughRelation = Safe(breakdown.TroughRelation),
                PressurePersistence = breakdown.PressurePersistence,
                PreviousFrameDirection = breakdown.PreviousFrameDirection,
                ClosedAlignment = breakdown.ClosedAlignment,
                StructureRangePosition = breakdown.StructureRangePosition,
                PeakHeadroomPercent = breakdown.PeakHeadroomPercent,
                PeakChaseRisk = breakdown.PeakChaseRisk,
                EntryLocationAcceptance = breakdown.EntryLocationAcceptance,
                BreakoutAcceptance = breakdown.BreakoutAcceptance,
                BreakoutDepth = breakdown.BreakoutDepth,
                BreakoutHoldPersistence = breakdown.BreakoutHoldPersistence,
                BreakoutClosedAlignment = breakdown.BreakoutClosedAlignment,
                BreakoutRetestAcceptance = breakdown.BreakoutRetestAcceptance,
                ContractionBreakInstantSupport = breakdown.ContractionBreakInstantSupport,
                ContractionBreakPersistence = breakdown.ContractionBreakPersistence,
                ContractionBreakSupport = breakdown.ContractionBreakSupport,
                ContractionBreakReady = breakdown.ContractionBreakReady,
                UptrendBreakoutReady = breakdown.UptrendBreakoutReady,
                FlatBuyPermission = Safe(flatBuyPermission),
                AdditionalBuyPermission = Safe(additionalBuyPermission)
            };

            string key = mode + "|" + code;
            PriceRegimeSnapshot previous;
            bool hasPrevious = _lastPriceRegimeByCode.TryGetValue(key, out previous);
            _lastPriceRegimeByCode[key] = current;

            var events = new List<string>();
            if (!hasPrevious)
                events.Add("Initialized");
            else
            {
                if (!NearlyEqual(previous.Peak, current.Peak))
                    events.Add("PeakConfirmed");
                if (!NearlyEqual(previous.Trough, current.Trough))
                    events.Add("TroughConfirmed");
                if (!string.Equals(previous.Regime, current.Regime, StringComparison.OrdinalIgnoreCase))
                    events.Add("RegimeChanged");
                bool transitionDirectionChanged = !string.Equals(
                    previous.TrendTransitionDirection,
                    current.TrendTransitionDirection,
                    StringComparison.OrdinalIgnoreCase);
                bool previousTransitionAccepted = previous.TrendTransitionAcceptance >= 0.20;
                bool currentTransitionAccepted = current.TrendTransitionAcceptance >= 0.20;
                if (transitionDirectionChanged ||
                    previousTransitionAccepted != currentTransitionAccepted)
                {
                    events.Add("TrendTransitionChanged");
                }
                bool previousUpMemoryReady = previous.UpTransitionPermissionAuthority >= 0.50;
                bool currentUpMemoryReady = current.UpTransitionPermissionAuthority >= 0.50;
                if (previousUpMemoryReady != currentUpMemoryReady)
                    events.Add("UpTransitionMemoryChanged");
                bool previousPeakChaseLimited = previous.EntryLocationAcceptance < 0.50;
                bool currentPeakChaseLimited = current.EntryLocationAcceptance < 0.50;
                if (previousPeakChaseLimited != currentPeakChaseLimited)
                    events.Add("EntryLocationChanged");

                if (previous.ContractionBreakReady != current.ContractionBreakReady ||
                    previous.UptrendBreakoutReady != current.UptrendBreakoutReady ||
                    !string.Equals(previous.FlatBuyPermission, current.FlatBuyPermission, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(previous.AdditionalBuyPermission, current.AdditionalBuyPermission, StringComparison.OrdinalIgnoreCase))
                {
                    events.Add("PermissionChanged");
                }
            }

            if (events.Count == 0)
                return;

            string[] row =
            {
                FormatDateTime(ResolveRecordedAt(code, mode, analysisTime)),
                FormatDateTime(analysisTime),
                mode,
                code,
                Safe(feature.SnapshotId),
                Safe(signalId),
                Safe(candidateId),
                Safe(current.EnginePatch),
                string.Join("+", events.ToArray()),
                Number(feature.Price),
                Number(hasPrevious ? previous.Peak : 0.0),
                Number(current.Peak),
                Number(hasPrevious ? previous.Trough : 0.0),
                Number(current.Trough),
                hasPrevious ? Safe(previous.Regime) : string.Empty,
                Safe(current.Regime),
                Number(current.PivotThresholdPercent),
                Number(current.StructureRangePercent),
                Number(current.StructureIntegrity),
                Number(current.StructureMaturity),
                Number(current.EffectiveStructureIntegrity),
                Safe(current.TrendTransitionDirection),
                Number(current.TrendTransitionProgress),
                Number(current.TrendTransitionPersistence),
                Number(current.TrendTransitionAcceptance),
                Number(current.ExistingTrendAuthority),
                Number(current.EmergingTrendAuthority),
                Number(current.MaturityRecovery),
                Number(current.AuthorityTransfer),
                Number(current.UpTransitionMemory),
                Number(current.UpTransitionSupportSeconds),
                Number(current.UpTransitionMemoryProgress),
                Number(current.UpTransitionPermissionAuthority),
                Number(current.UpTransitionCurrentCompatibility),
                Safe(current.PeakRelation),
                Safe(current.TroughRelation),
                Number(current.PressurePersistence),
                Number(current.PreviousFrameDirection),
                Number(current.ClosedAlignment),
                Number(current.StructureRangePosition),
                Number(current.PeakHeadroomPercent),
                Number(current.PeakChaseRisk),
                Number(current.EntryLocationAcceptance),
                Number(current.BreakoutAcceptance),
                Number(current.BreakoutDepth),
                Number(current.BreakoutHoldPersistence),
                Number(current.BreakoutClosedAlignment),
                Number(current.BreakoutRetestAcceptance),
                Number(current.ContractionBreakInstantSupport),
                Number(current.ContractionBreakPersistence),
                Number(current.ContractionBreakSupport),
                Bool(current.ContractionBreakReady),
                Bool(current.UptrendBreakoutReady),
                Safe(current.FlatBuyPermission),
                Safe(current.AdditionalBuyPermission),
                Compact(decisionReason, 500)
            };

            Enqueue(new CsvWriteRequest
            {
                Category = "PivotRegime",
                Code = code,
                EventTime = analysisTime,
                Mode = mode,
                Header = PivotHeader,
                Row = row
            });
        }

        private bool Enqueue(CsvWriteRequest request)
        {
            if (request == null || Volatile.Read(ref _disposed) != 0 || _queue.IsAddingCompleted)
                return false;

            if (_queue.TryAdd(request))
                return true;

            Interlocked.Increment(ref _droppedRowCount);
            return false;
        }

        private void WriterLoop()
        {
            var batch = new List<CsvWriteRequest>(BatchSize);

            try
            {
                while (!_queue.IsCompleted)
                {
                    CsvWriteRequest first;
                    if (!_queue.TryTake(out first, 500))
                    {
                        FlushWriters();
                        continue;
                    }

                    batch.Clear();
                    batch.Add(first);

                    CsvWriteRequest item;
                    while (batch.Count < BatchSize && _queue.TryTake(out item))
                        batch.Add(item);

                    for (int i = 0; i < batch.Count; i++)
                        ProcessRequest(batch[i]);

                    FlushWriters();
                }
            }
            catch (ThreadInterruptedException)
            {
            }
            catch
            {
            }
            finally
            {
                CsvWriteRequest remaining;
                while (_queue.TryTake(out remaining))
                {
                    try
                    {
                        ProcessRequest(remaining);
                    }
                    catch
                    {
                    }
                }

                FlushWriters();
                CloseWriters();
            }
        }

        private void ProcessRequest(CsvWriteRequest request)
        {
            if (request == null)
                return;

            if (request.FlushSignal != null)
            {
                FlushWriters();
                request.FlushSignal.Set();
                return;
            }

            if (request.BeginSession)
            {
                RotateSession(request.Code, request.EventTime, request.Mode);
                return;
            }

            if (string.IsNullOrWhiteSpace(request.Category) ||
                string.IsNullOrWhiteSpace(request.Code) ||
                request.Row == null)
            {
                return;
            }

            try
            {
                string filePath = BuildFilePath(
                    request.Category,
                    request.Code,
                    request.EventTime,
                    request.Mode);
                StreamWriter writer = GetWriter(filePath, request.Header);
                if (writer != null)
                    writer.WriteLine(ToCsvLine(request.Row));
            }
            catch
            {
                Interlocked.Increment(ref _droppedRowCount);
            }
        }

        private StreamWriter GetWriter(string path, string[] header)
        {
            StreamWriter writer;
            if (_writers.TryGetValue(path, out writer))
                return writer;

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            bool writeHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            writer = new StreamWriter(stream, new UTF8Encoding(true), 64 * 1024);
            writer.AutoFlush = false;

            if (writeHeader && header != null)
                writer.WriteLine(ToCsvLine(header));

            _writers[path] = writer;
            return writer;
        }

        private void FlushWriters()
        {
            foreach (StreamWriter writer in _writers.Values)
            {
                try
                {
                    writer.Flush();
                }
                catch
                {
                }
            }
        }

        private void CloseWriters()
        {
            foreach (StreamWriter writer in _writers.Values)
            {
                try
                {
                    writer.Flush();
                    writer.Dispose();
                }
                catch
                {
                }
            }

            _writers.Clear();
        }

        private void RotateSession(string code, DateTime eventTime, string mode)
        {
            DateTime date = ResolveSessionDataDate(code, mode, eventTime);
            if (date == default(DateTime) || date == DateTime.MinValue)
                return;

            string safeMode = SanitizeFileName(string.IsNullOrWhiteSpace(mode) ? "Unknown" : mode);
            string safeCode = SanitizeFileName(string.IsNullOrWhiteSpace(code) ? "UNKNOWN" : code);
            string sessionKey = safeCode + "|" + date.ToString("yyyyMMdd") + "|" + safeMode;

            FlushWriters();
            CloseWriters();

            lock (_sessionFilePathLock)
                _sessionSuffixByKey.Remove(sessionKey);
        }

        private string BuildFilePath(string category, string code, DateTime eventTime, string mode)
        {
            DateTime date = ResolveSessionDataDate(code, mode, eventTime);
            if (date == default(DateTime) || date == DateTime.MinValue)
                throw new InvalidOperationException("CSV 데이터 일자를 확인할 수 없습니다.");

            string safeMode = SanitizeFileName(string.IsNullOrWhiteSpace(mode) ? "Unknown" : mode);
            string safeCode = SanitizeFileName(string.IsNullOrWhiteSpace(code) ? "UNKNOWN" : code);
            string directory = Path.Combine(_rootDirectory, safeCode);
            int suffix = ResolveSessionSuffix(directory, safeCode, date, safeMode);
            string suffixText = suffix <= 0 ? string.Empty : "_" + suffix.ToString(CultureInfo.InvariantCulture);
            string fileName = category + "_" + safeCode + "_" + date.ToString("yyyyMMdd") + "_" + safeMode + suffixText + ".csv";
            return Path.Combine(directory, fileName);
        }

        private int ResolveSessionSuffix(string directory, string safeCode, DateTime date, string safeMode)
        {
            string sessionKey = safeCode + "|" + date.ToString("yyyyMMdd") + "|" + safeMode;

            lock (_sessionFilePathLock)
            {
                int resolved;
                if (_sessionSuffixByKey.TryGetValue(sessionKey, out resolved))
                    return resolved;

                Directory.CreateDirectory(directory);

                for (int suffix = 0; suffix < int.MaxValue; suffix++)
                {
                    string suffixText = suffix <= 0
                        ? string.Empty
                        : "_" + suffix.ToString(CultureInfo.InvariantCulture);

                    string framePath = Path.Combine(
                        directory,
                        "FrameAnalysis_" + safeCode + "_" + date.ToString("yyyyMMdd") + "_" + safeMode + suffixText + ".csv");
                    string pivotPath = Path.Combine(
                        directory,
                        "PivotRegime_" + safeCode + "_" + date.ToString("yyyyMMdd") + "_" + safeMode + suffixText + ".csv");
                    string orderPath = Path.Combine(
                        directory,
                        "OrderTrade_" + safeCode + "_" + date.ToString("yyyyMMdd") + "_" + safeMode + suffixText + ".csv");

                    if (!File.Exists(framePath) &&
                        !File.Exists(pivotPath) &&
                        !File.Exists(orderPath))
                    {
                        _sessionSuffixByKey[sessionKey] = suffix;
                        return suffix;
                    }
                }

                throw new IOException("CSV 중복 파일 번호를 결정하지 못했습니다: " + sessionKey);
            }
        }

        private static string BuildSessionDataDateKey(string code, string mode)
        {
            return NormalizeCode(code) + "|" +
                   (string.IsNullOrWhiteSpace(mode) ? "Unknown" : mode.Trim());
        }

        private static bool IsValidDataTime(DateTime value)
        {
            return value != default(DateTime) && value != DateTime.MinValue;
        }

        private DateTime ResolveSessionDataDate(string code, string mode, DateTime eventTime)
        {
            DateTime dataDate;
            if (_sessionDataDateByKey.TryGetValue(
                    BuildSessionDataDateKey(code, mode),
                    out dataDate) &&
                IsValidDataTime(dataDate))
            {
                return dataDate.Date;
            }

            return IsValidDataTime(eventTime)
                ? eventTime.Date
                : default(DateTime);
        }

        private DateTime ResolveDataTime(
            string code,
            string mode,
            DateTime preferredTime,
            DateTime fallbackTime)
        {
            DateTime sourceTime = IsValidDataTime(preferredTime)
                ? preferredTime
                : IsValidDataTime(fallbackTime)
                    ? fallbackTime
                    : default(DateTime);

            DateTime dataDate = ResolveSessionDataDate(code, mode, sourceTime);
            if (!IsValidDataTime(dataDate))
                return sourceTime;

            return dataDate.Date +
                   (IsValidDataTime(sourceTime)
                       ? sourceTime.TimeOfDay
                       : TimeSpan.Zero);
        }

        private DateTime ResolveRecordedAt(
            string code,
            string mode,
            DateTime dataTime)
        {
            DateTime dataDate = ResolveSessionDataDate(code, mode, dataTime);
            if (!IsValidDataTime(dataDate))
                return dataTime;

            // 기록 시각의 시/분/초는 유지하되 일자는 Replay/Real 데이터 일자로 통일한다.
            return dataDate.Date + DateTime.Now.TimeOfDay;
        }

        private string[] CreateOrderRow(
            DateTime eventTime,
            string mode,
            string code,
            string source,
            string eventType,
            string side,
            string strategy,
            string snapshotId,
            double signalPrice,
            int currentPrice,
            int bestAsk,
            int bestBid,
            int orderPrice,
            int quantity,
            string hogaType,
            int finalScore,
            string priceRegime,
            string buyPermission,
            bool isAdditionalBuy,
            string orderNo,
            string originalOrderNo,
            string orderStatus,
            string status,
            double execPrice,
            int execQty,
            int untradedQty,
            double fee,
            double tax,
            string tradeGroupId,
            string message)
        {
            return new[]
            {
                FormatDateTime(ResolveRecordedAt(code, mode, eventTime)),
                FormatDateTime(eventTime),
                Safe(mode),
                Safe(code),
                Safe(source),
                Safe(eventType),
                Safe(side),
                Safe(strategy),
                Safe(snapshotId),
                Number(signalPrice),
                Integer(currentPrice),
                Integer(bestAsk),
                Integer(bestBid),
                Integer(orderPrice),
                Integer(quantity),
                Safe(hogaType),
                Integer(finalScore),
                Safe(priceRegime),
                Safe(buyPermission),
                Bool(isAdditionalBuy),
                Safe(orderNo),
                Safe(originalOrderNo),
                Safe(orderStatus),
                Safe(status),
                Number(execPrice),
                Integer(execQty),
                Integer(untradedQty),
                Number(fee),
                Number(tax),
                Safe(tradeGroupId),
                Compact(message, 1000)
            };
        }

        private static bool ShouldCaptureOrderStage(string stage)
        {
            if (string.IsNullOrWhiteSpace(stage))
                return false;

            if (stage.EndsWith("_BLOCK", StringComparison.OrdinalIgnoreCase) ||
                stage.EndsWith("_FAIL", StringComparison.OrdinalIgnoreCase))
                return true;

            switch (stage.ToUpperInvariant())
            {
                case "REQUEST_RECEIVED":
                case "HARDSTOP_PENDING_SELL":
                case "INVALID_REQUEST":
                case "SEND_CALL":
                case "SEND_OK":
                case "EXCEPTION":
                    return true;
                default:
                    return false;
            }
        }

        private static string ResolveOrderBlockReason(string reason)
        {
            string guard = ExtractDecisionValue(reason, "Guard");
            string state = ExtractDecisionValue(reason, "State");
            string decisionReason = ExtractDecisionValue(reason, "Reason");

            if (!string.IsNullOrWhiteSpace(guard))
                return guard;

            if (!string.IsNullOrWhiteSpace(state) &&
                !string.Equals(state, "None", StringComparison.OrdinalIgnoreCase))
                return state;

            if (!string.IsNullOrWhiteSpace(decisionReason) &&
                (decisionReason.IndexOf("Block", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 decisionReason.IndexOf("Lock", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 decisionReason.IndexOf("Disabled", StringComparison.OrdinalIgnoreCase) >= 0))
                return decisionReason;

            return string.Empty;
        }

        private static string ExtractDecisionValue(string reason, string key)
        {
            if (string.IsNullOrWhiteSpace(reason) || string.IsNullOrWhiteSpace(key))
                return string.Empty;

            string token = key + "=";
            int start = reason.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return string.Empty;

            start += token.Length;
            int endComma = reason.IndexOf(',', start);
            int endPipe = reason.IndexOf('|', start);
            int end = -1;

            if (endComma >= 0 && endPipe >= 0)
                end = Math.Min(endComma, endPipe);
            else if (endComma >= 0)
                end = endComma;
            else if (endPipe >= 0)
                end = endPipe;

            string value = end < 0
                ? reason.Substring(start)
                : reason.Substring(start, end - start);
            return value.Trim();
        }

        private static double ReadBreakdown(SignalScoreBreakdown breakdown, string propertyName)
        {
            if (breakdown == null || string.IsNullOrWhiteSpace(propertyName))
                return 0.0;

            PropertyInfo property = typeof(SignalScoreBreakdown).GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property == null)
                return 0.0;

            object value = property.GetValue(breakdown, null);
            return ConvertToDouble(value);
        }

        private static int ReadBreakdownInt(SignalScoreBreakdown breakdown, string propertyName)
        {
            return (int)Math.Round(ReadBreakdown(breakdown, propertyName));
        }

        private static bool ReadBreakdownBool(SignalScoreBreakdown breakdown, string propertyName)
        {
            if (breakdown == null || string.IsNullOrWhiteSpace(propertyName))
                return false;

            PropertyInfo property = typeof(SignalScoreBreakdown).GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property == null)
                return false;

            object value = property.GetValue(breakdown, null);
            if (value is bool)
                return (bool)value;

            bool parsed;
            return bool.TryParse(ConvertToInvariantString(value), out parsed) && parsed;
        }

        private static string ReadBreakdownString(SignalScoreBreakdown breakdown, string propertyName)
        {
            if (breakdown == null || string.IsNullOrWhiteSpace(propertyName))
                return string.Empty;

            PropertyInfo property = typeof(SignalScoreBreakdown).GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property == null)
                return string.Empty;

            object value = property.GetValue(breakdown, null);
            return ConvertToInvariantString(value);
        }

        private static object GetPropertyValue(object source, string propertyName)
        {
            if (source == null || string.IsNullOrWhiteSpace(propertyName))
                return null;

            try
            {
                PropertyInfo property = source.GetType().GetProperty(
                    propertyName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                return property == null ? null : property.GetValue(source, null);
            }
            catch
            {
                return null;
            }
        }

        private static string GetString(object source, string propertyName)
        {
            return ConvertToInvariantString(GetPropertyValue(source, propertyName));
        }

        private static int GetInt(object source, string propertyName)
        {
            object value = GetPropertyValue(source, propertyName);
            if (value == null)
                return 0;

            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                int parsed;
                return int.TryParse(ConvertToInvariantString(value), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed)
                    ? parsed
                    : 0;
            }
        }

        private static double GetDouble(object source, string propertyName)
        {
            return ConvertToDouble(GetPropertyValue(source, propertyName));
        }

        private static DateTime GetDateTime(object source, string propertyName)
        {
            object value = GetPropertyValue(source, propertyName);
            if (value == null)
                return default(DateTime);

            if (value is DateTime)
                return (DateTime)value;

            DateTime parsed;
            return DateTime.TryParse(
                ConvertToInvariantString(value),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out parsed)
                ? parsed
                : default(DateTime);
        }

        private static double ConvertToDouble(object value)
        {
            if (value == null)
                return 0.0;

            try
            {
                double result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return double.IsNaN(result) || double.IsInfinity(result) ? 0.0 : result;
            }
            catch
            {
                double parsed;
                return double.TryParse(
                    ConvertToInvariantString(value),
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out parsed)
                    ? parsed
                    : 0.0;
            }
        }

        private static string ConvertToInvariantString(object value)
        {
            if (value == null)
                return string.Empty;

            var formattable = value as IFormattable;
            return formattable == null
                ? value.ToString()
                : formattable.ToString(null, CultureInfo.InvariantCulture);
        }

        private static string ToCsvLine(string[] values)
        {
            if (values == null || values.Length == 0)
                return string.Empty;

            var builder = new StringBuilder(values.Length * 12);
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0)
                    builder.Append(',');

                string value = values[i] ?? string.Empty;
                bool quote = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
                if (!quote)
                {
                    builder.Append(value);
                    continue;
                }

                builder.Append('"');
                builder.Append(value.Replace("\"", "\"\""));
                builder.Append('"');
            }

            return builder.ToString();
        }

        private static string FormatDateTime(DateTime value)
        {
            return value == default(DateTime)
                ? string.Empty
                : value.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }

        private static string Number(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return string.Empty;

            return value.ToString("0.########", CultureInfo.InvariantCulture);
        }

        private static string Integer(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string LongInteger(long value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string Bool(bool value)
        {
            return value ? "Y" : "N";
        }

        private static string Safe(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private static string Compact(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string compact = value.Replace("\r", " ").Replace("\n", " ").Trim();
            return compact.Length <= maxLength
                ? compact
                : compact.Substring(0, maxLength);
        }

        private static string NormalizeCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return string.Empty;

            string result = code.Trim();
            if (result.Length == 7 && (result[0] == 'A' || result[0] == 'a'))
                result = result.Substring(1);
            return result;
        }

        private static string SanitizeFileName(string value)
        {
            string safe = string.IsNullOrWhiteSpace(value) ? "UNKNOWN" : value.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars())
                safe = safe.Replace(invalid, '_');
            return safe;
        }

        private static bool NearlyEqual(double left, double right)
        {
            return Math.Abs(left - right) < 0.000001;
        }

        private static string FirstNotEmpty(params string[] values)
        {
            if (values == null)
                return string.Empty;

            for (int i = 0; i < values.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(values[i]))
                    return values[i].Trim();
            }

            return string.Empty;
        }

        private static int FirstNonZeroInt(params int[] values)
        {
            if (values == null)
                return 0;

            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] != 0)
                    return values[i];
            }

            return 0;
        }

        private static double FirstPositive(params double[] values)
        {
            if (values == null)
                return 0.0;

            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] > 0.0)
                    return values[i];
            }

            return 0.0;
        }

        private static DateTime FirstValidDateTime(params DateTime[] values)
        {
            if (values == null)
                return default(DateTime);

            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] != default(DateTime) &&
                    values[i] != DateTime.MinValue)
                {
                    return values[i];
                }
            }

            return default(DateTime);
        }

        private void OnProcessExit(object sender, EventArgs e)
        {
            try
            {
                Flush(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                Flush(TimeSpan.FromSeconds(1));
            }
            catch
            {
            }
        }

        private sealed class CsvWriteRequest
        {
            public bool BeginSession;
            public string Category;
            public string Code;
            public DateTime EventTime;
            public string Mode;
            public string[] Header;
            public string[] Row;
            public ManualResetEventSlim FlushSignal;
        }

        private sealed class PriceRegimeSnapshot
        {
            public double Peak;
            public double Trough;
            public string EnginePatch;
            public string Regime;
            public double PivotThresholdPercent;
            public double StructureRangePercent;
            public double StructureIntegrity;
            public double StructureMaturity;
            public double EffectiveStructureIntegrity;
            public string TrendTransitionDirection;
            public double TrendTransitionProgress;
            public double TrendTransitionPersistence;
            public double TrendTransitionAcceptance;
            public double ExistingTrendAuthority;
            public double EmergingTrendAuthority;
            public double MaturityRecovery;
            public double AuthorityTransfer;
            public double UpTransitionMemory;
            public double UpTransitionSupportSeconds;
            public double UpTransitionMemoryProgress;
            public double UpTransitionPermissionAuthority;
            public double UpTransitionCurrentCompatibility;
            public string PeakRelation;
            public string TroughRelation;
            public double PressurePersistence;
            public double PreviousFrameDirection;
            public double ClosedAlignment;
            public double StructureRangePosition;
            public double PeakHeadroomPercent;
            public double PeakChaseRisk;
            public double EntryLocationAcceptance;
            public double BreakoutAcceptance;
            public double BreakoutDepth;
            public double BreakoutHoldPersistence;
            public double BreakoutClosedAlignment;
            public double BreakoutRetestAcceptance;
            public double ContractionBreakInstantSupport;
            public double ContractionBreakPersistence;
            public double ContractionBreakSupport;
            public bool ContractionBreakReady;
            public bool UptrendBreakoutReady;
            public string FlatBuyPermission;
            public string AdditionalBuyPermission;
        }
    }
}
