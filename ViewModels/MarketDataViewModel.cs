using System.ComponentModel;
using MoneyCreator.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;

using AxKHOpenAPILib;
using MoneyCreator.Biz;
using MoneyCreator.Service;
using MoneyCreator.View;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using System.Windows.Media;
using MoneyCreator.AutoTrading;
using MoneyCreator.DataBase;
using HarfBuzzSharp;

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

namespace MoneyCreator.ViewModels
{
    public partial class MainWindowViewModel
    {
        // Source: MainWindowViewModel.MarketData.cs
        // ============================================================
        private readonly Queue<TrackedSymbol> _recentRealtimeTicks = new Queue<TrackedSymbol>();
        private readonly object _recentRealtimeTickLock = new object();

        private const int RawRealtimeTickRetentionSeconds = 300;
        private static readonly TimeSpan RegularRealtimeTickStartTime = new TimeSpan(9, 0, 0);
        private static readonly TimeSpan RegularRealtimeTickEndTime = new TimeSpan(15, 20, 0);

        private static bool IsRegularRealtimeTickForView(TrackedSymbol tick)
        {
            if (tick == null || tick.LastUpdated == default(DateTime))
                return false;

            TimeSpan tod = tick.LastUpdated.TimeOfDay;
            return tod >= RegularRealtimeTickStartTime && tod < RegularRealtimeTickEndTime;
        }

        private static bool IsRegularRealtimeTickTimeForView(DateTime time)
        {
            if (time == default(DateTime))
                return false;

            TimeSpan tod = time.TimeOfDay;
            return tod >= RegularRealtimeTickStartTime && tod < RegularRealtimeTickEndTime;
        }

        private readonly List<MinuteCandleRecord> _minuteHistoryBuffer = new List<MinuteCandleRecord>();
        private readonly List<TrackedSymbol> _minuteTickBuffer = new List<TrackedSymbol>();

        private bool _isDailyResetDone = false;
        private bool _isNewTradingDayReloading = false;

        private DateTime _currentMinute = DateTime.MinValue;
        private DateTime _lastChartMinute = DateTime.MinValue;

        private readonly ConcurrentDictionary<string, int> _requestTokens = new ConcurrentDictionary<string, int>();
        private readonly object _syncLock = new object();


        private bool IsCurrentWindowTick(string windowId, TrackedSymbol tick)
        {
            if (tick == null)
                return false;

            if (windowId != this.WindowId)
                return false;

            return Context != null && tick.Code == Context.Code;
        }

        private void UpdateCurrentSymbolFromTick(TrackedSymbol tick, bool isLoading)
        {
            if (tick == null || Context == null)
                return;

            Context.LastPrice = tick.LastPrice;
            Context.Strength = tick.Strength;
            IsLoading = isLoading;
        }

        private void UpdateAccountHoldingPrice(string code, double price)
        {
            if (AccountHoldings == null || string.IsNullOrWhiteSpace(code))
                return;

            var holding = AccountHoldings.FirstOrDefault(h => h != null && h.Code == code);
            if (holding != null)
                holding.CurrentPrice = price;
        }

        private void BufferRealtimeTick(TrackedSymbol tick)
        {
            if (tick == null)
                return;

            lock (_realTimeBuffer)
            {
                _realTimeBuffer.Enqueue(tick);
            }
        }

        private TrackedSymbol GetLatestCurrentMinuteForChart_NoLock()
        {
            if (_minuteHistoryBuffer.Count == 0)
                return null;

            var latestMinute = ToTrackedSymbol(_minuteHistoryBuffer[_minuteHistoryBuffer.Count - 1]);
            if (latestMinute == null)
                return null;

            DateTime latestMinuteTime = ToMinuteTime(latestMinute.LastUpdated);
            _lastChartMinute = latestMinuteTime;
            _currentMinute = latestMinuteTime;
            latestMinute.LastUpdated = latestMinuteTime;

            return latestMinute;
        }

        private TrackedSymbol GetCurrentMinuteForChartByTime_NoLock(DateTime minuteTime)
        {
            if (_minuteHistoryBuffer.Count == 0 || minuteTime == DateTime.MinValue)
                return null;

            for (int i = _minuteHistoryBuffer.Count - 1; i >= 0; i--)
            {
                var candle = _minuteHistoryBuffer[i];
                if (candle == null)
                    continue;

                if (ToMinuteTime(candle.CandleTime) != minuteTime)
                    continue;

                var minute = ToTrackedSymbol(candle);
                if (minute == null)
                    return null;

                _lastChartMinute = minuteTime;
                _currentMinute = minuteTime;
                minute.LastUpdated = minuteTime;

                return minute;
            }

            return null;
        }

        private List<TrackedSymbol> GetOrderedValidTicks(List<TrackedSymbol> ticks, string code)
        {
            if (ticks == null || ticks.Count == 0)
                return new List<TrackedSymbol>();

            var result = new List<TrackedSymbol>(ticks.Count);
            foreach (var tick in ticks)
            {
                if (tick != null && tick.Code == code)
                    result.Add(tick);
            }

            return result;
        }

        private DateTime GetLatestTickTimeOrNow(List<TrackedSymbol> ticks)
        {
            if (ticks != null && ticks.Count > 0)
                return ticks[ticks.Count - 1].LastUpdated;

            return DateTime.Now;
        }

        // ============================================================
        // Source: MainWindowViewModel.MarketData.Events.cs
        // ============================================================
        private void Kiwoom_OnRealPriceReceived(string code, string windowId, double currentPrice, int volume, DateTime LastUpdated)
        {
            if (string.IsNullOrWhiteSpace(code))
                return;

            if (IsReplayMode && !IsAutoTradingEnabled)
                return;

            if (!IsReplayMode && !IsRegularRealtimeTickTimeForView(LastUpdated))
                return;

            Application.Current.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (IsReplayMode && !IsAutoTradingEnabled)
                        return;

                    var holding = AccountHoldings.FirstOrDefault(h =>
                        h != null && h.Code == code);

                    if (holding != null)
                    {
                        int index = AccountHoldings.IndexOf(holding);

                        holding.CurrentPrice = currentPrice;

                        if (index >= 0)
                            AccountHoldings[index] = holding;

                        if (Context.CurrentHold != null && Context.CurrentHold.Code == code)
                            Context.CurrentHold = holding;
                    }

                    if (Context != null && Context.Code == code)
                        Context.LastPrice = currentPrice;
                }),
                DispatcherPriority.Background);
        }


        private void Kiwoom_OnHogaReceived(string wId, HogaData data)
        {
            if (data == null)
                return;

            if (IsReplayMode && !IsAutoTradingEnabled)
                return;

            if (Context == null || string.IsNullOrWhiteSpace(Context.Code))
                return;

            if (data.Code != Context.Code)
                return;

            OnHogaReceived(wId, data);
        }


        private void OnInitialHistoryReceived_Handler(List<TrackedSymbol> history, bool islast)
        {
            IsLoading = true;

            if (history == null || history.Count == 0)
            {
                _isHistoryBindingComplete = true;
                return;
            }

            string key = $"{WindowId}_{Context.Code}";
            int currentToken = _requestTokens.AddOrUpdate(key, 1, (_, old) => old + 1);

            lock (_syncLock)
            {
                foreach (var tick in history)
                {
                    Context.SymbolList.Add(tick);
                }
            }

            Task.Run(async () =>
            {
                if (!_requestTokens.TryGetValue(key, out var latest) || latest != currentToken)
                    return;

                if (!islast)
                    return;

                await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        if (Context.SymbolList.Count > 0)
                        {
                            Context.LastPrice = Context.SymbolList.Last().LastPrice;
                            Price = OrderPrice = Context.LastPrice;
                        }

                        // 로딩 중 수신된 실시간 틱 병합 (원본틱 유지)
                        lock (_realTimeBuffer)
                        {
                            while (_realTimeBuffer.Count > 0)
                            {
                                var bTick = _realTimeBuffer.Dequeue();
                                AddTickToBuffer(bTick);
                            }
                        }

                        lock (_syncLock)
                        {
                            Context.SymbolList.Sort((x, y) => x.LastUpdated.CompareTo(y.LastUpdated));
                        }

                        List<TrackedSymbol> chartData;
                        lock (_syncLock)
                        {
                            chartData = AggregateToMinuteBars(Context.SymbolList);

                            if (chartData.Count > 0)
                                _lastChartMinute = chartData.Last().LastUpdated;
                        }

                        // 실시간 분봉 시작 기준 동기화
                        _minuteTickBuffer.Clear();
                        _currentMinute = _lastChartMinute;

                        // 차트는 분봉 데이터로 렌더
                        RequestChartRefresh?.Invoke(CreateChartPayload(chartData, isFullRefresh: true));

                        _isHistoryBindingComplete = true;

                        AddLog($"✅ 최종 차트 바인딩 완료: 원본틱 {Context.SymbolList.Count}개 / 차트 {chartData.Count}개");
                    }
                    finally
                    {
                        IsLoading = false;
                    }
                });
            });
        }

        // ============================================================
        // Source: MainWindowViewModel.MarketData.Holdings.cs
        // ============================================================
        private void AddRecentTickToHoldingStore_NoLock(HoldingSymbolContext store, TrackedSymbol tick)
        {
            if (store == null || tick == null)
                return;

            store.RecentTickList.Add(CloneTrackedSymbolForAutoTrade(tick));

            store.IsRecentTickLoaded = true;
        }


        private void TrimHoldingRecentTicks_NoLock(HoldingSymbolContext store)
        {
            if (store == null)
                return;

            DateTime now = store.LastTickTime;

            if (now == default(DateTime))
            {
                if (store.RecentTickList.Count > 0)
                    now = store.RecentTickList[store.RecentTickList.Count - 1].LastUpdated;
                else
                    return;
            }

            DateTime rawCutoff = now.AddSeconds(-RawRealtimeTickRetentionSeconds);

            store.RecentTickList.RemoveAll(x => x == null || x.LastUpdated < rawCutoff);
        }


        private void RecalculateHoldingLoadState(HoldingSymbolContext store)
        {
            if (store == null)
                return;

            // 보유주 리스트의 "로딩중"은 분봉 로딩만 기준으로 종료
            // 최근틱은 없을 수 있으므로 필수 조건에서 제외
            bool loaded = store.IsMinuteLoaded || store.MinuteList.Count > 0;

            store.IsHistoryLoaded = loaded;
            store.IsLoadingHistory = !loaded;

            if (loaded)
                store.HistoryLoadedSource.TrySetResult(true);
        }


        private void RequestHoldingHistoryAndRealtime(HoldingInfo h)
        {
            if (h == null || string.IsNullOrWhiteSpace(h.Code))
                return;

            var store = GlobalHoldingStore.GetOrCreate(h.Code, h.Name);

            bool isMarketOpen = IsMarketOpenForRealtime();

            bool requestMinute = false;
            bool requestRecentTick = false;
            bool registerRealtime = false;

            lock (store.SyncRoot)
            {
                if (store.MinuteList.Count > 0 || store.IsMinuteLoaded)
                {
                    store.IsMinuteLoaded = true;
                    store.IsMinuteHistoryLoaded = true;
                    store.IsHistoryLoaded = true;
                    store.IsLoadingHistory = false;

                    h.IsHistoryLoaded = true;
                    h.IsHistoryLoading = false;
                }
                else if (!store.IsLoadingHistory)
                {
                    store.IsLoadingHistory = true;
                    store.IsHistoryLoaded = false;
                    store.IsMinuteLoaded = false;
                    store.IsMinuteHistoryLoaded = false;
                    store.HistoryLoadedSource = new TaskCompletionSource<bool>();

                    h.IsHistoryLoaded = false;
                    h.IsHistoryLoading = true;

                    requestMinute = true;
                }
                else
                {
                    h.IsHistoryLoaded = store.IsHistoryLoaded;
                    h.IsHistoryLoading = true;
                }

                if (isMarketOpen)
                {
                    registerRealtime = true;

                    if (store.RecentTickList.Count == 0 && !store.IsRecentTickLoaded)
                        requestRecentTick = true;
                }
                else
                {
                    // 장외에는 최근틱이 없어도 로딩중으로 두면 안 됨
                    store.IsRecentTickLoaded = true;
                }
            }

            if (registerRealtime)
            {
                _marketApi.RegisterInventoryTracking(
                    "COMMON",
                    h.Code,
                    "9999");
            }

            if (requestMinute)
            {
                _marketApi.RequestMinuteCandles(
                    $"INVMIN:{h.Code}",
                    h.Code,
                    _marketApi.GetScreenNo(),
                    daysToLoad: 2,
                    maxRows: 0,
                    priority: 0);
            }

            // 보유주 최근 5분 과거틱도 분봉 요청과 동시에 넣지 않고 지연시켜 낮은 우선순위로 보강한다.
            if (requestRecentTick)
            {
                Task.Run(async () =>
                {
                    await Task.Delay(300);
                    _marketApi.RequestRecentTicks(
                        $"INVTICK:{h.Code}",
                        h.Code,
                        _marketApi.GetScreenNo(),
                        minutes: 5,
                        priority: 2);
                });
            }

            // 실제로 신규 요청을 넣은 경우에만 로그를 남긴다.
            // 잔고 갱신/창 전환으로 이 메서드가 반복 호출되어도 로그가 무한히 쌓이지 않게 한다.
            if (requestMinute || requestRecentTick)
            {
                AddLog(isMarketOpen
                    ? $"📦 보유주 분봉/최근틱 요청: {h.Name}({h.Code})"
                    : $"📦 보유주 분봉만 요청(장외): {h.Name}({h.Code})");
            }
        }


        private void Kiwoom_OnInventoryTickReceived(TrackedSymbol tick)
        {
            if (tick == null || string.IsNullOrWhiteSpace(tick.Code))
                return;

            if (IsReplayMode && !IsAutoTradingEnabled)
                return;

            if (!IsReplayMode && !IsRegularRealtimeTickForView(tick))
                return;

            var store = GlobalHoldingStore.GetOrCreate(tick.Code);

            ResetHoldingChartForNewTradingDayIfNeeded(store, tick);

            DateTime tickMinute = ToMinuteTime(tick.LastUpdated);

            DateTime savedMaxMinute;

            lock (store.SyncRoot)
            {
                savedMaxMinute = GetMaxMinuteTimeFromHoldingStore(store);
            }

            bool shouldRefreshChart = savedMaxMinute < tickMinute;

            TrackedSymbol latestMinute = null;

            lock (store.SyncRoot)
            {
                AddRecentTickToHoldingStore_NoLock(store, tick);
                TrimHoldingRecentTicks_NoLock(store);

                UpsertHoldingMinuteCandle_NoSort(store, tick);

                store.LastTickTime = tick.LastUpdated;

                if (shouldRefreshChart &&
                    Context != null &&
                    Context.Code == tick.Code &&
                    store.MinuteList.Count > 0)
                {
                    latestMinute = store.MinuteList[store.MinuteList.Count - 1];

                    if (latestMinute != null)
                    {
                        DateTime latestMinuteTime = ToMinuteTime(latestMinute.LastUpdated);

                        _lastChartMinute = latestMinuteTime;
                        _currentMinute = latestMinuteTime;
                        latestMinute.LastUpdated = latestMinuteTime;
                    }
                }
            }

            var holding = AccountHoldings?.FirstOrDefault(h =>
                h != null &&
                h.Code == tick.Code);

            if (holding != null)
            {
                holding.CurrentPrice = tick.LastPrice;
                holding.IsHistoryLoading = store.IsLoadingHistory;
                holding.IsHistoryLoaded = store.IsHistoryLoaded;
            }

            if (Context != null && Context.Code == tick.Code)
            {
                UpdateCurrentSymbolFromTick(tick, isLoading: false);

                if (shouldRefreshChart && latestMinute != null && _isHistoryBindingComplete)
                {
                    RequestRealtimeMinuteChartRefresh(latestMinute);
                    SyncAutoTradeMinuteCandles(tick.Code, "보유주 실시간 분봉 갱신");
                }

                TryEnqueueAutoTradeTick(tick);
            }
        }


        private void ClearHoldingStoreForReload(HoldingInfo holding)
        {
            if (holding == null || string.IsNullOrWhiteSpace(holding.Code))
                return;

            var store = GlobalHoldingStore.GetOrCreate(holding.Code, holding.Name);

            lock (store.SyncRoot)
            {
                store.SymbolList.Clear();
                store.MinuteList.Clear();
                store.RecentTickList.Clear();
                store.TickFrameList.Clear();
                store.PendingTicks.Clear();

                store.LastTickTime = DateTime.MinValue;

                store.IsHistoryLoaded = false;
                store.IsLoadingHistory = false;
                store.IsMinuteLoaded = false;
                store.IsRecentTickLoaded = false;
                store.IsMinuteHistoryLoaded = false;
                store.HistoryLoadedSource = new TaskCompletionSource<bool>();
            }

            holding.IsHistoryLoaded = false;
            holding.IsHistoryLoading = false;
        }

        // ============================================================
        // Source: MainWindowViewModel.MarketData.Minutes.cs
        // ============================================================
        private List<TrackedSymbol> GetChartMinuteData(List<TrackedSymbol> source)
        {
            if (source == null || source.Count == 0)
                return new List<TrackedSymbol>();

            // source는 과거분봉 완료 시점에 1회 정렬되고,
            // 실시간 분봉은 Upsert에서 시간 위치를 보정해서 유지한다.
            // 여기서는 매번 OrderBy 하지 않는다.
            DateTime lastDate = source[source.Count - 1].LastUpdated.Date;

            return source
                .Where(x => x != null && x.LastUpdated.Date == lastDate)
                .ToList();
        }


        private void UpsertCurrentMinuteCandle_NoSort(TrackedSymbol tick)
        {
            var incoming = ToRealtimeMinuteCandle(tick);
            if (incoming == null)
                return;

            lock (_syncLock)
            {
                UpsertMinuteCandle_NoSort(_minuteHistoryBuffer, incoming, preferRealtime: true);
            }
        }


        private void UpsertHoldingMinuteCandle_NoSort(HoldingSymbolContext store, TrackedSymbol tick)
        {
            if (store == null || tick == null)
                return;

            var incoming = ToRealtimeMinuteSymbol(tick);
            UpsertTrackedMinute_NoSort(store.MinuteList, incoming, preferRealtime: true);
        }


        private void UpsertMinuteCandle_NoSort(List<MinuteCandleRecord> list, MinuteCandleRecord incoming, bool preferRealtime)
        {
            if (list == null || incoming == null)
                return;

            // 일반적인 실시간 흐름: 마지막 분봉과 같은 시간이면 병합, 이후 시간이면 바로 추가.
            // 이 경로가 대부분이므로 정렬은 하지 않는다.
            if (list.Count == 0)
            {
                list.Add(incoming);
                return;
            }

            var last = list[list.Count - 1];

            if (last.Code == incoming.Code &&
                last.Interval == incoming.Interval &&
                last.CandleTime == incoming.CandleTime)
            {
                MergeMinuteCandle(last, incoming, preferRealtime);
                return;
            }

            if (incoming.CandleTime > last.CandleTime)
            {
                list.Add(incoming);
                return;
            }

            // 과거분봉/버퍼 병합 중 앞뒤가 섞여 들어오는 경우만 위치 보정.
            // 매번 전체 정렬하지 않고, 뒤에서 앞으로 필요한 위치만 찾는다.
            for (int i = list.Count - 2; i >= 0; i--)
            {
                var old = list[i];

                if (old.Code == incoming.Code &&
                    old.Interval == incoming.Interval &&
                    old.CandleTime == incoming.CandleTime)
                {
                    MergeMinuteCandle(old, incoming, preferRealtime);
                    return;
                }

                if (old.CandleTime < incoming.CandleTime)
                {
                    list.Insert(i + 1, incoming);
                    return;
                }
            }

            list.Insert(0, incoming);
        }


        private void MergeMinuteCandle(MinuteCandleRecord old, MinuteCandleRecord incoming, bool preferRealtime)
        {
            if (old == null || incoming == null)
                return;

            bool oldIsReal = IsRealMinuteSource(old.Source);
            bool incomingIsReal = IsRealMinuteSource(incoming.Source);

            // Open은 기존 분봉의 시작가 유지. 기존값이 없을 때만 보정.
            if (old.Open <= 0)
                old.Open = incoming.Open;

            old.High = Math.Max(old.High, incoming.High);
            old.Low = old.Low <= 0 ? incoming.Low : Math.Min(old.Low, incoming.Low);

            if (preferRealtime && oldIsReal && !incomingIsReal)
            {
                // 이미 실시간 분봉이 있는 상태에서 늦게 도착한 HIST는 종가를 덮지 않음.
            }
            else
            {
                old.Close = incoming.Close;
            }

            // 같은 분의 HIST와 REAL을 단순 합산하면 중복 거래량이 될 수 있어 큰 값을 유지.
            // REAL끼리 들어오는 경우는 incoming 자체가 단일 틱 기반이므로 기존 로직처럼 누적.
            if (oldIsReal && incomingIsReal)
                old.Volume += incoming.Volume;
            else
                old.Volume = Math.Max(old.Volume, incoming.Volume);

            old.AccumulatedVolume = Math.Max(old.AccumulatedVolume, incoming.AccumulatedVolume);
            old.Strength = incoming.Strength > 0 ? incoming.Strength : old.Strength;
            old.TotalAskQty = incoming.TotalAskQty > 0 ? incoming.TotalAskQty : old.TotalAskQty;
            old.TotalBidQty = incoming.TotalBidQty > 0 ? incoming.TotalBidQty : old.TotalBidQty;
            old.Source = MergeMinuteSource(old.Source, incoming.Source);
            old.CreatedAt = DateTime.Now;
        }


        private bool IsRealMinuteSource(string source)
        {
            return source == "REAL" || source == "REAL_MIN" || source == "HIST+REAL";
        }


        private string MergeMinuteSource(string oldSource, string incomingSource)
        {
            bool hasHist = oldSource == "HIST" || incomingSource == "HIST" || oldSource == "HIST+REAL" || incomingSource == "HIST+REAL";
            bool hasReal = IsRealMinuteSource(oldSource) || IsRealMinuteSource(incomingSource);

            if (hasHist && hasReal)
                return "HIST+REAL";

            if (hasReal)
                return "REAL";

            return "HIST";
        }


        private TrackedSymbol ToRealtimeMinuteSymbol(TrackedSymbol tick)
        {
            if (tick == null)
                return null;

            DateTime minute = ToMinuteTime(tick.LastUpdated);

            return new TrackedSymbol
            {
                Code = tick.Code,
                LastUpdated = minute,
                LastPrice = tick.LastPrice,
                ClosePrice = tick.LastPrice,
                HighPrice = tick.LastPrice,
                LowPrice = tick.LastPrice,
                LastVolume = Math.Abs(tick.LastVolume),
                AccumulatedVolume = tick.AccumulatedVolume,
                Strength = tick.Strength,
                TotalAskQty = tick.TotalAskQty,
                TotalBidQty = tick.TotalBidQty,
                Side = "REAL_MIN"
            };
        }


        private void UpsertTrackedMinute_NoSort(List<TrackedSymbol> list, TrackedSymbol incoming, bool preferRealtime)
        {
            if (list == null || incoming == null)
                return;

            if (list.Count == 0)
            {
                list.Add(incoming);
                return;
            }

            var last = list[list.Count - 1];

            if (last.Code == incoming.Code && last.LastUpdated == incoming.LastUpdated)
            {
                MergeTrackedMinute(last, incoming, preferRealtime);
                return;
            }

            if (incoming.LastUpdated > last.LastUpdated)
            {
                list.Add(incoming);
                return;
            }

            // 보유주 과거분봉/실시간분봉이 섞여 들어올 때만 위치 보정.
            for (int i = list.Count - 2; i >= 0; i--)
            {
                var old = list[i];

                if (old.Code == incoming.Code && old.LastUpdated == incoming.LastUpdated)
                {
                    MergeTrackedMinute(old, incoming, preferRealtime);
                    return;
                }

                if (old.LastUpdated < incoming.LastUpdated)
                {
                    list.Insert(i + 1, incoming);
                    return;
                }
            }

            list.Insert(0, incoming);
        }


        private void MergeTrackedMinute(TrackedSymbol old, TrackedSymbol incoming, bool preferRealtime)
        {
            if (old == null || incoming == null)
                return;

            bool oldIsReal = old.Side == "REAL_MIN" || old.Side == "HIST+REAL";
            bool incomingIsReal = incoming.Side == "REAL_MIN" || incoming.Side == "HIST+REAL";

            old.HighPrice = Math.Max(old.HighPrice, incoming.HighPrice);
            old.LowPrice = old.LowPrice <= 0 ? incoming.LowPrice : Math.Min(old.LowPrice, incoming.LowPrice);

            if (preferRealtime && oldIsReal && !incomingIsReal)
            {
                // 실시간 종가 유지
            }
            else
            {
                old.LastPrice = incoming.LastPrice;
                old.ClosePrice = incoming.ClosePrice;
            }

            if (oldIsReal && incomingIsReal)
                old.LastVolume += incoming.LastVolume;
            else
                old.LastVolume = Math.Max(old.LastVolume, incoming.LastVolume);

            old.AccumulatedVolume = Math.Max(old.AccumulatedVolume, incoming.AccumulatedVolume);
            old.Strength = incoming.Strength > 0 ? incoming.Strength : old.Strength;
            old.TotalAskQty = incoming.TotalAskQty > 0 ? incoming.TotalAskQty : old.TotalAskQty;
            old.TotalBidQty = incoming.TotalBidQty > 0 ? incoming.TotalBidQty : old.TotalBidQty;

            if ((old.Side == "M" || old.Side == "HIST") && incomingIsReal)
                old.Side = "HIST+REAL";
            else if (oldIsReal && (incoming.Side == "M" || incoming.Side == "HIST"))
                old.Side = "HIST+REAL";
            else
                old.Side = incoming.Side;
        }


        private MinuteCandleRecord ToRealtimeMinuteCandle(TrackedSymbol tick)
        {
            if (tick == null)
                return null;

            DateTime minute = ToMinuteTime(tick.LastUpdated);

            return new MinuteCandleRecord
            {
                Code = tick.Code,
                Name = Context != null && Context.Code == tick.Code ? Context.Name : "",
                CandleTime = minute,
                Interval = "1m",
                Open = tick.LastPrice,
                High = tick.LastPrice,
                Low = tick.LastPrice,
                Close = tick.LastPrice,
                Volume = (long)tick.LastVolume,
                AccumulatedVolume = tick.AccumulatedVolume,
                Strength = tick.Strength,
                TotalAskQty = tick.TotalAskQty,
                TotalBidQty = tick.TotalBidQty,
                Source = "REAL",
                CreatedAt = DateTime.Now
            };
        }


        private void RequestRealtimeMinuteChartRefresh(TrackedSymbol minute)
        {
            if (minute == null)
                return;

            // UI 스레드 비동기 Invoke를 사용 중이거나 Chart 컴포넌트가 이벤트를 구독 중일 때
            // 리스트 객체가 레이스 컨디션에 빠지지 않도록 새로 생성해서 던짐
            var candleList = new List<TrackedSymbol> { minute };
            var markers = GetCurrentTradeMarkers() ?? new List<TradeMarkerInfo>();

            RequestChartRefresh?.Invoke(new ChartPayload
            {
                Candles = candleList,
                Markers = markers,
                IsFullRefresh = false // 새로운 분봉 1개만 추가/복사하는 경량 업데이트 지시
            });
        }



        private void Kiwoom_OnMinuteHistoryReceived(string windowId, string code, List<MinuteCandleRecord> candles, bool isLast)
        {
            bool isHoldingMinuteRequest =
                !string.IsNullOrWhiteSpace(windowId) &&
                windowId.StartsWith("INV", StringComparison.OrdinalIgnoreCase);

            if (isHoldingMinuteRequest)
            {
                string holdingCode = code;

                if (string.IsNullOrWhiteSpace(holdingCode) && windowId.Contains(":"))
                    holdingCode = windowId.Substring(windowId.IndexOf(":") + 1);

                if (!string.IsNullOrWhiteSpace(holdingCode))
                    HandleHoldingMinuteHistory(windowId, holdingCode, candles, isLast);

                return;
            }

            if (windowId != this.WindowId || code != Context.Code)
                return;

            if (candles != null && candles.Count > 0)
            {
                candles.Sort((a, b) => a.CandleTime.CompareTo(b.CandleTime));

                if (!IsReplayMode)
                    Db.EnqueueMinuteCandles(candles);

                lock (_syncLock)
                {
                    foreach (var candle in candles)
                        UpsertMinuteCandle_NoSort(_minuteHistoryBuffer, candle, preferRealtime: true);
                }
            }

            if (!isLast)
                return;

            // Replay는 과거 분봉을 분석 버퍼로만 보관한다.
            // 차트는 Replay Tick의 분이 바뀌는 시점에 1분봉씩 바인딩한다.
            if (!IsReplayMode)
            {
                // 분봉 히스토리 재요청 중 들어온 실시간틱을 오늘 REAL 분봉으로 병합한다.
                // 장 시작 전 전일 차트를 비운 뒤 오늘 분봉이 도착하기 전의 틱도 누락하지 않기 위함.
                lock (_realTimeBuffer)
                {
                    while (_realTimeBuffer.Count > 0)
                    {
                        var bufferedTick = _realTimeBuffer.Dequeue();
                        var realMinute = ToRealtimeMinuteCandle(bufferedTick);
                        if (realMinute != null)
                        {
                            lock (_syncLock)
                            {
                                UpsertMinuteCandle_NoSort(_minuteHistoryBuffer, realMinute, preferRealtime: true);
                            }
                        }
                    }
                }
            }

            List<TrackedSymbol> analysisData;
            List<TrackedSymbol> chartData;

            lock (_syncLock)
            {
                // 과거분봉 완료 시점에만 1회 정렬한다.
                _minuteHistoryBuffer.Sort((a, b) => a.CandleTime.CompareTo(b.CandleTime));

                analysisData = _minuteHistoryBuffer
                    .Select(ToTrackedSymbol)
                    .ToList();

                Context.SymbolList.Clear();
                Context.SymbolList.AddRange(analysisData);

                chartData = GetChartMinuteData(analysisData);

                if (!IsReplayMode && chartData.Count > 0)
                {
                    var last = chartData[chartData.Count - 1];

                    _lastChartMinute = last.LastUpdated;
                    _currentMinute = _lastChartMinute;

                    Context.LastPrice = last.LastPrice;
                    Price = OrderPrice = last.LastPrice;
                }
            }

            _minuteTickBuffer.Clear();
            _isHistoryBindingComplete = true;
            IsLoading = false;

            if (IsReplayMode)
            {
                _lastChartMinute = DateTime.MinValue;
                _currentMinute = DateTime.MinValue;

                SyncAutoTradeMinuteCandles(code, "Replay 과거분봉 분석버퍼 준비");

                AddLog($"🧪 Replay 분봉 분석버퍼 준비 완료: {code} / 분석 {analysisData.Count}개 / 차트는 Tick 분 변경 시 바인딩");
                return;
            }

            RequestChartRefresh?.Invoke(
                CreateChartPayload(chartData, isFullRefresh: true));

            SyncAutoTradeMinuteCandles(code, "일반 과거분봉 완료");

            Task.Run(async () =>
            {
                await Task.Delay(300);

                if (Context.Code == code)
                    _marketApi.RequestRecentTicks(this.WindowId, code, ScreenNo, minutes: 5, priority: 2);
            });

            AddLog($"✅ 분봉 차트 바인딩 완료: {code} / 차트 {chartData.Count}개 / 분석 {analysisData.Count}개");
        }



        private void HandleHoldingMinuteHistory(string windowId, string code, List<MinuteCandleRecord> candles, bool isLast)
        {
            if (string.IsNullOrWhiteSpace(code))
                code = windowId.Replace("INVMIN:", "");

            var store = GlobalHoldingStore.GetOrCreate(code);

            if (candles != null && candles.Count > 0)
            {
                Db.EnqueueMinuteCandles(candles);

                lock (store.SyncRoot)
                {
                    foreach (var candle in candles)
                    {
                        var item = ToTrackedSymbol(candle);
                        UpsertTrackedMinute_NoSort(store.MinuteList, item, preferRealtime: true);
                    }
                }
            }

            if (!isLast)
                return;

            bool firstMinuteHistoryComplete;
            bool isStoreHistoryLoaded;
            int minuteCount;

            lock (store.SyncRoot)
            {
                // 보유주 과거분봉 완료 시점에만 1회 정렬한다.
                store.MinuteList.Sort((a, b) => a.LastUpdated.CompareTo(b.LastUpdated));

                // 이미 완료된 보유주 분봉 요청의 중복 마지막 응답이면
                // 차트 재바인딩/완료 로그를 반복하지 않는다.
                firstMinuteHistoryComplete = !store.IsMinuteHistoryLoaded;

                store.IsMinuteLoaded = store.MinuteList.Count > 0;
                store.IsMinuteHistoryLoaded = store.IsMinuteLoaded;
                RecalculateHoldingLoadState(store);

                if (store.IsHistoryLoaded)
                    store.HistoryLoadedSource.TrySetResult(true);

                if (store.MinuteList.Count > 0)
                    store.LastTickTime = store.MinuteList[store.MinuteList.Count - 1].LastUpdated;

                isStoreHistoryLoaded = store.IsHistoryLoaded;
                minuteCount = store.MinuteList.Count;
            }

            if (!firstMinuteHistoryComplete)
                return;

            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.DataBind,
                new Action(() =>
                {
                    var holding = AccountHoldings.FirstOrDefault(x => x.Code == code);

                    if (holding != null)
                    {
                        holding.IsHistoryLoading = false;
                        holding.IsHistoryLoaded = isStoreHistoryLoaded;
                    }

                    if (Context.Code == code && minuteCount > 0)
                    {
                        var symbol = new Symbol
                        {
                            Code = code,
                            Name = holding?.Name ?? code,
                            LastPrice = holding?.CurrentPrice.ToString() ?? ""
                        };

                        BindHoldingStoreToChart(store, symbol);
                    }
                }));

            if (Context != null && Context.Code == code && minuteCount > 0)
            {
                MarkAutoTradeDataReady(code);
                SyncAutoTradeMinuteCandles(code, "보유주 과거분봉 완료");
            }

            AddLog($"✅ 보유주 분봉 저장 완료: {code} / {minuteCount}개");
        }


        private TrackedSymbol ToTrackedSymbol(MinuteCandleRecord c)
        {
            return new TrackedSymbol
            {
                Code = c.Code,
                LastUpdated = c.CandleTime,
                LastPrice = c.Close,
                ClosePrice = c.Close,
                HighPrice = c.High,
                LowPrice = c.Low,
                LastVolume = c.Volume,
                AccumulatedVolume = c.AccumulatedVolume,
                Strength = c.Strength,
                TotalAskQty = c.TotalAskQty,
                TotalBidQty = c.TotalBidQty,
                Side = string.IsNullOrWhiteSpace(c.Source) ? "HIST" : c.Source
            };
        }


        private DateTime ToMinuteTime(DateTime time)
        {
            if (time == default(DateTime))
                return default(DateTime);

            return new DateTime(
                time.Year,
                time.Month,
                time.Day,
                time.Hour,
                time.Minute,
                0);
        }


        private DateTime GetMaxMinuteTimeFromCurrentBuffer()
        {
            lock (_syncLock)
            {
                if (_minuteHistoryBuffer == null || _minuteHistoryBuffer.Count == 0)
                    return DateTime.MinValue;

                var last = _minuteHistoryBuffer[_minuteHistoryBuffer.Count - 1];

                if (last == null)
                    return DateTime.MinValue;

                return ToMinuteTime(last.CandleTime);
            }
        }



        private DateTime GetMaxMinuteTimeFromHoldingStore(HoldingSymbolContext store)
        {
            if (store == null)
                return DateTime.MinValue;

            if (store.MinuteList == null || store.MinuteList.Count == 0)
                return DateTime.MinValue;

            var last = store.MinuteList[store.MinuteList.Count - 1];

            if (last == null)
                return DateTime.MinValue;

            return ToMinuteTime(last.LastUpdated);
        }

        // ============================================================
        // Source: MainWindowViewModel.MarketData.Reset.cs
        // ============================================================
        private void CheckAndPerformDailyReset()
        {
            // 기존 방식은 09:00에 LoadSymbolDataAsync()를 다시 호출했지만,
            // 현재 창이 같은 종목을 보고 있으면 FocusOpenedSymbolWindow()에서 즉시 반환되어
            // 실제 차트 초기화가 되지 않을 수 있었다.
            //
            // 실제 장 시작 초기화는 ResetChartForNewTradingDayIfNeeded(tick)에서
            // "전일 데이터 → 오늘 첫 실시간틱" 날짜 전환을 기준으로 처리한다.
            DateTime now = DateTime.Now;

            if (now.Hour == 0 && _isDailyResetDone)
                _isDailyResetDone = false;
        }


        private async Task ReloadCurrentSymbolForNewTradingDayAsync(TrackedSymbol tick)
        {
            if (tick == null || string.IsNullOrWhiteSpace(tick.Code))
                return;

            string code = tick.Code;
            string name = Context != null && Context.Code == code
                ? Context.Name
                : string.Empty;

            var holding = AccountHoldings?.FirstOrDefault(h => h.Code == code);
            if (holding != null)
            {
                name = string.IsNullOrWhiteSpace(holding.Name) ? name : holding.Name;
                //ClearHoldingStoreForReload(holding);
            }

            var symbol = new Symbol
            {
                Code = code,
                Name = name,
                LastPrice = tick.LastPrice > 0 ? tick.LastPrice.ToString() : Context?.LastPrice.ToString()
            };

            await LoadSymbolDataForCurrentWindowAsync(symbol, waitHoldingLoad: false);
        }

        private async Task LoadSymbolDataForCurrentWindowAsync(Symbol symbol, bool waitHoldingLoad = true)
        {
            if (symbol == null)
                return;

            IsLoading = true;
            await Task.Yield();

            PrepareCurrentWindowForSymbol(symbol);

            var holding = FindHoldingByCode(symbol.Code);

            if (holding != null)
            {
                bool handled = await TryLoadHoldingSymbolDataAsync(
                    symbol,
                    holding,
                    waitHoldingLoad);

                if (handled)
                    return;
            }

            RequestNormalSymbolMinuteData(symbol);

            IsSearchPopupOpen = false;
            AddLog($"✅ 현재창 일반 종목 분봉 요청: {symbol.Name}({symbol.Code})");
        }

        private void PrepareCurrentWindowForSymbol(Symbol symbol)
        {
            ClearBuffer();
            OnChartResetRequired?.Invoke();

            double price = 0;
            double.TryParse(symbol.LastPrice, out price);

            Context.SwitchSymbol(symbol.Code, symbol.Name, price);

            MarkAutoTradeDataNotReady(symbol.Code);
            _autoTradeController?.Reset(symbol.Code);

            ResetRealtimeTickWindow();
            _minuteHistoryBuffer.Clear();

            CheckHoldingStatus();

            _marketApi.ReplaceSubscription(
                this.WindowId,
                ScreenNo,
                symbol.Code);
        }

        private async Task<bool> TryLoadHoldingSymbolDataAsync(Symbol symbol, HoldingInfo holding, bool waitHoldingLoad)
        {
            var store = GlobalHoldingStore.GetOrCreate(
                holding.Code,
                holding.Name);

            bool hasMinuteData;
            bool isLoading;

            lock (store.SyncRoot)
            {
                hasMinuteData =
                    store.MinuteList.Count > 0 ||
                    store.IsMinuteHistoryLoaded ||
                    store.IsHistoryLoaded;

                isLoading = store.IsLoadingHistory;
            }

            if (!hasMinuteData && !isLoading)
                RequestHoldingHistoryAndRealtime(holding);

            if (hasMinuteData)
            {
                BindHoldingStoreToChart(store, symbol);
                CompleteSymbolLoading();
                AddLog($"📊 현재창 보유주 저장 데이터 바인딩: {symbol.Name}({symbol.Code})");
                return true;
            }

            if (waitHoldingLoad && isLoading)
            {
                AddLog($"⏳ 현재창 보유주 데이터 로딩 대기: {symbol.Name}({symbol.Code})");

                await store.HistoryLoadedSource.Task;

                if (store.IsHistoryLoaded)
                    BindHoldingStoreToChart(store, symbol);

                CompleteSymbolLoading();
                return true;
            }

            _ = BindHoldingStoreWhenLoadedAsync(store, symbol);

            CompleteSymbolLoading();
            AddLog($"⏳ 현재창 보유주 데이터 로딩 중: {symbol.Name}({symbol.Code})");
            return true;
        }
        private void RequestNormalSymbolMinuteData(Symbol symbol)
        {
            _marketApi.RequestMinuteCandles(
                this.WindowId,
                symbol.Code,
                ScreenNo,
                daysToLoad: 2,
                maxRows: 0,
                priority: 0);
        }
        private void CompleteSymbolLoading()
        {
            IsLoading = false;
            IsSearchPopupOpen = false;
        }
        private bool ResetChartForNewTradingDayIfNeeded(TrackedSymbol tick)
        {
            if (tick == null || tick.LastUpdated == default(DateTime))
                return false;

            if (string.IsNullOrWhiteSpace(Context.Code) || Context.Code != tick.Code)
                return false;

            DateTime tickDate = tick.LastUpdated.Date;
            DateTime currentDataDate = DateTime.MinValue;

            lock (_syncLock)
            {
                if (_minuteHistoryBuffer.Count > 0)
                    currentDataDate = _minuteHistoryBuffer[_minuteHistoryBuffer.Count - 1].CandleTime.Date;
                else if (Context.SymbolList != null && Context.SymbolList.Count > 0)
                    currentDataDate = Context.SymbolList[Context.SymbolList.Count - 1].LastUpdated.Date;
            }

            if (currentDataDate == DateTime.MinValue || currentDataDate >= tickDate)
                return false;

            if (_isDailyResetDone)
                return false;

            _isDailyResetDone = true;

            lock (_syncLock)
            {
                // 핵심 수정:
                // 전체 Clear 금지.
                // 오늘 데이터가 있으면 유지하고, 전일 데이터만 제거.
                _minuteHistoryBuffer.RemoveAll(x => x.CandleTime.Date < tickDate);

                _minuteTickBuffer.Clear();
                _currentMinute = DateTime.MinValue;
                _lastChartMinute = DateTime.MinValue;

                // Context.SymbolList도 전체 Clear 후 끝내면 차트가 실시간만 남을 수 있음.
                // 오늘 데이터만 다시 채운다.
                var analysisData = _minuteHistoryBuffer
                    .Select(ToTrackedSymbol)
                    .ToList();

                Context.SymbolList.Clear();
                Context.SymbolList.AddRange(analysisData);
            }

            lock (_realTimeBuffer)
            {
                // 기존 실시간 버퍼 전체 삭제는 유지해도 되지만,
                // 현재 틱은 반드시 보관.
                _realTimeBuffer.Clear();
                _realTimeBuffer.Enqueue(tick);
            }

            Application.Current?.Dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(() =>
                {
                    OnChartResetRequired?.Invoke();

                    Context.LastPrice = tick.LastPrice;
                    Context.Strength = tick.Strength;
                    IsLoading = false;

                    AddLog($"🧹 장 시작 차트 경계 초기화: {tick.Code} / {currentDataDate:yyyy-MM-dd} → {tickDate:yyyy-MM-dd}");
                }));

            return false;
        }


        private bool ResetHoldingChartForNewTradingDayIfNeeded(HoldingSymbolContext store, TrackedSymbol tick)
        {
            if (store == null || tick == null || tick.LastUpdated == default(DateTime))
                return false;

            if (string.IsNullOrWhiteSpace(Context.Code) || Context.Code != tick.Code)
                return false;

            DateTime tickDate = tick.LastUpdated.Date;
            DateTime currentDataDate = DateTime.MinValue;

            lock (store.SyncRoot)
            {
                if (store.MinuteList.Count > 0)
                    currentDataDate = store.MinuteList[store.MinuteList.Count - 1].LastUpdated.Date;
                else if (store.SymbolList.Count > 0)
                    currentDataDate = store.SymbolList[store.SymbolList.Count - 1].LastUpdated.Date;
            }

            if (currentDataDate == DateTime.MinValue || currentDataDate >= tickDate)
                return false;

            if (_isDailyResetDone)
                return false;

            _isDailyResetDone = true;

            lock (_syncLock)
            {
                _minuteTickBuffer.Clear();
                _currentMinute = DateTime.MinValue;
                _lastChartMinute = DateTime.MinValue;
            }

            Application.Current?.Dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(() =>
                {
                    OnChartResetRequired?.Invoke();

                    Context.LastPrice = tick.LastPrice;
                    Context.Strength = tick.Strength;
                    IsLoading = false;

                    AddLog($"🧹 보유주 장 시작 차트 초기화 1회: {tick.Code} / {currentDataDate:yyyy-MM-dd} → {tickDate:yyyy-MM-dd}");
                }));

            // 중요:
            // true 반환하면 현재 틱이 아래 UpsertHoldingMinuteCandle_NoSort()로 못 내려감.
            // 그래서 false 반환해야 함.
            return false;
        }

        // ============================================================
        // Source: MainWindowViewModel.MarketData.Ticks.cs
        // ============================================================
        private void AddRecentRealtimeTick(TrackedSymbol tick)
        {
            if (tick == null || string.IsNullOrWhiteSpace(tick.Code))
                return;

            lock (_recentRealtimeTickLock)
            {
                _recentRealtimeTicks.Enqueue(CloneTrackedSymbolForAutoTrade(tick));
                TrimRecentRawTicks_NoLock(_recentRealtimeTicks, tick.LastUpdated);
            }
        }


        private void TrimRecentRawTicks_NoLock(Queue<TrackedSymbol> queue, DateTime now)
        {
            if (queue == null || queue.Count == 0)
                return;

            DateTime cutoff = now.AddSeconds(-RawRealtimeTickRetentionSeconds);

            while (queue.Count > 0 && queue.Peek().LastUpdated < cutoff)
                queue.Dequeue();
        }



        private void MergeRecentRawTicks_NoLock(Queue<TrackedSymbol> queue, IEnumerable<TrackedSymbol> ticks, string code)
        {
            if (queue == null || ticks == null)
                return;

            DateTime latest = DateTime.MinValue;

            foreach (var tick in ticks)
            {
                if (tick == null || tick.Code != code || tick.LastUpdated == default(DateTime))
                    continue;

                queue.Enqueue(CloneTrackedSymbolForAutoTrade(tick));

                if (tick.LastUpdated > latest)
                    latest = tick.LastUpdated;
            }

            if (latest != DateTime.MinValue)
                TrimRecentRawTicks_NoLock(queue, latest);
        }


        private void ResetRealtimeTickWindow()
        {
            lock (_recentRealtimeTickLock)
            {
                _recentRealtimeTicks.Clear();
            }
        }


        private void Kiwoom_OnTickReceived(string windowId, TrackedSymbol tick)
        {
            if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
            {
                if (IsReplayMode)
                {
                    Application.Current.Dispatcher.Invoke(
                        DispatcherPriority.Background,
                        new Action(() => Kiwoom_OnTickReceived(windowId, tick)));
                }
                else
                {
                    Application.Current.Dispatcher.BeginInvoke(
                        DispatcherPriority.Background,
                        new Action(() => Kiwoom_OnTickReceived(windowId, tick)));
                }
                return;
            }

            if (IsReplayMode && !IsAutoTradingEnabled)
                return;

            if (!IsReplayMode && !IsRegularRealtimeTickForView(tick))
                return;

            if (!IsCurrentWindowTick(windowId, tick))
                return;

            if (ResetChartForNewTradingDayIfNeeded(tick))
                return;

            AddRecentRealtimeTick(tick);

            if (!_isHistoryBindingComplete)
            {
                BufferRealtimeTick(tick);
                UpdateCurrentSymbolFromTick(tick, isLoading: true);
                return;
            }

            DateTime tickMinute = ToMinuteTime(tick.LastUpdated);
            DateTime chartBaseMinute = IsReplayMode ? _lastChartMinute : GetMaxMinuteTimeFromCurrentBuffer();
            bool shouldRefreshChart = chartBaseMinute < tickMinute;

            UpsertCurrentMinuteCandle_NoSort(tick);
            CheckAndPerformDailyReset();

            TrackedSymbol latestMinute = null;

            if (shouldRefreshChart)
            {
                lock (_syncLock)
                {
                    latestMinute = IsReplayMode
                        ? GetCurrentMinuteForChartByTime_NoLock(tickMinute)
                        : GetLatestCurrentMinuteForChart_NoLock();
                }
            }

            UpdateCurrentSymbolFromTick(tick, isLoading: false);
            UpdateAccountHoldingPrice(tick.Code, tick.LastPrice);

            if (shouldRefreshChart && latestMinute != null)
            {
                RequestRealtimeMinuteChartRefresh(latestMinute);
                SyncAutoTradeMinuteCandles(tick.Code, IsReplayMode ? "Replay Tick 분봉 갱신" : "일반 실시간 분봉 갱신");
            }

            TryEnqueueAutoTradeTick(tick);
        }


        private void TryEnqueueAutoTradeTick(TrackedSymbol tick)
        {
            if (tick == null)
                return;

            if (_autoTradeSettings == null)
                return;

            if (_autoTradeController == null)
                return;

            if (!CanRunAutoTradeForTick(tick))
                return;


            HogaData latestHoga = null;

            lock (_latestHogaLock)
            {
                if (_latestHogaForAutoTrade != null &&
                    _latestHogaForAutoTrade.Code == tick.Code)
                {
                    latestHoga = _latestHogaForAutoTrade;
                }
            }

            if (latestHoga != null)
                _autoTradeController.UpdateHoga(latestHoga);

            _autoTradeController.EnqueueLatestTick(CloneTrackedSymbolForAutoTrade(tick));
        }

        private void Kiwoom_OnRecentTicksReceived(string windowId, string code, List<TrackedSymbol> ticks, bool isLast)
        {
            if (!string.IsNullOrWhiteSpace(windowId) && windowId.StartsWith("INVTICK:"))
            {
                HandleHoldingRecentTicks(windowId, code, ticks, isLast);
                return;
            }

            if (Context == null || windowId != this.WindowId || code != Context.Code)
                return;

            if (ticks != null && ticks.Count > 0)
            {
                var ordered = GetOrderedValidTicks(ticks, code);

                lock (_recentRealtimeTickLock)
                {
                    MergeRecentRawTicks_NoLock(_recentRealtimeTicks, ordered, code);

                    DateTime latest = GetLatestTickTimeOrNow(ordered);

                    TrimRecentRawTicks_NoLock(_recentRealtimeTicks, latest);
                }
            }

            if (!isLast)
                return;

            int rawCount;

            lock (_recentRealtimeTickLock)
            {
                rawCount = _recentRealtimeTicks.Count;
            }

            AddLog($"✅ 최근틱 보강 완료: {code} / 원본5분={rawCount}개");

            SeedAutoTradeHistory(code);
        }

        private void SeedAutoTradeHistory(string code)
        {
            if (string.IsNullOrWhiteSpace(code) || _autoTradeController == null)
                return;

            var minutes = GetMinuteCandlesForAutoTrade(code)
                .Where(x => x != null)
                .Select(CloneTrackedSymbolForAutoTrade)
                .ToList();

            if (minutes.Count > 0)
                _autoTradeController.UpdateMinuteCandles(code, minutes);

            List<TrackedSymbol> rawTicks5m = new List<TrackedSymbol>();

            if (!IsReplayMode)
            {
                HoldingSymbolContext store;
                if (GlobalHoldingStore.TryGet(code, out store) && store != null)
                {
                    lock (store.SyncRoot)
                    {
                        rawTicks5m = store.RecentTickList
                            .Where(x => x != null && x.Code == code)
                            .Select(CloneTrackedSymbolForAutoTrade)
                            .ToList();
                    }
                }
            }

            if (rawTicks5m.Count == 0)
            {
                lock (_recentRealtimeTickLock)
                {
                    rawTicks5m = _recentRealtimeTicks
                        .Where(x => x != null && x.Code == code)
                        .Select(CloneTrackedSymbolForAutoTrade)
                        .ToList();
                }
            }

            if (rawTicks5m.Count > 0)
                _autoTradeController.SeedRecentTicks(rawTicks5m);

            //_autoTradeController.UpdateCash(CurrentBalance);
            //_autoTradeController.UpdateOpenOrders(GetOpenOrdersForAutoTrade());
            SyncAutoTradeState("분석 데이터 Seed");

            if (Context != null && Context.Code == code)
            {
                _autoTradeController.UpdatePosition(
                    code,
                    GetHoldingQtyByCode(code),
                    GetHoldingEntryPriceByCode(code),
                    ResolveAutoTradePositionTimestamp());
            }

            if (minutes.Count >= 3)
            {
                MarkAutoTradeDataReady(code);

                AddAutoTradeAnalysisLog(
                    $"[자동매매] 분석 데이터 준비 완료: {code} / " +
                    $"분봉={minutes.Count}, 원본5분={rawTicks5m.Count}");
            }
            else
            {
                MarkAutoTradeDataNotReady(code);

                AddAutoTradeAnalysisLog(
                    $"[자동매매] 분석 데이터 부족: {code} / " +
                    $"분봉={minutes.Count}, 원본5분={rawTicks5m.Count}");
            }
        }

        private double GetHoldingEntryPriceByCode(string code)
        {
            var holding = FindHoldingByCode(code);
            return holding == null ? 0 : holding.EntryPrice;
        }

        private int GetHoldingQtyByCode(string code)
        {
            var holding = FindHoldingByCode(code);
            return holding == null ? 0 : holding.Quantity;
        }

        private HoldingInfo FindHoldingByCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return null;

            return AccountHoldings?
                .FirstOrDefault(x => x.Code == code);
        }


        private void HandleHoldingRecentTicks(string windowId, string code, List<TrackedSymbol> ticks, bool isLast)
        {
            if (string.IsNullOrWhiteSpace(code))
                code = windowId.Replace("INVTICK:", "");

            var store = GlobalHoldingStore.GetOrCreate(code);

            if (ticks != null && ticks.Count > 0)
            {
                var ordered = GetOrderedValidTicks(ticks, code);

                lock (store.SyncRoot)
                {
                    foreach (var tick in ordered)
                        AddRecentTickToHoldingStore_NoLock(store, tick);

                    if (ordered.Count > 0)
                        store.LastTickTime = ordered[ordered.Count - 1].LastUpdated;

                    TrimHoldingRecentTicks_NoLock(store);
                }
            }

            if (!isLast)
                return;

            int rawCount;

            lock (store.SyncRoot)
            {
                store.IsRecentTickLoaded = true;

                if (store.RecentTickList.Count > 0)
                    store.LastTickTime = store.RecentTickList[store.RecentTickList.Count - 1].LastUpdated;

                TrimHoldingRecentTicks_NoLock(store);
                RecalculateHoldingLoadState(store);

                rawCount = store.RecentTickList.Count;
            }

            Application.Current.Dispatcher.BeginInvoke(
                DispatcherPriority.DataBind,
                new Action(() =>
                {
                    var holding = AccountHoldings.FirstOrDefault(x => x.Code == code);
                    if (holding != null)
                    {
                        holding.IsHistoryLoading = !store.IsHistoryLoaded;
                        holding.IsHistoryLoaded = store.IsHistoryLoaded;
                    }
                }));

            AddLog($"✅ 보유주 최근틱 로딩 완료: {code} / 원본5분={rawCount}개");
        }


        private List<TrackedSymbol> AggregateToMinuteBars(List<TrackedSymbol> source)
        {
            var result = new List<TrackedSymbol>();

            if (source == null || source.Count == 0)
                return result;

            DateTime currentMinute = DateTime.MinValue;
            List<TrackedSymbol> buffer = new List<TrackedSymbol>();

            foreach (var tick in source)
            {
                if (tick == null)
                    continue;

                DateTime minute = ToMinuteTime(tick.LastUpdated);

                if (currentMinute == DateTime.MinValue)
                    currentMinute = minute;

                if (minute != currentMinute)
                {
                    var merged = MergeMinuteTicksCore(buffer, currentMinute);
                    if (merged != null)
                        result.Add(merged);

                    buffer.Clear();
                    currentMinute = minute;
                }

                buffer.Add(tick);
            }

            if (buffer.Count > 0)
            {
                var merged = MergeMinuteTicksCore(buffer, currentMinute);
                if (merged != null)
                    result.Add(merged);
            }

            return result;
        }


        private TrackedSymbol MergeMinuteTicksCore(List<TrackedSymbol> ticks, DateTime minute)
        {
            if (ticks == null || ticks.Count == 0)
                return null;

            var first = ticks[0];
            var last = ticks[ticks.Count - 1];

            double totalVolume = 0;
            double strengthWeightSum = 0;
            double high = double.MinValue;
            double low = double.MaxValue;

            foreach (var tick in ticks)
            {
                double vol = Math.Abs(tick.LastVolume);

                totalVolume += vol;
                strengthWeightSum += tick.Strength * vol;

                if (tick.LastPrice > high)
                    high = tick.LastPrice;

                if (tick.LastPrice < low)
                    low = tick.LastPrice;
            }

            if (totalVolume <= 0)
                totalVolume = 1;

            return new TrackedSymbol
            {
                Code = first.Code,
                LastUpdated = minute,

                LastPrice = last.LastPrice,
                ClosePrice = last.LastPrice,
                HighPrice = high,
                LowPrice = low,

                LastVolume = totalVolume,
                AccumulatedVolume = last.AccumulatedVolume,
                Strength = strengthWeightSum / totalVolume,

                TotalAskQty = last.TotalAskQty,
                TotalBidQty = last.TotalBidQty,
                Side = last.Side
            };
        }


        private void AddTickToBuffer(TrackedSymbol tick)
        {
            lock (_syncLock)
            {
                Context.SymbolList.Add(tick);
                Context.Code = tick.Code;
                Context.LastPrice = tick.LastPrice;

                if (Context.CurrentHold != null)
                {
                    Context.CurrentHold.CurrentPrice = tick.LastPrice;
                }
            }
        }

        // ============================================================
    }
}
