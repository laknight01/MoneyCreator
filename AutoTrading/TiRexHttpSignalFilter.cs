using MoneyCreator.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace MoneyCreator.AutoTrading
{
    public sealed class TiRexHttpSignalFilter : IMlNetSignalFilter
    {
        private readonly string _endpoint;
        private readonly Action<string> _log;
        private readonly Action<TiRexPredictionResult> _onPrediction;
        private readonly ConcurrentDictionary<string, TiRexPredictionResult> _lastPredictions = new ConcurrentDictionary<string, TiRexPredictionResult>();
        private readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2)
        };
        private void SavePrediction(string code, TiRexPredictionResult result)
        {
            if (string.IsNullOrWhiteSpace(code) || result == null)
                return;

            _lastPredictions[code] = result;
        }
        public TiRexHttpSignalFilter(string endpoint, Action<string> log, Action<TiRexPredictionResult> onPrediction)
        {
            _endpoint = string.IsNullOrWhiteSpace(endpoint)
                ? "http://127.0.0.1:5001/predict"
                : endpoint;

            _log = log;
            _onPrediction = onPrediction;
        }

        private TiRexPredictionResult GetLastPrediction(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return null;

            _lastPredictions.TryGetValue(code, out var result);
            return result;
        }

        public async Task<TiRexPredictionResult> PredictOnlyAsync(
      string code,
      IEnumerable<TrackedSymbol> minuteCandles)
        {
            try
            {
                var orderedMinutes = minuteCandles == null
                    ? new List<TrackedSymbol>()
                    : minuteCandles
                        .Where(x => x != null && x.LastPrice > 0)
                        .OrderBy(x => x.LastUpdated)
                        .ToList();

                var minutes = orderedMinutes
                    .Skip(Math.Max(0, orderedMinutes.Count - 64))
                    .ToList();

                if (minutes.Count < 20)
                {
                    var empty = TiRexPredictionResult.Unknown($"분봉 부족: {minutes.Count}");
                    SavePrediction(code, empty);
                    _onPrediction?.Invoke(empty);
                    return empty;
                }

                var request = new
                {
                    code = code,
                    candles = minutes.Select(x => new
                    {
                        time = x.LastUpdated,
                        open = x.ClosePrice > 0 ? x.ClosePrice : x.LastPrice,
                        high = x.HighPrice > 0 ? x.HighPrice : x.LastPrice,
                        low = x.LowPrice > 0 ? x.LowPrice : x.LastPrice,
                        close = x.LastPrice,
                        volume = Math.Abs(x.LastVolume)
                    }).ToList()
                };

                string json = JsonConvert.SerializeObject(request);

                using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
                using (var response = await _httpClient.PostAsync(_endpoint, content))
                {
                    string body = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        var fail = TiRexPredictionResult.Unknown($"HTTP {(int)response.StatusCode}");
                        SavePrediction(code, fail);
                        _onPrediction?.Invoke(fail);
                        return fail;
                    }

                    var dto = JsonConvert.DeserializeObject<TiRexResponseDto>(body);
                    var result = Convert(dto);

                    SavePrediction(code, result);
                    _onPrediction?.Invoke(result);

                    _log?.Invoke(
                        $"[TiRex] {code} / " +
                        $"dir={result.Direction}, ret={result.PredictedReturn:F2}%, " +
                        $"conf={result.Confidence:F2}, msg={result.Message}");

                    return result;
                }
            }
            catch (Exception ex)
            {
                var error = TiRexPredictionResult.Unknown(ex.Message);
                SavePrediction(code, error);
                _onPrediction?.Invoke(error);
                _log?.Invoke($"[TiRex 오류] {code} / {ex.Message}");
                return error;
            }
        }

        public async Task<TradeSignal> ReviewAsync(TradeSignal signal, FeatureSnapshot feature, AutoTradeSettings settings)
        {
            if (signal == null || feature == null)
                return signal;

            TiRexPredictionResult pred = GetLastPrediction(feature.Code);

            if (pred == null || pred.Direction == TiRexDirection.Unknown)
            {
                signal.TiRexDirection = pred == null ? null : pred.Direction.ToString();
                signal.TiRexConfidence = pred == null ? 0 : pred.Confidence;
                signal.TiRexPredictedReturn = pred == null ? 0 : pred.PredictedReturn;
                return signal;
            }

            signal.TiRexDirection = pred.Direction.ToString();
            signal.TiRexConfidence = pred.Confidence;
            signal.TiRexPredictedReturn = pred.PredictedReturn;

            if (signal.Action == TradeActionType.Buy)
            {
                if (pred.Direction == TiRexDirection.Down &&
                    pred.Confidence >= 0.60)
                {
                    var hold = TradeSignal.Hold(
                        signal.Code,
                        $"TiRex 매수차단: DOWN / conf={pred.Confidence:F2}, ret={pred.PredictedReturn:F2}%");

                    hold.StrategyName = "TiRexFilter";
                    hold.SnapshotFeature = null;
                    hold.Score = 0;

                    hold.TiRexDirection = pred.Direction.ToString();
                    hold.TiRexConfidence = pred.Confidence;
                    hold.TiRexPredictedReturn = pred.PredictedReturn;
                    return hold;
                }

                if (pred.Direction == TiRexDirection.Up &&
                    pred.Confidence >= 0.60)
                {
                    signal.Score = Math.Min(100, signal.Score + 5);
                    signal.Reason += $" | TiRex 매수보강: UP conf={pred.Confidence:F2}";
                }

                signal.SnapshotFeature = null;
                return signal;
            }

            if (signal.Action == TradeActionType.Sell)
            {
                bool isHardExit =
                    signal.StrategyName == "StopLoss" ||
                    signal.StrategyName == "EmergencyExit";

                if (isHardExit)
                {
                    signal.SnapshotFeature = null;
                    return signal;
                }

                if (pred.Direction == TiRexDirection.Up &&
                    pred.Confidence >= 0.65 &&
                    feature.ProfitRate > -0.4)
                {
                    var hold = TradeSignal.Hold(
                        signal.Code,
                        $"TiRex 매도보류: UP / conf={pred.Confidence:F2}, ret={pred.PredictedReturn:F2}%");

                    hold.StrategyName = "TiRexHold";
                    hold.SnapshotFeature = null;
                    hold.Score = 0;
                    hold.TiRexDirection = pred.Direction.ToString();
                    hold.TiRexConfidence = pred.Confidence;
                    hold.TiRexPredictedReturn = pred.PredictedReturn;
                    return hold;
                }

                if (pred.Direction == TiRexDirection.Down &&
                    pred.Confidence >= 0.60)
                {
                    signal.Score = Math.Min(100, signal.Score + 5);
                    signal.Reason += $" | TiRex 매도강화: DOWN conf={pred.Confidence:F2}";
                }

                signal.SnapshotFeature = null;
                return signal;
            }

            signal.SnapshotFeature = null;
            return signal;
        }

        private static TiRexPredictionResult Convert(TiRexResponseDto dto)
        {
            if (dto == null)
                return TiRexPredictionResult.Unknown("응답 없음");

            string dir = dto.Direction ?? "UNKNOWN";

            return new TiRexPredictionResult
            {
                Direction =
                    dir.Equals("UP", StringComparison.OrdinalIgnoreCase) ? TiRexDirection.Up :
                    dir.Equals("DOWN", StringComparison.OrdinalIgnoreCase) ? TiRexDirection.Down :
                    dir.Equals("FLAT", StringComparison.OrdinalIgnoreCase) ? TiRexDirection.Flat :
                    TiRexDirection.Unknown,
                PredictedReturn = dto.PredictedReturn,
                Confidence = dto.Confidence,
                Message = dto.Message
            };
        }

        private sealed class TiRexResponseDto
        {
            public string Direction { get; set; }
            public double PredictedReturn { get; set; }
            public double Confidence { get; set; }
            public string Message { get; set; }
        }
    }
}