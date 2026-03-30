using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataUtility
{
    // Simple CSV comparison handler.
    // Produces lines present in primary but not in secondary (by exact line match).
    public class DataComparisonHandler
    {
        public event Action<int, int, string> ProgressChanged;

        public async Task<string> RunAsync(string primaryPath, string secondaryPath, CancellationToken cancellationToken)
        {
            if (!File.Exists(primaryPath)) throw new FileNotFoundException("Primary file not found", primaryPath);
            if (!File.Exists(secondaryPath)) throw new FileNotFoundException("Secondary file not found", secondaryPath);

            var primaryLines = await Task.Run(() => File.ReadAllLines(primaryPath)).ConfigureAwait(false);
            var secondaryRaw = await Task.Run(() => File.ReadAllLines(secondaryPath)).ConfigureAwait(false);

            // Create trimmed, case-insensitive sets for comparison
            var secondarySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in secondaryRaw)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                secondarySet.Add(s.Trim());
            }

            AuditLogger.Instance.Info("Comparison started. Primary lines=" + primaryLines.Length + ", Secondary unique lines=" + secondarySet.Count);

            var newOrModified = new List<string>();
            var existing = new List<string>();
            var matchedByDate = new List<string>();
            var mismatchedByDate = new List<string>();

            int total = primaryLines.Length;
            // Build secondary lookup by key (FullFilePath at index 0) with parsed modified date
            var secondaryLookup = new Dictionary<string, Tuple<string, DateTime?>>(StringComparer.OrdinalIgnoreCase);
            for (int s = 0; s < secondaryRaw.Length; s++)
            {
                var line = secondaryRaw[s];
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('|');
                if (parts.Length == 0) continue;
                var key = parts[0].Trim().Trim('"');
              //  DateTime parsedSec;
                DateTime? secDate = null;
                if (parts.Length > 4)
                {
                    var modText = parts[4].Trim().Trim('"');
                    DateTime temp;
                    if (TryParseDate(modText, out temp)) secDate = temp.Date;
                }
                if (!secondaryLookup.ContainsKey(key)) secondaryLookup.Add(key, Tuple.Create(line, secDate));
            }

            for (int i = 0; i < total; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var raw = primaryLines[i];
                if (string.IsNullOrWhiteSpace(raw))
                {
                    ProgressChanged?.Invoke(i + 1, total, string.Format("Comparing ({0}/{1})", i + 1, total));
                    continue;
                }

                var line = raw.Trim();
                // determine key and modified date from primary
                var partsP = line.Split('|');
                var keyP = partsP.Length > 0 ? partsP[0].Trim().Trim('"') : string.Empty;
                DateTime? priDate = null;
                if (partsP.Length > 4)
                {
                    DateTime t;
                    if (TryParseDate(partsP[4].Trim().Trim('"'), out t)) priDate = t.Date;
                }

                if (secondaryLookup.ContainsKey(keyP))
                {
                    // exists in secondary
                    existing.Add(line);
                    var secEntry = secondaryLookup[keyP];
                    var secDate = secEntry.Item2;
                    if (priDate.HasValue && secDate.HasValue && priDate.Value == secDate.Value)
                    {
                        matchedByDate.Add(line);
                    }
                    else
                    {
                        mismatchedByDate.Add(line);
                    }
                }
                else
                {
                    newOrModified.Add(line);
                }

                ProgressChanged?.Invoke(i + 1, total, string.Format("Comparing ({0}/{1})", i + 1, total));
            }

            // Write outputs and logs to the same folder as the primary input file
            // Determine base folder from primary file and create separate Logs and Output folders
            var baseFolder = Path.GetDirectoryName(Path.GetFullPath(primaryPath));
            if (string.IsNullOrEmpty(baseFolder)) baseFolder = AppConfig.Instance.OutputDirectory;
            var logsDir = Path.Combine(baseFolder, "Logs");
            var outputsDir = Path.Combine(baseFolder, "Output");
            if (!Directory.Exists(logsDir)) Directory.CreateDirectory(logsDir);
            if (!Directory.Exists(outputsDir)) Directory.CreateDirectory(outputsDir);

            // write audit logs to logsDir
            AuditLogger.Instance.SetOutputFolder(logsDir);

            var deltaPath = Path.Combine(outputsDir, "comparison_delta.txt");
            var existingPath = Path.Combine(outputsDir, "comparison_existing.txt");
            var logPath = Path.Combine(logsDir, "comparison_log.txt");
            var matchedPath = Path.Combine(outputsDir, "comparison_matched_by_date.txt");
            var mismatchedPath = Path.Combine(outputsDir, "comparison_mismatched_by_date.txt");

            if (newOrModified.Count == 0)
            {
                await Task.Run(() => File.WriteAllText(deltaPath, "# No new or modified records found")).ConfigureAwait(false);
            }
            else
            {
                await Task.Run(() => File.WriteAllLines(deltaPath, newOrModified.ToArray())).ConfigureAwait(false);
            }

            if (existing.Count == 0)
            {
                await Task.Run(() => File.WriteAllText(existingPath, "# No existing records found")).ConfigureAwait(false);
            }
            else
            {
                await Task.Run(() => File.WriteAllLines(existingPath, existing.ToArray())).ConfigureAwait(false);
            }

            if (matchedByDate.Count == 0)
            {
                await Task.Run(() => File.WriteAllText(matchedPath, "# No matched-by-date records found")).ConfigureAwait(false);
            }
            else
            {
                await Task.Run(() => File.WriteAllLines(matchedPath, matchedByDate.ToArray())).ConfigureAwait(false);
            }

            if (mismatchedByDate.Count == 0)
            {
                await Task.Run(() => File.WriteAllText(mismatchedPath, "# No mismatched-by-date records found")).ConfigureAwait(false);
            }
            else
            {
                await Task.Run(() => File.WriteAllLines(mismatchedPath, mismatchedByDate.ToArray())).ConfigureAwait(false);
            }

            // Write a simple comparison log with counts and timestamps
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Comparison Log");
            sb.AppendLine("Timestamp: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("Primary: " + primaryPath);
            sb.AppendLine("Secondary: " + secondaryPath);
            sb.AppendLine("Total primary rows: " + total);
            sb.AppendLine("Existing records: " + existing.Count);
            sb.AppendLine("New/Modified records: " + newOrModified.Count);
            sb.AppendLine();
            sb.AppendLine("Sample New/Modified (up to 20):");
            for (int i = 0; i < Math.Min(20, newOrModified.Count); i++) sb.AppendLine(newOrModified[i]);
            sb.AppendLine();
            sb.AppendLine("Sample Existing (up to 20):");
            for (int i = 0; i < Math.Min(20, existing.Count); i++) sb.AppendLine(existing[i]);

            await Task.Run(() => File.WriteAllText(logPath, sb.ToString())).ConfigureAwait(false);

            AuditLogger.Instance.Info("Comparison complete. Delta=" + newOrModified.Count + ", Existing=" + existing.Count + ", DeltaPath=" + deltaPath + ", ExistingPath=" + existingPath + ", Log=" + logPath);

            // Return delta path as primary result path
            return deltaPath;
        }

        private bool TryParseDate(string text, out DateTime parsed)
        {
            parsed = default(DateTime);
            foreach (var fmt in AppConfig.Instance.DateFormats)
            {
                if (DateTime.TryParseExact(text, fmt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out parsed)) return true;
            }
            return DateTime.TryParse(text, out parsed);
        }
    }
}
