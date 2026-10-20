using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace OpenHotspot
{
    public sealed class ProcessResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; }

        public bool Success
        {
            get { return ExitCode == 0; }
        }
    }

    public static class NetshRunner
    {
        public static ProcessResult Run(string arguments)
        {
            return RunFile(Path.Combine(Environment.SystemDirectory, "netsh.exe"), arguments);
        }

        public static ProcessResult RunFile(string fileName, string arguments)
        {
            ProcessStartInfo info = new ProcessStartInfo(fileName, arguments);
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WindowStyle = ProcessWindowStyle.Hidden;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;

            try
            {
                Encoding encoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
                info.StandardOutputEncoding = encoding;
                info.StandardErrorEncoding = encoding;
            }
            catch
            {
            }

            StringBuilder output = new StringBuilder();
            object gate = new object();

            using (Process process = new Process())
            {
                process.StartInfo = info;
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (gate) { output.AppendLine(e.Data); }
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (gate) { output.AppendLine(e.Data); }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                bool exited = process.WaitForExit(60000);
                if (!exited)
                {
                    try { process.Kill(); } catch { }
                }
                else
                {
                    process.WaitForExit();
                }

                ProcessResult result = new ProcessResult();
                result.ExitCode = exited ? process.ExitCode : -1;
                lock (gate) { result.Output = output.ToString(); }
                return result;
            }
        }
    }
}
