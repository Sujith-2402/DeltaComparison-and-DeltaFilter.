using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace DataUtility
{
    // Delta folder comparison logic
    // Implements: compare PrimaryPath (baseline) and SecondaryPath (incoming)
    // Produces NewFiles and ModifiedFiles under configured OutputFolder and a Logs.txt
    public class DeltaFolderComparisonHandler
    {
        public event Action<int, int, string> ProgressChanged;

        public async Task<string> RunAsync(string primaryPath, string secondaryPath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(primaryPath) || !Directory.Exists(primaryPath)) throw new DirectoryNotFoundException("Primary path not found");
            if (string.IsNullOrWhiteSpace(secondaryPath) || !Directory.Exists(secondaryPath)) throw new DirectoryNotFoundException("Secondary path not found");

            // Read configuration
            var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            DateTime? modifiedAfter = null;
            string outputFolder = null;

            try
            {
                var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Configuration.xml");
                if (File.Exists(configPath))
                {
                    var doc = XDocument.Load(configPath);
                    var root = doc.Element("Configuration");
                    if (root != null)
                    {
                        var exts = root.Element("AllowedExtensions");
                        if (exts != null)
                        {
                            // support child <Extension> elements or comma-separated text
                            var children = exts.Elements("Extension").Select(x => x.Value).Where(s => !string.IsNullOrWhiteSpace(s));
                            foreach (var c in children)
                            {
                                var v = c.Trim();
                                if (!v.StartsWith(".")) v = "." + v;
                                allowedExtensions.Add(v);
                            }

                            if (!children.Any())
                            {
                                var text = exts.Value ?? string.Empty;
                                foreach (var token in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                                {
                                    var v = token.Trim();
                                    if (string.IsNullOrEmpty(v)) continue;
                                    if (!v.StartsWith(".")) v = "." + v;
                                    allowedExtensions.Add(v);
                                }
                            }
                        }

                        var mafter = root.Element("ModifiedAfterDate");
                        if (mafter != null && !string.IsNullOrWhiteSpace(mafter.Value))
                        {
                            DateTime dt;
                            if (DateTime.TryParse(mafter.Value.Trim(), out dt)) modifiedAfter = dt;
                        }

                        var outEl = root.Element("OutputFolder");
                        if (outEl != null && !string.IsNullOrWhiteSpace(outEl.Value)) outputFolder = outEl.Value.Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                AuditLogger.Instance.Warning("DeltaFolderComparison: failed to read Configuration.xml: " + ex.Message);
            }

            if (string.IsNullOrWhiteSpace(outputFolder))
            {
                // fallback to AppConfig output directory or a folder next to primary
                outputFolder = AppConfig.Instance?.OutputDirectory ?? Path.Combine(primaryPath, "DeltaOutput");
            }

            // prepare output directories
            var newOut = Path.Combine(outputFolder, "NewFiles");
            var modOut = Path.Combine(outputFolder, "ModifiedFiles");
            if (!Directory.Exists(newOut)) Directory.CreateDirectory(newOut);
            if (!Directory.Exists(modOut)) Directory.CreateDirectory(modOut);

            // Collect files
            var primaryFiles = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
            var secondaryFiles = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (var f in Directory.EnumerateFiles(primaryPath, "*.*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fi = new FileInfo(f);
                if (allowedExtensions.Count > 0 && !allowedExtensions.Contains(fi.Extension)) continue;
                if (modifiedAfter.HasValue && fi.LastWriteTime < modifiedAfter.Value) continue;
                var key = fi.Name; // key = filename
                if (!primaryFiles.ContainsKey(key)) primaryFiles.Add(key, fi);
            }

            var allSecondary = Directory.EnumerateFiles(secondaryPath, "*.*", SearchOption.AllDirectories).ToList();
            int total = allSecondary.Count;
            for (int i = 0; i < total; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var f = allSecondary[i];
                var fi = new FileInfo(f);
                ProgressChanged?.Invoke(i + 1, total, $"Scanning secondary ({i + 1}/{total})");
                if (allowedExtensions.Count > 0 && !allowedExtensions.Contains(fi.Extension)) continue;
                if (modifiedAfter.HasValue && fi.LastWriteTime < modifiedAfter.Value) continue;
                var key = fi.Name;
                if (!secondaryFiles.ContainsKey(key)) secondaryFiles.Add(key, fi);
            }

            // Identify NEW and MODIFIED
            var newFiles = new List<FileInfo>();
            var modifiedFiles = new List<FileInfo>();

            int processed = 0;
            var logLines = new List<string>();

            var keys = secondaryFiles.Keys.ToList();
            int kTotal = keys.Count;
            for (int idx = 0; idx < kTotal; idx++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = keys[idx];
                var secFi = secondaryFiles[key];

                if (!primaryFiles.ContainsKey(key))
                {
                    // NEW
                    newFiles.Add(secFi);
                    logLines.Add($"{secFi.Name} | {secFi.FullName} | {secFi.CreationTime} | {secFi.LastWriteTime} | New");
                }
                else
                {
                    var priFi = primaryFiles[key];
                    if (priFi.LastWriteTime != secFi.LastWriteTime)
                    {
                        modifiedFiles.Add(secFi);
                        logLines.Add($"{secFi.Name} | {secFi.FullName} | {secFi.CreationTime} | {secFi.LastWriteTime} | Modified");
                    }
                }

                processed++;
                ProgressChanged?.Invoke(processed, kTotal, $"Evaluating ({processed}/{kTotal})");
            }

            // Copy files
            int copyIndex = 0;
            int copyTotal = newFiles.Count + modifiedFiles.Count;

            foreach (var nf in newFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dest = Path.Combine(newOut, nf.Name);
                dest = EnsureUniquePath(dest);
                File.Copy(nf.FullName, dest);
                copyIndex++;
                ProgressChanged?.Invoke(copyIndex, copyTotal, $"Copying new files ({copyIndex}/{copyTotal})");
                AuditLogger.Instance.Info($"DeltaFolderComparison: NEW copied {nf.FullName} -> {dest}");
            }

            foreach (var mf in modifiedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dest = Path.Combine(modOut, mf.Name);
                // overwrite if exists
                File.Copy(mf.FullName, dest, true);
                copyIndex++;
                ProgressChanged?.Invoke(copyIndex, copyTotal, $"Copying modified files ({copyIndex}/{copyTotal})");
                AuditLogger.Instance.Info($"DeltaFolderComparison: MODIFIED copied {mf.FullName} -> {dest}");
            }

            // Write Logs.txt
            try
            {
                var logPath = Path.Combine(outputFolder, "Logs.txt");
                File.WriteAllLines(logPath, logLines);
                AuditLogger.Instance.Info("DeltaFolderComparison: Logs written to " + logPath);
            }
            catch (Exception ex)
            {
                AuditLogger.Instance.Warning("DeltaFolderComparison: Failed to write Logs.txt: " + ex.Message);
            }

            return outputFolder;
        }

        private static string EnsureUniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            var dir = Path.GetDirectoryName(path) ?? string.Empty;
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            int i = 1;
            string candidate;
            do
            {
                candidate = Path.Combine(dir, $"{name}({i}){ext}");
                i++;
            } while (File.Exists(candidate));
            return candidate;
        }
    }
}
