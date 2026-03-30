using System;
using System.IO;
using System.Text;

namespace DataUtility
{
    /// <summary>
    /// Thread-safe audit logger. Writes to a dated log file under the configured log folder.
    /// </summary>
    public class AuditLogger
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        private static AuditLogger _instance;
        public static AuditLogger Instance => _instance ??= new AuditLogger();

        private readonly object _lock = new object();
        private string _logFilePath;
        private bool _enabled;

        // ── Constructor ───────────────────────────────────────────────────────
        private AuditLogger()
        {
            Initialise();
        }

        public void Initialise()
        {
            var cfg = AppConfig.Instance;
            _enabled = cfg.AuditLogEnabled;

            if (!_enabled) return;

            string folder = Path.IsPathRooted(cfg.AuditLogFolder)
                ? cfg.AuditLogFolder
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, cfg.AuditLogFolder);

            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            string fileName = cfg.AuditLogFileName
                .Replace("{DATE}", DateTime.Now.ToString("yyyyMMdd"));

            _logFilePath = Path.Combine(folder, fileName);

            // Write session header
            WriteRaw(BuildSeparator('='));
            WriteRaw($"  {cfg.ApplicationName}  v{cfg.Version}");
            WriteRaw($"  Session started : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            WriteRaw($"  Log Level       : {cfg.LogLevel}");
            WriteRaw(BuildSeparator('='));
        }

        // ── Public API ────────────────────────────────────────────────────────

        public void Info(string message) => Write("INFO ", message);
        public void Debug(string message) => Write("DEBUG", message);
        public void Warning(string message) => Write("WARN ", message);
        public void Error(string message) => Write("ERROR", message);
        public void Error(string message, Exception ex) => Write("ERROR", $"{message} | Exception: {ex}");

        public void Section(string title)
        {
            WriteRaw(string.Empty);
            WriteRaw(BuildSeparator('-'));
            WriteRaw($"  {title}");
            WriteRaw(BuildSeparator('-'));
        }

        public void SessionEnd()
        {
            WriteRaw(string.Empty);
            WriteRaw(BuildSeparator('='));
            WriteRaw($"  Session ended : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            WriteRaw(BuildSeparator('='));
        }

        // ── Internal helpers ─────────────────────────────────────────────────

        private void Write(string level, string message)
        {
            if (!_enabled) return;
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
            WriteRaw(line);
            Console.WriteLine(line); // also emit to VS output
        }

        private void WriteRaw(string line)
        {
            if (!_enabled || string.IsNullOrEmpty(_logFilePath)) return;
            lock (_lock)
            {
                File.AppendAllText(_logFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
        }

        private static string BuildSeparator(char ch, int width = 80)
            => new string(ch, width);
    }
}
