using AxKHOpenAPILib;
using MoneyCreator.DataBase;
using MoneyCreator.Models;
using System;
using System.Collections.Generic;

namespace MoneyCreator.Service
{
    public interface IMarketApiService
    {
        event Action<bool> OnLogin;
        event Action<string, string> OnLog;
        event Action<string, string, string> OnSymbolDetected;
        event Action<string, Symbol> OnSymbolDetailUpdated;
        event Action<string, string, string> OnAiSymbolDetected;
        event Action<string, string, double, int, DateTime> OnRealPriceReceived;
        event Action<string, List<TrackedSymbol>, bool> OnInitialHistoryReceived;
        event Action<string, TrackedSymbol> OnTickReceived;
        event Action<string, HogaData> OnHogaReceived;
        event Action<string, long> OnBalanceUpdated;
        event Action<string, List<HoldingInfo>> OnHoldingsReceived;
        // code, name, absoluteQty, averagePrice
        // Kiwoom balance Chejan(sGubun=1) based immediate position state.
        event Action<string, string, int, double> OnPositionBalanceChanged;
        event Action<string, List<OrderInfo>> OnOrdersReceived;
        event Action<string, List<OrderInfo>> OnUnfilledOrdersReceived;
        event Action<string> OnGlobalUpdateRequired;
        event Action<string, string, string, string, int, double> OnOrderFilled;
        // windowId, code, name, orderType, filledQty, filledPrice
        event Action<TrackedSymbol> OnInventoryTickReceived;
        event Action<string, string, List<MinuteCandleRecord>, bool> OnMinuteHistoryReceived;
        event Action<string, string, List<TrackedSymbol>, bool> OnRecentTicksReceived;
        event Action<MarketIndexInfo> OnMarketIndexReceived;

        StockDbManager DbManager { get; }

        string GetScreenNo();
        bool Login();
        bool IsConnected();

        void SetApiInstance(AxKHOpenAPI api);

        void ReplaceSubscription(string windowId, string screenNo, string newCode);
        void RegisterInventoryTracking(string windowId, string code, string screenNo);
        void UnregisterWindow(string windowId);

        void RequestMinuteCandles(string windowId, string code, string screenNo, int daysToLoad = 2, int maxRows = 0, int priority = 1, int next = 0);
        void RequestRecentTicks(string windowId, string code, string screenNo, int minutes = 10, int priority = 1);
        void RequestAccountHoldings(string windowId, string accountNo, string screenNo, int priority = 1);
        void RequestCashInfo(string windowId, string accountNo, string screenNo = "1000");
        void RequestOrderHistory(string windowId, string accountNo, string screenNo, int next = 0);
        void RequestUnfilledOrders(string windowId, string accountNo, string screenNo);
        void RegisterMarketIndexRealtime(string windowId);

        int SendOrder(string windowId, string screenNo, string accountNo, int orderType, string code, int qty, int price, string hogaType, string orgOrderNo = "");
        

        List<Symbol> GetTotalSymbolList();
    }
}