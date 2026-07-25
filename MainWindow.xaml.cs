using AxKHOpenAPILib;
using MoneyCreator.Models;
using MoneyCreator.DataBase;
using MoneyCreator.Service;
using MoneyCreator.ViewModels;
using ScottPlot;
using ScottPlot.ArrowShapes;
using ScottPlot.AxisRules;
using ScottPlot.Plottables;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MoneyCreator
{
    public partial class MainWindow : Window
    {
        private MainWindowViewModel _vm;
        private AxKHOpenAPI _axAPI;
        private readonly MarketApiMode _windowApiMode;
        private readonly List<double> _xs = new List<double>();
        private readonly List<double> _ps = new List<double>();
        private readonly List<double> _vs = new List<double>();
        private readonly List<double> _upPs = new List<double>();
        private readonly List<double> _downPs = new List<double>();

        private Scatter _buyMarkerScatter;
        private Scatter _sellMarkerScatter;

        private readonly List<double> _buyMarkerXs = new List<double>();
        private readonly List<double> _buyMarkerYs = new List<double>();
        private readonly List<double> _sellMarkerXs = new List<double>();
        private readonly List<double> _sellMarkerYs = new List<double>();

        // MainWindow 클래스 상단에 추가
        private double? _lastX = null;
        private double? _lastY = null;
        private bool _isFirstDraw = true; // 처음인지 확인용

        // 선을 하나씩 추가하지 않고, 이 객체 하나만 계속 업데이트함
        private SignalXY _priceSignal;
        private SignalXY _upSignal;
        private SignalXY _downSignal;
        private SignalXY _volumeSignal;
        private SignalXY _scoreSignal;
        private readonly List<double> _scoreXs = new List<double>();
        private readonly List<double> _scoreYs = new List<double>();
        private DateTime _lastReplayChartRenderTime = DateTime.MinValue;
        private DateTime _lastScoreChartRefreshAt = DateTime.MinValue;
        private HorizontalLine _scoreZeroLine;
        private HorizontalLine _scoreBuyLine;
        private HorizontalLine _scoreSellLine;
        private Crosshair _scoreCrosshair;
        private bool _scoreChartInitialized = false;
        private bool _hasRealAnalysisScorePoint = false;

        private Crosshair _crosshair;
        private HorizontalLine _openLine;
        private LockedVertical _volumeLockRule;

        private bool _chartInitialized = false;
        private int _tickCount = 0;

        private DateTime _lastTickTime = DateTime.MinValue;

        public MainWindow()
        {
            InitializeComponent();
            RegisterAutoTradeAnalysisLogCopyShortcut();
            RegisterMainLogCopyShortcut();
            _windowApiMode = GlobalApi.Mode;
            //CreatePassword();

            if (GlobalApi.Service == null)
            {
                if (_windowApiMode == MarketApiMode.Replay)
                {
                    GlobalApi.Service = CreateReplayApiService();

                    // 테스트 모드는 OCX 필요 없음
                    APIHost.Visibility = Visibility.Collapsed;
                }
                else
                {
                    GlobalApi.Service = KiwoomApiService.Instance;
                    GlobalApi.AxAPI = new AxKHOpenAPI();
                    this.APIHost.Child = GlobalApi.AxAPI;
                }
            }

            _vm = new MainWindowViewModel(GlobalApi.Service);
            this.DataContext = _vm;
            _vm.HostWindow = this;

            if (_windowApiMode == MarketApiMode.Real && GlobalApi.AxAPI != null)
            {
                this.APIHost.Child = GlobalApi.AxAPI;
            }

            // 생성자나 차트 초기화 부분
            var crosshair = MyPlot.Plot.Add.Crosshair(0, 0);
            crosshair.IsVisible = false; // 기본은 숨김

            MyPlot.UserInputProcessor.IsEnabled = false;
            // 6. 폰트 색상 설정 (밝은 회색)
            var fontColor = ScottPlot.Color.FromHex("#CCCCCC"); // 밝은 회색
            // X축(시간) 글자 색상
            MyPlot.Plot.Axes.Bottom.TickLabelStyle.ForeColor = fontColor;
            // Y축(가격) 글자 색상
            MyPlot.Plot.Axes.Left.TickLabelStyle.ForeColor = fontColor;
            // Y축(거래량) 글자 색상
            MyPlot.Plot.Axes.Right.TickLabelStyle.ForeColor = fontColor;
            MyPlot.Plot.FigureBackground.Color = ScottPlot.Color.FromHex("#1E1E1E"); // 바깥
            MyPlot.Plot.DataBackground.Color = ScottPlot.Color.FromHex("#002341");   // 내부
            MyPlot.Plot.DataBorder.Color = ScottPlot.Color.FromHex("#020202");   // 내부
            MyPlot.Plot.FigureBorder.Color = ScottPlot.Color.FromHex("#E0E0E0");   // 내부
            // 추가: 격자(Grid) 선도 밝은 회색으로 해주면 검은색 배경에서 훨씬 예뻐!
            MyPlot.Plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#333333");
            MyPlot.MouseMove += MyPlot_MouseMove;
            MyPlot.MouseLeave += MyPlot_MouseLeave;

            MyPlotScore.MouseMove += MyPlotScore_MouseMove;
            MyPlotScore.MouseLeave += MyPlotScore_MouseLeave;

            InitScoreChart();
            ApplyMatchedChartLayout();
            _crosshair = MyPlot.Plot.Add.Crosshair(0, 0);
            _crosshair.IsVisible = false;

            // 뷰모델의 리셋 이벤트 구독
            _vm.OnChartResetRequired += ResetChart;

            this.Loaded += async (s, e) =>
            {
                SearchBox.Focus();

                if (_windowApiMode == MarketApiMode.Real)
                {
                    _vm.RegisterApi(GlobalApi.AxAPI);
                }

                if (DataContext is MainWindowViewModel vm)
                {
                    if (vm.IsConnected())
                        vm.HandleAlreadyLoggedIn();
                    else
                        await vm.ExecuteConnect();
                }
                // =========================
                // 이벤트
                // =========================
                _vm.TradeMarkerRequested += (time, price, isBuy) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        AddTradeMarker(time, price, isBuy);
                    });
                };
                _vm.RequestChartRefresh += (payload) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        var symbolList = payload?.Candles;
                        if (symbolList == null || symbolList.Count == 0)
                            return;

                        //Debug.WriteLine($"[차트 갱신] {symbolList.Count}개 봉, FullRefresh={payload.IsFullRefresh}, Markers={payload.Markers?.Count ?? 0}");
                        InitChart();

                        bool isFullRefresh = payload.IsFullRefresh || _xs.Count == 0;

                        if (isFullRefresh)
                        {
                            var sortedList = NormalizeChartCandlesForFullRefresh(symbolList);
                            if (sortedList.Count == 0)
                                return;

                            LoadInitialHistory(sortedList);

                            if (payload.Markers != null)
                                ApplyTradeMarkers(payload.Markers);
                        }
                        else
                        {
                            var latestTick = symbolList[symbolList.Count - 1];

                            if (_xs.Count > 0 && latestTick.LastUpdated < _lastTickTime)
                                return;

                            if (_vm != null && _vm.IsReplayMode && _lastReplayChartRenderTime != DateTime.MinValue &&
                                (latestTick.LastUpdated - _lastReplayChartRenderTime).TotalMilliseconds < 1000)
                            {
                                if (payload.Markers != null)
                                    MergeTradeMarkers(payload.Markers);
                                return;
                            }

                            if (_vm != null && _vm.IsReplayMode)
                                _lastReplayChartRenderTime = latestTick.LastUpdated;

                            AppendTick(latestTick);

                            // 실시간 갱신에서는 기존 마커를 지우지 않고 새 마커만 병합한다.
                            // 전체 재구성은 FullRefresh에서만 수행한다.
                            if (payload.Markers != null)
                                MergeTradeMarkers(payload.Markers);
                        }

                        if (!IsAscendingXs())
                            return;

                        double[] xsArray = _xs.ToArray();
                        double[] psArray = _ps.ToArray();
                        double[] upArray = _upPs.ToArray();
                        double[] downArray = _downPs.ToArray();
                        double[] vsArray = _vs.ToArray();

                        _priceSignal.Data = new ScottPlot.DataSources.SignalXYSourceDoubleArray(xsArray, psArray);
                        _upSignal.Data = new ScottPlot.DataSources.SignalXYSourceDoubleArray(xsArray, upArray);
                        _downSignal.Data = new ScottPlot.DataSources.SignalXYSourceDoubleArray(xsArray, downArray);
                        _volumeSignal.Data = new ScottPlot.DataSources.SignalXYSourceDoubleArray(xsArray, vsArray);

                        double openPrice = _ps[0];

                        _priceSignal.Color = _ps[_ps.Count - 1] >= openPrice
                            ? ScottPlot.Color.FromHex("#E62E22").WithAlpha(0.35)
                            : ScottPlot.Color.FromHex("#012cca").WithAlpha(0.55);

                        _openLine.Y = openPrice;

                        double minPrice = _ps.Min();
                        double maxPrice = _ps.Max();

                        if (minPrice == maxPrice)
                        {
                            minPrice -= 1;
                            maxPrice += 1;
                        }

                        double pad = (maxPrice - minPrice) * 0.05;
                        if (pad <= 0) pad = 1;

                        MyPlot.Plot.Axes.Left.Min = minPrice - pad;
                        MyPlot.Plot.Axes.Left.Max = maxPrice + pad;

                        MyPlot.Plot.Axes.Bottom.Min = _xs[0];
                        MyPlot.Plot.Axes.Bottom.Max = _xs[_xs.Count - 1];

                        double maxVol = _vs.Max();
                        if (maxVol <= 0) maxVol = 100;

                        _volumeLockRule.YMin = 0;
                        _volumeLockRule.Max = maxVol * 2;

                        if (++_tickCount % 10 == 0 || _isFirstDraw || isFullRefresh)
                        {
                            UpdateAxisTicks();
                            _isFirstDraw = false;
                        }

                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            MyPlot.Refresh();
                        }), System.Windows.Threading.DispatcherPriority.Background);
                    });
                };

                _vm.RequestAnalysisScorePoint += (point) =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        AddAnalysisScorePoint(point);
                    }), System.Windows.Threading.DispatcherPriority.Background);
                };
            };
        }

        private static IMarketApiService CreateReplayApiService()
        {
            return new LocalReplayApiService(KiwoomApiService.Instance);
        }

        private void ApplyMatchedChartLayout()
        {
            // ScottPlot 내부 PlotArea 폭을 맞추기 위한 처리.
            // 주의: Layout.Fixed(int, int, int, int) 형태는 ScottPlot 버전에 따라
            // padding이 아니라 사각형 좌표로 해석될 수 있어 차트가 쪼그라든다.
            // 그래서 1개 인자를 받는 PixelPadding 계열 Fixed 메서드가 있을 때만 적용한다.
            const int left = 55;
            const int right = 55;
            const int bottom = 25;
            const int top = 10;

            TryApplyPixelPaddingLayout(MyPlot, left, right, bottom, top);
            TryApplyPixelPaddingLayout(MyPlotScore, left, right, bottom, top);
        }

        private static void TryApplyPixelPaddingLayout(object plotControl, int left, int right, int bottom, int top)
        {
            if (plotControl == null)
                return;

            try
            {
                var plotProperty = plotControl.GetType().GetProperty("Plot");
                var plot = plotProperty == null ? null : plotProperty.GetValue(plotControl, null);
                if (plot == null)
                    return;

                var layoutProperty = plot.GetType().GetProperty("Layout");
                var layout = layoutProperty == null ? null : layoutProperty.GetValue(plot, null);
                if (layout == null)
                    return;

                var fixedMethods = layout.GetType()
                    .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                    .Where(x => x.Name == "Fixed")
                    .ToList();

                foreach (var method in fixedMethods)
                {
                    var parameters = method.GetParameters();
                    if (parameters.Length != 1)
                        continue;

                    var parameterType = parameters[0].ParameterType;
                    string typeName = parameterType.Name ?? string.Empty;

                    if (!typeName.Contains("Padding") && !typeName.Contains("Margin"))
                        continue;

                    object padding = CreatePaddingObject(parameterType, left, right, bottom, top);
                    if (padding == null)
                        continue;

                    method.Invoke(layout, new[] { padding });
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[차트 레이아웃 맞춤 실패] " + ex.Message);
            }
        }

        private static object CreatePaddingObject(Type paddingType, int left, int right, int bottom, int top)
        {
            foreach (var ctor in paddingType.GetConstructors())
            {
                var parameters = ctor.GetParameters();
                if (parameters.Length != 4)
                    continue;

                try
                {
                    var args = new object[parameters.Length];
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        int value = ResolvePaddingValue(parameters[i].Name, i, left, right, bottom, top);
                        args[i] = ConvertPaddingValue(parameters[i].ParameterType, value);
                    }

                    return ctor.Invoke(args);
                }
                catch
                {
                }
            }

            return null;
        }

        private static int ResolvePaddingValue(string name, int index, int left, int right, int bottom, int top)
        {
            name = string.IsNullOrWhiteSpace(name) ? string.Empty : name.ToLowerInvariant();

            if (name.Contains("left"))
                return left;

            if (name.Contains("right"))
                return right;

            if (name.Contains("bottom"))
                return bottom;

            if (name.Contains("top"))
                return top;

            switch (index)
            {
                case 0: return left;
                case 1: return right;
                case 2: return bottom;
                case 3: return top;
                default: return 0;
            }
        }

        private static object ConvertPaddingValue(Type targetType, int value)
        {
            if (targetType == typeof(float))
                return (float)value;

            if (targetType == typeof(double))
                return (double)value;

            if (targetType == typeof(int))
                return value;

            return Convert.ChangeType(value, targetType);
        }

        private void InitScoreChart()
        {
            if (_scoreChartInitialized)
                return;

            MyPlotScore.UserInputProcessor.IsEnabled = false;
            MyPlotScore.Plot.Clear();

            var fontColor = ScottPlot.Color.FromHex("#CCCCCC");
            MyPlotScore.Plot.FigureBackground.Color = ScottPlot.Color.FromHex("#1E1E1E");
            MyPlotScore.Plot.DataBackground.Color = ScottPlot.Color.FromHex("#101820");
            MyPlotScore.Plot.DataBorder.Color = ScottPlot.Color.FromHex("#020202");
            MyPlotScore.Plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#333333");
            MyPlotScore.Plot.Axes.Bottom.TickLabelStyle.ForeColor = fontColor;
            MyPlotScore.Plot.Axes.Left.TickLabelStyle.ForeColor = fontColor;

            _scoreSignal = MyPlotScore.Plot.Add.SignalXY(Array.Empty<double>(), Array.Empty<double>());
            _scoreSignal.LineWidth = 1.5f;
            _scoreSignal.Color = ScottPlot.Color.FromHex("#FFD966");

            _scoreZeroLine = MyPlotScore.Plot.Add.HorizontalLine(0);
            _scoreZeroLine.LineStyle.Pattern = ScottPlot.LinePattern.Dotted;
            _scoreZeroLine.Color = ScottPlot.Color.FromHex("#888888");

            // 실제 매매 기준선은 AutoTrading Core의 최종 점수 기준을 따라간다.
            // 고정 +25/-20 기준선은 통합 DecisionScore 기준과 어긋날 수 있어 초기에는 숨긴다.
            _scoreBuyLine = MyPlotScore.Plot.Add.HorizontalLine(0);
            _scoreBuyLine.LineStyle.Pattern = ScottPlot.LinePattern.Dashed;
            _scoreBuyLine.Color = ScottPlot.Color.FromHex("#3CB371");
            _scoreBuyLine.IsVisible = false;

            _scoreSellLine = MyPlotScore.Plot.Add.HorizontalLine(0);
            _scoreSellLine.LineStyle.Pattern = ScottPlot.LinePattern.Dashed;
            _scoreSellLine.Color = ScottPlot.Color.FromHex("#CD5C5C");
            _scoreSellLine.IsVisible = false;

            _scoreCrosshair = MyPlotScore.Plot.Add.Crosshair(0, 0);
            _scoreCrosshair.IsVisible = false;

            MyPlotScore.Plot.Axes.Left.Min = -100;
            MyPlotScore.Plot.Axes.Left.Max = 100;

            _scoreChartInitialized = true;
            ApplyMatchedChartLayout();
        }

        private void AddAnalysisScorePoint(AnalysisScorePoint point)
        {
            if (point == null || point.Time == DateTime.MinValue)
                return;

            InitScoreChart();

            double x = point.Time.ToOADate();
            double y = point.SignalScore;

            if (y < -100) y = -100;
            if (y > 100) y = 100;

            // 분봉 차트 기준으로 0점 seed가 먼저 들어간 상태에서 실제 분석값이 들어오면,
            // 해당 시점 이후의 0점 placeholder는 제거하고 실제 점수만 이어 붙인다.
            if (!_hasRealAnalysisScorePoint)
            {
                RemoveScorePlaceholdersFrom(x);
                _hasRealAnalysisScorePoint = true;
            }

            int insertIndex = FindScoreInsertIndex(x);

            if (insertIndex < _scoreXs.Count && _scoreXs[insertIndex] == x)
            {
                _scoreYs[insertIndex] = y;
                UpdateScoreThresholdLines(point);
                RefreshScoreChart();
                return;
            }

            _scoreXs.Insert(insertIndex, x);
            _scoreYs.Insert(insertIndex, y);
            TrimScoreChartPointsIfNeeded();
            UpdateScoreThresholdLines(point);
            RefreshScoreChart();
        }

        private void UpdateScoreThresholdLines(AnalysisScorePoint point)
        {
            if (point == null || _scoreBuyLine == null || _scoreSellLine == null)
                return;

            double? buyThreshold;
            double? sellThreshold;
            TryResolveScoreThresholds(point, out buyThreshold, out sellThreshold);

            if (!buyThreshold.HasValue || !sellThreshold.HasValue)
            {
                double? fallbackBuy;
                double? fallbackSell;
                TryResolveScoreThresholdsFromViewModel(out fallbackBuy, out fallbackSell);

                if (!buyThreshold.HasValue)
                    buyThreshold = fallbackBuy;

                if (!sellThreshold.HasValue)
                    sellThreshold = fallbackSell;
            }

            if (buyThreshold.HasValue)
            {
                _scoreBuyLine.Y = ClampScoreAxisValue(buyThreshold.Value);
                _scoreBuyLine.IsVisible = true;
            }

            if (sellThreshold.HasValue)
            {
                _scoreSellLine.Y = ClampScoreAxisValue(sellThreshold.Value);
                _scoreSellLine.IsVisible = true;
            }
        }

        private static bool TryResolveScoreThresholds(
            AnalysisScorePoint point,
            out double? buyThreshold,
            out double? sellThreshold)
        {
            buyThreshold = null;
            sellThreshold = null;

            if (point == null)
                return false;

            string text = string.Format(
                "{0} {1} {2}",
                point.Action ?? string.Empty,
                point.StrategyName ?? string.Empty,
                point.Reason ?? string.Empty);

            double value;
            if (TryParseThresholdAfter(text, "BuyDecisionScore", out value) ||
                TryParseThresholdAfter(text, "매수기준", out value))
            {
                buyThreshold = Math.Abs(value);
            }

            if (TryParseThresholdAfter(text, "SellDecisionScore", out value) ||
                TryParseThresholdAfter(text, "매도기준", out value))
            {
                sellThreshold = value > 0 ? -value : value;
            }

            if (!buyThreshold.HasValue && !sellThreshold.HasValue && TryParseThresholdAfter(text, string.Empty, out value))
            {
                if (value >= 0)
                    buyThreshold = value;
                else
                    sellThreshold = value;
            }

            return buyThreshold.HasValue || sellThreshold.HasValue;
        }

        private bool TryResolveScoreThresholdsFromViewModel(out double? buyThreshold, out double? sellThreshold)
        {
            buyThreshold = null;
            sellThreshold = null;

            object vm = _vm;
            if (vm == null)
                return false;

            double value;
            if (TryReadViewModelScoreThreshold(vm, "AutoTradeChartBuyThreshold", out value))
                buyThreshold = Math.Abs(value);

            if (TryReadViewModelScoreThreshold(vm, "AutoTradeChartSellThreshold", out value))
                sellThreshold = value > 0 ? -value : value;

            return buyThreshold.HasValue || sellThreshold.HasValue;
        }

        private static bool TryReadViewModelScoreThreshold(object viewModel, string propertyName, out double value)
        {
            value = 0;

            if (viewModel == null || string.IsNullOrWhiteSpace(propertyName))
                return false;

            try
            {
                var property = viewModel.GetType().GetProperty(propertyName);
                if (property == null)
                    return false;

                object raw = property.GetValue(viewModel, null);
                if (raw == null)
                    return false;

                value = Convert.ToDouble(raw);
                return !double.IsNaN(value) && !double.IsInfinity(value) && value != 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryParseThresholdAfter(string text, string marker, out double value)
        {
            value = 0;

            if (string.IsNullOrWhiteSpace(text))
                return false;

            string searchText = text;
            if (!string.IsNullOrWhiteSpace(marker))
            {
                int markerIndex = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (markerIndex < 0)
                    return false;

                searchText = text.Substring(markerIndex);
            }

            Match match = Regex.Match(
                searchText,
                @"Threshold\s*=\s*(?<value>[+-]?\d+(?:\.\d+)?)",
                RegexOptions.IgnoreCase);

            if (!match.Success)
                return false;

            return double.TryParse(match.Groups["value"].Value, out value);
        }

        private static double ClampScoreAxisValue(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return 0;

            if (value < -100)
                return -100;

            if (value > 100)
                return 100;

            return value;
        }

        private int FindScoreInsertIndex(double x)
        {
            int index = _scoreXs.BinarySearch(x);
            if (index >= 0)
                return index;

            return ~index;
        }

        private void RemoveScorePlaceholdersFrom(double x)
        {
            for (int i = _scoreXs.Count - 1; i >= 0; i--)
            {
                if (_scoreXs[i] < x)
                    continue;

                _scoreXs.RemoveAt(i);
                _scoreYs.RemoveAt(i);
            }
        }

        private void SeedScoreChartZerosFromPriceChart()
        {
            if (_xs.Count == 0)
                return;

            InitScoreChart();

            _scoreXs.Clear();
            _scoreYs.Clear();
            _hasRealAnalysisScorePoint = false;

            string code = _vm != null && _vm.Context != null && !string.IsNullOrWhiteSpace(_vm.Context.Code)
                ? _vm.Context.Code
                : string.Empty;

            for (int i = 0; i < _xs.Count; i++)
            {
                double x = _xs[i];
                double price = i < _ps.Count ? _ps[i] : 0;
                DateTime time = DateTime.FromOADate(x);

                _scoreXs.Add(x);
                _scoreYs.Add(0);
            }

            RefreshScoreChart(true);
        }

        private void TrimScoreChartPointsIfNeeded()
        {
            const int maxScorePoints = 30000;
            if (_scoreXs.Count <= maxScorePoints)
                return;

            int removeCount = _scoreXs.Count - maxScorePoints;
            _scoreXs.RemoveRange(0, removeCount);
            _scoreYs.RemoveRange(0, removeCount);
        }

        private void RefreshScoreChart()
        {
            RefreshScoreChart(false);
        }

        private void RefreshScoreChart(bool force)
        {
            if (_scoreSignal == null || _scoreXs.Count == 0)
                return;

            DateTime now = DateTime.Now;
            if (!force &&
                _lastScoreChartRefreshAt != DateTime.MinValue &&
                (now - _lastScoreChartRefreshAt).TotalMilliseconds < 250)
                return;

            _lastScoreChartRefreshAt = now;

            _scoreSignal.Data = new ScottPlot.DataSources.SignalXYSourceDoubleArray(
                _scoreXs.ToArray(),
                _scoreYs.ToArray());

            MyPlotScore.Plot.Axes.Left.Min = -100;
            MyPlotScore.Plot.Axes.Left.Max = 100;

            double minX;
            double maxX;
            ResolveScoreAxisRange(out minX, out maxX);

            MyPlotScore.Plot.Axes.Bottom.Min = minX;
            MyPlotScore.Plot.Axes.Bottom.Max = maxX;

            UpdateScoreAxisTicks();
            MyPlotScore.Refresh();
        }

        private void ResolveScoreAxisRange(out double minX, out double maxX)
        {
            if (_xs.Count > 0)
            {
                minX = _xs[0];
                maxX = _xs[_xs.Count - 1];
            }
            else
            {
                minX = _scoreXs[0];
                maxX = _scoreXs[_scoreXs.Count - 1];
            }

            if (minX == maxX)
            {
                minX = DateTime.FromOADate(minX).AddMinutes(-1).ToOADate();
                maxX = DateTime.FromOADate(maxX).AddMinutes(1).ToOADate();
            }
        }

        private void UpdateScoreAxisTicks()
        {
            if (_xs.Count == 0 && _scoreXs.Count == 0)
                return;

            double startOa;
            double endOa;
            ResolveScoreAxisRange(out startOa, out endOa);

            DateTime startTime = DateTime.FromOADate(startOa);
            DateTime endTime = DateTime.FromOADate(endOa);

            var manualTicks = new ScottPlot.TickGenerators.NumericManual();

            for (DateTime dt = startTime; dt <= endTime; dt = dt.AddMinutes(30))
                manualTicks.Add(new ScottPlot.Tick(dt.ToOADate(), dt.ToString("HH:mm")));

            manualTicks.Add(new ScottPlot.Tick(endTime.ToOADate(), endTime.ToString("HH:mm")));

            MyPlotScore.Plot.Axes.Bottom.TickGenerator = manualTicks;
            MyPlotScore.Plot.Axes.Bottom.TickLabelStyle.ForeColor = ScottPlot.Color.FromHex("#CCCCCC");
        }

        private void ResetScoreChart()
        {
            _scoreXs.Clear();
            _scoreYs.Clear();
            _hasRealAnalysisScorePoint = false;
            _lastReplayChartRenderTime = DateTime.MinValue;
            _lastScoreChartRefreshAt = DateTime.MinValue;

            if (_scoreSignal != null)
                _scoreSignal.Data = new ScottPlot.DataSources.SignalXYSourceDoubleArray(Array.Empty<double>(), Array.Empty<double>());

            if (_scoreCrosshair != null)
                _scoreCrosshair.IsVisible = false;

            if (ScoreHoverBorder != null)
                ScoreHoverBorder.Visibility = Visibility.Collapsed;

            if (_scoreChartInitialized)
            {
                MyPlotScore.Plot.Axes.Left.Min = -100;
                MyPlotScore.Plot.Axes.Left.Max = 100;

                if (_scoreBuyLine != null)
                    _scoreBuyLine.IsVisible = false;

                if (_scoreSellLine != null)
                    _scoreSellLine.IsVisible = false;

                MyPlotScore.Plot.Axes.Bottom.Min = DateTime.Now.ToOADate();
                MyPlotScore.Plot.Axes.Bottom.Max = DateTime.Now.AddMinutes(30).ToOADate();
                MyPlotScore.Refresh();
            }
        }

        private List<TrackedSymbol> NormalizeChartCandlesForFullRefresh(List<TrackedSymbol> candles)
        {
            if (candles == null || candles.Count == 0)
                return new List<TrackedSymbol>();

            // 전체 바인딩 시에만 1회 정렬한다.
            // 같은 시간 분봉이 중복되면 뒤쪽 데이터를 우선한다.
            return candles
                .Where(x => x != null)
                .GroupBy(x => x.LastUpdated)
                .Select(g => g.Last())
                .OrderBy(x => x.LastUpdated)
                .ToList();
        }

        private bool IsAscendingXs()
        {
            for (int i = 1; i < _xs.Count; i++)
            {
                if (_xs[i] < _xs[i - 1])
                    return false;
            }

            return true;
        }

        // =========================
        // 실시간 append
        // =========================
        private void AppendTick(TrackedSymbol tick)
        {
            double x = tick.LastUpdated.ToOADate();
            double p = tick.LastPrice;
            double v = tick.LastVolume;

            // 같은 시간 틱이면 마지막 값만 갱신
            if (_xs.Count > 0 && x == _xs[_xs.Count - 1])
            {
                int last = _xs.Count - 1;

                _ps[last] = p;
                _vs[last] = v;

                _upPs[last] = double.NaN;
                _downPs[last] = double.NaN;

                if (last > 0)
                {
                    bool isUp = p >= _ps[last - 1];

                    if (isUp)
                        _upPs[last] = p;
                    else
                        _downPs[last] = p;
                }

                _lastTickTime = tick.LastUpdated;
                return;
            }

            int lastIndex = _ps.Count - 1;

            _xs.Add(x);
            _ps.Add(p);
            _vs.Add(v);

            _upPs.Add(double.NaN);
            _downPs.Add(double.NaN);

            if (lastIndex < 0)
            {
                _upPs[0] = p;
            }
            else
            {
                bool isUp = p >= _ps[lastIndex];

                if (isUp)
                {
                    _upPs[lastIndex] = _ps[lastIndex];
                    _upPs[lastIndex + 1] = p;
                }
                else
                {
                    _downPs[lastIndex] = _ps[lastIndex];
                    _downPs[lastIndex + 1] = p;
                }
            }

            _lastTickTime = tick.LastUpdated;
        }

        // =========================
        // 전체 히스토리 초기 로드
        // (처음 1회만 전체 적재)
        // =========================
        private void LoadInitialHistory(List<TrackedSymbol> sortedList)
        {
            _xs.Clear();
            _ps.Clear();
            _vs.Clear();
            _upPs.Clear();
            _downPs.Clear();

            for (int i = 0; i < sortedList.Count; i++)
            {
                double x = sortedList[i].LastUpdated.ToOADate();
                double p = sortedList[i].LastPrice;
                double v = sortedList[i].LastVolume;

                _xs.Add(x);
                _ps.Add(p);
                _vs.Add(v);

                _upPs.Add(double.NaN);
                _downPs.Add(double.NaN);

                if (i == 0)
                {
                    _upPs[0] = p;
                }
                else
                {
                    bool isUp = p >= _ps[i - 1];

                    if (isUp)
                    {
                        _upPs[i - 1] = _ps[i - 1];
                        _upPs[i] = p;
                    }
                    else
                    {
                        _downPs[i - 1] = _ps[i - 1];
                        _downPs[i] = p;
                    }
                }
            }

            _lastTickTime = sortedList[sortedList.Count - 1].LastUpdated;

            SeedScoreChartZerosFromPriceChart();

        }

        private void ApplyTradeMarkers(List<TradeMarkerInfo> markers)
        {
            ClearTradeMarkers();

            if (markers == null || markers.Count == 0)
                return;

            foreach (var marker in markers)
                AddTradeMarker(marker.Time, marker.Price, marker.IsBuy);
        }

        private void MergeTradeMarkers(List<TradeMarkerInfo> markers)
        {
            if (markers == null || markers.Count == 0)
                return;

            foreach (var marker in markers)
                AddTradeMarker(marker.Time, marker.Price, marker.IsBuy);
        }
        private void RestoreTradeMarkers()
        {
            if (_vm == null)
                return;

            ClearTradeMarkers();

            var markers = _vm.GetCurrentTradeMarkers();

            foreach (var marker in markers)
            {
                AddTradeMarker(
                    marker.Time,
                    marker.Price,
                    marker.IsBuy);
            }
        }
        private void InitChart()
        {
            if (_chartInitialized)
                return;

            MyPlot.Plot.Clear();

            _priceSignal = MyPlot.Plot.Add.SignalXY(Array.Empty<double>(), Array.Empty<double>());
            _priceSignal.LineWidth = 1.5f;

            _upSignal = MyPlot.Plot.Add.SignalXY(Array.Empty<double>(), Array.Empty<double>());
            _upSignal.Color = ScottPlot.Color.FromHex("#a34900");
            _upSignal.LineWidth = 1.5f;

            _downSignal = MyPlot.Plot.Add.SignalXY(Array.Empty<double>(), Array.Empty<double>());
            _downSignal.Color = ScottPlot.Color.FromHex("#4E7AFD");
            _downSignal.LineWidth = 1.5f;

            _volumeSignal = MyPlot.Plot.Add.SignalXY(Array.Empty<double>(), Array.Empty<double>());
            _volumeSignal.Axes.YAxis = MyPlot.Plot.Axes.Right;
            _volumeSignal.Axes.XAxis = MyPlot.Plot.Axes.Bottom;
            _volumeSignal.Color = ScottPlot.Color.FromHex("#FBFBCF").WithAlpha(0.7);
            _volumeSignal.LineWidth = 1.5f;

            _openLine = MyPlot.Plot.Add.HorizontalLine(0);
            _openLine.LineStyle.Pattern = ScottPlot.LinePattern.Dotted;
            _openLine.Color = ScottPlot.Color.FromHex("#555555");

            RebuildTradeMarkerPlots();

            _crosshair = MyPlot.Plot.Add.Crosshair(0, 0);
            _crosshair.LineColor = ScottPlot.Color.FromHex("#c3c308");

            _volumeLockRule = new LockedVertical(MyPlot.Plot.Axes.Right, 0, 100);
            MyPlot.Plot.Axes.Rules.Add(_volumeLockRule);

            _chartInitialized = true;
            ApplyMatchedChartLayout();
        }

        private void AddTradeMarker(DateTime time, double price, bool isBuy)
        {
            if (price <= 0)
                return;

            InitChart();

            double x = time.ToOADate();

            if (HasTradeMarker(x, price, isBuy))
                return;

            if (isBuy)
            {
                _buyMarkerXs.Add(x);
                _buyMarkerYs.Add(price);
            }
            else
            {
                _sellMarkerXs.Add(x);
                _sellMarkerYs.Add(price);
            }

            RebuildTradeMarkerPlots();
            MyPlot.Refresh();
        }

        private bool HasTradeMarker(double x, double price, bool isBuy)
        {
            var xs = isBuy ? _buyMarkerXs : _sellMarkerXs;
            var ys = isBuy ? _buyMarkerYs : _sellMarkerYs;

            for (int i = 0; i < xs.Count && i < ys.Count; i++)
            {
                if (Math.Abs(xs[i] - x) < 0.000000001 && Math.Abs(ys[i] - price) < 0.0001)
                    return true;
            }

            return false;
        }

        private void ClearTradeMarkers()
        {
            _buyMarkerXs.Clear();
            _buyMarkerYs.Clear();
            _sellMarkerXs.Clear();
            _sellMarkerYs.Clear();

            if (_chartInitialized)
                RebuildTradeMarkerPlots();
        }

        private void RebuildTradeMarkerPlots()
        {
            if (_buyMarkerScatter != null)
                MyPlot.Plot.Remove(_buyMarkerScatter);

            if (_sellMarkerScatter != null)
                MyPlot.Plot.Remove(_sellMarkerScatter);

            _buyMarkerScatter = MyPlot.Plot.Add.Scatter(
                _buyMarkerXs.ToArray(),
                _buyMarkerYs.ToArray());

            _buyMarkerScatter.LineWidth = 0;
            _buyMarkerScatter.MarkerSize = 8;
            _buyMarkerScatter.Color = ScottPlot.Color.FromHex("#00FF66");

            _sellMarkerScatter = MyPlot.Plot.Add.Scatter(
                _sellMarkerXs.ToArray(),
                _sellMarkerYs.ToArray());

            _sellMarkerScatter.LineWidth = 0;
            _sellMarkerScatter.MarkerSize = 8;
            _sellMarkerScatter.Color = ScottPlot.Color.FromHex("#FF3333");
        }

        //// vs 대신에 전체 데이터 리스트를 받도록 수정
        //private void UpdateAxisTicks()
        //{
        //    // 1. 데이터에서 실제 시작 시간과 끝 시간을 추출
        //    double startOa = _xs[0];
        //    double endOa = _xs[_xs.Count - 1];

        //    DateTime startTime = DateTime.FromOADate(startOa);
        //    DateTime endTime = DateTime.FromOADate(endOa);

        //    // [중요] 만약 데이터가 너무 적을 경우를 대비해 최소 30분 정도의 폭은 확보
        //    if ((endTime - startTime).TotalMinutes < 30)
        //    {
        //        endTime = startTime.AddMinutes(30);
        //    }

        //    // 2. 하단 시간축 눈금 설정
        //    var manualTicks = new ScottPlot.TickGenerators.NumericManual();

        //    // 30분 간격으로 눈금 생성
        //    for (DateTime dt = startTime; dt <= endTime; dt = dt.AddMinutes(30))
        //    {
        //        manualTicks.Add(new ScottPlot.Tick(dt.ToOADate(), dt.ToString("HH:mm")));
        //    }

        //    // 마지막 시간 눈금 추가
        //    manualTicks.Add(new ScottPlot.Tick(endTime.ToOADate(), endTime.ToString("HH:mm")));

        //    MyPlot.Plot.Axes.Bottom.TickGenerator = manualTicks;
        //    MyPlot.Plot.Axes.Bottom.TickLabelStyle.ForeColor = ScottPlot.Color.FromHex("#CCCCCC");

        //    // X축 노출 범위를 실제 데이터 범위로 고정
        //    MyPlot.Plot.Axes.SetLimitsX(startTime.ToOADate(), endTime.ToOADate());

        //    // 오른쪽 거래량 축 설정 (기존과 동일)
        //    MyPlot.Plot.Axes.Right.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic();
        //    MyPlot.Plot.Axes.Right.TickLabelStyle.ForeColor = ScottPlot.Color.FromHex("#CCCCCC");
        //}

        private void UpdateAxisTicks()
        {
            if (_xs.Count == 0) return;

            double startOa = _xs[0];
            double endOa = _xs[_xs.Count - 1];

            DateTime startTime = DateTime.FromOADate(startOa);
            DateTime endTime = DateTime.FromOADate(endOa);

            // 데이터가 적어도 눈금은 미래까지 미리 만들어둘 필요가 없음
            // 차라리 현재 데이터 범위 안에서 30분 간격으로 생성
            var manualTicks = new ScottPlot.TickGenerators.NumericManual();

            // 시작 시간부터 데이터 끝까지 30분 단위 눈금
            for (DateTime dt = startTime; dt <= endTime; dt = dt.AddMinutes(30))
            {
                manualTicks.Add(new ScottPlot.Tick(dt.ToOADate(), dt.ToString("HH:mm")));
            }

            // 현재 마지막 시간은 항상 표시
            manualTicks.Add(new ScottPlot.Tick(endTime.ToOADate(), endTime.ToString("HH:mm")));

            MyPlot.Plot.Axes.Bottom.TickGenerator = manualTicks;
            MyPlot.Plot.Axes.Bottom.TickLabelStyle.ForeColor = ScottPlot.Color.FromHex("#CCCCCC");

            // 여기서 SetLimitsX나 Axis.Bottom.Max를 건드리지 마! (밖에서 처리할 거니까)
        }

        private void MyPlot_MouseMove(object sender, MouseEventArgs e)
        {
            // 1. 데이터 소스 및 크로스헤어 체크
            if (_priceSignal == null || _crosshair == null) return;

            // ISignalXYSource 타입으로 받기 (네가 준 소스 코드의 Data 타입)
            var priceData = _priceSignal.Data;
            var volumeData = _volumeSignal?.Data;

            if (priceData == null || priceData.Count == 0) return;

            // 2. 마우스 좌표를 차트 좌표로 변환
            var position = e.GetPosition(MyPlot);
            var pixel = new ScottPlot.Pixel((float)position.X, (float)position.Y);
            var coords = MyPlot.Plot.GetCoordinates(pixel);

            // 3. 인덱스 찾기
            // 소스 코드에서 본 GetNearest 로직처럼 IDataSource로 형변환해서 인덱스를 찾음
            // 인터페이스가 ScottPlot 네임스페이스에 있으므로 (ScottPlot.IDataSource)로 접근
            int index = 0;
            if (priceData is ScottPlot.IDataSource ds)
            {
                index = ds.GetXClosestIndex(coords);

                // 4. 데이터 값 추출 (IDataSource 메서드 활용)
                double xVal = ds.GetX(index);
                double yVal = ds.GetY(index);

                // 거래량도 동일하게 처리
                double vVal = 0;
                if (volumeData is ScottPlot.IDataSource vds)
                {
                    vVal = vds.GetY(index);
                }

                // 5. 크로스헤어 및 라벨 업데이트
                _crosshair.X = xVal;
                _crosshair.Y = yVal;
                _crosshair.IsVisible = true;

                DateTime tickTime = DateTime.FromOADate(xVal);
                PriceLabel.Text = $"[{tickTime:HH:mm:ss}] 가격: {yVal:N0} | 거래량: {vVal:N0}";

                Canvas.SetLeft(PriceLabel, position.X + 15);
                Canvas.SetTop(PriceLabel, position.Y + 20);
                PriceLabel.Visibility = Visibility.Visible;

                // 6. 차트 새로고침 (Refresh 호출 횟수를 조절하면 성능이 더 좋아짐)
                MyPlot.Refresh();
            }
        }
        private void MyPlot_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_crosshair != null)
            {
                _crosshair.IsVisible = false;
                PriceLabel.Visibility = Visibility.Collapsed;
                MyPlot.Refresh();
            }
        }

        private void MyPlotScore_MouseMove(object sender, MouseEventArgs e)
        {
            if (_scoreCrosshair == null || _scoreXs == null || _scoreYs == null)
                return;

            int count = Math.Min(_scoreXs.Count, _scoreYs.Count);
            if (count <= 0)
                return;

            Point mousePosition = e.GetPosition(MyPlotScore);
            ScottPlot.Pixel pixel = new ScottPlot.Pixel(
                (float)mousePosition.X,
                (float)mousePosition.Y);
            ScottPlot.Coordinates mouseCoordinates =
                MyPlotScore.Plot.GetCoordinates(pixel);

            int nearestIndex = 0;
            double nearestDistance = double.MaxValue;

            for (int i = 0; i < count; i++)
            {
                double distance = Math.Abs(_scoreXs[i] - mouseCoordinates.X);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearestIndex = i;
                }
            }

            double x = _scoreXs[nearestIndex];
            double score = _scoreYs[nearestIndex];

            _scoreCrosshair.X = x;
            _scoreCrosshair.Y = score;
            _scoreCrosshair.IsVisible = true;

            DateTime time;
            try
            {
                time = DateTime.FromOADate(x);
            }
            catch
            {
                time = DateTime.MinValue;
            }

            if (time == DateTime.MinValue)
            {
                ScoreLabel.Text = string.Format(
                    "점수: {0:+0.##;-0.##;0}",
                    score);
            }
            else
            {
                ScoreLabel.Text = string.Format(
                    "[{0:HH:mm:ss.fff}] 점수: {1:+0.##;-0.##;0}",
                    time,
                    score);
            }

            double left = mousePosition.X + 15.0;
            double top = mousePosition.Y + 15.0;

            const double estimatedWidth = 190.0;
            const double estimatedHeight = 34.0;

            if (left + estimatedWidth > MyPlotScore.ActualWidth)
                left = Math.Max(0.0, mousePosition.X - estimatedWidth - 10.0);

            if (top + estimatedHeight > MyPlotScore.ActualHeight)
                top = Math.Max(0.0, mousePosition.Y - estimatedHeight - 10.0);

            Canvas.SetLeft(ScoreHoverBorder, left);
            Canvas.SetTop(ScoreHoverBorder, top);
            ScoreHoverBorder.Visibility = Visibility.Visible;

            MyPlotScore.Refresh();
        }

        private void MyPlotScore_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_scoreCrosshair != null)
                _scoreCrosshair.IsVisible = false;

            if (ScoreHoverBorder != null)
                ScoreHoverBorder.Visibility = Visibility.Collapsed;

            MyPlotScore.Refresh();
        }

        private void ResetChart()
        {
            // =========================
            // 1. 누적 버퍼 초기화
            // =========================
            _xs.Clear();
            _ps.Clear();
            _vs.Clear();
            _upPs.Clear();
            _downPs.Clear();

            ClearTradeMarkers();
            ResetScoreChart();

            _lastTickTime = DateTime.MinValue;
            _tickCount = 0;

            // =========================
            // 2. Signal 데이터 비우기
            // (플로터블는 유지, 데이터만 제거)
            // =========================
            if (_priceSignal != null)
                _priceSignal.Data = new ScottPlot.DataSources.SignalXYSourceDoubleArray(Array.Empty<double>(), Array.Empty<double>());

            if (_upSignal != null)
                _upSignal.Data = new ScottPlot.DataSources.SignalXYSourceDoubleArray(Array.Empty<double>(), Array.Empty<double>());

            if (_downSignal != null)
                _downSignal.Data = new ScottPlot.DataSources.SignalXYSourceDoubleArray(Array.Empty<double>(), Array.Empty<double>());

            if (_volumeSignal != null)
                _volumeSignal.Data = new ScottPlot.DataSources.SignalXYSourceDoubleArray(Array.Empty<double>(), Array.Empty<double>());

            // =========================
            // 3. 기준선 초기화
            // =========================
            if (_openLine != null)
                _openLine.Y = 0;

            // =========================
            // 4. 축 초기화
            // (이전 종목 스케일 제거)
            // =========================
            MyPlot.Plot.Axes.Left.Min = 0;
            MyPlot.Plot.Axes.Left.Max = 1;

            MyPlot.Plot.Axes.Bottom.Min = DateTime.Now.ToOADate();
            MyPlot.Plot.Axes.Bottom.Max = DateTime.Now.AddMinutes(30).ToOADate();

            if (_volumeLockRule != null)
            {
                _volumeLockRule.YMin = 0;
                _volumeLockRule.Max = 100;
            }
            _isFirstDraw = true;
            // =========================
            // 5. 축 라벨 초기화
            // =========================
            //UpdateAxisTicks();

            // =========================
            // 6. 화면 즉시 갱신
            // =========================
            MyPlot.Refresh();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.UpdateSearchResults(SearchBox.Text);
            }
        }

        private void SearchListBox_MouseClick(object sender, MouseButtonEventArgs e)
        {
            DependencyObject source = e.OriginalSource as DependencyObject;

            while (source != null && !(source is ListBoxItem))
            {
                source = VisualTreeHelper.GetParent(source);
            }

            var item = source as ListBoxItem;
            if (item != null)
            {
                int index = SearchListBox.ItemContainerGenerator.IndexFromContainer(item);
                if (index >= 0)
                {
                    SearchListBox.SelectedIndex = index;
                    SearchBox.Text = (SearchListBox.Items[index] as Symbol).Name;
                }
            }
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Up || e.Key == Key.Down)
            {
                e.Handled = true;
            }
        }

        // SearchListBox용 (XAML에서 연결 필수)
        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!SearchListBox.HasItems) return;

            if (e.Key == Key.Down || e.Key == Key.Up)
            {
                HandleListBoxArrowNavigation(e);
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;

                if (_hoverIndex >= 0 && _hoverIndex < SearchListBox.Items.Count)
                {
                    SearchBox.TextChanged -= SearchBox_TextChanged;
                    SearchBox.Text = (SearchListBox.Items[_hoverIndex] as Symbol).Name;
                    SearchListBox.SelectedIndex = _hoverIndex;
                    SearchBox.TextChanged += SearchBox_TextChanged;
                }
            }
        }

        // 통합 제어 메서드
        private int _hoverIndex = -1;

        private void HandleListBoxArrowNavigation(KeyEventArgs e)
        {
            if (!SearchListBox.HasItems) return;

            e.Handled = true;

            if (_hoverIndex < 0)
                _hoverIndex = 0;
            else
            {
                _hoverIndex = e.Key == Key.Down
                    ? Math.Min(SearchListBox.Items.Count - 1, _hoverIndex + 1)
                    : Math.Max(0, _hoverIndex - 1);
            }

            //SearchListBox.Items.Refresh();
            Debug.WriteLine(_hoverIndex);
            SearchListBox.ScrollIntoView(SearchListBox.Items[_hoverIndex]);
            RefreshHoverHighlight();
            
        }

        private void RefreshHoverHighlight()
        {
            for (int i = 0; i < SearchListBox.Items.Count; i++)
            {
                if (SearchListBox.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem item)
                {
                    if (i == _hoverIndex)
                    {
                        item.Background = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString("#444444"));
                        item.BorderBrush = Brushes.Gold;
                    }
                    else
                    {
                        item.Background = Brushes.Transparent;
                        item.BorderBrush = Brushes.Transparent;
                    }
                }
            }
        }


        private void RegisterAutoTradeAnalysisLogCopyShortcut()
        {
            if (AutoTradeAnalysisLogListBox == null)
                return;

            AutoTradeAnalysisLogListBox.CommandBindings.Add(new CommandBinding(
                ApplicationCommands.Copy,
                (s, e) =>
                {
                    e.Handled = CopySelectedAutoTradeAnalysisLogs();
                },
                (s, e) =>
                {
                    e.CanExecute = AutoTradeAnalysisLogListBox.SelectedItems != null && AutoTradeAnalysisLogListBox.SelectedItems.Count > 0;
                    e.Handled = true;
                }));

            AutoTradeAnalysisLogListBox.InputBindings.Add(new KeyBinding(
                ApplicationCommands.Copy,
                new KeyGesture(Key.C, ModifierKeys.Control)));
        }

        private void AutoTradeAnalysisLogListBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.C && e.SystemKey != Key.C)
                return;

            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
                return;

            if (CopySelectedAutoTradeAnalysisLogs())
                e.Handled = true;
        }

        private bool CopySelectedAutoTradeAnalysisLogs()
        {
            var listBox = AutoTradeAnalysisLogListBox;
            if (listBox == null)
                return false;

            var lines = new List<string>();

            if (listBox.SelectedItems != null && listBox.SelectedItems.Count > 0)
            {
                foreach (var item in listBox.SelectedItems)
                {
                    string text = item == null ? string.Empty : item.ToString();
                    if (!string.IsNullOrEmpty(text))
                        lines.Add(text);
                }
            }

            if (lines.Count == 0 && listBox.SelectedItem != null)
            {
                string text = listBox.SelectedItem.ToString();
                if (!string.IsNullOrEmpty(text))
                    lines.Add(text);
            }

            if (lines.Count == 0)
                return false;

            Clipboard.SetText(string.Join(Environment.NewLine, lines));
            return true;
        }

        private void RegisterMainLogCopyShortcut()
        {
            if (MainLogListBox == null)
                return;

            MainLogListBox.CommandBindings.Add(new CommandBinding(
                ApplicationCommands.Copy,
                (s, e) =>
                {
                    e.Handled = CopySelectedMainLogs();
                },
                (s, e) =>
                {
                    e.CanExecute = MainLogListBox.SelectedItems != null && MainLogListBox.SelectedItems.Count > 0;
                    e.Handled = true;
                }));

            MainLogListBox.InputBindings.Add(new KeyBinding(
                ApplicationCommands.Copy,
                new KeyGesture(Key.C, ModifierKeys.Control)));
        }

        private void MainLogListBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.C && e.SystemKey != Key.C)
                return;

            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
                return;

            if (CopySelectedMainLogs())
                e.Handled = true;
        }

        private bool CopySelectedMainLogs()
        {
            return CopySelectedListBoxItems(MainLogListBox);
        }

        private bool CopySelectedListBoxItems(ListBox listBox)
        {
            if (listBox == null)
                return false;

            var lines = new List<string>();

            if (listBox.SelectedItems != null && listBox.SelectedItems.Count > 0)
            {
                foreach (var item in listBox.SelectedItems)
                {
                    string text = item == null ? string.Empty : item.ToString();
                    if (!string.IsNullOrEmpty(text))
                        lines.Add(text);
                }
            }

            if (lines.Count == 0 && listBox.SelectedItem != null)
            {
                string text = listBox.SelectedItem.ToString();
                if (!string.IsNullOrEmpty(text))
                    lines.Add(text);
            }

            if (lines.Count == 0)
                return false;

            Clipboard.SetText(string.Join(Environment.NewLine, lines));
            return true;
        }


        private void CreatePassword()
        {
            // 1. 네가 사용할 계좌 비밀번호를 여기에 적어 (실행 후 이 코드는 지워!)
            string realPassword = "521468";

            // 2. 저장할 경로 (실행 파일이 생기는 곳이나 특정 경로)
            string savePath = @"E:\Project\MoneyCreator\bin\x86\Debug\config.enc";

            try
            {
                // 우리가 만든 SecurityHelper로 암호화 저장
                SecurityHelper.SaveEncryptedPassword(realPassword, savePath);
                Console.WriteLine($"✅ 암호화 완료: {savePath}");
                Console.WriteLine("이 파일을 프로그램 실행 폴더로 옮기세요.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 오류 발생: {ex.Message}");
            }
        }
        private void NumberOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            // 숫자인지 확인하는 정규식 (0-9)
            Regex regex = new Regex("[^0-9]+");
            e.Handled = regex.IsMatch(e.Text);
        }

        // 마우스 우클릭으로 문자를 붙여넣는 경우까지 방어
        private void TextBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(typeof(string)))
            {
                string text = (string)e.DataObject.GetData(typeof(string));
                if (new Regex("[^0-9]+").IsMatch(text))
                {
                    e.CancelCommand();
                }
            }
            else
            {
                e.CancelCommand();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_vm != null)
            {
                _vm.OnChartResetRequired -= ResetChart;
            }

            MyPlot.MouseMove -= MyPlot_MouseMove;
            MyPlot.MouseLeave -= MyPlot_MouseLeave;

            if (DataContext is MainWindowViewModel vm)
                vm.Dispose();

            DataContext = null;
            _vm = null;

            base.OnClosed(e);
        }
    }
}