using System;
using System.IO;
using System.Xml;

namespace DataUtility
{
    /// <summary>
    /// Reads and exposes all settings from Configuration.xml
    /// </summary>
    public class AppConfig
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        private static AppConfig _instance;
        public static AppConfig Instance => _instance ??= new AppConfig();

        // ── Paths ─────────────────────────────────────────────────────────────
        public string ConfigFilePath { get; private set; }

        // ── General ───────────────────────────────────────────────────────────
        public string ApplicationName { get; private set; }
        public string Version { get; private set; }
        public string LogLevel { get; private set; }
        public bool AuditLogEnabled { get; private set; }
        public string AuditLogFolder { get; private set; }
        public string AuditLogFileName { get; private set; }

        // ── Data Comparison ───────────────────────────────────────────────────
        public string[] ComparisonSupportedExtensions { get; private set; }
        public string PrimaryKeyColumn { get; private set; }
        public string DateModifiedColumn { get; private set; }
        public string SourceDateFormat { get; private set; }
        public string ComparisonOutputSuffix { get; private set; }
        public string ComparisonOutputFormat { get; private set; }
        public bool ComparisonIncludeHeader { get; private set; }
        public string ComparisonDelimiter { get; private set; }
        public bool DetectNewRecords { get; private set; }
        public bool DetectModifiedRecords { get; private set; }
        public bool DetectDeletedRecords { get; private set; }
        public bool CaseSensitiveComparison { get; private set; }
        public bool TrimWhitespace { get; private set; }
        public string[] ExcludeColumnsFromComparison { get; private set; }

        // ── Data Filter ────────────────────────────────────────────────────────
        public string[] FilterSupportedExtensions { get; private set; }
        public string DateFilterColumn { get; private set; }
        public string FilterSourceDateFormat { get; private set; }
        public string FilterMode { get; private set; }
        public string UIDateFormat { get; private set; }
        public string FilterOutputSuffix { get; private set; }
        public string FilterOutputFormat { get; private set; }
        public bool FilterIncludeHeader { get; private set; }
        public string FilterDelimiter { get; private set; }
        public bool IncludeBoundaryDate { get; private set; }

        // ── UI ────────────────────────────────────────────────────────────────
        public int ProgressBarUpdateIntervalMs { get; private set; }
        public bool ShowRecordCount { get; private set; }
        public bool DragAndDropEnabled { get; private set; }

        // ── Constructor ───────────────────────────────────────────────────────
        private AppConfig()
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            ConfigFilePath = Path.Combine(appDir, "Configuration.xml");
            Load();
        }

        // ── Load XML ──────────────────────────────────────────────────────────
        public void Load()
        {
            if (!File.Exists(ConfigFilePath))
                throw new FileNotFoundException($"Configuration file not found: {ConfigFilePath}");

            var doc = new XmlDocument();
            doc.Load(ConfigFilePath);

            // General
            ApplicationName = GetValue(doc, "//General/ApplicationName", "Data Utility");
            Version = GetValue(doc, "//General/Version", "1.0.0");
            LogLevel = GetValue(doc, "//General/LogLevel", "INFO");
            AuditLogEnabled = bool.Parse(GetValue(doc, "//General/AuditLogEnabled", "true"));
            AuditLogFolder = GetValue(doc, "//General/AuditLogFolder", "Logs");
            AuditLogFileName = GetValue(doc, "//General/AuditLogFileName", "AuditLog_{DATE}.txt");

            // Data Comparison
            ComparisonSupportedExtensions = GetNodeList(doc, "//DataComparison/SupportedExtensions/Extension");
            PrimaryKeyColumn = GetValue(doc, "//DataComparison/PrimaryKeyColumn", "ID");
            DateModifiedColumn = GetValue(doc, "//DataComparison/DateModifiedColumn", "DateModified");
            SourceDateFormat = GetValue(doc, "//DataComparison/SourceDateFormat", "yyyy-MM-dd HH:mm:ss");
            ComparisonOutputSuffix = GetValue(doc, "//DataComparison/o/FileNameSuffix", "_Compared");
            ComparisonOutputFormat = GetValue(doc, "//DataComparison/o/Format", "CSV");
            ComparisonIncludeHeader = bool.Parse(GetValue(doc, "//DataComparison/o/IncludeHeader", "true"));
            ComparisonDelimiter = GetValue(doc, "//DataComparison/o/Delimiter", ",");
            DetectNewRecords = bool.Parse(GetValue(doc, "//DataComparison/Options/DetectNewRecords", "true"));
            DetectModifiedRecords = bool.Parse(GetValue(doc, "//DataComparison/Options/DetectModifiedRecords", "true"));
            DetectDeletedRecords = bool.Parse(GetValue(doc, "//DataComparison/Options/DetectDeletedRecords", "false"));
            CaseSensitiveComparison = bool.Parse(GetValue(doc, "//DataComparison/Options/CaseSensitiveComparison", "false"));
            TrimWhitespace = bool.Parse(GetValue(doc, "//DataComparison/Options/TrimWhitespace", "true"));
            ExcludeColumnsFromComparison = GetNodeList(doc, "//DataComparison/Options/ExcludeColumnsFromComparison/Column");

            // Data Filter
            FilterSupportedExtensions = GetNodeList(doc, "//DataFilter/SupportedExtensions/Extension");
            DateFilterColumn = GetValue(doc, "//DataFilter/DateFilterColumn", "DateModified");
            FilterSourceDateFormat = GetValue(doc, "//DataFilter/SourceDateFormat", "yyyy-MM-dd HH:mm:ss");
            FilterMode = GetValue(doc, "//DataFilter/FilterMode", "AFTER");
            UIDateFormat = GetValue(doc, "//DataFilter/UIDateFormat", "dd/MM/yyyy");
            FilterOutputSuffix = GetValue(doc, "//DataFilter/o/FileNameSuffix", "_Filtered");
            FilterOutputFormat = GetValue(doc, "//DataFilter/o/Format", "SAME_AS_INPUT");
            FilterIncludeHeader = bool.Parse(GetValue(doc, "//DataFilter/o/IncludeHeader", "true"));
            FilterDelimiter = GetValue(doc, "//DataFilter/o/Delimiter", ",");
            IncludeBoundaryDate = bool.Parse(GetValue(doc, "//DataFilter/Options/IncludeBoundaryDate", "true"));

            // UI
            ProgressBarUpdateIntervalMs = int.Parse(GetValue(doc, "//UI/ProgressBar/UpdateIntervalMs", "100"));
            ShowRecordCount = bool.Parse(GetValue(doc, "//UI/ProgressBar/ShowRecordCount", "true"));
            DragAndDropEnabled = bool.Parse(GetValue(doc, "//UI/DragAndDrop/Enabled", "true"));
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private string GetValue(XmlDocument doc, string xpath, string defaultValue = "")
        {
            XmlNode node = doc.SelectSingleNode(xpath);
            return node?.InnerText?.Trim() ?? defaultValue;
        }

        private string[] GetNodeList(XmlDocument doc, string xpath)
        {
            XmlNodeList nodes = doc.SelectNodes(xpath);
            if (nodes == null || nodes.Count == 0) return Array.Empty<string>();
            var list = new System.Collections.Generic.List<string>();
            foreach (XmlNode n in nodes)
                if (!string.IsNullOrWhiteSpace(n.InnerText))
                    list.Add(n.InnerText.Trim());
            return list.ToArray();
        }
    }
}
