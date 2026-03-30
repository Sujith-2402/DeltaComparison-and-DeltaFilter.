using System;
using System.IO;
using System.Text;

namespace DataUtility
{
    public class AuditLogger
    {
        private readonly string _path;
        private string _alternatePath;
        private readonly object _sync = new object();

        public static AuditLogger Instance { get; } = new AuditLogger();

        private AuditLogger()
        {
            try
            {
                var folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                _path = Path.Combine(folder, "audit.log");
            }
            catch
            {
                _path = null;
            }
        }

        // Allow callers to redirect logs to another folder (e.g. input file folder)
        public void SetOutputFolder(string folder)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folder)) return;
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                _alternatePath = Path.Combine(folder, "audit.log");
            }
            catch { }
        }

        public void Initialise()
        {
            Info("Logger initialised");
        }

        public void Info(string msg) => Write("INFO", msg);
        public void Warning(string msg) => Write("WARN", msg);
        public void Error(string msg) => Write("ERROR", msg);
        public void Error(string msg, Exception ex) => Write("ERROR", msg + "\n" + ex);
        public void SessionEnd() => Info("Session ended");

        private void Write(string level, string msg)
        {
            try
            {
                var line = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}{3}", DateTime.Now, level, msg, Environment.NewLine);
                lock (_sync)
                {
                    if (_path != null) File.AppendAllText(_path, line, Encoding.UTF8);
                    if (!string.IsNullOrEmpty(_alternatePath)) File.AppendAllText(_alternatePath, line, Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
