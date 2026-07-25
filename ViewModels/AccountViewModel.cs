using System.ComponentModel;
using System;
using AxKHOpenAPILib;
using MoneyCreator.Models;
using System.Collections.ObjectModel;

using MoneyCreator.Biz;
using MoneyCreator.Service;
using MoneyCreator.View;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using MoneyCreator.AutoTrading;
using MoneyCreator.DataBase;
using HarfBuzzSharp;

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

namespace MoneyCreator.ViewModels
{
    public partial class MainWindowViewModel
    {
        // Source: MainWindowViewModel.Account.cs
        // ============================================================
        #region 사용자정보
        public ObservableCollection<string> AccountList { get; set; } = new ObservableCollection<string>();
        private string _selectedAccount;
        public string SelectedAccount
        {
            get => _selectedAccount;
            set
            {
                if (_selectedAccount == value)
                    return;

                _selectedAccount = value;
                OnPropertyChanged();

                if (_suppressAccountAutoRefresh)
                    return;

                if (AccountHoldings == null || AccountHoldings.Count == 0)
                {
                    UpdateBalance();
                }
                else
                {
                    AccountHoldings.Clear();
                    UpdateBalance();
                }
            }
        }
        public void ApplyParentAccountContext(
            string accountNo,
            long currentBalance,
            ObservableCollection<HoldingInfo> accountHoldings,
            ObservableCollection<OrderInfo> orderHistory)
        {
            _suppressAccountAutoRefresh = true;

            try
            {
                _selectedAccount = accountNo;
                OnPropertyChanged(nameof(SelectedAccount));

                long sharedRaw;
                long sharedCash;
                bool hasSharedCash;
                lock (_moneyCreatorPortfolioSyncRoot)
                {
                    sharedRaw = _moneyCreatorKiwoomCashRaw;
                    sharedCash = _moneyCreatorEffectiveCash;
                    hasSharedCash = _moneyCreatorCashSnapshotReady;
                }

                if (hasSharedCash)
                {
                    KiwoomSyncedBalance = sharedRaw;
                    CurrentBalance = sharedCash;
                }
                else if (currentBalance > 0)
                {
                    CurrentBalance = currentBalance;
                    KiwoomSyncedBalance = currentBalance;
                }
                else
                {
                    AddAutoTradeAnalysisLog(
                        $"[계좌 컨텍스트 예수금 보류] 0원 이하 수신 / 기존 CurrentBalance={CurrentBalance:N0}, 키움동기화={KiwoomSyncedBalance:N0}");
                }

                AccountHoldings = accountHoldings ?? new ObservableCollection<HoldingInfo>();
                OrderHistory = orderHistory ?? new ObservableCollection<OrderInfo>();

                // CurrentBalance와 OrderHistory가 모두 반영된 뒤 자동매매용 예수금을 계산해야 한다.
                UpdateAutoTradeCash("계좌 컨텍스트 적용");
            }
            finally
            {
                _suppressAccountAutoRefresh = false;
            }
        }

        private bool _suppressAccountAutoRefresh;

        // 키움에서 받은 예수금은 동기화/검증용 원본값이다.
        // MoneyCreator 내부와 UI, AutoTrading 전달값은 CurrentBalance를 사용한다.
        private long _kiwoomSyncedBalance;
        private bool _isMoneyCreatorCashInitialized;

        public long KiwoomSyncedBalance
        {
            get => _kiwoomSyncedBalance;
            private set
            {
                _kiwoomSyncedBalance = value;
                OnPropertyChanged(nameof(KiwoomSyncedBalance));
            }
        }

        private long _currentBalance;
        public long CurrentBalance
        {
            get => _currentBalance;
            set
            {
                _currentBalance = value;
                _isMoneyCreatorCashInitialized = true;
                OnPropertyChanged(nameof(CurrentBalance));
            }
        }
        private void FetchAccountList()
        {
            if (_api == null)
                return;

            string accList = _api.GetLoginInfo("ACCNO").Trim();

            var accounts = accList
                .Split(';')
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Where(x => x.Length >= 10)
                .Select(x => x.Substring(0, 10))
                .Distinct()
                .ToList();

            Application.Current.Dispatcher.Invoke(() =>
            {
                AccountList.Clear();

                foreach (var account in accounts)
                    AccountList.Add(account);

                SelectedAccount = AccountList.FirstOrDefault();
            });
        }

        private async void UpdateBalance()
        {
            if (_suppressAccountAutoRefresh)
                return;

            if (string.IsNullOrEmpty(SelectedAccount) || _api == null)
                return;

            BeginInitialMainLoading();

            _marketApi.RequestCashInfo(WindowId, SelectedAccount, ScreenNo);
            await Task.Delay(300);

            _marketApi.RequestAccountHoldings(WindowId, SelectedAccount, ScreenNo);
            await Task.Delay(300);

            _marketApi.RequestOrderHistory(WindowId, SelectedAccount, ScreenNo);
        }

        public void UpdateBalanceFromApi(long currentBalance)
        {
            Application.Current?.Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() =>
                {
                    if (currentBalance < 0)
                    {
                        AddAutoTradeAnalysisLog(
                            $"[예수금 수신 무시] 음수 수신={currentBalance:N0} / 기존={CurrentBalance:N0}");
                        return;
                    }

                    if (currentBalance == 0 && _isMoneyCreatorCashInitialized && CurrentBalance > 0)
                    {
                        AddAutoTradeAnalysisLog(
                            $"[예수금 수신 보류] 0원 수신 / 기존 CurrentBalance 유지={CurrentBalance:N0}");
                        SynchronizeMoneyCreatorCashFromCurrentKiwoomSnapshot("예수금 0원 수신 보류");
                        MarkMoneyCreatorAccountRefreshPartReceived("CASH", "예수금 0원 수신 보류");
                        return;
                    }

                    SynchronizeMoneyCreatorCashFromKiwoom(currentBalance, "예수금 수신/키움 동기화");
                    MarkMoneyCreatorAccountRefreshPartReceived("CASH", "예수금 수신");
                }));
        }
        // 4. 잔고 업데이트
        private void Kiwoom_OnBalanceUpdated(string wId, long balance)
        {
            CompleteInitialMainLoadingToken("CASH");
            UpdateBalanceFromApi(balance);
        }
        #endregion

        public void RegisterApi(AxKHOpenAPI api)
        {
            if (api == null)
                return;

            _api = api;
            _marketApi.SetApiInstance(api);
            AddLog("✅ API 인스턴스 등록 완료");
        }

        // ============================================================
    }
}
