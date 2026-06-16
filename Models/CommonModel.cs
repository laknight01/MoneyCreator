using AxKHOpenAPILib;
using MoneyCreator.Service;
using MoneyCreator.ViewModels;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;

namespace MoneyCreator.Models
{
    // ============================================================
    // UI 전용 모델
    // - ViewModelBase / SetProperty / OnPropertyChanged가 필요한 클래스만 남긴다.
    // - 자동매매/DB/틱/주문 공용 DTO는 MoneyCreator.Shared 쪽 CoreModels.cs / DataRecords.cs에 둔다.
    // ============================================================

    public class StockContext : ViewModelBase
    {
        private string _code;
        public string Code { get => _code; set => SetProperty(ref _code, value); }

        private string _name;
        public string Name { get => _name; set => SetProperty(ref _name, value); }

        private int _profitrate;
        public int ProfitRate { get => _profitrate; set => SetProperty(ref _profitrate, value); }

        public string Change { get; set; } = string.Empty;

        // TrackedSymbol은 AutoTradeCore의 MoneyCreator.Models.TrackedSymbol을 사용한다.
        public List<TrackedSymbol> SymbolList { get; } = new List<TrackedSymbol>();

        private double _strength;
        public double Strength
        {
            get => _strength;
            set => SetProperty(ref _strength, value);
        }

        private HoldingInfo _currentHold;
        public HoldingInfo CurrentHold
        {
            get => _currentHold;
            set
            {
                if (SetProperty(ref _currentHold, value))
                    OnPropertyChanged(nameof(IsHolding));
            }
        }

        public bool IsHolding => CurrentHold != null && CurrentHold.Quantity > 0;

        private double _openPrice;
        public double OpenPrice
        {
            get => _openPrice;
            set => SetProperty(ref _openPrice, value);
        }

        private double _lastPrice;
        public double LastPrice
        {
            get => _lastPrice;
            set
            {
                if (SetProperty(ref _lastPrice, value))
                {
                    OnPropertyChanged(nameof(DisplayChangeText));
                    OnPropertyChanged(nameof(ChangeRate));
                }
            }
        }

        public string DisplayChangeText => OpenPrice == 0 ? "0(0%)" :
            $"{(_lastPrice >= _openPrice ? "+" : "")}{_lastPrice - _openPrice:N0}({ChangeRate:P2})";

        public double ChangeRate => OpenPrice == 0 ? 0 : (double)(_lastPrice - _openPrice) / _openPrice;

        public void Reset()
        {
            _code = string.Empty;
            _name = string.Empty;
            SymbolList.Clear();
            _currentHold = null;
            _lastPrice = 0;
            OnPropertyChanged(string.Empty);
            SwitchSymbol(string.Empty, string.Empty, 0);
        }

        public void SwitchSymbol(string newCode, string newName, double lastprice)
        {
            _code = string.Empty;
            _name = string.Empty;
            _lastPrice = 0;
            _currentHold = null;

            SymbolList.Clear();

            Code = newCode;
            Name = newName;
            LastPrice = lastprice;
            OpenPrice = lastprice;

            OnPropertyChanged(string.Empty);
            OnPropertyChanged(nameof(IsHolding));
            OnPropertyChanged("CurrentHold.Quantity");
            OnPropertyChanged("CurrentHold.EntryPrice");
        }
    }

    public class HoldingSymbolContext
    {
        public string Code { get; set; }
        public string Name { get; set; }

        public List<TrackedSymbol> SymbolList { get; } = new List<TrackedSymbol>();
        public List<TrackedSymbol> MinuteList { get; } = new List<TrackedSymbol>();
        public Queue<TrackedSymbol> PendingTicks { get; } = new Queue<TrackedSymbol>();
        // 최근 30초 원본틱
        public List<TrackedSymbol> RecentTickList { get; } = new List<TrackedSymbol>();
        // 최근 5분 1초 압축틱
        public List<TickFrame> TickFrameList { get; } = new List<TickFrame>();
        public bool IsHistoryLoaded { get; set; }
        public bool IsLoadingHistory { get; set; }
        public bool IsMinuteLoaded { get; set; }
        public bool IsRecentTickLoaded { get; set; }
        public bool IsMinuteHistoryLoaded { get; set; }

        public DateTime LastTickTime { get; set; } = DateTime.MinValue;
        public readonly object SyncRoot = new object();

        public TaskCompletionSource<bool> HistoryLoadedSource { get; set; }
            = new TaskCompletionSource<bool>();
    }

    public static class GlobalHoldingStore
    {
        public static readonly ConcurrentDictionary<string, HoldingSymbolContext> Holdings
            = new ConcurrentDictionary<string, HoldingSymbolContext>();

        public static HoldingSymbolContext GetOrCreate(string code, string name = "")
        {
            return Holdings.GetOrAdd(code, _ => new HoldingSymbolContext
            {
                Code = code,
                Name = name
            });
        }

        public static bool TryGet(string code, out HoldingSymbolContext ctx)
        {
            return Holdings.TryGetValue(code, out ctx);
        }
    }

    public enum MarketApiMode
    {
        Real,
        Replay
    }

    public static class GlobalApi
    {
        public static MarketApiMode Mode { get; set; } = MarketApiMode.Real;
        public static IMarketApiService Service { get; set; }
        public static AxKHOpenAPI AxAPI { get; set; }
    }

    public class Symbol
    {
        public string Name { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string LastPrice { get; set; } = string.Empty;
        public string Change { get; set; } = string.Empty;
    }

    public class TradeMarkerInfo
    {
        public DateTime Time { get; set; }
        public double Price { get; set; }
        public bool IsBuy { get; set; }
    }

    public class AiResult : INotifyPropertyChanged
    {
        private string _code;
        [JsonProperty("code")]
        public string Code
        {
            get => _code;
            set { _code = value; OnPropertyChanged(nameof(Code)); }
        }

        private string _name;
        [JsonProperty("name")]
        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(nameof(Name)); }
        }

        private string _strength;
        [JsonProperty("strength")]
        public string Strength
        {
            get => _strength;
            set { _strength = value; OnPropertyChanged(nameof(Strength)); }
        }

        private string _reason;
        [JsonProperty("reason")]
        public string Reason
        {
            get => _reason;
            set { _reason = value; OnPropertyChanged(nameof(Reason)); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
