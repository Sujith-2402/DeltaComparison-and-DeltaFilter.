using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace DataUtility
{
    // Lightweight AppConfig that reads a simple Configuration.xml if present.
    // Supported elements:
    // <Configuration>
    //   <DragAndDropEnabled>true</DragAndDropEnabled>
    //   <ShowRecordCount>false</ShowRecordCount>
    //   <DateFormats>
    //     <Format>yyyy-MM-dd</Format>
    //   </DateFormats>
    //   <DateColumnIndex>0</DateColumnIndex>
    //   <OutputDirectory>c:\out</OutputDirectory>
    // </Configuration>
    public class AppConfig
    {
        public static AppConfig Instance { get; } = new AppConfig();

        public bool DragAndDropEnabled { get; private set; }
        public bool ShowRecordCount { get; private set; }
        public string[] DateFormats { get; private set; }
        public int DateColumnIndex { get; private set; }
        public string OutputDirectory { get; private set; }
        public string DeltaCopyDefaultSource { get; private set; }
        public string DeltaCopyDefaultDestination { get; private set; }

        private AppConfig()
        {
            // sensible defaults
            DragAndDropEnabled = true;
            ShowRecordCount = false;
            DateFormats = new[] { "yyyy-MM-dd", "MM/dd/yyyy", "M/d/yyyy", "yyyyMMdd" };
            DateColumnIndex = 0;
            OutputDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Output");
            DeltaCopyDefaultSource = string.Empty;
            DeltaCopyDefaultDestination = string.Empty;

            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Configuration.xml");
                if (!File.Exists(path)) return;

                var doc = XDocument.Load(path);
                var root = doc.Element("Configuration");
                if (root == null) return;

                bool b;
                var n = root.Element("DragAndDropEnabled");
                if (n != null && bool.TryParse(n.Value.Trim(), out b)) DragAndDropEnabled = b;

                n = root.Element("ShowRecordCount");
                if (n != null && bool.TryParse(n.Value.Trim(), out b)) ShowRecordCount = b;

                var formats = root.Element("DateFormats");
                if (formats != null)
                {
                    var f = formats.Elements("Format").Select(x => x.Value.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToArray();
                    if (f.Length > 0) DateFormats = f;
                }

                var idx = root.Element("DateColumnIndex");
                if (idx != null)
                {
                    int v;
                    if (int.TryParse(idx.Value.Trim(), out v)) DateColumnIndex = v;
                }

                var outDir = root.Element("OutputDirectory");
                if (outDir != null && !string.IsNullOrWhiteSpace(outDir.Value))
                {
                    OutputDirectory = Path.GetFullPath(outDir.Value.Trim());
                }

                var delta = root.Element("DeltaCopy");
                if (delta != null)
                {
                    var ds = delta.Element("DefaultSource");
                    if (ds != null) DeltaCopyDefaultSource = ds.Value.Trim();
                    var dd = delta.Element("DefaultDestination");
                    if (dd != null) DeltaCopyDefaultDestination = dd.Value.Trim();
                }

                // ensure directory exists
                if (!Directory.Exists(OutputDirectory)) Directory.CreateDirectory(OutputDirectory);
            }
            catch
            {
                // ignore configuration errors; defaults will be used
            }
        }
    }
}
