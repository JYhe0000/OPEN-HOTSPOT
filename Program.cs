using System;
using System.Threading;
using System.Windows.Forms;

namespace OpenHotspot
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool created;
            using (Mutex mutex = new Mutex(true, "OpenHotspot_SingleInstance_6F1C2A7B", out created))
            {
                if (!created)
                {
                    MessageBox.Show("OpenHotspot 已经在运行。", "OpenHotspot", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
                {
                    ShowError(e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
                {
                    ShowError(e.ExceptionObject as Exception);
                };

                Application.Run(new MainForm());
                GC.KeepAlive(mutex);
            }
        }

        private static void ShowError(Exception exception)
        {
            if (exception == null) return;
            MessageBox.Show(exception.Message, "OpenHotspot", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
