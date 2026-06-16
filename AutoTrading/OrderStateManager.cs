using MoneyCreator.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace MoneyCreator.AutoTrading
{
    public sealed class OrderStateManager
    {
        private sealed class OrderLockState
        {
            public string AccountNo { get; set; }
            public string Code { get; set; }
            public TradeActionType Side { get; set; }
            public string OrderNo { get; set; }
            public int Qty { get; set; }
            public int UntradedQty { get; set; }
            public long ReservedAmount { get; set; }
            public OrderLifecycleState State { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime LastUpdated { get; set; }
            public bool CancelRequested { get; set; }
        }

        private static readonly object _cashReserveLock = new object();

        private static readonly ConcurrentDictionary<string, long> _reservedBuyAmountByAccount =
            new ConcurrentDictionary<string, long>();

        private readonly ConcurrentDictionary<string, OrderLockState> _locks =
            new ConcurrentDictionary<string, OrderLockState>();

        public bool CanAccept(OrderRequest request)
        {
            if (request == null || request.Signal == null)
                return false;

            string code = request.Signal.Code;
            TradeActionType side = request.Signal.Action;

            if (string.IsNullOrWhiteSpace(code))
                return false;

            if (side == TradeActionType.Buy)
            {
                // 매수 미체결 또는 매도 미체결 중에는 신규 매수 차단
                return !HasPendingBuy(code) && !HasPendingSell(code);
            }

            if (side == TradeActionType.Sell)
            {
                // 매도 미체결 중에는 추가 매도 차단
                // 매수 미체결 중에도 기존 보유분 매도는 허용
                return !HasPendingSell(code);
            }

            return false;
        }
        public bool TryAcceptAndReserve(OrderRequest request)
        {
            if (request == null || request.Signal == null)
                return false;

            string code = request.Signal.Code;
            TradeActionType side = request.Signal.Action;

            if (string.IsNullOrWhiteSpace(code))
                return false;

            lock (_cashReserveLock)
            {
                if (side == TradeActionType.Buy)
                {
                    if (HasPendingBuy(code) || HasPendingSell(code))
                        return false;

                    long orderAmount = request.OrderAmount;
                    if (orderAmount <= 0)
                        return false;

                    string accountKey = GetAccountKey(request.AccountNo);

                    long reserved = GetReservedBuyAmount(accountKey);
                    long cash = Math.Max(0, request.CashSnapshot);
                    long minRemain = Math.Max(0, request.MinCashRemainSnapshot);
                    long available = Math.Max(0, cash - minRemain - reserved);

                    if (orderAmount > available)
                        return false;

                    AddReservedBuyAmount(accountKey, orderAmount);

                    string key = GetSideKey(code, side);

                    _locks[key] = new OrderLockState
                    {
                        AccountNo = accountKey,
                        Code = code,
                        Side = side,
                        Qty = request.Signal.Quantity,
                        UntradedQty = request.Signal.Quantity,
                        ReservedAmount = orderAmount,
                        State = OrderLifecycleState.Requested,
                        CreatedAt = DateTime.Now,
                        LastUpdated = DateTime.Now,
                        CancelRequested = false
                    };

                    return true;
                }

                if (side == TradeActionType.Sell)
                {
                    if (HasPendingSell(code))
                        return false;

                    string key = GetSideKey(code, side);

                    _locks[key] = new OrderLockState
                    {
                        AccountNo = GetAccountKey(request.AccountNo),
                        Code = code,
                        Side = side,
                        Qty = request.Signal.Quantity,
                        UntradedQty = request.Signal.Quantity,
                        ReservedAmount = 0,
                        State = OrderLifecycleState.Requested,
                        CreatedAt = DateTime.Now,
                        LastUpdated = DateTime.Now,
                        CancelRequested = false
                    };

                    return true;
                }

                return false;
            }
        }
        public void MarkSubmitted(OrderRequest request, int sendResult)
        {
            if (request == null || request.Signal == null)
                return;

            string key = GetSideKey(request.Signal.Code, request.Signal.Action);

            lock (_cashReserveLock)
            {
                OrderLockState state;
                if (!_locks.TryGetValue(key, out state))
                    return;

                state.State = sendResult == 0
                    ? OrderLifecycleState.Submitted
                    : OrderLifecycleState.Rejected;

                state.LastUpdated = DateTime.Now;

                if (sendResult != 0)
                {
                    ReleaseReservedBuyAmount(state);

                    OrderLockState removed;
                    _locks.TryRemove(key, out removed);
                }
            }
        }

        public void MarkCompleted(OrderRequest request)
        {
            if (request == null || request.Signal == null)
                return;

            string key = GetSideKey(request.Signal.Code, request.Signal.Action);

            lock (_cashReserveLock)
            {
                OrderLockState state;
                if (_locks.TryGetValue(key, out state))
                    ReleaseReservedBuyAmount(state);

                OrderLockState removed;
                _locks.TryRemove(key, out removed);
            }
        }

        public void MarkCancelRequested(OrderRequest request)
        {
            if (request == null || request.Signal == null)
                return;

            string key = GetSideKey(request.Signal.Code, request.Signal.Action);

            OrderLockState state;
            if (!_locks.TryGetValue(key, out state))
                return;

            state.CancelRequested = true;
            state.LastUpdated = DateTime.Now;
        }

        public bool TryGetCancelableOrder(OrderRequest request, out string orderNo, out int untradedQty)
        {
            orderNo = null;
            untradedQty = 0;

            if (request == null || request.Signal == null)
                return false;

            string key = GetSideKey(request.Signal.Code, request.Signal.Action);

            OrderLockState state;
            if (!_locks.TryGetValue(key, out state))
                return false;

            if (state.State != OrderLifecycleState.Submitted &&
                state.State != OrderLifecycleState.PartiallyFilled)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(state.OrderNo))
                return false;

            if (state.UntradedQty <= 0)
                return false;

            orderNo = state.OrderNo;
            untradedQty = state.UntradedQty;
            return true;
        }

        public void UpdateFromOrders(IEnumerable<OrderInfo> orders)
        {
            if (orders == null)
                return;

            lock (_cashReserveLock)
            {
                foreach (var order in orders)
                {
                    if (order == null || string.IsNullOrWhiteSpace(order.Code))
                        continue;

                    TradeActionType side = ParseSide(order.OrderType);

                    if (side == TradeActionType.Hold)
                        continue;

                    string key = GetSideKey(order.Code, side);
                    OrderLifecycleState parsed = ParseState(order);

                    OrderLockState state;
                    if (!_locks.TryGetValue(key, out state))
                    {
                        state = new OrderLockState
                        {
                            Code = order.Code,
                            Side = side,
                            CreatedAt = DateTime.Now
                        };

                        _locks[key] = state;
                    }

                    state.OrderNo = order.OrderNo;
                    state.Qty = order.Qty;
                    state.UntradedQty = order.UntradedQty;
                    state.State = parsed;
                    state.LastUpdated = DateTime.Now;

                    if (parsed == OrderLifecycleState.Cancelled ||
                        parsed == OrderLifecycleState.Rejected ||
                        order.UntradedQty <= 0)
                    {
                        ReleaseReservedBuyAmount(state);

                        OrderLockState removed;
                        _locks.TryRemove(key, out removed);
                    }
                }
            }
        }

        public bool HasPendingBuy(string code)
        {
            return HasPending(code, TradeActionType.Buy);
        }

        public bool HasPendingSell(string code)
        {
            return HasPending(code, TradeActionType.Sell);
        }

        public bool HasActiveOrder(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return false;

            return HasPendingBuy(code) || HasPendingSell(code);
        }

        public void Clear(string code = null)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                _locks.Clear();
                return;
            }

            foreach (var key in _locks.Keys.Where(x => x.StartsWith(code + ":", StringComparison.Ordinal)).ToList())
            {
                OrderLockState removed;
                _locks.TryRemove(key, out removed);
            }
        }

        private bool HasPending(string code, TradeActionType side)
        {
            if (string.IsNullOrWhiteSpace(code))
                return false;

            string key = GetSideKey(code, side);

            OrderLockState state;
            if (!_locks.TryGetValue(key, out state))
                return false;

            return state.State == OrderLifecycleState.Requested ||
                   state.State == OrderLifecycleState.Submitted ||
                   state.State == OrderLifecycleState.PartiallyFilled;
        }

        private static string GetSideKey(string code, TradeActionType side)
        {
            return (code ?? string.Empty) + ":" + side;
        }

        private static TradeActionType ParseSide(string orderType)
        {
            string text = orderType ?? string.Empty;

            if (text.Contains("매수") || text.Equals("BUY", StringComparison.OrdinalIgnoreCase))
                return TradeActionType.Buy;

            if (text.Contains("매도") || text.Equals("SELL", StringComparison.OrdinalIgnoreCase))
                return TradeActionType.Sell;

            return TradeActionType.Hold;
        }

        private static OrderLifecycleState ParseState(OrderInfo order)
        {
            if (order == null)
                return OrderLifecycleState.None;

            string status = order.Status ?? string.Empty;

            if (status.Contains("취소"))
                return OrderLifecycleState.Cancelled;

            if (status.Contains("거부"))
                return OrderLifecycleState.Rejected;

            if (order.UntradedQty == 0 && order.ExecQty > 0)
                return OrderLifecycleState.Filled;

            if (order.ExecQty > 0 && order.UntradedQty > 0)
                return OrderLifecycleState.PartiallyFilled;

            if (order.UntradedQty > 0)
                return OrderLifecycleState.Submitted;

            if (status.Contains("접수") || status.Contains("확인"))
                return OrderLifecycleState.Submitted;

            return OrderLifecycleState.None;
        }

        private static string GetAccountKey(string accountNo)
        {
            return string.IsNullOrWhiteSpace(accountNo)
                ? "DEFAULT"
                : accountNo.Trim();
        }

        private static long GetReservedBuyAmount(string accountKey)
        {
            long value;
            return _reservedBuyAmountByAccount.TryGetValue(accountKey, out value)
                ? Math.Max(0, value)
                : 0;
        }

        private static void AddReservedBuyAmount(string accountKey, long amount)
        {
            if (string.IsNullOrWhiteSpace(accountKey) || amount <= 0)
                return;

            _reservedBuyAmountByAccount.AddOrUpdate(
                accountKey,
                amount,
                (_, old) => Math.Max(0, old + amount));
        }

        private static void ReleaseReservedBuyAmount(OrderLockState state)
        {
            if (state == null || state.Side != TradeActionType.Buy)
                return;

            if (state.ReservedAmount <= 0)
                return;

            string accountKey = GetAccountKey(state.AccountNo);
            long amount = state.ReservedAmount;

            _reservedBuyAmountByAccount.AddOrUpdate(
                accountKey,
                0,
                (_, old) => Math.Max(0, old - amount));

            state.ReservedAmount = 0;
        }
    }
}