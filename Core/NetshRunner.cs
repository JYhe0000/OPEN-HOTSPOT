using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

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

            using (Process process = new Process())
            {
                process.StartInfo = info;
                process.Start();

                byte[] standardBytes = null;
                byte[] errorBytes = null;
                object gate = new object();

                Thread standardThread = new Thread(delegate
                {
                    try
                    {
                        byte[] data = ReadAll(process.StandardOutput.BaseStream);
                        lock (gate) { standardBytes = data; }
                    }
                    catch
                    {
                    }
                });
                standardThread.IsBackground = true;

                Thread errorThread = new Thread(delegate
                {
                    try
                    {
                        byte[] data = ReadAll(process.StandardError.BaseStream);
                        lock (gate) { errorBytes = data; }
                    }
                    catch
                    {
                    }
                });
                errorThread.IsBackground = true;

                standardThread.Start();
                errorThread.Start();

                bool exited = process.WaitForExit(60000);
                if (!exited)
                {
                    try { process.Kill(); } catch { }
                }
                else
                {
                    process.WaitForExit();
                }

                standardThread.Join(5000);
                errorThread.Join(5000);

                ProcessResult result = new ProcessResult();
                result.ExitCode = exited ? process.ExitCode : -1;
                lock (gate)
                {
                    result.Output = Decode(standardBytes) + Decode(errorBytes);
                }
                return result;
            }
        }

        private static byte[] ReadAll(Stream stream)
        {
            MemoryStream buffer = new MemoryStream();
            byte[] chunk = new byte[4096];
            while (true)
            {
                int read = stream.Read(chunk, 0, chunk.Length);
                if (read <= 0) break;
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }

        private static string Decode(byte[] data)
        {
            if (data == null || data.Length == 0) return string.Empty;

            int offset = 0;
            int count = data.Length;
            if (count >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) offset = 3;

            Encoding encoding;
            if (TryCreateUtf8(out encoding))
            {
                try { return encoding.GetString(data, offset, data.Length - offset); }
                catch { }
            }

            encoding = TryGetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            if (encoding != null)
            {
                try { return encoding.GetString(data, offset, data.Length - offset); }
                catch { }
            }

            return Encoding.Default.GetString(data, offset, data.Length - offset);
        }

        private static bool TryCreateUtf8(out Encoding encoding)
        {
            try
            {
                encoding = new UTF8Encoding(false, true);
                return true;
            }
            catch
            {
                encoding = null;
                return false;
            }
        }

        private static Encoding TryGetEncoding(int codePage)
        {
            try { return Encoding.GetEncoding(codePage); }
            catch { return null; }
        }
    }
}
