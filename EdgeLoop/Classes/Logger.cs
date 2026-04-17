using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace EdgeLoop.Classes {
    public enum LogLevel {
        Debug = 0,
        Info = 1,
        Warning = 2,
        Error = 3,
        None = 4
    }

    /// <summary>
    /// Simple file-based logger with different log levels
    /// </summary>
    public static class Logger {
        public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;
        internal static readonly object _lock = new object();
        internal static string _logFilePath;
        internal static int _consecutiveFailures = 0;
        internal const int MaxConsecutiveFailures = 10; // Stop trying file logging after this many failures
        private static readonly System.Collections.Concurrent.BlockingCollection<string> _logQueue = new System.Collections.Concurrent.BlockingCollection<string>();

        static Logger() {
            _logFilePath = AppPaths.LogFile;
            
            // Start background logging thread
            var thread = new Thread(ProcessLogQueue) {
                IsBackground = true,
                Name = "LoggerBackgroundThread",
                Priority = ThreadPriority.BelowNormal
            };
            thread.Start();
        }

        private static void ProcessLogQueue() {
            foreach (var logEntry in _logQueue.GetConsumingEnumerable()) {
                if (_consecutiveFailures >= MaxConsecutiveFailures) continue;

                try {
                    lock (_lock) {
                        File.AppendAllText(_logFilePath, logEntry);
                        _consecutiveFailures = 0;
                    }
                } catch (Exception fileEx) {
                    _consecutiveFailures++;
                    System.Diagnostics.Debug.WriteLine($"[LOGGER FILE ERROR] Failed to write to log file ({_consecutiveFailures}/{MaxConsecutiveFailures}): {fileEx.Message}");
                }
            }
        }

        /// <summary>
        /// Log an error message
        /// </summary>
        public static void Error(string message, Exception exception = null) => Log(LogLevel.Error, null, message, exception);
        public static void Error(string context, string message, Exception exception = null) => Log(LogLevel.Error, context, message, exception);

        /// <summary>
        /// Log a warning message
        /// </summary>
        public static void Warning(string message, Exception exception = null) => Log(LogLevel.Warning, null, message, exception);
        public static void Warning(string context, string message, Exception exception = null) => Log(LogLevel.Warning, context, message, exception);

        /// <summary>
        /// Log an info message
        /// </summary>
        public static void Info(string message) => Log(LogLevel.Info, null, message, null);
        public static void Info(string context, string message) => Log(LogLevel.Info, context, message, null);

        /// <summary>
        /// Log a debug message
        /// </summary>
        public static void Debug(string message) => Log(LogLevel.Debug, null, message, null);
        public static void Debug(string context, string message) => Log(LogLevel.Debug, context, message, null);

        private static void Log(LogLevel level, string context, string message, Exception exception) {
            if (level < MinimumLevel) return;

            try {
                var levelStr = level.ToString().ToUpper();
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                var logEntry = string.IsNullOrEmpty(context) 
                    ? $"[{timestamp}] [{levelStr}] {message}"
                    : $"[{timestamp}] [{levelStr}] [{context}] {message}";
                
                if (exception != null) {
                    logEntry += $"\nException: {exception.GetType().Name}: {exception.Message}";
                    if (exception.StackTrace != null) {
                        logEntry += $"\nStack Trace: {exception.StackTrace}";
                    }
                }
                
                logEntry += Environment.NewLine;
                
                // Add to background queue instead of writing synchronously
                _logQueue.Add(logEntry);
                
                // Also output to Debug for immediate inspection in IDE
                System.Diagnostics.Debug.WriteLine(logEntry.TrimEnd());
            } catch (Exception ex) {
                // Last resort: try Debug.WriteLine without any formatting
                try {
                    System.Diagnostics.Debug.WriteLine($"[LOGGER CRITICAL ERROR] {message} | Exception: {ex.Message}");
                } catch {
                    // Absolutely nothing we can do at this point
                }
            }
        }
        /// <summary>
        /// Checks if the log file exceeds the maximum size and rotates it if necessary.
        /// Should be called at application startup.
        /// </summary>
        /// <param name="maxSizeBytes">The maximum size in bytes before rotation occurs</param>
        public static void CheckAndRotateLogFile(long maxSizeBytes) {
            try {
                lock (_lock) {
                    var logFile = new FileInfo(_logFilePath);
                    if (logFile.Exists && logFile.Length > maxSizeBytes) {
                        try {
                            var oldLogPath = _logFilePath + ".old";
                            
                            // Delete existing backup if it exists
                            if (File.Exists(oldLogPath)) {
                                File.Delete(oldLogPath);
                            }

                            // Move current log to backup
                            logFile.MoveTo(oldLogPath);

                            // Initial log entry in new file
                            Log(LogLevel.Info, null, $"Log file rotated. Previous log moved to {oldLogPath}", null);
                        } catch (Exception ex) {
                            System.Diagnostics.Debug.WriteLine($"[LOGGER ROTATION ERROR] Failed to rotate log file: {ex.Message}");
                        }
                    }
                }
            } catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine($"[LOGGER ROTATION ERROR] Error checking log file size: {ex.Message}");
            }
        }
    }
}



