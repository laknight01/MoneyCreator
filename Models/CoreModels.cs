using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MoneyCreator.Models
{
    // ============================================================
    // AutoTradeCore / MoneyCreator 공용 모델
    // - ViewModelBase 의존성 제거
    // - 단, UI에서 직접 바인딩되는 HoldingInfo / OrderInfo / MarketIndexInfo는
    //   INotifyPropertyChanged를 자체 구현해서 SetProperty 동작을 유지한다.
    // - 이 파일은 MoneyCreator.AutoTradeCore 프로젝트에 둔다.
    // ============================================================

    public static class TradingCostPolicy
    {
        public const double SimulationFeeRate = 0.00355;
        public const double DefaultSellTaxRate = 0.0020;

        private static readonly object _syncRoot = new object();
        private static double _buyFeeRate = SimulationFeeRate;
        private static double _sellFeeRate = SimulationFeeRate;
        private static double _sellTaxRate = DefaultSellTaxRate;

        public static double BuyFeeRate
        {
            get { lock (_syncRoot) return _buyFeeRate; }
            set { lock (_syncRoot) _buyFeeRate = ClampRate(value); }
        }

        public static double SellFeeRate
        {
            get { lock (_syncRoot) return _sellFeeRate; }
            set { lock (_syncRoot) _sellFeeRate = ClampRate(value); }
        }

        public static double SellTaxRate
        {
            get { lock (_syncRoot) return _sellTaxRate; }
            set { lock (_syncRoot) _sellTaxRate = ClampRate(value); }
        }

        public static void SetRates(double buyFeeRate, double sellFeeRate, double sellTaxRate)
        {
            lock (_syncRoot)
            {
                _buyFeeRate = ClampRate(buyFeeRate);
                _sellFeeRate = ClampRate(sellFeeRate);
                _sellTaxRate = ClampRate(sellTaxRate);
            }
        }

        public static double EstimatedRoundTripCostRate
        {
            get
            {
                lock (_syncRoot)
                    return _buyFeeRate + _sellFeeRate + _sellTaxRate;
            }
        }

        public static long EstimateBuyRequiredAmount(int quantity, double price)
        {
            if (quantity <= 0 || price <= 0)
                return 0;

            double rate;
            lock (_syncRoot)
                rate = _buyFeeRate;

            return (long)Math.Ceiling(quantity * price * (1.0 + rate));
        }

        public static long EstimateSellReceivableAmount(int quantity, double price)
        {
            if (quantity <= 0 || price <= 0)
                return 0;

            double sellFeeRate;
            double sellTaxRate;

            lock (_syncRoot)
            {
                sellFeeRate = _sellFeeRate;
                sellTaxRate = _sellTaxRate;
            }

            double amount = quantity * price;
            double cost = amount * (sellFeeRate + sellTaxRate);
            return (long)Math.Max(0, Math.Floor(amount - cost));
        }

        public static double CalculateGrossProfitRate(double entryPrice, double exitPrice)
        {
            if (entryPrice <= 0 || exitPrice <= 0)
                return 0;

            return (exitPrice - entryPrice) / entryPrice;
        }

        public static double CalculateNetProfitRate(double entryPrice, double exitPrice)
        {
            if (entryPrice <= 0 || exitPrice <= 0)
                return 0;

            double buyFeeRate;
            double sellFeeRate;
            double sellTaxRate;

            lock (_syncRoot)
            {
                buyFeeRate = _buyFeeRate;
                sellFeeRate = _sellFeeRate;
                sellTaxRate = _sellTaxRate;
            }

            double buyCost = entryPrice * (1.0 + buyFeeRate);
            double sellNet = exitPrice * (1.0 - sellFeeRate - sellTaxRate);

            if (buyCost <= 0)
                return 0;

            return (sellNet - buyCost) / buyCost;
        }

        private static double ClampRate(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return 0;

            if (value < 0)
                return 0;

            if (value > 0.05)
                return 0.05;

            return value;
        }
    }

    public abstract class ObservableModelBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(storage, value))
                return false;

            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class TickData
    {
        public string Code { get; set; }
        public int Price { get; set; }
        public int Volume { get; set; }
        public double Strength { get; set; }
        public long BidVolume { get; set; }
        public long AskVolume { get; set; }
        public string Side { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class TrackedSymbol
    {
        public string Code { get; set; }
        public double LastPrice { get; set; }
        public double LastVolume { get; set; }
        public double ClosePrice { get; set; }
        public double HighPrice { get; set; }
        public double LowPrice { get; set; }
        public long AccumulatedVolume { get; set; }
        public long TotalAskQty { get; set; }
        public long TotalBidQty { get; set; }
        public double Strength { get; set; }
        public string Side { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    public class HogaData
    {
        public string Code { get; set; }
        public List<HogaItem> Asks { get; set; } = new List<HogaItem>();
        public List<HogaItem> Bids { get; set; } = new List<HogaItem>();
        public long TotalAskQty { get; set; }
        public long TotalBidQty { get; set; }
        public long NetQty => TotalBidQty - TotalAskQty;
    }

    public class HogaItem
    {
        public double Price { get; set; }
        public long KrxQuantity { get; set; }
        public long NxtQuantity { get; set; }
        public bool IsCurrentPrice { get; set; }
        public long MaxQuantity { get; set; }
        public long Quantity { get; set; }
        public long TotalQuantity => KrxQuantity + NxtQuantity;
    }

    public class HogaSnapshot
    {
        public string Code { get; set; }
        public long TotalAskQty { get; set; }
        public long TotalBidQty { get; set; }
        public DateTime Time { get; set; }
    }

    public enum OrderStatus
    {
        None = 0,
        Received,
        Confirmed,
        PartiallyFilled,
        FullyFilled,
        Corrected,
        Cancelled,
        Rejected
    }

    public class HoldingInfo : ObservableModelBase
    {
        private double _currentPrice;
        private double _entryPrice;
        private int _quantity;
        private bool _isHistoryLoading;
        private bool _isHistoryLoaded;

        public string Code { get; set; }
        public string Name { get; set; }

        public int Quantity
        {
            get => _quantity;
            set
            {
                if (SetProperty(ref _quantity, value))
                {
                    OnPropertyChanged(nameof(ProfitRate));
                    UpdateStatus();
                }
            }
        }

        public double EntryPrice
        {
            get => _entryPrice;
            set
            {
                if (SetProperty(ref _entryPrice, value))
                {
                    OnPropertyChanged(nameof(ProfitRate));
                    UpdateStatus();
                }
            }
        }

        public double CurrentPrice
        {
            get => _currentPrice;
            set
            {
                if (SetProperty(ref _currentPrice, value))
                {
                    OnPropertyChanged(nameof(ProfitRate));
                    UpdateStatus();
                }
            }
        }

        public double GrossProfitRate => TradingCostPolicy.CalculateGrossProfitRate(EntryPrice, CurrentPrice);
        public double ProfitRate => TradingCostPolicy.CalculateNetProfitRate(EntryPrice, CurrentPrice);
        public bool IsProfit => ProfitRate > 0;
        public bool IsLoss => ProfitRate < 0;
        public string ProfitStatus => ProfitRate > 0 ? "수익" : ProfitRate < 0 ? "손실" : "보합";

        public void UpdateCurrentPrice(double price)
        {
            CurrentPrice = price;
        }

        public void RefreshProfitStatus()
        {
            UpdateStatus();
            OnPropertyChanged(nameof(GrossProfitRate));
        }

        private void UpdateStatus()
        {
            OnPropertyChanged(nameof(GrossProfitRate));
            OnPropertyChanged(nameof(IsProfit));
            OnPropertyChanged(nameof(IsLoss));
            OnPropertyChanged(nameof(ProfitStatus));
            OnPropertyChanged(nameof(ProfitRate));
        }

        public bool IsHistoryLoading
        {
            get => _isHistoryLoading;
            set
            {
                if (SetProperty(ref _isHistoryLoading, value))
                    OnPropertyChanged(nameof(HistoryStatusText));
            }
        }

        public bool IsHistoryLoaded
        {
            get => _isHistoryLoaded;
            set
            {
                if (SetProperty(ref _isHistoryLoaded, value))
                    OnPropertyChanged(nameof(HistoryStatusText));
            }
        }

        public string HistoryStatusText
        {
            get
            {
                if (IsHistoryLoaded)
                    return "완료";

                if (IsHistoryLoading)
                    return "로딩중";

                return "대기";
            }
        }
    }

    public class OrderInfo : ObservableModelBase
    {
        private string _status;
        private int _untradedQty;
        private OrderStatus _orderStatus;

        public string OrderNo { get; set; }
        public string TradeGroupId { get; set; }
        public string OrderTime { get; set; }
        public string Code { get; set; }
        public string Name { get; set; }
        public string OrderType { get; set; }
        public int Qty { get; set; }
        public int ExecQty { get; set; }
        public double Price { get; set; }
        public double ExecPrice { get; set; }
        public string StrategyName { get; set; }
        public double Fee { get; set; }
        public double Tax { get; set; }
        public string ParentOrderNo { get; set; }

        public string TiRexDirection { get; set; }
        public double TiRexConfidence { get; set; }
        public double TiRexPredictedReturn { get; set; }

        public int UntradedQty
        {
            get => _untradedQty;
            set => SetProperty(ref _untradedQty, value);
        }

        public string Status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        public OrderStatus OrderStatus
        {
            get => _orderStatus;
            set => SetProperty(ref _orderStatus, value);
        }
    }

    public class MarketIndexInfo : ObservableModelBase
    {
        private double _price;
        private double _change;
        private double _changeRate;
        private long _volume;
        private DateTime _updatedAt;

        public string Code { get; set; }
        public string Name { get; set; }

        public double Price
        {
            get => _price;
            set => SetProperty(ref _price, value);
        }

        public double Change
        {
            get => _change;
            set => SetProperty(ref _change, value);
        }

        public double ChangeRate
        {
            get => _changeRate;
            set => SetProperty(ref _changeRate, value);
        }

        public long Volume
        {
            get => _volume;
            set => SetProperty(ref _volume, value);
        }

        public DateTime UpdatedAt
        {
            get => _updatedAt;
            set => SetProperty(ref _updatedAt, value);
        }

        public enum MarketTrendState
        {
            Unknown = 0,
            Panic = 1,
            Bear = 2,
            Weak = 3,
            Neutral = 4,
            Bull = 5,
            StrongBull = 6
        }

        public sealed class MarketEnvironment
        {
            public DateTime UpdatedAt { get; set; }
            public double KospiChangeRate { get; set; }
            public double KosdaqChangeRate { get; set; }
            public double AverageChangeRate { get; set; }
            public double MarketRiskScore { get; set; }
            public double MarketSupportScore { get; set; }
            public MarketTrendState Trend { get; set; }
            public string Description { get; set; }
            public int EntryScoreAdjustment { get; set; }
            public double PositionSizeMultiplier { get; set; } = 1.0;
            public double StopLossMultiplier { get; set; } = 1.0;
            public double TakeProfitMultiplier { get; set; } = 1.0;
            public double TrailingMultiplier { get; set; } = 1.0;

            public bool IsRiskOff =>
                Trend == MarketTrendState.Panic ||
                Trend == MarketTrendState.Bear ||
                Trend == MarketTrendState.Weak;
        }
    }
    public sealed class TickFrame
    {
        public string Code { get; set; }

        // 키움 체결 데이터 시각을 초 단위로 절사한 버킷 시각.
        public DateTime Time { get; set; }
        public DateTime UpdatedAt { get; set; }
        public bool IsClosed { get; set; }

        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Close { get; set; }

        public long Volume { get; set; }
        public long BuyVolume { get; set; }
        public long SellVolume { get; set; }
        public long NeutralVolume { get; set; }
        public long NetBuyVolume { get; set; }

        // 1초 동안의 방향별 체결대금 원천값.
        public double TotalTradeValue { get; set; }
        public double BuyTradeValue { get; set; }
        public double SellTradeValue { get; set; }
        public double NeutralTradeValue { get; set; }
        public double NetBuyTradeValue => BuyTradeValue - SellTradeValue;
        public double Vwap => Volume > 0 ? TotalTradeValue / Volume : Close;

        public long AccumulatedVolume { get; set; }

        // AvgStrength는 기존 호환용 단순평균, 공식 Frame 관찰값은 거래대금 가중값이다.
        public double AvgStrength { get; set; }
        public double ValueWeightedStrength { get; set; }
        public double LastStrength { get; set; }

        // 기존 호가 필드는 해당 Frame 마지막 관측값을 유지한다.
        public long TotalAskQty { get; set; }
        public long TotalBidQty { get; set; }

        public int TickCount { get; set; }
        public int BuyTickCount { get; set; }
        public int SellTickCount { get; set; }
        public int NeutralTickCount { get; set; }

        // 빈 초는 생성하지 않고 직전 데이터 Frame과의 실제 간격만 보존한다.
        public double ElapsedSeconds { get; set; } = 1.0;
        public bool HasTimeGap { get; set; }
    }

    public sealed class RealSubscription
    {
        public string WindowId { get; set; }
        public string Code { get; set; }
        public string ScreenNo { get; set; }
    }

    public class TradeSummary
    {
        public int TradeId { get; set; }
        public string TradeGroupId { get; set; }
        public string Code { get; set; }
        public string Name { get; set; }
        public string StrategyName { get; set; }
        public DateTime EntryTime { get; set; }
        public DateTime ExitTime { get; set; }
        public double AvgBuyPrice { get; set; }
        public int BuyQty { get; set; }
        public double AvgSellPrice { get; set; }
        public int SellQty { get; set; }
        public double ProfitRate { get; set; }
        public decimal ProfitAmount { get; set; }
        public double MaxProfitRate { get; set; }
        public double MinProfitRate { get; set; }
        public double HoldTimeMin { get; set; }
        public double TotalFeeTax { get; set; }
    }
}
