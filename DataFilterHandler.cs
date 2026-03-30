using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataUtility
{
    /// <summary>
    /// Handles Data Filtering based on a provided date condition.
    /// Writes filtered output to the same folder as input with the same base name + suffix.
    /// </summary>
    public class DataFilterHandler
    {
        // ── Progress reporting ─────────────────────────────────────────────────
        public event Action<int, int, string> ProgressChanged;

        // ── Config & Logger shortcuts ──────────────────────────────────────────
        private readonly AppConfig _cfg = AppConfig.Instance;
        private readonly AuditLogger _log = AuditLogger.Instance;

        // ── Public entry point ─────────────────────────────────────────────────
        /// <summary>
        /// Filters <paramref name="inputFilePath"/> by date and writes the result next to the input file.
        /// </summary>
        /// <param name="inputFilePath">Source data file.</param>
        /// <param name="filterDate">The date used for filtering.</param>
        /// <param name="filterMode">EXACT | BEFORE | AFTER | BETWEEN</param>
        /// <param name="endDate">Required only when filterMode == BETWEEN.</param>
        /// <returns>Path of the output file.</returns>
        public async Task<string> RunAsync(
            string inputFilePath,
            DateTime filterDate,
            string filterMode,
            DateTime? endDate,
            CancellationToken ct)
        {
            return await Task.Run(
                () => Run(inputFilePath, filterDate, filterMode, endDate, ct), ct);
        }

        private string Run(
            string inputPath,
            DateTime filterDate,
            string filterMode,
            DateTime? endDate,
            CancellationToken ct)
        {
            _log.Section("DATA FILTER STARTED");
            _log.Info($"Input file   : {inputPath}");
            _log.Info($"Filter mode  : {filterMode}");
            _log.Info($"Filter date  : {filterDate:yyyy-MM-dd}");
            if (filterMode.Equals("BETWEEN", StringComparison.OrdinalIgnoreCase) && endDate.HasValue)
                _log.Info($"End date     : {endDate.Value:yyyy-MM-dd}");

            // 1. Validate
            ValidateFile(inputPath);
            if (filterMode.Equals("BETWEEN", StringComparison.OrdinalIgnoreCase) && !endDate.HasValue)
                throw new ArgumentException("End date is required for BETWEEN filter mode.");

            // 2. Determine format
            string ext = Path.GetExtension(inputPath).ToLowerInvariant();

            // 3. Build output path
            string outputPath = BuildOutputPath(inputPath, _cfg.FilterOutputSuffix, _cfg.FilterOutputFormat, ext);
            _log.Info($"Output file  : {outputPath}");

            // 4. Filter
            Report("Reading input file …", 0, 0);
            switch (ext)
            {
                case ".csv":
                case ".txt":
                    FilterDelimitedFile(inputPath, outputPath, filterDate, filterMode, endDate, ext, ct);
                    break;
                case ".xlsx":
                case ".xls":
                    FilterExcelFile(inputPath, outputPath, filterDate, filterMode, endDate, ct);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported file extension: {ext}");
            }

            _log.Info($"Output written to: {outputPath}");
            _log.Section("DATA FILTER FINISHED");

            return outputPath;
        }

        // ── Delimited file filter ─────────────────────────────────────────────

        private void FilterDelimitedFile(
            string inputPath,
            string outputPath,
            DateTime filterDate,
            string filterMode,
            DateTime? endDate,
            string ext,
            CancellationToken ct)
        {
            _log.Debug($"Filtering delimited file: {inputPath}");
            string[] lines = File.ReadAllLines(inputPath, Encoding.UTF8);
            if (lines.Length == 0)
            {
                _log.Warning("Input file is empty.");
                File.WriteAllText(outputPath, string.Empty);
                return;
            }

            string delimiter = _cfg.FilterDelimiter;
            string[] headers = SplitLine(lines[0], delimiter);

            int dateColIndex = Array.FindIndex(headers,
                h => string.Equals(h.Trim(), _cfg.DateFilterColumn, StringComparison.OrdinalIgnoreCase));

            if (dateColIndex < 0)
                throw new InvalidDataException(
                    $"Date filter column '{_cfg.DateFilterColumn}' not found in file.");

            _log.Debug($"Date column index: {dateColIndex}");

            var outputLines = new List<string>();
            if (_cfg.FilterIncludeHeader)
                outputLines.Add(lines[0]);

            int total = lines.Length - 1;
            int matched = 0;

            for (int i = 1; i < lines.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                Report($"Filtering record {i} / {total} …", i, total);

                if (string.IsNullOrWhiteSpace(lines[i])) continue;

                string[] values = SplitLine(lines[i], delimiter);
                string rawDate = dateColIndex < values.Length ? values[dateColIndex].Trim() : string.Empty;

                if (!TryParseDate(rawDate, out DateTime recordDate))
                {
                    _log.Warning($"  Row {i + 1}: Cannot parse date '{rawDate}' – row skipped.");
                    continue;
                }

                if (MatchesFilter(recordDate, filterDate, filterMode, endDate))
                {
                    outputLines.Add(lines[i]);
                    matched++;
                }
            }

            File.WriteAllLines(outputPath, outputLines, Encoding.UTF8);
            _log.Info($"Filter complete. {matched} record(s) matched out of {total}.");
        }

        // ── Excel file filter ─────────────────────────────────────────────────

        private void FilterExcelFile(
            string inputPath,
            string outputPath,
            DateTime filterDate,
            string filterMode,
            DateTime? endDate,
            CancellationToken ct)
        {
            // Requires EPPlus NuGet package
            _log.Debug($"Filtering Excel file: {inputPath}");

            using var inputPkg = new OfficeOpenXml.ExcelPackage(new FileInfo(inputPath));
            var wsIn = inputPkg.Workbook.Worksheets[0];
            int rowCount = wsIn.Dimension?.Rows ?? 0;
            int colCount = wsIn.Dimension?.Columns ?? 0;

            if (rowCount < 2)
            {
                _log.Warning("Excel file has no data rows.");
                return;
            }

            // Find date column
            int dateColIndex = -1;
            for (int c = 1; c <= colCount; c++)
            {
                if (string.Equals(wsIn.Cells[1, c].Text?.Trim(),
                    _cfg.DateFilterColumn, StringComparison.OrdinalIgnoreCase))
                {
                    dateColIndex = c;
                    break;
                }
            }

            if (dateColIndex < 0)
                throw new InvalidDataException(
                    $"Date filter column '{_cfg.DateFilterColumn}' not found in Excel file.");

            using var outPkg = new OfficeOpenXml.ExcelPackage();
            var wsOut = outPkg.Workbook.Worksheets.Add("FilteredData");

            // Copy header
            int outRow = 1;
            if (_cfg.FilterIncludeHeader)
            {
                for (int c = 1; c <= colCount; c++)
                    wsOut.Cells[outRow, c].Value = wsIn.Cells[1, c].Value;
                outRow++;
            }

            int total = rowCount - 1;
            int matched = 0;

            for (int r = 2; r <= rowCount; r++)
            {
                ct.ThrowIfCancellationRequested();
                Report($"Filtering record {r - 1} / {total} …", r - 1, total);

                string rawDate = wsIn.Cells[r, dateColIndex].Text?.Trim() ?? string.Empty;
                if (!TryParseDate(rawDate, out DateTime recordDate))
                {
                    _log.Warning($"  Row {r}: Cannot parse date '{rawDate}' – skipped.");
                    continue;
                }

                if (MatchesFilter(recordDate, filterDate, filterMode, endDate))
                {
                    for (int c = 1; c <= colCount; c++)
                        wsOut.Cells[outRow, c].Value = wsIn.Cells[r, c].Value;
                    outRow++;
                    matched++;
                }
            }

            outPkg.SaveAs(new FileInfo(outputPath));
            _log.Info($"Filter complete. {matched} record(s) matched out of {total}.");
        }

        // ── Filter predicate ──────────────────────────────────────────────────

        private bool MatchesFilter(DateTime recordDate, DateTime filterDate, string mode, DateTime? endDate)
        {
            // Compare only dates (ignore time) unless required
            DateTime rd = recordDate.Date;
            DateTime fd = filterDate.Date;
            bool inclusive = _cfg.IncludeBoundaryDate;

            return mode.ToUpperInvariant() switch
            {
                "EXACT" => rd == fd,
                "BEFORE" => inclusive ? rd <= fd : rd < fd,
                "AFTER" => inclusive ? rd >= fd : rd > fd,
                "BETWEEN" when endDate.HasValue =>
                    (inclusive ? rd >= fd : rd > fd) &&
                    (inclusive ? rd <= endDate.Value.Date : rd < endDate.Value.Date),
                _ => false
            };
        }

        // ── Date parsing ──────────────────────────────────────────────────────

        private bool TryParseDate(string raw, out DateTime result)
        {
            string[] formats =
            {
                _cfg.FilterSourceDateFormat,
                "yyyy-MM-dd HH:mm:ss",
                "yyyy-MM-dd",
                "MM/dd/yyyy",
                "dd/MM/yyyy",
                "dd-MM-yyyy",
                "MM-dd-yyyy"
            };

            return DateTime.TryParseExact(raw, formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None, out result)
                || DateTime.TryParse(raw, out result);
        }

        // ── Utilities ─────────────────────────────────────────────────────────

        private void ValidateFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("File path cannot be empty.");
            if (!File.Exists(path))
                throw new FileNotFoundException($"File not found: {path}");
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (!Array.Exists(_cfg.FilterSupportedExtensions,
                    e => e.Equals(ext, StringComparison.OrdinalIgnoreCase)))
                throw new NotSupportedException($"Unsupported file extension: {ext}");
        }

        private static string BuildOutputPath(
            string inputPath, string suffix, string outputFormat, string inputExt)
        {
            string dir = Path.GetDirectoryName(inputPath)!;
            string name = Path.GetFileNameWithoutExtension(inputPath);
            string ext = outputFormat.ToUpperInvariant() switch
            {
                "SAME_AS_INPUT" => inputExt,
                "CSV" => ".csv",
                "TXT" => ".txt",
                "XLSX" => ".xlsx",
                _ => inputExt
            };
            return Path.Combine(dir, name + suffix + ext);
        }

        private static string[] SplitLine(string line, string delimiter)
        {
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
    }
}
