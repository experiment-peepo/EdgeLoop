using System;
using System.Windows;
using EdgeLoop.ViewModels;

namespace EdgeLoop.Classes
{
    /// <summary>
    /// Centralized error handling policy for consistent error management
    /// </summary>
    public static class ErrorHandler
    {

        /// <summary>
        /// Handles an exception with consistent logging and optional user notification
        /// </summary>
        /// <param name="ex">The exception</param>
        /// <param name="context">Where the error occurred (e.g., "LoadVideo", "SaveSettings")</param>
        /// <param name="severity">Error severity level</param>
        /// <param name="notifyUser">If true, shows a user-friendly message via the UI status bar</param>
        public static void Handle(Exception ex, string context,
            ErrorSeverity severity = ErrorSeverity.Warning,
            bool notifyUser = false)
        {

            // Always log
            switch (severity)
            {
                case ErrorSeverity.Debug:
                    Logger.Debug(context, ex.Message);
                    break;
                case ErrorSeverity.Info:
                    Logger.Debug(context, ex.Message);
                    break;
                case ErrorSeverity.Warning:
                    Logger.Warning(context, ex.Message);
                    break;
                case ErrorSeverity.Error:
                case ErrorSeverity.Critical:
                    Logger.Error(context, ex.Message, ex);
                    break;
            }

            // Optional user notification via Status Bar
            if (notifyUser && Application.Current?.Dispatcher != null)
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ServiceContainer.TryGet<LauncherViewModel>(out var vm))
                    {
                        string userMsg = GetUserFriendlyMessage(ex, context);
                        vm.SetStatusMessage($"Error: {userMsg}", StatusMessageType.Error);
                    }
                }));
            }
        }

        /// <summary>
        /// Handles an exception and rethrows if critical
        /// </summary>
        public static void HandleOrThrow(Exception ex, string context,
            ErrorSeverity severity = ErrorSeverity.Error)
        {
            Handle(ex, context, severity, severity >= ErrorSeverity.Error);

            if (severity == ErrorSeverity.Critical)
            {
                throw new ApplicationException($"Critical error in {context}", ex);
            }
        }

        /// <summary>
        /// Converts technical exceptions to user-friendly messages for the status bar
        /// </summary>
        private static string GetUserFriendlyMessage(Exception ex, string context)
        {
            if (ex is OperationCanceledException) return "Operation cancelled";
            if (ex is System.IO.FileNotFoundException) return "File not found";
            if (ex is System.IO.DirectoryNotFoundException) return "Folder not found";
            if (ex is UnauthorizedAccessException) return "Access denied (check permissions)";
            if (ex is System.Net.Http.HttpRequestException) return "Network connection error";
            if (ex is TimeoutException) return "Operation timed out";

            return $"Failed to {context.ToLower()}";
        }
    }

    public enum ErrorSeverity
    {
        Debug = 0,
        Info = 1,
        Warning = 2,
        Error = 3,
        Critical = 4
    }
}

