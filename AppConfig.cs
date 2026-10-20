using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;

namespace OpenHotspot
{
    public sealed class AppConfig
    {
        public string WiredName { get; set; }
        public string HostedName { get; set; }
        public string Ssid { get; set; }
        public string Ip { get; set; }
        public string Mask { get; set; }
        public string Gateway { get; set; }
        public bool AutoSetStaticIp { get; set; }
        public bool RestoreOnStop { get; set; }
        public string ProtectedKey { get; set; }

        public AppConfig()
        {
            WiredName = "本地连接";
            HostedName = "无线网络连接 2";
            Ssid = "Student-WiFi";
            Ip = "10.40.2.155";
            Mask = "255.255.255.128";
            Gateway = "10.40.2.129";
            AutoSetStaticIp = true;
            RestoreOnStop = true;
            ProtectedKey = "";
        }

        [XmlIgnore]
        public string Password
        {
            get { return Unprotect(ProtectedKey); }
            set { ProtectedKey = Protect(value); }
        }

        public static string ConfigDirectory
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenHotspot");
            }
        }

        public static string ConfigPath
        {
            get { return Path.Combine(ConfigDirectory, "config.xml"); }
        }

        public static AppConfig Load()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return new AppConfig();
                using (FileStream stream = File.OpenRead(ConfigPath))
                {
                    XmlSerializer serializer = new XmlSerializer(typeof(AppConfig));
                    AppConfig config = serializer.Deserialize(stream) as AppConfig;
                    return config == null ? new AppConfig() : config;
                }
            }
            catch
            {
                return new AppConfig();
            }
        }

        public void Save()
        {
            Directory.CreateDirectory(ConfigDirectory);
            using (FileStream stream = File.Create(ConfigPath))
            {
                XmlSerializer serializer = new XmlSerializer(typeof(AppConfig));
                serializer.Serialize(stream, this);
            }
        }

        private static string Protect(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(value);
                byte[] encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(encrypted);
            }
            catch
            {
                return "";
            }
        }

        private static string Unprotect(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            try
            {
                byte[] data = Convert.FromBase64String(value);
                byte[] plain = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch
            {
                return "";
            }
        }
    }
}
