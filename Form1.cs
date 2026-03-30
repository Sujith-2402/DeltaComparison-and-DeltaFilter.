using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DataUtility
{
    /// <summary>
    /// Main application form.
    /// Hosts two modes (Data Comparison / Data Filter) driven by radio buttons.
    /// </summary>
    public partial class Form1 : Form
    {
        // ── Handlers ───────────────────────────────────────────────────────────
        private readonly DataComparisonHandler _comparisonHandler = new DataComparisonHandler();
        private readonly DataFilterHandler _filterHandler = new DataFilterHandler();
        private readonly DeltaCopyHandler _deltaCopyHandler = new DeltaCopyHandler();

        // ── Cancellation ───────────────────────────────────────────────────────
        private CancellationTokenSource _cts;

        // ── Constructor ───────────────────────────────────────────────────────
        public Form1()
        {
            // Load config first so UI can read settings
            try { _ = AppConfig.Instance; }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load Configuration.xml:\n{ex.Message}",
                    "Configuration Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.Exit(1);
            }

            AuditLogger.Instance.Initialise();

            InitializeComponent();
            WireEvents();
            ApplyInitialState();
            // Populate delta copy defaults
            try
            {
                if (!string.IsNullOrWhiteSpace(AppConfig.Instance.DeltaCopyDefaultSource)) txtSourceCommonPath.Text = AppConfig.Instance.DeltaCopyDefaultSource;
                if (!string.IsNullOrWhiteSpace(AppConfig.Instance.DeltaCopyDefaultDestination)) txtDestinationPath.Text = AppConfig.Instance.DeltaCopyDefaultDestination;
            }
            catch { }
        }

        // ── Event wiring ──────────────────────────────────────────────────────

        private void WireEvents()
        {
            // Radio buttons switch panels
            rbComparison.CheckedChanged += (s, e) => SwitchMode();
            rbFilter.CheckedChanged += (s, e) => SwitchMode();
            rbDeltaCopy.CheckedChanged += (s, e) => SwitchMode();

            // Drag & drop for primary / secondary / filter file boxes
            if (AppConfig.Instance.DragAndDropEnabled)
            {
                SetupDragDrop(txtPrimaryFile, OnPrimaryFileDrop);
                SetupDragDrop(txtSecondaryFile, OnSecondaryFileDrop);
                SetupDragDrop(txtFilterFile, OnFilterFileDrop);
                SetupDragDrop(txtDeltaFile, OnDeltaFileDrop);
            }

            // Browse buttons
            btnBrowsePrimary.Click += (s, e) => BrowseFile(txtPrimaryFile, "primary");
            btnBrowseSecondary.Click += (s, e) => BrowseFile(txtSecondaryFile, "secondary");
            btnBrowseFilter.Click += (s, e) => BrowseFile(txtFilterFile, "filter");
            btnBrowseDeltaFile.Click += (s, e) => BrowseFile(txtDeltaFile, "delta");
            btnBrowseDestination.Click += (s, e) => BrowseFolder(txtDestinationPath);
            btnBrowseSourceCommonPath.Click += (s, e) => BrowseFolder(txtSourceCommonPath);

            // Run / Cancel
            btnRun.Click += BtnRun_Click;

            // Placeholder management
            txtPrimaryFile.GotFocus += (s, e) => ClearPlaceholder(txtPrimaryFile);
            txtPrimaryFile.LostFocus += (s, e) => EnsurePlaceholder(txtPrimaryFile, "Click Browse or drag & drop a file here …");
            txtSecondaryFile.GotFocus += (s, e) => ClearPlaceholder(txtSecondaryFile);
            txtSecondaryFile.LostFocus += (s, e) => EnsurePlaceholder(txtSecondaryFile, "Click Browse or drag & drop a file here …");
            txtFilterFile.GotFocus += (s, e) => ClearPlaceholder(txtFilterFile);
            txtFilterFile.LostFocus += (s, e) => EnsurePlaceholder(txtFilterFile, "Click Browse or drag & drop a file here …");
            txtDeltaFile.GotFocus += (s, e) => ClearPlaceholder(txtDeltaFile);
            txtDeltaFile.LostFocus += (s, e) => EnsurePlaceholder(txtDeltaFile, "Click Browse or drag & drop a file here …");
            txtDestinationPath.GotFocus += (s, e) => ClearPlaceholder(txtDestinationPath);
            txtDestinationPath.LostFocus += (s, e) => EnsurePlaceholder(txtDestinationPath, destinationPlaceholder);
            txtSourceCommonPath.GotFocus += (s, e) => ClearPlaceholder(txtSourceCommonPath);
            txtSourceCommonPath.LostFocus += (s, e) => EnsurePlaceholder(txtSourceCommonPath, sourceCommonPlaceholder);
        }

        private void OnDeltaFileDrop(object sender, DragEventArgs e) => HandleFileDrop(e, txtDeltaFile, "delta");

        private void BrowseFolder(TextBox target)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    target.Text = dlg.SelectedPath;
                    target.ForeColor = Color.FromArgb(220, 220, 220);
                    AuditLogger.Instance.Info($"UI: folder selected: {dlg.SelectedPath}");
                }
            }
        }

        private void ApplyInitialState()
        {
            rbComparison.Checked = true;
            SwitchMode();
            ResetProgress();
        }

        // ── Mode switching ────────────────────────────────────────────────────

        private void SwitchMode()
        {
            // Support three modes: Comparison, Filter, Delta Copy
            bool isComparison = rbComparison.Checked;
            bool isFilter = rbFilter.Checked;
            bool isDeltaCopy = rbDeltaCopy.Checked;

            pnlComparison.Visible = isComparison;
            pnlFilter.Visible = isFilter;
            pnlDeltaCopy.Visible = isDeltaCopy;

            ResetProgress();

            if (isComparison)
            {
                UpdateStatus("Data Comparison mode selected.");
                AuditLogger.Instance.Info("UI: Switched to Data Comparison mode.");
            }
            else if (isFilter)
            {
                UpdateStatus("Data Filter mode selected.");
                AuditLogger.Instance.Info("UI: Switched to Data Filter mode.");
            }
            else if (isDeltaCopy)
            {
                UpdateStatus("Delta Copy mode selected.");
                AuditLogger.Instance.Info("UI: Switched to Delta Copy mode.");
            }
        }

        // ── Browse helpers ────────────────────────────────────────────────────

        private void BrowseFile(TextBox target, string role)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = string.Format("Select {0} file", role);
                dlg.Filter = BuildFilter();
                dlg.CheckFileExists = true;
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    target.Text = dlg.FileName;
                    target.ForeColor = Color.FromArgb(220, 220, 220);
                    AuditLogger.Instance.Info(string.Format("UI: {0} file selected: {1}", role, dlg.FileName));
                }
            }
        }

        private static string BuildFilter()
        {
            return "Supported Files (*.csv;*.txt;*.xlsx;*.xls)|*.csv;*.txt;*.xlsx;*.xls" +
                   "|CSV Files (*.csv)|*.csv" +
                   "|Text Files (*.txt)|*.txt" +
                   "|Excel Files (*.xlsx;*.xls)|*.xlsx;*.xls" +
                   "|All Files (*.*)|*.*";
        }

        // Placeholder helpers for .NET Framework TextBox (no PlaceholderText property)
        private void EnsurePlaceholder(TextBox box, string hint)
        {
            if (string.IsNullOrWhiteSpace(box.Text))
            {
                box.Text = hint;
                box.ForeColor = Color.FromArgb(140, 140, 160);
            }
        }

        private void ClearPlaceholder(TextBox box)
        {
            // If current text looks like the hint (light color), clear it on focus
            if (box.ForeColor.R == 140 && box.ForeColor.G == 140 && box.ForeColor.B == 160)
            {
                box.Text = string.Empty;
                box.ForeColor = Color.FromArgb(220, 220, 220);
            }
        }

        // ── Drag & drop helpers ───────────────────────────────────────────────

        private static void SetupDragDrop(TextBox box, DragEventHandler dropHandler)
        {
            box.AllowDrop = true;
            box.DragEnter += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                    e.Effect = DragDropEffects.Copy;
            };
            box.DragDrop += dropHandler;
        }

        private void OnPrimaryFileDrop(object sender, DragEventArgs e)
            => HandleFileDrop(e, txtPrimaryFile, "primary");

        private void OnSecondaryFileDrop(object sender, DragEventArgs e)
            => HandleFileDrop(e, txtSecondaryFile, "secondary");

        private void OnFilterFileDrop(object sender, DragEventArgs e)
            => HandleFileDrop(e, txtFilterFile, "filter");

        private void HandleFileDrop(DragEventArgs e, TextBox target, string role)
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0)
            {
                target.Text = files[0];
                target.ForeColor = Color.FromArgb(220, 220, 220);
                AuditLogger.Instance.Info($"UI: {role} file dropped: {files[0]}");
            }
        }

        // ── Run / Cancel ──────────────────────────────────────────────────────

        private async void BtnRun_Click(object sender, EventArgs e)
        {
            if (btnRun.Tag is "running")
            {
                _cts?.Cancel();
                return;
            }

            if (!ValidateInputs(out string error))
            {
                MessageBox.Show(error, "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Switch button to Cancel mode
            btnRun.Text = "Cancel";
            btnRun.Tag = "running";
            btnRun.BackColor = Color.FromArgb(192, 0, 0);
            SetControlsEnabled(false);
            ResetProgress();

            _cts = new CancellationTokenSource();

            try
            {
                string outputPath;

                if (rbComparison.Checked)
                {
                    _comparisonHandler.ProgressChanged += OnProgressChanged;
                    outputPath = await _comparisonHandler.RunAsync(
                        txtPrimaryFile.Text.Trim(),
                        txtSecondaryFile.Text.Trim(),
                        _cts.Token);
                    _comparisonHandler.ProgressChanged -= OnProgressChanged;
                }
                else if (rbFilter.Checked)
                {
                    _filterHandler.ProgressChanged += OnProgressChanged;

                    string filterMode = rbFilterAfter.Checked ? "AFTER"
                        : rbFilterBefore.Checked ? "BEFORE"
                        : rbFilterExact.Checked ? "EXACT"
                        : "BETWEEN";

                    DateTime filterDate = dtpFilterDate.Value.Date;
                    DateTime? endDate = rbFilterBetween.Checked ? dtpEndDate.Value.Date : (DateTime?)null;

                    outputPath = await _filterHandler.RunAsync(
                        txtFilterFile.Text.Trim(),
                        filterDate,
                        filterMode,
                        endDate,
                        _cts.Token);

                    _filterHandler.ProgressChanged -= OnProgressChanged;
                }
                else
                {
                    // Delta Copy mode
                    _deltaCopyHandler.ProgressChanged += OnProgressChanged;
                    // Ensure audit log is written to the same folder as the delta file
                    try
                    {
                        var deltaFolder = Path.GetDirectoryName(txtDeltaFile.Text.Trim());
                        if (!string.IsNullOrWhiteSpace(deltaFolder)) AuditLogger.Instance.SetOutputFolder(deltaFolder);
                    }
                    catch { }

                    outputPath = await _deltaCopyHandler.RunAsync(
                        txtDeltaFile.Text.Trim(),
                        txtSourceCommonPath.Text.Trim(),
                        txtDestinationPath.Text.Trim(),
                        _cts.Token);
                    _deltaCopyHandler.ProgressChanged -= OnProgressChanged;

                    // create a converted marker file next to the delta input (same base name)
                    try
                    {
                        var deltaFolder = Path.GetDirectoryName(txtDeltaFile.Text.Trim());
                        var baseName = Path.GetFileNameWithoutExtension(txtDeltaFile.Text.Trim());
                        var converted = Path.Combine(deltaFolder ?? string.Empty, baseName + ".converted.txt");
                        File.WriteAllText(converted, "Converted: " + DateTime.Now.ToString("o") + Environment.NewLine + "Source=" + txtSourceCommonPath.Text + Environment.NewLine + "Destination=" + txtDestinationPath.Text);
                        AuditLogger.Instance.Info("Converted marker written: " + converted);
                    }
                    catch { }
                }

                progressBar.Value = progressBar.Maximum;
                UpdateStatus($"✔  Done! Output: {outputPath}");
                lblProgressCount.Text = "100%";

                MessageBox.Show(
                    $"Operation completed successfully!\n\nOutput file:\n{outputPath}",
                    "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);

                AuditLogger.Instance.Info($"Operation completed. Output: {outputPath}");
            }
            catch (OperationCanceledException)
            {
                UpdateStatus("⚠  Operation cancelled by user.");
                AuditLogger.Instance.Warning("Operation cancelled by user.");
            }
            catch (Exception ex)
            {
                UpdateStatus($"✘  Error: {ex.Message}");
                AuditLogger.Instance.Error("Unhandled error during operation", ex);
                MessageBox.Show($"Error:\n{ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnRun.Text = "▶   Run";
                btnRun.Tag = null;
                btnRun.BackColor = Color.FromArgb(0, 122, 204);
                SetControlsEnabled(true);
                _cts?.Dispose();
                _cts = null;
                AuditLogger.Instance.SessionEnd();
            }
        }

        // ── Progress callback ─────────────────────────────────────────────────

        private void OnProgressChanged(int current, int total, string message)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<int, int, string>(OnProgressChanged), current, total, message);
                return;
            }

            if (total > 0)
            {
                int percent = (int)((double)current / total * 100);
                progressBar.Value = Math.Min(percent, progressBar.Maximum);
                lblProgressCount.Text = AppConfig.Instance.ShowRecordCount
                    ? $"{current} / {total} ({percent}%)"
                    : $"{percent}%";
            }

            UpdateStatus(message);
        }

        // ── Validation ────────────────────────────────────────────────────────

        private bool ValidateInputs(out string error)
        {
            error = string.Empty;

            if (rbComparison.Checked)
            {
                if (string.IsNullOrWhiteSpace(txtPrimaryFile.Text))
                { error = "Please select a Primary file."; return false; }
                if (!File.Exists(txtPrimaryFile.Text.Trim()))
                { error = "Primary file does not exist."; return false; }
                if (string.IsNullOrWhiteSpace(txtSecondaryFile.Text))
                { error = "Please select a Secondary file."; return false; }
                if (!File.Exists(txtSecondaryFile.Text.Trim()))
                { error = "Secondary file does not exist."; return false; }
            }
            else if (rbFilter.Checked)
            {
                var filterPath = txtFilterFile.Text?.Trim() ?? string.Empty;
                // treat placeholder as empty
                if (filterPath == filterPlaceholder) filterPath = string.Empty;
                if (string.IsNullOrWhiteSpace(filterPath))
                { error = "Please select an Input file for filtering."; return false; }
                if (!File.Exists(filterPath))
                { error = "Filter input file does not exist."; return false; }
                if (rbFilterBetween.Checked && dtpEndDate.Value.Date < dtpFilterDate.Value.Date)
                { error = "End date must be on or after the Start date."; return false; }
            }
            else if (rbDeltaCopy.Checked)
            {
                var deltaPath = txtDeltaFile.Text?.Trim() ?? string.Empty;
                if (deltaPath == deltaPlaceholder) deltaPath = string.Empty;
                if (string.IsNullOrWhiteSpace(deltaPath)) { error = "Please select a Delta file."; return false; }
                if (!File.Exists(deltaPath)) { error = "Delta file does not exist."; return false; }
                var dest = txtDestinationPath.Text?.Trim() ?? string.Empty;
                if (dest == destinationPlaceholder) dest = string.Empty;
                if (string.IsNullOrWhiteSpace(dest)) { error = "Please select a Destination base path."; return false; }
                if (!Directory.Exists(dest)) { error = "Destination base path does not exist."; return false; }
                var src = txtSourceCommonPath.Text?.Trim() ?? string.Empty;
                if (src == sourceCommonPlaceholder) src = string.Empty;
                if (string.IsNullOrWhiteSpace(src)) { error = "Please select a Source common path."; return false; }
                if (!Directory.Exists(src)) { error = "Source common path does not exist."; return false; }
            }

            return true;
        }

        // ── UI helpers ────────────────────────────────────────────────────────

        private void UpdateStatus(string message)
        {
            if (InvokeRequired) { Invoke(new Action<string>(UpdateStatus), message); return; }
            lblStatus.Text = message;
        }

        private void ResetProgress()
        {
            progressBar.Value = 0;
            lblProgressCount.Text = "0%";
            lblStatus.Text = "Ready.";
        }

        private void SetControlsEnabled(bool enabled)
        {
            rbComparison.Enabled = enabled;
            rbFilter.Enabled = enabled;
            rbDeltaCopy.Enabled = enabled;
            btnBrowsePrimary.Enabled = enabled;
            btnBrowseSecondary.Enabled = enabled;
            btnBrowseFilter.Enabled = enabled;
            btnBrowseDeltaFile.Enabled = enabled;
            btnBrowseDestination.Enabled = enabled;
            btnBrowseSourceCommonPath.Enabled = enabled;
            txtPrimaryFile.Enabled = enabled;
            txtSecondaryFile.Enabled = enabled;
            txtFilterFile.Enabled = enabled;
            txtDeltaFile.Enabled = enabled;
            txtDestinationPath.Enabled = enabled;
            txtSourceCommonPath.Enabled = enabled;
            dtpFilterDate.Enabled = enabled;
            dtpEndDate.Enabled = enabled;
            rbFilterAfter.Enabled = enabled;
            rbFilterBefore.Enabled = enabled;
            rbFilterExact.Enabled = enabled;
            rbFilterBetween.Enabled = enabled;
        }
    }
}
