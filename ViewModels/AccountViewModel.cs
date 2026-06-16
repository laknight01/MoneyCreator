using System.ComponentModel;
using System;
using AxKHOpenAPILib;
using MoneyCreator.Models;
using System.Collections.ObjectModel;

namespace MoneyCreator.ViewModels
{
    /// <summary>
    /// 계좌 정보, 잔고, 예수금 담당 ViewModel.
    /// 원본 MainWindowViewModel의 상태를 누락 없이 중재하기 위해 Owner를 통해 접근한다.
    /// </summary>
    public sealed class AccountViewModel : ViewModelBase, IDisposable
    {
        private readonly MainWindowViewModel _owner;
        protected MainWindowViewModel Owner => _owner;

        public AccountViewModel(MainWindowViewModel owner)
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

        public ObservableCollection<string> AccountList => Owner.AccountList;

        public string SelectedAccount
        {
            get => Owner.SelectedAccount;
            set
            {
                Owner.SelectedAccount = value;
                Refresh(nameof(SelectedAccount));
            }
        }

        public string AccountPassword
        {
            get => Owner.AccountPassword;
            set
            {
                Owner.AccountPassword = value;
                Refresh(nameof(AccountPassword));
            }
        }

        public long CurrentBalance
        {
            get => Owner.CurrentBalance;
            set
            {
                Owner.CurrentBalance = value;
                Refresh(nameof(CurrentBalance));
            }
        }

        public long KiwoomSyncedBalance => Owner.KiwoomSyncedBalance;

        public ObservableCollection<HoldingInfo> AccountHoldings
        {
            get => Owner.AccountHoldings;
            set
            {
                Owner.AccountHoldings = value;
                Refresh(nameof(AccountHoldings));
            }
        }

        public int HoldingQty
        {
            get => Owner.HoldingQty;
            set
            {
                Owner.HoldingQty = value;
                Refresh(nameof(HoldingQty));
            }
        }

        public HoldingInfo SelectedHolding
        {
            get => Owner.SelectedHolding;
            set
            {
                Owner.SelectedHolding = value;
                Refresh(nameof(SelectedHolding));
            }
        }

        public StockContext Context => Owner.Context;
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

        public void ApplyParentAccountContext(
            string accountNo,
            long currentBalance,
            ObservableCollection<HoldingInfo> accountHoldings,
            ObservableCollection<OrderInfo> orderHistory)
        {
            Owner.ApplyParentAccountContext(accountNo, currentBalance, accountHoldings, orderHistory);
        }

        public void UpdateBalanceFromApi(long currentBalance)
        {
            Owner.UpdateBalanceFromApi(currentBalance);
        }

        public void RegisterApi(AxKHOpenAPI api)
        {
            Owner.RegisterApi(api);
        }

        public void CheckHoldingStatus()
        {
            Owner.CheckHoldingStatus();
        }

        public void AddLog(string message)
        {
            Owner.AddLog(message);
        }
    }
}