using System.ComponentModel;
using System;
using MoneyCreator.AutoTrading;
using MoneyCreator.Models;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;

using AxKHOpenAPILib;
using MoneyCreator.Biz;
using MoneyCreator.Service;
using MoneyCreator.View;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using MoneyCreator.DataBase;
using HarfBuzzSharp;

namespace MoneyCreator.ViewModels
{
    /// <summary>
    /// 자동매매 로직 중재 ViewModel.
    /// 실제 자동매매 실행 경로는 기존 MainWindowViewModel 내부 구현을 유지하고,
    /// 바인딩/상태만 이 ViewModel 경로로 점진 이관할 수 있게 만든다.
    /// </summary>
    public sealed class AutoTradingViewModel : ViewModelBase, IDisposable
    {
        private readonly MainWindowViewModel _owner;
        protected MainWindowViewModel Owner => _owner;
        private bool IsReplayMode => Owner != null && Owner.IsReplayMode;

        public AutoTradingViewModel(MainWindowViewModel owner)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));

            if (_owner is INotifyPropertyChanged notify)
                notify.PropertyChanged += Owner_PropertyChanged;
        }

        private void Owner_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.PropertyName))
            {
                OnPropertyChanged(string.Empty);
                return;
            }

            if (ShouldForwardOwnerPropertyChanged(e.PropertyName))
                OnPropertyChanged(e.PropertyName);
        }

        private bool ShouldForwardOwnerPropertyChanged(string propertyName)
        {
            return true;
        }

        protected void Refresh(string propertyName)
        {
            if (!string.IsNullOrWhiteSpace(propertyName))
                OnPropertyChanged(propertyName);
        }

        public void Dispose()
        {
            try
            {
                Owner?.FlushAutoTradeMlAnalysisQueues("AutoTradingViewModel.Dispose");
                AutoTradeCsvWriter.Shared.Flush(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

            if (_owner is INotifyPropertyChanged notify)
                notify.PropertyChanged -= Owner_PropertyChanged;
        }

        public ObservableCollection<string> AutoTradeAnalysisLogs => Owner.AutoTradeAnalysisLogs;
        public System.Collections.Generic.IReadOnlyList<string> AutoTradeLogModeOptions => Owner.AutoTradeLogModeOptions;

        public ICommand AutoTradingToggleCommand => Owner.AutoTradingToggleCommand;

        public bool IsAutoTradingEnabled
        {
            get => Owner.IsAutoTradingEnabled;
            set
            {
                Owner.IsAutoTradingEnabled = value;
                Refresh(nameof(IsAutoTradingEnabled));
            }
        }

        public bool IsAutoTradingSmallScale
        {
            get => Owner.IsAutoTradingSmallScale;
            set
            {
                Owner.IsAutoTradingSmallScale = value;
                Refresh(nameof(IsAutoTradingSmallScale));
            }
        }

        public string SelectedAutoTradeLogMode
        {
            get => Owner.SelectedAutoTradeLogMode;
            set
            {
                Owner.SelectedAutoTradeLogMode = value;
                Refresh(nameof(SelectedAutoTradeLogMode));
            }
        }

        public bool IsAutoTradeLogTradeOnly
        {
            get => Owner.IsAutoTradeLogTradeOnly;
            set
            {
                Owner.IsAutoTradeLogTradeOnly = value;
                Refresh(nameof(IsAutoTradeLogTradeOnly));
            }
        }

        public bool IsAutoTradeLogAll
        {
            get => Owner.IsAutoTradeLogAll;
            set
            {
                Owner.IsAutoTradeLogAll = value;
                Refresh(nameof(IsAutoTradeLogAll));
            }
        }

        public string TiRexPredictionText
        {
            get => Owner.TiRexPredictionText;
            set
            {
                Owner.TiRexPredictionText = value;
                Refresh(nameof(TiRexPredictionText));
            }
        }

        public string TiRexPredictionDetail
        {
            get => Owner.TiRexPredictionDetail;
            set
            {
                Owner.TiRexPredictionDetail = value;
                Refresh(nameof(TiRexPredictionDetail));
            }
        }

        public string TradePerformanceStatusText => Owner.TradePerformanceStatusText;
        public string PerformanceLastUpdatedText => Owner.PerformanceLastUpdatedText;
        public int TodayTradeCount => Owner.TodayTradeCount;
        public int TodayWinCount => Owner.TodayWinCount;
        public int TodayLossCount => Owner.TodayLossCount;
        public double TodayWinRate => Owner.TodayWinRate;
        public double TodayAvgProfitRate => Owner.TodayAvgProfitRate;
        public double TodayNetProfit => Owner.TodayNetProfit;
        public double TodayGrossProfit => Owner.TodayGrossProfit;
        public double TodayGrossLoss => Owner.TodayGrossLoss;
        public double TodayFeeTax => Owner.TodayFeeTax;
        public double TodayBestTradeRate => Owner.TodayBestTradeRate;
        public double TodayWorstTradeRate => Owner.TodayWorstTradeRate;
        public int TotalTradeCount => Owner.TotalTradeCount;
        public double TotalNetProfit => Owner.TotalNetProfit;
        public double TotalWinRate => Owner.TotalWinRate;
        public double TotalAvgProfitRate => Owner.TotalAvgProfitRate;

        public Brush TradeCardAccentBrush => Owner.TradeCardAccentBrush;
        public Brush TradeCardActionBrush => Owner.TradeCardActionBrush;
        public Brush TradeCardProfitBrush => Owner.TradeCardProfitBrush;
        public string TradeCardUpdatedAt => Owner.TradeCardUpdatedAt;
        public string TradeCardTitle => Owner.TradeCardTitle;
        public string TradeCardAction => Owner.TradeCardAction;
        public string TradeCardCode => Owner.TradeCardCode;
        public string TradeCardPrice => Owner.TradeCardPrice;
        public string TradeCardProfitRate => Owner.TradeCardProfitRate;
        public string TradeCardScore => Owner.TradeCardScore;
        public string TradeCardHoldingQty => Owner.TradeCardHoldingQty;
        public string TradeCardStrength => Owner.TradeCardStrength;
        public string TradeCardVolumeRatio => Owner.TradeCardVolumeRatio;
        public string TradeCardOneMinuteChange => Owner.TradeCardOneMinuteChange;
        public string TradeCardThreeMinuteChange => Owner.TradeCardThreeMinuteChange;
        public string TradeCardBidAskRatio => Owner.TradeCardBidAskRatio;
        public string TradeCardReason => Owner.TradeCardReason;
        public string TradeCardPhase => Owner.TradeCardPhase;
        public string TradeCardMarketPhase => Owner.TradeCardMarketPhase;
        public string TradeCardTradeMode => Owner.TradeCardTradeMode;
        public string TradeCardStrategy => Owner.TradeCardStrategy;

        public StockContext Context => Owner.Context;
        public ObservableCollection<HoldingInfo> AccountHoldings => Owner.AccountHoldings;
        public ObservableCollection<OrderInfo> OrderHistory => Owner.OrderHistory;

        public void AddAutoTradeAnalysisLog(string message)
        {
            Owner.AddAutoTradeAnalysisLog(message);
        }

        public void AddLog(string message)
        {
            Owner.AddLog(message);
        }
    }
}

namespace MoneyCreator.ViewModels
{
    public partial class MainWindowViewModel
    {
        // Source: MainWindowViewModel.AutoTrading.cs
        // ============================================================
        private AutoTradeCoreWorker _autoTradeController;

        // 자동매매 분석 가능 상태
        // - 종목 전환/과거분봉 로딩 중에는 자동매매가 분봉 리스트를 읽지 않게 막는다.
        // - 자동매매 ON 상태에서 새창/새종목 검색 시 차트/분봉 데이터가 교체되는 중 분석이 먼저 도는 문제 방지.
        private volatile bool _isAutoTradeDataReady = false;
        private string _autoTradeReadyCode = string.Empty;
        private const int MoneyCreatorBuySignalOpportunitySeconds = 10;
        private const int MoneyCreatorStrongOpportunityScoreMargin = 15;
        private const int MoneyCreatorStrongOpportunityMinScore = 85;
        private const int AutoTradeBuyRepriceDelayMilliseconds = 3000;
        private const int AutoTradeBuyRepriceMaxAttemptsPerOrder = 1;
        private const int AutoTradeSellFirstCheckDelayMilliseconds = 4000;
        private const int AutoTradeUrgentSellFirstCheckDelayMilliseconds = 2000;
        private const int AutoTradeSellReplaceCooldownMilliseconds = 5000;
        private const int AutoTradeUrgentSellReplaceCooldownMilliseconds = 2500;
        private const int AutoTradeNormalSellReplaceMaxAttemptsPerOrder = 1;
        private const int AutoTradeUrgentSellReplaceMaxAttemptsPerOrder = 2;

        private readonly DispatcherTimer _moneyCreatorPortfolioUsageRefreshTimer = new DispatcherTimer();
        private readonly DispatcherTimer _moneyCreatorPortfolioFallbackRebuildTimer = new DispatcherTimer();

        private readonly ConcurrentDictionary<string, bool> _autoCancelWatchMap =
            new ConcurrentDictionary<string, bool>();
        private readonly ConcurrentDictionary<string, int> _autoTradeBuyRepriceAttemptByOrderNo =
            new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> _autoTradeSellReplaceAttemptByOrderNo =
            new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _lastAutoTradeSellReplaceAtByCode =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> _autoTradeSellReplaceDeferMap =
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, AutoTradeOrderMetadata> _autoTradeOrderMetadataByRouteKey =
            new ConcurrentDictionary<string, AutoTradeOrderMetadata>();

        // CSV 주문흐름 기록용 현재 요청. ExecuteAutoTradeOrderRequest는 UI 주문 스레드에서 직렬 실행된다.
        private OrderRequest _activeAutoTradeCsvOrderRequest;

        private readonly ConcurrentDictionary<string, bool> _outcomeLabelWatchMap =
            new ConcurrentDictionary<string, bool>();

        private readonly ConcurrentDictionary<string, FeatureSnapshotRecord> _pendingFeatureSnapshotOutcomeMap =
            new ConcurrentDictionary<string, FeatureSnapshotRecord>();

        private readonly ConcurrentDictionary<string, DecisionCandidateRecord> _pendingDecisionCandidateOutcomeMap =
            new ConcurrentDictionary<string, DecisionCandidateRecord>();

        private readonly DispatcherTimer _decisionCandidateOutcomePumpTimer = new DispatcherTimer();
        private const int DecisionCandidateOutcomePumpIntervalSeconds = 5;
        private const int OutcomeLabelRetryGraceSeconds = 60;

        private DateTime _lastDecisionCandidateOutcomePumpAt = DateTime.MinValue;

        private readonly ConcurrentDictionary<string, DateTime> _lastFeatureSnapshotSavedAtByCode =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, DateTime> _lastAutoTradeMinuteCandleSyncAtByCode =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private readonly CancellationTokenSource _outcomeLabelCts = new CancellationTokenSource();

        private sealed class AutoTradeOrderMetadata
        {
            public string StrategyName { get; set; }
            public string TiRexDirection { get; set; }
            public double TiRexConfidence { get; set; }
            public double TiRexPredictedReturn { get; set; }
            public DateTime CreatedAt { get; set; }
        }

        // Kiwoom domestic online stock trading cost baseline.
        // Internal rates are decimal ratios, not percentage points.
        // 0.00015 = 0.015%, 0.00200 = 0.20%, round-trip cost ~= 0.23%.
        private const double MoneyCreatorKiwoomDomesticOnlineFeeRate = 0.00015;
        private const double MoneyCreatorKiwoomDomesticSellTaxRate = 0.00200;
        private const double MoneyCreatorKiwoomDomesticRoundTripCostPercent =
            (MoneyCreatorKiwoomDomesticOnlineFeeRate +
             MoneyCreatorKiwoomDomesticOnlineFeeRate +
             MoneyCreatorKiwoomDomesticSellTaxRate) * 100.0;

        private readonly AutoTradeSettings _autoTradeSettings = new AutoTradeSettings
        {
            UseMlNet = true,
            UseFullCashSizingForTest = false,
            UsePartialTakeProfitSell = false,
            UsePortfolioCashSizing = true,
            UseDynamicMarketThresholds = true,

            MaxPortfolioCashUseRatio = 0.80,
            MaxCashRatioPerSymbol = 0.25,
            MaxScorePositionMultiplier = 1.5,
            MinOrderAmount = 30000,

            Enabled = false,
            UseAi = false,
            AllowBuy = true,
            AllowSell = true,

            AllowAdditionalBuy = true,
            MaxAdditionalBuyCountPerCode = 2,
            AdditionalBuyMinProfitRate = -1.5,
            AdditionalBuyMaxProfitRate = 2.0,
            AdditionalBuyCashRatio = 0.15,
            AdditionalBuyPositionRatio = 0.5,

            DefaultOrderQty = 1,
            MaxOrderAmount = 100000,
            MaxPositionAmount = 300000,
            MaxTotalPositionAmount = 1000000,
            MinCashRemain = 30000,

            MinSignalScore = 70,
            CooldownSeconds = 20,
            AiModelFilter = "llama-3-8b",
            HogaType = "00",
            // HardStop은 외부 시장상태/동적 임계값과 분리된 고정 Gross 손실한도다.
            StopLossRate = 1.0,
            TakeProfitRate = 999,
            TrailingStartProfitRate = 0.80,
            TrailingStopRate = 0.70,
            EstimatedBuyFeeRate = MoneyCreatorKiwoomDomesticOnlineFeeRate,
            EstimatedSellFeeRate = MoneyCreatorKiwoomDomesticOnlineFeeRate,
            EstimatedSellTaxRate = MoneyCreatorKiwoomDomesticSellTaxRate,
            UseEstimatedTradingCostForExit = false,
            IsReplayMode = false,
            AnalysisLogMode = AutoTradeAnalysisLogMode.All
        };

        private string _tirexPredictionText = "TiRex 대기";
        public string TiRexPredictionText
        {
            get => _tirexPredictionText;
            set => SetProperty(ref _tirexPredictionText, value);
        }

        private string _tirexPredictionDetail = "-";
        public string TiRexPredictionDetail
        {
            get => _tirexPredictionDetail;
            set => SetProperty(ref _tirexPredictionDetail, value);
        }


        private string _currentSymbolAllocationText = "할당 -";
        public string CurrentSymbolAllocationText
        {
            get => _currentSymbolAllocationText;
            set => SetProperty(ref _currentSymbolAllocationText, value);
        }

        public bool IsAutoTradingEnabled
        {
            get => _autoTradeSettings.Enabled;
            set
            {
                if (value && !CanEnableAutoTradingForCurrentSymbol())
                {
                    if (_autoTradeSettings.Enabled)
                    {
                        _autoTradeSettings.Enabled = false;
                        OnPropertyChanged();
                        _autoTradeController?.UpdateSettings(_autoTradeSettings);
                        ApplyReplayAutoTradingState(false);
                        StopAutoTradingForCurrentSymbol();
                    }
                    else
                    {
                        OnPropertyChanged();
                    }

                    AddLog("⚠️ 자동매매 ON 차단: 종목코드가 없습니다.");
                    AddAutoTradeAnalysisLog("[자동매매 차단] 종목코드 없음 - Auto OFF 유지");
                    return;
                }

                if (_autoTradeSettings.Enabled == value)
                    return;

                _autoTradeSettings.Enabled = value;
                OnPropertyChanged();

                if (value && _autoTradeController == null)
                    InitializeAutoTrading();

                SyncAutoTradeReplayModeSetting("AutoTrading Enabled 변경", false);
                _autoTradeController?.UpdateSettings(_autoTradeSettings);

                if (IsReplayMode && value)
                {
                    // Replay는 분석 seed/상태 동기화를 먼저 끝낸 뒤 tick 재생을 시작한다.
                    StartAutoTradingForCurrentSymbol();
                    ApplyReplayAutoTradingState(true);
                    return;
                }

                ApplyReplayAutoTradingState(value);

                if (value)
                    StartAutoTradingForCurrentSymbol();
                else
                    StopAutoTradingForCurrentSymbol();
            }
        }

        private void InitializeAutoTrading()
        {
            if (_autoTradeController != null)
                return;

            var mlFilter = new TiRexHttpSignalFilter(
                "http://127.0.0.1:5001/predict",
                AddAutoTradeAnalysisLog,
                UpdateTiRexPredictionUi);

            SyncAutoTradeReplayModeSetting("자동매매 초기화", false);

            _autoTradeController = new AutoTradeCoreWorker(
                _autoTradeSettings,
                mlFilter,
                () => WindowId,
                () => ScreenNo,
                () => SelectedAccount,
                GetActiveAutoTradeSlotCount,
                GetActiveAutoTradeCodes);

            lock (_moneyCreatorPortfolioSyncRoot)
                ApplyMoneyCreatorEstimatedTradingCostToSettingsLocked();

            _autoTradeController.AnalysisEvaluated += OnAutoTradeAnalysisEvaluated;
            _autoTradeController.OrderRequested += OnAutoTradeOrderRequested;
            _autoTradeController.Log += AddAutoTradeAnalysisLog;

            SyncAutoTradeState("자동매매 초기화");
            _autoTradeController.Start();
            StartDecisionCandidateOutcomePumpTimer();
        }

        private void SyncAutoTradeReplayModeSetting(string reason)
        {
            SyncAutoTradeReplayModeSetting(reason, true);
        }

        private void SyncAutoTradeReplayModeSetting(string reason, bool logWhenChanged)
        {
            if (_autoTradeSettings == null)
                return;

            bool replayMode = IsReplayMode;
            bool changed = _autoTradeSettings.IsReplayMode != replayMode;
            _autoTradeSettings.IsReplayMode = replayMode;

            if (_autoTradeController != null)
                _autoTradeController.UpdateSettings(_autoTradeSettings);

            if (changed && logWhenChanged)
            {
                AddAutoTradeAnalysisLog(string.Format(
                    "[ReplayMode 동기화] AutoTrading.IsReplayMode={0} / {1}",
                    replayMode ? "ON" : "OFF",
                    string.IsNullOrWhiteSpace(reason) ? "-" : reason));
            }
        }

        private void StartAutoTradingForCurrentSymbol()
        {
            bool replayMode = IsReplayMode;

            AddLog(replayMode ? "🤖 자동매매 ON / Replay 재개" : "🤖 자동매매 ON");
            AddAutoTradeAnalysisLog(replayMode
                ? "🤖 자동매매 ON - Replay 일시정지 해제/재개"
                : "🤖 자동매매 ON - 기존 분봉/최근틱 기준 분석 준비");

            if (Context == null || string.IsNullOrWhiteSpace(Context.Code))
                return;

            if (replayMode)
            {
                bool firstReplayStart = TryMarkReplayAutoSessionStarted(Context.Code);

                if (firstReplayStart)
                {
                    AutoTradeCsvWriter.Shared.BeginSession(
                        Context.Code,
                        ResolveReplayTradingDateForAutoSession(),
                        true);

                    ResetReplayMlAnalysisDataBeforeAutoStart(Context.Code);
                    SeedAutoTradeHistory(Context.Code);
                    SyncAutoTradeMinuteCandles(
                        Context.Code,
                        "Replay Auto 최초 시작",
                        ResolveAutoTradeMinuteSyncCutoff(DateTime.MinValue));

                    AddAutoTradeAnalysisLog(
                        $"[Replay Auto 시작] {Context.Code} / 신규 세션 / ML데이터 초기화=Y / pending 초기화=Y / RoundTripCost={GetMoneyCreatorEstimatedRoundTripCostPercent():0.###}%");
                }
                else
                {
                    // Replay Auto OFF는 일시정지이므로 기존 Candidate/Outcome pending 상태를 유지한다.
                    // 분봉은 현재 Replay 시간 이하 cutoff만 가볍게 재동기화한다.
                    SyncAutoTradeMinuteCandles(
                        Context.Code,
                        "Replay Auto 재개",
                        ResolveAutoTradeMinuteSyncCutoff(DateTime.MinValue));

                    AddAutoTradeAnalysisLog(
                        $"[Replay Auto 재개] {Context.Code} / ML데이터 초기화=N / pending 유지=Y / RoundTripCost={GetMoneyCreatorEstimatedRoundTripCostPercent():0.###}%");
                }

                _replayAutoPauseActive = false;
                SyncAutoTradeState(firstReplayStart ? "Replay Auto 최초 시작" : "Replay Auto 재개");
                return;
            }

            AutoTradeCsvWriter.Shared.BeginSession(
                Context.Code,
                ResolveAutoTradePositionTimestamp().Date,
                false);

            SeedAutoTradeHistory(Context.Code);
            SyncAutoTradeMinuteCandles(
                Context.Code,
                "자동매매 ON",
                ResolveAutoTradeMinuteSyncCutoff(DateTime.MinValue));
            SyncAutoTradeState("자동매매 ON");
        }

        private void ResetReplayMlAnalysisDataBeforeAutoStart(string code)
        {
            if (!IsReplayMode || Db == null || string.IsNullOrWhiteSpace(code))
                return;

            if (!(_marketApi is LocalReplayApiService replayApi))
                return;

            DateTime tradingDate = replayApi.ReplayTradingDate.Date;
            if (tradingDate == default(DateTime).Date)
                return;

            try
            {
                _pendingFeatureSnapshotOutcomeMap.Clear();
                _pendingDecisionCandidateOutcomeMap.Clear();
                _outcomeLabelWatchMap.Clear();
                _lastReplayOutcomeLabelAt = DateTime.MinValue;
                int removedQueued = Db.DeleteReplayMlAnalysisData(code, tradingDate);
                AddAutoTradeAnalysisLog($"[Replay ML 초기화 예약] 기존 분석데이터 삭제요청: {code} / {tradingDate:yyyy-MM-dd} / 저장큐 제거={removedQueued:N0}건 / DB삭제=Worker");
            }
            catch (Exception ex)
            {
                AddAutoTradeAnalysisLog($"[Replay ML 초기화 실패] {code} / {tradingDate:yyyy-MM-dd} / {ex.Message}");
            }
        }

        private bool TryMarkReplayAutoSessionStarted(string code)
        {
            if (!IsReplayMode)
                return true;

            string key = BuildReplayAutoSessionKey(code);
            if (string.IsNullOrWhiteSpace(key))
                return true;

            if (string.Equals(_replayAutoInitializedSessionKey, key, StringComparison.OrdinalIgnoreCase))
                return false;

            _replayAutoInitializedSessionKey = key;
            _replayAutoPauseActive = false;
            return true;
        }

        private string BuildReplayAutoSessionKey(string code)
        {
            string safeCode = string.IsNullOrWhiteSpace(code) ? "UNKNOWN" : code.Trim();
            string safeWindow = string.IsNullOrWhiteSpace(WindowId) ? "SYSTEM" : WindowId;
            DateTime tradingDate = ResolveReplayTradingDateForAutoSession();

            return string.Format(
                "{0:yyyyMMdd}:{1}:{2}",
                tradingDate == default(DateTime) ? DateTime.Today : tradingDate.Date,
                safeCode,
                safeWindow);
        }

        private DateTime ResolveReplayTradingDateForAutoSession()
        {
            try
            {
                if (IsReplayMode && _marketApi is LocalReplayApiService replayApi && replayApi.ReplayTradingDate != default(DateTime))
                    return replayApi.ReplayTradingDate.Date;
            }
            catch
            {
            }

            return DateTime.Today;
        }

        private void ResetReplayAutoSessionLifecycleState(string reason)
        {
            _replayAutoInitializedSessionKey = string.Empty;
            _replayAutoPauseActive = false;
            _lastReplayOutcomeLabelAt = DateTime.MinValue;

            if (!string.IsNullOrWhiteSpace(reason))
                AddAutoTradeAnalysisLog($"[Replay Auto 상태초기화] {reason} / 세션키 초기화");
        }

        private sealed class ReplayModeOffFinalizeContext
        {
            public string Reason { get; set; }
            public string Code { get; set; }
            public DateTime TradingDate { get; set; }
        }

        private ReplayModeOffFinalizeContext CaptureReplayModeOffFinalizeContext(string reason)
        {
            return new ReplayModeOffFinalizeContext
            {
                Reason = string.IsNullOrWhiteSpace(reason) ? "ReplayMode OFF" : reason,
                Code = Context == null ? string.Empty : Context.Code,
                TradingDate = ResolveReplayTradingDateForAutoSession()
            };
        }

        private void FinalizeReplayAutoTradingForModeOff(string reason)
        {
            ReplayModeOffFinalizeContext context = null;

            try
            {
                if (!IsReplayMode)
                    return;

                context = CaptureReplayModeOffFinalizeContext(reason);

                AddAutoTradeAnalysisLog(
                    $"[Replay Auto 종료예약] {context.Reason} / UI전환 우선 / Flush+라벨분석=Background / Code={context.Code} / Date={context.TradingDate:yyyy-MM-dd}");

                Task.Run(() => RunReplayModeOffFinalizeBackground(context));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Replay Auto 종료 예약 실패] " + ex.Message);
            }
            finally
            {
                ResetReplayAutoSessionLifecycleState(reason);
            }
        }

        private void RunReplayModeOffFinalizeBackground(ReplayModeOffFinalizeContext context)
        {
            if (context == null)
                return;

            try
            {
                Debug.WriteLine($"[Replay Auto 종료후처리 시작] {context.Reason} / {context.Code} / {context.TradingDate:yyyy-MM-dd}");

                // ReplayMode OFF는 종료지만 DB 저장은 worker가 처리해야 한다.
                // 여기서는 Flush 요청만 남기고, 라벨분석 리포트는 DB worker 저장 이후의 수동/조회 시점으로 넘긴다.
                Db?.RequestFlushAllPending();

                Debug.WriteLine($"[Replay Auto 종료후처리 요청완료] {context.Reason} / {context.Code} / {context.TradingDate:yyyy-MM-dd} / DB저장=Worker");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Replay Auto 종료후처리 실패] " + ex.Message);
            }
        }


        private void StopAutoTradingForCurrentSymbol()
        {
            if (IsReplayMode)
            {
                _replayAutoPauseActive = true;

                AddLog("🤖 자동매매 OFF / Replay 일시정지");
                AddAutoTradeAnalysisLog(
                    "🤖 자동매매 OFF - Replay 일시정지 / DB강제Flush=N / 라벨분석=N / pending 유지=Y");
                SyncAutoTradeState("Replay Auto Pause");
                return;
            }

            AddLog("🤖 자동매매 실주문 OFF");
            AddAutoTradeAnalysisLog("🤖 자동매매 실주문 OFF");
            FlushAutoTradeMlAnalysisQueues("자동매매 실주문 OFF");
            SyncAutoTradeState("자동매매 실주문 OFF");
        }

        private void StartMoneyCreatorPortfolioTimers()
        {
            try
            {
                _moneyCreatorPortfolioUsageRefreshTimer.Stop();
                _moneyCreatorPortfolioUsageRefreshTimer.Tick -= MoneyCreatorPortfolioUsageRefreshTimer_Tick;
                _moneyCreatorPortfolioUsageRefreshTimer.Interval = TimeSpan.FromSeconds(MoneyCreatorPortfolioUsageRefreshSeconds);
                _moneyCreatorPortfolioUsageRefreshTimer.Tick += MoneyCreatorPortfolioUsageRefreshTimer_Tick;
                _moneyCreatorPortfolioUsageRefreshTimer.Start();

                _moneyCreatorPortfolioFallbackRebuildTimer.Stop();
                _moneyCreatorPortfolioFallbackRebuildTimer.Tick -= MoneyCreatorPortfolioFallbackRebuildTimer_Tick;
                _moneyCreatorPortfolioFallbackRebuildTimer.Interval = TimeSpan.FromSeconds(MoneyCreatorPortfolioFallbackRebuildSeconds);
                _moneyCreatorPortfolioFallbackRebuildTimer.Tick += MoneyCreatorPortfolioFallbackRebuildTimer_Tick;
                _moneyCreatorPortfolioFallbackRebuildTimer.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MoneyCreator 자산배분 타이머 시작 실패] " + ex.Message);
            }
        }

        private void StopMoneyCreatorPortfolioTimers()
        {
            try
            {
                _moneyCreatorPortfolioUsageRefreshTimer.Stop();
                _moneyCreatorPortfolioUsageRefreshTimer.Tick -= MoneyCreatorPortfolioUsageRefreshTimer_Tick;

                _moneyCreatorPortfolioFallbackRebuildTimer.Stop();
                _moneyCreatorPortfolioFallbackRebuildTimer.Tick -= MoneyCreatorPortfolioFallbackRebuildTimer_Tick;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MoneyCreator 자산배분 타이머 종료 실패] " + ex.Message);
            }
        }

        private void MoneyCreatorPortfolioUsageRefreshTimer_Tick(object sender, EventArgs e)
        {
            if (!IsMoneyCreatorPortfolioCoordinatorOnUiThread())
                return;

            if (IsMoneyCreatorAccountRefreshRunning() || IsMoneyCreatorPortfolioFullRebuildRunning())
                return;

            var viewModels = GetMoneyCreatorAutoTradeViewModelsOnUiThread();
            RefreshMoneyCreatorPortfolioAllocationUsage(viewModels, "정기잔여갱신");
        }

        private void MoneyCreatorPortfolioFallbackRebuildTimer_Tick(object sender, EventArgs e)
        {
            if (!IsMoneyCreatorPortfolioCoordinatorOnUiThread())
                return;

            if (IsMoneyCreatorAccountRefreshRunning() || IsMoneyCreatorPortfolioFullRebuildRunning())
                return;

            RebuildMoneyCreatorPortfolioAllocations("정기총할당재산정", true);
        }

        private bool IsMoneyCreatorPortfolioCoordinatorOnUiThread()
        {
            var app = Application.Current;
            if (app == null)
                return true;

            if (!app.Dispatcher.CheckAccess())
                return (bool)app.Dispatcher.Invoke(new Func<bool>(IsMoneyCreatorPortfolioCoordinatorOnUiThread));

            var owner = app.Windows
                .Cast<Window>()
                .Select(w => w.DataContext as MainWindowViewModel)
                .Where(vm => vm != null)
                .OrderByDescending(vm => !string.IsNullOrWhiteSpace(vm.SelectedAccount))
                .ThenBy(vm => vm.WindowId)
                .FirstOrDefault();

            return owner == null || ReferenceEquals(owner, this);
        }

        private void UpdateTiRexPredictionUi(TiRexPredictionResult result)
        {
            Application.Current?.Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() =>
                {
                    if (result == null)
                    {
                        TiRexPredictionText = "TiRex 대기";
                        TiRexPredictionDetail = "-";
                        return;
                    }

                    TiRexPredictionText =
                        result.Direction == TiRexDirection.Up ? "TiRex 상승 예측" :
                        result.Direction == TiRexDirection.Down ? "TiRex 하락 예측" :
                        result.Direction == TiRexDirection.Flat ? "TiRex 횡보 예측" :
                        "TiRex 불확실";

                    TiRexPredictionDetail =
                        $"수익예측 {result.PredictedReturn:+0.00;-0.00;0.00}% / 신뢰 {result.Confidence:P0}";
                }));
        }

        // ============================================================
        // Source: MainWindowViewModel.AutoTrading.Analysis.cs
        // ============================================================
        private DateTime _lastReplayAnalysisScorePointAt = DateTime.MinValue;
        private DateTime _lastReplayAnalysisCardUpdateAt = DateTime.MinValue;
        private DateTime _lastReplayOutcomeLabelAt = DateTime.MinValue;

        // Replay Auto OFF는 종료가 아니라 일시정지다.
        // 이 키는 Replay 최초 시작/재개를 구분해서, 일시정지 후 Auto ON 때
        // ML 데이터 삭제/대기 라벨 초기화가 다시 실행되지 않게 막는다.
        private string _replayAutoInitializedSessionKey = string.Empty;
        private bool _replayAutoPauseActive;

        // 일반 분석/DB/주문 판단은 Buy/Sell DollarBar가 ValueFilled로 닫힐 때만 1회 진행한다.
        // Raw Tick은 Bar 생성과 HardStop 안전경로에만 즉시 사용한다.
        // UI 버킷은 표시 안정화를 위한 묶음일 뿐 매매 판단 시계가 아니다.
        private const int AutoTradeAnalysisUiRefreshIntervalMs = 1000;
        private readonly object _autoTradeAnalysisUiLock = new object();
        private readonly Queue<AnalysisScoreBucket> _pendingAnalysisScoreBuckets = new Queue<AnalysisScoreBucket>();
        private AnalysisScoreBucket _currentAnalysisScoreBucket;
        private FeatureSnapshot _pendingAnalysisCardFeature;
        private TradeSignal _pendingAnalysisCardSignal;
        private bool _analysisUiFlushScheduled;
        private DateTime _lastAnalysisUiFlushAt = DateTime.MinValue;

        private sealed class AnalysisScoreBucket
        {
            public DateTime BucketTime { get; set; }
            public string Code { get; set; }
            public double Price { get; set; }
            public int Count { get; set; }

            public double OpenScore { get; set; }
            public double CloseScore { get; set; }
            public double MinScore { get; set; }
            public double MaxScore { get; set; }

            public FeatureSnapshot CloseFeature { get; set; }
            public TradeSignal CloseSignal { get; set; }
            public FeatureSnapshot MinFeature { get; set; }
            public TradeSignal MinSignal { get; set; }
            public FeatureSnapshot MaxFeature { get; set; }
            public TradeSignal MaxSignal { get; set; }
            public FeatureSnapshot PeakFeature { get; set; }
            public TradeSignal PeakSignal { get; set; }
            public double PeakScore { get; set; }
        }

        private void OnAutoTradeAnalysisEvaluated(FeatureSnapshot feature, TradeSignal signal)
        {
            if (feature == null)
                return;

            if (IsReplayMode && !IsAutoTradingEnabled)
                return;

            if (signal != null)
            {
                feature.SignalAction = signal.Action.ToString();
                feature.SignalScore = signal.Score;
                feature.StrategyName = signal.StrategyName;
                feature.SignalReason = signal.Reason;
            }

            RememberMoneyCreatorFeature(feature);
            // 분석갱신은 점수/Feature만 갱신한다.
            // 총할당금액 재계산은 자동매매 ON/OFF, 종목 변경, 명시적 주기 갱신에서만 수행한다.
            ApplyMoneyCreatorAllocationToFeature(feature);
            TrySyncAutoTradeMinuteCandlesFromAnalysis(feature);

            PersistAutoTradeFeatureSnapshotIfNeeded(feature, signal);
            string signalId = PersistAutoTradeSignalLog(feature, signal);
            string candidateId = PersistAutoTradeDecisionCandidateIfNeeded(feature, signal, signalId);

            // CSV는 DB/UI와 독립된 분석 보조 저장이다. 실제 파일 I/O는 DataBase 전용 Writer Thread가 처리한다.
            AutoTradeCsvWriter.Shared.EnqueueFrame(
                feature,
                signal,
                IsReplayMode,
                signalId,
                candidateId);

            ProcessDueMlOutcomeLabels(feature.Time == default(DateTime) ? DateTime.Now : feature.Time);

            // 분석 로그는 UI 카드/차트 갱신과 분리한다.
            // 일반 판단은 [바판단], Raw HardStop 안전경로는 [원시안전]으로 구분해 판단 사건마다 남긴다.
            WritePerAnalysisBarMinimalLog(feature, signal);

            TryCancelBlockingUnfilledOrderFromAnalysis(feature);

            QueueAnalysisUiUpdate(feature, signal);
        }

        private void WritePerAnalysisBarMinimalLog(FeatureSnapshot feature, TradeSignal signal)
        {
            if (feature == null)
                return;

            // 화면의 "매매만" 모드는 일반 Hold 판단을 출력하지 않는다.
            // CSV Frame은 아래 UI 필터와 무관하게 계속 전부 저장된다.
            if (_autoTradeSettings != null &&
                _autoTradeSettings.AnalysisLogMode == AutoTradeAnalysisLogMode.TradeOnly &&
                feature.IsClosedDecisionBar &&
                (signal == null || signal.Action == TradeActionType.Hold))
            {
                return;
            }

            try
            {
                string msg = BuildBarMinimalAnalysisLog(feature, signal);
                if (!string.IsNullOrWhiteSpace(msg))
                    AddAutoTradeAnalysisLog(msg);

                // v135: 전체 축 상세는 Action/HardStop/임계값 근처에서만 한 줄 추가한다.
                // 모든 Hold에 Bar/Flow/Frame/Breakdown 전체를 반복 출력하지 않는다.
                string axisDetail = BuildAxisDetailAnalysisLog(feature, signal);
                if (!string.IsNullOrWhiteSpace(axisDetail))
                    AddAutoTradeAnalysisLog(axisDetail);
            }
            catch (Exception ex)
            {
                AddAutoTradeAnalysisLog("[분석로그 오류] " + ex.Message);
            }
        }

        private string BuildBarMinimalAnalysisLog(FeatureSnapshot feature, TradeSignal signal)
        {
            if (feature == null)
                return null;

            DateTime time = feature.Time == default(DateTime) ? DateTime.Now : feature.Time;
            string code = string.IsNullOrWhiteSpace(feature.Code) ? signal?.Code ?? "-" : feature.Code;
            string action = signal == null ? feature.SignalAction : signal.Action.ToString();
            if (string.IsNullOrWhiteSpace(action))
                action = "Hold";

            int score = (int)Math.Round(CalculateAnalysisChartScore(feature, signal));
            string position = feature.HasPosition && feature.HoldingQty > 0
                ? "Holding"
                : "Flat";
            string reason = BuildCompactDecisionReason(feature, signal);
            SignalScoreBreakdown breakdown = signal == null ? null : signal.ScoreBreakdown;

            double rawPressure = breakdown != null && breakdown.IsReady
                ? breakdown.RawBarPressureScore
                : feature.BarSidePressureBias * 100.0;
            double structure = breakdown != null && breakdown.IsReady
                ? breakdown.StructureContribution
                : 0.0;
            double entryRisk = breakdown != null && breakdown.IsReady
                ? breakdown.EntryRiskContribution
                : 0.0;
            double unifiedExit = breakdown != null && breakdown.IsReady
                ? breakdown.UnifiedExitContribution
                : 0.0;
            string regime = breakdown == null || string.IsNullOrWhiteSpace(breakdown.PriceRegime)
                ? "-"
                : breakdown.PriceRegime;

            string pending = feature.HasPendingBuy || feature.HasPendingSell || feature.OpenOrderCount > 0
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    " | 주문=B:{0}/S:{1}/Open:{2}",
                    feature.HasPendingBuy ? "Y" : "N",
                    feature.HasPendingSell ? "Y" : "N",
                    feature.OpenOrderCount)
                : string.Empty;

            string logPrefix = feature.IsClosedDecisionBar
                ? "[바판단] "
                : "[원시안전] ";

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}{1} | 종목={2} | 현재가={3} | 점수={4} | 판단={5} | 보유={6}/{7} | 수익률={8:F2}% | Raw={9:+0.0;-0.0;0.0} | Struct={10:+0.0;-0.0;0.0} | Entry={11:+0.0;-0.0;0.0} | Exit={12:+0.0;-0.0;0.0} | Regime={13} | 사유={14}{15}",
                logPrefix,
                time.ToString("HH:mm:ss"),
                code,
                FormatAnalysisMoney(feature.Price),
                FormatSignedAnalysisScore(score),
                action,
                position,
                feature.HoldingQty,
                feature.NetProfitRate,
                rawPressure,
                structure,
                entryRisk,
                unifiedExit,
                regime,
                reason,
                pending);
        }

        private static string BuildAxisDetailAnalysisLog(FeatureSnapshot feature, TradeSignal signal)
        {
            if (feature == null || signal == null)
                return null;

            SignalScoreBreakdown breakdown = signal.ScoreBreakdown;
            if (breakdown == null || !breakdown.IsReady)
                return null;

            bool actionEvent = signal.Action != TradeActionType.Hold;
            bool rawSafetyEvent = !feature.IsClosedDecisionBar;
            int thresholdDistance = 10;
            int activeThreshold = signal.Score >= 0
                ? Math.Max(1, breakdown.BuyThreshold)
                : Math.Max(1, Math.Abs(breakdown.SellThreshold));
            bool nearThreshold = Math.Abs(signal.Score) >= Math.Max(0, activeThreshold - thresholdDistance);

            if (!actionEvent && !rawSafetyEvent && !nearThreshold)
                return null;

            DateTime time = feature.Time == default(DateTime) ? DateTime.Now : feature.Time;
            string code = string.IsNullOrWhiteSpace(feature.Code) ? signal.Code ?? "-" : feature.Code;
            string memoryReason = string.IsNullOrWhiteSpace(breakdown.ExitMemoryResetReason)
                ? "None"
                : breakdown.ExitMemoryResetReason;

            return string.Format(
                CultureInfo.InvariantCulture,
                "[축상세] {0} | 종목={1} | PPart=S:{2:+0.0;-0.0;0.0}/T:{3:+0.0;-0.0;0.0}/A:{4:+0.0;-0.0;0.0}/Agr:{5:+0.0;-0.0;0.0}(Raw:{6:+0.0;-0.0;0.0},Ind:{7:0.000}) | AccRaw=B:{8:+0.000;-0.000;0.000}/S:{9:+0.000;-0.000;0.000} | AccNorm=B:{10:+0.000;-0.000;0.000}/S:{11:+0.000;-0.000;0.000} | Drift=B:{12:+0.000;-0.000;0.000}/S:{13:+0.000;-0.000;0.000} | RelAcc={14:+0.0;-0.0;0.0}/Auth:{15:0.000} | Obs=B:{16}/S:{17}/T:{18} | Turn={19:+0.0;-0.0;0.0} | Path={20:+0.0;-0.0;0.0} | ProfitBreak={21:0.000} | ProfitAccept={22:0.000} | PersistApply={23:0.000} | ResidualRisk={24:0.000} | ResidualPenalty={25:0.0} | ExitMemory={26}",
                time.ToString("HH:mm:ss"),
                code,
                breakdown.PressureStatePart,
                breakdown.PressureTransitionPart,
                breakdown.PressureAccelerationPart,
                breakdown.PressureAgreementPart,
                breakdown.RawPressureAgreementPart,
                breakdown.AgreementIndependentSupport,
                breakdown.BuyRawMoneyAcceleration,
                breakdown.SellRawMoneyAcceleration,
                breakdown.BuyTargetNormalizedAcceleration,
                breakdown.SellTargetNormalizedAcceleration,
                breakdown.BuyTargetDriftContribution,
                breakdown.SellTargetDriftContribution,
                breakdown.SignedRelativeAcceleration,
                breakdown.AccelerationAuthority,
                breakdown.BuyPressureObservationCount,
                breakdown.SellPressureObservationCount,
                breakdown.TransitionObservationCount,
                breakdown.PressureTurnScore,
                breakdown.PricePathScore,
                breakdown.ProfitMarketBreakEvidence,
                breakdown.ProfitMarketAcceptance,
                breakdown.PersistentCurrentApplicationAuthority,
                breakdown.ResidualExitReEntryRisk,
                breakdown.ResidualExitPenalty,
                memoryReason);
        }

        private static string BuildCompactDecisionReason(FeatureSnapshot feature, TradeSignal signal)
        {
            string reason = ResolveTradeCardReason(feature, signal);
            if (string.IsNullOrWhiteSpace(reason))
                return "분석 완료";

            string decisionReason = ExtractDecisionValue(reason, "Reason");
            string guard = ExtractDecisionValue(reason, "Guard");
            string state = ExtractDecisionValue(reason, "State");
            string detail = ExtractDecisionValue(reason, "Detail");
            string finalDecision = ExtractDecisionValue(reason, "FinalDecision");

            if (string.Equals(decisionReason, "BarTargetMode", StringComparison.OrdinalIgnoreCase) ||
                ContainsIgnoreCase(reason, "Decision 전 BarTargetMode"))
            {
                string mode = feature == null || string.IsNullOrWhiteSpace(feature.BarTargetMode)
                    ? "NoBar"
                    : feature.BarTargetMode;
                return "NoDecisionBar:" + mode;
            }

            if (ContainsIgnoreCase(reason, "HardStopLoss") ||
                ContainsIgnoreCase(reason, "절대손절"))
                return "HardStopLoss";

            if (string.Equals(decisionReason, "OrderIntentHold", StringComparison.OrdinalIgnoreCase))
            {
                string result = string.IsNullOrWhiteSpace(state)
                    ? "OrderIntentHold"
                    : "OrderIntentHold:" + state;

                if (!string.IsNullOrWhiteSpace(detail))
                {
                    string compactDetail = detail.Length <= 60
                        ? detail
                        : detail.Substring(0, 60) + "...";
                    result += "(" + compactDetail + ")";
                }

                return result;
            }

            if (!string.IsNullOrWhiteSpace(decisionReason))
            {
                if (!string.IsNullOrWhiteSpace(guard))
                    return decisionReason + ":" + guard;

                return decisionReason;
            }

            if (!string.IsNullOrWhiteSpace(guard))
                return guard;

            if (!string.IsNullOrWhiteSpace(finalDecision))
                return "FinalDecision=" + finalDecision;

            string first = reason.Split(new[] { " | " }, StringSplitOptions.None)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(first))
                return "분석 완료";

            return first.Length <= 80 ? first : first.Substring(0, 80) + "...";
        }

        private static string BuildCompactScoreSummary(TradeSignal signal)
        {
            SignalScoreBreakdown breakdown = signal == null ? null : signal.ScoreBreakdown;
            if (breakdown == null || !breakdown.IsReady)
                return null;

            return string.Format(
                CultureInfo.InvariantCulture,
                "Raw:{0:+0.0;-0.0;0.0}/Struct:{1:+0.0;-0.0;0.0}/Entry:{2:+0.0;-0.0;0.0}/Exit:{3:+0.0;-0.0;0.0}/Final:{4:+0;-0;0}/Th:{5}/{6}/Regime:{7}",
                breakdown.RawBarPressureScore,
                breakdown.StructureContribution,
                breakdown.EntryRiskContribution,
                breakdown.UnifiedExitContribution,
                breakdown.FinalScore,
                breakdown.BuyThreshold,
                breakdown.SellThreshold,
                string.IsNullOrWhiteSpace(breakdown.PriceRegime) ? "-" : breakdown.PriceRegime);
        }



        private static string BuildAnalysisScorePointReason(
            FeatureSnapshot feature,
            TradeSignal signal,
            double score,
            string pointType,
            AnalysisScoreBucket bucket)
        {
            string action = signal == null ? "Hold" : signal.Action.ToString();
            return string.Format(
                CultureInfo.InvariantCulture,
                "FinalScore={0:+0.##;-0.##;0}, Point={1}, Count={2}, Open={3:+0.##;-0.##;0}, Close={4:+0.##;-0.##;0}, Min={5:+0.##;-0.##;0}, Max={6:+0.##;-0.##;0}, Action={7}",
                score,
                string.IsNullOrWhiteSpace(pointType) ? "RAW" : pointType,
                bucket == null ? 0 : bucket.Count,
                bucket == null ? score : bucket.OpenScore,
                bucket == null ? score : bucket.CloseScore,
                bucket == null ? score : bucket.MinScore,
                bucket == null ? score : bucket.MaxScore,
                action);
        }

        private static string BuildCompactBarSummary(FeatureSnapshot feature)
        {
            if (feature == null)
                return "-";

            string mode = string.IsNullOrWhiteSpace(feature.BarTargetMode)
                ? "NoBar"
                : feature.BarTargetMode;

            if (!string.Equals(mode, "Decision", StringComparison.OrdinalIgnoreCase))
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} Tick={1} Elapsed={2:F1}/15.0s Coverage={3:P0} Value={4} MaxTick={5:P1} Source={6}",
                    mode,
                    feature.BarBootstrapTickCount,
                    feature.BarBootstrapElapsedSeconds,
                    feature.BarBootstrapCoverageRatio,
                    FormatCompactAnalysisMoney(feature.BarBootstrapTradeValue),
                    feature.BarBootstrapLargestTickShare,
                    string.IsNullOrWhiteSpace(feature.BarBootstrapSource) ? "Live15sFromFirstTick" : feature.BarBootstrapSource);
            }

            string totalState = feature.DollarBarIsValueFilledClosed
                ? "Value"
                : feature.DollarBarIsTimeoutClosed
                    ? "Timeout"
                    : feature.DollarBarIsClosed ? "Closed" : "Open";

            string buyState = feature.BuyDollarBarIsValueFilledClosed
                ? "Value"
                : feature.BuyDollarBarIsClosed ? "Closed" : "Open";

            string sellState = feature.SellDollarBarIsValueFilledClosed
                ? "Value"
                : feature.SellDollarBarIsClosed ? "Closed" : "Open";

            return string.Format(
                CultureInfo.InvariantCulture,
                "SelectedClose=T:{0}/B:{1}/S:{2} AgeT={3:0}s SelectedFill=T:{4:P0}/B:{5:P0}/S:{6:P0} SelectedSpeed=B:{7:0.00}/S:{8:0.00} Closed60=ValueB:{9}/AllB:{10}/ValueS:{11}/AllS:{12} DTarget={13}->{14}({15:+0.0%;-0.0%;0.0%}) Obs={16} Src={17}/{18:0.###}s Eff={19:0.###}s Trend={20:+0.00;-0.00;0.00} Cf={21:P0} TRel={22:P0} SRel={23:P0} Recent={24:P0} x{25:0.00} SideRatio=B:{26:0.000}/S:{27:0.000}",
                totalState,
                buyState,
                sellState,
                feature.DollarBarAgeSeconds,
                feature.DollarBarFillRatio,
                feature.BuyDollarBarFillRatio,
                feature.SellDollarBarFillRatio,
                feature.BuyDollarBarSpeedRatio,
                feature.SellDollarBarSpeedRatio,
                feature.BuyDollarBarValueClosedCount60s,
                feature.BuyDollarBarClosedCount60s,
                feature.SellDollarBarValueClosedCount60s,
                feature.SellDollarBarClosedCount60s,
                FormatCompactAnalysisMoney(feature.BarPreviousTargetTradeValue),
                FormatCompactAnalysisMoney(feature.BarNextTargetTradeValue),
                feature.BarDTargetChangeRatio,
                FormatCompactAnalysisMoney(feature.BarObservedTargetTradeValue),
                string.IsNullOrWhiteSpace(feature.BarDTargetUpdateReason) ? "-" : feature.BarDTargetUpdateReason,
                feature.BarDTargetSourceDurationSeconds,
                feature.BarDTargetEffectiveDurationSeconds,
                feature.BarDTargetInternalTrend,
                feature.BarDTargetConfidence,
                feature.BarDTargetTimeReliability,
                feature.BarDTargetSampleReliability,
                feature.BarDTargetRecentConsistency,
                feature.BarDTargetSourceOvershootRatio,
                feature.BuyDollarBarTargetRatioToTotal,
                feature.SellDollarBarTargetRatioToTotal);
        }

        private static string BuildCompactIndependentFlowSummary(FeatureSnapshot feature)
        {
            if (feature == null)
                return "-";

            string raw = ExtractIndependentBarFlowDiagnostic(feature.LiveBarProgressReason);
            string official = string.Format(
                CultureInfo.InvariantCulture,
                "Evidence=C:{0:+0.00;-0.00;0.00}/L:{1:+0.00;-0.00;0.00} Pressure:{2:+0.00;-0.00;0.00} Price:{3:+0.00;-0.00;0.00} Abs:{4:+0.00;-0.00;0.00} Cf:{5:P0} LiveStrength=B:{6:0.00}/S:{7:0.00}",
                feature.BarSidePressureValueBias,
                feature.BarSidePressureSpeedBias,
                feature.BarSidePressureBias,
                feature.BarSidePressurePriceReactionBias,
                feature.BarSidePressureAbsorptionBias,
                feature.BarSidePressureConfidence,
                feature.LiveBarProgressBuyScore / 100.0,
                feature.LiveBarProgressSellScore / 100.0);

            return string.IsNullOrWhiteSpace(raw)
                ? official
                : official + " Raw=" + raw;
        }

        private static string ExtractIndependentBarFlowDiagnostic(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return string.Empty;

            int start = reason.IndexOf("ClosedSideBarDecision", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                start = reason.IndexOf("IndependentBarFlow", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return string.Empty;

            string diagnostic = reason.Substring(start).Trim();
            int separator = diagnostic.IndexOf(" | ", StringComparison.Ordinal);
            if (separator >= 0)
                diagnostic = diagnostic.Substring(0, separator).Trim();

            return diagnostic;
        }

        private static string BuildCompactFrameSummary(FeatureSnapshot feature)
        {
            if (feature == null || !feature.IsClosedDecisionBar)
                return "RawSafety";

            return string.Format(
                "Type={15}/{16} Ticks={0} OHLC={1:N0}/{2:N0}/{3:N0}/{4:N0} Value={5} B={6} S={7} N={8} Net={9} Direction={10:+0.00;-0.00;0.00} VWAP={11:N0} Strength={12:0.##} Duration={13:0.###}s TimeGap={14}",
                feature.FrameTickCount,
                feature.FrameOpenPrice,
                feature.FrameHighPrice,
                feature.FrameLowPrice,
                feature.Price,
                FormatCompactAnalysisMoney(feature.TickTradeValue),
                FormatCompactAnalysisMoney(feature.FrameBuyTradeValue),
                FormatCompactAnalysisMoney(feature.FrameSellTradeValue),
                FormatCompactAnalysisMoney(feature.FrameNeutralTradeValue),
                FormatCompactAnalysisMoney(feature.FrameNetBuyTradeValue),
                feature.FrameDirectionalPurity,
                feature.FrameVwap,
                feature.FrameValueWeightedStrength,
                feature.FrameElapsedSeconds,
                feature.FrameHasTimeGap ? "Y" : "N",
                string.IsNullOrWhiteSpace(feature.DecisionBarType) ? "-" : feature.DecisionBarType,
                string.IsNullOrWhiteSpace(feature.DecisionBarCloseReason) ? "-" : feature.DecisionBarCloseReason);
        }

        private static string BuildCompactOrderSummary(FeatureSnapshot feature, TradeSignal signal)
        {
            if (feature == null)
                return "-";

            string state = "None";
            string mode = string.IsNullOrWhiteSpace(feature.BarTargetMode) ? "NoBar" : feature.BarTargetMode;
            string rawReason = ResolveTradeCardReason(feature, signal);
            string decisionReason = ExtractDecisionValue(rawReason, "Reason");
            string operationalState = ExtractDecisionValue(rawReason, "State");
            string guard = ExtractDecisionValue(rawReason, "Guard");

            if (string.Equals(mode, "RawSafety", StringComparison.OrdinalIgnoreCase))
                state = feature.HasPendingSell ? "Blocked:PendingSell" : "HardStopRaw";
            else if (!string.Equals(mode, "Decision", StringComparison.OrdinalIgnoreCase))
                state = "Blocked:NoDecisionBar";
            else if (feature.HasPendingBuy || feature.HasPendingSell || feature.OpenOrderCount > 0)
                state = "Blocked:Pending";
            else if (!string.IsNullOrWhiteSpace(operationalState))
                state = "Blocked:" + operationalState;
            else if (!string.IsNullOrWhiteSpace(guard))
                state = "Blocked:" + (string.IsNullOrWhiteSpace(decisionReason) ? "Guard" : decisionReason);
            else if (!string.IsNullOrWhiteSpace(decisionReason) &&
                     (decisionReason.EndsWith("Block", StringComparison.OrdinalIgnoreCase) ||
                      decisionReason.EndsWith("Disabled", StringComparison.OrdinalIgnoreCase) ||
                      decisionReason.StartsWith("NoSellable", StringComparison.OrdinalIgnoreCase)))
                state = "Blocked:" + decisionReason;

            return string.Format(
                "{0} Pending=B:{1}/S:{2} Open={3} Cash={4} Remain={5}",
                state,
                feature.HasPendingBuy ? "Y" : "N",
                feature.HasPendingSell ? "Y" : "N",
                feature.OpenOrderCount,
                FormatAnalysisMoney(feature.Cash),
                FormatAnalysisMoney(feature.SymbolRemainingBudget));
        }

        private static string FormatSignedAnalysisScore(int score)
        {
            return score.ToString("+0;-0;0");
        }

        private static string FormatCompactAnalysisMoney(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return "-";

            double abs = Math.Abs(value);
            if (abs >= 100000000.0)
                return (value / 100000000.0).ToString("0.##") + "억";

            if (abs >= 1000000.0)
                return (value / 1000000.0).ToString("0.#") + "M";

            if (abs >= 1000.0)
                return (value / 1000.0).ToString("0.#") + "K";

            return value.ToString("N0");
        }

        private static string ExtractDecisionValue(string reason, string key)
        {
            if (string.IsNullOrWhiteSpace(reason) || string.IsNullOrWhiteSpace(key))
                return null;

            var match = Regex.Match(
                reason,
                Regex.Escape(key) + @"\s*=\s*(?<value>[^,|]+)",
                RegexOptions.IgnoreCase);

            return match.Success ? match.Groups["value"].Value.Trim() : null;
        }

        private static bool ContainsIgnoreCase(string text, string value)
        {
            return !string.IsNullOrWhiteSpace(text) &&
                   !string.IsNullOrWhiteSpace(value) &&
                   text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string FormatAnalysisMoney(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return "-";

            return value.ToString("N0");
        }

        private static string FormatAnalysisNumber(double value, string format)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return "-";

            return value.ToString(format);
        }

        private void QueueAnalysisUiUpdate(FeatureSnapshot feature, TradeSignal signal)
        {
            if (feature == null)
                return;

            bool shouldSchedule = false;

            lock (_autoTradeAnalysisUiLock)
            {
                _pendingAnalysisCardFeature = feature;
                _pendingAnalysisCardSignal = signal;

                QueueAnalysisScoreBucket_NoLock(feature, signal);

                if (!_analysisUiFlushScheduled)
                {
                    _analysisUiFlushScheduled = true;
                    shouldSchedule = true;
                }
            }

            if (shouldSchedule)
                ScheduleAnalysisUiFlush();
        }

        private void QueueAnalysisScoreBucket_NoLock(FeatureSnapshot feature, TradeSignal signal)
        {
            if (feature == null)
                return;

            DateTime time = ResolveAnalysisDisplayTime(feature);
            DateTime bucketTime = NormalizeAnalysisBucketTime(time);
            double score = CalculateAnalysisChartScore(feature, signal);

            if (_currentAnalysisScoreBucket == null)
            {
                _currentAnalysisScoreBucket = CreateAnalysisScoreBucket(bucketTime, feature, signal, score);
                return;
            }

            if (_currentAnalysisScoreBucket.BucketTime != bucketTime)
            {
                _pendingAnalysisScoreBuckets.Enqueue(_currentAnalysisScoreBucket);
                _currentAnalysisScoreBucket = CreateAnalysisScoreBucket(bucketTime, feature, signal, score);
                return;
            }

            AddAnalysisScoreToBucket(_currentAnalysisScoreBucket, feature, signal, score);
        }

        private static DateTime NormalizeAnalysisBucketTime(DateTime time)
        {
            if (time == default(DateTime))
                time = DateTime.Now;

            return new DateTime(
                time.Year,
                time.Month,
                time.Day,
                time.Hour,
                time.Minute,
                time.Second);
        }

        private static AnalysisScoreBucket CreateAnalysisScoreBucket(
            DateTime bucketTime,
            FeatureSnapshot feature,
            TradeSignal signal,
            double score)
        {
            var bucket = new AnalysisScoreBucket
            {
                BucketTime = bucketTime,
                Code = feature == null || string.IsNullOrWhiteSpace(feature.Code) ? string.Empty : feature.Code,
                Price = feature == null ? 0 : feature.Price,
                Count = 0,
                OpenScore = score,
                CloseScore = score,
                MinScore = score,
                MaxScore = score
            };

            AddAnalysisScoreToBucket(bucket, feature, signal, score);
            return bucket;
        }

        private static void AddAnalysisScoreToBucket(
            AnalysisScoreBucket bucket,
            FeatureSnapshot feature,
            TradeSignal signal,
            double score)
        {
            if (bucket == null || feature == null)
                return;

            bucket.Count++;
            bucket.Code = string.IsNullOrWhiteSpace(feature.Code) ? bucket.Code ?? string.Empty : feature.Code;
            bucket.Price = feature.Price;
            bucket.CloseScore = score;
            bucket.CloseFeature = feature;
            bucket.CloseSignal = signal;

            if (bucket.Count == 1)
            {
                bucket.OpenScore = score;
                bucket.MinScore = score;
                bucket.MaxScore = score;
                bucket.MinFeature = feature;
                bucket.MinSignal = signal;
                bucket.MaxFeature = feature;
                bucket.MaxSignal = signal;
            }
            else
            {
                if (score < bucket.MinScore)
                {
                    bucket.MinScore = score;
                    bucket.MinFeature = feature;
                    bucket.MinSignal = signal;
                }

                if (score > bucket.MaxScore)
                {
                    bucket.MaxScore = score;
                    bucket.MaxFeature = feature;
                    bucket.MaxSignal = signal;
                }
            }

            if (signal != null && signal.Action != TradeActionType.Hold)
            {
                if (bucket.PeakSignal == null || Math.Abs(score) >= Math.Abs(bucket.PeakScore))
                {
                    bucket.PeakScore = score;
                    bucket.PeakFeature = feature;
                    bucket.PeakSignal = signal;
                }
            }
        }

        private void ScheduleAnalysisUiFlush()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                FlushPendingAnalysisUiUpdate();
                return;
            }

            dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() =>
                {
                    int delayMs = ResolveAnalysisUiFlushDelayMilliseconds();

                    if (delayMs <= 0)
                    {
                        FlushPendingAnalysisUiUpdate();
                        return;
                    }

                    var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
                    {
                        Interval = TimeSpan.FromMilliseconds(delayMs)
                    };

                    timer.Tick += (s, e) =>
                    {
                        timer.Stop();
                        FlushPendingAnalysisUiUpdate();
                    };

                    timer.Start();
                }));
        }

        private int ResolveAnalysisUiFlushDelayMilliseconds()
        {
            if (_lastAnalysisUiFlushAt == DateTime.MinValue)
                return 0;

            double elapsedMs = (DateTime.Now - _lastAnalysisUiFlushAt).TotalMilliseconds;
            int delayMs = AutoTradeAnalysisUiRefreshIntervalMs - (int)elapsedMs;

            return delayMs <= 0 ? 0 : delayMs;
        }

        private void FlushPendingAnalysisUiUpdate()
        {
            FeatureSnapshot cardFeature;
            TradeSignal cardSignal;
            List<AnalysisScoreBucket> scoreBuckets;

            lock (_autoTradeAnalysisUiLock)
            {
                cardFeature = _pendingAnalysisCardFeature;
                cardSignal = _pendingAnalysisCardSignal;
                _pendingAnalysisCardFeature = null;
                _pendingAnalysisCardSignal = null;

                scoreBuckets = DrainAnalysisScoreBuckets_NoLock();

                _analysisUiFlushScheduled = false;
                _lastAnalysisUiFlushAt = DateTime.Now;
            }

            if (IsReplayMode && !IsAutoTradingEnabled)
                return;

            foreach (var bucket in scoreBuckets)
                PublishAnalysisScoreBucket(bucket);

            // OrderHistory는 UI 컬렉션이므로 Dispatcher 흐름에서 1초 단위 변화만 CSV로 전달한다.
            // Writer가 주문번호별 fingerprint를 비교해 신규/상태변경/체결갱신만 기록한다.
            DateTime csvFallbackTime =
                cardFeature != null && cardFeature.Time != default(DateTime)
                    ? cardFeature.Time
                    : ResolveAutoTradePositionTimestamp();

            AutoTradeCsvWriter.Shared.EnqueueOrderSnapshots(
                OrderHistory,
                IsReplayMode,
                csvFallbackTime);

            if (cardFeature != null)
                UpdateTradeAnalysisCardFromFeature(cardFeature, cardSignal);
        }

        private List<AnalysisScoreBucket> DrainAnalysisScoreBuckets_NoLock()
        {
            var buckets = new List<AnalysisScoreBucket>();

            while (_pendingAnalysisScoreBuckets.Count > 0)
            {
                var bucket = _pendingAnalysisScoreBuckets.Dequeue();
                if (bucket != null && bucket.Count > 0)
                    buckets.Add(bucket);
            }

            if (_currentAnalysisScoreBucket != null && _currentAnalysisScoreBucket.Count > 0)
            {
                buckets.Add(_currentAnalysisScoreBucket);
                _currentAnalysisScoreBucket = null;
            }

            return buckets;
        }

        private void PublishAnalysisScoreBucket(AnalysisScoreBucket bucket)
        {
            if (bucket == null || bucket.Count <= 0)
                return;

            var points = new List<AnalysisScorePoint>();

            if (Math.Abs(bucket.MinScore - bucket.CloseScore) > 0.0001)
            {
                AddUniqueAnalysisScorePoint(points, CreateBucketScorePoint(
                    bucket,
                    bucket.MinFeature,
                    bucket.MinSignal,
                    bucket.MinScore,
                    bucket.BucketTime.AddMilliseconds(100),
                    "RANGE_MIN"));
            }

            if (Math.Abs(bucket.MaxScore - bucket.CloseScore) > 0.0001)
            {
                AddUniqueAnalysisScorePoint(points, CreateBucketScorePoint(
                    bucket,
                    bucket.MaxFeature,
                    bucket.MaxSignal,
                    bucket.MaxScore,
                    bucket.BucketTime.AddMilliseconds(500),
                    "RANGE_MAX"));
            }

            if (bucket.PeakSignal != null &&
                !IsScorePointAlreadyIncluded(points, bucket.PeakScore, bucket.PeakSignal.Action.ToString()))
            {
                AddUniqueAnalysisScorePoint(points, CreateBucketScorePoint(
                    bucket,
                    bucket.PeakFeature,
                    bucket.PeakSignal,
                    bucket.PeakScore,
                    bucket.BucketTime.AddMilliseconds(750),
                    "PEAK_ACTION"));
            }

            AddUniqueAnalysisScorePoint(points, CreateBucketScorePoint(
                bucket,
                bucket.CloseFeature,
                bucket.CloseSignal,
                bucket.CloseScore,
                bucket.BucketTime.AddMilliseconds(999),
                "CLOSE"));

            foreach (var point in points)
                PublishAnalysisScorePoint(point);
        }

        private static AnalysisScorePoint CreateBucketScorePoint(
            AnalysisScoreBucket bucket,
            FeatureSnapshot feature,
            TradeSignal signal,
            double score,
            DateTime time,
            string pointType)
        {
            string action = signal == null ? "HOLD" : signal.Action.ToString();
            string strategy = string.IsNullOrWhiteSpace(signal?.StrategyName) ? "-" : signal.StrategyName;

            return new AnalysisScorePoint
            {
                Time = time,
                Code = feature != null && !string.IsNullOrWhiteSpace(feature.Code)
                    ? feature.Code
                    : bucket == null ? string.Empty : bucket.Code ?? string.Empty,
                Price = feature != null ? feature.Price : bucket == null ? 0 : bucket.Price,
                SignalScore = score,
                Action = action,
                StrategyName = strategy,
                Reason = BuildAnalysisScorePointReason(feature, signal, score, pointType, bucket)
            };
        }

        private static void AddUniqueAnalysisScorePoint(List<AnalysisScorePoint> points, AnalysisScorePoint point)
        {
            if (points == null || point == null)
                return;

            foreach (var existing in points)
            {
                if (existing == null)
                    continue;

                if (Math.Abs(existing.SignalScore - point.SignalScore) < 0.0001 &&
                    string.Equals(existing.Action, point.Action, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            points.Add(point);
        }

        private static bool IsScorePointAlreadyIncluded(List<AnalysisScorePoint> points, double score, string action)
        {
            if (points == null)
                return false;

            foreach (var point in points)
            {
                if (point == null)
                    continue;

                if (Math.Abs(point.SignalScore - score) < 0.0001 &&
                    string.Equals(point.Action, action, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private bool ShouldUpdateAnalysisCard(FeatureSnapshot feature, TradeSignal signal)
        {
            if (!IsReplayMlDataSource())
                return true;

            if (signal != null && signal.Action != TradeActionType.Hold)
                return true;

            DateTime time = ResolveAnalysisDisplayTime(feature);
            if (_lastReplayAnalysisCardUpdateAt != DateTime.MinValue &&
                (time - _lastReplayAnalysisCardUpdateAt).TotalMilliseconds < 1000)
                return false;

            _lastReplayAnalysisCardUpdateAt = time;
            return true;
        }

        private bool ShouldCreateReplayOutcomeLabel(FeatureSnapshotRecord record)
        {
            if (!IsReplayMlDataSource())
                return true;

            if (IsBuySignalSnapshot(record))
                return true;

            DateTime time = record == null || record.SnapshotTime == default(DateTime)
                ? DateTime.Now
                : record.SnapshotTime;

            if (_lastReplayOutcomeLabelAt != DateTime.MinValue &&
                (time - _lastReplayOutcomeLabelAt).TotalMilliseconds < 10000)
                return false;

            _lastReplayOutcomeLabelAt = time;
            return true;
        }

        private void TryCancelBlockingUnfilledOrderFromAnalysis(FeatureSnapshot feature)
        {
            // Replay 주문 체결/취소는 ReplayOrderSimulator가 replay tick 시간 기준으로 처리한다.
            // 여기서 DateTime.Now 기반 실거래 미체결 차단을 같이 돌리면
            // Replay에서는 매수 직후 곧바로 매수취소가 나갈 수 있다.
            if (IsReplayMode)
                return;

            if (feature == null || string.IsNullOrWhiteSpace(feature.Code))
                return;

            if (feature.OpenOrderCount <= 0 &&
                !feature.HasPendingBuy &&
                !feature.HasPendingSell)
                return;

            if (!feature.HasPendingBuy && feature.HasPendingSell)
            {
                Application.Current?.Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() =>
                    {
                        TrySchedulePendingSellReplaceFromAnalysisOnUi(feature.Code);
                    }));
                return;
            }

            Application.Current?.Dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(() =>
                {
                    TryCancelBlockingUnfilledOrderFromAnalysisOnUi(feature.Code);
                }));
        }

        private void TryCancelBlockingUnfilledOrderFromAnalysisOnUi(string code)
        {
            if (IsReplayMode)
                return;

            if (string.IsNullOrWhiteSpace(code))
                return;

            var now = DateTime.Now;
            var orders = OrderHistory?.ToList();

            if (orders == null || orders.Count == 0)
                return;

            OrderInfo order = null;
            DateTime latestOrderTime = DateTime.MinValue;

            foreach (var item in orders)
            {
                if (item == null ||
                    item.Code != code ||
                    string.IsNullOrWhiteSpace(item.OrderNo) ||
                    !IsBuyOrder(item) ||
                    GetRemainingOrderQty(item) <= 0)
                    continue;

                DateTime parsedOrderTime = ParseOrderTime(item.OrderTime);
                if ((now - parsedOrderTime).TotalSeconds < 3)
                    continue;

                if (order == null || parsedOrderTime > latestOrderTime)
                {
                    order = item;
                    latestOrderTime = parsedOrderTime;
                }
            }

            if (order == null)
                return;

            string key = "ANALYSIS_CANCEL_" + order.OrderNo;

            if (!_autoCancelWatchMap.TryAdd(key, true))
                return;

            RequestCancelOrder(order, "자동매매 분석 중 미체결 차단");

            Task.Run(async () =>
            {
                await Task.Delay(3000);
                bool removed;
                _autoCancelWatchMap.TryRemove(key, out removed);
            });
        }

        private void PersistAutoTradeFeatureSnapshotIfNeeded(FeatureSnapshot feature, TradeSignal signal)
        {
            try
            {
                if (feature == null || string.IsNullOrWhiteSpace(feature.Code) || Db == null)
                    return;

                if (_autoTradeSettings == null || !_autoTradeSettings.EnableMlDataCollection)
                    return;

                DateTime snapshotTime = feature.Time == default(DateTime)
                    ? DateTime.Now
                    : feature.Time;

                // ML 학습용 FeatureSnapshot은 실제 매매 여부와 무관하게 모든 분석 tick마다 저장한다.
                // FeatureSnapshotIntervalSeconds는 화면/로그 완화용으로만 남기고, ML 라벨 후보 생성에는 사용하지 않는다.
                _lastFeatureSnapshotSavedAtByCode[feature.Code] = snapshotTime;

                var record = CreateFeatureSnapshotRecord(feature, signal, snapshotTime);
                if (record == null)
                    return;

                Db.EnqueueFeatureSnapshot(record);
                ScheduleSnapshotOutcomeLabel(record);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[자동매매 FeatureSnapshot 저장큐 등록 실패] " + ex.Message);
            }
        }

        private FeatureSnapshotRecord CreateFeatureSnapshotRecord(
            FeatureSnapshot feature,
            TradeSignal signal,
            DateTime snapshotTime)
        {
            if (feature == null)
                return null;

            double buyValue = Math.Max(0, feature.BuyDollarBarTradeValue);
            double sellValue = Math.Max(0, feature.SellDollarBarTradeValue);
            double totalValue = buyValue + sellValue;
            double netBuyValue = buyValue - sellValue;
            double netBuyRatio = totalValue <= 0 ? 0 : netBuyValue / totalValue;

            // BarMinimal 이후 ML 저장 레코드는 기존 DB 컬럼 호환만 유지하고,
            // 원천 값은 DollarBar/BarState 축으로만 채운다.
            return new FeatureSnapshotRecord
            {
                SnapshotId = feature.SnapshotId,
                Code = feature.Code,
                SnapshotTime = snapshotTime,
                Price = feature.Price,
                PriceChangeRate = feature.DollarBarChangeRate,
                Strength = feature.BarCurrentScore,
                StrengthDelta = feature.BarTrendFilterScore,
                VolumeAccel = feature.DollarBarSpeed,
                BidAskRatio = feature.BuySellDollarValueRatio,
                BuyPressureScore = feature.BarBuyPressureScore,
                BuyVolume = buyValue,
                SellVolume = sellValue,
                BuyVolumeRatio = feature.BuyDollarBarFillRatio,
                SellVolumeRatio = feature.SellDollarBarFillRatio,
                NetBuyVolume = netBuyValue,
                NetBuyVolumeRatio = netBuyRatio,
                FrameChange30s = 0,
                FrameChange1m = 0,
                FrameChange3m = 0,
                FrameChange5m = 0,
                FrameVolumeRatio = feature.DollarBarFillRatio,
                FrameBuyPressureScore = feature.BarBuyPressureScore,
                FrameStrengthDelta = 0,
                MinuteChange15m = 0,
                MinuteChange30m = 0,
                MinuteChange60m = 0,
                TodayRangePosition = feature.DollarBarDisplayDPos,
                TwoDayRangePosition = feature.DollarBarDisplayDPos,
                MarketRiskScore = feature.MarketRiskScore,
                RelativeStrength = 0,
                HoldingQty = feature.HoldingQty,
                EntryPrice = feature.EntryPrice,
                ProfitRate = feature.ProfitRate,
                MaxProfitRate = feature.MaxProfitRate,
                MinProfitRate = feature.MinProfitRate,
                DrawdownRate = feature.DrawdownRate,
                HoldingSeconds = feature.HoldSeconds,
                EstimatedRoundTripCostRate = feature.EstimatedRoundTripCostRate,
                NetProfitRate = feature.NetProfitRate,
                NetMaxProfitRate = feature.NetMaxProfitRate,
                NetDrawdownRate = feature.NetDrawdownRate,
                SecondTrend = feature.BarPhase.ToString(),
                MinuteTrend = feature.BarState.ToString(),
                HourTrend = feature.MarketTrend.ToString(),
                Phase = feature.BarState.ToString(),
                IsSideways = feature.BarState == BarState.SidewaysHold,
                TrendStrength = feature.BarTrendFilterScore,
                VolatilityRate = Math.Abs(feature.DollarBarChangeRate),
                RangePosition = feature.DollarBarDisplayDPos,
                TickCount5Min = feature.DollarBarTickCount,
                SecondCandleCount = feature.DollarBarCount,
                MinuteCandleCount = feature.DollarBarCount,
                SignalAction = signal == null ? "HOLD" : signal.Action.ToString(),
                SignalScore = signal == null ? 0 : signal.Score,
                StrategyName = signal == null ? "HOLD" : signal.StrategyName,
                LocalMlUsed = false,
                LocalMlAvailable = false,
                LocalMlAppliedToTrading = false,
                LocalMlScore = 0,
                LocalMlConfidence = 0,
                LocalMlInfluenceRatio = 0,
                LocalMlDirection = null,
                LocalMlReason = null,
                TradeMode = feature.BarPhase.ToString(),
                DataSourceType = GetMlDataSourceType(),
                IsReplay = IsReplayMlDataSource(),
                MlSessionId = BuildMlSessionId(feature.Code, snapshotTime),
                CreatedAt = DateTime.Now
            };
        }

        private void ScheduleSnapshotOutcomeLabel(FeatureSnapshotRecord record)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.Code) || string.IsNullOrWhiteSpace(record.SnapshotId))
                return;

            if (Db == null || _autoTradeSettings == null || !_autoTradeSettings.EnableMlDataCollection)
                return;

            if (record.Price <= 0)
                return;

            if (!ShouldCreateReplayOutcomeLabel(record))
                return;

            if (!_outcomeLabelWatchMap.TryAdd(record.SnapshotId, true))
                return;

            int horizonSeconds = Math.Max(30, _autoTradeSettings.OutcomeLabelMaxHorizonSeconds);
            CancellationToken token = _outcomeLabelCts.Token;

            if (IsReplayMlDataSource())
            {
                try
                {
                    if (token.IsCancellationRequested)
                        return;

                    var label = CreateSnapshotOutcomeLabel(record, horizonSeconds);
                    if (label != null && Db != null)
                        Db.EnqueueSnapshotOutcomeLabel(label);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[자동매매 Replay OutcomeLabel 생성 실패] " + ex.Message);
                }
                finally
                {
                    bool removed;
                    _outcomeLabelWatchMap.TryRemove(record.SnapshotId, out removed);
                }

                return;
            }

            _pendingFeatureSnapshotOutcomeMap[record.SnapshotId] = record;
        }

        private bool IsBuySignalSnapshot(FeatureSnapshotRecord record)
        {
            return record != null &&
                   string.Equals(record.SignalAction, TradeActionType.Buy.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private SnapshotOutcomeLabelRecord CreateSnapshotOutcomeLabel(FeatureSnapshotRecord record, int horizonSeconds)
        {
            if (record == null || record.Price <= 0)
                return null;

            DateTime snapshotTime = record.SnapshotTime == default(DateTime)
                ? DateTime.Now
                : record.SnapshotTime;

            string action = string.IsNullOrWhiteSpace(record.SignalAction) ? TradeActionType.Hold.ToString() : record.SignalAction;

            return CreateSnapshotOutcomeLabelCore(
                record.SnapshotId,
                null,
                record.Code,
                snapshotTime,
                record.Price,
                action,
                action,
                record.DataSourceType,
                record.IsReplay,
                record.MlSessionId,
                horizonSeconds);
        }

        private SnapshotOutcomeLabelRecord CreateDecisionCandidateOutcomeLabel(DecisionCandidateRecord candidate, int horizonSeconds)
        {
            if (candidate == null || candidate.Price <= 0)
                return null;

            DateTime decisionTime = candidate.DecisionTime == default(DateTime)
                ? DateTime.Now
                : candidate.DecisionTime;

            return CreateSnapshotOutcomeLabelCore(
                candidate.SnapshotId,
                candidate.CandidateId,
                candidate.Code,
                decisionTime,
                candidate.Price,
                candidate.ActionCandidate,
                candidate.FinalAction,
                candidate.DataSourceType,
                candidate.IsReplay,
                candidate.MlSessionId,
                horizonSeconds);
        }

        private SnapshotOutcomeLabelRecord CreateSnapshotOutcomeLabelCore(
            string snapshotId,
            string candidateId,
            string code,
            DateTime snapshotTime,
            double entryPrice,
            string actionCandidate,
            string finalAction,
            string dataSourceType,
            bool isReplay,
            string mlSessionId,
            int horizonSeconds)
        {
            if (string.IsNullOrWhiteSpace(code) || entryPrice <= 0)
                return null;

            if (snapshotTime == default(DateTime))
                snapshotTime = DateTime.Now;

            DateTime labelTime = snapshotTime.AddSeconds(horizonSeconds);
            var points = GetMinuteCandlesForAutoTrade(code)
                .Where(x =>
                    x != null &&
                    x.LastUpdated > snapshotTime &&
                    x.LastUpdated <= labelTime.AddSeconds(5) &&
                    x.LastPrice > 0)
                .ToList();

            if (points.Count == 0)
                return null;

            double return30s = CalculateReturnRate(entryPrice, ResolveForwardPrice(points, snapshotTime.AddSeconds(30)));
            double return1m = CalculateReturnRate(entryPrice, ResolveForwardPrice(points, snapshotTime.AddMinutes(1)));
            double return3m = CalculateReturnRate(entryPrice, ResolveForwardPrice(points, snapshotTime.AddMinutes(3)));
            double return5m = CalculateReturnRate(entryPrice, ResolveForwardPrice(points, snapshotTime.AddMinutes(5)));
            double return10m = CalculateReturnRate(entryPrice, ResolveForwardPrice(points, snapshotTime.AddMinutes(10)));

            double estimatedRoundTripCostRate = GetMoneyCreatorEstimatedRoundTripCostPercent();

            double netReturn30s = return30s - estimatedRoundTripCostRate;
            double netReturn1m = return1m - estimatedRoundTripCostRate;
            double netReturn3m = return3m - estimatedRoundTripCostRate;
            double netReturn5m = return5m - estimatedRoundTripCostRate;
            double netReturn10m = return10m - estimatedRoundTripCostRate;

            double maxProfit = 0;
            double maxLoss = 0;
            double? profitHitSeconds = null;
            double? stopHitSeconds = null;

            double profitTargetRate = GetOutcomeProfitTargetRate();
            double stopTargetRate = GetOutcomeStopTargetRate();

            foreach (var point in points)
            {
                double highPrice = point.HighPrice > 0 ? point.HighPrice : point.LastPrice;
                double lowPrice = point.LowPrice > 0 ? point.LowPrice : point.LastPrice;

                double highRate = CalculateReturnRate(entryPrice, highPrice);
                double lowRate = CalculateReturnRate(entryPrice, lowPrice);

                if (highRate > maxProfit)
                    maxProfit = highRate;

                if (lowRate < maxLoss)
                    maxLoss = lowRate;

                double seconds = Math.Max(0, (point.LastUpdated - snapshotTime).TotalSeconds);

                // Profit/Stop hit timing must follow the candidate action direction.
                // Buy-like candidates profit on high movement, sell-like candidates profit on low movement.
                // The previous buy-oriented hit logic could make Sell/Reversal labels look better or worse than reality.
                double actionHighRate = CalculateActionAdjustedReturn(highRate, actionCandidate, estimatedRoundTripCostRate);
                double actionLowRate = CalculateActionAdjustedReturn(lowRate, actionCandidate, estimatedRoundTripCostRate);
                double actionBestRate = Math.Max(actionHighRate, actionLowRate);
                double actionWorstRate = Math.Min(actionHighRate, actionLowRate);

                if (!profitHitSeconds.HasValue && actionBestRate >= profitTargetRate)
                    profitHitSeconds = seconds;

                if (!stopHitSeconds.HasValue && actionWorstRate <= -stopTargetRate)
                    stopHitSeconds = seconds;
            }

            bool hitProfitFirst = profitHitSeconds.HasValue &&
                                  (!stopHitSeconds.HasValue || profitHitSeconds.Value <= stopHitSeconds.Value);
            bool hitStopFirst = stopHitSeconds.HasValue &&
                                (!profitHitSeconds.HasValue || stopHitSeconds.Value < profitHitSeconds.Value);

            double netMaxProfit = maxProfit - estimatedRoundTripCostRate;
            double netMaxLoss = maxLoss - estimatedRoundTripCostRate;

            int labelBuyGood = 0;
            int labelAvoid = 0;

            if ((hitProfitFirst || (netMaxProfit >= profitTargetRate && netMaxLoss > -stopTargetRate)) && !hitStopFirst)
                labelBuyGood = 1;

            if (hitStopFirst || netMaxLoss <= -stopTargetRate || netReturn10m <= -Math.Max(0.20, profitTargetRate * 0.8))
                labelAvoid = 1;

            if (labelBuyGood == 1 && labelAvoid == 1)
            {
                if (hitProfitFirst && !hitStopFirst)
                    labelAvoid = 0;
                else
                    labelBuyGood = 0;
            }

            double actionReturn30s = CalculateActionAdjustedReturn(return30s, actionCandidate, estimatedRoundTripCostRate);
            double actionReturn1m = CalculateActionAdjustedReturn(return1m, actionCandidate, estimatedRoundTripCostRate);
            double actionReturn3m = CalculateActionAdjustedReturn(return3m, actionCandidate, estimatedRoundTripCostRate);
            double actionReturn5m = CalculateActionAdjustedReturn(return5m, actionCandidate, estimatedRoundTripCostRate);
            double actionReturn10m = CalculateActionAdjustedReturn(return10m, actionCandidate, estimatedRoundTripCostRate);
            double actionMfe = CalculateActionAdjustedMfe(maxProfit, maxLoss, actionCandidate, estimatedRoundTripCostRate);
            double actionMae = CalculateActionAdjustedMae(maxProfit, maxLoss, actionCandidate, estimatedRoundTripCostRate);
            double costAdjustedOutcome = Math.Max(actionReturn3m, Math.Max(actionReturn5m, Math.Max(actionReturn10m, actionMfe)));

            // LabelBuyGood/LabelAvoid도 Action 기준으로 다시 확정한다.
            // CostAdjustedOutcome은 MFE 성격이 강하므로, 실전성 판단에는 hit 순서와 ActionMae를 같이 본다.
            labelBuyGood = 0;
            labelAvoid = 0;

            if ((hitProfitFirst || (actionMfe >= profitTargetRate && actionMae > -stopTargetRate)) && !hitStopFirst)
                labelBuyGood = 1;

            if (hitStopFirst || actionMae <= -stopTargetRate || actionReturn10m <= -Math.Max(0.20, profitTargetRate * 0.8))
                labelAvoid = 1;

            if (labelBuyGood == 1 && labelAvoid == 1)
            {
                if (hitProfitFirst && !hitStopFirst)
                    labelAvoid = 0;
                else
                    labelBuyGood = 0;
            }

            return new SnapshotOutcomeLabelRecord
            {
                OutcomeId = Guid.NewGuid().ToString("N"),
                SnapshotId = snapshotId,
                CandidateId = candidateId,
                Code = code,
                SnapshotTime = snapshotTime,
                LabelTime = labelTime,
                ActionCandidate = actionCandidate,
                FinalAction = finalAction,
                ReturnAfter30s = return30s,
                ReturnAfter1m = return1m,
                ReturnAfter3m = return3m,
                ReturnAfter5m = return5m,
                ReturnAfter10m = return10m,
                MaxProfitAfter10m = maxProfit,
                MaxLossAfter10m = maxLoss,
                EstimatedRoundTripCostRate = estimatedRoundTripCostRate,
                NetReturnAfter30s = netReturn30s,
                NetReturnAfter1m = netReturn1m,
                NetReturnAfter3m = netReturn3m,
                NetReturnAfter5m = netReturn5m,
                NetReturnAfter10m = netReturn10m,
                NetMaxProfitAfter10m = netMaxProfit,
                NetMaxLossAfter10m = netMaxLoss,
                ActionReturnAfter30s = actionReturn30s,
                ActionReturnAfter1m = actionReturn1m,
                ActionReturnAfter3m = actionReturn3m,
                ActionReturnAfter5m = actionReturn5m,
                ActionReturnAfter10m = actionReturn10m,
                ActionMfeAfter10m = actionMfe,
                ActionMaeAfter10m = actionMae,
                CostAdjustedOutcome = costAdjustedOutcome,
                HitProfitFirst = hitProfitFirst,
                HitStopFirst = hitStopFirst,
                ProfitHitSeconds = profitHitSeconds,
                StopHitSeconds = stopHitSeconds,
                LabelBuyGood = labelBuyGood,
                LabelAvoid = labelAvoid,
                DataSourceType = dataSourceType,
                IsReplay = isReplay,
                MlSessionId = mlSessionId,
                CreatedAt = DateTime.Now
            };
        }

        private static double CalculateActionAdjustedReturn(double priceReturnRate, string actionCandidate, double estimatedRoundTripCostRate)
        {
            if (IsSellLikeActionCandidate(actionCandidate))
                return -priceReturnRate - estimatedRoundTripCostRate;

            if (IsBuyLikeActionCandidate(actionCandidate))
                return priceReturnRate - estimatedRoundTripCostRate;

            return priceReturnRate;
        }

        private static double CalculateActionAdjustedMfe(double maxProfit, double maxLoss, string actionCandidate, double estimatedRoundTripCostRate)
        {
            if (IsSellLikeActionCandidate(actionCandidate))
                return -maxLoss - estimatedRoundTripCostRate;

            if (IsBuyLikeActionCandidate(actionCandidate))
                return maxProfit - estimatedRoundTripCostRate;

            return maxProfit;
        }

        private static double CalculateActionAdjustedMae(double maxProfit, double maxLoss, string actionCandidate, double estimatedRoundTripCostRate)
        {
            if (IsSellLikeActionCandidate(actionCandidate))
                return -maxProfit - estimatedRoundTripCostRate;

            if (IsBuyLikeActionCandidate(actionCandidate))
                return maxLoss - estimatedRoundTripCostRate;

            return maxLoss;
        }

        private static bool IsBuyLikeActionCandidate(string actionCandidate)
        {
            if (string.IsNullOrWhiteSpace(actionCandidate))
                return false;

            return string.Equals(actionCandidate, TradeActionType.Buy.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSellLikeActionCandidate(string actionCandidate)
        {
            if (string.IsNullOrWhiteSpace(actionCandidate))
                return false;

            return string.Equals(actionCandidate, TradeActionType.Sell.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private double GetOutcomeProfitTargetRate()
        {
            double stopRate = _autoTradeSettings == null ? 1.0 : Math.Max(0.1, _autoTradeSettings.StopLossRate);
            return Math.Max(0.25, Math.Min(1.0, stopRate * 0.5));
        }

        private double GetOutcomeStopTargetRate()
        {
            double stopRate = _autoTradeSettings == null ? 1.0 : Math.Max(0.1, _autoTradeSettings.StopLossRate);
            return Math.Max(0.50, stopRate);
        }

        private static double ResolveForwardPrice(List<TrackedSymbol> points, DateTime targetTime)
        {
            if (points == null || points.Count == 0)
                return 0;

            TrackedSymbol after = null;
            TrackedSymbol before = null;

            foreach (var point in points)
            {
                if (point == null || point.LastPrice <= 0)
                    continue;

                if (point.LastUpdated >= targetTime)
                {
                    if (after == null || point.LastUpdated < after.LastUpdated)
                        after = point;
                }
                else if (point.LastUpdated <= targetTime)
                {
                    if (before == null || point.LastUpdated > before.LastUpdated)
                        before = point;
                }
            }

            if (after != null)
                return after.LastPrice;

            return before == null ? 0 : before.LastPrice;
        }

        private static double CalculateReturnRate(double entryPrice, double exitPrice)
        {
            if (entryPrice <= 0 || exitPrice <= 0)
                return 0;

            return ((exitPrice - entryPrice) / entryPrice) * 100.0;
        }

        private string PersistAutoTradeDecisionCandidateIfNeeded(FeatureSnapshot feature, TradeSignal signal, string signalId)
        {
            try
            {
                if (feature == null || string.IsNullOrWhiteSpace(feature.Code) || Db == null)
                    return null;

                if (_autoTradeSettings == null || !_autoTradeSettings.EnableMlDataCollection)
                    return null;

                DateTime decisionTime = feature.Time == default(DateTime)
                    ? DateTime.Now
                    : feature.Time;

                var candidate = CreateDecisionCandidateRecord(feature, signal, decisionTime, signalId);
                if (candidate == null)
                    return null;

                if (ShouldSkipDuplicateAnalysisWatchCandidate(candidate))
                    return null;

                Db.EnqueueDecisionCandidate(candidate);
                ScheduleDecisionCandidateOutcomeLabel(candidate);
                return candidate.CandidateId;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[자동매매 DecisionCandidate 저장큐 등록 실패] " + ex.Message);
                return null;
            }
        }

        private DecisionCandidateRecord CreateDecisionCandidateRecord(
            FeatureSnapshot feature,
            TradeSignal signal,
            DateTime decisionTime,
            string signalId)
        {
            if (feature == null || string.IsNullOrWhiteSpace(feature.Code))
                return null;

            string finalAction = NormalizeTradeActionName(signal == null ? feature.SignalAction : signal.Action.ToString());
            string reason = signal == null ? feature.SignalReason : signal.Reason;
            string actionCandidate = ResolveDecisionActionCandidate(finalAction, reason);
            string strategyName = ResolveDecisionCandidateStrategyName(signal, actionCandidate, finalAction, reason);

            // AutoTrading(3)(65) 이후에는 SignalEngine의 점수분해값이
            // TradeSignal.ScoreBreakdown으로 구조화되어 넘어온다.
            // 우선 구조화값을 저장하고, 구버전 AutoTrading/Guard 로그처럼 구조화값이 없는 경우에만
            // 기존 Reason 문자열 파싱으로 fallback한다.
            SignalScoreBreakdown structuredBreakdown = signal == null ? null : signal.ScoreBreakdown;
            var fallbackBreakdown = structuredBreakdown == null ? ParseScoreBreakdown(reason) : null;

            return new DecisionCandidateRecord
            {
                CandidateId = Guid.NewGuid().ToString("N"),
                SnapshotId = feature.SnapshotId,
                SignalId = signalId,
                Code = feature.Code,
                DecisionTime = decisionTime,
                Price = feature.Price,
                ActionCandidate = actionCandidate,
                FinalAction = finalAction,
                SignalScore = signal == null ? 0 : signal.Score,
                StrategyName = strategyName,
                TradeMode = feature.BarPhase.ToString(),
                BaseFinal = ReadBreakdownValue(structuredBreakdown, fallbackBreakdown, "Base"),
                EntryScore = ReadBreakdownValue(structuredBreakdown, fallbackBreakdown, "Entry"),
                ExitScore = ReadBreakdownValue(structuredBreakdown, fallbackBreakdown, "Exit"),
                ExpectedMoveScore = ReadBreakdownValue(structuredBreakdown, fallbackBreakdown, "Expected"),
                WeakFlowPenalty = ReadBreakdownValue(structuredBreakdown, fallbackBreakdown, "WeakFlowPenalty"),
                HighChasePenalty = ReadBreakdownValue(structuredBreakdown, fallbackBreakdown, "HighChasePenalty"),
                CostRecoveryHoldScore = ReadBreakdownValue(structuredBreakdown, fallbackBreakdown, "CostRecoveryHold"),
                AdversePersist = ReadBreakdownValue(structuredBreakdown, fallbackBreakdown, "AdversePersist"),
                EffectiveCostRate = ReadBreakdownValue(structuredBreakdown, fallbackBreakdown, "EffectiveCost"),
                Reason = reason,
                DataSourceType = GetMlDataSourceType(),
                IsReplay = IsReplayMlDataSource(),
                MlSessionId = BuildMlSessionId(feature.Code, decisionTime),
                CreatedAt = DateTime.Now
            };
        }


        private static string ResolveDecisionActionCandidate(string finalAction, string reason)
        {
            // DecisionCandidate는 실제 FinalAction 품질 검증용으로만 저장한다.
            // Reason 문자열 안의 관찰값(Reversal/Trend/Profit/Watch/Strong 등)을
            // Buy/Sell 후보로 재승격하지 않는다.
            return NormalizeTradeActionName(finalAction);
        }

        private static string ResolveDecisionCandidateStrategyName(
            TradeSignal signal,
            string actionCandidate,
            string finalAction,
            string reason)
        {
            string normalizedFinalAction = NormalizeTradeActionName(finalAction);
            string strategyName = signal == null ? null : signal.StrategyName;

            if (IsCaseObservationStrategyName(strategyName))
                return BuildFinalDecisionStrategyName(normalizedFinalAction);

            if (string.IsNullOrWhiteSpace(strategyName))
                return BuildFinalDecisionStrategyName(normalizedFinalAction);

            return strategyName.Trim();
        }

        private static string BuildFinalDecisionStrategyName(string finalAction)
        {
            string normalized = NormalizeTradeActionName(finalAction);

            if (string.Equals(normalized, TradeActionType.Buy.ToString(), StringComparison.OrdinalIgnoreCase))
                return "FinalDecisionBuy";

            if (string.Equals(normalized, TradeActionType.Sell.ToString(), StringComparison.OrdinalIgnoreCase))
                return "FinalDecisionSell";

            return "FinalDecisionHold";
        }

        private static string NormalizeTradeActionName(string action)
        {
            if (string.Equals(action, TradeActionType.Buy.ToString(), StringComparison.OrdinalIgnoreCase))
                return TradeActionType.Buy.ToString();

            if (string.Equals(action, TradeActionType.Sell.ToString(), StringComparison.OrdinalIgnoreCase))
                return TradeActionType.Sell.ToString();

            return TradeActionType.Hold.ToString();
        }

        private static bool IsCaseObservationStrategyName(string strategyName)
        {
            if (string.IsNullOrWhiteSpace(strategyName))
                return false;

            // 아래 이름들은 검증 전 관찰/후보 케이스다.
            // DB의 공식 DecisionCandidate.StrategyName으로 저장하면
            // 실제 FinalAction 품질과 후보 품질이 다시 섞이므로 FinalDecision*으로 강등한다.
            string value = strategyName.Trim();
            return ContainsIgnoreCase(value, "Reversal") ||
                   ContainsIgnoreCase(value, "TrendBuy") ||
                   ContainsIgnoreCase(value, "ProfitExhaustion") ||
                   ContainsIgnoreCase(value, "ProfitProtect") ||
                   ContainsIgnoreCase(value, "WeakSell") ||
                   ContainsIgnoreCase(value, "StrongPlus") ||
                   ContainsIgnoreCase(value, "Strong") ||
                   ContainsIgnoreCase(value, "Watch");
        }

        private static string ExtractReasonToken(string reason, string key)
        {
            if (string.IsNullOrWhiteSpace(reason) || string.IsNullOrWhiteSpace(key))
                return string.Empty;

            var match = Regex.Match(
                reason,
                Regex.Escape(key) + @"[:=]([^,/| ]+)",
                RegexOptions.IgnoreCase);

            return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
        }

        private static double ExtractReasonDouble(string reason, string key)
        {
            string token = ExtractReasonToken(reason, key);
            double value;
            return double.TryParse(token, NumberStyles.Any, CultureInfo.InvariantCulture, out value)
                ? value
                : 0.0;
        }

        private bool ShouldSkipDuplicateAnalysisWatchCandidate(DecisionCandidateRecord candidate)
        {
            // 별도 Watch 후보를 Buy/Sell Candidate로 저장하지 않으므로 후보 중복 제거도 적용하지 않는다.
            return false;
        }

        private void ScheduleDecisionCandidateOutcomeLabel(DecisionCandidateRecord candidate)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.CandidateId) || string.IsNullOrWhiteSpace(candidate.Code))
                return;

            if (Db == null || _autoTradeSettings == null || !_autoTradeSettings.EnableMlDataCollection)
                return;

            if (candidate.Price <= 0 || !ShouldCreateDecisionCandidateOutcomeLabel(candidate))
                return;

            if (!_outcomeLabelWatchMap.TryAdd(candidate.CandidateId, true))
                return;

            int horizonSeconds = Math.Max(30, _autoTradeSettings.OutcomeLabelMaxHorizonSeconds);

            if (IsReplayMlDataSource())
            {
                try
                {
                    var label = CreateDecisionCandidateOutcomeLabel(candidate, horizonSeconds);
                    if (label != null && Db != null)
                        Db.EnqueueSnapshotOutcomeLabel(label);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[자동매매 Replay DecisionCandidate OutcomeLabel 생성 실패] " + ex.Message);
                }
                finally
                {
                    bool removed;
                    _outcomeLabelWatchMap.TryRemove(candidate.CandidateId, out removed);
                }

                return;
            }

            _pendingDecisionCandidateOutcomeMap[candidate.CandidateId] = candidate;
        }

        private bool ShouldCreateDecisionCandidateOutcomeLabel(DecisionCandidateRecord candidate)
        {
            if (candidate == null)
                return false;

            // DecisionCandidate OutcomeLabel은 Buy/Sell 품질을 Hold 기준선과 비교하기 위한 최종 분석 데이터다.
            // Replay에서는 미래 데이터가 이미 있으므로 Hold를 샘플링하면 기준선이 사라지고,
            // DecisionCandidates에는 Hold가 있는데 SnapshotOutcomeLabels 조인 결과에는 Hold가 없는 왜곡이 생긴다.
            // 따라서 DecisionCandidate 라벨은 Replay/Real 모두 후보 단위로 생성한다.
            // DB 폭증 완화용 샘플링은 FeatureSnapshot 라벨(ShouldCreateReplayOutcomeLabel)에만 유지한다.
            return true;
        }

        private void ProcessDueMlOutcomeLabels(DateTime now)
        {
            ProcessDueMlOutcomeLabels(now, false);
        }

        private void ProcessDueMlOutcomeLabels(DateTime now, bool forcePump)
        {
            ProcessDueFeatureSnapshotOutcomeLabels(now, forcePump);
            ProcessDueDecisionCandidateOutcomeLabels(now, forcePump);
        }

        private void ProcessDueFeatureSnapshotOutcomeLabels(DateTime now, bool forcePump)
        {
            if (IsReplayMlDataSource())
                return;

            if (Db == null || _autoTradeSettings == null || !_autoTradeSettings.EnableMlDataCollection)
                return;

            if (_pendingFeatureSnapshotOutcomeMap.IsEmpty)
                return;

            DateTime currentTime = now == default(DateTime) ? DateTime.Now : now;
            int horizonSeconds = Math.Max(30, _autoTradeSettings.OutcomeLabelMaxHorizonSeconds);
            int expirySeconds = horizonSeconds + OutcomeLabelRetryGraceSeconds;

            foreach (var pair in _pendingFeatureSnapshotOutcomeMap.ToArray())
            {
                var record = pair.Value;
                if (record == null)
                    continue;

                DateTime snapshotTime = record.SnapshotTime == default(DateTime)
                    ? currentTime
                    : record.SnapshotTime;

                if (currentTime < snapshotTime.AddSeconds(horizonSeconds))
                    continue;

                try
                {
                    var label = CreateSnapshotOutcomeLabel(record, horizonSeconds);
                    if (label != null && Db != null)
                    {
                        FeatureSnapshotRecord removedRecord;
                        _pendingFeatureSnapshotOutcomeMap.TryRemove(pair.Key, out removedRecord);
                        Db.EnqueueSnapshotOutcomeLabel(label);

                        bool removed;
                        _outcomeLabelWatchMap.TryRemove(pair.Key, out removed);
                    }
                    else if (forcePump || currentTime >= snapshotTime.AddSeconds(expirySeconds))
                    {
                        FeatureSnapshotRecord removedRecord;
                        _pendingFeatureSnapshotOutcomeMap.TryRemove(pair.Key, out removedRecord);

                        bool removed;
                        _outcomeLabelWatchMap.TryRemove(pair.Key, out removed);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[자동매매 FeatureSnapshot OutcomeLabel 생성 실패] " + ex.Message);
                }
            }
        }

        private void ProcessDueDecisionCandidateOutcomeLabels(DateTime now, bool forcePump)
        {
            if (IsReplayMlDataSource())
                return;

            if (Db == null || _autoTradeSettings == null || !_autoTradeSettings.EnableMlDataCollection)
                return;

            if (_pendingDecisionCandidateOutcomeMap.IsEmpty)
                return;

            DateTime currentTime = now == default(DateTime) ? DateTime.Now : now;
            if (!forcePump &&
                _lastDecisionCandidateOutcomePumpAt != DateTime.MinValue &&
                (currentTime - _lastDecisionCandidateOutcomePumpAt).TotalMilliseconds < 1000)
                return;

            _lastDecisionCandidateOutcomePumpAt = currentTime;
            int horizonSeconds = Math.Max(30, _autoTradeSettings.OutcomeLabelMaxHorizonSeconds);
            int expirySeconds = horizonSeconds + OutcomeLabelRetryGraceSeconds;

            foreach (var pair in _pendingDecisionCandidateOutcomeMap.ToArray())
            {
                var candidate = pair.Value;
                if (candidate == null)
                    continue;

                DateTime decisionTime = candidate.DecisionTime == default(DateTime)
                    ? currentTime
                    : candidate.DecisionTime;

                if (currentTime < decisionTime.AddSeconds(horizonSeconds))
                    continue;

                try
                {
                    var label = CreateDecisionCandidateOutcomeLabel(candidate, horizonSeconds);
                    if (label != null && Db != null)
                    {
                        DecisionCandidateRecord removedCandidate;
                        _pendingDecisionCandidateOutcomeMap.TryRemove(pair.Key, out removedCandidate);
                        Db.EnqueueSnapshotOutcomeLabel(label);

                        bool removed;
                        _outcomeLabelWatchMap.TryRemove(pair.Key, out removed);
                    }
                    else if (forcePump || currentTime >= decisionTime.AddSeconds(expirySeconds))
                    {
                        DecisionCandidateRecord removedCandidate;
                        _pendingDecisionCandidateOutcomeMap.TryRemove(pair.Key, out removedCandidate);

                        bool removed;
                        _outcomeLabelWatchMap.TryRemove(pair.Key, out removed);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[자동매매 DecisionCandidate OutcomeLabel 생성 실패] " + ex.Message);
                }
            }
        }

        private void StartDecisionCandidateOutcomePumpTimer()
        {
            if (_isDisposingOrDisposed)
                return;

            try
            {
                _decisionCandidateOutcomePumpTimer.Stop();
                _decisionCandidateOutcomePumpTimer.Tick -= DecisionCandidateOutcomePumpTimer_Tick;
                _decisionCandidateOutcomePumpTimer.Interval = TimeSpan.FromSeconds(DecisionCandidateOutcomePumpIntervalSeconds);
                _decisionCandidateOutcomePumpTimer.Tick += DecisionCandidateOutcomePumpTimer_Tick;
                _decisionCandidateOutcomePumpTimer.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DecisionCandidate Outcome 타이머 시작 실패] " + ex.Message);
            }
        }

        private void StopDecisionCandidateOutcomePumpTimer()
        {
            try
            {
                _decisionCandidateOutcomePumpTimer.Stop();
                _decisionCandidateOutcomePumpTimer.Tick -= DecisionCandidateOutcomePumpTimer_Tick;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DecisionCandidate Outcome 타이머 종료 실패] " + ex.Message);
            }
        }

        private void DecisionCandidateOutcomePumpTimer_Tick(object sender, EventArgs e)
        {
            if (_isDisposingOrDisposed)
                return;

            ProcessDueMlOutcomeLabels(DateTime.Now, false);
        }

        public void FlushAutoTradeMlAnalysisQueues(string reason)
        {
            try
            {
                if (IsReplayMode && IsReplayAutoPauseFlushReason(reason))
                {
                    AddAutoTradeAnalysisLog(
                        $"[Replay Auto Pause] {reason} / 일시정지 상태이므로 DB강제Flush와 라벨분석을 생략합니다.");
                    return;
                }

                ProcessDueMlOutcomeLabels(DateTime.Now, true);
                Db?.RequestFlushAllPending();
                AddAutoTradeAnalysisLog($"[ML 분석 Flush 요청] {reason} / DB저장=Worker / 라벨분석 리포트=저장 후 조회시점");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[자동매매 ML 분석데이터 Flush 실패] " + ex.Message);
            }
        }

        private static bool IsReplayAutoPauseFlushReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return false;

            return reason.IndexOf("Replay Auto OFF", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   reason.IndexOf("Replay Auto Pause", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   reason.IndexOf("Replay 일시정지", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void WriteDecisionOutcomeAnalysisReport(string reason)
        {
            if (Db == null || _autoTradeSettings == null || !_autoTradeSettings.EnableMlDataCollection)
                return;

            string code = Context == null ? null : Context.Code;
            if (string.IsNullOrWhiteSpace(code))
                return;

            WriteDecisionOutcomeAnalysisReport(
                reason,
                code,
                ResolveMlAnalysisReportDate(),
                IsReplayMlDataSource());
        }

        private void WriteDecisionOutcomeAnalysisReport(string reason, string code, DateTime tradingDate, bool replayOnly)
        {
            try
            {
                if (Db == null || _autoTradeSettings == null || !_autoTradeSettings.EnableMlDataCollection)
                    return;

                if (string.IsNullOrWhiteSpace(code))
                    return;

                DateTime safeTradingDate = tradingDate == default(DateTime)
                    ? DateTime.Now.Date
                    : tradingDate.Date;

                var rows = Db.LoadDecisionOutcomeAnalysisSummary(code, safeTradingDate, replayOnly);

                if (rows == null || rows.Count == 0)
                {
                    AddAutoTradeAnalysisLog(string.Format(
                        "[라벨분석] {0} / {1} / {2:yyyy-MM-dd} / 완료된 OutcomeLabel 없음",
                        string.IsNullOrWhiteSpace(reason) ? "Flush" : reason,
                        code,
                        safeTradingDate));
                    return;
                }

                int labeledCount = rows
                    .Where(x => string.Equals(x.CategoryName, "ALL", StringComparison.OrdinalIgnoreCase))
                    .Sum(x => x.CandidateCount);

                AddAutoTradeAnalysisLog(string.Format(
                    "[라벨분석] {0} / {1} / {2:yyyy-MM-dd} / Labeled={3:N0} / Source={4}",
                    string.IsNullOrWhiteSpace(reason) ? "Flush" : reason,
                    code,
                    safeTradingDate,
                    labeledCount,
                    replayOnly ? "Replay" : "Real"));

                foreach (var row in rows
                    .Where(x => string.Equals(x.CategoryName, "ALL", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.CandidateCount))
                {
                    AddAutoTradeAnalysisLog(FormatDecisionOutcomeAnalysisLine(row));
                }

            }
            catch (Exception ex)
            {
                Debug.WriteLine("[자동매매 라벨분석 리포트 생성 실패] " + ex.Message);
            }
        }

        private DateTime ResolveMlAnalysisReportDate()
        {
            try
            {
                if (IsReplayMode && _marketApi is LocalReplayApiService replayApi && replayApi.ReplayTradingDate != default(DateTime))
                    return replayApi.ReplayTradingDate.Date;
            }
            catch
            {
            }

            return DateTime.Now.Date;
        }



        private static string FormatDecisionOutcomeAnalysisLine(DecisionOutcomeAnalysisSummaryRecord row)
        {
            if (row == null)
                return "[라벨분석] -";

            return string.Format(
                CultureInfo.InvariantCulture,
                "[라벨분석] {0}/{1} N={2} Outcome={3:+0.00;-0.00;0.00}% Pos={4:0}% MFE={5:+0.00;-0.00;0.00}% MAE={6:+0.00;-0.00;0.00}% R1/3/5/10={7:+0.00;-0.00;0.00}/{8:+0.00;-0.00;0.00}/{9:+0.00;-0.00;0.00}/{10:+0.00;-0.00;0.00}% Score Final/Base/Entry/Exit={11:+0.0;-0.0;0.0}/{12:+0.0;-0.0;0.0}/{13:+0.0;-0.0;0.0}/{14:+0.0;-0.0;0.0} First=P:{15:0}%/S:{16:0}%",
                string.IsNullOrWhiteSpace(row.CategoryName) ? "ALL" : row.CategoryName,
                string.IsNullOrWhiteSpace(row.ActionCandidate) ? "Hold" : row.ActionCandidate,
                row.CandidateCount,
                row.AvgCostAdjustedOutcome,
                row.PositiveOutcomeRate * 100.0,
                row.AvgActionMfeAfter10m,
                row.AvgActionMaeAfter10m,
                row.AvgActionReturnAfter1m,
                row.AvgActionReturnAfter3m,
                row.AvgActionReturnAfter5m,
                row.AvgActionReturnAfter10m,
                row.AvgSignalScore,
                row.AvgBaseFinal,
                row.AvgEntryScore,
                row.AvgExitScore,
                row.ProfitFirstRate * 100.0,
                row.StopFirstRate * 100.0);
        }

        private static Dictionary<string, double> ParseScoreBreakdown(string reason)
        {
            var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(reason))
                return result;

            int idx = reason.IndexOf("ScoreBreakdown=", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return result;

            string tail = reason.Substring(idx + "ScoreBreakdown=".Length);
            int end = tail.IndexOf(" | ", StringComparison.Ordinal);
            if (end >= 0)
                tail = tail.Substring(0, end);

            foreach (var part in tail.Split('/'))
            {
                if (string.IsNullOrWhiteSpace(part))
                    continue;

                int colon = part.IndexOf(':');
                if (colon <= 0)
                    continue;

                string key = part.Substring(0, colon).Trim();
                string raw = part.Substring(colon + 1).Trim().Replace("+", string.Empty);
                double value;
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                    double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
                {
                    result[key] = value;
                }
            }

            return result;
        }

        private static double ReadBreakdownValue(
            SignalScoreBreakdown structuredBreakdown,
            Dictionary<string, double> fallbackBreakdown,
            string key)
        {
            if (structuredBreakdown != null)
            {
                if (string.Equals(key, "Base", StringComparison.OrdinalIgnoreCase))
                    return structuredBreakdown.BaseFinal;

                if (string.Equals(key, "Entry", StringComparison.OrdinalIgnoreCase))
                    return structuredBreakdown.EntryScore;

                if (string.Equals(key, "Exit", StringComparison.OrdinalIgnoreCase))
                    return structuredBreakdown.ExitScore;

                if (string.Equals(key, "Expected", StringComparison.OrdinalIgnoreCase))
                    return structuredBreakdown.ExpectedMoveScore;

                if (string.Equals(key, "WeakFlowPenalty", StringComparison.OrdinalIgnoreCase))
                    return structuredBreakdown.WeakFlowPenalty;

                if (string.Equals(key, "HighChasePenalty", StringComparison.OrdinalIgnoreCase))
                    return structuredBreakdown.HighChasePenalty;

                if (string.Equals(key, "CostRecoveryHold", StringComparison.OrdinalIgnoreCase))
                    return structuredBreakdown.CostRecoveryHoldScore;

                if (string.Equals(key, "AdversePersist", StringComparison.OrdinalIgnoreCase))
                    return structuredBreakdown.AdversePersist;

                if (string.Equals(key, "EffectiveCost", StringComparison.OrdinalIgnoreCase))
                    return structuredBreakdown.EffectiveCostRate;
            }

            return ReadBreakdownValue(fallbackBreakdown, key);
        }

        private static double ReadBreakdownValue(Dictionary<string, double> breakdown, string key)
        {
            if (breakdown == null || string.IsNullOrWhiteSpace(key))
                return 0;

            double value;
            return breakdown.TryGetValue(key, out value) ? value : 0;
        }

        private string PersistAutoTradeSignalLog(FeatureSnapshot feature, TradeSignal signal)
        {
            try
            {
                if (feature == null || string.IsNullOrWhiteSpace(feature.Code) || Db == null)
                    return null;

                DateTime signalTime = feature.Time == default(DateTime)
                    ? DateTime.Now
                    : feature.Time;

                return SaveSignalLogIfNeeded(feature, signal, signalTime);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[자동매매 신호로그 저장 실패] " + ex.Message);
                return null;
            }
        }

        private string SaveSignalLogIfNeeded(
            FeatureSnapshot feature,
            TradeSignal signal,
            DateTime signalTime)
        {
            if (!ShouldSaveSignalLog(signal))
                return null;

            var signalRecord = CreateSignalLogRecord(feature, signal, signalTime);
            Db.EnqueueSignalLog(signalRecord);

            // BarMinimal 단계에서는 LocalML 평가/저장 경로를 사용하지 않는다.
            return signalRecord.SignalId;
        }

        private bool ShouldSaveSignalLog(TradeSignal signal)
        {
            if (signal == null)
                return false;

            if (signal.Action != TradeActionType.Hold)
                return true;

            if (_autoTradeSettings != null && _autoTradeSettings.EnableMlDataCollection)
                return true;

            return _autoTradeSettings != null &&
                   _autoTradeSettings.AnalysisLogMode == AutoTradeAnalysisLogMode.All;
        }

        private SignalLogRecord CreateSignalLogRecord(
            FeatureSnapshot feature,
            TradeSignal signal,
            DateTime signalTime)
        {
            return new SignalLogRecord
            {
                SignalId = Guid.NewGuid().ToString("N"),
                Code = feature.Code,
                SignalTime = signalTime,
                Action = signal.Action.ToString(),
                Score = signal.Score,
                StrategyName = signal.StrategyName,
                Reason = signal.Reason,
                AiUsed = _autoTradeSettings != null && _autoTradeSettings.UseAi,
                AiApproved = false,
                AiReason = null,
                MlUsed = _autoTradeSettings != null && _autoTradeSettings.UseMlNet,
                MlApproved = !string.IsNullOrWhiteSpace(signal.TiRexDirection),
                MlScore = signal.TiRexConfidence,
                SnapshotId = feature.SnapshotId,
                TradeMode = feature.BarPhase.ToString(),
                TiRexDirection = signal.TiRexDirection,
                TiRexConfidence = signal.TiRexConfidence,
                TiRexPredictedReturn = signal.TiRexPredictedReturn,
                LocalMlUsed = false,
                LocalMlAvailable = false,
                LocalMlAppliedToTrading = false,
                LocalMlScore = 0,
                LocalMlConfidence = 0,
                LocalMlInfluenceRatio = 0,
                LocalMlDirection = null,
                LocalMlReason = null,
                DataSourceType = GetMlDataSourceType(),
                IsReplay = IsReplayMlDataSource(),
                MlSessionId = BuildMlSessionId(feature.Code, signalTime),
                CreatedAt = DateTime.Now
            };
        }

        private LocalMlEvaluationRecord CreateLocalMlEvaluationRecord(
            FeatureSnapshot feature,
            TradeSignal signal,
            DateTime signalTime,
            string signalId)
        {
            return new LocalMlEvaluationRecord
            {
                EvaluationId = Guid.NewGuid().ToString("N"),
                SnapshotId = feature == null ? null : feature.SnapshotId,
                SignalId = signalId,
                Code = feature == null ? signal?.Code : feature.Code,
                EvaluationTime = signalTime,
                ModelKey = "BAR_MINIMAL_DISABLED",
                ModelName = "BarMinimal",
                ModelVersion = null,
                EvaluatorType = "Disabled",
                FeatureSchemaVersion = "BarMinimal",
                RuntimeMode = "Disabled",
                IsEnabled = false,
                IsAvailable = false,
                IsAppliedToTrading = false,
                InfluenceRatio = 0,
                RuleAction = signal == null ? "HOLD" : signal.Action.ToString(),
                FinalAction = signal == null ? "HOLD" : signal.Action.ToString(),
                RuleScore = signal == null ? 0 : signal.Score,
                FinalScore = signal == null ? 0 : signal.Score,
                StrategyName = signal == null ? null : signal.StrategyName,
                TradeMode = feature == null ? null : feature.BarPhase.ToString(),
                Direction = null,
                Score = 0,
                Confidence = 0,
                BuyQualityScore = 0,
                SellRiskScore = 0,
                AvoidScore = 0,
                ExpectedReturn = 0,
                ExpectedDrawdown = 0,
                Reason = "BarMinimal: LocalML disabled",
                DataSourceType = GetMlDataSourceType(),
                IsReplay = IsReplayMlDataSource(),
                MlSessionId = BuildMlSessionId(feature == null ? signal?.Code : feature.Code, signalTime),
                CreatedAt = DateTime.Now
            };
        }


        private bool IsReplayMlDataSource()
        {
            // Replay ML 구분은 MainWindowViewModel.IsReplayMode 한 곳만 기준으로 사용한다.
            // GlobalApi.Mode를 함께 보면 토글/전환 타이밍에 Real/Replay가 어긋날 수 있다.
            return IsReplayMode;
        }

        private string GetMlDataSourceType()
        {
            return IsReplayMlDataSource() ? "Replay" : "Real";
        }

        private string BuildMlSessionId(string code, DateTime time)
        {
            DateTime safeTime = time == default(DateTime) ? DateTime.Now : time;
            string source = GetMlDataSourceType();
            string windowId = string.IsNullOrWhiteSpace(WindowId) ? "SYSTEM" : WindowId;
            string safeCode = string.IsNullOrWhiteSpace(code) ? "UNKNOWN" : code.Trim();

            return string.Format("{0}_{1:yyyyMMdd}_{2}_{3}", source, safeTime.Date, safeCode, windowId);
        }

        private static DateTime ResolveAnalysisDisplayTime(FeatureSnapshot feature)
        {
            if (feature != null && feature.Time != default(DateTime))
                return feature.Time;

            return DateTime.Now;
        }

        private void PublishAnalysisScorePoint(FeatureSnapshot feature, TradeSignal signal)
        {
            if (feature == null)
                return;

            double score = CalculateAnalysisChartScore(feature, signal);

            PublishAnalysisScorePoint(new AnalysisScorePoint
            {
                Time = ResolveAnalysisDisplayTime(feature),
                Code = string.IsNullOrWhiteSpace(feature.Code) ? string.Empty : feature.Code,
                Price = feature.Price,
                SignalScore = score,
                Action = signal == null ? "HOLD" : signal.Action.ToString(),
                StrategyName = string.IsNullOrWhiteSpace(signal?.StrategyName) ? "-" : signal.StrategyName,
                Reason = BuildAnalysisScorePointReason(feature, signal, score, "RAW", null)
            });
        }

        private static double CalculateAnalysisChartScore(FeatureSnapshot feature, TradeSignal signal)
        {
            if (feature == null)
                return 0;

            double score = signal == null ? feature.SignalScore : signal.Score;

            if (double.IsNaN(score) || double.IsInfinity(score))
                return 0;

            if (score < -100)
                return -100;

            if (score > 100)
                return 100;

            return score;
        }

        private void UpdateTradeAnalysisCardFromFeature(FeatureSnapshot feature, TradeSignal signal)
        {
            if (feature == null)
                return;

            string action = signal == null ? "HOLD" : signal.Action.ToString();
            string profitRate = feature.HasPosition
                ? feature.NetProfitRate.ToString("F2") + "%"
                : feature.ProfitRate.ToString("F2") + "%";

            TradeCardUpdatedAt = ResolveAnalysisDisplayTime(feature).ToString("HH:mm:ss");
            TradeCardCode = string.IsNullOrWhiteSpace(feature.Code) ? "-" : feature.Code;
            TradeCardTitle = GetTradeCardTitle(feature);
            TradeCardAction = action;
            TradeCardScore = CalculateAnalysisChartScore(feature, signal).ToString("+0.0;-0.0;0.0");
            TradeCardPrice = feature.Price <= 0 ? "-" : feature.Price.ToString("N0");
            TradeCardHoldingQty = feature.HoldingQty.ToString();
            TradeCardProfitRate = profitRate;
            TradeCardOneMinuteChange = feature.DollarBarChangeRate.ToString("F2") + "%";
            TradeCardThreeMinuteChange = feature.DollarBarFillRatio.ToString("P0");
            TradeCardStrength = feature.BarCurrentScore.ToString("F1");
            TradeCardVolumeRatio = feature.BuySellDollarValueRatio.ToString("F2");
            TradeCardBidAskRatio = feature.BuySellDollarValueRatio.ToString("F2");
            TradeCardPhase = feature.MarketTrend.ToString();
            TradeCardMarketPhase = feature.BarState.ToString();
            TradeCardTradeMode = feature.BarPhase.ToString();
            TradeCardStrategy = string.IsNullOrWhiteSpace(signal?.StrategyName) ? "-" : signal.StrategyName;
            TradeCardReason = BuildCompactDecisionReason(feature, signal);
            UpdateCurrentSymbolAllocationText(feature);

            ApplyTradeCardBrushes(action, profitRate);
        }

        private void UpdateCurrentSymbolAllocationText(FeatureSnapshot feature)
        {
            if (feature == null || string.IsNullOrWhiteSpace(feature.Code))
                return;

            if (Context != null && !string.IsNullOrWhiteSpace(Context.Code) &&
                NormalizeMoneyCreatorCode(Context.Code) != NormalizeMoneyCreatorCode(feature.Code))
                return;

            ApplyMoneyCreatorAllocationToFeature(feature);
            RefreshCurrentSymbolAllocationTextFromCore();
        }

        private void RefreshCurrentSymbolAllocationTextFromCore()
        {
            try
            {
                if (Context == null || string.IsNullOrWhiteSpace(Context.Code))
                    return;

                double budget;
                double remaining;
                double reserved;
                double positionAmount;
                bool isOverAllocated;

                if (!TryGetMoneyCreatorAllocation(
                    Context.Code,
                    out budget,
                    out remaining,
                    out reserved,
                    out positionAmount,
                    out isOverAllocated))
                {
                    if (_autoTradeController == null ||
                        !_autoTradeController.TryGetAllocationInfo(
                            Context.Code,
                            out budget,
                            out remaining,
                            out reserved,
                            out positionAmount,
                            out isOverAllocated))
                    {
                        return;
                    }
                }

                SetCurrentSymbolAllocationText(
                    budget,
                    remaining,
                    reserved,
                    positionAmount,
                    isOverAllocated);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[할당 UI 갱신 실패] " + ex.Message);
            }
        }

        private void SetCurrentSymbolAllocationText(
            double budget,
            double remaining,
            double reserved,
            double positionAmount,
            bool isOverAllocated)
        {
            budget = Math.Max(0, budget);
            remaining = Math.Max(0, remaining);
            reserved = Math.Max(0, reserved);
            positionAmount = Math.Max(0, positionAmount);

            if (budget <= 0 && remaining <= 0 && reserved <= 0 && positionAmount <= 0)
            {
                CurrentSymbolAllocationText = "할당 -";
                return;
            }

            string text = "할당 " + remaining.ToString("N0") + " / " + budget.ToString("N0") + "원";

            if (reserved > 0)
                text += "\n예약 " + reserved.ToString("N0");

            if (positionAmount > 0)
                text += "\n보유 " + positionAmount.ToString("N0");

            if (isOverAllocated)
                text += "\n초과";

            CurrentSymbolAllocationText = text;
        }

        private string GetTradeCardTitle(FeatureSnapshot feature)
        {
            if (Context != null &&
                Context.Code == feature.Code &&
                !string.IsNullOrWhiteSpace(Context.Name))
            {
                return Context.Name;
            }

            return string.IsNullOrWhiteSpace(feature.Code) ? "-" : feature.Code;
        }

        private static string ResolveTradeCardReason(FeatureSnapshot feature, TradeSignal signal)
        {
            if (!string.IsNullOrWhiteSpace(signal?.Reason))
                return signal.Reason;

            if (!string.IsNullOrWhiteSpace(feature?.HoldReason))
                return feature.HoldReason;

            return "분석 완료";
        }

        // ============================================================
        // Source: MainWindowViewModel.AutoTrading.Helpers.cs
        // ============================================================
        // Stage6 리팩토링: 이 파일의 활성 로직은 다른 AutoTrading partial 파일로 통합됨.
        // 기존 csproj의 Compile Include 호환을 위해 파일은 유지한다.

        // ============================================================
        // Source: MainWindowViewModel.AutoTrading.Orders.cs
        // ============================================================
        private void OnAutoTradeOrderRequested(OrderRequest request)
        {
            if (request == null || request.Signal == null)
                return;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                ExecuteAutoTradeOrderRequest(request);
                return;
            }

            if (IsReplayMode)
            {
                dispatcher.Invoke(
                    new Action(() =>
                    {
                        ExecuteAutoTradeOrderRequest(request);
                    }),
                    DispatcherPriority.Send);
                return;
            }

            dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(() =>
                {
                    ExecuteAutoTradeOrderRequest(request);
                }));
        }

        private void LogMoneyCreatorOrderFlow(
            string stage,
            string actionName,
            string code,
            int quantity,
            int price,
            string message)
        {
            AddAutoTradeAnalysisLog(
                "[주문흐름] " + stage +
                " / " + (string.IsNullOrWhiteSpace(code) ? "-" : code) +
                " / " + (string.IsNullOrWhiteSpace(actionName) ? "-" : actionName) +
                " / " + quantity.ToString("N0") + "주" +
                " / " + price.ToString("N0") + "원" +
                " / " + (message ?? string.Empty));

            TradeSignal csvSignal = _activeAutoTradeCsvOrderRequest == null
                ? null
                : _activeAutoTradeCsvOrderRequest.Signal;
            DateTime csvEventTime =
                csvSignal != null &&
                csvSignal.SnapshotFeature != null &&
                csvSignal.SnapshotFeature.Time != default(DateTime)
                    ? csvSignal.SnapshotFeature.Time
                    : ResolveAutoTradePositionTimestamp();

            AutoTradeCsvWriter.Shared.EnqueueOrderStage(
                csvEventTime,
                IsReplayMode,
                stage,
                actionName,
                code,
                quantity,
                price,
                ResolveAutoTradeCurrentPriceForPreOrder(code),
                ResolveCurrentBestAskPriceForAutoTrade(code),
                ResolveCurrentBestBidPriceForAutoTrade(code),
                _activeAutoTradeCsvOrderRequest == null ? string.Empty : _activeAutoTradeCsvOrderRequest.HogaType,
                csvSignal,
                message);
        }

        private void ExecuteAutoTradeOrderRequest(OrderRequest request)
        {
            _activeAutoTradeCsvOrderRequest = request;

            bool reservedBeforeSend = false;
            long reservedAmount = 0;
            string reserveBlockReason = null;
            string code = string.Empty;
            int quantity = 0;
            int price = 0;
            string actionName = "-";
            string stage = "INIT";

            try
            {
                var signal = request?.Signal;
                if (signal == null)
                    return;

                code = signal.Code;
                quantity = signal.Quantity;
                price = signal.Price <= 0 ? 0 : (int)Math.Round(signal.Price);

                actionName = signal.Action == TradeActionType.Buy
                    ? "자동매수"
                    : "자동매도";

                bool hardStopOrder = IsHardStopOrderSignal(signal);
                if (hardStopOrder)
                {
                    // Core의 StopLossLock 전량매도 수량을 실제 보유수량으로 한 번 더 동기화한다.
                    // 일반 매도/익절 주문에는 적용하지 않는다.
                    var liveHolding = FindHolding(code);
                    if (liveHolding != null && liveHolding.Quantity > 0)
                    {
                        quantity = liveHolding.Quantity;
                        signal.Quantity = quantity;
                    }

                    // StopLossLock은 유지하되 이미 매도 미체결이 있으면 중복 주문만 막는다.
                    if (HasPendingSellOrderForCode(code))
                    {
                        stage = "HARDSTOP_PENDING_SELL";
                        _autoTradeController?.MarkOrderSubmitted(request, -183);
                        LogMoneyCreatorOrderFlow(
                            stage,
                            actionName,
                            code,
                            quantity,
                            price,
                            "StopLossLock 유지 / 기존 매도 미체결 중복주문 차단");
                        return;
                    }
                }

                if (string.IsNullOrWhiteSpace(code) || quantity <= 0 || price <= 0)
                {
                    stage = "INVALID_REQUEST";
                    LogMoneyCreatorOrderFlow(stage, actionName, code, quantity, price, "주문 필수값 오류");
                    _autoTradeController?.MarkOrderSubmitted(request, -1);
                    return;
                }

                stage = "REQUEST_RECEIVED";
                LogMoneyCreatorOrderFlow(stage, actionName, code, quantity, price, "Core 주문후보 수신");

                stage = "ORDER_EXEC_START";
                LogMoneyCreatorOrderFlow(stage, actionName, code, quantity, price, "주문 실행 시작");

                if (signal.Action == TradeActionType.Buy)
                {
                    stage = "MARKET_SAFETY_CHECK";
                    LogMoneyCreatorOrderFlow(stage, actionName, code, quantity, price, "시장안전 확인 시작");

                    string marketSafetyBlockReason;
                    if (IsMarketSafetyBuyBlocked(out marketSafetyBlockReason))
                    {
                        stage = "MARKET_SAFETY_BLOCK";
                        _autoTradeController?.MarkOrderSubmitted(request, -180);
                        LogMoneyCreatorOrderFlow(stage, actionName, code, quantity, price, marketSafetyBlockReason);
                        AddAutoTradeAnalysisLog(
                            $"[자동매수 차단] {code} {quantity}주 / {price:N0}원 / {marketSafetyBlockReason}");
                        return;
                    }

                    stage = "MARKET_SAFETY_OK";
                    LogMoneyCreatorOrderFlow(stage, actionName, code, quantity, price, "시장안전 통과");

                    stage = "PRICE_GUARD_CHECK";
                    LogMoneyCreatorOrderFlow(stage, actionName, code, quantity, price, "가격가드 확인 시작");

                    string priceGuardBlockReason;
                    if (IsAutoTradeBuyPriceGuardBlocked(request, price, out priceGuardBlockReason))
                    {
                        stage = "PRICE_GUARD_BLOCK";
                        _autoTradeController?.MarkOrderSubmitted(request, -181);
                        LogMoneyCreatorOrderFlow(stage, actionName, code, quantity, price, priceGuardBlockReason);
                        AddAutoTradeAnalysisLog(
                            $"[자동매수 차단] {code} {quantity}주 / {price:N0}원 / {priceGuardBlockReason}");
                        return;
                    }

                    stage = "PRICE_GUARD_OK";
                    LogMoneyCreatorOrderFlow(stage, actionName, code, quantity, price, "가격가드 통과");

                    stage = "LIVE_LIMIT_APPLY";
                    price = ApplyAutoTradeLiveEntryLimitPriceGuard(request, code, price);
                    LogMoneyCreatorOrderFlow(stage, actionName, code, quantity, price, "실시간 진입 제한가격 적용");

                    stage = "LIVE_LIMIT_CHECK";
                    LogMoneyCreatorOrderFlow(stage, actionName, code, quantity, price, "실시간 진입 제한 확인 시작");

                    string liveLimitBlockReason;
                    if (IsAutoTradeLiveEntryLimitOrderInvalid(request, price, out liveLimitBlockReason))
                    {
                        stage = "LIVE_LIMIT_BLOCK";
                        _autoTradeController?.MarkOrderSubmitted(request, -182);
                        LogMoneyCreatorOrderFlow(stage, actionName, code, quantity, price, liveLimitBlockReason);
                        AddAutoTradeAnalysisLog(
                            $"[자동매수 차단] {code} {quantity}주 / {price:N0}원 / {liveLimitBlockReason}");
                        return;
                    }

                    stage = "LIVE_LIMIT_OK";
                    LogMoneyCreatorOrderFlow(stage, actionName, code, quantity, price, "실시간 진입 제한 통과");

                    // 주문 직전 기준으로만 최종 한도 검사/예약을 수행한다.
                    // 검사와 예약을 같은 lock 안에서 처리하지 않으면 연속 신호가 한도를 뚫을 수 있다.
                    stage = "REALLOCATION_START";
                    LogMoneyCreatorOrderFlow(stage, actionName, code, quantity, price, "자동주문전 한도확인 시작");
                    RebuildMoneyCreatorPortfolioAllocations("자동주문전 한도확인", false);

                    stage = "REALLOCATION_END";
                    LogMoneyCreatorOrderFlow(stage, actionName, code, quantity, price, "자동주문전 한도확인 완료");

                    stage = "RESERVE_START";
                    LogMoneyCreatorOrderFlow(stage, actionName, code, quantity, price, "주문전 예약 시작");

                    if (!TryReserveMoneyCreatorBuyOrderBeforeSend(
                        "자동주문전 한도확인",
                        code,
                        quantity,
                        price,
                        out reservedAmount,
                        out reserveBlockReason))
                    {
                        stage = "RESERVE_BLOCK";
                        _autoTradeController?.MarkOrderSubmitted(request, -1);
                        LogMoneyCreatorOrderFlow(stage, actionName, code, quantity, price, reserveBlockReason);
                        AddAutoTradeAnalysisLog(
                            $"[자동매수 차단] {code} {quantity}주 / {price:N0}원 / {reserveBlockReason}");
                        return;
                    }

                    reservedBeforeSend = true;
                    stage = "RESERVE_OK";
                    LogMoneyCreatorOrderFlow(stage, actionName, code, quantity, price, $"예약금={reservedAmount:N0}");
                }

                stage = "METADATA_SAVE";
                RememberAutoTradeOrderMetadata(signal);

                stage = "SEND_CALL";
                LogMoneyCreatorOrderFlow(stage, actionName, code, quantity, price, "Kiwoom SendOrder 호출");
                int sendResult = SendAutoTradeOrder(request, code, quantity, price);

                stage = "SUBMIT_RESULT";
                _autoTradeController?.MarkOrderSubmitted(request, sendResult);

                LogAutoTradeOrderResult(sendResult, actionName, code, quantity, price, signal);

                if (sendResult != 0)
                {
                    stage = "SEND_FAIL";
                    RemoveAutoTradeOrderMetadata(signal);

                    if (reservedBeforeSend)
                    {
                        ReleaseMoneyCreatorBuyOrderReservationBeforeSend(
                            code,
                            reservedAmount,
                            "자동주문전송실패");
                    }

                    return;
                }

                if (signal.Action == TradeActionType.Buy)
                {
                    AddAutoTradeAnalysisLog(
                        IsReplayMode
                            ? $"[자동매수 예약확정] {code} / 주문={reservedAmount:N0} / Replay 매수딜레이 없음"
                            : $"[자동매수 예약확정] {code} / 주문={reservedAmount:N0} / {MoneyCreatorBuyOrderDelaySeconds}초 매수대기 적용");
                }

                stage = "POST_SEND_REFRESH";
                RefreshCurrentSymbolAllocationTextFromCore();
                RequestAutoTradeOrderSync(request);
            }
            catch (Exception ex)
            {
                if (reservedBeforeSend)
                {
                    ReleaseMoneyCreatorBuyOrderReservationBeforeSend(
                        code,
                        reservedAmount,
                        "자동주문처리예외");
                }

                _autoTradeController?.MarkOrderSubmitted(request, -1);
                Debug.WriteLine("[자동매매 주문 처리 오류] " + ex.Message);
                LogMoneyCreatorOrderFlow(
                    "EXCEPTION",
                    actionName,
                    code,
                    quantity,
                    price,
                    $"stage={stage} / {ex.Message}");
                AddAutoTradeAnalysisLog(
                    $"[자동매매 주문 처리 오류] stage={stage} / {code} / qty={quantity} / price={price:N0} / {ex.Message}");
            }
            finally
            {
                _activeAutoTradeCsvOrderRequest = null;
            }
        }

        private bool TryReserveMoneyCreatorBuyOrderBeforeSend(
            string reason,
            string code,
            int quantity,
            int price,
            out long reservedAmount,
            out string blockReason)
        {
            reservedAmount = CalculateBuyReserveCashAmount(quantity, price);
            blockReason = null;

            string key = NormalizeMoneyCreatorCode(code);
            if (string.IsNullOrWhiteSpace(key))
            {
                blockReason = "종목코드 없음";
                return false;
            }

            if (quantity <= 0 || price <= 0 || reservedAmount <= 0)
            {
                blockReason = $"주문금액 오류 / qty={quantity}, price={price}";
                return false;
            }

            // Replay에서는 ReplayNow, Real에서는 실제 현재시간을 사용한다.
            DateTime now = ResolveAutoTradePositionTimestamp();

            lock (_moneyCreatorPortfolioSyncRoot)
            {
                if (_moneyCreatorCashSyncWaitMode)
                {
                    blockReason = string.IsNullOrWhiteSpace(_moneyCreatorCashSyncWaitReason)
                        ? "키움 예수금 동기화 대기"
                        : "키움 예수금 동기화 대기 / " + _moneyCreatorCashSyncWaitReason;
                    return false;
                }

                if (!_moneyCreatorCashSnapshotReady)
                {
                    _moneyCreatorCashSyncWaitMode = true;
                    _moneyCreatorCashSyncWaitReason = "키움 예수금 최초 수신 전";
                    blockReason = "키움 예수금 최초 수신 전";
                    return false;
                }
            }

            lock (_moneyCreatorPortfolioSyncRoot)
            {
                // Replay는 실제 30초 대기로 초입 신호가 뒤늦게 고점에서 체결되는 문제를 막기 위해
                // MoneyCreator 주문예약 매수딜레이를 완전히 사용하지 않는다.
                DateTime lastBuyAt;
                if (!IsReplayMode &&
                    _moneyCreatorLastBuyOrderReservedAtByCode.TryGetValue(key, out lastBuyAt))
                {
                    double elapsed = Math.Max(0.0, (now - lastBuyAt).TotalSeconds);
                    if (elapsed < MoneyCreatorBuyOrderDelaySeconds)
                    {
                        blockReason = $"매수 딜레이 {elapsed:F0}/{MoneyCreatorBuyOrderDelaySeconds}s";
                        return false;
                    }
                }

                MoneyCreatorPortfolioAllocationState allocation;
                if (!_moneyCreatorPortfolioAllocationByCode.TryGetValue(key, out allocation) || allocation == null)
                {
                    blockReason = "종목 할당정보 없음";
                    return false;
                }

                double budget = Math.Max(0, allocation.Budget);
                double positionAmount = Math.Max(0, allocation.PositionAmount);

                double tempReserved;
                _moneyCreatorAcceptedBuyReservedByCode.TryGetValue(key, out tempReserved);

                double currentReserved = Math.Max(Math.Max(0, allocation.ReservedAmount), Math.Max(0, tempReserved));
                double remaining = Math.Max(0, budget - positionAmount - currentReserved);

                string reallocationReason = null;

                if (reservedAmount > remaining)
                {
                    TryApplyMoneyCreatorSignalAwareReallocationOnBuyLocked(
                        key,
                        reservedAmount,
                        now,
                        string.IsNullOrWhiteSpace(reason) ? "자동주문전 기회재배분" : reason + " / 기회재배분",
                        out reallocationReason);

                    _moneyCreatorPortfolioAllocationByCode.TryGetValue(key, out allocation);
                    if (allocation != null)
                    {
                        budget = Math.Max(0, allocation.Budget);
                        positionAmount = Math.Max(0, allocation.PositionAmount);
                        currentReserved = Math.Max(Math.Max(0, allocation.ReservedAmount), Math.Max(0, tempReserved));
                        remaining = Math.Max(0, budget - positionAmount - currentReserved);
                    }
                }

                if (reservedAmount > remaining)
                {
                    blockReason =
                        "종목 한도 초과 / " +
                        "주문=" + reservedAmount.ToString("N0") +
                        ", 잔여=" + remaining.ToString("N0") +
                        ", 예산=" + budget.ToString("N0") +
                        ", 보유=" + positionAmount.ToString("N0") +
                        ", 예약=" + currentReserved.ToString("N0") +
                        (string.IsNullOrWhiteSpace(reallocationReason) ? string.Empty : ", 기회재배분=" + reallocationReason);
                    return false;
                }

                if (_moneyCreatorCashSnapshotReady && reservedAmount > Math.Max(0, _moneyCreatorEffectiveCash))
                {
                    _moneyCreatorCashSyncWaitMode = true;
                    _moneyCreatorCashSyncWaitReason =
                        "공통 예수금 초과 / 주문=" + reservedAmount.ToString("N0") +
                        ", 사용가능=" + Math.Max(0, _moneyCreatorEffectiveCash).ToString("N0");

                    blockReason = _moneyCreatorCashSyncWaitReason;
                    return false;
                }

                double nextTempReserved = Math.Max(0, tempReserved + reservedAmount);
                _moneyCreatorAcceptedBuyReservedByCode[key] = nextTempReserved;

                allocation.ReservedAmount = Math.Max(currentReserved, nextTempReserved);
                allocation.RemainingBudget = Math.Max(0, budget - positionAmount - allocation.ReservedAmount);
                allocation.IsOverAllocated = positionAmount + allocation.ReservedAmount > budget;
                allocation.UpdatedAt = now;
                allocation.Reason =
                    (string.IsNullOrWhiteSpace(reason) ? "자동주문전예약" : reason) +
                    " / 주문전예약=" + reservedAmount.ToString("N0") +
                    ", 잔여=" + allocation.RemainingBudget.ToString("N0");

                _moneyCreatorPortfolioAllocationByCode[key] = allocation;
                _moneyCreatorLastBuyOrderReservedAtByCode[key] = now;
            }

            SynchronizeMoneyCreatorCashFromCurrentKiwoomSnapshot(reason + " 주문전예약");
            RefreshCurrentSymbolAllocationTextFromCore();
            return true;
        }

        private void ReleaseMoneyCreatorBuyOrderReservationBeforeSend(
            string code,
            long amount,
            string reason)
        {
            string key = NormalizeMoneyCreatorCode(code);
            if (!string.IsNullOrWhiteSpace(key))
            {
                lock (_moneyCreatorPortfolioSyncRoot)
                    _moneyCreatorLastBuyOrderReservedAtByCode.Remove(key);
            }

            if (amount > 0)
                ReleaseMoneyCreatorAcceptedBuyReservation(code, amount, reason);

            RefreshCurrentSymbolAllocationTextFromCore();
        }

        private void RememberAutoTradeOrderMetadata(TradeSignal signal)
        {
            if (signal == null || string.IsNullOrWhiteSpace(signal.Code))
                return;

            string key = BuildAutoTradeOrderMetadataKey(signal.Code, signal.Action);
            if (string.IsNullOrWhiteSpace(key))
                return;

            _autoTradeOrderMetadataByRouteKey[key] = new AutoTradeOrderMetadata
            {
                StrategyName = signal.StrategyName,
                TiRexDirection = signal.TiRexDirection,
                TiRexConfidence = signal.TiRexConfidence,
                TiRexPredictedReturn = signal.TiRexPredictedReturn,
                CreatedAt = DateTime.Now
            };
        }

        private void RemoveAutoTradeOrderMetadata(TradeSignal signal)
        {
            if (signal == null || string.IsNullOrWhiteSpace(signal.Code))
                return;

            string key = BuildAutoTradeOrderMetadataKey(signal.Code, signal.Action);
            if (string.IsNullOrWhiteSpace(key))
                return;

            AutoTradeOrderMetadata removed;
            _autoTradeOrderMetadataByRouteKey.TryRemove(key, out removed);
        }

        private void ApplyAutoTradeOrderMetadata(OrderInfo order)
        {
            if (order == null || string.IsNullOrWhiteSpace(order.Code))
                return;

            string key = BuildAutoTradeOrderMetadataKey(order);
            if (string.IsNullOrWhiteSpace(key))
                return;

            AutoTradeOrderMetadata metadata;
            if (!_autoTradeOrderMetadataByRouteKey.TryGetValue(key, out metadata) || metadata == null)
                return;

            if ((DateTime.Now - metadata.CreatedAt).TotalMinutes > 10)
            {
                AutoTradeOrderMetadata removed;
                _autoTradeOrderMetadataByRouteKey.TryRemove(key, out removed);
                return;
            }

            if (IsEmptyOrSystemOrderStrategy(order.StrategyName) && !string.IsNullOrWhiteSpace(metadata.StrategyName))
                order.StrategyName = metadata.StrategyName;

            if (string.IsNullOrWhiteSpace(order.TiRexDirection) && !string.IsNullOrWhiteSpace(metadata.TiRexDirection))
                order.TiRexDirection = metadata.TiRexDirection;

            if (order.TiRexConfidence <= 0 && metadata.TiRexConfidence > 0)
                order.TiRexConfidence = metadata.TiRexConfidence;

            if (order.TiRexPredictedReturn == 0 && metadata.TiRexPredictedReturn != 0)
                order.TiRexPredictedReturn = metadata.TiRexPredictedReturn;
        }

        private static bool IsEmptyOrSystemOrderStrategy(string strategyName)
        {
            if (string.IsNullOrWhiteSpace(strategyName))
                return true;

            strategyName = strategyName.Trim();
            return strategyName == "CHEJAN" ||
                   strategyName == "ORDER_TR" ||
                   strategyName == "ORDER_HISTORY" ||
                   strategyName == "미체결 주문" ||
                   strategyName == "주문내역";
        }

        private static string BuildAutoTradeOrderMetadataKey(string code, TradeActionType action)
        {
            if (string.IsNullOrWhiteSpace(code))
                return null;

            if (action == TradeActionType.Buy)
                return NormalizeMoneyCreatorCode(code) + "_BUY";

            if (action == TradeActionType.Sell)
                return NormalizeMoneyCreatorCode(code) + "_SELL";

            return null;
        }

        private string BuildAutoTradeOrderMetadataKey(OrderInfo order)
        {
            if (order == null || string.IsNullOrWhiteSpace(order.Code))
                return null;

            if (IsBuyOrder(order))
                return NormalizeMoneyCreatorCode(order.Code) + "_BUY";

            if (IsSellOrder(order))
                return NormalizeMoneyCreatorCode(order.Code) + "_SELL";

            return null;
        }

        private bool IsMarketSafetyBuyBlocked(out string blockReason)
        {
            blockReason = null;

            var kiwoom = _marketApi as KiwoomApiService;
            if (kiwoom == null)
                return false;

            return kiwoom.IsBuyBlockedByMarketSafety(out blockReason);
        }

        private bool IsAutoTradeBuyPriceGuardBlocked(
            OrderRequest request,
            int signalPrice,
            out string blockReason)
        {
            blockReason = null;

            TradeSignal signal = request == null ? null : request.Signal;
            if (!IsAutoTradeLiveEntryBuySignal(signal))
                return false;

            if (signalPrice <= 0)
                return false;

            int currentPrice = ResolveAutoTradeCurrentPriceForPreOrder(signal.Code);
            if (currentPrice <= 0)
                return false;

            double maxMoveRate = _autoTradeSettings == null || _autoTradeSettings.LiveBreakoutMaxPreOrderPriceMoveRate <= 0
                ? 0.50
                : _autoTradeSettings.LiveBreakoutMaxPreOrderPriceMoveRate;

            double moveRate = ((currentPrice - signalPrice) / (double)signalPrice) * 100.0;
            if (moveRate <= maxMoveRate)
                return false;

            blockReason =
                "LiveBreakout 주문직전 가격괴리 차단: " +
                "signal=" + signalPrice.ToString("N0") +
                ", current=" + currentPrice.ToString("N0") +
                ", move=" + moveRate.ToString("F2") + "%" +
                ", limit=" + maxMoveRate.ToString("F2") + "%";
            return true;
        }

        private int ApplyAutoTradeLiveEntryLimitPriceGuard(
            OrderRequest request,
            string code,
            int signalPrice)
        {
            if (request == null || !IsAutoTradeLiveEntryBuySignal(request.Signal))
                return signalPrice;

            if (signalPrice <= 0)
                return signalPrice;

            bool useLimit = _autoTradeSettings == null || _autoTradeSettings.LiveBreakoutUseLimitPriceForLiveEntry;
            if (!useLimit)
                return signalPrice;

            string oldHoga = string.IsNullOrWhiteSpace(request.HogaType)
                ? string.Empty
                : request.HogaType.Trim();

            // Stage178:
            // Live 계열 매수는 신호가를 상한 지정가로 고정한다.
            // price 지역변수, request.Signal.Price, request.HogaType이 서로 어긋나면
            // SendOrder에서 시장가/최유리처럼 체결가가 튀는 사고를 막을 수 없다.
            int limitPrice = signalPrice;
            request.HogaType = "00";
            request.Signal.Price = limitPrice;

            AddAutoTradeAnalysisLog(
                $"[자동매수 슬리피지방어] {code} / Live계열 신호가상한 지정가 확정 / " +
                $"signal={signalPrice:N0} / limit={limitPrice:N0} / hoga={(string.IsNullOrWhiteSpace(oldHoga) ? "-" : oldHoga)}->00");

            return limitPrice;
        }

        private bool IsAutoTradeLiveEntryLimitOrderInvalid(
            OrderRequest request,
            int orderPrice,
            out string blockReason)
        {
            blockReason = null;

            if (request == null || !IsAutoTradeLiveEntryBuySignal(request.Signal))
                return false;

            if (orderPrice <= 0)
            {
                blockReason = "Live계열 지정가 주문가격 오류";
                return true;
            }

            string hoga = string.IsNullOrWhiteSpace(request.HogaType)
                ? string.Empty
                : request.HogaType.Trim();

            if (hoga != "00")
            {
                blockReason = "Live계열 시장가/최유리 주문 차단: hoga=" + (string.IsNullOrWhiteSpace(hoga) ? "-" : hoga);
                return true;
            }

            int signalPrice = request.Signal.Price <= 0 ? 0 : (int)Math.Round(request.Signal.Price);
            if (signalPrice <= 0)
            {
                blockReason = "Live계열 신호가 없음";
                return true;
            }

            if (orderPrice > signalPrice)
            {
                blockReason = "Live계열 주문가가 신호가 초과: signal=" + signalPrice.ToString("N0") + ", order=" + orderPrice.ToString("N0");
                return true;
            }

            return false;
        }

        private static bool IsAutoTradeLiveEntryBuySignal(TradeSignal signal)
        {
            if (signal == null || signal.Action != TradeActionType.Buy)
                return false;

            string strategy = signal.StrategyName ?? string.Empty;
            if (strategy.IndexOf("PVLiveEntry", StringComparison.OrdinalIgnoreCase) >= 0 ||
                strategy.IndexOf("PVAccumTurn", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            string reason = signal.Reason ?? string.Empty;
            return reason.IndexOf("LiveBreakout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   reason.IndexOf("LiveAccumTurn", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private int ResolveAutoTradeCurrentPriceForPreOrder(string code)
        {
            string normalizedCode = NormalizeMoneyCreatorCode(code);
            if (string.IsNullOrWhiteSpace(normalizedCode))
                return 0;

            if (Context != null &&
                NormalizeMoneyCreatorCode(Context.Code) == normalizedCode &&
                Context.LastPrice > 0)
            {
                return (int)Math.Round(Context.LastPrice);
            }

            lock (_moneyCreatorPortfolioSyncRoot)
            {
                FeatureSnapshot feature;
                if (_moneyCreatorLatestFeatureByCode.TryGetValue(normalizedCode, out feature) &&
                    feature != null &&
                    feature.Price > 0)
                {
                    return (int)Math.Round(feature.Price);
                }
            }

            if (Symbols != null)
            {
                var symbol = Symbols.FirstOrDefault(x =>
                    x != null &&
                    NormalizeMoneyCreatorCode(x.Code) == normalizedCode);

                if (symbol != null && !string.IsNullOrWhiteSpace(symbol.LastPrice))
                {
                    string raw = Regex.Replace(symbol.LastPrice, @"[^\d.]", string.Empty);
                    double parsed;
                    if (double.TryParse(raw, out parsed) && parsed > 0)
                        return (int)Math.Round(parsed);
                }
            }

            return 0;
        }

        private int SendAutoTradeOrder(OrderRequest request, string code, int quantity, int price)
        {
            if (request != null && IsAutoTradeLiveEntryBuySignal(request.Signal))
            {
                request.HogaType = "00";

                if (request.Signal != null && request.Signal.Price > 0)
                    price = Math.Min(price, (int)Math.Round(request.Signal.Price));

                if (price <= 0)
                {
                    AddAutoTradeAnalysisLog($"[자동매수 차단] {code} / Live계열 지정가 오류 / price={price:N0}");
                    return -183;
                }
            }

            return _marketApi.SendOrder(
                request.WindowId,
                request.ScreenNo,
                request.AccountNo,
                request.OrderType,
                code,
                quantity,
                price,
                request.HogaType);
        }

        private void LogAutoTradeOrderResult(
            int sendResult,
            string actionName,
            string code,
            int quantity,
            int price,
            TradeSignal signal)
        {
            LogMoneyCreatorOrderFlow(
                sendResult == 0 ? "SEND_OK" : "SEND_FAIL",
                actionName,
                code,
                quantity,
                price,
                sendResult == 0 ? "Kiwoom 접수" : "result=" + sendResult);

            AddAutoTradeAnalysisLog(
                sendResult == 0
                    ? $"[자동주문접수] {actionName} {code} {quantity}주 / {price:N0}원 / {signal.StrategyName}"
                    : $"[자동주문실패] {actionName} {code} {quantity}주 / result={sendResult}");
        }

        private async void RequestAutoTradeOrderSync(OrderRequest request)
        {
            if (string.IsNullOrWhiteSpace(SelectedAccount))
                return;

            // 자동취소 감시는 늦게 걸면 안 되므로 먼저 등록한다.
            ScheduleAutoTradeCancelIfUnfilled(request);

            await RequestAccountStateRefreshAfterTrade("자동주문접수 " + request.Signal.Code);
        }

        private void ScheduleAutoTradeCancelIfUnfilled(OrderRequest request)
        {
            // Real 전용 미체결 감시/정정/취소 루프다.
            // Replay에서는 ReplayOrderSimulator가 replay tick 시간으로 미체결을 처리해야 한다.
            // 실제 타이머(Task.Delay) 기반 감시를 같이 돌리면 replay 배속에서
            // 매수 직후 원주문 취소가 들어가므로 완전히 차단한다.
            if (IsReplayMode)
                return;

            if (request == null || request.Signal == null)
                return;

            string code = request.Signal.Code;
            string key = $"{code}_{request.Signal.Action}_{DateTime.Now:yyyyMMddHHmmssfff}";

            if (request.Signal.Action == TradeActionType.Sell)
            {
                if (!_autoCancelWatchMap.TryAdd(key, true))
                    return;

                Task.Run(async () =>
                {
                    try
                    {
                        bool urgentSell = IsUrgentAutoTradeSellSignal(request.Signal);

                        await Task.Delay(urgentSell ? AutoTradeUrgentSellFirstCheckDelayMilliseconds : AutoTradeSellFirstCheckDelayMilliseconds);

                        if (string.IsNullOrWhiteSpace(SelectedAccount))
                            return;

                        var sellOrder = urgentSell
                            ? await TryFindCancelableAutoTradeOrder(request, 3, 700)
                            : await TryFindCancelableAutoTradeOrder(request, 3, 1000);

                        if (sellOrder != null && sellOrder.Success)
                        {
                            TryRequestAutoTradeSellReplaceOrDefer(
                                request,
                                sellOrder,
                                urgentSell ? "urgent-watch" : "sell-watch");
                        }
                        else
                        {
                            AddAutoTradeAnalysisLog(
                                $"[매도상태확인] {code} / {request.Signal.StrategyName} / " +
                                "미체결 없음 또는 이미 체결/취소");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[매도미체결 확인 오류] " + ex.Message);
                        AddAutoTradeAnalysisLog("[매도미체결 확인 오류] " + ex.Message);
                    }
                    finally
                    {
                        bool removed;
                        _autoCancelWatchMap.TryRemove(key, out removed);
                    }
                });

                return;
            }

            if (request.Signal.Action != TradeActionType.Buy)
                return;

            if (!_autoCancelWatchMap.TryAdd(key, true))
                return;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(AutoTradeBuyRepriceDelayMilliseconds);

                    if (string.IsNullOrWhiteSpace(SelectedAccount))
                        return;

                    var cancelableOrder =
                        await TryFindCancelableAutoTradeOrder(request);

                    if (cancelableOrder == null ||
                        !cancelableOrder.Success)
                    {
                        AddAutoTradeAnalysisLog(
                            $"[자동취소 생략] {code} / 취소 가능 원주문번호 없음 또는 미체결 없음");
                        return;
                    }

                    if (TryRequestAutoTradeBuyRepriceOrDeferCancel(request, cancelableOrder))
                        return;

                    int cancelType = GetCancelOrderType(request.Signal.Action);
                    if (cancelType <= 0)
                        return;

                    RequestAutoTradeCancelOrder(
                        request,
                        cancelableOrder.OrderNo,
                        cancelableOrder.UntradedQty,
                        cancelType);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[자동취소 처리 오류] " + ex.Message);
                    AddAutoTradeAnalysisLog("[자동취소 처리 오류] " + ex.Message);
                }
                finally
                {
                    bool removed;
                    _autoCancelWatchMap.TryRemove(key, out removed);
                }
            });
        }

        private async Task<CancelableAutoTradeOrderResult> TryFindCancelableAutoTradeOrder(
            OrderRequest request,
            int maxAttempts = 10,
            int delayMs = 1000)
        {
            if (request == null)
                return CancelableAutoTradeOrderResult.Fail();

            maxAttempts = Math.Max(1, maxAttempts);
            delayMs = Math.Max(200, delayMs);

            for (int i = 0; i < maxAttempts; i++)
            {
                RequestAutoTradeUnfilledOrdersOnUi();

                await Task.Delay(delayMs);

                string orderNo;
                int untradedQty;

                if (_autoTradeController != null &&
                    _autoTradeController.TryGetCancelableOrder(
                        request,
                        out orderNo,
                        out untradedQty))
                {
                    var uiOrderResult = await TryFindCancelableAutoTradeOrderFromUiAsync(request);
                    if (uiOrderResult != null &&
                        uiOrderResult.Success &&
                        string.Equals(uiOrderResult.OrderNo, orderNo, StringComparison.OrdinalIgnoreCase))
                    {
                        return uiOrderResult;
                    }

                    return CancelableAutoTradeOrderResult.SuccessResult(
                        orderNo,
                        untradedQty);
                }

                var uiResult = await TryFindCancelableAutoTradeOrderFromUiAsync(request);
                if (uiResult != null && uiResult.Success)
                    return uiResult;
            }

            return CancelableAutoTradeOrderResult.Fail();
        }

        private void RequestAutoTradeUnfilledOrdersOnUi()
        {
            var dispatcher = Application.Current?.Dispatcher;

            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(
                    new Action(RequestAutoTradeUnfilledOrdersCore),
                    DispatcherPriority.Send);
                return;
            }

            RequestAutoTradeUnfilledOrdersCore();
        }

        private void RequestAutoTradeUnfilledOrdersCore()
        {
            if (string.IsNullOrWhiteSpace(SelectedAccount))
                return;

            _marketApi.RequestUnfilledOrders(
                "COMMON",
                SelectedAccount,
                ScreenNo);
        }

        private Task<CancelableAutoTradeOrderResult> TryFindCancelableAutoTradeOrderFromUiAsync(
            OrderRequest request)
        {
            var dispatcher = Application.Current?.Dispatcher;

            if (dispatcher == null || dispatcher.CheckAccess())
                return Task.FromResult(TryFindCancelableAutoTradeOrderFromUi(request));

            var tcs = new TaskCompletionSource<CancelableAutoTradeOrderResult>();

            dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(() =>
                {
                    try
                    {
                        tcs.SetResult(TryFindCancelableAutoTradeOrderFromUi(request));
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                }));

            return tcs.Task;
        }

        private CancelableAutoTradeOrderResult TryFindCancelableAutoTradeOrderFromUi(
            OrderRequest request)
        {
            if (request == null || request.Signal == null)
                return CancelableAutoTradeOrderResult.Fail();

            string code = request.Signal.Code;
            TradeActionType action = request.Signal.Action;

            if (string.IsNullOrWhiteSpace(code))
                return CancelableAutoTradeOrderResult.Fail();

            var orders = OrderHistory == null
                ? new List<OrderInfo>()
                : OrderHistory
                    .Where(x =>
                        x != null &&
                        x.Code == code &&
                        !string.IsNullOrWhiteSpace(x.OrderNo) &&
                        GetRemainingOrderQty(x) > 0 &&
                        IsSameAutoTradeOrderSide(x, action) &&
                        IsCancelableOrderState(x))
                    .Select(CloneOrderInfo)
                    .ToList();

            OrderInfo order = null;
            DateTime latestOrderTime = DateTime.MinValue;

            foreach (var item in orders)
            {
                DateTime parsedOrderTime = ParseOrderTime(item.OrderTime);
                if (order == null || parsedOrderTime > latestOrderTime)
                {
                    order = item;
                    latestOrderTime = parsedOrderTime;
                }
            }

            if (order == null)
                return CancelableAutoTradeOrderResult.Fail();

            return CancelableAutoTradeOrderResult.SuccessResult(
                order.OrderNo,
                GetRemainingOrderQty(order),
                order);
        }

        private bool IsSameAutoTradeOrderSide(OrderInfo order, TradeActionType action)
        {
            if (action == TradeActionType.Buy)
                return IsBuyOrder(order);

            if (action == TradeActionType.Sell)
                return IsSellOrder(order);

            return false;
        }

        private static bool IsCancelableOrderState(OrderInfo order)
        {
            if (order == null)
                return false;

            if (order.OrderStatus == OrderStatus.Cancelled ||
                order.OrderStatus == OrderStatus.Rejected ||
                order.OrderStatus == OrderStatus.FullyFilled)
            {
                return false;
            }

            string status = order.Status ?? string.Empty;
            if (status.Contains("취소") || status.Contains("거부"))
                return false;

            return order.UntradedQty > 0 || order.Qty - order.ExecQty > 0;
        }

        private static int GetCancelOrderType(TradeActionType action)
        {
            if (action == TradeActionType.Buy)
                return 3;

            if (action == TradeActionType.Sell)
                return 0;

            return 0;
        }

        private static bool IsUrgentAutoTradeSellSignal(TradeSignal signal)
        {
            if (signal == null || signal.Action != TradeActionType.Sell)
                return false;

            string strategy = signal.StrategyName ?? string.Empty;

            return strategy.IndexOf("StopLoss", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   strategy.IndexOf("EmergencyExit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   strategy.IndexOf("RiskExit", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private int ResolveUrgentSellLimitPrice(OrderRequest request)
        {
            if (request == null || request.Signal == null)
                return 0;

            double price = 0;

            if (Context != null &&
                !string.IsNullOrWhiteSpace(Context.Code) &&
                NormalizeMoneyCreatorCode(Context.Code) == NormalizeMoneyCreatorCode(request.Signal.Code) &&
                Context.LastPrice > 0)
            {
                price = Context.LastPrice;
            }

            if (price <= 0 && request.Signal.Price > 0)
                price = request.Signal.Price;

            if (price <= 0)
                return 0;

            return Math.Max(1, (int)Math.Round(price));
        }

        private void RequestUrgentAutoTradeSellCorrection(
            OrderRequest request,
            string orderNo,
            int untradedQty)
        {
            if (request == null || request.Signal == null)
                return;

            if (request.Signal.Action != TradeActionType.Sell)
                return;

            if (!IsUrgentAutoTradeSellSignal(request.Signal))
                return;

            if (string.IsNullOrWhiteSpace(orderNo) || untradedQty <= 0)
                return;

            Application.Current?.Dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(async () =>
                {
                    int result = _marketApi.SendOrder(
                        WindowId,
                        ScreenNo,
                        SelectedAccount,
                        6,
                        request.Signal.Code,
                        untradedQty,
                        0,
                        "03",
                        orderNo);

                    if (result == 0)
                    {
                        AddAutoTradeAnalysisLog(
                            $"[긴급매도 정정] {request.Signal.Code} / {request.Signal.StrategyName} / " +
                            $"시장가정정 {untradedQty}주 / 원주문={orderNo}");

                        await RequestAccountStateRefreshAfterTrade("긴급매도 시장가정정 " + request.Signal.Code);
                        return;
                    }

                    int limitPrice = ResolveUrgentSellLimitPrice(request);
                    if (limitPrice <= 0)
                    {
                        AddAutoTradeAnalysisLog(
                            $"[긴급매도 정정실패] {request.Signal.Code} / 시장가 result={result} / " +
                            "지정가 fallback 가격 없음");
                        return;
                    }

                    int fallbackResult = _marketApi.SendOrder(
                        WindowId,
                        ScreenNo,
                        SelectedAccount,
                        6,
                        request.Signal.Code,
                        untradedQty,
                        limitPrice,
                        "00",
                        orderNo);

                    if (fallbackResult == 0)
                    {
                        AddAutoTradeAnalysisLog(
                            $"[긴급매도 정정] {request.Signal.Code} / {request.Signal.StrategyName} / " +
                            $"지정가정정 {untradedQty}주 / {limitPrice:N0}원 / 원주문={orderNo}");

                        await RequestAccountStateRefreshAfterTrade("긴급매도 지정가정정 " + request.Signal.Code);
                    }
                    else
                    {
                        AddAutoTradeAnalysisLog(
                            $"[긴급매도 정정실패] {request.Signal.Code} / 시장가 result={result} / " +
                            $"지정가 result={fallbackResult} / 원주문={orderNo}");
                    }
                }));
        }

        private void TrySchedulePendingSellReplaceFromAnalysisOnUi(string code)
        {
            if (IsReplayMode)
                return;

            if (string.IsNullOrWhiteSpace(code))
                return;

            var orders = OrderHistory == null
                ? new List<OrderInfo>()
                : OrderHistory
                    .Where(x =>
                        x != null &&
                        x.Code == code &&
                        !string.IsNullOrWhiteSpace(x.OrderNo) &&
                        GetRemainingOrderQty(x) > 0 &&
                        IsSellOrder(x) &&
                        IsCancelableOrderState(x))
                    .Select(CloneOrderInfo)
                    .ToList();

            OrderInfo order = null;
            DateTime latestOrderTime = DateTime.MinValue;

            foreach (var item in orders)
            {
                DateTime parsedOrderTime = ParseOrderTime(item.OrderTime);
                if (order == null || parsedOrderTime > latestOrderTime)
                {
                    order = item;
                    latestOrderTime = parsedOrderTime;
                }
            }

            if (order == null)
                return;

            string watchKey = "SELL_REPLACE_ANALYSIS_" + order.OrderNo;
            if (!_autoCancelWatchMap.TryAdd(watchKey, true))
                return;

            var request = CreateAutoTradeSellRequestFromOpenOrder(order);
            if (request == null)
            {
                bool removedImmediately;
                _autoCancelWatchMap.TryRemove(watchKey, out removedImmediately);
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    var latestOrder = await TryFindCancelableAutoTradeOrder(request, 2, 700);
                    if (latestOrder != null && latestOrder.Success)
                    {
                        TryRequestAutoTradeSellReplaceOrDefer(
                            request,
                            latestOrder,
                            "analysis-watch");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[매도미체결 분석감시 오류] " + ex.Message);
                    AddAutoTradeAnalysisLog("[매도미체결 분석감시 오류] " + ex.Message);
                }
                finally
                {
                    await Task.Delay(1000);
                    bool removed;
                    _autoCancelWatchMap.TryRemove(watchKey, out removed);
                }
            });
        }

        private OrderRequest CreateAutoTradeSellRequestFromOpenOrder(OrderInfo order)
        {
            if (order == null || string.IsNullOrWhiteSpace(order.Code))
                return null;

            int untradedQty = GetRemainingOrderQty(order);
            if (untradedQty <= 0)
                return null;

            int price = ResolveOrderPriceForCash(order);
            if (price <= 0 && Context != null && NormalizeMoneyCreatorCode(Context.Code) == NormalizeMoneyCreatorCode(order.Code))
                price = (int)Math.Round(Math.Max(0, Context.LastPrice));

            string strategy = string.IsNullOrWhiteSpace(order.StrategyName)
                ? "PendingSell"
                : order.StrategyName;

            var signal = new TradeSignal
            {
                Code = order.Code,
                Action = TradeActionType.Sell,
                Score = 0,
                Reason = "PendingSellMonitor",
                StrategyName = strategy,
                Price = price,
                Quantity = untradedQty,
                CreatedAt = DateTime.Now
            };

            return new OrderRequest
            {
                WindowId = WindowId,
                ScreenNo = ScreenNo,
                AccountNo = SelectedAccount,
                Signal = signal,
                OrderType = 2,
                HogaType = "00",
                CreatedAt = ParseOrderTime(order.OrderTime)
            };
        }

        private bool TryRequestAutoTradeSellReplaceOrDefer(
            OrderRequest request,
            CancelableAutoTradeOrderResult cancelableOrder,
            string source)
        {
            if (request == null || request.Signal == null || cancelableOrder == null)
                return false;

            if (request.Signal.Action != TradeActionType.Sell)
                return false;

            if (string.IsNullOrWhiteSpace(cancelableOrder.OrderNo) || cancelableOrder.UntradedQty <= 0)
                return false;

            bool urgentSell = IsUrgentAutoTradeSellSignal(request.Signal);
            int maxAttempts = urgentSell
                ? AutoTradeUrgentSellReplaceMaxAttemptsPerOrder
                : AutoTradeNormalSellReplaceMaxAttemptsPerOrder;

            int currentAttempt;
            _autoTradeSellReplaceAttemptByOrderNo.TryGetValue(cancelableOrder.OrderNo, out currentAttempt);

            if (currentAttempt >= maxAttempts)
            {
                AddAutoTradeAnalysisLog(
                    $"[매도미체결 정정제외] {request.Signal.Code} / 원주문={cancelableOrder.OrderNo} / " +
                    $"시도횟수 {currentAttempt}/{maxAttempts} / source={source}");
                return false;
            }

            int originalPrice = ResolveCancelableOrderPrice(cancelableOrder, request);
            int currentBid = ResolveCurrentBestBidPriceForAutoTrade(request.Signal.Code);
            FeatureSnapshot latestFeature = GetLatestAutoTradeFeatureSnapshot(request.Signal.Code);
            double elapsedSeconds = ResolveOrderElapsedSeconds(cancelableOrder, request);

            SellReplaceDecision decision = _autoTradeController == null
                ? SellReplaceDecision.None("AutoTrading 판단기 없음")
                : _autoTradeController.EvaluateUnfilledSellReplace(
                    request,
                    latestFeature,
                    originalPrice,
                    currentBid,
                    cancelableOrder.UntradedQty,
                    currentAttempt,
                    elapsedSeconds);

            if (decision == null || decision.Action != SellReplaceAction.Reprice)
            {
                AddAutoTradeAnalysisLog(
                    $"[매도미체결 유지] {request.Signal.Code} / {request.Signal.StrategyName} / " +
                    $"잔량={cancelableOrder.UntradedQty} / 원주문={cancelableOrder.OrderNo} / " +
                    $"order={originalPrice:N0} / bid={currentBid:N0} / elapsed={elapsedSeconds:F1}s / " +
                    $"reason={decision?.Reason ?? "판단 없음"} / source={source}");
                return false;
            }

            int cooldownMs = ResolveSellReplaceCooldownMilliseconds(urgentSell);
            int waitMs = ResolveSellReplaceCooldownWaitMilliseconds(request.Signal.Code, cooldownMs);
            if (waitMs > 0)
            {
                AddAutoTradeAnalysisLog(
                    $"[매도미체결 정정대기] {request.Signal.Code} / {request.Signal.StrategyName} / " +
                    $"잔량={cancelableOrder.UntradedQty} / 원주문={cancelableOrder.OrderNo} / " +
                    $"cooldown={waitMs}ms / reason={decision.Reason} / source={source}");

                ScheduleDeferredAutoTradeSellReplace(request, waitMs, source);
                return true;
            }

            RequestAutoTradeSellReplaceOrder(
                request,
                cancelableOrder,
                decision,
                source);

            return true;
        }

        private int ResolveSellReplaceCooldownMilliseconds(bool urgentSell)
        {
            return urgentSell
                ? AutoTradeUrgentSellReplaceCooldownMilliseconds
                : AutoTradeSellReplaceCooldownMilliseconds;
        }

        private int ResolveSellReplaceCooldownWaitMilliseconds(string code, int cooldownMs)
        {
            if (string.IsNullOrWhiteSpace(code) || cooldownMs <= 0)
                return 0;

            string key = NormalizeMoneyCreatorCode(code);
            if (string.IsNullOrWhiteSpace(key))
                return 0;

            DateTime lastAt;
            if (!_lastAutoTradeSellReplaceAtByCode.TryGetValue(key, out lastAt) || lastAt == default(DateTime))
                return 0;

            double elapsedMs = (DateTime.Now - lastAt).TotalMilliseconds;
            int waitMs = cooldownMs - (int)Math.Floor(elapsedMs);
            return waitMs <= 0 ? 0 : waitMs;
        }

        private void ScheduleDeferredAutoTradeSellReplace(
            OrderRequest request,
            int delayMs,
            string source)
        {
            if (request == null || request.Signal == null)
                return;

            string code = NormalizeMoneyCreatorCode(request.Signal.Code);
            if (string.IsNullOrWhiteSpace(code))
                return;

            delayMs = Math.Max(200, delayMs);
            string key = "SELL_REPLACE_DEFER_" + code;

            if (!_autoTradeSellReplaceDeferMap.TryAdd(key, true))
                return;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMs);

                    if (string.IsNullOrWhiteSpace(SelectedAccount))
                        return;

                    var latestOrder = await TryFindCancelableAutoTradeOrder(request, 2, 700);
                    if (latestOrder == null || !latestOrder.Success)
                        return;

                    TryRequestAutoTradeSellReplaceOrDefer(
                        request,
                        latestOrder,
                        string.IsNullOrWhiteSpace(source) ? "cooldown-release" : source + "/cooldown-release");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[매도미체결 정정대기 오류] " + ex.Message);
                    AddAutoTradeAnalysisLog("[매도미체결 정정대기 오류] " + ex.Message);
                }
                finally
                {
                    bool removed;
                    _autoTradeSellReplaceDeferMap.TryRemove(key, out removed);
                }
            });
        }

        private void RequestAutoTradeSellReplaceOrder(
            OrderRequest request,
            CancelableAutoTradeOrderResult cancelableOrder,
            SellReplaceDecision decision,
            string source)
        {
            if (request == null || request.Signal == null || cancelableOrder == null || decision == null)
                return;

            if (request.Signal.Action != TradeActionType.Sell)
                return;

            if (string.IsNullOrWhiteSpace(cancelableOrder.OrderNo) || cancelableOrder.UntradedQty <= 0)
                return;

            string codeKey = NormalizeMoneyCreatorCode(request.Signal.Code);
            if (string.IsNullOrWhiteSpace(codeKey))
                return;

            int orderPrice = decision.UseMarketPrice ? 0 : decision.NewPrice;
            string hogaType = string.IsNullOrWhiteSpace(decision.HogaType) ? "00" : decision.HogaType;

            if (!decision.UseMarketPrice && orderPrice <= 0)
            {
                AddAutoTradeAnalysisLog(
                    $"[매도미체결 정정불가] {request.Signal.Code} / 원주문={cancelableOrder.OrderNo} / 정정가 오류 {orderPrice:N0}");
                return;
            }

            Application.Current?.Dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(async () =>
                {
                    _lastAutoTradeSellReplaceAtByCode[codeKey] = DateTime.Now;
                    int attemptCount = _autoTradeSellReplaceAttemptByOrderNo.AddOrUpdate(
                        cancelableOrder.OrderNo,
                        1,
                        (orderNo, old) => old + 1);

                    int result = _marketApi.SendOrder(
                        WindowId,
                        ScreenNo,
                        SelectedAccount,
                        6,
                        request.Signal.Code,
                        cancelableOrder.UntradedQty,
                        orderPrice,
                        hogaType,
                        cancelableOrder.OrderNo);

                    if (result == 0)
                    {
                        _autoTradeController?.MarkSellReplaceRequested(request.Signal.Code, cancelableOrder.OrderNo);

                        AddAutoTradeAnalysisLog(
                            $"[매도미체결 정정] {request.Signal.Code} / {request.Signal.StrategyName} / " +
                            $"{cancelableOrder.UntradedQty}주 / " +
                            (decision.UseMarketPrice ? "시장가" : orderPrice.ToString("N0") + "원") +
                            $" / 원주문={cancelableOrder.OrderNo} / 시도={attemptCount} / {decision.Reason} / source={source}");

                        await RequestAccountStateRefreshAfterTrade("자동매도 미체결 정정 " + request.Signal.Code);
                    }
                    else
                    {
                        AddAutoTradeAnalysisLog(
                            $"[매도미체결 정정실패] {request.Signal.Code} / result={result} / " +
                            $"원주문={cancelableOrder.OrderNo} / 시도={attemptCount} / {decision.Reason}");
                    }
                }));
        }

        private double ResolveOrderElapsedSeconds(
            CancelableAutoTradeOrderResult cancelableOrder,
            OrderRequest request)
        {
            DateTime orderTime = DateTime.MinValue;

            if (cancelableOrder != null && cancelableOrder.Order != null)
                orderTime = ParseOrderTime(cancelableOrder.Order.OrderTime);

            if (orderTime == DateTime.MinValue && request != null && request.CreatedAt != default(DateTime))
                orderTime = request.CreatedAt;

            if (orderTime == DateTime.MinValue)
                return 0.0;

            return Math.Max(0.0, (DateTime.Now - orderTime).TotalSeconds);
        }

        private int ResolveCurrentBestBidPriceForAutoTrade(string code)
        {
            string targetCode = NormalizeMoneyCreatorCode(code);
            if (string.IsNullOrWhiteSpace(targetCode))
                return 0;

            HogaData hoga = null;
            lock (_latestHogaLock)
            {
                if (_latestHogaForAutoTrade != null &&
                    NormalizeMoneyCreatorCode(_latestHogaForAutoTrade.Code) == targetCode)
                {
                    hoga = _latestHogaForAutoTrade;
                }
            }

            if (hoga == null || hoga.Bids == null || hoga.Bids.Count == 0)
                return 0;

            double bestBid = 0;
            foreach (var bid in hoga.Bids)
            {
                if (bid == null || bid.Price <= 0)
                    continue;

                if (bestBid <= 0 || bid.Price > bestBid)
                    bestBid = bid.Price;
            }

            return bestBid <= 0 ? 0 : (int)Math.Round(bestBid);
        }

        private bool TryRequestAutoTradeBuyRepriceOrDeferCancel(
            OrderRequest request,
            CancelableAutoTradeOrderResult cancelableOrder)
        {
            if (request == null || request.Signal == null || cancelableOrder == null)
                return false;

            if (request.Signal.Action != TradeActionType.Buy)
                return false;

            if (string.IsNullOrWhiteSpace(cancelableOrder.OrderNo) || cancelableOrder.UntradedQty <= 0)
                return false;

            int attemptCount = _autoTradeBuyRepriceAttemptByOrderNo.AddOrUpdate(
                cancelableOrder.OrderNo,
                1,
                (orderNo, old) => old + 1);

            if (attemptCount > AutoTradeBuyRepriceMaxAttemptsPerOrder)
            {
                AddAutoTradeAnalysisLog(
                    $"[매수미체결 정정제외] {request.Signal.Code} / 원주문={cancelableOrder.OrderNo} / " +
                    $"시도횟수 초과 {attemptCount}/{AutoTradeBuyRepriceMaxAttemptsPerOrder}");
                return false;
            }

            int originalPrice = ResolveCancelableOrderPrice(cancelableOrder, request);
            int currentAsk = ResolveCurrentBestAskPriceForAutoTrade(request.Signal.Code);
            FeatureSnapshot latestFeature = GetLatestAutoTradeFeatureSnapshot(request.Signal.Code);

            BuyRepriceDecision decision = _autoTradeController == null
                ? BuyRepriceDecision.Cancel("AutoTrading 판단기 없음")
                : _autoTradeController.EvaluateUnfilledBuyReprice(
                    request,
                    latestFeature,
                    originalPrice,
                    currentAsk,
                    cancelableOrder.UntradedQty);

            if (decision == null || decision.Action != BuyRepriceAction.Reprice)
            {
                AddAutoTradeAnalysisLog(
                    $"[매수미체결 정정불가] {request.Signal.Code} / 원주문={cancelableOrder.OrderNo} / " +
                    $"order={originalPrice:N0} / ask={currentAsk:N0} / " +
                    $"reason={decision?.Reason ?? "판단 없음"} / 취소전환");
                return false;
            }

            int newPrice = decision.NewPrice;
            if (newPrice <= 0 || newPrice == originalPrice)
            {
                AddAutoTradeAnalysisLog(
                    $"[매수미체결 정정불가] {request.Signal.Code} / 원주문={cancelableOrder.OrderNo} / " +
                    $"정정가 오류 {newPrice:N0} / 취소전환");
                return false;
            }

            long oldReserve = CalculateBuyReserveCashAmount(cancelableOrder.UntradedQty, originalPrice);
            long newReserve = CalculateBuyReserveCashAmount(cancelableOrder.UntradedQty, newPrice);
            long extraReserve = Math.Max(0, newReserve - oldReserve);
            long availableCash = GetAutoTradeAvailableCash();

            if (extraReserve > availableCash)
            {
                AddAutoTradeAnalysisLog(
                    $"[매수미체결 정정불가] {request.Signal.Code} / 원주문={cancelableOrder.OrderNo} / " +
                    $"추가예약={extraReserve:N0} > 사용가능={availableCash:N0} / 취소전환");
                return false;
            }

            RequestAutoTradeBuyRepriceOrder(
                request,
                cancelableOrder,
                newPrice,
                decision.Reason);

            return true;
        }

        private FeatureSnapshot GetLatestAutoTradeFeatureSnapshot(string code)
        {
            string key = NormalizeMoneyCreatorCode(code);
            if (string.IsNullOrWhiteSpace(key))
                return null;

            lock (_moneyCreatorPortfolioSyncRoot)
            {
                FeatureSnapshot feature;
                _moneyCreatorLatestFeatureByCode.TryGetValue(key, out feature);
                return feature;
            }
        }

        private int ResolveCancelableOrderPrice(
            CancelableAutoTradeOrderResult cancelableOrder,
            OrderRequest request)
        {
            int price = 0;

            if (cancelableOrder != null && cancelableOrder.Order != null)
                price = ResolveOrderPriceForCash(cancelableOrder.Order);

            if (price <= 0 && request != null && request.Signal != null && request.Signal.Price > 0)
                price = (int)Math.Round(request.Signal.Price);

            return Math.Max(0, price);
        }

        private int ResolveCurrentBestAskPriceForAutoTrade(string code)
        {
            string targetCode = NormalizeMoneyCreatorCode(code);
            if (string.IsNullOrWhiteSpace(targetCode))
                return 0;

            HogaData hoga = null;
            lock (_latestHogaLock)
            {
                if (_latestHogaForAutoTrade != null &&
                    NormalizeMoneyCreatorCode(_latestHogaForAutoTrade.Code) == targetCode)
                {
                    hoga = _latestHogaForAutoTrade;
                }
            }

            if (hoga == null || hoga.Asks == null || hoga.Asks.Count == 0)
                return 0;

            double bestAsk = 0;
            foreach (var ask in hoga.Asks)
            {
                if (ask == null || ask.Price <= 0)
                    continue;

                if (bestAsk <= 0 || ask.Price < bestAsk)
                    bestAsk = ask.Price;
            }

            return bestAsk <= 0 ? 0 : (int)Math.Round(bestAsk);
        }

        private void RequestAutoTradeBuyRepriceOrder(
            OrderRequest request,
            CancelableAutoTradeOrderResult cancelableOrder,
            int newPrice,
            string reason)
        {
            if (request == null || request.Signal == null || cancelableOrder == null)
                return;

            if (request.Signal.Action != TradeActionType.Buy)
                return;

            if (string.IsNullOrWhiteSpace(cancelableOrder.OrderNo) || cancelableOrder.UntradedQty <= 0 || newPrice <= 0)
                return;

            Application.Current?.Dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(async () =>
                {
                    int result = _marketApi.SendOrder(
                        WindowId,
                        ScreenNo,
                        SelectedAccount,
                        5,
                        request.Signal.Code,
                        cancelableOrder.UntradedQty,
                        newPrice,
                        "00",
                        cancelableOrder.OrderNo);

                    if (result == 0)
                    {
                        request.Signal.Price = newPrice;
                        request.HogaType = "00";

                        if (cancelableOrder.Order != null)
                        {
                            AdjustMoneyCreatorCashForBuyModifyAccepted(
                                "자동매수 미체결 정정",
                                cancelableOrder.Order,
                                cancelableOrder.UntradedQty,
                                newPrice);
                        }

                        AddAutoTradeAnalysisLog(
                            $"[매수미체결 정정] {request.Signal.Code} / {request.Signal.StrategyName} / " +
                            $"{cancelableOrder.UntradedQty}주 / {newPrice:N0}원 / 원주문={cancelableOrder.OrderNo} / {reason}");

                        await RequestAccountStateRefreshAfterTrade("자동매수 미체결 정정 " + request.Signal.Code);
                        ScheduleAutoTradeCancelIfUnfilled(request);
                    }
                    else
                    {
                        AddAutoTradeAnalysisLog(
                            $"[매수미체결 정정실패] {request.Signal.Code} / result={result} / " +
                            $"{cancelableOrder.UntradedQty}주 / {newPrice:N0}원 / 원주문={cancelableOrder.OrderNo}");

                        int cancelType = GetCancelOrderType(request.Signal.Action);
                        if (cancelType > 0)
                        {
                            RequestAutoTradeCancelOrder(
                                request,
                                cancelableOrder.OrderNo,
                                cancelableOrder.UntradedQty,
                                cancelType);
                        }
                    }
                }));
        }

        private void RequestAutoTradeCancelOrder(
            OrderRequest request,
            string orderNo,
            int untradedQty,
            int cancelType)
        {
            if (request == null || request.Signal == null)
                return;

            if (request.Signal.Action != TradeActionType.Buy || cancelType != 3)
            {
                AddAutoTradeAnalysisLog(
                    $"[자동취소 차단] {request.Signal.Code} / {request.Signal.Action} / " +
                    "Sell 자동취소 금지");
                return;
            }

            Application.Current?.Dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(async () =>
                {
                    int result = _marketApi.SendOrder(
                        WindowId,
                        ScreenNo,
                        SelectedAccount,
                        cancelType,
                        request.Signal.Code,
                        untradedQty,
                        0,
                        "00",
                        orderNo);

                    if (result == 0)
                    {
                        // 취소 요청 전송 성공 시점에 남은 미체결 수량만 예약금에서 즉시 해제한다.
                        // 키움 미체결 목록이 바로 비거나 취소완료 row가 안 와도 종목 잔여할당금이 복구되어야 한다.
                        _autoTradeController?.MarkCancelRequested(request);
                        MarkMoneyCreatorOrderCanceledLocally(orderNo, request.Signal.Code, untradedQty);
                        RestoreMoneyCreatorCashForAutoTradeBuyCancel(
                            "자동취소 전송성공",
                            request,
                            untradedQty);
                        ReleaseMoneyCreatorAcceptedBuyReservation(
                            request.Signal.Code,
                            CalculateBuyReserveCashAmount(untradedQty, request.Signal.Price <= 0 ? 0 : (int)Math.Round(request.Signal.Price)),
                            "자동취소 전송성공");
                        _autoTradeController?.ReleaseBuyReservation(
                            request.Signal.Code,
                            untradedQty,
                            request.Signal.Price,
                            "자동취소 전송성공");
                        RefreshCurrentSymbolAllocationTextFromCore();

                        int removedRepriceAttempt;
                        _autoTradeBuyRepriceAttemptByOrderNo.TryRemove(orderNo, out removedRepriceAttempt);

                        AddAutoTradeAnalysisLog(
                            $"[자동취소] {request.Signal.Code} / 미체결 {untradedQty}주 / 원주문={orderNo}");

                        await RequestAccountStateRefreshAfterTrade("자동취소 요청 " + request.Signal.Code);
                    }
                    else
                    {
                        AddAutoTradeAnalysisLog(
                            $"[자동취소 실패] {request.Signal.Code} / result={result} / 원주문={orderNo}");
                    }
                }));
        }
        private sealed class CancelableAutoTradeOrderResult
        {
            public bool Success { get; set; }
            public string OrderNo { get; set; }
            public int UntradedQty { get; set; }
            public OrderInfo Order { get; set; }

            public static CancelableAutoTradeOrderResult Fail()
            {
                return new CancelableAutoTradeOrderResult
                {
                    Success = false,
                    OrderNo = null,
                    UntradedQty = 0,
                    Order = null
                };
            }

            public static CancelableAutoTradeOrderResult SuccessResult(
                string orderNo,
                int untradedQty,
                OrderInfo order = null)
            {
                return new CancelableAutoTradeOrderResult
                {
                    Success = !string.IsNullOrWhiteSpace(orderNo) && untradedQty > 0,
                    OrderNo = orderNo,
                    UntradedQty = untradedQty,
                    Order = order
                };
            }
        }

        // ============================================================
        // Source: MainWindowViewModel.AutoTrading.PortfolioAllocation.cs
        // ============================================================
        private void RememberMoneyCreatorFeature(FeatureSnapshot feature)
        {
            if (feature == null || string.IsNullOrWhiteSpace(feature.Code))
                return;

            string code = NormalizeMoneyCreatorCode(feature.Code);
            if (string.IsNullOrWhiteSpace(code))
                return;

            lock (_moneyCreatorPortfolioSyncRoot)
                _moneyCreatorLatestFeatureByCode[code] = feature;
        }
        private static bool IsMoneyCreatorAccountRefreshRunning()
        {
            lock (_moneyCreatorAccountRefreshSyncRoot)
                return _moneyCreatorAccountRefreshSequenceRunning;
        }

        private static bool IsMoneyCreatorPortfolioFullRebuildRunning()
        {
            lock (_moneyCreatorPortfolioSyncRoot)
                return _moneyCreatorPortfolioFullRebuildRunning;
        }

        private static bool IsMoneyCreatorAccountRefreshCompletionReason(string reason)
        {
            return !string.IsNullOrWhiteSpace(reason) && reason.Contains("계좌갱신완료");
        }

        private bool TryBeginMoneyCreatorPortfolioFullRebuild(string reason)
        {
            lock (_moneyCreatorPortfolioSyncRoot)
            {
                if (_moneyCreatorPortfolioFullRebuildRunning)
                {
                    if (!string.IsNullOrWhiteSpace(reason))
                        AddAutoTradeAnalysisLog("[MoneyCreator자산배분 스킵] 총할당 재산정 진행중 / " + reason);

                    return false;
                }

                _moneyCreatorPortfolioFullRebuildRunning = true;
                return true;
            }
        }

        private static void EndMoneyCreatorPortfolioFullRebuild()
        {
            lock (_moneyCreatorPortfolioSyncRoot)
                _moneyCreatorPortfolioFullRebuildRunning = false;
        }

        private bool ShouldForceMoneyCreatorPortfolioAllocationAfterAccountRefresh(List<MainWindowViewModel> viewModels)
        {
            List<string> pendingSellCodes;
            lock (_moneyCreatorAccountRefreshSyncRoot)
            {
                pendingSellCodes = _moneyCreatorPendingSellRefreshCodes.ToList();
                _moneyCreatorPendingSellRefreshCodes.Clear();
            }

            if (pendingSellCodes.Count == 0)
                return false;

            var holdingsByCode = BuildMoneyCreatorHoldingMap(viewModels);

            return pendingSellCodes.Any(code =>
            {
                string key = NormalizeMoneyCreatorCode(code);
                if (string.IsNullOrWhiteSpace(key))
                    return false;

                HoldingInfo holding;
                return !holdingsByCode.TryGetValue(key, out holding) || holding == null || holding.Quantity <= 0;
            });
        }

        private void OnMoneyCreatorAccountRefreshCompleted(string reason)
        {
            var app = Application.Current;
            if (app != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(
                    DispatcherPriority.Send,
                    new Action(() => OnMoneyCreatorAccountRefreshCompleted(reason)));
                return;
            }

            var viewModels = GetMoneyCreatorAutoTradeViewModelsOnUiThread();
            bool forceRebuild = ShouldForceMoneyCreatorPortfolioAllocationAfterAccountRefresh(viewModels);
            string completedReason = string.IsNullOrWhiteSpace(reason)
                ? "계좌갱신완료"
                : "계좌갱신완료 " + reason;

            FinalizeMoneyCreatorSellCashAdjustmentAfterAccountRefresh(completedReason);

            if (forceRebuild)
                RebuildMoneyCreatorPortfolioAllocations(completedReason + " / 매도완료", true);
            else
                RefreshMoneyCreatorPortfolioAllocationUsage(viewModels, completedReason);

            foreach (var vm in viewModels.Where(x => x != null))
                vm.RefreshCurrentSymbolAllocationTextFromCore();

            AddAutoTradeAnalysisLog(
                forceRebuild
                    ? "[MoneyCreator자산배분] 계좌갱신 완료 후 매도완료 force 재할당 / " + reason
                    : "[MoneyCreator자산배분] 계좌갱신 완료 후 사용량 갱신 / " + reason);
        }

        private void FinalizeMoneyCreatorSellCashAdjustmentAfterAccountRefresh(string reason)
        {
            long removedAdjustment;
            long rawCash;

            lock (_moneyCreatorPortfolioSyncRoot)
            {
                removedAdjustment = Math.Max(0, _moneyCreatorInternalCashAdjustment);
                if (removedAdjustment <= 0)
                    return;

                // CASH/HOLDINGS/UNFILLED 재조회가 모두 끝난 시점에는
                // 임시 예상치보다 키움 예수금 원본을 최종 기준으로 사용한다.
                _moneyCreatorInternalCashAdjustment = 0;
                _moneyCreatorCashSyncWaitMode = false;
                _moneyCreatorCashSyncWaitReason = null;
                rawCash = Math.Max(0, _moneyCreatorKiwoomCashRaw);
            }

            var viewModels = GetMoneyCreatorAutoTradeViewModelsOnUiThread();
            long reserved = CalculateMoneyCreatorEffectiveBuyReservedAmount(viewModels);
            long effectiveCash = Math.Max(0, rawCash - reserved);

            ApplyMoneyCreatorSharedCashSnapshot(
                viewModels,
                rawCash,
                effectiveCash,
                reserved,
                reason + " / 매도임시예수금 최종정산");

            AddAutoTradeAnalysisLog(
                $"[매도체결 현금최종정산] 임시조정제거={removedAdjustment:N0} / " +
                $"키움원본={rawCash:N0} / 사용가능={effectiveCash:N0}");
        }

        private void RegisterMoneyCreatorPendingSellRefreshCode(string code)
        {
            string key = NormalizeMoneyCreatorCode(code);
            if (string.IsNullOrWhiteSpace(key))
                return;

            lock (_moneyCreatorAccountRefreshSyncRoot)
                _moneyCreatorPendingSellRefreshCodes.Add(key);
        }

        private void MarkMoneyCreatorAccountRefreshPartReceived(string part, string eventReason)
        {
            string completedReason = null;
            bool completed = false;

            lock (_moneyCreatorAccountRefreshSyncRoot)
            {
                if (!_moneyCreatorAccountRefreshSequenceRunning)
                    return;

                if (string.Equals(part, "CASH", StringComparison.OrdinalIgnoreCase))
                    _moneyCreatorAccountRefreshCashReceived = true;
                else if (string.Equals(part, "HOLDINGS", StringComparison.OrdinalIgnoreCase))
                    _moneyCreatorAccountRefreshHoldingsReceived = true;
                else if (string.Equals(part, "UNFILLED", StringComparison.OrdinalIgnoreCase))
                    _moneyCreatorAccountRefreshUnfilledReceived = true;

                if (_moneyCreatorAccountRefreshCashReceived &&
                    _moneyCreatorAccountRefreshHoldingsReceived &&
                    _moneyCreatorAccountRefreshUnfilledReceived)
                {
                    completed = true;
                    completedReason = string.IsNullOrWhiteSpace(_moneyCreatorAccountRefreshReason)
                        ? eventReason
                        : _moneyCreatorAccountRefreshReason;
                    _moneyCreatorAccountRefreshSequenceRunning = false;
                    _moneyCreatorAccountRefreshCashReceived = false;
                    _moneyCreatorAccountRefreshHoldingsReceived = false;
                    _moneyCreatorAccountRefreshUnfilledReceived = false;
                    _moneyCreatorAccountRefreshReason = null;
                    _moneyCreatorAccountRefreshStartedAt = DateTime.MinValue;
                }
            }

            if (completed)
                OnMoneyCreatorAccountRefreshCompleted(completedReason);
        }

        private void EndMoneyCreatorAccountRefreshSequenceByTimeout(string reason)
        {
            bool timedOut = false;

            lock (_moneyCreatorAccountRefreshSyncRoot)
            {
                if (_moneyCreatorAccountRefreshSequenceRunning)
                {
                    timedOut = true;
                    _moneyCreatorAccountRefreshSequenceRunning = false;
                    _moneyCreatorAccountRefreshCashReceived = false;
                    _moneyCreatorAccountRefreshHoldingsReceived = false;
                    _moneyCreatorAccountRefreshUnfilledReceived = false;
                    _moneyCreatorAccountRefreshReason = null;
                    _moneyCreatorAccountRefreshStartedAt = DateTime.MinValue;
                }
            }

            if (timedOut && !string.IsNullOrWhiteSpace(reason))
                AddAutoTradeAnalysisLog("[계좌갱신완료 대기종료] 응답 일부 미수신 / " + reason);
        }

        private void RebuildMoneyCreatorPortfolioAllocations(string reason = null, bool force = false)
        {
            var app = Application.Current;
            if (app != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.Invoke(() => RebuildMoneyCreatorPortfolioAllocations(reason, force));
                return;
            }

            try
            {
                var viewModels = GetMoneyCreatorAutoTradeViewModelsOnUiThread();
                if (viewModels.Count == 0)
                    return;

                DateTime now = DateTime.Now;
                bool hasAllocation;
                lock (_moneyCreatorPortfolioSyncRoot)
                    hasAllocation = _moneyCreatorPortfolioAllocationByCode.Count > 0;

                var activeItems = viewModels
                    .Where(vm =>
                        vm != null &&
                        vm._autoTradeSettings != null &&
                        vm._autoTradeSettings.Enabled &&
                        vm.Context != null &&
                        !string.IsNullOrWhiteSpace(vm.Context.Code))
                    .Select(vm => new
                    {
                        Code = NormalizeMoneyCreatorCode(vm.Context.Code),
                        Owner = vm
                    })
                    .Where(x => !string.IsNullOrWhiteSpace(x.Code))
                    .GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                if (activeItems.Count == 0)
                {
                    lock (_moneyCreatorPortfolioSyncRoot)
                    {
                        _moneyCreatorPortfolioAllocationByCode.Clear();
                        _moneyCreatorLastConfirmedTotalAsset = 0;
                        _moneyCreatorLastConfirmedTotalAssetReady = false;
                    }

                    PushMoneyCreatorPortfolioAllocationsToCore(viewModels);
                    RefreshCurrentSymbolAllocationTextFromCore();
                    return;
                }

                bool activeSetChanged;
                lock (_moneyCreatorPortfolioSyncRoot)
                {
                    activeSetChanged =
                        _moneyCreatorPortfolioAllocationByCode.Count != activeItems.Count ||
                        activeItems.Any(x => !_moneyCreatorPortfolioAllocationByCode.ContainsKey(x.Code));
                }

                bool allocationExpired =
                    hasAllocation &&
                    (_lastMoneyCreatorPortfolioAllocationAt == DateTime.MinValue ||
                     (now - _lastMoneyCreatorPortfolioAllocationAt) >= TimeSpan.FromSeconds(MoneyCreatorPortfolioFallbackRebuildSeconds));

                bool allowFullRebuild =
                    force ||
                    !hasAllocation ||
                    activeSetChanged ||
                    allocationExpired ||
                    ShouldForceMoneyCreatorPortfolioAllocation(reason);

                if (!allowFullRebuild)
                {
                    if (RefreshMoneyCreatorPortfolioAllocationUsage(viewModels, reason))
                        return;
                }

                if (IsMoneyCreatorAccountRefreshRunning() && !IsMoneyCreatorAccountRefreshCompletionReason(reason))
                {
                    bool cashSyncWait;
                    lock (_moneyCreatorPortfolioSyncRoot)
                        cashSyncWait = _moneyCreatorCashSyncWaitMode;

                    if (cashSyncWait)
                    {
                        if (!string.IsNullOrWhiteSpace(reason))
                            AddAutoTradeAnalysisLog("[MoneyCreator자산배분 스킵] 키움예수금 동기화 대기 / " + reason);
                        return;
                    }
                }

                if (!TryBeginMoneyCreatorPortfolioFullRebuild(reason))
                    return;

                try
                {
                    long cash = GetMoneyCreatorSharedAvailableCash(viewModels);

                    var holdingsByCode = BuildMoneyCreatorHoldingMap(viewModels);
                    var pendingReservedByCode = BuildMoneyCreatorPendingBuyMap(viewModels);

                    Dictionary<string, double> tempReservedByCode;
                    Dictionary<string, FeatureSnapshot> latestFeatures;
                    lock (_moneyCreatorPortfolioSyncRoot)
                    {
                        tempReservedByCode = new Dictionary<string, double>(_moneyCreatorAcceptedBuyReservedByCode, StringComparer.OrdinalIgnoreCase);
                        latestFeatures = new Dictionary<string, FeatureSnapshot>(_moneyCreatorLatestFeatureByCode, StringComparer.OrdinalIgnoreCase);
                    }

                    var states = new List<MoneyCreatorPortfolioRuntimeState>();

                    foreach (var item in activeItems)
                    {
                        FeatureSnapshot feature;
                        latestFeatures.TryGetValue(item.Code, out feature);

                        HoldingInfo holding;
                        holdingsByCode.TryGetValue(item.Code, out holding);

                        double positionAmount = GetMoneyCreatorHoldingPositionAmount(holding);

                        double pendingReserved;
                        pendingReservedByCode.TryGetValue(item.Code, out pendingReserved);

                        double tempReserved;
                        tempReservedByCode.TryGetValue(item.Code, out tempReserved);

                        double reservedAmount = Math.Max(Math.Max(0, pendingReserved), Math.Max(0, tempReserved));

                        var state = new MoneyCreatorPortfolioRuntimeState
                        {
                            Code = item.Code,
                            Owner = item.Owner,
                            Feature = feature,
                            Settings = item.Owner._autoTradeSettings,
                            Weight = CalculateMoneyCreatorSymbolWeight(feature, item.Owner._autoTradeSettings),
                            PositionAmount = Math.Max(0, positionAmount),
                            ReservedAmount = reservedAmount
                        };

                        if (state.Weight <= 0)
                            state.Weight = 1.0;

                        states.Add(state);
                    }

                    double totalPositionAmount = states.Sum(x => Math.Max(0, x.PositionAmount));
                    double totalPendingReservedAmount = pendingReservedByCode.Values.Sum(x => Math.Max(0, x));
                    double totalTempReservedAmount = tempReservedByCode.Values.Sum(x => Math.Max(0, x));
                    double totalReservedAmount = states.Sum(x => Math.Max(0, x.ReservedAmount));
                    double totalAssetCandidate = Math.Max(0, cash + totalPositionAmount + totalReservedAmount);
                    double totalOperatingCapital;

                    lock (_moneyCreatorPortfolioSyncRoot)
                    {
                        if (!_moneyCreatorLastConfirmedTotalAssetReady || allowFullRebuild)
                        {
                            _moneyCreatorLastConfirmedTotalAsset = totalAssetCandidate;
                            _moneyCreatorLastConfirmedTotalAssetReady = totalAssetCandidate > 0;
                        }

                        totalOperatingCapital = _moneyCreatorLastConfirmedTotalAssetReady
                            ? _moneyCreatorLastConfirmedTotalAsset
                            : totalAssetCandidate;
                    }

                    ApplyMoneyCreatorWeightedBudgets(states, totalOperatingCapital);

                    var newAllocations = new Dictionary<string, MoneyCreatorPortfolioAllocationState>(StringComparer.OrdinalIgnoreCase);
                    foreach (var state in states)
                    {
                        string allocationReason =
                            (string.IsNullOrWhiteSpace(reason) ? "MoneyCreator자산배분" : reason) +
                            ": 총할당확정" +
                            ", 현금=" + cash.ToString("N0") +
                            ", 전체보유투입=" + totalPositionAmount.ToString("N0") +
                            ", 전체미체결예약=" + totalPendingReservedAmount.ToString("N0") +
                            ", 주문접수예약=" + totalTempReservedAmount.ToString("N0") +
                            ", 적용예약=" + totalReservedAmount.ToString("N0") +
                            ", 산출총자산=" + totalAssetCandidate.ToString("N0") +
                            ", 고정총자산=" + totalOperatingCapital.ToString("N0") +
                            ", 가중=" + state.Weight.ToString("F2") +
                            ", 종목예산=" + state.Budget.ToString("N0") +
                            ", 보유=" + state.PositionAmount.ToString("N0") +
                            ", 예약=" + state.ReservedAmount.ToString("N0") +
                            ", 잔여=" + state.RemainingBudget.ToString("N0") +
                            (state.IsOverAllocated ? ", 예산초과" : string.Empty);

                        newAllocations[state.Code] = new MoneyCreatorPortfolioAllocationState
                        {
                            Code = state.Code,
                            TotalOperatingCapital = totalOperatingCapital,
                            Weight = state.Weight,
                            Budget = state.Budget,
                            RemainingBudget = state.RemainingBudget,
                            ReservedAmount = state.ReservedAmount,
                            PositionAmount = state.PositionAmount,
                            IsOverAllocated = state.IsOverAllocated,
                            UpdatedAt = now,
                            Reason = allocationReason
                        };
                    }

                    lock (_moneyCreatorPortfolioSyncRoot)
                    {
                        _moneyCreatorPortfolioAllocationByCode.Clear();
                        foreach (var pair in newAllocations)
                            _moneyCreatorPortfolioAllocationByCode[pair.Key] = pair.Value;

                        _lastMoneyCreatorPortfolioAllocationAt = now;
                        _moneyCreatorPortfolioAllocationVersion++;
                        _lastMoneyCreatorPortfolioUsageRefreshAt = now;
                    }

                    PushMoneyCreatorPortfolioAllocationsToCore(viewModels);
                    RefreshCurrentSymbolAllocationTextFromCore();
                }
                finally
                {
                    EndMoneyCreatorPortfolioFullRebuild();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MoneyCreator 자산배분 실패] " + ex.Message);
            }
        }

        private bool RefreshMoneyCreatorPortfolioAllocationUsage(
            List<MainWindowViewModel> viewModels,
            string reason)
        {
            if (IsMoneyCreatorAccountRefreshRunning())
            {
                if (!string.IsNullOrWhiteSpace(reason))
                    AddAutoTradeAnalysisLog("[MoneyCreator잔여갱신 스킵] 계좌갱신 진행중 / " + reason);
                return true;
            }

            if (IsMoneyCreatorPortfolioFullRebuildRunning())
                return true;

            if (viewModels == null)
                return false;

            var activeCodes = viewModels
                .Where(vm =>
                    vm != null &&
                    vm._autoTradeSettings != null &&
                    vm._autoTradeSettings.Enabled &&
                    vm.Context != null &&
                    !string.IsNullOrWhiteSpace(vm.Context.Code))
                .Select(vm => NormalizeMoneyCreatorCode(vm.Context.Code))
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (activeCodes.Count == 0)
            {
                lock (_moneyCreatorPortfolioSyncRoot)
                    _moneyCreatorPortfolioAllocationByCode.Clear();

                PushMoneyCreatorPortfolioAllocationsToCore(viewModels);
                RefreshCurrentSymbolAllocationTextFromCore();
                return true;
            }

            Dictionary<string, MoneyCreatorPortfolioAllocationState> snapshot;
            lock (_moneyCreatorPortfolioSyncRoot)
                snapshot = new Dictionary<string, MoneyCreatorPortfolioAllocationState>(_moneyCreatorPortfolioAllocationByCode, StringComparer.OrdinalIgnoreCase);

            if (snapshot.Count != activeCodes.Count || activeCodes.Any(code => !snapshot.ContainsKey(code)))
                return false;

            long cash = GetMoneyCreatorSharedAvailableCash(viewModels);

            var holdingsByCode = BuildMoneyCreatorHoldingMap(viewModels);
            var pendingReservedByCode = BuildMoneyCreatorPendingBuyMap(viewModels);

            Dictionary<string, double> tempReservedByCode;
            lock (_moneyCreatorPortfolioSyncRoot)
                tempReservedByCode = new Dictionary<string, double>(_moneyCreatorAcceptedBuyReservedByCode, StringComparer.OrdinalIgnoreCase);

            double totalPositionAmount = holdingsByCode.Values.Sum(GetMoneyCreatorHoldingPositionAmount);
            double totalPendingReservedAmount = pendingReservedByCode.Values.Sum(x => Math.Max(0, x));
            double totalTempReservedAmount = tempReservedByCode.Values.Sum(x => Math.Max(0, x));

            var updated = new Dictionary<string, MoneyCreatorPortfolioAllocationState>(StringComparer.OrdinalIgnoreCase);

            foreach (string code in activeCodes)
            {
                MoneyCreatorPortfolioAllocationState oldAllocation;
                if (!snapshot.TryGetValue(code, out oldAllocation) || oldAllocation == null)
                    return false;

                HoldingInfo holding;
                holdingsByCode.TryGetValue(code, out holding);

                double positionAmount = GetMoneyCreatorHoldingPositionAmount(holding);

                double pendingReserved;
                pendingReservedByCode.TryGetValue(code, out pendingReserved);

                double tempReserved;
                tempReservedByCode.TryGetValue(code, out tempReserved);

                double reservedAmount = Math.Max(Math.Max(0, pendingReserved), Math.Max(0, tempReserved));
                double usedAmount = Math.Max(0, positionAmount + reservedAmount);
                double budget = Math.Max(0, oldAllocation.Budget);
                double remainingBudget = Math.Max(0, budget - usedAmount);

                string allocationReason =
                    (string.IsNullOrWhiteSpace(reason) ? "MoneyCreator잔여갱신" : reason) +
                    ": 기존총할당유지" +
                    ", 현금=" + cash.ToString("N0") +
                    ", 전체보유투입=" + totalPositionAmount.ToString("N0") +
                    ", 전체미체결예약=" + totalPendingReservedAmount.ToString("N0") +
                    ", 주문접수예약=" + totalTempReservedAmount.ToString("N0") +
                    ", 종목예산=" + budget.ToString("N0") +
                    ", 보유=" + positionAmount.ToString("N0") +
                    ", 예약=" + reservedAmount.ToString("N0") +
                    ", 잔여=" + remainingBudget.ToString("N0") +
                    ", 다음총할당재산정=" + _lastMoneyCreatorPortfolioAllocationAt.AddSeconds(MoneyCreatorPortfolioFallbackRebuildSeconds).ToString("HH:mm:ss");

                updated[code] = new MoneyCreatorPortfolioAllocationState
                {
                    Code = code,
                    TotalOperatingCapital = oldAllocation.TotalOperatingCapital,
                    Weight = oldAllocation.Weight,
                    Budget = budget,
                    RemainingBudget = remainingBudget,
                    ReservedAmount = reservedAmount,
                    PositionAmount = Math.Max(0, positionAmount),
                    IsOverAllocated = oldAllocation.IsOverAllocated || usedAmount > budget,
                    UpdatedAt = oldAllocation.UpdatedAt,
                    Reason = allocationReason
                };
            }

            lock (_moneyCreatorPortfolioSyncRoot)
            {
                _moneyCreatorPortfolioAllocationByCode.Clear();
                foreach (var pair in updated)
                    _moneyCreatorPortfolioAllocationByCode[pair.Key] = pair.Value;

                _lastMoneyCreatorPortfolioUsageRefreshAt = DateTime.Now;
            }

            PushMoneyCreatorPortfolioAllocationsToCore(viewModels);
            RefreshCurrentSymbolAllocationTextFromCore();
            return true;
        }

        private static List<MainWindowViewModel> GetMoneyCreatorAutoTradeViewModelsOnUiThread()
        {
            var app = Application.Current;
            if (app == null)
                return new List<MainWindowViewModel>();

            return app.Windows
                .Cast<Window>()
                .Select(w => w.DataContext as MainWindowViewModel)
                .Where(vm => vm != null)
                .Distinct()
                .ToList();
        }

        private static Dictionary<string, HoldingInfo> BuildMoneyCreatorHoldingMap(List<MainWindowViewModel> viewModels)
        {
            var result = new Dictionary<string, HoldingInfo>(StringComparer.OrdinalIgnoreCase);
            if (viewModels == null)
                return result;

            foreach (var holding in viewModels
                .Where(vm => vm != null && vm.AccountHoldings != null)
                .SelectMany(vm => vm.AccountHoldings)
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Code)))
            {
                string code = NormalizeMoneyCreatorCode(holding.Code);
                if (string.IsNullOrWhiteSpace(code))
                    continue;

                HoldingInfo existing;
                if (!result.TryGetValue(code, out existing) || IsBetterMoneyCreatorHolding(holding, existing))
                    result[code] = holding;
            }

            return result;
        }

        private static bool IsBetterMoneyCreatorHolding(HoldingInfo candidate, HoldingInfo existing)
        {
            if (existing == null)
                return true;

            if (candidate == null)
                return false;

            // 보유수량은 키움 TR 수신 전 수동 반영값이 섞이면 비정상적으로 커질 수 있다.
            // 그래서 수량이 크다는 이유만으로 더 신뢰하지 않는다.
            if (candidate.EntryPrice > 0 && existing.EntryPrice <= 0)
                return true;

            if (candidate.EntryPrice > 0 && existing.EntryPrice > 0 && candidate.Quantity > 0 && existing.Quantity <= 0)
                return true;

            return false;
        }

        private Dictionary<string, double> BuildMoneyCreatorPendingBuyMap(List<MainWindowViewModel> viewModels)
        {
            var uniqueOrders = new Dictionary<string, OrderInfo>(StringComparer.OrdinalIgnoreCase);
            if (viewModels == null)
                return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            foreach (var vm in viewModels.Where(x => x != null && x.OrderHistory != null))
            {
                foreach (var source in vm.OrderHistory.Where(x =>
                    x != null &&
                    !string.IsNullOrWhiteSpace(x.Code)))
                {
                    if (!IsPendingBuyOrder(source))
                        continue;

                    var order = CloneOrderInfo(source);
                    if (order == null)
                        continue;

                    int resolvedPrice = vm.ResolveOrderPriceForCash(order);
                    if (order.Price <= 0 && resolvedPrice > 0)
                        order.Price = resolvedPrice;

                    string key = GetMoneyCreatorOrderUniqueKey(order);
                    if (string.IsNullOrWhiteSpace(key))
                        continue;

                    OrderInfo existing;
                    if (!uniqueOrders.TryGetValue(key, out existing) ||
                        order.UntradedQty > existing.UntradedQty ||
                        (existing.Price <= 0 && order.Price > 0))
                    {
                        uniqueOrders[key] = order;
                    }
                }
            }

            var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            foreach (var order in uniqueOrders.Values)
            {
                if (!IsPendingBuyOrder(order))
                    continue;

                int price = ResolveOrderPriceForCash(order);
                if (price <= 0)
                {
                    AddAutoTradeAnalysisLog(
                        $"[미체결 예약금 제외] 가격 확인 불가 / " +
                        $"{order.Code} / 주문번호={order.OrderNo} / " +
                        $"미체결={order.UntradedQty:N0}주 / 표시가격={order.Price:N0}");

                    continue;
                }

                string code = NormalizeMoneyCreatorCode(order.Code);
                if (string.IsNullOrWhiteSpace(code))
                    continue;

                double amount = CalculateBuyReserveCashAmount(order.UntradedQty, price);
                if (amount <= 0)
                    continue;

                double old;
                result.TryGetValue(code, out old);
                result[code] = old + amount;
            }

            return result;
        }

        private static string GetMoneyCreatorOrderUniqueKey(OrderInfo order)
        {
            if (order == null)
                return null;

            if (!string.IsNullOrWhiteSpace(order.OrderNo))
                return order.OrderNo.Trim();

            return NormalizeMoneyCreatorCode(order.Code) + "/" +
                   (order.OrderType ?? string.Empty) + "/" +
                   order.OrderTime + "/" +
                   order.Price + "/" +
                   order.UntradedQty;
        }

        private static double GetMoneyCreatorHoldingPositionAmount(HoldingInfo holding)
        {
            if (holding == null || holding.Quantity <= 0)
                return 0;

            double price = holding.EntryPrice;

            if (price <= 0)
                return 0;

            return Math.Max(0, holding.Quantity * price);
        }
        private bool TryApplyMoneyCreatorSignalAwareReallocationOnBuyLocked(
            string buyCode,
            long requiredAmount,
            DateTime now,
            string reason,
            out string reallocationReason)
        {
            reallocationReason = null;

            if (string.IsNullOrWhiteSpace(buyCode) || requiredAmount <= 0)
            {
                reallocationReason = "기회재배분 입력 오류";
                return false;
            }

            if (_moneyCreatorPortfolioAllocationByCode.Count == 0)
            {
                reallocationReason = "기회재배분 할당정보 없음";
                return false;
            }

            var allocations = _moneyCreatorPortfolioAllocationByCode.Values
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Code))
                .GroupBy(x => NormalizeMoneyCreatorCode(x.Code), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Last())
                .ToList();

            if (allocations.Count == 0)
            {
                reallocationReason = "기회재배분 대상 없음";
                return false;
            }

            double totalOperatingCapital = allocations.Max(x => Math.Max(0, x.TotalOperatingCapital));
            if (totalOperatingCapital <= 0)
                totalOperatingCapital = allocations.Sum(x => Math.Max(0, x.Budget));

            if (totalOperatingCapital <= 0)
            {
                reallocationReason = "기회재배분 총자산 없음";
                return false;
            }

            string strongSignalReason;
            if (!IsMoneyCreatorStrongBuyOpportunitySignal(buyCode, now, out strongSignalReason))
            {
                reallocationReason = "강한 Buy신호 아님 / " + strongSignalReason;
                return false;
            }

            var signalCodes = new List<string> { NormalizeMoneyCreatorCode(buyCode) };

            double totalUsed = allocations.Sum(x => Math.Max(0, x.PositionAmount + x.ReservedAmount));
            double opportunityCapital = Math.Max(0, totalOperatingCapital - totalUsed);

            if (opportunityCapital <= 0)
            {
                reallocationReason = "기회재배분 가능예산 없음";
                return false;
            }

            double totalWeight = 0;
            foreach (var allocation in allocations)
            {
                string code = NormalizeMoneyCreatorCode(allocation.Code);
                if (!signalCodes.Contains(code, StringComparer.OrdinalIgnoreCase))
                    continue;

                totalWeight += CalculateMoneyCreatorOpportunityWeight(allocation, code);
            }

            if (totalWeight <= 0)
                totalWeight = signalCodes.Count;

            foreach (var allocation in allocations)
            {
                string code = NormalizeMoneyCreatorCode(allocation.Code);
                double used = Math.Max(0, allocation.PositionAmount + allocation.ReservedAmount);
                bool isSignalCode = signalCodes.Contains(code, StringComparer.OrdinalIgnoreCase);

                double budget = used;
                if (isSignalCode)
                    budget += opportunityCapital * (CalculateMoneyCreatorOpportunityWeight(allocation, code) / totalWeight);

                allocation.Budget = Math.Max(0, budget);
                allocation.RemainingBudget = Math.Max(0, allocation.Budget - used);
                allocation.IsOverAllocated = used > allocation.Budget;
                allocation.UpdatedAt = now;
                allocation.Reason =
                    (string.IsNullOrWhiteSpace(reason) ? "자동주문전 기회재배분" : reason) +
                    ": 신호종목=" + signalCodes.Count +
                    ", 기회예산=" + opportunityCapital.ToString("N0") +
                    ", 종목예산=" + allocation.Budget.ToString("N0") +
                    ", 보유=" + allocation.PositionAmount.ToString("N0") +
                    ", 예약=" + allocation.ReservedAmount.ToString("N0") +
                    ", 잔여=" + allocation.RemainingBudget.ToString("N0") +
                    (isSignalCode ? ", Buy신호배분" : ", 신호없음예산회수");

                _moneyCreatorPortfolioAllocationByCode[code] = allocation;
            }

            _moneyCreatorPortfolioAllocationVersion++;
            _lastMoneyCreatorPortfolioUsageRefreshAt = now;

            MoneyCreatorPortfolioAllocationState current;
            if (_moneyCreatorPortfolioAllocationByCode.TryGetValue(NormalizeMoneyCreatorCode(buyCode), out current) && current != null)
            {
                reallocationReason =
                    "기회재배분 완료 / 주문=" + requiredAmount.ToString("N0") +
                    ", 잔여=" + current.RemainingBudget.ToString("N0") +
                    ", 예산=" + current.Budget.ToString("N0");

                return current.RemainingBudget >= requiredAmount;
            }

            reallocationReason = "기회재배분 후 현재종목 없음";
            return false;
        }

        private bool IsMoneyCreatorStrongBuyOpportunitySignal(string code, DateTime now, out string reason)
        {
            reason = null;

            string key = NormalizeMoneyCreatorCode(code);
            if (string.IsNullOrWhiteSpace(key))
            {
                reason = "종목코드 없음";
                return false;
            }

            FeatureSnapshot feature;
            if (!_moneyCreatorLatestFeatureByCode.TryGetValue(key, out feature) || feature == null)
            {
                reason = "최근 Feature 없음";
                return false;
            }

            if (!string.Equals(feature.SignalAction, TradeActionType.Buy.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                reason = "최근 신호 Buy 아님";
                return false;
            }

            DateTime signalTime = feature.Time == default(DateTime) ? now : feature.Time;
            if (now - signalTime > TimeSpan.FromSeconds(MoneyCreatorBuySignalOpportunitySeconds))
            {
                reason = "Buy신호 유효시간 초과";
                return false;
            }

            int baseScore = _autoTradeSettings == null || _autoTradeSettings.MinSignalScore <= 0
                ? 70
                : _autoTradeSettings.MinSignalScore;
            int requiredScore = Math.Max(MoneyCreatorStrongOpportunityMinScore, baseScore + MoneyCreatorStrongOpportunityScoreMargin);

            if (feature.SignalScore < requiredScore)
            {
                reason = "점수 부족 " + feature.SignalScore + "/" + requiredScore;
                return false;
            }

            if (feature.BarState != BarState.TrendContinuation &&
                feature.BarState != BarState.EarlyTurn)
            {
                reason = "BarState 매수상태 아님: " + feature.BarState;
                return false;
            }

            if (feature.HasPosition || feature.HoldingQty > 0)
            {
                reason = "보유중 기회몰빵 금지";
                return false;
            }

            double buyValue = Math.Max(0, feature.BuyDollarBarTradeValue);
            double sellValue = Math.Max(0, feature.SellDollarBarTradeValue);
            if (buyValue + sellValue > 0 && sellValue > buyValue)
            {
                reason = "SellDollarBar 우세";
                return false;
            }

            reason =
                "강한 Buy신호 / score=" + feature.SignalScore +
                ", state=" + feature.BarState +
                ", buyValue=" + buyValue.ToString("N0") +
                ", sellValue=" + sellValue.ToString("N0");
            return true;
        }

        private static double CalculateMoneyCreatorOpportunityWeight(MoneyCreatorPortfolioAllocationState allocation, string code)
        {
            double weight = allocation == null ? 1.0 : Math.Max(0.0001, allocation.Weight);

            FeatureSnapshot feature;
            lock (_moneyCreatorPortfolioSyncRoot)
                _moneyCreatorLatestFeatureByCode.TryGetValue(NormalizeMoneyCreatorCode(code), out feature);

            if (feature == null)
                return weight;

            if (feature.SignalScore > 0)
                weight *= 1.0 + Math.Min(0.75, Math.Max(0, feature.SignalScore - 70) / 60.0);

            if (feature.BarState == BarState.TrendContinuation)
                weight *= 1.15;
            else if (feature.BarState == BarState.EarlyTurn)
                weight *= 1.08;
            else if (feature.BarState == BarState.AccumulationWatch)
                weight *= 0.95;
            else if (feature.BarState == BarState.ChaseRisk ||
                     feature.BarState == BarState.FrictionNoTrade ||
                     feature.BarState == BarState.BreakdownExit ||
                     feature.BarState == BarState.GhostDrop ||
                     feature.BarState == BarState.FlashCrash)
                weight *= 0.70;

            return Math.Max(0.0001, weight);
        }

        private static void ApplyMoneyCreatorWeightedBudgets(List<MoneyCreatorPortfolioRuntimeState> states, double totalOperatingCapital)
        {
            if (states == null || states.Count == 0)
                return;

            if (totalOperatingCapital <= 0)
            {
                foreach (var state in states)
                {
                    state.Budget = 0;
                    state.RemainingBudget = 0;
                    state.IsOverAllocated = Math.Max(0, state.PositionAmount + state.ReservedAmount) > 0;
                }

                return;
            }

            double totalUsed = states.Sum(x => Math.Max(0, x.PositionAmount + x.ReservedAmount));
            double totalRemainingCapital = Math.Max(0, totalOperatingCapital - totalUsed);

            double totalWeight = states.Sum(x => Math.Max(0.0001, x.Weight));
            if (totalWeight <= 0)
                totalWeight = states.Count;

            foreach (var state in states)
            {
                double used = Math.Max(0, state.PositionAmount + state.ReservedAmount);
                double weight = Math.Max(0.0001, state.Weight);
                double remainingShare = totalRemainingCapital * (weight / totalWeight);

                state.Budget = Math.Max(0, used + remainingShare);
                state.IsOverAllocated = totalOperatingCapital < totalUsed && used > state.Budget;
                state.RemainingBudget = Math.Max(0, state.Budget - used);
            }
        }

        private void PushMoneyCreatorPortfolioAllocationsToCore(List<MainWindowViewModel> viewModels)
        {
            Dictionary<string, MoneyCreatorPortfolioAllocationState> snapshot;
            lock (_moneyCreatorPortfolioSyncRoot)
                snapshot = new Dictionary<string, MoneyCreatorPortfolioAllocationState>(_moneyCreatorPortfolioAllocationByCode, StringComparer.OrdinalIgnoreCase);

            if (snapshot.Count == 0 || viewModels == null)
                return;

            foreach (var vm in viewModels)
            {
                if (vm == null || vm._autoTradeController == null)
                    continue;

                foreach (var allocation in snapshot.Values)
                {
                    vm._autoTradeController.ApplyPortfolioAllocation(
                        allocation.Code,
                        allocation.TotalOperatingCapital,
                        allocation.Weight,
                        allocation.Budget,
                        allocation.RemainingBudget,
                        allocation.ReservedAmount,
                        allocation.PositionAmount,
                        allocation.IsOverAllocated,
                        allocation.Reason);
                }
            }

            foreach (var allocation in snapshot.Values)
            {
                FeatureSnapshot feature;
                lock (_moneyCreatorPortfolioSyncRoot)
                {
                    if (!_moneyCreatorLatestFeatureByCode.TryGetValue(allocation.Code, out feature))
                        feature = null;
                }

                ApplyMoneyCreatorAllocationToFeature(feature);
            }
        }

        private static void ApplyMoneyCreatorAllocationToFeature(FeatureSnapshot feature)
        {
            if (feature == null || string.IsNullOrWhiteSpace(feature.Code))
                return;

            MoneyCreatorPortfolioAllocationState allocation;
            lock (_moneyCreatorPortfolioSyncRoot)
            {
                if (!_moneyCreatorPortfolioAllocationByCode.TryGetValue(NormalizeMoneyCreatorCode(feature.Code), out allocation) || allocation == null)
                    return;
            }

            feature.TotalOperatingCapital = allocation.TotalOperatingCapital;
            feature.PortfolioExposureRatio = 1.0;
            feature.PortfolioTradableCapital = allocation.TotalOperatingCapital;
            feature.SymbolAllocationWeight = allocation.Weight;
            feature.SymbolAllocationBudget = allocation.Budget;
            feature.SymbolRemainingBudget = allocation.RemainingBudget;
            feature.SymbolAllocationUpdatedAt = allocation.UpdatedAt;
            feature.SymbolAllocationReason = allocation.Reason;
            feature.DynamicSlotBudget = allocation.RemainingBudget;
            feature.DynamicAvailableCash = Math.Max(0, allocation.RemainingBudget);
        }

        private bool TryGetMoneyCreatorAllocation(
            string code,
            out double budget,
            out double remaining,
            out double reserved,
            out double positionAmount,
            out bool isOverAllocated)
        {
            budget = 0;
            remaining = 0;
            reserved = 0;
            positionAmount = 0;
            isOverAllocated = false;

            if (string.IsNullOrWhiteSpace(code))
                return false;

            MoneyCreatorPortfolioAllocationState allocation;
            lock (_moneyCreatorPortfolioSyncRoot)
            {
                if (!_moneyCreatorPortfolioAllocationByCode.TryGetValue(NormalizeMoneyCreatorCode(code), out allocation) || allocation == null)
                    return false;
            }

            budget = Math.Max(0, allocation.Budget);
            remaining = Math.Max(0, allocation.RemainingBudget);
            reserved = Math.Max(0, allocation.ReservedAmount);
            positionAmount = Math.Max(0, allocation.PositionAmount);
            isOverAllocated = allocation.IsOverAllocated;

            return budget > 0 || remaining > 0 || reserved > 0 || positionAmount > 0;
        }

        private void AddMoneyCreatorAcceptedBuyReservation(string code, double amount, string reason)
        {
            string key = NormalizeMoneyCreatorCode(code);
            if (string.IsNullOrWhiteSpace(key) || amount <= 0)
                return;

            lock (_moneyCreatorPortfolioSyncRoot)
            {
                double old;
                _moneyCreatorAcceptedBuyReservedByCode.TryGetValue(key, out old);
                _moneyCreatorAcceptedBuyReservedByCode[key] = Math.Max(0, old + amount);
            }

            SynchronizeMoneyCreatorCashFromCurrentKiwoomSnapshot(reason);
        }

        private void ReleaseMoneyCreatorAcceptedBuyReservation(string code, double amount, string reason)
        {
            string key = NormalizeMoneyCreatorCode(code);
            if (string.IsNullOrWhiteSpace(key) || amount <= 0)
                return;

            lock (_moneyCreatorPortfolioSyncRoot)
            {
                double old;
                if (_moneyCreatorAcceptedBuyReservedByCode.TryGetValue(key, out old))
                {
                    double next = Math.Max(0, old - amount);
                    if (next <= 0)
                        _moneyCreatorAcceptedBuyReservedByCode.Remove(key);
                    else
                        _moneyCreatorAcceptedBuyReservedByCode[key] = next;
                }
            }

            SynchronizeMoneyCreatorCashFromCurrentKiwoomSnapshot(reason);
        }

        private void ClearMoneyCreatorAcceptedBuyReservation(string code, string reason)
        {
            string key = NormalizeMoneyCreatorCode(code);
            if (string.IsNullOrWhiteSpace(key))
                return;

            bool removed = false;

            lock (_moneyCreatorPortfolioSyncRoot)
            {
                removed = _moneyCreatorAcceptedBuyReservedByCode.Remove(key);
            }

            if (!removed)
                return;

            AddAutoTradeAnalysisLog(
                $"[매수예약 정리] {key} / {reason}");

            SynchronizeMoneyCreatorCashFromCurrentKiwoomSnapshot(reason);
        }

        private bool IsMoneyCreatorSellRefreshPendingForCode(string code)
        {
            string key = NormalizeMoneyCreatorCode(code);
            if (string.IsNullOrWhiteSpace(key))
                return false;

            lock (_moneyCreatorAccountRefreshSyncRoot)
                return _moneyCreatorPendingSellRefreshCodes.Contains(key);
        }

        private void ReconcileMoneyCreatorAcceptedBuyReservationsWithUnfilledOrders(
            List<OrderInfo> unfilledOrders,
            string reason)
        {
            var pendingByCode = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            if (unfilledOrders != null)
            {
                foreach (var order in unfilledOrders)
                {
                    if (!IsPendingBuyOrder(order))
                        continue;

                    string key = NormalizeMoneyCreatorCode(order.Code);
                    if (string.IsNullOrWhiteSpace(key))
                        continue;

                    int price = ResolveOrderPriceForCash(order);
                    long amount = CalculateBuyReserveCashAmount(GetRemainingOrderQty(order), price);
                    if (amount <= 0)
                        continue;

                    double old;
                    pendingByCode.TryGetValue(key, out old);
                    pendingByCode[key] = old + amount;
                }
            }

            bool changed = false;
            DateTime now = ResolveAutoTradePositionTimestamp();

            lock (_moneyCreatorPortfolioSyncRoot)
            {
                foreach (string key in _moneyCreatorAcceptedBuyReservedByCode.Keys.ToList())
                {
                    double temp = Math.Max(0, _moneyCreatorAcceptedBuyReservedByCode[key]);
                    double pending;
                    pendingByCode.TryGetValue(key, out pending);

                    if (pending > 0)
                    {
                        double next = Math.Min(temp, pending);
                        if (Math.Abs(next - temp) >= 1)
                        {
                            _moneyCreatorAcceptedBuyReservedByCode[key] = next;
                            changed = true;
                        }

                        continue;
                    }

                    DateTime reservedAt;
                    bool recentReservation =
                        !IsReplayMode &&
                        _moneyCreatorLastBuyOrderReservedAtByCode.TryGetValue(key, out reservedAt) &&
                        (now - reservedAt).TotalSeconds < MoneyCreatorBuyOrderDelaySeconds;

                    if (recentReservation)
                        continue;

                    _moneyCreatorAcceptedBuyReservedByCode.Remove(key);
                    changed = true;
                }
            }

            if (!changed)
                return;

            AddAutoTradeAnalysisLog(
                "[매수예약 미체결동기화] " +
                (string.IsNullOrWhiteSpace(reason) ? "미체결 수신" : reason));

            SynchronizeMoneyCreatorCashFromCurrentKiwoomSnapshot(reason);
            RefreshCurrentSymbolAllocationTextFromCore();
        }

        private static long GetMoneyCreatorAcceptedBuyReservationTotal()
        {
            lock (_moneyCreatorPortfolioSyncRoot)
                return (long)_moneyCreatorAcceptedBuyReservedByCode.Values.Sum(x => Math.Max(0, x));
        }

        private long GetMoneyCreatorSharedAvailableCash(List<MainWindowViewModel> viewModels)
        {
            lock (_moneyCreatorPortfolioSyncRoot)
            {
                if (_moneyCreatorCashSnapshotReady)
                    return Math.Max(0, _moneyCreatorEffectiveCash);
            }

            long raw = viewModels == null
                ? 0
                : viewModels
                    .Where(vm => vm != null)
                    .Select(vm => Math.Max(Math.Max(0, vm.KiwoomSyncedBalance), Math.Max(0, vm.CurrentBalance)))
                    .DefaultIfEmpty(0)
                    .Max();

            long reserved = CalculateMoneyCreatorEffectiveBuyReservedAmount(viewModels);
            return Math.Max(0, raw - reserved);
        }

        private long CalculateMoneyCreatorEffectiveBuyReservedAmount(List<MainWindowViewModel> viewModels)
        {
            var pendingReservedByCode = BuildMoneyCreatorPendingBuyMap(viewModels);

            Dictionary<string, double> tempReservedByCode;
            lock (_moneyCreatorPortfolioSyncRoot)
                tempReservedByCode = new Dictionary<string, double>(_moneyCreatorAcceptedBuyReservedByCode, StringComparer.OrdinalIgnoreCase);

            var allCodes = pendingReservedByCode.Keys
                .Concat(tempReservedByCode.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            double total = 0;
            foreach (string code in allCodes)
            {
                double pending;
                double temp;
                pendingReservedByCode.TryGetValue(code, out pending);
                tempReservedByCode.TryGetValue(code, out temp);
                total += Math.Max(Math.Max(0, pending), Math.Max(0, temp));
            }

            return (long)Math.Round(Math.Max(0, total));
        }

        private void SynchronizeMoneyCreatorCashFromKiwoom(long kiwoomCashRaw, string reason)
        {
            var app = Application.Current;
            if (app != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.Invoke(() => SynchronizeMoneyCreatorCashFromKiwoom(kiwoomCashRaw, reason));
                return;
            }

            var viewModels = GetMoneyCreatorAutoTradeViewModelsOnUiThread();
            long safeRaw = Math.Max(0, kiwoomCashRaw);
            long remainingAdjustment;
            long confirmedAdjustment;

            lock (_moneyCreatorPortfolioSyncRoot)
            {
                // A cash TR queued before the sell settlement can return the old cash value.
                // Consume only the positive raw-cash increase that has actually appeared;
                // do not erase the full immediate sell credit on the first stale response.
                long positiveRawIncrease = Math.Max(0, safeRaw - _moneyCreatorKiwoomCashRaw);
                confirmedAdjustment = Math.Min(_moneyCreatorInternalCashAdjustment, positiveRawIncrease);
                _moneyCreatorInternalCashAdjustment = Math.Max(0, _moneyCreatorInternalCashAdjustment - confirmedAdjustment);
                remainingAdjustment = _moneyCreatorInternalCashAdjustment;

                _moneyCreatorKiwoomCashRaw = safeRaw;
                _moneyCreatorCashSnapshotReady = true;
                _moneyCreatorCashSyncWaitMode = false;
                _moneyCreatorCashSyncWaitReason = null;
            }

            long reserved = CalculateMoneyCreatorEffectiveBuyReservedAmount(viewModels);
            long effectiveCash = Math.Max(0, safeRaw + remainingAdjustment - reserved);

            ApplyMoneyCreatorSharedCashSnapshot(viewModels, safeRaw, effectiveCash, reserved, reason);

            if (confirmedAdjustment > 0 || remainingAdjustment > 0)
            {
                AddAutoTradeAnalysisLog(
                    $"[매도체결 현금검증] 키움증가확인={confirmedAdjustment:N0} / " +
                    $"미확인즉시반영={remainingAdjustment:N0} / 키움원본={safeRaw:N0}");
            }
        }

        private void SynchronizeMoneyCreatorCashFromCurrentKiwoomSnapshot(string reason)
        {
            var app = Application.Current;
            if (app != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.Invoke(() => SynchronizeMoneyCreatorCashFromCurrentKiwoomSnapshot(reason));
                return;
            }

            long raw;
            long adjustment;
            bool hasRaw;
            lock (_moneyCreatorPortfolioSyncRoot)
            {
                raw = _moneyCreatorKiwoomCashRaw;
                adjustment = _moneyCreatorInternalCashAdjustment;
                hasRaw = _moneyCreatorCashSnapshotReady;
            }

            if (!hasRaw)
                raw = Math.Max(Math.Max(0, KiwoomSyncedBalance), Math.Max(0, CurrentBalance));

            var viewModels = GetMoneyCreatorAutoTradeViewModelsOnUiThread();
            long reserved = CalculateMoneyCreatorEffectiveBuyReservedAmount(viewModels);
            long effectiveCash = Math.Max(0, raw + adjustment - reserved);

            ApplyMoneyCreatorSharedCashSnapshot(viewModels, raw, effectiveCash, reserved, reason);
        }

        private void ApplyMoneyCreatorSellFillCashImmediately(string code, double amount, string reason)
        {
            if (amount <= 0)
                return;

            var app = Application.Current;
            if (app != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.Invoke(() => ApplyMoneyCreatorSellFillCashImmediately(code, amount, reason));
                return;
            }

            ClearMoneyCreatorAcceptedBuyReservation(
                code,
                (string.IsNullOrWhiteSpace(reason) ? "매도체결" : reason) + " / 잔여매수예약 정리");

            long sellAmount = (long)Math.Round(amount);
            lock (_moneyCreatorPortfolioSyncRoot)
            {
                _moneyCreatorInternalCashAdjustment = Math.Max(0, _moneyCreatorInternalCashAdjustment + sellAmount);
            }

            // Pending sell refresh code is intentionally retained until the account refresh
            // confirms that the holding is gone. Removing it here skipped the forced reallocation.

            // 키움 예수금 원본은 TR 수신값으로만 갱신한다.
            // 매도 체결금은 내부 현금 조정값으로만 즉시 반영하고, 키움 TR 수신 시 검증/정리한다.
            SynchronizeMoneyCreatorCashFromCurrentKiwoomSnapshot(
                (string.IsNullOrWhiteSpace(reason) ? "매도체결" : reason) + " / 내부현금 즉시반영");

            RebuildMoneyCreatorPortfolioAllocations(
                (string.IsNullOrWhiteSpace(reason) ? "매도체결" : reason) + " / 내부기준 즉시전체재할당",
                true);

            AddAutoTradeAnalysisLog(
                $"[매도체결 내부현금반영] {code} / 예상순수령액={amount:N0} / " +
                "키움원본 직접증가 없음 / 키움TR 증가분만큼 단계적 검증");
        }

        private void ApplyMoneyCreatorSharedCashSnapshot(
            List<MainWindowViewModel> viewModels,
            long kiwoomCashRaw,
            long effectiveCash,
            long reservedAmount,
            string reason)
        {
            if (viewModels == null || viewModels.Count == 0)
                viewModels = GetMoneyCreatorAutoTradeViewModelsOnUiThread();

            lock (_moneyCreatorPortfolioSyncRoot)
            {
                _moneyCreatorKiwoomCashRaw = Math.Max(0, kiwoomCashRaw);
                _moneyCreatorEffectiveCash = Math.Max(0, effectiveCash);
                _moneyCreatorCashSnapshotReady = true;
            }

            foreach (var vm in viewModels.Where(x => x != null))
            {
                vm.KiwoomSyncedBalance = Math.Max(0, kiwoomCashRaw);
                vm.CurrentBalance = Math.Max(0, effectiveCash);
                vm._isMoneyCreatorCashInitialized = true;
                vm._autoTradeController?.UpdateCash(Math.Max(0, effectiveCash));
            }

            bool skipUsageRefresh = !string.IsNullOrWhiteSpace(reason) && reason.Contains("매도체결");
            if (!skipUsageRefresh)
                RefreshMoneyCreatorPortfolioAllocationUsage(viewModels, reason);

            foreach (var vm in viewModels.Where(x => x != null))
                vm.RefreshCurrentSymbolAllocationTextFromCore();

            if (!string.IsNullOrWhiteSpace(reason))
            {
                long adjustment;
                bool waitMode;
                string waitReason;
                lock (_moneyCreatorPortfolioSyncRoot)
                {
                    adjustment = _moneyCreatorInternalCashAdjustment;
                    waitMode = _moneyCreatorCashSyncWaitMode;
                    waitReason = _moneyCreatorCashSyncWaitReason;
                }

                AddAutoTradeAnalysisLog(
                    $"[공통예수금동기화] {reason} / " +
                    $"키움원본={kiwoomCashRaw:N0} / 내부조정={adjustment:N0} / " +
                    $"매수예약={reservedAmount:N0} / 사용가능={effectiveCash:N0}" +
                    (waitMode ? " / 동기화대기=" + waitReason : string.Empty));
            }
        }

        private static bool ShouldForceMoneyCreatorPortfolioAllocation(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return false;

            return reason.Contains("자동매매 ON") ||
                   reason.Contains("자동매매 OFF") ||
                   reason.Contains("자동매매 초기화");
        }

        private void MarkMoneyCreatorOrderCanceledLocally(string orderNo, string code, int cancelQty)
        {
            if (OrderHistory == null)
                return;

            string normalizedCode = NormalizeMoneyCreatorCode(code);
            var order = OrderHistory.FirstOrDefault(x =>
                x != null &&
                ((!string.IsNullOrWhiteSpace(orderNo) && !string.IsNullOrWhiteSpace(x.OrderNo) && x.OrderNo.Trim() == orderNo.Trim()) ||
                 (!string.IsNullOrWhiteSpace(normalizedCode) && NormalizeMoneyCreatorCode(x.Code) == normalizedCode)));

            if (order == null)
                return;

            if (cancelQty > 0 && order.UntradedQty > cancelQty)
                order.UntradedQty = Math.Max(0, order.UntradedQty - cancelQty);
            else
                order.UntradedQty = 0;

            order.Status = "취소요청";
            order.OrderStatus = OrderStatus.Cancelled;
        }

        private void ApplyFilledOrderToHoldingsOnUi(string code, string name, string orderType, int filledQty, double filledPrice)
        {
            if (filledQty <= 0 || filledPrice <= 0 || string.IsNullOrWhiteSpace(code))
                return;

            string normalizedCode = NormalizeMoneyCreatorCode(code);
            if (string.IsNullOrWhiteSpace(normalizedCode))
                return;

            if (AccountHoldings == null)
                AccountHoldings = new ObservableCollection<HoldingInfo>();

            var holding = AccountHoldings.FirstOrDefault(x =>
                x != null && NormalizeMoneyCreatorCode(x.Code) == normalizedCode);

            bool isBuy = !string.IsNullOrWhiteSpace(orderType) && orderType.Contains("매수");
            bool isSell = !string.IsNullOrWhiteSpace(orderType) && orderType.Contains("매도");

            if (isBuy)
            {
                if (holding == null)
                {
                    holding = new HoldingInfo
                    {
                        Code = normalizedCode,
                        Name = name,
                        Quantity = filledQty,
                        EntryPrice = filledPrice,
                        CurrentPrice = filledPrice
                    };
                    AccountHoldings.Add(holding);
                }
                else
                {
                    int oldQty = Math.Max(0, holding.Quantity);
                    double oldEntry = holding.EntryPrice > 0 ? holding.EntryPrice : filledPrice;
                    int newQty = oldQty + filledQty;
                    holding.EntryPrice = newQty <= 0
                        ? filledPrice
                        : ((oldQty * oldEntry) + (filledQty * filledPrice)) / newQty;
                    holding.Quantity = newQty;
                    holding.CurrentPrice = filledPrice;
                    if (string.IsNullOrWhiteSpace(holding.Name))
                        holding.Name = name;
                }
            }
            else if (isSell)
            {
                if (holding != null)
                {
                    holding.Quantity = Math.Max(0, holding.Quantity - filledQty);
                    holding.CurrentPrice = filledPrice;
                    if (holding.Quantity <= 0)
                        AccountHoldings.Remove(holding);
                }
            }

            if (holding != null)
                holding.RefreshProfitStatus();

            if (Context != null && NormalizeMoneyCreatorCode(Context.Code) == normalizedCode)
            {
                var currentHolding = AccountHoldings.FirstOrDefault(x =>
                    x != null && NormalizeMoneyCreatorCode(x.Code) == normalizedCode);

                Context.CurrentHold = currentHolding;
                HoldingQty = currentHolding == null ? 0 : currentHolding.Quantity;

                _autoTradeController?.UpdatePosition(
                    normalizedCode,
                    HoldingQty,
                    currentHolding == null ? 0 : currentHolding.EntryPrice,
                    ResolveAutoTradePositionTimestamp());
            }

            OnPropertyChanged(nameof(AccountHoldings));
            OnPropertyChanged(nameof(HoldingQty));
            OnPropertyChanged(nameof(TradeCardHoldingQty));
        }

        private static double CalculateMoneyCreatorSymbolWeight(FeatureSnapshot f, AutoTradeSettings settings)
        {
            if (f == null)
                return 1.0;

            double weight = 1.0;

            if (f.MarketPositionSizeMultiplier > 0)
                weight *= ClampMoneyCreatorValue(f.MarketPositionSizeMultiplier, 0.10, 1.50);

            switch (f.BarState)
            {
                case BarState.TrendContinuation:
                    weight += 0.25;
                    break;
                case BarState.EarlyTurn:
                    weight += 0.14;
                    break;
                case BarState.AccumulationWatch:
                    weight += 0.05;
                    break;
                case BarState.ChaseRisk:
                    weight -= 0.20;
                    break;
                case BarState.FrictionNoTrade:
                case BarState.BreakdownExit:
                case BarState.GhostDrop:
                case BarState.FlashCrash:
                case BarState.DataIntegrityRisk:
                    weight -= 0.45;
                    break;
            }

            if (f.BarCurrentScore >= 85)
                weight += 0.20;
            else if (f.BarCurrentScore >= 70)
                weight += 0.12;
            else if (f.BarCurrentScore <= 35)
                weight -= 0.20;

            if (f.BarBuyPressureScore >= 70)
                weight += 0.15;
            else if (f.BarBuyPressureScore >= 55)
                weight += 0.08;

            if (f.BarTrendFilterScore >= 70)
                weight += 0.12;
            else if (f.BarTrendFilterScore <= 30)
                weight -= 0.15;

            switch (f.MarketTrend)
            {
                case MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Panic:
                    weight *= 0.60;
                    break;
                case MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Bear:
                    weight *= 0.75;
                    break;
                case MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Weak:
                    weight *= 0.90;
                    break;
                case MoneyCreator.Models.MarketIndexInfo.MarketTrendState.Bull:
                    weight *= 1.08;
                    break;
                case MoneyCreator.Models.MarketIndexInfo.MarketTrendState.StrongBull:
                    weight *= 1.15;
                    break;
            }

            if (Math.Abs(f.DollarBarChangeRate) >= 3.0)
                weight *= 0.70;
            else if (Math.Abs(f.DollarBarChangeRate) >= 2.0)
                weight *= 0.85;

            if (f.DollarBarDecisionDPos >= 0.90 && f.BarState != BarState.TrendContinuation)
                weight *= 0.80;

            if (f.HasPosition && (f.NetProfitRate < 0 || f.ProfitRate < 0))
                weight *= 0.90;

            double max = settings == null || settings.MaxScorePositionMultiplier <= 0
                ? 1.5
                : ClampMoneyCreatorValue(settings.MaxScorePositionMultiplier, 1.0, 3.0);

            return ClampMoneyCreatorValue(weight, 0.10, max);
        }

        private static double ClampMoneyCreatorValue(double value, double min, double max)
        {
            if (value < min)
                return min;

            if (value > max)
                return max;

            return value;
        }

        private static string NormalizeMoneyCreatorCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return string.Empty;

            return code.Trim().Replace("A", string.Empty);
        }

        // ============================================================
        // Source: MainWindowViewModel.AutoTrading.State.cs
        // ============================================================
        private void SyncAutoTradeState(string reason = null)
        {
            try
            {
                if (_autoTradeController == null)
                    return;

                var activeCodes = GetActiveAutoTradeCodes();

                // 자산배분은 창별 Worker가 아니라 전체 자동매매 ON 종목 목록을 기준으로 해야 한다.
                // 활성 종목 목록을 먼저 Core 공통 allocator에 반영한 뒤 예수금/미체결/보유를 동기화한다.
                _autoTradeController.UpdateActiveAutoTradeCodes(activeCodes);
                _autoTradeController.UpdateCash(GetAutoTradeAvailableCash());
                _autoTradeController.UpdateOpenOrders(GetOpenOrdersForAutoTrade());

                if (AccountHoldings != null && AccountHoldings.Count > 0)
                    UpdateAutoTradePositionsFromHoldings(AccountHoldings.ToList());
                else
                    SyncAutoTradePosition(Context?.Code);

                RebuildMoneyCreatorPortfolioAllocations(
                    string.IsNullOrWhiteSpace(reason) ? "MoneyCreator동기화" : reason,
                    ShouldForceMoneyCreatorPortfolioAllocation(reason));

                RefreshCurrentSymbolAllocationTextFromCore();

                if (!string.IsNullOrWhiteSpace(reason))
                    AddAutoTradeAnalysisLog($"[MoneyCreator자산배분] {reason}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AutoTradeCore 상태 동기화 실패] " + ex.Message);
            }
        }

        private long GetAutoTradeAvailableCash()
        {
            lock (_moneyCreatorPortfolioSyncRoot)
            {
                if (_moneyCreatorCashSnapshotReady)
                    return Math.Max(0, _moneyCreatorEffectiveCash);
            }

            long current = Math.Max(0, CurrentBalance);
            if (current > 0)
                return current;

            return Math.Max(0, KiwoomSyncedBalance);
        }

        private long CalculateOrderCashAmount(int quantity, int price)
        {
            if (quantity <= 0 || price <= 0)
                return 0;

            return (long)quantity * price;
        }

        private long CalculateBuyReserveCashAmount(int quantity, int price)
        {
            long amount = CalculateOrderCashAmount(quantity, price);
            if (amount <= 0)
                return 0;

            double feeRate = GetMoneyCreatorEstimatedBuyFeeRate();
            return (long)Math.Ceiling(amount * (1.0 + feeRate));
        }

        private long CalculateSellReceivableCashAmount(int quantity, int price)
        {
            long amount = CalculateOrderCashAmount(quantity, price);
            if (amount <= 0)
                return 0;

            double costRate = GetMoneyCreatorEstimatedSellFeeRate() + GetMoneyCreatorEstimatedSellTaxRate();
            return (long)Math.Max(0, Math.Floor(amount * (1.0 - costRate)));
        }

        private string BuildManualOrderCostText(string type, int quantity, int price)
        {
            long grossAmount = CalculateOrderCashAmount(quantity, price);

            if (string.Equals(type, "BUY", StringComparison.OrdinalIgnoreCase))
            {
                long requiredAmount = CalculateBuyReserveCashAmount(quantity, price);
                long estimatedFee = Math.Max(0, requiredAmount - grossAmount);

                return
                    $"총액 : {grossAmount:#,0}원\n" +
                    $"예상 매수수수료 : {estimatedFee:#,0}원\n" +
                    $"예상 필요금액 : {requiredAmount:#,0}원";
            }

            long receivableAmount = CalculateSellReceivableCashAmount(quantity, price);
            long estimatedFeeTax = Math.Max(0, grossAmount - receivableAmount);

            return
                $"총액 : {grossAmount:#,0}원\n" +
                $"예상 매도비용 : {estimatedFeeTax:#,0}원\n" +
                $"예상 정산금액 : {receivableAmount:#,0}원";
        }

        private double GetMoneyCreatorEstimatedBuyFeeRate()
        {
            lock (_moneyCreatorPortfolioSyncRoot)
                return NormalizeMoneyCreatorDomesticFeeRate(_moneyCreatorEstimatedBuyFeeRate);
        }

        private double GetMoneyCreatorEstimatedSellFeeRate()
        {
            lock (_moneyCreatorPortfolioSyncRoot)
                return NormalizeMoneyCreatorDomesticFeeRate(_moneyCreatorEstimatedSellFeeRate);
        }

        private double GetMoneyCreatorEstimatedSellTaxRate()
        {
            lock (_moneyCreatorPortfolioSyncRoot)
                return NormalizeMoneyCreatorDomesticSellTaxRate(_moneyCreatorEstimatedSellTaxRate);
        }

        private double GetMoneyCreatorEstimatedRoundTripCostPercent()
        {
            return (GetMoneyCreatorEstimatedBuyFeeRate() +
                    GetMoneyCreatorEstimatedSellFeeRate() +
                    GetMoneyCreatorEstimatedSellTaxRate()) * 100.0;
        }

        private static double NormalizeMoneyCreatorDomesticFeeRate(double value)
        {
            value = ClampMoneyCreatorCostRate(value, 0, 0.05);

            // Legacy default 0.00355 meant 0.355%, not Kiwoom's ordinary online 0.015%.
            // For current domestic stock replay/real baseline, fall back to Kiwoom online fee.
            if (value <= 0.0 || value > 0.001)
                return MoneyCreatorKiwoomDomesticOnlineFeeRate;

            return value;
        }

        private static double NormalizeMoneyCreatorDomesticSellTaxRate(double value)
        {
            value = ClampMoneyCreatorCostRate(value, 0, 0.05);

            if (value <= 0.0)
                return MoneyCreatorKiwoomDomesticSellTaxRate;

            return value;
        }

        private static double ClampMoneyCreatorCostRate(double value, double min, double max)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return min;

            if (value < min)
                return min;

            if (value > max)
                return max;

            return value;
        }

        private void ObserveMoneyCreatorTradeCostFromOrder(OrderInfo order, string reason)
        {
            if (order == null || order.ExecQty <= 0 || order.ExecPrice <= 0)
                return;

            double amount = Math.Abs(order.ExecQty) * order.ExecPrice;
            if (amount <= 0)
                return;

            double observedFeeRate = order.Fee > 0 ? ClampMoneyCreatorCostRate(order.Fee / amount, 0, 0.05) : 0;
            double observedTaxRate = order.Tax > 0 ? ClampMoneyCreatorCostRate(order.Tax / amount, 0, 0.05) : 0;

            if (observedFeeRate <= 0 && observedTaxRate <= 0)
                return;

            Debug.WriteLine(
                "[거래비용 관찰] " +
                (string.IsNullOrWhiteSpace(reason) ? "체결" : reason) +
                " / " + order.Code +
                " / 실제수수료=" + observedFeeRate.ToString("P3") +
                " / 실제세금=" + observedTaxRate.ToString("P3") +
                " / 적용매수수수료=" + GetMoneyCreatorEstimatedBuyFeeRate().ToString("P3") +
                " / 적용매도수수료=" + GetMoneyCreatorEstimatedSellFeeRate().ToString("P3") +
                " / 적용매도세금=" + GetMoneyCreatorEstimatedSellTaxRate().ToString("P3"));
        }

        private static double BlendMoneyCreatorCostRate(double current, double observed)
        {
            current = ClampMoneyCreatorCostRate(current, 0, 0.05);
            observed = ClampMoneyCreatorCostRate(observed, 0, 0.05);

            if (observed <= 0)
                return current;

            if (current <= 0)
                return observed;

            double blended = (current * 0.70) + (observed * 0.30);
            return ClampMoneyCreatorCostRate(Math.Max(blended, observed * 0.90), 0, 0.05);
        }

        private void ApplyMoneyCreatorEstimatedTradingCostToSettingsLocked()
        {
            double buyFeeRate = NormalizeMoneyCreatorDomesticFeeRate(_moneyCreatorEstimatedBuyFeeRate);
            double sellFeeRate = NormalizeMoneyCreatorDomesticFeeRate(_moneyCreatorEstimatedSellFeeRate);
            double sellTaxRate = NormalizeMoneyCreatorDomesticSellTaxRate(_moneyCreatorEstimatedSellTaxRate);

            TradingCostPolicy.SetRates(buyFeeRate, sellFeeRate, sellTaxRate);

            if (_autoTradeSettings == null)
                return;

            if (_autoTradeSettings.UseEstimatedTradingCostForExit)
            {
                _autoTradeSettings.EstimatedBuyFeeRate = buyFeeRate;
                _autoTradeSettings.EstimatedSellFeeRate = sellFeeRate;
                _autoTradeSettings.EstimatedSellTaxRate = sellTaxRate;
            }
            else
            {
                _autoTradeSettings.EstimatedBuyFeeRate = 0.0;
                _autoTradeSettings.EstimatedSellFeeRate = 0.0;
                _autoTradeSettings.EstimatedSellTaxRate = 0.0;
            }
        }

        private void RefreshMoneyCreatorHoldingProfitRates()
        {
            if (AccountHoldings == null)
                return;

            foreach (var holding in AccountHoldings)
            {
                if (holding == null)
                    continue;

                holding.RefreshProfitStatus();
            }

            OnPropertyChanged(nameof(AccountHoldings));
        }

        private bool IsBuyOrderTypeCode(int orderType)
        {
            // 1=신규매수, 5=매수정정. 매수취소(3)는 복원 처리 쪽에서 별도 처리한다.
            return orderType == 1 || orderType == 5;
        }

        private bool IsBuyCancelOrderTypeCode(int orderType)
        {
            return orderType == 3;
        }

        private bool IsBuyOrderInfo(OrderInfo order)
        {
            return order != null &&
                   !string.IsNullOrWhiteSpace(order.OrderType) &&
                   order.OrderType.Contains("매수");
        }

        private int ResolveOrderPriceForCash(OrderInfo order)
        {
            if (order == null)
                return 0;

            if (order.Price > 0)
                return (int)Math.Round(order.Price);

            if (order.ExecPrice > 0)
                return (int)Math.Round(order.ExecPrice);

            string orderNo = string.IsNullOrWhiteSpace(order.OrderNo)
                ? string.Empty
                : order.OrderNo.Trim();

            if (!string.IsNullOrWhiteSpace(orderNo) && OrderHistory != null)
            {
                var existing = OrderHistory.FirstOrDefault(x =>
                    x != null &&
                    !string.IsNullOrWhiteSpace(x.OrderNo) &&
                    x.OrderNo.Trim() == orderNo);

                if (existing != null)
                {
                    if (existing.Price > 0)
                        return (int)Math.Round(existing.Price);

                    if (existing.ExecPrice > 0)
                        return (int)Math.Round(existing.ExecPrice);
                }
            }

            string code = NormalizeMoneyCreatorCode(order.Code);

            if (!string.IsNullOrWhiteSpace(code) && AccountHoldings != null)
            {
                var holding = AccountHoldings.FirstOrDefault(x =>
                    x != null &&
                    NormalizeMoneyCreatorCode(x.Code) == code);

                if (holding != null)
                {
                    if (holding.CurrentPrice > 0)
                        return (int)Math.Round(holding.CurrentPrice);

                    if (holding.EntryPrice > 0)
                        return (int)Math.Round(holding.EntryPrice);
                }
            }

            if (!string.IsNullOrWhiteSpace(code) &&
                Context != null &&
                NormalizeMoneyCreatorCode(Context.Code) == code &&
                Context.LastPrice > 0)
            {
                return (int)Math.Round(Context.LastPrice);
            }

            if (!string.IsNullOrWhiteSpace(code) && Symbols != null)
            {
                var symbol = Symbols.FirstOrDefault(x =>
                    x != null &&
                    NormalizeMoneyCreatorCode(x.Code) == code);

                if (symbol != null && !string.IsNullOrWhiteSpace(symbol.LastPrice))
                {
                    string raw = Regex.Replace(symbol.LastPrice, @"[^\d.]", string.Empty);
                    double parsed;
                    if (double.TryParse(raw, out parsed) && parsed > 0)
                        return (int)Math.Round(parsed);
                }
            }

            lock (_moneyCreatorPortfolioSyncRoot)
            {
                FeatureSnapshot feature;
                if (!string.IsNullOrWhiteSpace(code) &&
                    _moneyCreatorLatestFeatureByCode.TryGetValue(code, out feature) &&
                    feature != null &&
                    feature.Price > 0)
                {
                    return (int)Math.Round(feature.Price);
                }
            }

            return 0;
        }

        private void DeductMoneyCreatorCashForAcceptedOrder(
            string reason,
            int orderType,
            string code,
            int quantity,
            int price)
        {
            if (!IsBuyOrderTypeCode(orderType))
                return;

            long amount = CalculateBuyReserveCashAmount(quantity, price);
            if (amount <= 0)
                return;

            AddMoneyCreatorAcceptedBuyReservation(code, amount, reason + " 주문접수");

            AddAutoTradeAnalysisLog(
                $"[예수금 예약/주문접수] {reason} / {code} / " +
                $"주문={amount:N0} / 키움동기화={KiwoomSyncedBalance:N0} / 사용가능={CurrentBalance:N0}");
        }

        private void RestoreMoneyCreatorCashForCanceledBuyOrder(
            string reason,
            OrderInfo order,
            int cancelQty)
        {
            if (!IsBuyOrderInfo(order))
                return;

            int price = ResolveOrderPriceForCash(order);
            long amount = CalculateBuyReserveCashAmount(cancelQty, price);
            if (amount <= 0)
                return;

            AddAutoTradeAnalysisLog(
                $"[예수금 복원요청/취소] {reason} / {order.Code} / " +
                $"취소={amount:N0} / 키움동기화={KiwoomSyncedBalance:N0} / 사용가능={CurrentBalance:N0}");
        }

        private void RestoreMoneyCreatorCashForAutoTradeBuyCancel(
            string reason,
            OrderRequest request,
            int untradedQty)
        {
            if (request == null || request.Signal == null)
                return;

            if (!IsBuyCancelOrderTypeCode(GetCancelOrderType(request.Signal.Action)))
                return;

            int price = request.Signal.Price <= 0
                ? 0
                : (int)Math.Round(request.Signal.Price);

            long amount = CalculateBuyReserveCashAmount(untradedQty, price);
            if (amount <= 0)
                return;

            AddAutoTradeAnalysisLog(
                $"[예수금 복원요청/자동취소] {reason} / {request.Signal.Code} / " +
                $"취소={amount:N0} / 키움동기화={KiwoomSyncedBalance:N0} / 사용가능={CurrentBalance:N0}");
        }

        private void AdjustMoneyCreatorCashForBuyModifyAccepted(
            string reason,
            OrderInfo originalOrder,
            int newQty,
            int newPrice)
        {
            if (!IsBuyOrderInfo(originalOrder))
                return;

            int oldQty = GetRemainingOrderQty(originalOrder);
            int oldPrice = ResolveOrderPriceForCash(originalOrder);

            long oldAmount = CalculateBuyReserveCashAmount(oldQty, oldPrice);
            long newAmount = CalculateBuyReserveCashAmount(newQty, newPrice);
            long delta = newAmount - oldAmount;

            if (delta > 0)
                AddMoneyCreatorAcceptedBuyReservation(originalOrder.Code, delta, reason + " 정정증가");
            else if (delta < 0)
                ReleaseMoneyCreatorAcceptedBuyReservation(originalOrder.Code, -delta, reason + " 정정감소");

            AddAutoTradeAnalysisLog(
                $"[예수금 정정예약] {reason} / {originalOrder.Code} / " +
                $"기존예약={oldAmount:N0} / 신규예약={newAmount:N0} / " +
                $"변동={delta:N0} / 키움동기화={KiwoomSyncedBalance:N0} / 사용가능={CurrentBalance:N0}");
        }

        private long GetPendingBuyAmount()
        {
            // 과거 호환용으로 메서드는 유지한다.
            // 예수금 계산에는 직접 사용하지 않고, 종목별 할당/미체결 동기화에서 예약금으로 반영한다.
            if (OrderHistory == null)
                return 0;

            long amount = 0;

            foreach (var order in OrderHistory)
            {
                if (!IsPendingBuyOrder(order))
                    continue;

                int price = ResolveOrderPriceForCash(order);
                if (price <= 0)
                    continue;

                amount += CalculateBuyReserveCashAmount(order.UntradedQty, price);
            }

            return amount;
        }

        public bool IsAutoTradingSmallScale
        {
            get => !_autoTradeSettings.UseFullCashSizingForTest;
            set
            {
                bool useFullCashSizing = !value;

                if (_autoTradeSettings.UseFullCashSizingForTest == useFullCashSizing)
                    return;

                _autoTradeSettings.UseFullCashSizingForTest = useFullCashSizing;
                OnPropertyChanged();

                _autoTradeController?.UpdateSettings(_autoTradeSettings);
                SyncAutoTradeState(value ? "SmallScale 모드 적용" : "FullCash 모드 적용");
                AddAutoTradeAnalysisLog(value
                    ? "[자동매매 수량모드] SmallScale 적용"
                    : "[자동매매 수량모드] FullCash 적용");
            }
        }

        private static bool IsHardStopOrderSignal(TradeSignal signal)
        {
            return signal != null &&
                   signal.Action == TradeActionType.Sell &&
                   string.Equals(signal.StrategyName, "HardStopLoss", StringComparison.OrdinalIgnoreCase);
        }

        private bool HasPendingSellOrderForCode(string code)
        {
            string key = NormalizeMoneyCreatorCode(code);
            if (string.IsNullOrWhiteSpace(key) || OrderHistory == null)
                return false;

            return OrderHistory.Any(order =>
                order != null &&
                string.Equals(
                    NormalizeMoneyCreatorCode(order.Code),
                    key,
                    StringComparison.OrdinalIgnoreCase) &&
                IsPendingSellOrder(order));
        }

        private static bool IsPendingSellOrder(OrderInfo order)
        {
            if (order == null || order.UntradedQty <= 0)
                return false;

            if (order.OrderStatus == OrderStatus.Cancelled ||
                order.OrderStatus == OrderStatus.Rejected ||
                order.OrderStatus == OrderStatus.FullyFilled)
            {
                return false;
            }

            string status = order.Status ?? string.Empty;
            if (status.Contains("취소") || status.Contains("거부"))
                return false;

            return !string.IsNullOrWhiteSpace(order.OrderType) &&
                   order.OrderType.Contains("매도");
        }

        private static bool IsPendingBuyOrder(OrderInfo order)
        {
            if (order == null || order.UntradedQty <= 0)
                return false;

            if (order.OrderStatus == OrderStatus.Cancelled ||
                order.OrderStatus == OrderStatus.Rejected ||
                order.OrderStatus == OrderStatus.FullyFilled)
                return false;

            string status = order.Status ?? string.Empty;
            if (status.Contains("취소") || status.Contains("거부"))
                return false;

            return !string.IsNullOrWhiteSpace(order.OrderType) &&
                   order.OrderType.Contains("매수");
        }

        // 외부 Account/Orders partial에서 호출할 수 있으므로 유지.
        // Core로 들어가는 Cash의 단일 통로다.
        private void UpdateAutoTradeCash(string reason = null)
        {
            try
            {
                if (_autoTradeController == null)
                    return;

                long moneyCreatorCash = GetAutoTradeAvailableCash();
                _autoTradeController.UpdateCash(moneyCreatorCash);

                var viewModels = GetMoneyCreatorAutoTradeViewModelsOnUiThread();
                RefreshMoneyCreatorPortfolioAllocationUsage(
                    viewModels,
                    string.IsNullOrWhiteSpace(reason) ? "예수금동기화" : reason);

                if (!string.IsNullOrWhiteSpace(reason))
                {
                    AddAutoTradeAnalysisLog(
                        $"[자동매매 예수금] {reason} / " +
                        $"키움동기화={KiwoomSyncedBalance:N0} / " +
                        $"MoneyCreator={CurrentBalance:N0} / " +
                        $"AutoTrading전달={moneyCreatorCash:N0}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[자동매매 예수금 동기화 실패] " + ex.Message);
            }
        }

        private DateTime ResolveAutoTradePositionTimestamp()
        {
            try
            {
                if (IsReplayMode && _marketApi is LocalReplayApiService replayApi)
                {
                    DateTime replayTime = replayApi.ReplayNow;
                    if (replayTime != default(DateTime) && replayTime != DateTime.MinValue)
                        return replayTime;
                }
            }
            catch
            {
            }

            return DateTime.Now;
        }

        private void SyncAutoTradePosition(string code)
        {
            if (string.IsNullOrWhiteSpace(code) || _autoTradeController == null)
                return;

            var holding = FindHolding(code);

            _autoTradeController.UpdatePosition(
                code,
                holding == null ? 0 : holding.Quantity,
                holding == null ? 0 : holding.EntryPrice,
                ResolveAutoTradePositionTimestamp());
        }

        private HoldingInfo FindHolding(string code)
        {
            if (string.IsNullOrWhiteSpace(code) || AccountHoldings == null)
                return null;

            return AccountHoldings.FirstOrDefault(x => x != null && x.Code == code);
        }

        private IEnumerable<OrderInfo> GetOpenOrdersForAutoTrade()
        {
            if (OrderHistory == null)
                return Enumerable.Empty<OrderInfo>();

            return OrderHistory
                .Where(x =>
                    x != null &&
                    !string.IsNullOrWhiteSpace(x.Code) &&
                    !string.IsNullOrWhiteSpace(x.OrderType))
                .Select(CloneOrderInfo)
                .ToList();
        }

        private bool SyncAutoTradeMinuteCandles(string code, string reason = null, DateTime? maxTime = null)
        {
            try
            {
                if (_autoTradeController == null || string.IsNullOrWhiteSpace(code))
                    return false;

                DateTime? effectiveMaxTime = ResolveEffectiveAutoTradeMinuteSyncCutoff(maxTime);

                var rawMinutes = GetMinuteCandlesForAutoTrade(code)
                    .Where(x => x != null)
                    .ToList();

                var minutes = rawMinutes
                    .Where(x => IsAutoTradeMinuteAllowedForCutoff(x, effectiveMaxTime))
                    .Select(CloneTrackedSymbolForAutoTrade)
                    .ToList();

                if (minutes.Count == 0)
                {
                    if (!string.IsNullOrWhiteSpace(reason) && IsReplayMode && effectiveMaxTime.HasValue)
                    {
                        AddAutoTradeAnalysisLog(
                            $"[Core분봉동기화 보류] {reason} / {code} / " +
                            $"ReplayCutoff={effectiveMaxTime.Value:HH:mm:ss} / raw={rawMinutes.Count:N0} / filtered=0");
                    }

                    return false;
                }

                _autoTradeController.UpdateMinuteCandles(code, minutes);

                if (!string.IsNullOrWhiteSpace(reason))
                {
                    var last = minutes[minutes.Count - 1];
                    int blockedFutureCount = rawMinutes.Count - minutes.Count;

                    AddAutoTradeAnalysisLog(
                        $"[Core분봉동기화] {reason} / {code} / " +
                        $"분봉={minutes.Count} / 마지막={last.LastUpdated:HH:mm} / 종가={last.LastPrice:N0}" +
                        (effectiveMaxTime.HasValue && effectiveMaxTime.Value != DateTime.MinValue ? $" / cutoff={effectiveMaxTime.Value:HH:mm:ss}" : string.Empty) +
                        (IsReplayMode ? $" / ReplayFutureBlocked={Math.Max(0, blockedFutureCount):N0}" : string.Empty));
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AutoTradeCore 분봉 동기화 실패] " + ex.Message);
                return false;
            }
        }

        private void TrySyncAutoTradeMinuteCandlesFromAnalysis(FeatureSnapshot feature)
        {
            if (feature == null || string.IsNullOrWhiteSpace(feature.Code))
                return;

            DateTime analysisTime = feature.Time == default(DateTime) ? DateTime.Now : feature.Time;
            if (analysisTime == DateTime.MinValue)
                return;

            DateTime minuteKey = TruncateAutoTradeMinute(analysisTime);
            string key = NormalizeMoneyCreatorCode(feature.Code);
            if (string.IsNullOrWhiteSpace(key))
                return;

            DateTime lastSync;
            if (_lastAutoTradeMinuteCandleSyncAtByCode.TryGetValue(key, out lastSync) && lastSync == minuteKey)
                return;

            DateTime cutoff = ResolveAutoTradeMinuteSyncCutoff(analysisTime);
            if (SyncAutoTradeMinuteCandles(feature.Code, IsReplayMode ? "Replay분변경" : "분변경", cutoff))
                _lastAutoTradeMinuteCandleSyncAtByCode[key] = minuteKey;
        }

        private DateTime? ResolveEffectiveAutoTradeMinuteSyncCutoff(DateTime? requestedMaxTime)
        {
            if (requestedMaxTime.HasValue &&
                requestedMaxTime.Value != DateTime.MinValue &&
                requestedMaxTime.Value != default(DateTime))
            {
                return requestedMaxTime.Value;
            }

            if (!IsReplayMode)
                return requestedMaxTime;

            try
            {
                if (_marketApi is LocalReplayApiService replayApi)
                {
                    DateTime replayTime = replayApi.ReplayNow;
                    if (replayTime != DateTime.MinValue && replayTime != default(DateTime))
                        return ResolveAutoTradeMinuteSyncCutoff(replayTime);

                    if (replayApi.ReplayTradingDate != DateTime.MinValue && replayApi.ReplayTradingDate != default(DateTime))
                        return replayApi.ReplayTradingDate.Date.AddHours(9).AddTicks(-1);
                }
            }
            catch
            {
            }

            return requestedMaxTime;
        }

        private DateTime ResolveAutoTradeMinuteSyncCutoff(DateTime analysisTime)
        {
            DateTime time = analysisTime;

            if (time == DateTime.MinValue || time == default(DateTime))
            {
                try
                {
                    if (IsReplayMode && _marketApi is LocalReplayApiService replayApi)
                    {
                        DateTime replayTime = replayApi.ReplayNow;
                        if (replayTime != DateTime.MinValue && replayTime != default(DateTime))
                            time = replayTime;
                        else if (replayApi.ReplayTradingDate != DateTime.MinValue && replayApi.ReplayTradingDate != default(DateTime))
                            time = replayApi.ReplayTradingDate.Date.AddHours(9);
                    }
                }
                catch
                {
                }
            }

            if (time == DateTime.MinValue || time == default(DateTime))
                time = DateTime.Now;

            DateTime currentMinute = TruncateAutoTradeMinute(time);
            return currentMinute.AddTicks(-1);
        }

        private static DateTime TruncateAutoTradeMinute(DateTime time)
        {
            if (time == DateTime.MinValue || time == default(DateTime))
                return time;

            return new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0);
        }

        private static bool IsAutoTradeMinuteAllowedForCutoff(TrackedSymbol minute, DateTime? maxTime)
        {
            if (minute == null)
                return false;

            if (!maxTime.HasValue || maxTime.Value == DateTime.MinValue || maxTime.Value == default(DateTime))
                return true;

            if (minute.LastUpdated == DateTime.MinValue || minute.LastUpdated == default(DateTime))
                return false;

            return minute.LastUpdated <= maxTime.Value;
        }

        private IEnumerable<TrackedSymbol> GetMinuteCandlesForAutoTrade(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return Enumerable.Empty<TrackedSymbol>();

            var holdingMinutes = GetHoldingStoreMinutes(code);
            if (holdingMinutes.Any())
                return holdingMinutes;

            return GetCurrentWindowMinutes(code);
        }

        private IEnumerable<TrackedSymbol> GetHoldingStoreMinutes(string code)
        {
            HoldingSymbolContext store;
            if (!GlobalHoldingStore.TryGet(code, out store) || store == null)
                return Enumerable.Empty<TrackedSymbol>();

            lock (store.SyncRoot)
            {
                if (store.MinuteList == null || store.MinuteList.Count == 0)
                    return Enumerable.Empty<TrackedSymbol>();

                return store.MinuteList
                    .Where(x =>
                        x != null &&
                        x.Code == code &&
                        x.LastUpdated != DateTime.MinValue &&
                        x.LastPrice > 0)
                    .Select(CloneTrackedSymbolForAutoTrade)
                    .ToList();
            }
        }

        private IEnumerable<TrackedSymbol> GetCurrentWindowMinutes(string code)
        {
            if (Context == null || Context.Code != code)
                return Enumerable.Empty<TrackedSymbol>();

            lock (_syncLock)
            {
                if (_minuteHistoryBuffer == null || _minuteHistoryBuffer.Count == 0)
                    return Enumerable.Empty<TrackedSymbol>();

                return _minuteHistoryBuffer
                    .Where(x =>
                        x != null &&
                        x.Code == code &&
                        x.CandleTime != DateTime.MinValue &&
                        x.Close > 0)
                    .Select(ToTrackedSymbol)
                    .Select(CloneTrackedSymbolForAutoTrade)
                    .ToList();
            }
        }

        private int GetActiveAutoTradeSlotCount()
        {
            try
            {
                var app = Application.Current;
                if (app == null)
                    return 1;

                if (app.Dispatcher.CheckAccess())
                    return GetActiveAutoTradeSlotCountOnUiThread();

                return app.Dispatcher.Invoke(GetActiveAutoTradeSlotCountOnUiThread);
            }
            catch
            {
                return 1;
            }
        }

        private int GetActiveAutoTradeSlotCountOnUiThread()
        {
            int count = Application.Current.Windows
                .Cast<Window>()
                .Select(w => w.DataContext as MainWindowViewModel)
                .Where(vm =>
                    vm != null &&
                    vm._autoTradeSettings != null &&
                    vm._autoTradeSettings.Enabled &&
                    vm.Context != null &&
                    !string.IsNullOrWhiteSpace(vm.Context.Code))
                .Select(vm => vm.Context.Code)
                .Distinct()
                .Count();

            return Math.Max(1, count);
        }


        private IReadOnlyList<string> GetActiveAutoTradeCodes()
        {
            try
            {
                var app = Application.Current;
                if (app == null)
                    return new string[0];

                if (app.Dispatcher.CheckAccess())
                    return GetActiveAutoTradeCodesOnUiThread();

                return app.Dispatcher.Invoke(GetActiveAutoTradeCodesOnUiThread);
            }
            catch
            {
                return new string[0];
            }
        }

        private IReadOnlyList<string> GetActiveAutoTradeCodesOnUiThread()
        {
            return Application.Current.Windows
                .Cast<Window>()
                .Select(w => w.DataContext as MainWindowViewModel)
                .Where(vm =>
                    vm != null &&
                    vm._autoTradeSettings != null &&
                    vm._autoTradeSettings.Enabled &&
                    vm.Context != null &&
                    !string.IsNullOrWhiteSpace(vm.Context.Code))
                .Select(vm => vm.Context.Code.Trim().Replace("A", string.Empty))
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void MarkAutoTradeDataReady(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return;

            _autoTradeReadyCode = code;
            _isAutoTradeDataReady = true;
        }

        private void MarkAutoTradeDataNotReady(string code = null)
        {
            _isAutoTradeDataReady = false;
            _autoTradeReadyCode = string.IsNullOrWhiteSpace(code) ? string.Empty : code;
        }

        private bool CanRunAutoTradeForTick(TrackedSymbol tick)
        {
            return tick != null &&
                   _autoTradeController != null &&
                   _autoTradeSettings != null &&
                   Context != null &&
                   Context.Code == tick.Code &&
                   _isHistoryBindingComplete &&
                   _isAutoTradeDataReady &&
                   _autoTradeReadyCode == tick.Code;
        }

        private static TrackedSymbol CloneTrackedSymbolForAutoTrade(TrackedSymbol x)
        {
            if (x == null)
                return null;

            return new TrackedSymbol
            {
                Code = x.Code,
                LastPrice = x.LastPrice,
                LastVolume = x.LastVolume,
                ClosePrice = x.ClosePrice,
                HighPrice = x.HighPrice,
                LowPrice = x.LowPrice,
                AccumulatedVolume = x.AccumulatedVolume,
                TotalAskQty = x.TotalAskQty,
                TotalBidQty = x.TotalBidQty,
                Strength = x.Strength,
                Side = x.Side,
                LastUpdated = x.LastUpdated
            };
        }

        // ============================================================
        // Source: MainWindowViewModel.AutoTrading.TiRex.cs
        // ============================================================
        // Stage6 리팩토링: 이 파일의 활성 로직은 다른 AutoTrading partial 파일로 통합됨.
        // 기존 csproj의 Compile Include 호환을 위해 파일은 유지한다.

        // ============================================================
        // Source: MainWindowViewModel.Performance.cs
        // ============================================================
        private readonly DispatcherTimer _performanceRefreshTimer = new DispatcherTimer();
        private bool _isRefreshingPerformance;
        private DateTime _lastPerformanceBuildTime = DateTime.MinValue;
        public ICommand RefreshCommand => new RelayCommand(_ => UpdateBalance());//잔고조회

        private string _accountPassword;
        private string _tradePerformanceStatusText = "성과 집계 대기";
        public string TradePerformanceStatusText
        {
            get => _tradePerformanceStatusText;
            set { _tradePerformanceStatusText = value; OnPropertyChanged(nameof(TradePerformanceStatusText)); }
        }
        private string _performanceLastUpdatedText = "-";
        public string PerformanceLastUpdatedText
        {
            get => _performanceLastUpdatedText;
            set { _performanceLastUpdatedText = value; OnPropertyChanged(nameof(PerformanceLastUpdatedText)); }
        }
        private int _todayTradeCount;
        public int TodayTradeCount
        {
            get => _todayTradeCount;
            set { _todayTradeCount = value; OnPropertyChanged(nameof(TodayTradeCount)); }
        }
        private int _todayWinCount;
        public int TodayWinCount
        {
            get => _todayWinCount;
            set { _todayWinCount = value; OnPropertyChanged(nameof(TodayWinCount)); }
        }
        private int _todayLossCount;
        public int TodayLossCount
        {
            get => _todayLossCount;
            set { _todayLossCount = value; OnPropertyChanged(nameof(TodayLossCount)); }
        }
        private double _todayWinRate;
        public double TodayWinRate
        {
            get => _todayWinRate;
            set { _todayWinRate = value; OnPropertyChanged(nameof(TodayWinRate)); }
        }
        private double _todayAvgProfitRate;
        public double TodayAvgProfitRate
        {
            get => _todayAvgProfitRate;
            set { _todayAvgProfitRate = value; OnPropertyChanged(nameof(TodayAvgProfitRate)); }
        }
        private double _todayNetProfit;
        public double TodayNetProfit
        {
            get => _todayNetProfit;
            set { _todayNetProfit = value; OnPropertyChanged(nameof(TodayNetProfit)); }
        }
        private double _todayGrossProfit;
        public double TodayGrossProfit
        {
            get => _todayGrossProfit;
            set { _todayGrossProfit = value; OnPropertyChanged(nameof(TodayGrossProfit)); }
        }
        private double _todayGrossLoss;
        public double TodayGrossLoss
        {
            get => _todayGrossLoss;
            set { _todayGrossLoss = value; OnPropertyChanged(nameof(TodayGrossLoss)); }
        }
        private double _todayFeeTax;
        public double TodayFeeTax
        {
            get => _todayFeeTax;
            set { _todayFeeTax = value; OnPropertyChanged(nameof(TodayFeeTax)); }
        }
        private double _todayBestTradeRate;
        public double TodayBestTradeRate
        {
            get => _todayBestTradeRate;
            set { _todayBestTradeRate = value; OnPropertyChanged(nameof(TodayBestTradeRate)); }
        }
        private double _todayWorstTradeRate;
        public double TodayWorstTradeRate
        {
            get => _todayWorstTradeRate;
            set { _todayWorstTradeRate = value; OnPropertyChanged(nameof(TodayWorstTradeRate)); }
        }
        private int _totalTradeCount;
        public int TotalTradeCount
        {
            get => _totalTradeCount;
            set { _totalTradeCount = value; OnPropertyChanged(nameof(TotalTradeCount)); }
        }
        private double _totalNetProfit;
        public double TotalNetProfit
        {
            get => _totalNetProfit;
            set { _totalNetProfit = value; OnPropertyChanged(nameof(TotalNetProfit)); }
        }
        private double _totalWinRate;
        public double TotalWinRate
        {
            get => _totalWinRate;
            set { _totalWinRate = value; OnPropertyChanged(nameof(TotalWinRate)); }
        }
        private double _totalAvgProfitRate;
        public double TotalAvgProfitRate
        {
            get => _totalAvgProfitRate;
            set { _totalAvgProfitRate = value; OnPropertyChanged(nameof(TotalAvgProfitRate)); }
        }
        private async void PerformanceRefreshTimer_Tick(object sender, EventArgs e)
        {
            if (_isDisposingOrDisposed)
                return;

            await RefreshTradePerformanceAsync(forceBuildToday: true);
        }

        private void StartPerformanceMonitor()
        {
            if (_isDisposingOrDisposed)
                return;

            try
            {
                _performanceRefreshTimer.Stop();
                _performanceRefreshTimer.Tick -= PerformanceRefreshTimer_Tick;

                _performanceRefreshTimer.Interval = TimeSpan.FromMinutes(5);
                _performanceRefreshTimer.Tick += PerformanceRefreshTimer_Tick;
                _performanceRefreshTimer.Start();

                // 프로그램 시작 직후 7일치 성과 재집계가 화면 표시/DB 초기화와 겹치지 않게 지연한다.
                // 최초 화면은 저장된 성과만 가볍게 읽고, 무거운 rebuild는 타이머/종료/명시 갱신 경로에 맡긴다.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(3000);

                        if (_isDisposingOrDisposed)
                            return;

                        Db?.StartDeferredStartupInitialization();

                        if (_isDisposingOrDisposed)
                            return;

                        await RefreshTradePerformanceAsync(forceBuildToday: false);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[성과 지연 로딩 실패] " + ex.Message);
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[성과 모니터 시작 실패] " + ex.Message);
            }
        }
        public async Task RefreshTradePerformanceAsync(bool forceBuildToday)
        {
            if (_isDisposingOrDisposed || _isRefreshingPerformance)
                return;

            _isRefreshingPerformance = true;

            try
            {
                var result = await Task.Run(() =>
                {
                    DailyPerformanceRecord today = null;
                    DailyPerformanceRecord total = null;

                    if (_isDisposingOrDisposed)
                        return new
                        {
                            Today = today,
                            Total = total
                        };

                    if (forceBuildToday)
                    {
                        today = Db?.BuildAndSaveDailyPerformance(DateTime.Today, rebuildTradeResults: true);
                        _lastPerformanceBuildTime = DateTime.Now;
                        total = Db?.RebuildAndSaveTotalPerformance();
                    }
                    else
                    {
                        today = Db?.GetDailyPerformance(DateTime.Today);
                        total = Db?.GetSavedCumulativePerformance();
                    }

                    return new
                    {
                        Today = today,
                        Total = total
                    };
                });

                if (_isDisposingOrDisposed)
                    return;

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                    return;

                dispatcher.Invoke(() =>
                {
                    if (_isDisposingOrDisposed)
                        return;

                    ApplyPerformanceSnapshot(result.Today, result.Total);
                });
            }
            catch (Exception ex)
            {
                if (!_isDisposingOrDisposed)
                    TradePerformanceStatusText = "성과 집계 실패";

                Debug.WriteLine("[성과 갱신 실패] " + ex.Message);
            }
            finally
            {
                _isRefreshingPerformance = false;
            }
        }
        private void ApplyPerformanceSnapshot(DailyPerformanceRecord today, DailyPerformanceRecord total)
        {
            if (today != null)
            {
                TodayTradeCount = today.TradeCount;
                TodayWinCount = today.WinCount;
                TodayLossCount = today.LossCount;
                TodayWinRate = today.WinRate;
                TodayAvgProfitRate = today.AvgProfitRate;
                TodayNetProfit = today.NetProfit;
                TodayGrossProfit = today.GrossProfit;
                TodayGrossLoss = today.GrossLoss;
                TodayFeeTax = today.FeeTax;
                TodayBestTradeRate = today.BestTradeRate;
                TodayWorstTradeRate = today.WorstTradeRate;
                PerformanceLastUpdatedText = today.UpdatedAt == default(DateTime)
                    ? "-"
                    : today.UpdatedAt.ToString("HH:mm:ss");
            }

            if (total != null)
            {
                TotalTradeCount = total.TradeCount;
                TotalNetProfit = total.NetProfit;
                TotalWinRate = total.WinRate;
                TotalAvgProfitRate = total.AvgProfitRate;
            }

            TradePerformanceStatusText = "성과 집계 정상";
        }

        // ============================================================
        // Source: MainWindowViewModel.TradeAnalysisCard.cs
        // ============================================================
        // ============================================================
        // 자동매매 분석 카드 바인딩 속성
        // - XAML의 TradeCardAccentBrush / TradeCard* 바인딩 대상
        // - AddAutoTradeAnalysisLog()에서 최신 분석 로그를 파싱해 갱신한다.
        // ============================================================
        private Brush _tradeCardAccentBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(36, 52, 71));
        public Brush TradeCardAccentBrush
        {
            get => _tradeCardAccentBrush;
            set => SetProperty(ref _tradeCardAccentBrush, value);
        }
        private Brush _tradeCardActionBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 238, 255));
        public Brush TradeCardActionBrush
        {
            get => _tradeCardActionBrush;
            set => SetProperty(ref _tradeCardActionBrush, value);
        }
        private Brush _tradeCardProfitBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 238, 255));
        public Brush TradeCardProfitBrush
        {
            get => _tradeCardProfitBrush;
            set => SetProperty(ref _tradeCardProfitBrush, value);
        }
        private string _tradeCardUpdatedAt = "--:--:--";
        public string TradeCardUpdatedAt
        {
            get => _tradeCardUpdatedAt;
            set => SetProperty(ref _tradeCardUpdatedAt, value);
        }
        private string _tradeCardTitle = "분석 대기";
        public string TradeCardTitle
        {
            get => _tradeCardTitle;
            set => SetProperty(ref _tradeCardTitle, value);
        }
        private string _tradeCardAction = "HOLD";
        public string TradeCardAction
        {
            get => _tradeCardAction;
            set => SetProperty(ref _tradeCardAction, value);
        }
        private string _tradeCardCode = "-";
        public string TradeCardCode
        {
            get => _tradeCardCode;
            set => SetProperty(ref _tradeCardCode, value);
        }
        private string _tradeCardPrice = "-";
        public string TradeCardPrice
        {
            get => _tradeCardPrice;
            set => SetProperty(ref _tradeCardPrice, value);
        }
        private string _tradeCardProfitRate = "-";
        public string TradeCardProfitRate
        {
            get => _tradeCardProfitRate;
            set => SetProperty(ref _tradeCardProfitRate, value);
        }
        private string _tradeCardScore = "-";
        public string TradeCardScore
        {
            get => _tradeCardScore;
            set => SetProperty(ref _tradeCardScore, value);
        }
        private string _tradeCardHoldingQty = "-";
        public string TradeCardHoldingQty
        {
            get => _tradeCardHoldingQty;
            set => SetProperty(ref _tradeCardHoldingQty, value);
        }
        private string _tradeCardStrength = "-";
        public string TradeCardStrength
        {
            get => _tradeCardStrength;
            set => SetProperty(ref _tradeCardStrength, value);
        }
        private string _tradeCardVolumeRatio = "-";
        public string TradeCardVolumeRatio
        {
            get => _tradeCardVolumeRatio;
            set => SetProperty(ref _tradeCardVolumeRatio, value);
        }
        private string _tradeCardOneMinuteChange = "-";
        public string TradeCardOneMinuteChange
        {
            get => _tradeCardOneMinuteChange;
            set => SetProperty(ref _tradeCardOneMinuteChange, value);
        }
        private string _tradeCardThreeMinuteChange = "-";
        public string TradeCardThreeMinuteChange
        {
            get => _tradeCardThreeMinuteChange;
            set => SetProperty(ref _tradeCardThreeMinuteChange, value);
        }
        private string _tradeCardBidAskRatio = "-";
        public string TradeCardBidAskRatio
        {
            get => _tradeCardBidAskRatio;
            set => SetProperty(ref _tradeCardBidAskRatio, value);
        }
        private string _tradeCardReason = "아직 분석 로그가 없습니다.";
        public string TradeCardReason
        {
            get => _tradeCardReason;
            set => SetProperty(ref _tradeCardReason, value);
        }
        private string _tradeCardPhase = "-";
        public string TradeCardPhase
        {
            get => _tradeCardPhase;
            set => SetProperty(ref _tradeCardPhase, value);
        }
        private string _tradeCardMarketPhase = "-";
        public string TradeCardMarketPhase
        {
            get => _tradeCardMarketPhase;
            set => SetProperty(ref _tradeCardMarketPhase, value);
        }
        private string _tradeCardTradeMode = "-";
        public string TradeCardTradeMode
        {
            get => _tradeCardTradeMode;
            set => SetProperty(ref _tradeCardTradeMode, value);
        }
        private string _tradeCardStrategy = "-";
        public string TradeCardStrategy
        {
            get => _tradeCardStrategy;
            set => SetProperty(ref _tradeCardStrategy, value);
        }
        private Brush MakeBrush(byte r, byte g, byte b)
        {
            var brush =
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));

            if (brush.CanFreeze)
                brush.Freeze();

            return brush;
        }
        private void ResetTradeAnalysisCard()
        {
            TradeCardAccentBrush = MakeBrush(36, 52, 71);
            TradeCardActionBrush = MakeBrush(220, 238, 255);
            TradeCardProfitBrush = MakeBrush(220, 238, 255);

            TradeCardUpdatedAt = "--:--:--";
            TradeCardTitle = "분석 대기";
            TradeCardAction = "HOLD";
            TradeCardCode = "-";
            TradeCardPrice = "-";
            TradeCardProfitRate = "-";
            TradeCardScore = "-";
            TradeCardHoldingQty = "-";
            TradeCardStrength = "-";
            TradeCardVolumeRatio = "-";
            TradeCardOneMinuteChange = "-";
            TradeCardThreeMinuteChange = "-";
            TradeCardBidAskRatio = "-";
            TradeCardReason = "아직 분석 로그가 없습니다.";
            TradeCardPhase = "-";
            TradeCardMarketPhase = "-";
            TradeCardTradeMode = "-";
            TradeCardStrategy = "-";
            TiRexPredictionText = "TiRex 대기";
            TiRexPredictionDetail = "-";
            CurrentSymbolAllocationText = "할당 -";
        }

        private string GetLogField(Dictionary<string, string> fields, string key, string fallback = "-")
        {
            if (fields == null || string.IsNullOrWhiteSpace(key))
                return fallback;

            return fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : fallback;
        }
        private Dictionary<string, string> ParseAutoTradeAnalysisFields(string msg)
        {
            var result = new Dictionary<string, string>();

            if (string.IsNullOrWhiteSpace(msg))
                return result;

            string text = msg;

            if (text.StartsWith("[틱분석]"))
                text = text.Substring("[틱분석]".Length).Trim();

            var parts = text.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                string item = part.Trim();

                if (string.IsNullOrWhiteSpace(item))
                    continue;

                int eqIndex = item.IndexOf('=');
                if (eqIndex <= 0)
                    continue;

                string key = item.Substring(0, eqIndex).Trim();
                string value = item.Substring(eqIndex + 1).Trim();

                if (string.IsNullOrWhiteSpace(key))
                    continue;

                result[key] = value;
            }

            return result;
        }
        private void UpdateTradeAnalysisCardFromLog(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg))
                return;

            bool isTickAnalysis = msg.Contains("종목=") || msg.Contains("[틱분석]");
            bool isSignalLog = msg.StartsWith("[Signal]", StringComparison.OrdinalIgnoreCase);

            if (!isTickAnalysis && !isSignalLog)
            {
                Application.Current?.Dispatcher.BeginInvoke(
                    DispatcherPriority.DataBind,
                    new Action(() =>
                    {
                        TradeCardUpdatedAt = DateTime.Now.ToString("HH:mm:ss");

                        TradeCardTitle =
                            Context != null && !string.IsNullOrWhiteSpace(Context.Name)
                                ? Context.Name
                                : "자동매매 분석";

                        TradeCardCode =
                            Context != null && !string.IsNullOrWhiteSpace(Context.Code)
                                ? Context.Code
                                : "-";

                        TradeCardReason = msg;

                        ApplyTradeCardBrushes(TradeCardAction, TradeCardProfitRate);
                    }));

                return;
            }

            Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.DataBind,
                new Action(() =>
                {
                    TradeCardUpdatedAt = DateTime.Now.ToString("HH:mm:ss");

                    if (isSignalLog)
                    {
                        var tokens = msg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                        if (tokens.Length >= 2)
                            TradeCardAction = tokens[1];

                        string parsedCode = null;

                        foreach (var token in tokens)
                        {
                            string cleaned = token.Trim();

                            if (Regex.IsMatch(cleaned, @"^\d{6}$"))
                            {
                                parsedCode = cleaned;
                                break;
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(parsedCode))
                        {
                            TradeCardCode = parsedCode;

                            TradeCardTitle =
                                Context != null &&
                                Context.Code == parsedCode &&
                                !string.IsNullOrWhiteSpace(Context.Name)
                                    ? Context.Name
                                    : parsedCode;
                        }
                        else
                        {
                            // 코드가 없는 Signal 로그에서는 기존 종목정보 유지.
                            // Pullback/Breakout 같은 전략명을 Title/Code에 넣지 않는다.
                            TradeCardCode =
                                Context != null && !string.IsNullOrWhiteSpace(Context.Code)
                                    ? Context.Code
                                    : TradeCardCode;

                            TradeCardTitle =
                                Context != null && !string.IsNullOrWhiteSpace(Context.Name)
                                    ? Context.Name
                                    : TradeCardTitle;
                        }

                        var scoreMatch = Regex.Match(msg, @"score=(\d+)");
                        if (scoreMatch.Success)
                            TradeCardScore = scoreMatch.Groups[1].Value;

                        var slashParts = msg.Split('/');

                        if (slashParts.Length >= 2)
                            TradeCardStrategy = slashParts[1].Trim();

                        if (slashParts.Length >= 3)
                            TradeCardReason = slashParts[2].Trim();

                        if (string.IsNullOrWhiteSpace(TradeCardPhase))
                            TradeCardPhase = "-";

                        if (string.IsNullOrWhiteSpace(TradeCardMarketPhase))
                            TradeCardMarketPhase = "-";

                        if (string.IsNullOrWhiteSpace(TradeCardTradeMode))
                            TradeCardTradeMode = "-";

                        ApplyTradeCardBrushes(TradeCardAction, TradeCardProfitRate);
                        return;
                    }

                    var fields = ParseAutoTradeAnalysisFields(msg);

                    string code = GetLogField(fields, "종목", Context?.Code ?? "-");
                    string action = GetLogField(fields, "판단", "HOLD");
                    string profitRate = GetLogField(fields, "수익률", "-");

                    string marketTrend = GetLogField(fields, "시장", "-");
                    string symbolPhase = GetLogField(fields, "종목국면", "-");

                    TradeCardCode = IsValidStockCode(code)
                        ? code
                        : Context?.Code ?? "-";

                    TradeCardTitle =
                        Context != null &&
                        Context.Code == TradeCardCode &&
                        !string.IsNullOrWhiteSpace(Context.Name)
                            ? Context.Name
                            : TradeCardCode;

                    TradeCardAction = action;
                    TradeCardScore = GetLogField(fields, "점수", "-");
                    TradeCardPrice = GetLogField(fields, "현재가", "-");
                    TradeCardHoldingQty = GetLogField(fields, "보유수량", "-");
                    TradeCardProfitRate = profitRate;
                    TradeCardOneMinuteChange = GetLogField(fields, "1분등락", "-");
                    TradeCardThreeMinuteChange = GetLogField(fields, "3분등락", "-");
                    TradeCardStrength = GetLogField(fields, "체결강도", "-");
                    TradeCardVolumeRatio = GetLogField(fields, "거래량비율", "-");
                    TradeCardBidAskRatio = GetLogField(fields, "매수우위", "-");

                    // 시장 전체 상태: Bear / Bull / Panic / Sideways 등
                    TradeCardPhase = string.IsNullOrWhiteSpace(marketTrend)
                        ? "-"
                        : marketTrend;

                    // 종목 자체 국면: Pullback / Breakout / Unstable 등
                    TradeCardMarketPhase = string.IsNullOrWhiteSpace(symbolPhase)
                        ? "-"
                        : symbolPhase;

                    TradeCardTradeMode = GetLogField(fields, "매매모드", GetLogField(fields, "TradeMode", "-"));
                    TradeCardStrategy = GetLogField(fields, "전략", "-");
                    string logReason = GetLogField(fields, "판단사유", msg);
                    string scoreSummary = GetLogField(fields, "점수구성", null);
                    TradeCardReason = string.IsNullOrWhiteSpace(scoreSummary)
                        ? logReason
                        : logReason + " / " + scoreSummary;

                    ApplyTradeCardBrushes(action, profitRate);
                }));
        }
        private bool IsValidStockCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return false;

            return Regex.IsMatch(code.Trim(), @"^\d{6}$");
        }

        private void ApplyTradeCardBrushes(string action, string profitRateText)
        {
            action = action ?? string.Empty;

            if (action.IndexOf("BUY", StringComparison.OrdinalIgnoreCase) >= 0 ||
                action.Contains("매수"))
            {
                TradeCardAccentBrush = MakeBrush(22, 59, 42);

                TradeCardActionBrush = MakeBrush(103, 245, 164);
            }
            else if (action.IndexOf("SELL", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     action.Contains("매도"))
            {
                TradeCardAccentBrush = MakeBrush(74, 31, 36);
                TradeCardActionBrush = MakeBrush(255, 138, 138);
            }
            else
            {
                TradeCardAccentBrush = MakeBrush(36, 52, 71);
                TradeCardActionBrush = MakeBrush(220, 238, 255);
            }

            double profitRate;
            string normalized = (profitRateText ?? string.Empty)
                .Replace("%", "")
                .Replace(",", "")
                .Trim();

            if (double.TryParse(normalized, out profitRate))
                TradeCardProfitBrush = profitRate >= 0
                    ? MakeBrush(115, 255, 154)
                    : MakeBrush(255, 119, 119);
            else
                TradeCardProfitBrush = MakeBrush(220, 238, 255);
        }
    }
}
