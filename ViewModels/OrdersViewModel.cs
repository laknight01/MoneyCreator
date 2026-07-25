using System.ComponentModel;
using System;
using MoneyCreator.Models;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;

using AxKHOpenAPILib;
using MoneyCreator.Biz;
using MoneyCreator.Service;
using MoneyCreator.View;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Windows.Threading;
using System.Windows.Media;
using MoneyCreator.AutoTrading;
using MoneyCreator.DataBase;
using HarfBuzzSharp;

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

namespace MoneyCreator.ViewModels
{
    public partial class MainWindowViewModel
    {
        // 매도 체결 예수금 즉시반영은 주문번호별 누적체결금 차액만 적용한다.
        // 동일 주문 재수신/부분체결 누적통보가 반복되어도 중복 가산하지 않는다.
        private static readonly Dictionary<string, long> _moneyCreatorAppliedSellReceivableByOrder =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _moneyCreatorSellCashLedgerInitializedAccounts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Source: MainWindowViewModel.Orders.cs
        // ============================================================
        public ICommand OrderCommand => new RelayCommand<string>(async (type) =>
        {
            if (string.IsNullOrWhiteSpace(SelectedAccount))
            {
                AddLog("⚠️ 계좌를 먼저 선택해줘.");
                return;
            }

            if (Context == null || string.IsNullOrWhiteSpace(Context.Code))
            {
                AddLog("⛔ 주문 실패: 종목코드 없음");
                return;
            }

            if (Quantity <= 0 || OrderPrice <= 0)
            {
                AddLog($"⛔ 주문 실패: 수량/가격 오류 / Qty={Quantity}, Price={OrderPrice}");
                return;
            }

            string action = GetOrderActionName(type);
            int orderTypeCode = type == "BUY" ? 1 : 2;

            string msg =
                $"[{action}] {Context.Name}\n" +
                $"가격 : {OrderPrice:#,0}원\n" +
                $"수량 : {Quantity:#,0}주\n" +
                BuildManualOrderCostText(type, Quantity, (int)OrderPrice) + "\n" +
                $"진행할까?";

            if (!await PopBox.Show(HostWindow, PopUpType.Confirm, msg))
                return;

            int result = _marketApi.SendOrder(
                WindowId,
                ScreenNo,
                SelectedAccount,
                orderTypeCode,
                Context.Code,
                Quantity,
                (int)OrderPrice,
                "00");

            if (result == 0)
            {
                DeductMoneyCreatorCashForAcceptedOrder(
                    action,
                    orderTypeCode,
                    Context.Code,
                    Quantity,
                    (int)OrderPrice);

                AddLog($"🚀 {action} 주문 전송 성공: {Context.Name} {Quantity}주");

                await PopBox.Show(
                    HostWindow,
                    PopUpType.Alert,
                    $"[주문전송완료]\n{Context.Name}: {OrderPrice:#,0}원 / {Quantity:#,0}주\n키움 응답 기준으로 주문내역을 갱신합니다.");

                RequestOrderStateRefresh("COMMON");
            }
            else
            {
                AddLog($"❌ {action} 주문 전송 실패: {Context.Name} / result={result}");
            }
        });

        public ICommand SetCurrentPriceCommand => new RelayCommand(() =>
        {
            if (Context != null && Context.LastPrice > 0)
            {
                OrderPrice = Context.LastPrice;
            }
            else
            {
                AddLog("⚠️ 현재가 데이터가 없습니다. 종목을 다시 선택해주세요.");
            }
        });

        public ICommand CancelOrderCommand => new RelayCommand<OrderInfo>(async (order) =>
        {
            RequestCancelOrder(order, "수동취소");
        });
        private void RequestCancelOrder(OrderInfo order, string reason)
        {
            Application.Current?.Dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(async () =>
                {
                    if (!ValidateOrderTarget(order, "취소"))
                        return;

                    int cancelOrderType = GetCancelOrderType(order);
                    if (cancelOrderType == 0)
                    {
                        AddLog($"⛔ 취소 실패: 주문구분 확인 불가 / {order.OrderType}");
                        return;
                    }

                    int cancelQty = GetRemainingOrderQty(order);
                    if (cancelQty <= 0)
                    {
                        AddLog($"⛔ 취소 실패: 취소 가능 수량 없음 / {order.Name}");
                        return;
                    }

                    int result = _marketApi.SendOrder(
                        WindowId,
                        ScreenNo,
                        SelectedAccount,
                        cancelOrderType,
                        order.Code,
                        cancelQty,
                        0,
                        "00",
                        order.OrderNo);

                    if (result == 0)
                    {
                        RestoreMoneyCreatorCashForCanceledBuyOrder(reason, order, cancelQty);
                        MarkMoneyCreatorOrderCanceledLocally(order.OrderNo, order.Code, cancelQty);

                        if (IsBuyOrderInfo(order))
                        {
                            int releasePrice = ResolveOrderPriceForCash(order);
                            ReleaseMoneyCreatorAcceptedBuyReservation(
                                order.Code,
                                CalculateBuyReserveCashAmount(cancelQty, releasePrice),
                                reason + " 취소요청 전송성공");
                            _autoTradeController?.ReleaseBuyReservation(
                                order.Code,
                                cancelQty,
                                releasePrice,
                                reason + " 취소요청 전송성공");
                            RefreshCurrentSymbolAllocationTextFromCore();
                        }

                        AddLog($"✅ 취소 요청 전송 성공: {order.Name} / {cancelQty}주 / 원주문={order.OrderNo}");
                        AddAutoTradeAnalysisLog($"[미체결 취소] {order.Code} / {cancelQty}주 / 원주문={order.OrderNo} / {reason}");

                        await RequestAccountStateRefreshAfterTrade(reason + " 취소요청 " + order.Code);
                    }
                    else
                    {
                        AddLog($"❌ 취소 요청 실패: {order.Name} / result={result}");
                        AddAutoTradeAnalysisLog($"[미체결 취소 실패] {order.Code} / result={result} / 원주문={order.OrderNo}");
                    }
                }));
        }
        public ICommand ModifyOrderCommand => new RelayCommand<OrderInfo>(async (order) =>
        {
            if (!ValidateOrderTarget(order, "정정"))
                return;

            int modifyCode = GetModifyOrderType(order);
            if (modifyCode == 0)
            {
                AddLog($"⛔ 정정 실패: 주문구분 확인 불가 / {order.OrderType}");
                return;
            }

            var modifyResult = await OrderView.ShowModify(HostWindow, order);
            if (!modifyResult.IsConfirmed)
                return;

            if (modifyResult.NewQty <= 0 || modifyResult.NewPrice <= 0)
            {
                AddLog($"⛔ 정정 실패: 수량/가격 오류 / Qty={modifyResult.NewQty}, Price={modifyResult.NewPrice}");
                return;
            }

            int sendResult = _marketApi.SendOrder(
                WindowId,
                ScreenNo,
                SelectedAccount,
                modifyCode,
                order.Code,
                modifyResult.NewQty,
                modifyResult.NewPrice,
                "00",
                order.OrderNo);

            if (sendResult == 0)
            {
                AdjustMoneyCreatorCashForBuyModifyAccepted(
                    "주문정정",
                    order,
                    modifyResult.NewQty,
                    modifyResult.NewPrice);

                AddLog($"✅ 정정 요청 전송 성공: {order.Name} / {modifyResult.NewPrice:#,0}원 / {modifyResult.NewQty}주");

                await PopBox.Show(
                    HostWindow,
                    PopUpType.Alert,
                    $"[정정전송완료]\n{order.Name}: {modifyResult.NewPrice:#,0}원 / {modifyResult.NewQty:#,0}주\n키움 응답 기준으로 주문내역을 갱신합니다.");

                RequestOrderStateRefresh("COMMON");
            }
            else
            {
                AddLog($"❌ 정정 요청 실패: {order.Name} / result={sendResult}");
            }
        });

        public ICommand AdjustPriceCommand => new RelayCommand<string>((param) =>
        {
            if (Price <= 0)
                return;

            double tickSize = GetTickSize(Price);
            int direction = int.Parse(param);

            OrderPrice += tickSize * direction;
        });

        // ============================================================
        // Source: MainWindowViewModel.Orders.Events.cs
        // ============================================================
        private async void Kiwoom_OnGlobalUpdateRequired(string accNo)
        {
            if (SelectedAccount != accNo)
                return;

            await Task.Delay(300);
            RequestOrderStateRefresh(WindowId);
        }

        private async void Kiwoom_OnOrderFilled(string windowId, string code, string name, string orderType, int filledQty, double filledPrice)
        {
            bool showPopup = IsOrderEventForCurrentWindow(windowId);
            bool shouldProcessSharedAccountState = ShouldProcessSharedAccountOrderEvent(windowId);
            string actionName = GetOrderTypeDisplayName(orderType);
            Task accountRefreshTask = Task.CompletedTask;

            // 주문 체결 이벤트는 주문내역/현금 예상치만 처리한다.
            // 보유수량은 중복 체결 통보를 피하기 위해 추정 차감하지 않고,
            // 별도의 잔고 Chejan(sGubun=1) 절대수량 이벤트에서 즉시 갱신한다.
            if (shouldProcessSharedAccountState)
            {
                // 매수 예약금 해제/매도 예수금 임시반영도 계좌 공통 처리이므로 대표 창 1회만 수행한다.
                if (!string.IsNullOrWhiteSpace(orderType) && orderType.Contains("매수"))
                {
                    ReleaseMoneyCreatorAcceptedBuyReservation(
                        code,
                        filledQty * filledPrice,
                        "매수체결 " + code);
                }
                else if (!string.IsNullOrWhiteSpace(orderType) && orderType.Contains("매도"))
                {
                    // OnOrderFilled의 filledQty는 부분체결/재통보 형태가 일정하지 않아
                    // 여기서 현금을 직접 가산하면 동일 체결이 중복 반영될 수 있다.
                    // 현금 즉시반영은 아래 OnOrdersReceived의 주문번호별 누적체결 차액으로만 처리한다.
                    RegisterMoneyCreatorPendingSellRefreshCode(code);
                }

                // 체결 직후 TR은 검증용으로 요청한다.
                // 보유종목/Core는 잔고 Chejan 절대수량에서 먼저 즉시 동기화된다.
                accountRefreshTask = RequestAccountStateRefreshAfterTrade(
                    !string.IsNullOrWhiteSpace(orderType) && orderType.Contains("매도")
                        ? "매도체결통보 " + code
                        : "체결통보 " + code);
                RefreshOrderHistory();
            }

            if (showPopup)
            {
                string msg =
                    $"[{name}] {actionName}\n" +
                    $"종목코드 : {code}\n" +
                    $"구분 : {actionName}\n" +
                    $"체결가격 : {filledPrice:#,0}원\n" +
                    $"체결수량 : {filledQty:#,0}주\n" +
                    $"체결금액 : {(filledPrice * filledQty):#,0}원\n" +
                    $"처리되었습니다.";

                await PopBox.Show(HostWindow, PopUpType.Alert, msg);
            }

            await accountRefreshTask;
        }

        private void Kiwoom_OnOrdersReceived(string windowId, List<OrderInfo> orders)
        {
            CompleteInitialMainLoadingToken("ORDERS");

            // 주문/체결내역 수신은 주문 UI/DB/마커만 갱신한다.
            // 여기서 계좌갱신을 다시 호출하면
            // 계좌갱신 -> 주문내역조회 -> 주문내역수신 -> 계좌갱신 루프가 생긴다.
            var safeOrders = PrepareIncomingOrders(orders, "KIWOOM");

            if (ShouldProcessSharedAccountOrderEvent(windowId))
                ApplyMoneyCreatorSellCashFromCumulativeOrders(safeOrders);

            if (safeOrders.Count == 0)
                return;

            ApplyOrdersToUi(safeOrders, syncReason: "키움 주문내역 갱신");
            SaveOrdersAndBuildTradeResults(safeOrders, buildTradeResultOnSell: true, logTitle: "주문내역");
        }

        private void ApplyMoneyCreatorSellCashFromCumulativeOrders(List<OrderInfo> orders)
        {
            orders = orders ?? new List<OrderInfo>();
            string accountKey = string.IsNullOrWhiteSpace(SelectedAccount) ? "COMMON" : SelectedAccount.Trim();
            bool initializedNow = false;

            lock (_moneyCreatorPortfolioSyncRoot)
            {
                if (!_moneyCreatorSellCashLedgerInitializedAccounts.Contains(accountKey))
                {
                    foreach (var baselineOrder in orders)
                    {
                        if (baselineOrder == null ||
                            string.IsNullOrWhiteSpace(baselineOrder.OrderNo) ||
                            !IsSellOrder(baselineOrder) ||
                            baselineOrder.ExecQty <= 0 ||
                            baselineOrder.ExecPrice <= 0)
                            continue;

                        string baselineKey = accountKey + "|" + baselineOrder.OrderNo.Trim();
                        _moneyCreatorAppliedSellReceivableByOrder[baselineKey] =
                            CalculateSellReceivableCashAmount(
                                baselineOrder.ExecQty,
                                (int)Math.Round(baselineOrder.ExecPrice));
                    }

                    _moneyCreatorSellCashLedgerInitializedAccounts.Add(accountKey);
                    initializedNow = true;
                }
            }

            if (initializedNow)
            {
                AddAutoTradeAnalysisLog(
                    $"[매도체결 현금장부 초기화] 계좌={accountKey} / 기존 주문은 기준값만 저장");
                return;
            }

            foreach (var order in orders)
            {
                if (order == null ||
                    string.IsNullOrWhiteSpace(order.OrderNo) ||
                    string.IsNullOrWhiteSpace(order.Code) ||
                    !IsSellOrder(order) ||
                    order.ExecQty <= 0 ||
                    order.ExecPrice <= 0)
                    continue;

                string ledgerKey = accountKey + "|" + order.OrderNo.Trim();
                long cumulativeReceivable = CalculateSellReceivableCashAmount(
                    order.ExecQty,
                    (int)Math.Round(order.ExecPrice));
                long deltaReceivable = 0;

                lock (_moneyCreatorPortfolioSyncRoot)
                {
                    long previouslyApplied;
                    _moneyCreatorAppliedSellReceivableByOrder.TryGetValue(ledgerKey, out previouslyApplied);

                    if (cumulativeReceivable > previouslyApplied)
                    {
                        deltaReceivable = cumulativeReceivable - previouslyApplied;
                        _moneyCreatorAppliedSellReceivableByOrder[ledgerKey] = cumulativeReceivable;
                    }
                }

                if (deltaReceivable <= 0)
                    continue;

                RegisterMoneyCreatorPendingSellRefreshCode(order.Code);
                ApplyMoneyCreatorSellFillCashImmediately(
                    order.Code,
                    deltaReceivable,
                    "매도누적체결차액 " + order.Code + " / 주문=" + order.OrderNo);

                AddAutoTradeAnalysisLog(
                    $"[매도체결 중복방지] {order.Code} / 주문={order.OrderNo} / " +
                    $"누적체결={order.ExecQty:N0}주 / 신규예상순수령액={deltaReceivable:N0}");
            }
        }

        private void Kiwoom_OnUnfilledOrdersReceived(string windowId, List<OrderInfo> orders)
        {
            // 미체결은 계좌 단위 상태다.
            // 자동매매 종목별 예약금은 미체결 상태를 기준으로 유지/해제되므로 모든 창에서 동기화한다.
            var safeOrders = PrepareIncomingOrders(orders, "UNFILLED");

            var unfilledOrders = safeOrders
                .Where(x =>
                    x != null &&
                    !string.IsNullOrWhiteSpace(x.Code) &&
                    !string.IsNullOrWhiteSpace(x.OrderNo) &&
                    GetRemainingOrderQty(x) > 0)
                .ToList();

            ReconcileMoneyCreatorAcceptedBuyReservationsWithUnfilledOrders(
                unfilledOrders,
                "키움 미체결 갱신");

            ApplyOrdersToUi(unfilledOrders, syncReason: "키움 미체결 갱신");
            SaveOrdersAndBuildTradeResults(unfilledOrders, buildTradeResultOnSell: false, logTitle: "미체결 주문");

            _autoTradeController?.UpdateOpenOrders(unfilledOrders);
        }

        private async Task RequestAccountStateRefreshAfterTrade(string reason)
        {
            if (string.IsNullOrWhiteSpace(SelectedAccount))
                return;

            if (!TryBeginMoneyCreatorAccountRefresh(reason, 250))
                return;

            try
            {
                RequestAccountStateRefreshCore(reason);

                await ScheduleAccountStateRefreshAfterTrade(reason, 1000);
                await ScheduleAccountStateRefreshAfterTrade(reason, 1500);
                await Task.Delay(1200).ConfigureAwait(false);

                EndMoneyCreatorAccountRefreshSequenceByTimeout(reason);
            }
            catch (Exception ex)
            {
                EndMoneyCreatorAccountRefreshSequenceByTimeout(reason);
                Debug.WriteLine("[계좌갱신 요청 처리 실패] " + ex.Message);
            }
        }

        private bool TryBeginMoneyCreatorAccountRefresh(string reason, int minIntervalMs)
        {
            DateTime now = DateTime.Now;

            lock (_moneyCreatorAccountRefreshSyncRoot)
            {
                if (_moneyCreatorAccountRefreshSequenceRunning)
                {
                    if (!string.IsNullOrWhiteSpace(reason))
                        AddAutoTradeAnalysisLog($"[계좌갱신요청 병합] 진행중 / {reason}");

                    return false;
                }

                if ((now - _lastMoneyCreatorAccountRefreshRequestAt).TotalMilliseconds < minIntervalMs)
                {
                    if (!string.IsNullOrWhiteSpace(reason))
                        AddAutoTradeAnalysisLog($"[계좌갱신요청 병합] {reason}");

                    return false;
                }

                _lastMoneyCreatorAccountRefreshRequestAt = now;
                _moneyCreatorAccountRefreshSequenceRunning = true;
                _moneyCreatorAccountRefreshCashReceived = false;
                _moneyCreatorAccountRefreshHoldingsReceived = false;
                _moneyCreatorAccountRefreshUnfilledReceived = false;
                _moneyCreatorAccountRefreshReason = reason;
                _moneyCreatorAccountRefreshStartedAt = now;
                return true;
            }
        }

        private async void RequestAccountStateRefreshCore(string reason)
        {
            if (string.IsNullOrWhiteSpace(SelectedAccount))
                return;

            _marketApi.RequestCashInfo(WindowId, SelectedAccount, ScreenNo);
            await Task.Delay(300);

            _marketApi.RequestAccountHoldings(WindowId, SelectedAccount, ScreenNo);
            await Task.Delay(300);

            _marketApi.RequestUnfilledOrders("COMMON", SelectedAccount, ScreenNo);

            if (!string.IsNullOrWhiteSpace(reason))
                AddAutoTradeAnalysisLog($"[계좌갱신요청] {reason}");
        }

        private async Task ScheduleAccountStateRefreshAfterTrade(string reason, int delayMs)
        {
            try
            {
                await Task.Delay(delayMs).ConfigureAwait(false);

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null)
                    return;

                if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                    return;

                await dispatcher.InvokeAsync(
                    new Action(() =>
                    {
                        if (Application.Current == null)
                            return;

                        RequestAccountStateRefreshCore(reason + " 재확인");
                    }),
                    DispatcherPriority.Background);
            }
            catch (TaskCanceledException)
            {
                // 창 종료 / Dispatcher 종료 / 예약 작업 취소는 정상 흐름
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[계좌갱신 재확인 실패] " + ex.Message);
            }
        }

        private bool IsOrderEventForCurrentWindow(string windowId)
        {
            return windowId == WindowId || windowId == "COMMON";
        }

        private bool ShouldProcessSharedAccountOrderEvent(string windowId)
        {
            if (windowId == WindowId)
                return true;

            if (!string.Equals(windowId, "COMMON", StringComparison.OrdinalIgnoreCase))
                return false;

            var app = Application.Current;
            if (app == null)
                return false;

            if (!app.Dispatcher.CheckAccess())
                return (bool)app.Dispatcher.Invoke(new Func<bool>(() => ShouldProcessSharedAccountOrderEvent(windowId)));

            var owner = app.Windows
                .Cast<Window>()
                .Select(w => w.DataContext as MainWindowViewModel)
                .Where(vm => vm != null)
                .OrderByDescending(vm => !string.IsNullOrWhiteSpace(vm.SelectedAccount))
                .FirstOrDefault();

            return ReferenceEquals(owner, this);
        }

        private List<OrderInfo> PrepareIncomingOrders(List<OrderInfo> orders, string fallbackStrategyName)
        {
            if (orders == null || orders.Count == 0)
                return new List<OrderInfo>();

            var result = new List<OrderInfo>();

            foreach (var source in orders)
            {
                if (source == null || string.IsNullOrWhiteSpace(source.Code))
                    continue;

                var order = CloneOrderInfo(source);
                NormalizeIncomingOrder(order, fallbackStrategyName);
                result.Add(order);
            }

            return result;
        }

        private void NormalizeIncomingOrder(OrderInfo order, string fallbackStrategyName)
        {
            if (order == null)
                return;

            order.OrderTime = NormalizeOrderTime(order.OrderTime);

            order.OrderStatus = ParseOrderStatus(
                order.Status,
                order.OrderType,
                order.ExecQty,
                order.UntradedQty);

            if (order.Price <= 0)
            {
                int resolvedPrice = ResolveOrderPriceForCash(order);
                if (resolvedPrice > 0)
                    order.Price = resolvedPrice;
            }

            if (string.IsNullOrWhiteSpace(order.TradeGroupId) && Db != null)
                order.TradeGroupId = Db.GetTradeGroupId(order.Code);

            if (string.IsNullOrWhiteSpace(order.StrategyName))
                order.StrategyName = fallbackStrategyName;

            ApplyAutoTradeOrderMetadata(order);
        }

        private void ApplyOrdersToUi(List<OrderInfo> orders, string syncReason)
        {
            var dispatcher = Application.Current == null ? null : Application.Current.Dispatcher;
            Action apply = () =>
            {
                if (orders != null)
                {
                    foreach (var order in orders)
                    {
                        AddOrUpdateOrderHistory(order);

                        if (order.ExecQty > 0 && order.ExecPrice > 0)
                            AddTradeMarkerIfNeeded(order);
                    }
                }

                RefreshOrderHistory();

                //if (completeInitialLoadingToken)
                //    CompleteInitialMainLoadingToken("ORDERS");

                bool containsFilledOrder = orders != null && orders.Any(x => x != null && x.ExecQty > 0);
                bool isOrderHistorySync = !string.IsNullOrWhiteSpace(syncReason) &&
                    syncReason.Contains("주문내역");

                if (containsFilledOrder && isOrderHistorySync)
                {
                    // 체결 주문내역은 주문 UI/마커만 반영한다.
                    // 보유종목/Core는 잔고 Chejan에서 즉시 반영하고, 보유 TR은 검증한다.
                    AddAutoTradeAnalysisLog($"[체결주문 반영] {syncReason} / 잔고Chejan 즉시반영·보유TR 검증");
                    return;
                }

                UpdateAutoTradeCash(syncReason);
                SyncAutoTradeState(syncReason);

                if (!string.IsNullOrWhiteSpace(syncReason) && syncReason.Contains("미체결"))
                    MarkMoneyCreatorAccountRefreshPartReceived("UNFILLED", "미체결 수신");
            };

            if (dispatcher == null || dispatcher.CheckAccess())
            {
                apply();
                return;
            }

            dispatcher.Invoke(DispatcherPriority.Send, apply);
        }

        private void SaveOrdersAndBuildTradeResults(List<OrderInfo> orders, bool buildTradeResultOnSell, string logTitle)
        {
            if (orders == null || orders.Count == 0 || Db == null)
                return;

            Task.Run(() =>
            {
                try
                {
                    foreach (var order in orders)
                    {
                        try
                        {
                            ObserveMoneyCreatorTradeCostFromOrder(order, logTitle);
                            Db.InsertOrder(order);

                            if (buildTradeResultOnSell && IsSellFilledOrder(order))
                                Db.BuildTradeResultFromOrders(order.TradeGroupId);
                        }
                        catch (Exception ex)
                        {
                            AddLog($"❌ {logTitle}/성과 저장 실패: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[{logTitle} 저장 실패] " + ex.Message);
                    AddLog($"❌ {logTitle} 저장 실패: {ex.Message}");
                }
            });
        }

        // ============================================================
        // Source: MainWindowViewModel.Orders.Mapping.cs
        // ============================================================
        private string GetOrderActionName(string type)
        {
            switch (type)
            {
                case "BUY":
                    return "매수";
                case "SELL":
                    return "매도";
                case "BUY_CANCEL":
                    return "매수취소";
                case "SELL_CANCEL":
                    return "매도취소";
                case "BUY_MODIFY":
                    return "매수정정";
                case "SELL_MODIFY":
                    return "매도정정";
                default:
                    return type ?? "-";
            }
        }

        private string GetOrderTypeDisplayName(string orderType)
        {
            if (string.IsNullOrWhiteSpace(orderType))
                return "체결";

            if (orderType.Contains("매수") && orderType.Contains("취소"))
                return "매수취소";

            if (orderType.Contains("매도") && orderType.Contains("취소"))
                return "매도취소";

            if (orderType.Contains("매수") && orderType.Contains("정정"))
                return "매수정정";

            if (orderType.Contains("매도") && orderType.Contains("정정"))
                return "매도정정";

            if (orderType.Contains("매수"))
                return "매수체결";

            if (orderType.Contains("매도"))
                return "매도체결";

            return orderType;
        }

        private double GetTickSize(double currentPrice)
        {
            if (currentPrice < 2000) return 1;
            if (currentPrice < 5000) return 5;
            if (currentPrice < 20000) return 10;
            if (currentPrice < 50000) return 50;
            if (currentPrice < 200000) return 100;
            return 500;
        }

        private async void RequestOrderStateRefresh(string windowId = "COMMON")
        {
            if (string.IsNullOrWhiteSpace(SelectedAccount))
                return;

            _marketApi.RequestUnfilledOrders(windowId, SelectedAccount, ScreenNo);
            await Task.Delay(300);

            _marketApi.RequestOrderHistory(windowId, SelectedAccount, ScreenNo);
        }

        private bool IsBuyOrder(OrderInfo order)
        {
            return order != null &&
                   !string.IsNullOrWhiteSpace(order.OrderType) &&
                   order.OrderType.Contains("매수");
        }

        private bool IsSellOrder(OrderInfo order)
        {
            return order != null &&
                   !string.IsNullOrWhiteSpace(order.OrderType) &&
                   order.OrderType.Contains("매도");
        }

        private bool IsSellFilledOrder(OrderInfo order)
        {
            return order != null &&
                   order.ExecQty > 0 &&
                   IsSellOrder(order) &&
                   !string.IsNullOrWhiteSpace(order.TradeGroupId);
        }

        private int GetCancelOrderType(OrderInfo order)
        {
            if (IsBuyOrder(order))
                return 3;

            if (IsSellOrder(order))
                return 4;

            return 0;
        }

        private int GetModifyOrderType(OrderInfo order)
        {
            if (IsBuyOrder(order))
                return 5;

            if (IsSellOrder(order))
                return 6;

            return 0;
        }

        private int GetRemainingOrderQty(OrderInfo order)
        {
            if (order == null)
                return 0;

            if (order.UntradedQty > 0)
                return order.UntradedQty;

            int remainingQty = order.Qty - order.ExecQty;
            return remainingQty > 0 ? remainingQty : 0;
        }

        private bool ValidateOrderTarget(OrderInfo order, string actionName)
        {
            if (order == null)
                return false;

            if (string.IsNullOrWhiteSpace(SelectedAccount))
            {
                AddLog($"⛔ {actionName} 실패: 계좌번호 없음");
                return false;
            }

            if (string.IsNullOrWhiteSpace(order.OrderNo))
            {
                AddLog($"⛔ {actionName} 실패: 원주문번호 없음 / {order.Code}");
                return false;
            }

            if (string.IsNullOrWhiteSpace(order.Code))
            {
                AddLog($"⛔ {actionName} 실패: 종목코드 없음");
                return false;
            }

            return true;
        }

        private static OrderInfo CloneOrderInfo(OrderInfo x)
        {
            if (x == null)
                return null;

            return new OrderInfo
            {
                OrderNo = x.OrderNo,
                TradeGroupId = x.TradeGroupId,
                OrderTime = x.OrderTime,
                Code = x.Code,
                Name = x.Name,
                OrderType = x.OrderType,
                Qty = x.Qty,
                ExecQty = x.ExecQty,
                UntradedQty = x.UntradedQty,
                Price = x.Price,
                ExecPrice = x.ExecPrice,
                Status = x.Status,
                OrderStatus = x.OrderStatus,
                StrategyName = x.StrategyName,
                Fee = x.Fee,
                Tax = x.Tax,
                ParentOrderNo = x.ParentOrderNo,
                TiRexDirection = x.TiRexDirection,
                TiRexConfidence = x.TiRexConfidence,
                TiRexPredictedReturn = x.TiRexPredictedReturn
            };
        }

        private string NormalizeOrderTime(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return DateTime.Now.ToString("yyyyMMddHHmmss");

            raw = raw.Trim();

            DateTime dt;

            if (DateTime.TryParseExact(raw, "yyyyMMddHHmmss", null,
                System.Globalization.DateTimeStyles.None, out dt))
                return raw;

            if (DateTime.TryParseExact(raw, "HH:mm:ss", null,
                System.Globalization.DateTimeStyles.None, out dt))
                return DateTime.Today.Add(dt.TimeOfDay).ToString("yyyyMMddHHmmss");

            if (DateTime.TryParseExact(raw, "HHmmss", null,
                System.Globalization.DateTimeStyles.None, out dt))
                return DateTime.Today.Add(dt.TimeOfDay).ToString("yyyyMMddHHmmss");

            if (DateTime.TryParse(raw, out dt))
                return dt.ToString("yyyyMMddHHmmss");

            return DateTime.Now.ToString("yyyyMMddHHmmss");
        }

        private DateTime ParseOrderDateTime(string orderTime, DateTime baseDate)
        {
            if (string.IsNullOrWhiteSpace(orderTime))
                return baseDate;

            DateTime dt;

            if (DateTime.TryParseExact(
                orderTime,
                "yyyyMMddHHmmss",
                null,
                System.Globalization.DateTimeStyles.None,
                out dt))
            {
                return dt;
            }

            if (DateTime.TryParseExact(
                orderTime,
                "HH:mm:ss",
                null,
                System.Globalization.DateTimeStyles.None,
                out dt))
            {
                return baseDate.Date.Add(dt.TimeOfDay);
            }

            if (DateTime.TryParse(orderTime, out dt))
                return dt;

            return baseDate;
        }

        private DateTime ParseOrderTime(string orderTime)
        {
            return ParseOrderDateTime(orderTime, GetCurrentChartDate());
        }

        private OrderStatus ParseOrderStatus(
            string status,
            string orderType,
            int execQty,
            int untradedQty)
        {
            status = status?.Trim() ?? string.Empty;
            orderType = orderType?.Trim() ?? string.Empty;

            if (orderType.Contains("취소"))
                return OrderStatus.Cancelled;

            if (orderType.Contains("정정") && status == "확인")
                return OrderStatus.Corrected;

            if (execQty > 0 && untradedQty > 0)
                return OrderStatus.PartiallyFilled;

            if (execQty > 0 && untradedQty <= 0)
                return OrderStatus.FullyFilled;

            if (untradedQty > 0)
            {
                if (status == "접수")
                    return OrderStatus.Received;

                return OrderStatus.Confirmed;
            }

            return status switch
            {
                "접수" => OrderStatus.Received,
                "확인" => OrderStatus.Confirmed,
                "체결" => OrderStatus.FullyFilled,
                "부분체결" => OrderStatus.PartiallyFilled,
                "정정" => OrderStatus.Corrected,
                "취소" => OrderStatus.Cancelled,
                "거부" => OrderStatus.Rejected,
                _ => OrderStatus.None
            };
        }

        // ============================================================
        // Source: MainWindowViewModel.Orders.Markers.cs
        // ============================================================
        private void AddTradeMarkerIfNeeded(OrderInfo order)
        {
            if (order == null)
                return;

            if (order.ExecQty <= 0 || order.ExecPrice <= 0)
                return;

            if (string.IsNullOrWhiteSpace(order.Code))
                return;

            if (Context == null || order.Code != Context.Code)
                return;

            DateTime time;

            if (!DateTime.TryParse(order.OrderTime, out time))
                time = DateTime.Now;

            bool isBuy = order.OrderType != null && order.OrderType.Contains("매수");
            bool isSell = order.OrderType != null && order.OrderType.Contains("매도");

            if (!isBuy && !isSell)
                return;

            string key = $"{order.OrderNo}_{order.ExecQty}_{order.ExecPrice}_{isBuy}";

            if (_tradeMarkerKeys.Contains(key))
                return;

            _tradeMarkerKeys.Add(key);

            TradeMarkerRequested?.Invoke(time, order.ExecPrice, isBuy);
        }


        private ChartPayload CreateChartPayload(List<TrackedSymbol> candles, bool isFullRefresh)
        {
            return new ChartPayload
            {
                Candles = candles ?? new List<TrackedSymbol>(),
                Markers = GetCurrentTradeMarkers(),
                IsFullRefresh = isFullRefresh
            };
        }


        private List<TradeMarkerInfo> GetMarkersForChartRange(List<TrackedSymbol> candles)
        {
            // 차트는 실시간 때 마지막 분봉 1개만 받을 수 있다.
            // 따라서 마커를 candle 범위로 자르면 기존 매수/매도 점이 사라진다.
            // 현재 차트 날짜 기준 전체 주문 마커를 함께 넘긴다.
            return GetCurrentTradeMarkers();
        }


        public List<TradeMarkerInfo> GetCurrentTradeMarkers()
        {
            if (Context == null || string.IsNullOrWhiteSpace(Context.Code))
                return new List<TradeMarkerInfo>();

            DateTime chartDate = GetCurrentChartDate();

            var source = OrderHistory
                .Where(x =>
                    x != null &&
                    x.Code == Context.Code &&
                    x.ExecQty > 0 &&
                    ParseOrderDateTime(x.OrderTime, chartDate).Date == chartDate.Date)
                .GroupBy(x => string.IsNullOrWhiteSpace(x.OrderNo)
                    ? $"{x.Code}_{x.OrderTime}_{x.ExecPrice}_{x.Price}_{x.OrderType}"
                    : x.OrderNo)
                .Select(g => g.Last())
                .ToList();

            if (source.Count == 0)
            {
                try
                {
                    source = Db.LoadOrdersForChartMarkers(Context.Code, chartDate);
                }
                catch (Exception ex)
                {
                    AddLog($"❌ DB 매매마커 조회 실패: {ex.Message}");
                    source = new List<OrderInfo>();
                }
            }

            return source
                .Where(x => x != null && x.ExecQty > 0)
                .Select(x => new TradeMarkerInfo
                {
                    Time = ParseOrderDateTime(x.OrderTime, chartDate),
                    Price = x.ExecPrice > 0 ? x.ExecPrice : x.Price,
                    IsBuy = x.OrderType != null && x.OrderType.Contains("매수")
                })
                .Where(x => x.Price > 0)
                .OrderBy(x => x.Time)
                .ToList();
        }


        private DateTime GetCurrentChartDate()
        {
            if (Context.SymbolList != null && Context.SymbolList.Count > 0)
                return Context.SymbolList[Context.SymbolList.Count - 1].LastUpdated.Date;

            return DateTime.Today;
        }


        private void TryAddTradeMarkerFromOrder(OrderInfo order)
        {
            if (order == null)
                return;

            if (string.IsNullOrWhiteSpace(Context.Code) || order.Code != Context.Code)
                return;

            if (order.ExecQty <= 0)
                return;

            double markerPrice = order.ExecPrice > 0 ? order.ExecPrice : order.Price;

            if (markerPrice <= 0)
                return;

            // 여기서 차트 전체 재바인딩 금지.
            // 실시간 분봉 갱신 때 CreateChartPayload()에서 Markers가 같이 넘어간다.
        }

        // ============================================================
    }
}
