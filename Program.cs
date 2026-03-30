using System;
using System.Windows.Forms;

namespace DataUtility
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
             Console.WriteLine("Starting DataUtility...");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // SetHighDpiMode is unavailable on .NET Framework targets; keep DPI settings default.

            Application.ThreadException += (s, e) =>
            {
                try
                {
                    // AuditLogger may not be present in minimal test projects; protect calls.
                    var loggerType = Type.GetType("DataUtility.AuditLogger, " + typeof(Program).Assembly.FullName);
                    if (loggerType != null)
                    {
                        var prop = loggerType.GetProperty("Instance");
                        var inst = prop.GetValue(null);
                        var err = loggerType.GetMethod("Error");
                        if (err != null) err.Invoke(inst, new object[] { "Unhandled UI thread exception", e.Exception });
                    }
                }
                catch { /* ignore logging failures in minimal environment */ }

                MessageBox.Show("Unexpected error:\n" + e.Exception.Message,
                    "Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            Application.Run(new Form1());
        }
    }
}
