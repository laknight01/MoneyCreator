using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MoneyCreator.Service
{
    /// <summary>
    /// 자동매매 분석 로그 파일 저장 전용 유틸.
    /// UI 스레드에서 직접 File I/O를 하지 않기 위해 로그를 큐에 넣고,
    /// 백그라운드에서 1초 단위 또는 일정 수량 단위로 묶어서 저장한다.
    /// </summary>
    public static class AutoTradeAnalysisLogFileWriter
    {
        private const string LogDirectory = @"F:\Project\LOG";
        private const int FlushIntervalMilliseconds = 1000;
        private const int ImmediateFlushThreshold = 100;

        private static readonly ConcurrentQueue<PendingLogLine> _queue = new ConcurrentQueue<PendingLogLine>();
        private static readonly object _filePathResolveLock = new object();
        private static readonly Dictionary<string, string> _resolvedFilePathByBasePath =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _reservedFilePaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Timer _flushTimer;
        private static int _queuedCount;
        private static int _flushRunning;
        private static int _immediateFlushQueued;

        static AutoTradeAnalysisLogFileWriter()
        {
            _flushTimer = new Timer(_ => FlushAll(), null, FlushIntervalMilliseconds, FlushIntervalMilliseconds);
        }

        public static void Enqueue(string code, string name, string message)
        {
            Enqueue(code, name, string.Empty, DateTime.MinValue, message);
        }

        public static void Enqueue(string code, string name, string mode, DateTime fallbackDate, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            string resolvedCode = ResolveCode(code, message);
            if (string.IsNullOrWhiteSpace(resolvedCode))
                return;

            string resolvedName = string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();
            string resolvedMode = ResolveMode(mode);
            DateTime logDate = ResolveLogDate(message, fallbackDate);
            string filePath = BuildFilePath(resolvedName, resolvedCode, logDate, resolvedMode);
            string line = NormalizeLine(message);

            _queue.Enqueue(new PendingLogLine
            {
                FilePath = filePath,
                Line = line
            });

            int count = Interlocked.Increment(ref _queuedCount);
            if (count >= ImmediateFlushThreshold)
                QueueImmediateFlush();
        }

        public static void FlushAll()
        {
            if (Interlocked.Exchange(ref _flushRunning, 1) == 1)
                return;

            try
            {
                Interlocked.Exchange(ref _immediateFlushQueued, 0);

                var linesByFile = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                int drained = 0;

                while (_queue.TryDequeue(out var item))
                {
                    drained++;

                    if (string.IsNullOrWhiteSpace(item.FilePath) || string.IsNullOrWhiteSpace(item.Line))
                        continue;

                    if (!linesByFile.TryGetValue(item.FilePath, out var lines))
                    {
                        lines = new List<string>();
                        linesByFile[item.FilePath] = lines;
                    }

                    lines.Add(item.Line);
                }

                if (drained > 0)
                    Interlocked.Add(ref _queuedCount, -drained);

                if (linesByFile.Count == 0)
                    return;

                Directory.CreateDirectory(LogDirectory);

                foreach (var pair in linesByFile)
                {
                    try
                    {
                        File.AppendAllLines(pair.Key, pair.Value, Encoding.UTF8);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[자동매매 로그 파일 저장 실패] {pair.Key} / {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[자동매매 로그 파일 Flush 실패] " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _flushRunning, 0);
            }
        }

        private static void QueueImmediateFlush()
        {
            if (Interlocked.Exchange(ref _immediateFlushQueued, 1) == 1)
                return;

            ThreadPool.QueueUserWorkItem(_ => FlushAll());
        }

        private static string BuildFilePath(string name, string code, DateTime logDate, string mode)
        {
            string safeCode = SanitizeFileNamePart(code);
            string safeName = SanitizeFileNamePart(name);
            string safeMode = SanitizeFileNamePart(ResolveMode(mode));

            string prefix = string.IsNullOrWhiteSpace(safeName)
                ? safeCode
                : safeName + safeCode;

            string fileName = $"{prefix}_{logDate:yyyyMMdd}_{safeMode}.txt";
            string basePath = Path.Combine(LogDirectory, fileName);
            return ResolveUniqueFilePathForCurrentSession(basePath);
        }

        private static string ResolveUniqueFilePathForCurrentSession(string basePath)
        {
            if (string.IsNullOrWhiteSpace(basePath))
                return basePath;

            lock (_filePathResolveLock)
            {
                if (_resolvedFilePathByBasePath.TryGetValue(basePath, out string resolvedPath))
                    return resolvedPath;

                string directory = Path.GetDirectoryName(basePath);
                string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(basePath);
                string extension = Path.GetExtension(basePath);

                if (string.IsNullOrWhiteSpace(directory))
                    directory = LogDirectory;

                string candidatePath = basePath;
                int suffix = 0;

                while (File.Exists(candidatePath) || _reservedFilePaths.Contains(candidatePath))
                {
                    suffix++;
                    candidatePath = Path.Combine(
                        directory,
                        $"{fileNameWithoutExtension}_{suffix}{extension}");
                }

                _resolvedFilePathByBasePath[basePath] = candidatePath;
                _reservedFilePaths.Add(candidatePath);
                return candidatePath;
            }
        }

        private static string ResolveCode(string code, string message)
        {
            if (!string.IsNullOrWhiteSpace(code))
                return code.Trim();

            var match = Regex.Match(message ?? string.Empty, @"종목=([A-Za-z0-9]+)");
            if (match.Success)
                return match.Groups[1].Value.Trim();

            return string.Empty;
        }

        private static DateTime ResolveLogDate(string message, DateTime fallbackDate)
        {
            var match = Regex.Match(message ?? string.Empty, @"시간=(\d{4})-(\d{2})-(\d{2})");
            if (match.Success)
            {
                if (int.TryParse(match.Groups[1].Value, out int year) &&
                    int.TryParse(match.Groups[2].Value, out int month) &&
                    int.TryParse(match.Groups[3].Value, out int day))
                {
                    try
                    {
                        return new DateTime(year, month, day);
                    }
                    catch
                    {
                    }
                }
            }

            if (fallbackDate != DateTime.MinValue)
                return fallbackDate.Date;

            return DateTime.Now.Date;
        }

        private static string ResolveMode(string mode)
        {
            if (string.Equals(mode, "Replay", StringComparison.OrdinalIgnoreCase))
                return "Replay";

            return "Real";
        }

        private static string SanitizeFileNamePart(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string result = value.Trim();
            foreach (char c in Path.GetInvalidFileNameChars())
                result = result.Replace(c, '_');

            return result.Replace(" ", string.Empty);
        }

        private static string NormalizeLine(string message)
        {
            if (message == null)
                return string.Empty;

            return message.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        }

        private sealed class PendingLogLine
        {
            public string FilePath { get; set; }
            public string Line { get; set; }
        }
    }
}
