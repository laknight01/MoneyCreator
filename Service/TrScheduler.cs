using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MoneyCreator.Service
{
    public class TrRequest
    {
        public string WindowId { get; set; }
        public string Code { get; set; }
        public int Next { get; set; }
        public string ScreenNo { get; set; }
        public int Priority { get; set; }
        public int Token { get; set; }
        public Dictionary<string, string> Inputs { get; set; } = new Dictionary<string, string>();
        public string RequestType { get; set; }
    }

    public class TrScheduler
    {
        private readonly Func<int> _getActiveTrCount;
        private string GetTokenKey(string windowId, string code, string requestType) => $"{windowId}_{code}_{requestType}";
        private string GetResponseKey(string windowId, string code, string requestType) => $"{windowId}_{code}_{requestType}";
        private bool IsCycleRequestType(string requestType)
        {
            return requestType == "PREV" ||
                   requestType == "MINUTE" ||
                   requestType == "RECENT_TICKS" ||
                   requestType == "ORDERS" ||
                   requestType == "PROGRAM_TRADE";
        }

        private readonly ConcurrentQueue<TrRequest> _continuationQueue = new ConcurrentQueue<TrRequest>();
        private readonly ConcurrentQueue<TrRequest> _highQueue = new ConcurrentQueue<TrRequest>();
        private readonly ConcurrentQueue<TrRequest> _mediumQueue = new ConcurrentQueue<TrRequest>();
        private readonly ConcurrentQueue<TrRequest> _lowQueue = new ConcurrentQueue<TrRequest>();
        private readonly ConcurrentDictionary<string, int> _latestToken = new ConcurrentDictionary<string, int>();
        private readonly ConcurrentDictionary<string, int> _activeCycleToken = new ConcurrentDictionary<string, int>();

        private readonly ConcurrentDictionary<string, byte> _activeWindows = new ConcurrentDictionary<string, byte>();

        private int _tokenSeed = 0;
        private bool _isProcessing = false;
        private readonly object _lock = new object();
        private readonly object _responseLock = new object();
        private readonly SemaphoreSlim _trSemaphore = new SemaphoreSlim(1, 1);
        private readonly Func<TrRequest, Task> _executeAction;

        private TaskCompletionSource<bool> _waitingResponseSource;
        private string _waitingResponseKey;

        public TrScheduler(Func<TrRequest, Task> executeAction, Func<int> getActiveTrCount)
        {
            _executeAction = executeAction ?? throw new ArgumentNullException(nameof(executeAction));
            _getActiveTrCount = getActiveTrCount ?? throw new ArgumentNullException(nameof(getActiveTrCount));
        }

        public void RegisterWindow(string windowId)
        {
            if (!string.IsNullOrWhiteSpace(windowId))
                _activeWindows.TryAdd(windowId, 0);
        }

        public void UnregisterWindow(string windowId)
        {
            if (string.IsNullOrWhiteSpace(windowId))
                return;

            _activeWindows.TryRemove(windowId, out _);

            string prefix = windowId + "_";

            foreach (var key in _latestToken.Keys.ToList())
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                    _latestToken.TryRemove(key, out _);
            }

            foreach (var key in _activeCycleToken.Keys.ToList())
            {
                int removed;
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                    _activeCycleToken.TryRemove(key, out removed);
            }
        }

        public void Enqueue(TrRequest req)
        {
            if (req == null)
                return;

            RegisterWindow(req.WindowId);
            string tokenKey = GetTokenKey(req.WindowId, req.Code, req.RequestType);

            lock (_lock)
            {
                if (req.Next == 2)
                {
                    int token;
                    if (_activeCycleToken.TryGetValue(tokenKey, out token))
                    {
                        req.Token = token;
                    }
                    else if (_latestToken.TryGetValue(tokenKey, out token))
                    {
                        req.Token = token;
                    }
                    else
                    {
                        req.Token = Interlocked.Increment(ref _tokenSeed);
                        _latestToken[tokenKey] = req.Token;
                    }

                    _continuationQueue.Enqueue(req);
                }
                else
                {
                    req.Token = Interlocked.Increment(ref _tokenSeed);
                    _latestToken[tokenKey] = req.Token;

                    if (req.Priority == 0) _highQueue.Enqueue(req);
                    else if (req.Priority == 1) _mediumQueue.Enqueue(req);
                    else _lowQueue.Enqueue(req);
                }

                if (!_isProcessing)
                {
                    _isProcessing = true;
                    Task.Run(RunQueueLoop);
                }
            }
        }

        // 키움 TR은 CommRqData 호출 성공 후에도 실제 응답(OnReceiveTrData)이 늦게 오므로
        // 아래 요청들은 응답 처리 완료 Notify가 올 때까지 다음 큐로 넘기지 않는다.
        private bool RequiresResponseWait(TrRequest req)
        {
            return req != null &&
                   (req.RequestType == "MINUTE" ||
                    req.RequestType == "RECENT_TICKS" ||
                    req.RequestType == "PREV" ||
                    req.RequestType == "ORDERS" ||
                    req.RequestType == "PROGRAM_TRADE" ||
                    req.RequestType == "HOLDINGS" ||
                    req.RequestType == "CASH" ||
                    req.RequestType == "UNFILLED");
        }

        private Task<bool> BeginResponseWait(TrRequest req)
        {
            lock (_responseLock)
            {
                _waitingResponseKey = GetResponseKey(req.WindowId, req.Code, req.RequestType);
                _waitingResponseSource = new TaskCompletionSource<bool>();
                return _waitingResponseSource.Task;
            }
        }

        private void ClearResponseWait(TrRequest req)
        {
            lock (_responseLock)
            {
                string key = GetResponseKey(req.WindowId, req.Code, req.RequestType);

                if (_waitingResponseKey == key)
                {
                    _waitingResponseKey = null;
                    _waitingResponseSource = null;
                }
            }
        }

        // KiwoomApiService의 OnReceiveTrData 처리 완료 지점에서 호출한다.
        public void NotifyResponseReceived(string windowId, string code, string requestType)
        {
            string key = GetResponseKey(windowId, code, requestType);

            lock (_responseLock)
            {
                if (_waitingResponseKey == key && _waitingResponseSource != null)
                    _waitingResponseSource.TrySetResult(true);
            }
        }

        // CommRqData가 음수로 실패하면 OnReceiveTrData가 오지 않으므로 반드시 실패 완료 처리한다.
        public void NotifyResponseFailed(string windowId, string code, string requestType, int errorCode)
        {
            string key = GetResponseKey(windowId, code, requestType);

            lock (_responseLock)
            {
                if (_waitingResponseKey == key && _waitingResponseSource != null)
                    _waitingResponseSource.TrySetResult(false);
            }
        }

        private async Task<bool> WaitForResponseOrTimeout(Task<bool> waitTask, TrRequest req)
        {
            if (waitTask == null)
                return true;

            // 키움 응답 이벤트가 누락되었을 때 스케줄러가 영구 정지하지 않도록 안전장치.
            // 정상 상황에서는 OnReceiveTrData 처리 완료 후 즉시 풀린다.
            Task completed = await Task.WhenAny(waitTask, Task.Delay(10000));
            return completed == waitTask && waitTask.Result;
        }

        private async Task RunQueueLoop()
        {
            int emptyCount = 0;

            try
            {
                while (true)
                {
                    bool didWork = false;

                    // next=2 연속조회는 현재 히스토리 사이클 내부 후속 페이지다.
                    // 일반 high/medium/low 큐보다 반드시 먼저 처리해서 사이클 중간에 다른 TR이 끼지 않게 한다.
                    didWork |= await ProcessBatch(_continuationQueue, 1);

                    if (!didWork)
                        didWork |= await ProcessBatch(_highQueue, 1);

                    if (!didWork)
                        didWork |= await ProcessBatch(_mediumQueue, 1);

                    if (!didWork)
                        didWork |= await ProcessBatch(_lowQueue, 1);

                    if (!didWork)
                    {
                        await Task.Delay(50);

                        if (++emptyCount > 20)
                        {
                            if (_continuationQueue.IsEmpty &&
                                _highQueue.IsEmpty &&
                                _mediumQueue.IsEmpty &&
                                _lowQueue.IsEmpty &&
                                _getActiveTrCount() == 0)
                            {
                                break;
                            }
                        }
                    }
                    else
                    {
                        emptyCount = 0;
                    }
                }
            }
            finally
            {
                lock (_lock)
                {
                    _isProcessing = false;

                    // finally와 Enqueue 사이의 아주 짧은 타이밍에 새 요청이 들어온 경우 다시 루프를 켠다.
                    if (!_continuationQueue.IsEmpty || !_highQueue.IsEmpty || !_mediumQueue.IsEmpty || !_lowQueue.IsEmpty)
                    {
                        _isProcessing = true;
                        Task.Run(RunQueueLoop);
                    }
                }
            }
        }

        private string GetCycleKey(TrRequest req)
        {
            if (req == null)
                return string.Empty;

            return GetTokenKey(req.WindowId, req.Code, req.RequestType);
        }

        private void BeginCycleIfNeeded(TrRequest req)
        {
            if (req == null || !IsCycleRequestType(req.RequestType))
                return;

            string key = GetCycleKey(req);
            if (string.IsNullOrWhiteSpace(key))
                return;

            _activeCycleToken[key] = req.Token;
        }

        private void EndCycleIfNoContinuation(TrRequest req)
        {
            if (req == null || !IsCycleRequestType(req.RequestType))
                return;

            string key = GetCycleKey(req);
            if (string.IsNullOrWhiteSpace(key))
                return;

            foreach (var next in _continuationQueue.ToArray())
            {
                if (next == null)
                    continue;

                if (next.Token == req.Token && GetCycleKey(next) == key)
                    return;
            }

            int removed;
            _activeCycleToken.TryRemove(key, out removed);
        }

        private async Task<bool> ProcessBatch(ConcurrentQueue<TrRequest> queue, int weight)
        {
            bool processedAny = false;

            
            for (int i = 0; i < weight; i++)
            {
                if (!queue.TryDequeue(out var req))
                    continue;

                if (!IsValid(req))
                    continue;

                BeginCycleIfNeeded(req);

                bool waitResponse = RequiresResponseWait(req);
                Task<bool> responseWaitTask = null;

                await _trSemaphore.WaitAsync();

                try
                {
                    if (waitResponse)
                        responseWaitTask = BeginResponseWait(req);

                    await _executeAction(req);

                    if (waitResponse)
                    {
                        await WaitForResponseOrTimeout(responseWaitTask, req);

                        // 응답이 실제로 온 뒤 다음 TR을 바로 쏘지 않도록 최소 숨 고르기.
                        await Task.Delay(GetPostResponseDelay(req));
                    }
                    else
                    {
                        int winCount = _activeWindows.Count;
                        int dynamicDelay = 350 + (Math.Max(0, winCount - 1) * 100);
                        await Task.Delay(dynamicDelay);
                    }
                }
                finally
                {
                    if (waitResponse)
                        ClearResponseWait(req);

                    EndCycleIfNoContinuation(req);

                    _trSemaphore.Release();
                }

                processedAny = true;
                //Debug.WriteLine($"ENQUEUE {req.Inputs} {req.Code} {req.WindowId}");
            }

            return processedAny;
        }

        private int GetPostResponseDelay(TrRequest req)
        {
            if (req == null)
                return 500;

            if (req.RequestType == "ORDERS")
                return req.Next == 2 ? 800 : 1200;

            if (req.RequestType == "HOLDINGS" ||
                req.RequestType == "CASH" ||
                req.RequestType == "UNFILLED")
                return 800;

            if (req.RequestType == "MINUTE")
                return req.Next == 2 ? 350 : 500;

            if (req.RequestType == "RECENT_TICKS" ||
                req.RequestType == "PREV")
                return req.Next == 2 ? 300 : 500;

            if (req.RequestType == "PROGRAM_TRADE")
                return req.Next == 2 ? 500 : 800;

            return 500;
        }

        private bool IsValid(TrRequest req)
        {
            if (req == null)
                return false;

            if (IsCycleRequestType(req.RequestType))
            {
                string tokenKey = GetTokenKey(req.WindowId, req.Code, req.RequestType);
                int activeToken;
                if (_activeCycleToken.TryGetValue(tokenKey, out activeToken) && activeToken == req.Token)
                    return true;

                int latestToken;
                return _latestToken.TryGetValue(tokenKey, out latestToken) && latestToken == req.Token;
            }

            return true;
        }
    }
}
