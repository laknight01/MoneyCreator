using System.ComponentModel;
using System;
using MoneyCreator.Models;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;

namespace MoneyCreator.ViewModels
{
    /// <summary>
    /// 주문 입력, 주문내역, 미체결 현황 중재 ViewModel.
    /// 주문 실행 로직은 누락 방지를 위해 원본 MainWindowViewModel 내부 경로를 유지한다.
    /// </summary>
    public sealed class OrdersViewModel : ViewModelBase, IDisposable
    {
        private readonly MainWindowViewModel _owner;
        protected MainWindowViewModel Owner => _owner;

        public OrdersViewModel(MainWindowViewModel owner)
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

        public ObservableCollection<OrderInfo> OrderHistory
        {
            get => Owner.OrderHistory;
            set
            {
                Owner.OrderHistory = value;
                Refresh(nameof(OrderHistory));
            }
        }

        public ObservableCollection<HoldingInfo> AccountHoldings
        {
            get => Owner.AccountHoldings;
            set
            {
                Owner.AccountHoldings = value;
                Refresh(nameof(AccountHoldings));
            }
        }

        public StockContext Context => Owner.Context;

        public int Quantity
        {
            get => Owner.Quantity;
            set
            {
                Owner.Quantity = value;
                Refresh(nameof(Quantity));
                Refresh(nameof(TotalAmount));
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
                Refresh(nameof(TotalAmount));
            }
        }

        public double TotalAmount => Owner.TotalAmount;

        public int HoldingQty
        {
            get => Owner.HoldingQty;
            set
            {
                Owner.HoldingQty = value;
                Refresh(nameof(HoldingQty));
            }
        }

        public string SelectedAccount
        {
            get => Owner.SelectedAccount;
            set
            {
                Owner.SelectedAccount = value;
                Refresh(nameof(SelectedAccount));
            }
        }

        public string WindowId => Owner.WindowId;

        public string ScreenNo
        {
            get => Owner.ScreenNo;
            set
            {
                Owner.ScreenNo = value;
                Refresh(nameof(ScreenNo));
            }
        }

        public Window HostWindow
        {
            get => Owner.HostWindow;
            set
            {
                Owner.HostWindow = value;
                Refresh(nameof(HostWindow));
            }
        }

        public ICommand SetQtyCommand => Owner.SetQtyCommand;

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