using AxKHOpenAPILib;
using MoneyCreator.Service;
using MoneyCreator.ViewModels;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Remoting.Contexts;
using System.Threading.Tasks;

namespace MoneyCreator.Models
{
    // 3. 창별 전역 컨텍스트 (이 창의 모든 데이터 주머니)
    public class StockContext : ViewModelBase
    {
        private string _code;
        public string Code { get => _code; set => SetProperty(ref _code, value); }

        private string _name;
        public string Name { get => _name; set => SetProperty(ref _name, value); }

        private int _profitrate;
        public int ProfitRate { get => _profitrate; set => SetProperty(ref _profitrate, value); } //수익률
        public string Change { get; set; } = string.Empty;
        // [데이터 통합] 모든 틱 히스토리가 쌓이는 곳
        public List<TrackedSymbol> SymbolList { get; } = new List<TrackedSymbol>();

        private double _strength;
        public double Strength
        {
            get => _strength;
            set => SetProperty(ref _strength, value);
        }

        // [상태 관리] 현재 보유 중인 정보 (미보유 시 null)
        private HoldingInfo _currentHold;
        public HoldingInfo CurrentHold
        {
            get => _currentHold;
            set
            {
                // 1. HoldingInfo 객체 자체가 바뀔 때
                if (SetProperty(ref _currentHold, value))
                {
                    OnPropertyChanged(nameof(IsHolding));
                    // 여기서 더 이상 OnPropertyChanged(nameof(ProfitStatus))를 부를 필요가 없어!
                    // 왜냐하면 뷰모델에는 그런 속성이 없으니까.
                }
            }
        }

        // 뷰모델에서 IsHolding을 판단하는 로직
        public bool IsHolding => CurrentHold != null && CurrentHold.Quantity > 0;
        // 실시간 UI 업데이트용 (가장 빈번하게 변함)
        private double _openPrice;
        public double OpenPrice
        {
            get => _openPrice;
            set => SetProperty(ref _openPrice, value); // 시가 설정
        }

        private double _lastPrice;
        public double LastPrice
        {
            get => _lastPrice;
            set
            {
                if (SetProperty(ref _lastPrice, value))
                {
                    // 가격이 변할 때마다 아래 두 속성도 같이 변했다고 알림
                    OnPropertyChanged(nameof(DisplayChangeText));
                    OnPropertyChanged(nameof(ChangeRate));
                }
            }
        }
        // UI에 표시될 문자열을 여기서 한 번에 포맷팅 (심플화의 핵심)
        public string DisplayChangeText => OpenPrice == 0 ? "0(0%)" :
            $"{(_lastPrice >= _openPrice ? "+" : "")}{_lastPrice - _openPrice:N0}({ChangeRate:P2})";

        // 색상 결정을 위한 등락률
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
            // 1. [초기화] 이전 데이터 완전 삭제 (필드를 직접 건드려 알림 최적화)
            _code = string.Empty;
            _name = string.Empty;
            _lastPrice = 0;
            _currentHold = null;

            // 리스트 비우기 (새로 할당해서 GC에 맡기는 게 더 빠를 수 있음)
            SymbolList.Clear();

            // 2. [주입] 새로운 종목 정보 설정
            // 여기서 프로퍼티(Code, Name)를 사용해야 OnPropertyChanged가 발생해서 UI가 바뀜
            this.Code = newCode;
            this.Name = newName;
            this.LastPrice = lastprice;
            this.OpenPrice = lastprice;
            // 3. [알림] UI에 모든 상태가 변경되었음을 일괄 통지 (성능 최적화)
            // 개별 속성 알림 외에 전체 갱신을 한 번 더 때려줌
            OnPropertyChanged(string.Empty);


            // IsHolding 상태도 명시적으로 갱신
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

        // 분봉
        public List<TrackedSymbol> MinuteList { get; } = new List<TrackedSymbol>();

        // 기존 호환용
        public Queue<TrackedSymbol> PendingTicks { get; } = new Queue<TrackedSymbol>();

        // 최근 10분 틱
        public List<TrackedSymbol> RecentTickList { get; } = new List<TrackedSymbol>();

        public bool IsHistoryLoaded { get; set; }
        public bool IsLoadingHistory { get; set; }

        // 신규
        public bool IsMinuteLoaded { get; set; }
        public bool IsRecentTickLoaded { get; set; }

        // 기존 호환 유지
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

    public enum OrderStatus
    {
        None = 0,           // 없음 / 초기값

        // 주문 접수 단계
        Received,           // 주문접수
        Confirmed,          // 주문확인 (정정/취소 확인 포함)

        // 체결 단계
        PartiallyFilled,    // 부분체결
        FullyFilled,        // 전량체결

        // 정정 / 취소
        Corrected,          // 정정완료
        Cancelled,          // 취소완료

        // 실패 / 거부
        Rejected            // 주문거부 / 실패
    }


    public enum MarketApiMode
    {
        Real,
        Replay
    }

    // App.xaml.cs 나 별도 클래스에서 관리
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

    public class HogaData
    {
        public string Code { get; set; }         // 종목코드

        // 매도호가 리스트 (보통 10단 ~ 1단 역순으로 저장해서 UI에 바인딩)
        public List<HogaItem> Asks { get; set; } = new List<HogaItem>();

        // 매수호가 리스트 (1단 ~ 10단 순서로 저장)
        public List<HogaItem> Bids { get; set; } = new List<HogaItem>();

        public long TotalAskQty { get; set; }    // 매도호가 총잔량 (FID 121)
        public long TotalBidQty { get; set; }    // 매수호가 총잔량 (FID 125)

        public long NetQty => TotalBidQty - TotalAskQty; // 순잔량 (매수잔량 - 매도잔량)
    }

    /// <summary>
    /// 호가 한 줄(가격과 수량)을 담당하는 클래스
    /// </summary>
    public class HogaItem
    {
        public double Price { get; set; }    // 호가
        public long KrxQuantity { get; set; } // KRX 잔량
        public long NxtQuantity { get; set; } // NXT 잔량
        public bool IsCurrentPrice { get; set; } //현재가
        public long MaxQuantity { get; set; }// 잔량최대
        public long Quantity { get; set; }   // 호가 잔량
        public long TotalQuantity => KrxQuantity + NxtQuantity;
    }
    public class TradeMarkerInfo
    {
        public DateTime Time { get; set; }

        public double Price { get; set; }

        public bool IsBuy { get; set; }
    }

    // 1. 개별 틱/호가 데이터 (도서관의 책 한 권)
    public class TrackedSymbol
    {
        public string Code { get; set; }
        public double LastPrice { get; set; }        // 현재 체결가
        public double LastVolume { get; set; }          // 현재 체결량
        public double ClosePrice { get; set; }       // [추가] 해당 초의 최종 체결가 (분석용)
        public double HighPrice { get; set; }        // [추가] 해당 초의 최고가
        public double LowPrice { get; set; }         // [추가] 해당 초의 최저가
        public long AccumulatedVolume { get; set; }  // 누적 거래량

        // [추가] 시점 분석 데이터 (AI 학습 필수 항목)
        public long TotalAskQty { get; set; }        // 매도 총잔량
        public long TotalBidQty { get; set; }        // 매수 총잔량
        public double Strength { get; set; }         // 체결강도

        public string Side { get; set; } //매도 매수 구분

        public DateTime LastUpdated { get; set; }
    }

    // 2. 매수/보유 정보 (내가 산 시점의 말뚝)
    public class HoldingInfo : ViewModelBase
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
                    // 수량이 변할 때도 수익률이나 평가금액 갱신이 필요하면 추가
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

        public double ProfitRate => EntryPrice <= 0 ? 0 : (CurrentPrice - EntryPrice) / EntryPrice;

        public bool IsProfit => ProfitRate > 0;
        public bool IsLoss => ProfitRate < 0;

        public string ProfitStatus => ProfitRate > 0 ? "수익" : ProfitRate < 0 ? "손실" : "보합";

        public void UpdateCurrentPrice(double price)
        {
            CurrentPrice = price;
        }

        private void UpdateStatus()
        {
            OnPropertyChanged(nameof(IsProfit));
            OnPropertyChanged(nameof(IsLoss));
            OnPropertyChanged(nameof(ProfitStatus));
            OnPropertyChanged(nameof(ProfitRate)); // 수익률 값 자체도 갱신 알림
        }
        public bool IsHistoryLoading
        {
            get => _isHistoryLoading;
            set
            {
                if (SetProperty(ref _isHistoryLoading, value))
                {
                    OnPropertyChanged(nameof(HistoryStatusText));
                }
            }
        }

        public bool IsHistoryLoaded
        {
            get => _isHistoryLoaded;
            set
            {
                if (SetProperty(ref _isHistoryLoaded, value))
                {
                    OnPropertyChanged(nameof(HistoryStatusText));
                }
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

        // 💡 핵심: strength 필드 추가
        private string _strength;
        [JsonProperty("strength")]
        public string Strength
        {
            get => _strength;
            set { _strength = value; OnPropertyChanged(nameof(Strength)); }
        }

        // 기존 Reason은 유지하거나 필요 없으면 삭제해도 됨 (여기선 일단 유지)
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
    public sealed class RealSubscription
    {
        public string WindowId { get; set; }
        public string Code { get; set; }
        public string ScreenNo { get; set; }
    }

    public class OrderInfo : ViewModelBase
    {
        private string _status;
        private int _untradedQty;
        private OrderStatus _orderStatus;

        public string OrderNo { get; set; }           // PK
        public string TradeGroupId { get; set; }      // 그룹 ID (연결 고리)
        public string OrderTime { get; set; }
        public string Code { get; set; }
        public string Name { get; set; }
        public string OrderType { get; set; }         // BUY / SELL
        public int Qty { get; set; }
        public int ExecQty { get; set; }
        public double Price { get; set; }
        public double ExecPrice { get; set; }
        public string StrategyName { get; set; }
        public double Fee { get; set; }
        public double Tax { get; set; }
        public string ParentOrderNo { get; set; }

        // TiRex prediction associated with this order when available.
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

    public class TradeSummary
    {
        public int TradeId { get; set; }
        public string TradeGroupId { get; set; }      // UNIQUE
        public string Code { get; set; }
        public string Name { get; set; }
        public string StrategyName { get; set; }

        public DateTime EntryTime { get; set; }       // 최초 진입 시간
        public DateTime ExitTime { get; set; }        // 최종 청산 시간

        public double AvgBuyPrice { get; set; }
        public int BuyQty { get; set; }
        public double AvgSellPrice { get; set; }
        public int SellQty { get; set; }

        // [AI 학습 핵심 지표]
        public double ProfitRate { get; set; }        // 최종 수익률
        public decimal ProfitAmount { get; set; }     // 최종 수익금
        public double MaxProfitRate { get; set; }     // 보유 중 최고 수익률 (익절 타점 분석)
        public double MinProfitRate { get; set; }     // 보유 중 최저 수익률 (MDD, 리스크 분석)

        public double HoldTimeMin { get; set; }       // 보유 시간(분)
        public double TotalFeeTax { get; set; }
    }
    public class HogaSnapshot
    {
        public string Code { get; set; }

        public long TotalAskQty { get; set; }
        public long TotalBidQty { get; set; }

        public DateTime Time { get; set; }
    }

    public class MarketIndexInfo : ViewModelBase
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
}