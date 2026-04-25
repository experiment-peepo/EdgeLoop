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
    /// Simple file-based logger with different log levels.
    /// Supports a separate diagnostic log file that captures all log levels
    /// when DiagnosticMode is enabled, independent of the main log's MinimumLevel.
    /// </summary>
    public static class Logger {
        public static LogLevel MinimumLevel { get; set; } = LogLevel.Warning;

        /// <summary>
        /// When true, all log entries (including Debug/Info) are also written
        /// to the diagnostics.log file regardless of MinimumLevel.
        /// </summary>
        public static bool DiagnosticMode { get; set; } = false;

        internal static readonly object _lock = new object();
        internal static string _logFilePath;
        internal static int _consecutiveFailures = 0;
        internal const int MaxConsecutiveFailures = 10; // Stop trying file logging after this many failures

        private static readonly System.Collections.Concurrent.BlockingCollection<(string entry, bool writeToMain, bool writeToDiag)> _logQueue
            = new System.Collections.Concurrent.BlockingCollection<(string, bool, bool)>();

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
            foreach (var (logEntry, writeToMain, writeToDiag) in _logQueue.GetConsumingEnumerable()) {
                // Write to main log file (only if this entry meets MinimumLevel)
                if (writeToMain && _consecutiveFailures < MaxConsecutiveFailures) {
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

                // Write to diagnostic log file (captures everything when enabled)
                if (writeToDiag) {
                    try {
                        var diagPath = AppPaths.DiagnosticsLogFile;
                        lock (_lock) {
                            File.AppendAllText(diagPath, logEntry);
                        }
                    } catch (Exception diagEx) {
                        System.Diagnostics.Debug.WriteLine($"[LOGGER DIAG ERROR] Failed to write to diagnostics log: {diagEx.Message}");
                    }
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
            bool writeToMainLog = level >= MinimumLevel;
            bool writeToDiag = DiagnosticMode;

            // Skip entirely if neither destination wants this entry
            if (!writeToMainLog && !writeToDiag) return;

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
                
                // Add to background queue with both routing flags
                _logQueue.Add((logEntry, writeToMain: writeToMainLog, writeToDiag));
                
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
            RotateFileIfNeeded(_logFilePath, maxSizeBytes);
        }

        /// <summary>
        /// Checks if the diagnostic log file exceeds the maximum size and rotates it if necessary.
        /// Should be called at application startup when diagnostic mode is enabled.
        /// </summary>
        /// <param name="maxSizeBytes">The maximum size in bytes before rotation occurs</param>
        public static void CheckAndRotateDiagnosticsLogFile(long maxSizeBytes) {
            RotateFileIfNeeded(AppPaths.DiagnosticsLogFile, maxSizeBytes);
        }

        private static void RotateFileIfNeeded(string filePath, long maxSizeBytes) {
            try {
                lock (_lock) {
                    var logFile = new FileInfo(filePath);
                    if (logFile.Exists && logFile.Length > maxSizeBytes) {
                        try {
                            var oldLogPath = filePath + ".old";
                            
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



