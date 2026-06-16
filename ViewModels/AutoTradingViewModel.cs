using System.ComponentModel;
using System;
using MoneyCreator.AutoTrading;
using MoneyCreator.Models;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;

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