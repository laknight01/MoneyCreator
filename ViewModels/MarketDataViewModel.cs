using System.ComponentModel;
using MoneyCreator.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;

namespace MoneyCreator.ViewModels
{
    /// <summary>
    /// 실시간 시세, 종목 검색/로딩, 차트 데이터 중재 ViewModel.
    /// </summary>
    public sealed class MarketDataViewModel : ViewModelBase, IDisposable
    {
        private readonly MainWindowViewModel _owner;
        protected MainWindowViewModel Owner => _owner;

        public MarketDataViewModel(MainWindowViewModel owner)
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

        public ObservableCollection<Symbol> Symbols => Owner.Symbols;
        public ObservableCollection<AiResult> AiSelectedSymbols => Owner.AiSelectedSymbols;
        public ObservableCollection<Symbol> SearchResults => Owner.SearchResults;
        public ObservableCollection<HogaItem> AskList => Owner.AskList;
        public ObservableCollection<HogaItem> BidList => Owner.BidList;

        public StockContext Context => Owner.Context;

        public string SearchQuery
        {
            get => Owner.SearchQuery;
            set
            {
                Owner.SearchQuery = value;
                Refresh(nameof(SearchQuery));
            }
        }

        public bool IsSearchPopupOpen
        {
            get => Owner.IsSearchPopupOpen;
            set
            {
                Owner.IsSearchPopupOpen = value;
                Refresh(nameof(IsSearchPopupOpen));
            }
        }

        public Symbol SelectedSearchSymbol
        {
            get => Owner.SelectedSearchSymbol;
            set
            {
                Owner.SelectedSearchSymbol = value;
                Refresh(nameof(SelectedSearchSymbol));
            }
        }

        public bool IsLoading
        {
            get => Owner.IsLoading;
            set
            {
                Owner.IsLoading = value;
                Refresh(nameof(IsLoading));
            }
        }

        public double Price
        {
            get => Owner.Price;
            set
            {
                Owner.Price = value;
                Refresh(nameof(Price));
            }
        }

        public double OrderPrice
        {
            get => Owner.OrderPrice;
            set
            {
                Owner.OrderPrice = value;
                Refresh(nameof(OrderPrice));
            }
        }

        public long TotalAskQty
        {
            get => Owner.TotalAskQty;
            set
            {
                Owner.TotalAskQty = value;
                Refresh(nameof(TotalAskQty));
            }
        }

        public long TotalBidQty
        {
            get => Owner.TotalBidQty;
            set
            {
                Owner.TotalBidQty = value;
                Refresh(nameof(TotalBidQty));
            }
        }

        public MarketIndexInfo Kospi
        {
            get => Owner.Kospi;
            set
            {
                Owner.Kospi = value;
                Refresh(nameof(Kospi));
            }
        }

        public MarketIndexInfo Kosdaq
        {
            get => Owner.Kosdaq;
            set
            {
                Owner.Kosdaq = value;
                Refresh(nameof(Kosdaq));
            }
        }

        public string MarketTrendText
        {
            get => Owner.MarketTrendText;
            set
            {
                Owner.MarketTrendText = value;
                Refresh(nameof(MarketTrendText));
            }
        }

        public ICommand SearchCommand => Owner.SearchCommand;
        public ICommand AiToggleCommand => Owner.AiToggleCommand;
        public ICommand OpenAllHoldingsCommand => Owner.OpenAllHoldingsCommand;

        public event Action<ChartPayload> RequestChartRefresh
        {
            add { Owner.RequestChartRefresh += value; }
            remove { Owner.RequestChartRefresh -= value; }
        }

        public event Action OnChartResetRequired
        {
            add { Owner.OnChartResetRequired += value; }
            remove { Owner.OnChartResetRequired -= value; }
        }

        public event Action<DateTime, double, bool> TradeMarkerRequested
        {
            add { Owner.TradeMarkerRequested += value; }
            remove { Owner.TradeMarkerRequested -= value; }
        }

        public void UpdateSearchResults(string query)
        {
            Owner.UpdateSearchResults(query);
        }

        public Task LoadSymbolDataAsync(Symbol symbol, bool waitHoldingLoad = true)
        {
            return Owner.LoadSymbolDataAsync(symbol, waitHoldingLoad);
        }

        public Task OpenStockWindow(HoldingInfo holding)
        {
            return Owner.OpenStockWindow(holding);
        }

        public void ClearBuffer()
        {
            Owner.ClearBuffer();
        }

        public void OnHogaReceived(string windowId, HogaData data)
        {
            Owner.OnHogaReceived(windowId, data);
        }

        public List<TradeMarkerInfo> GetCurrentTradeMarkers()
        {
            return Owner.GetCurrentTradeMarkers();
        }

        public void AddLog(string message)
        {
            Owner.AddLog(message);
        }
    }
}