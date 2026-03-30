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

            var primaryLines = (await Task.Run(() => File.ReadAllLines(primaryPath)).ConfigureAwait(false)).ToList();
            var secondaryRaw = (await Task.Run(() => File.ReadAllLines(secondaryPath)).ConfigureAwait(false)).ToList();

            // If files include a header row with column names, detect and remove it so it is not
            // treated as a data row during comparison. We detect common header tokens.
            Func<string, bool> isHeader = (ln) =>
            {
                if (string.IsNullOrWhiteSpace(ln)) return false;
                var low = ln.ToLowerInvariant();
                return low.Contains("fullfilepath") || low.Contains("filename") || low.Contains("createddate") || low.Contains("modifieddate") || low.Contains("size(bytes)") || low.Contains("originalfiledate");
            };

            // remove first header-like line from primary
            for (int idx = 0; idx < primaryLines.Count; idx++)
            {
                if (!string.IsNullOrWhiteSpace(primaryLines[idx]))
                {
                    if (isHeader(primaryLines[idx])) primaryLines.RemoveAt(idx);
                    break;
                }
            }

            // remove first header-like line from secondary
            for (int idx = 0; idx < secondaryRaw.Count; idx++)
            {
                if (!string.IsNullOrWhiteSpace(secondaryRaw[idx]))
                {
                    if (isHeader(secondaryRaw[idx])) secondaryRaw.RemoveAt(idx);
                    break;
                }
            }

            // Treat Primary as baseline and iterate Secondary as the source to check for NEW/MODIFIED
            var primaryLookup = new Dictionary<string, Tuple<string, DateTime?>>(StringComparer.OrdinalIgnoreCase);
            for (int p = 0; p < primaryLines.Count; p++)
            {
                var line = primaryLines[p];
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('|');
                if (parts.Length == 0) continue;
                var key = parts[0].Trim().Trim('"');
                DateTime? priDate = null;
                if (parts.Length > 4)
                {
                    DateTime temp;
                    if (TryParseDate(parts[4].Trim().Trim('"'), out temp)) priDate = temp.Date;
                }
                if (!primaryLookup.ContainsKey(key)) primaryLookup.Add(key, Tuple.Create(line, priDate));
            }

            AuditLogger.Instance.Info("Comparison started. Primary lines=" + primaryLookup.Count + ", Secondary lines=" + secondaryRaw.Count);

            var newRows = new List<string>();
            var modifiedRows = new List<string>();
            var existingRows = new List<string>();

            int total = secondaryRaw.Count;
            for (int i = 0; i < total; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var raw = secondaryRaw[i];
                if (string.IsNullOrWhiteSpace(raw))
                {
                    ProgressChanged?.Invoke(i + 1, total, string.Format("Comparing ({0}/{1})", i + 1, total));
                    continue;
                }

                var line = raw.Trim();
                var partsS = line.Split('|');
                var keyS = partsS.Length > 0 ? partsS[0].Trim().Trim('"') : string.Empty;
                DateTime? secDate = null;
                if (partsS.Length > 4)
                {
                    DateTime t;
                    if (TryParseDate(partsS[4].Trim().Trim('"'), out t)) secDate = t.Date;
                }

                if (primaryLookup.ContainsKey(keyS))
                {
                    var primaryEntry = primaryLookup[keyS];
                    var priDate = primaryEntry.Item2;
                    if (priDate.HasValue && secDate.HasValue && priDate.Value == secDate.Value)
                    {
                        existingRows.Add(line);
                    }
                    else
                    {
                        modifiedRows.Add(line);
                    }
                }
                else
                {
                    newRows.Add(line);
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
            // remove matched/mismatched intermediate outputs per user request

            // Build delta file with NEW and MODIFIED rows (Secondary compared against Primary baseline)
            var deltaLines = new List<string>();
            deltaLines.Add("sep=|");
            deltaLines.Add("FullFilePath|FileName|Extension|CreatedDate|ModifiedDate|OriginalFileDate|Size(Bytes)|DeltaStatus");

            Func<string, string> normalize = (ln) =>
            {
                if (string.IsNullOrWhiteSpace(ln)) return string.Join("|", Enumerable.Repeat(string.Empty, 7));
                var parts = ln.Split('|');
                var arr = new string[7];
                for (int i = 0; i < 7; i++) arr[i] = i < parts.Length ? parts[i] : string.Empty;
                return string.Join("|", arr);
            };

            // Add modified then new (user example shows modified first)
            foreach (var line in modifiedRows)
            {
                deltaLines.Add(normalize(line) + "|MODIFIED");
            }
            foreach (var line in newRows)
            {
                deltaLines.Add(normalize(line) + "|NEW");
            }

            if (deltaLines.Count == 2)
            {
                await Task.Run(() => File.WriteAllText(deltaPath, "# No new or modified records found")).ConfigureAwait(false);
            }
            else
            {
                await Task.Run(() => File.WriteAllLines(deltaPath, deltaLines.ToArray())).ConfigureAwait(false);
            }

            // existing should contain only previously existing (matched by date) records
            if (existingRows.Count == 0)
            {
                await Task.Run(() => File.WriteAllText(existingPath, "# No previously existing records found")).ConfigureAwait(false);
            }
            else
            {
                await Task.Run(() => File.WriteAllLines(existingPath, existingRows.ToArray())).ConfigureAwait(false);
            }

            // Do not produce matched/mismatched intermediate files per user request

            // Write a simple comparison log with counts and timestamps
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Comparison Log");
            sb.AppendLine("Timestamp: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("Primary (baseline): " + primaryPath + " (rows=" + primaryLookup.Count + ")");
            sb.AppendLine("Secondary (scan): " + secondaryPath + " (rows=" + secondaryRaw.Count + ")");
            sb.AppendLine("Existing records: " + existingRows.Count);
            sb.AppendLine("New/Modified records: " + (modifiedRows.Count + newRows.Count));
            sb.AppendLine();
            sb.AppendLine("Sample New/Modified (up to 20):");
            var combinedDeltaSample = new List<string>();
            combinedDeltaSample.AddRange(modifiedRows);
            combinedDeltaSample.AddRange(newRows);
            for (int i = 0; i < Math.Min(20, combinedDeltaSample.Count); i++) sb.AppendLine(combinedDeltaSample[i]);
            sb.AppendLine();
            sb.AppendLine("Sample Existing (up to 20):");
            for (int i = 0; i < Math.Min(20, existingRows.Count); i++) sb.AppendLine(existingRows[i]);

            await Task.Run(() => File.WriteAllText(logPath, sb.ToString())).ConfigureAwait(false);

            AuditLogger.Instance.Info("Comparison complete. Delta=" + (modifiedRows.Count + newRows.Count) + ", Existing=" + existingRows.Count + ", DeltaPath=" + deltaPath + ", ExistingPath=" + existingPath + ", Log=" + logPath);

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
