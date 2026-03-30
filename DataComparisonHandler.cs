using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataUtility
{
    /// <summary>
    /// Handles Data Comparison between Primary and Secondary files.
    /// Detects NEW and MODIFIED records and writes an output report.
    /// </summary>
    public class DataComparisonHandler
    {
        // ── Progress reporting ─────────────────────────────────────────────────
        public event Action<int, int, string> ProgressChanged;
        // (currentRecord, totalRecords, statusMessage)

        // ── Config & Logger shortcuts ──────────────────────────────────────────
        private readonly AppConfig _cfg = AppConfig.Instance;
        private readonly AuditLogger _log = AuditLogger.Instance;

        // ── Public entry point ─────────────────────────────────────────────────
        /// <summary>
        /// Compares primaryFile with secondaryFile and writes results next to primaryFile.
        /// Returns the output file path on success.
        /// </summary>
        public async Task<string> RunAsync(
            string primaryFilePath,
            string secondaryFilePath,
            CancellationToken ct)
        {
            return await Task.Run(() => Run(primaryFilePath, secondaryFilePath, ct), ct);
        }

        private string Run(string primaryPath, string secondaryPath, CancellationToken ct)
        {
            _log.Section("DATA COMPARISON STARTED");
            _log.Info($"Primary   file : {primaryPath}");
            _log.Info($"Secondary file : {secondaryPath}");

            // 1. Validate inputs
            ValidateFile(primaryPath);
            ValidateFile(secondaryPath);

            // 2. Read data
            Report("Reading primary file …", 0, 0);
            var primaryRecords = ReadRecords(primaryPath);
            _log.Info($"Primary records loaded   : {primaryRecords.Count}");

            Report("Reading secondary file …", 0, 0);
            var secondaryRecords = ReadRecords(secondaryPath);
            _log.Info($"Secondary records loaded : {secondaryRecords.Count}");

            // 3. Build output file path (same folder as primary, same name + suffix)
            string outputPath = BuildOutputPath(primaryPath,
                _cfg.ComparisonOutputSuffix,
                _cfg.ComparisonOutputFormat);
            _log.Info($"Output file    : {outputPath}");

            // 4. Compare
            var results = new List<ComparisonResult>();
            int total = primaryRecords.Count;
            int current = 0;

            StringComparison strCmp = _cfg.CaseSensitiveComparison
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            string pkCol = _cfg.PrimaryKeyColumn;
            string dateCol = _cfg.DateModifiedColumn;
            var excludeCols = new HashSet<string>(
                _cfg.ExcludeColumnsFromComparison,
                StringComparer.OrdinalIgnoreCase);

            foreach (var (key, primaryRow) in primaryRecords)
            {
                ct.ThrowIfCancellationRequested();
                current++;
                Report($"Comparing record {current} / {total} …", current, total);

                if (!secondaryRecords.TryGetValue(key, out var secondaryRow))
                {
                    // Not found in secondary → NEW record
                    if (_cfg.DetectNewRecords)
                    {
                        string dateModified = GetDateModified(primaryRow, dateCol);
                        results.Add(new ComparisonResult
                        {
                            FileLocation = primaryPath,
                            FileName = Path.GetFileName(primaryPath),
                            ChangeType = "NEW",
                            DateModified = dateModified,
                            PrimaryKeyValue = key
                        });
                        _log.Debug($"  NEW record found: key={key}, DateModified={dateModified}");
                    }
                }
                else
                {
                    // Found in secondary → check for MODIFICATION
                    if (_cfg.DetectModifiedRecords && IsModified(primaryRow, secondaryRow, excludeCols, strCmp))
                    {
                        string dateModified = GetDateModified(primaryRow, dateCol);
                        results.Add(new ComparisonResult
                        {
                            FileLocation = primaryPath,
                            FileName = Path.GetFileName(primaryPath),
                            ChangeType = "MODIFIED",
                            DateModified = dateModified,
                            PrimaryKeyValue = key
                        });
                        _log.Debug($"  MODIFIED record: key={key}, DateModified={dateModified}");
                    }
                }
            }

            _log.Info($"Comparison complete. NEW={results.Count(r => r.ChangeType == "NEW")}, " +
                      $"MODIFIED={results.Count(r => r.ChangeType == "MODIFIED")}");

            // 5. Write output
            Report("Writing output file …", total, total);
            WriteOutput(results, outputPath);

            _log.Info($"Output written to: {outputPath}");
            _log.Section("DATA COMPARISON FINISHED");

            return outputPath;
        }

        // ── File reading ──────────────────────────────────────────────────────

        private Dictionary<string, Dictionary<string, string>> ReadRecords(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch
            {
                ".csv" or ".txt" => ReadDelimitedFile(filePath),
                ".xlsx" or ".xls" => ReadExcelFile(filePath),
                _ => throw new NotSupportedException($"File extension not supported: {ext}")
            };
        }

        private Dictionary<string, Dictionary<string, string>> ReadDelimitedFile(string filePath)
        {
            _log.Debug($"Reading delimited file: {filePath}");
            var records = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var lines = File.ReadAllLines(filePath, Encoding.UTF8);

            if (lines.Length == 0)
            {
                _log.Warning("File is empty.");
                return records;
            }

            string delimiter = _cfg.ComparisonDelimiter;
            string[] headers = SplitLine(lines[0], delimiter);

            int pkIndex = Array.FindIndex(headers,
                h => string.Equals(h.Trim(), _cfg.PrimaryKeyColumn, StringComparison.OrdinalIgnoreCase));

            if (pkIndex < 0)
                throw new InvalidDataException(
                    $"Primary key column '{_cfg.PrimaryKeyColumn}' not found in file: {filePath}");

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                string[] values = SplitLine(lines[i], delimiter);
                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                for (int j = 0; j < headers.Length; j++)
                {
                    string colName = headers[j].Trim();
                    string colValue = j < values.Length ? values[j] : string.Empty;
                    if (_cfg.TrimWhitespace) colValue = colValue.Trim();
                    row[colName] = colValue;
                }

                string key = pkIndex < values.Length ? values[pkIndex].Trim() : string.Empty;
                if (!string.IsNullOrEmpty(key))
                    records[key] = row;
            }

            return records;
        }

        private Dictionary<string, Dictionary<string, string>> ReadExcelFile(string filePath)
        {
            // Requires EPPlus or NPOI NuGet package.
            // Using EPPlus (OfficeOpenXml) pattern:
            _log.Debug($"Reading Excel file: {filePath}");
            var records = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            try
            {
                // EPPlus usage (add NuGet: EPPlus)
                using var package = new OfficeOpenXml.ExcelPackage(new FileInfo(filePath));
                var ws = package.Workbook.Worksheets[0];
                int rowCount = ws.Dimension?.Rows ?? 0;
                int colCount = ws.Dimension?.Columns ?? 0;
                if (rowCount < 2) return records;

                // Build header map
                var headers = new Dictionary<int, string>();
                int pkIndex = -1;
                for (int c = 1; c <= colCount; c++)
                {
                    string hdr = ws.Cells[1, c].Text?.Trim() ?? $"Col{c}";
                    headers[c] = hdr;
                    if (string.Equals(hdr, _cfg.PrimaryKeyColumn, StringComparison.OrdinalIgnoreCase))
                        pkIndex = c;
                }

                if (pkIndex < 0)
                    throw new InvalidDataException(
                        $"Primary key column '{_cfg.PrimaryKeyColumn}' not found in: {filePath}");

                for (int r = 2; r <= rowCount; r++)
                {
                    string key = ws.Cells[r, pkIndex].Text?.Trim() ?? string.Empty;
                    if (string.IsNullOrEmpty(key)) continue;

                    var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int c = 1; c <= colCount; c++)
                    {
                        string val = ws.Cells[r, c].Text ?? string.Empty;
                        if (_cfg.TrimWhitespace) val = val.Trim();
                        row[headers[c]] = val;
                    }
                    records[key] = row;
                }
            }
            catch (Exception ex)
            {
                _log.Error("Excel read error", ex);
                throw;
            }

            return records;
        }

        // ── Comparison logic ──────────────────────────────────────────────────

        private bool IsModified(
            Dictionary<string, string> primaryRow,
            Dictionary<string, string> secondaryRow,
            HashSet<string> excludeCols,
            StringComparison cmp)
        {
            foreach (var (col, primaryVal) in primaryRow)
            {
                if (excludeCols.Contains(col)) continue;
                secondaryRow.TryGetValue(col, out string secondaryVal);
                if (!string.Equals(primaryVal, secondaryVal ?? string.Empty, cmp))
                    return true;
            }
            return false;
        }

        private string GetDateModified(Dictionary<string, string> row, string dateCol)
        {
            return row.TryGetValue(dateCol, out string val)
                ? val
                : DateTime.Now.ToString(_cfg.SourceDateFormat);
        }

        // ── Output writing ────────────────────────────────────────────────────

        private void WriteOutput(List<ComparisonResult> results, string outputPath)
        {
            var sb = new StringBuilder();
            if (_cfg.ComparisonIncludeHeader)
                sb.AppendLine("FileLocation,FileName,ChangeType,DateModified,PrimaryKeyValue");

            foreach (var r in results)
                sb.AppendLine($"\"{r.FileLocation}\",\"{r.FileName}\",{r.ChangeType}," +
                              $"\"{r.DateModified}\",\"{r.PrimaryKeyValue}\"");

            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
            _log.Info($"Wrote {results.Count} result(s) to output file.");
        }

        // ── Utilities ─────────────────────────────────────────────────────────

        private void ValidateFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("File path cannot be empty.");
            if (!File.Exists(path))
                throw new FileNotFoundException($"File not found: {path}");
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (!Array.Exists(_cfg.ComparisonSupportedExtensions,
                    e => e.Equals(ext, StringComparison.OrdinalIgnoreCase)))
                throw new NotSupportedException($"Unsupported file extension: {ext}");
        }

        private static string BuildOutputPath(string inputPath, string suffix, string format)
        {
            string dir = Path.GetDirectoryName(inputPath)!;
            string name = Path.GetFileNameWithoutExtension(inputPath);
            string ext = format.ToUpperInvariant() switch
            {
                "CSV" => ".csv",
                "TXT" => ".txt",
                "XLSX" => ".xlsx",
                _ => Path.GetExtension(inputPath)
            };
            return Path.Combine(dir, name + suffix + ext);
        }

        private static string[] SplitLine(string line, string delimiter)
        {
            // Simple CSV split – handles basic quoted fields
            var result = new List<string>();
            bool inQuotes = false;
            var current = new StringBuilder();
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"') { inQuotes = !inQuotes; }
                else if (!inQuotes && line.Substring(i).StartsWith(delimiter))
                {
                    result.Add(current.ToString());
                    current.Clear();
                    i += delimiter.Length - 1;
                }
                else { current.Append(c); }
            }
            result.Add(current.ToString());
            return result.ToArray();
        }

        private void Report(string message, int current, int total)
        {
            _log.Debug(message);
            ProgressChanged?.Invoke(current, total, message);
        }

        // ── Inner model ───────────────────────────────────────────────────────
        private class ComparisonResult
        {
            public string FileLocation { get; set; }
            public string FileName { get; set; }
            public string ChangeType { get; set; }
            public string DateModified { get; set; }
            public string PrimaryKeyValue { get; set; }
        }
    }
}
