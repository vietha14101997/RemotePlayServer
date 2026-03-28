#nullable enable
using System;
using System.IO;

namespace RemotePlayServer.Core
{
    /// <summary>
    /// Simple logging utility with log levels and optional file output.
    /// Set LogLevel to control verbosity. Call InitFileLogging() to enable file logging.
    /// </summary>
    public static class Logger
    {
        /// <summary>
        /// Current log level. Messages below this level are ignored.
        /// </summary>
        public static LogLevel Level { get; set; } = LogLevel.Info;

        public static event Action<DateTime, string, LogLevel>? OnLogEntry;

        private static StreamWriter? _fileWriter;
        private static readonly object _fileLock = new object();

        /// <summary>
        /// Initialize file logging. Logs will be written to both console and file.
        /// Creates a new log file per session with rotation (keeps last 5 files).
        /// </summary>
        public static void InitFileLogging(string? logDirectory = null)
        {
            try
            {
                var dir = logDirectory ?? Path.Combine(AppContext.BaseDirectory, "logs");
                Directory.CreateDirectory(dir);

                // Rotate: keep last 5 log files
                var existing = Directory.GetFiles(dir, "server_*.log");
                Array.Sort(existing);
                for (int i = 0; i < existing.Length - 4; i++)
                {
                    try { File.Delete(existing[i]); } catch { }
                }

                var fileName = $"server_{DateTime.Now:yyyyMMdd_HHmmss}.log";
                var filePath = Path.Combine(dir, fileName);
                _fileWriter = new StreamWriter(filePath, append: true) { AutoFlush = true };

                Info($"[Logger] File logging started: {filePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to init file logging: {ex.Message}");
            }
        }

        /// <summary>
        /// Log a debug message (very verbose, disabled by default).
        /// </summary>
        public static void Debug(string message)
        {
            if (Level <= LogLevel.Debug)
                WriteLog($"[DEBUG] {message}", LogLevel.Debug);
        }

        public static void Info(string message)
        {
            if (Level <= LogLevel.Info)
                WriteLog(message, LogLevel.Info);
        }

        public static void Warn(string message)
        {
            if (Level <= LogLevel.Warning)
                WriteLog($"[WARN] {message}", LogLevel.Warning);
        }

        public static void Error(string message)
        {
            WriteLog($"[ERROR] {message}", LogLevel.Error);
        }

        public static void Error(string message, Exception ex)
        {
            WriteLog($"[ERROR] {message}: {ex.Message}", LogLevel.Error);
        }

        /// <summary>
        /// Flush and close file logging.
        /// </summary>
        public static void Shutdown()
        {
            lock (_fileLock)
            {
                _fileWriter?.Flush();
                _fileWriter?.Dispose();
                _fileWriter = null;
            }
        }

        private static void WriteLog(string message, LogLevel level = LogLevel.Info)
        {
            var now = DateTime.Now;
            var timestamped = $"{now:HH:mm:ss.fff} {message}";
            Console.WriteLine(timestamped);

            try { OnLogEntry?.Invoke(now, message, level); } catch { }

            if (_fileWriter != null)
            {
                lock (_fileLock)
                {
                    try { _fileWriter?.WriteLine(timestamped); }
                    catch { /* Don't let file errors crash the server */ }
                }
            }
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
