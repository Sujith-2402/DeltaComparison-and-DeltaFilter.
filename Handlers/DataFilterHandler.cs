using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataUtility
{
    // Basic CSV filter implementation based on a modified date column in pipe-separated format.
    public class DataFilterHandler
    {
        public event Action<int, int, string> ProgressChanged;

        public async Task<string> RunAsync(string inputPath, DateTime filterDate, string mode, DateTime? endDate, CancellationToken cancellationToken)
        {
            if (!File.Exists(inputPath)) throw new FileNotFoundException("Input file not found", inputPath);

            // Read input file containing pipe-separated records
            var allLines = await Task.Run(() => File.ReadAllLines(inputPath)).ConfigureAwait(false);
            int total = allLines.Length;
            var matched = new List<string>();
            var unmatched = new List<string>();

            AuditLogger.Instance.Info(string.Format("Filter run on file: {0}, Mode={1}, FilterDate={2}, EndDate={3}, TotalRows={4}",
                inputPath, mode, filterDate.Date.ToShortDateString(), (endDate.HasValue ? endDate.Value.Date.ToShortDateString() : "<none>"), total));

            int modifiedIndex = 4; // zero-based index for ModifiedDate column
            DateTime fDate = filterDate.Date;
            DateTime? eDate = endDate?.Date;

            for (int i = 0; i < total; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = allLines[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    ProgressChanged?.Invoke(i + 1, total, string.Format("Filtering ({0}/{1})", i + 1, total));
                    continue;
                }

                var parts = line.Split('|');
                if (parts.Length <= modifiedIndex)
                {
                    AuditLogger.Instance.Info(string.Format("Skipping malformed line {0}", i + 1));
                    ProgressChanged?.Invoke(i + 1, total, string.Format("Filtering ({0}/{1})", i + 1, total));
                    continue;
                }

                var modifiedText = parts[modifiedIndex].Trim().Trim('"');
                DateTime parsed;
                bool ok = TryParseDate(modifiedText, out parsed);
                bool keep = false;
                if (ok)
                {
                    var modDate = parsed.Date;
                    switch ((mode ?? string.Empty).ToUpperInvariant())
                    {
                        case "AFTER": keep = modDate >= fDate; break;
                        case "BEFORE": keep = modDate <= fDate; break;
                        case "EXACT": keep = modDate == fDate; break;
                        case "BETWEEN": if (eDate.HasValue) keep = modDate >= fDate && modDate <= eDate.Value; break;
                        default: keep = false; break;
                    }
                }

                if (keep)
                {
                    matched.Add(line);
                }
                else
                {
                    unmatched.Add(line);
                }

                ProgressChanged?.Invoke(i + 1, total, string.Format("Filtering ({0}/{1})", i + 1, total));
            }

            // Determine base folder from input path and create separate Logs and Output folders
            var baseFolder = Path.GetDirectoryName(Path.GetFullPath(inputPath));
            if (string.IsNullOrEmpty(baseFolder)) baseFolder = AppConfig.Instance.OutputDirectory;
            var logsDir = Path.Combine(baseFolder, "Logs");
            var outputsDir = Path.Combine(baseFolder, "Output");
            if (!Directory.Exists(logsDir)) Directory.CreateDirectory(logsDir);
            if (!Directory.Exists(outputsDir)) Directory.CreateDirectory(outputsDir);

            // write audit logs to logsDir
            AuditLogger.Instance.SetOutputFolder(logsDir);

            var matchedPath = Path.Combine(outputsDir, "filter_matched.txt");
            var unmatchedPath = Path.Combine(outputsDir, "filter_unmatched.txt");
            var logPath = Path.Combine(logsDir, "filter_log.txt");

            if (matched.Count == 0)
            {
                await Task.Run(() => File.WriteAllText(matchedPath, "# No records matched the filter (by modified date)")).ConfigureAwait(false);
            }
            else
            {
                await Task.Run(() => File.WriteAllLines(matchedPath, matched.ToArray())).ConfigureAwait(false);
            }

            if (unmatched.Count == 0)
            {
                await Task.Run(() => File.WriteAllText(unmatchedPath, "# All records matched the filter (none unmatched)" )).ConfigureAwait(false);
            }
            else
            {
                await Task.Run(() => File.WriteAllLines(unmatchedPath, unmatched.ToArray())).ConfigureAwait(false);
            }

            AuditLogger.Instance.Info(string.Format("Filter complete. Input={0}, Mode={1}, FilterDate={2}, Matched={3}, Unmatched={4}, MatchedOutput={5}, UnmatchedOutput={6}",
                inputPath, mode, filterDate.ToShortDateString(), matched.Count, unmatched.Count, matchedPath, unmatchedPath));

            // write a small log file in the same folder
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Filter Log");
            sb.AppendLine("Timestamp: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("Input: " + inputPath);
            sb.AppendLine("Mode: " + mode);
            sb.AppendLine("FilterDate: " + filterDate.ToShortDateString());
            sb.AppendLine("Matched: " + matched.Count);
            sb.AppendLine("Unmatched: " + unmatched.Count);
            if (matched.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Sample Matched (up to 20):");
                for (int i = 0; i < Math.Min(20, matched.Count); i++) sb.AppendLine(matched[i]);
            }

            await Task.Run(() => File.WriteAllText(logPath, sb.ToString())).ConfigureAwait(false);

            // return matched file path by default
            return matchedPath;
        }

        private bool TryParseDate(string text, out DateTime parsed)
        {
            parsed = default;
            foreach (var fmt in AppConfig.Instance.DateFormats)
            {
                if (DateTime.TryParseExact(text, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)) return true;
            }
            return DateTime.TryParse(text, out parsed);
        }
    }
}
