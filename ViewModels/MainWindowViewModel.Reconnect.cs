using MoneyCreator.Models;
using MoneyCreator.Service;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace MoneyCreator.ViewModels
{
    public partial class MainWindowViewModel
    {
        private sealed class ReconnectWindowSnapshot
        {
            public int Index { get; set; }
            public string Code { get; set; }
            public string Name { get; set; }
            public string Account { get; set; }
            public bool Auto { get; set; }
            public bool Small { get; set; }
            public bool Replay { get; set; }
            public string LogMode { get; set; }
            public double LastPrice { get; set; }
        }

        private static readonly object _kiwoomReconnectSyncRoot = new object();
        private static int _kiwoomReconnectSnapshotInProgress;
        private static int _kiwoomReconnectRestoreInProgress;
        private static readonly string _kiwoomReconnectStatePath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ReconnectState.ini");

        private void Kiwoom_OnConnectionLost()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(() =>
                {
                    if (Interlocked.CompareExchange(ref _kiwoomReconnectSnapshotInProgress, 1, 0) != 0)
                        return;

                    try
                    {
                        SaveAllWindowReconnectState();
                        ResetAllWindowsForReconnect();
                    }
                    catch (Exception ex)
                    {
                        AddLog($"[재연결] 상태 저장/초기화 실패: {ex.Message}");
                    }
                }));
        }

        private void Kiwoom_OnReconnected()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(async () =>
                {
                    if (Interlocked.CompareExchange(ref _kiwoomReconnectRestoreInProgress, 1, 0) != 0)
                        return;

                    try
                    {
                        await RestoreAllWindowsFromReconnectStateAsync();
                    }
                    catch (Exception ex)
                    {
                        AddLog($"[재연결] 상태 복구 실패: {ex.Message}");
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _kiwoomReconnectRestoreInProgress, 0);
                        Interlocked.Exchange(ref _kiwoomReconnectSnapshotInProgress, 0);
                    }
                }));
        }

        private static List<MainWindowViewModel> GetOpenMainWindowViewModels()
        {
            if (Application.Current == null)
                return new List<MainWindowViewModel>();

            return Application.Current.Windows
                .Cast<Window>()
                .Select(w => w.DataContext as MainWindowViewModel)
                .Where(vm => vm != null)
                .ToList();
        }

        private static void SaveAllWindowReconnectState()
        {
            var windows = GetOpenMainWindowViewModels();
            var snapshots = new List<ReconnectWindowSnapshot>();

            for (int i = 0; i < windows.Count; i++)
            {
                var snapshot = windows[i].CreateReconnectSnapshot(i + 1);
                snapshots.Add(snapshot);
            }

            SaveReconnectStateIni(snapshots);

            foreach (var vm in windows)
                vm.AddLog($"[재연결] 창 상태 저장 완료: {Path.GetFileName(_kiwoomReconnectStatePath)}");
        }

        private ReconnectWindowSnapshot CreateReconnectSnapshot(int index)
        {
            return new ReconnectWindowSnapshot
            {
                Index = index,
                Code = Context == null ? string.Empty : (Context.Code ?? string.Empty),
                Name = Context == null ? string.Empty : (Context.Name ?? string.Empty),
                Account = SelectedAccount ?? string.Empty,
                Auto = IsAutoTradingEnabled,
                Small = IsAutoTradingSmallScale,
                Replay = IsReplayMode,
                LogMode = SelectedAutoTradeLogMode ?? "전체",
                LastPrice = Context == null ? 0 : Context.LastPrice
            };
        }

        private static void ResetAllWindowsForReconnect()
        {
            foreach (var vm in GetOpenMainWindowViewModels())
                vm.ResetWindowRuntimeForReconnect("키움 연결 끊김");
        }

        private void ResetWindowRuntimeForReconnect(string reason)
        {
            if (IsAutoTradingEnabled)
                IsAutoTradingEnabled = false;

            if (IsReplayMode && _marketApi is LocalReplayApiService replayApi)
                replayApi.PauseReplay(WindowId);

            string oldCode = Context == null ? string.Empty : Context.Code;
            if (!string.IsNullOrWhiteSpace(oldCode))
                MarkAutoTradeDataNotReady(oldCode);
            else
                MarkAutoTradeDataNotReady();

            _autoTradeController?.Reset();
            ResetRealtimeTickWindow();
            ResetTradeAnalysisCard();
            AutoTradeAnalysisLogs.Clear();
            ClearCurrentWindowAccountRuntimeState(reason);

            AccountHoldings.Clear();
            OrderHistory.Clear();
            Context.CurrentHold = null;
            _lastAutoTradeHoldingCodes.Clear();
            AskList.Clear();
            BidList.Clear();
            _tradeMarkerKeys.Clear();
            _realTimeBuffer.Clear();
            _minuteTickBuffer.Clear();
            _minuteHistoryBuffer.Clear();

            HoldingQty = 0;
            CurrentBalance = 0;
            TotalAskQty = 0;
            TotalBidQty = 0;
            _latestHogaForAutoTrade = null;
            _lastHogaUiUpdateTime = DateTime.MinValue;
            _isHistoryBindingComplete = false;

            ClearBuffer();
            ResetInitialMainLoadingState();
            OnChartResetRequired?.Invoke();

            AddLog($"[재연결] 창 런타임 완전 초기화 완료: {reason}");
            AddAutoTradeAnalysisLog($"[재연결] 연결 복구 전 주문/분석 상태 초기화: {reason}");
        }

        private static async Task RestoreAllWindowsFromReconnectStateAsync()
        {
            var snapshots = LoadReconnectStateIni();
            var windows = GetOpenMainWindowViewModels();

            if (snapshots.Count == 0)
            {
                foreach (var vm in windows)
                    vm.AddLog("[재연결] 복구할 ReconnectState.ini 상태가 없습니다.");
                return;
            }

            int count = Math.Min(snapshots.Count, windows.Count);

            for (int i = 0; i < count; i++)
                await windows[i].RestoreWindowAfterReconnectAsync(snapshots[i]);

            if (snapshots.Count > windows.Count && windows.Count > 0)
                windows[0].AddLog($"[재연결] 저장 창 {snapshots.Count}개 중 현재 창 {windows.Count}개만 복구했습니다.");
        }

        private async Task RestoreWindowAfterReconnectAsync(ReconnectWindowSnapshot snapshot)
        {
            if (snapshot == null)
                return;

            AddLog($"[재연결] 창 상태 복구 시작: index={snapshot.Index}, code={snapshot.Code}, auto={snapshot.Auto}, small={snapshot.Small}, replay={snapshot.Replay}");

            if (!string.IsNullOrWhiteSpace(snapshot.LogMode))
                SelectedAutoTradeLogMode = snapshot.LogMode;

            if (!string.IsNullOrWhiteSpace(snapshot.Account))
                SelectedAccount = snapshot.Account;

            IsAutoTradingSmallScale = snapshot.Small;

            if (IsReplayMode != snapshot.Replay)
                IsReplayMode = snapshot.Replay;

            if (!string.IsNullOrWhiteSpace(snapshot.Code))
            {
                var symbol = new Symbol
                {
                    Code = snapshot.Code,
                    Name = string.IsNullOrWhiteSpace(snapshot.Name) ? snapshot.Code : snapshot.Name,
                    LastPrice = snapshot.LastPrice <= 0 ? string.Empty : snapshot.LastPrice.ToString(CultureInfo.InvariantCulture)
                };

                await LoadSymbolDataAsync(symbol, waitHoldingLoad: false);
            }

            if (!snapshot.Replay)
                await RequestAccountReloadAfterReconnectAsync();

            if (snapshot.Auto && !string.IsNullOrWhiteSpace(snapshot.Code))
            {
                IsAutoTradingEnabled = true;
                AddAutoTradeAnalysisLog($"[재연결] Auto 상태 복원: ON / {snapshot.Code}");
            }
            else
            {
                IsAutoTradingEnabled = false;
                AddAutoTradeAnalysisLog($"[재연결] Auto 상태 복원: OFF / {snapshot.Code}");
            }

            AddLog($"[재연결] 창 상태 복구 완료: {snapshot.Code}");
        }

        private async Task RequestAccountReloadAfterReconnectAsync()
        {
            if (_marketApi == null || _marketApi is LocalReplayApiService)
                return;

            if (string.IsNullOrWhiteSpace(SelectedAccount))
            {
                FetchAccountList();
                if (string.IsNullOrWhiteSpace(SelectedAccount))
                {
                    AddLog("[재연결] 계좌번호가 없어 계좌 재조회를 건너뜁니다.");
                    return;
                }
            }

            ResetInitialMainLoadingState();
            BeginInitialMainLoading();

            _marketApi.RequestAccountHoldings(WindowId, SelectedAccount, ScreenNo);
            await Task.Delay(300);

            _marketApi.RequestCashInfo(WindowId, SelectedAccount, ScreenNo);
            await Task.Delay(300);

            _marketApi.RequestUnfilledOrders("COMMON", SelectedAccount, ScreenNo);
            await Task.Delay(300);

            _marketApi.RequestOrderHistory(WindowId, SelectedAccount, ScreenNo);

            AddLog("[재연결] 계좌/예수금/미체결/주문내역 재조회 요청 완료");
        }

        private static void SaveReconnectStateIni(List<ReconnectWindowSnapshot> snapshots)
        {
            var sb = new StringBuilder();
            sb.AppendLine("; MoneyCreator reconnect state");
            sb.AppendLine($"SavedAt={DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Count={snapshots.Count}");
            sb.AppendLine();

            foreach (var snapshot in snapshots)
            {
                sb.AppendLine($"[Window{snapshot.Index}]");
                sb.AppendLine($"Index={snapshot.Index}");
                sb.AppendLine($"Code={EscapeIniValue(snapshot.Code)}");
                sb.AppendLine($"Name={EscapeIniValue(snapshot.Name)}");
                sb.AppendLine($"Account={EscapeIniValue(snapshot.Account)}");
                sb.AppendLine($"Auto={snapshot.Auto}");
                sb.AppendLine($"Small={snapshot.Small}");
                sb.AppendLine($"Replay={snapshot.Replay}");
                sb.AppendLine($"LogMode={EscapeIniValue(snapshot.LogMode)}");
                sb.AppendLine($"LastPrice={snapshot.LastPrice.ToString(CultureInfo.InvariantCulture)}");
                sb.AppendLine();
            }

            File.WriteAllText(_kiwoomReconnectStatePath, sb.ToString(), Encoding.UTF8);
        }

        private static List<ReconnectWindowSnapshot> LoadReconnectStateIni()
        {
            var result = new List<ReconnectWindowSnapshot>();

            if (!File.Exists(_kiwoomReconnectStatePath))
                return result;

            ReconnectWindowSnapshot current = null;

            foreach (var rawLine in File.ReadAllLines(_kiwoomReconnectStatePath, Encoding.UTF8))
            {
                string line = (rawLine ?? string.Empty).Trim();
                if (line.Length == 0 || line.StartsWith(";"))
                    continue;

                if (line.StartsWith("[Window", StringComparison.OrdinalIgnoreCase) && line.EndsWith("]"))
                {
                    current = new ReconnectWindowSnapshot();
                    result.Add(current);
                    continue;
                }

                if (current == null)
                    continue;

                int idx = line.IndexOf('=');
                if (idx <= 0)
                    continue;

                string key = line.Substring(0, idx).Trim();
                string value = UnescapeIniValue(line.Substring(idx + 1).Trim());

                switch (key)
                {
                    case "Index":
                        int parsedIndex;
                        if (int.TryParse(value, out parsedIndex))
                            current.Index = parsedIndex;
                        break;
                    case "Code":
                        current.Code = value;
                        break;
                    case "Name":
                        current.Name = value;
                        break;
                    case "Account":
                        current.Account = value;
                        break;
                    case "Auto":
                        current.Auto = string.Equals(value, "True", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "Small":
                        current.Small = string.Equals(value, "True", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "Replay":
                        current.Replay = string.Equals(value, "True", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "LogMode":
                        current.LogMode = value;
                        break;
                    case "LastPrice":
                        double parsedPrice;
                        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out parsedPrice))
                            current.LastPrice = parsedPrice;
                        break;
                }
            }

            return result.OrderBy(x => x.Index).ToList();
        }

        private static string EscapeIniValue(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return value
                .Replace("\\", "\\\\")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

        private static string UnescapeIniValue(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return value
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\\\", "\\");
        }
    }
}
