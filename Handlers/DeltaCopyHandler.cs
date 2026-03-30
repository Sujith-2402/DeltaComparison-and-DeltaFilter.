using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataUtility
{
    // Handles Delta Copy operations: reads a delta file and for each row
    // with Status=NEW copies the file from sourceCommonPath to destinationBase (no overwrite),
    // and for Status=MODIFIED replaces file at destination with source.
    public class DeltaCopyHandler
    {
        public event Action<int, int, string> ProgressChanged;
        // runtime collected log lines for organized output
        private readonly List<string> _runtimeLogLines = new List<string>();

        public async Task<string> RunAsync(string deltaFilePath, string sourceCommonPath, string destinationBase, CancellationToken cancellationToken)
        {
            if (!File.Exists(deltaFilePath)) throw new FileNotFoundException("Delta file not found", deltaFilePath);
            if (string.IsNullOrWhiteSpace(sourceCommonPath) || !Directory.Exists(sourceCommonPath)) throw new DirectoryNotFoundException("Source common path not found");
            if (string.IsNullOrWhiteSpace(destinationBase)) throw new ArgumentException("Destination base path required");

            var allLines = await Task.Run(() => File.ReadAllLines(deltaFilePath)).ConfigureAwait(false);
            var lines = allLines.ToList();

            // prepare runtime log collector
            _runtimeLogLines.Clear();
            _runtimeLogLines.Add("sep=|");
            _runtimeLogLines.Add("FullFilePath|FileName|Extension|CreatedDate|ModifiedDate|OriginalFileDate|Size(Bytes)|Status|Result");

            // Skip optional sep header and named header
            if (lines.Count > 0 && lines[0].StartsWith("sep=", StringComparison.OrdinalIgnoreCase)) lines.RemoveAt(0);
            if (lines.Count > 0 && lines[0].ToLowerInvariant().Contains("fullfilepath") && lines[0].ToLowerInvariant().Contains("status")) lines.RemoveAt(0);

            int total = lines.Count;
            for (int i = 0; i < total; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var raw = lines[i];
                if (string.IsNullOrWhiteSpace(raw))
                {
                    ProgressChanged?.Invoke(i + 1, total, string.Format("Processing ({0}/{1})", i + 1, total));
                    continue;
                }

                var parts = raw.Split('|');
                if (parts.Length < 2) continue; // need at least fullpath and filename
                var fullPath = parts[0].Trim().Trim('"');
                var fileName = parts[1].Trim().Trim('"');
                var status = string.Empty;
                if (parts.Length >= 8) status = parts[7].Trim().Trim('"').ToUpperInvariant();
                else if (parts.Length >= 7) status = parts[6].Trim().Trim('"').ToUpperInvariant();

                // Compute relative path under source/destination.
                // If the full path in delta refers to a path under the source common path, use that relative suffix.
                // If it refers to a path under the destination base, use that relative suffix.
                // Otherwise fall back to the file name only.
                string relative = fileName;
                try
                {
                    var fullNorm = Path.GetFullPath(fullPath);
                    var srcNorm = Path.GetFullPath(sourceCommonPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    var destNorm = Path.GetFullPath(destinationBase).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

                    if (fullNorm.StartsWith(srcNorm, StringComparison.OrdinalIgnoreCase))
                    {
                        relative = fullNorm.Substring(srcNorm.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    }
                    else if (fullNorm.StartsWith(destNorm, StringComparison.OrdinalIgnoreCase))
                    {
                        relative = fullNorm.Substring(destNorm.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    }
                    else
                    {
                        // try to use path segments if filename appears inside fullNorm
                        var idx = fullNorm.IndexOf(fileName, StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0 && idx + fileName.Length == fullNorm.Length)
                        {
                            // use the trailing part that leads to the filename
                            relative = fullNorm.Substring(Math.Max(0, idx - 1)).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        }
                        else
                        {
                            relative = fileName;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AuditLogger.Instance.Warning("DeltaCopy: failed to compute relative path for: " + fullPath + " — " + ex.Message);
                    relative = fileName;
                }

                // Resolve best source path and destination path
                string sourceFile = null;
                string destFile = null;

                // If fullPath is absolute and exists, prefer it as source
                try
                {
                    if (!string.IsNullOrWhiteSpace(fullPath) && File.Exists(fullPath))
                    {
                        sourceFile = Path.GetFullPath(fullPath);
                    }
                }
                catch { }

                // If source not found yet, try combinations
                if (string.IsNullOrWhiteSpace(sourceFile))
                {
                    // try combining sourceCommonPath + relative
                    try
                    {
                        var candidate = Path.Combine(sourceCommonPath, relative);
                        if (File.Exists(candidate)) sourceFile = candidate;
                    }
                    catch { }
                }

                if (string.IsNullOrWhiteSpace(sourceFile))
                {
                    // try candidate where fullPath may be relative to sourceCommonPath
                    try
                    {
                        var candidate = Path.Combine(sourceCommonPath, fullPath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                        if (File.Exists(candidate)) sourceFile = candidate;
                    }
                    catch { }
                }

                if (string.IsNullOrWhiteSpace(sourceFile))
                {
                    // last resort: search by filename under sourceCommonPath (recursive) — expensive but reliable
                    try
                    {
                        var found = Directory.EnumerateFiles(sourceCommonPath, fileName, SearchOption.AllDirectories).FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(found)) sourceFile = found;
                    }
                    catch { }
                }

                // Destination mapping: prefer keeping same relative structure when possible
                try
                {
                    var destNorm = Path.GetFullPath(destinationBase).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    var srcNorm = Path.GetFullPath(sourceCommonPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!string.IsNullOrWhiteSpace(fullPath))
                    {
                        var fullNorm = Path.GetFullPath(fullPath);
                        if (fullNorm.StartsWith(srcNorm, StringComparison.OrdinalIgnoreCase))
                        {
                            var rel = fullNorm.Substring(srcNorm.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                            destFile = Path.Combine(destinationBase, rel);
                        }
                        else if (fullNorm.StartsWith(destNorm, StringComparison.OrdinalIgnoreCase))
                        {
                            destFile = fullNorm; // already inside destination
                        }
                    }
                }
                catch { }

                if (string.IsNullOrWhiteSpace(destFile)) destFile = Path.Combine(destinationBase, relative);

                try
                {
                    var destDir = Path.GetDirectoryName(destFile);
                    if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);

                    AuditLogger.Instance.Info($"DeltaCopy: processing row: status={status}, source={sourceFile}, dest={destFile}");

                    string result = "Skipped";
                    if (string.Equals(status, "NEW", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(sourceFile) && File.Exists(sourceFile))
                        {
                            if (!File.Exists(destFile))
                            {
                                File.Copy(sourceFile, destFile);
                                result = "Copied";
                                AuditLogger.Instance.Info($"DeltaCopy: NEW copied '{sourceFile}' -> '{destFile}'");
                            }
                            else
                            {
                                result = "DestinationExists";
                                AuditLogger.Instance.Info($"DeltaCopy: NEW skipped because destination already exists: '{destFile}'");
                            }
                        }
                        else
                        {
                            result = "SourceNotFound";
                            AuditLogger.Instance.Warning($"DeltaCopy: NEW source not found: {sourceFile}");
                        }
                    }
                    else if (string.Equals(status, "MODIFIED", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(sourceFile) && File.Exists(sourceFile))
                        {
                            try
                            {
                                // If destination exists, try to remove read-only attribute then delete before copying
                                if (File.Exists(destFile))
                                {
                                    try
                                    {
                                        var destInfo = new FileInfo(destFile);
                                        if (destInfo.IsReadOnly)
                                        {
                                            destInfo.IsReadOnly = false;
                                        }
                                        File.Delete(destFile);
                                    }
                                    catch (Exception delEx)
                                    {
                                        AuditLogger.Instance.Warning($"DeltaCopy: failed to delete destination before replace: {destFile} — {delEx.Message}");
                                    }
                                }

                                // Copy source to destination
                                File.Copy(sourceFile, destFile, true);

                                // Preserve timestamps from source on destination
                                try
                                {
                                    var srcInfo = new FileInfo(sourceFile);
                                    File.SetCreationTime(destFile, srcInfo.CreationTime);
                                    File.SetLastWriteTime(destFile, srcInfo.LastWriteTime);
                                }
                                catch { }

                                result = "Replaced";
                                AuditLogger.Instance.Info($"DeltaCopy: MODIFIED replaced '{destFile}' with '{sourceFile}'");
                            }
                            catch (Exception copyEx)
                            {
                                result = "ReplaceFailed";
                                AuditLogger.Instance.Error($"DeltaCopy: failed to replace destination '{destFile}' with '{sourceFile}'", copyEx);
                            }
                        }
                        else
                        {
                            result = "SourceNotFound";
                            AuditLogger.Instance.Warning($"DeltaCopy: MODIFIED source not found: {sourceFile}");
                        }
                    }
                    else
                    {
                        result = "UnknownStatus";
                        AuditLogger.Instance.Info($"DeltaCopy: Skipping unknown status '{status}' for row: {raw}");
                    }

                    // Build organized log entry
                    try
                    {
                        var fiSrc = (!string.IsNullOrWhiteSpace(sourceFile) && File.Exists(sourceFile)) ? new FileInfo(sourceFile) : null;
                        var fiDest = (File.Exists(destFile)) ? new FileInfo(destFile) : null;
                        var created = fiSrc != null ? fiSrc.CreationTime.ToString("o") : string.Empty;
                        var modified = fiSrc != null ? fiSrc.LastWriteTime.ToString("o") : string.Empty;
                        var size = fiSrc != null ? fiSrc.Length.ToString() : string.Empty;
                        var logLine = string.Join("|", new[] {
                            fullPath ?? string.Empty,
                            fileName ?? string.Empty,
                            Path.GetExtension(fileName) ?? string.Empty,
                            created,
                            modified,
                            string.Empty,
                            size,
                            status ?? string.Empty,
                            result
                        });
                        // ensure per-handler log list exists
                        _runtimeLogLines.Add(logLine);
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    AuditLogger.Instance.Error($"DeltaCopy: error processing row: {raw}", ex);
                    try { _runtimeLogLines.Add($"{fullPath}|{fileName}|||ERROR|{ex.Message}"); } catch { }
                }

                ProgressChanged?.Invoke(i + 1, total, string.Format("Processing ({0}/{1})", i + 1, total));
            }

            // Write marker file next to delta input to indicate processing completed
            try
            {
                var marker = Path.Combine(Path.GetDirectoryName(deltaFilePath), Path.GetFileNameWithoutExtension(deltaFilePath) + ".delta_copy_done.txt");
                File.WriteAllText(marker, "DeltaCopy completed: " + DateTime.Now.ToString("o"));
                AuditLogger.Instance.Info("DeltaCopy completed. Marker: " + marker);
            }
            catch { }

            // Write organized log file next to delta input
            try
            {
                var logFile = Path.Combine(Path.GetDirectoryName(deltaFilePath), "DeltaCopy_Logs_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
                File.WriteAllLines(logFile, _runtimeLogLines);
                AuditLogger.Instance.Info("DeltaCopy: organized logs written to " + logFile);
            }
            catch (Exception ex)
            {
                AuditLogger.Instance.Warning("DeltaCopy: failed to write organized logs: " + ex.Message);
            }

            return deltaFilePath;
        }
    }
}
