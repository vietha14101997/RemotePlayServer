using System;

namespace RemotePlayServer.Utils
{
    /// <summary>
    /// Simple logging utility with log levels to reduce RAM usage.
    /// Set LogLevel to control verbosity.
    /// </summary>
    public static class Logger
    {
        /// <summary>
        /// Current log level. Messages below this level are ignored.
        /// </summary>
        public static LogLevel Level { get; set; } = LogLevel.Info;

        /// <summary>
        /// Log a debug message (very verbose, disabled by default).
        /// </summary>
        public static void Debug(string message)
        {
            if (Level <= LogLevel.Debug)
                Console.WriteLine($"[DEBUG] {message}");
        }

        /// <summary>
        /// Log an info message (normal verbosity).
        /// </summary>
        public static void Info(string message)
        {
            if (Level <= LogLevel.Info)
                Console.WriteLine(message);
        }

        /// <summary>
        /// Log a warning message.
        /// </summary>
        public static void Warn(string message)
        {
            if (Level <= LogLevel.Warning)
                Console.WriteLine($"[WARN] {message}");
        }

        /// <summary>
        /// Log an error message (always shown).
        /// </summary>
        public static void Error(string message)
        {
            Console.WriteLine($"[ERROR] {message}");
        }

        /// <summary>
        /// Log an error with exception.
        /// </summary>
        public static void Error(string message, Exception ex)
        {
            Console.WriteLine($"[ERROR] {message}: {ex.Message}");
        }
    }

    public enum LogLevel
    {
        Debug = 0,   // Most verbose - all messages
        Info = 1,    // Normal - skip debug messages
        Warning = 2, // Only warnings and errors
        Error = 3    // Only errors
    }
}
