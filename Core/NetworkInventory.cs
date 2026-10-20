using System;
using System.Collections.Generic;
using System.Management;
using System.Net;
using System.Net.Sockets;

namespace OpenHotspot
{
    public sealed class AdapterInfo
    {
        public uint Index { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public bool Enabled { get; set; }
        public bool IpEnabled { get; set; }
        public bool DhcpEnabled { get; set; }
        public string[] IpAddresses { get; set; }
        public string[] Subnets { get; set; }
        public string[] Gateways { get; set; }
        public string[] DnsServers { get; set; }

        public AdapterInfo()
        {
            Name = "";
            Description = "";
            IpAddresses = new string[0];
            Subnets = new string[0];
            Gateways = new string[0];
            DnsServers = new string[0];
        }

        public override string ToString()
        {
            return Name + "  -  " + Description;
        }
    }

    public static class NetworkInventory
    {
        public static List<AdapterInfo> GetAdapters()
        {
            List<AdapterInfo> result = new List<AdapterInfo>();
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                "SELECT Index, NetConnectionID, Description, NetEnabled FROM Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL"))
            {
                foreach (ManagementObject item in searcher.Get())
                {
                    AdapterInfo adapter = new AdapterInfo();
                    adapter.Index = ToUInt(item["Index"]);
                    adapter.Name = Convert.ToString(item["NetConnectionID"]);
                    adapter.Description = Convert.ToString(item["Description"]);
                    adapter.Enabled = ToBool(item["NetEnabled"]);
                    FillConfiguration(adapter);
                    result.Add(adapter);
                    item.Dispose();
                }
            }
            result.Sort(delegate(AdapterInfo a, AdapterInfo b)
            {
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            return result;
        }

        public static AdapterInfo FindByName(List<AdapterInfo> adapters, string name)
        {
            if (adapters == null || name == null) return null;
            foreach (AdapterInfo adapter in adapters)
            {
                if (string.Equals(adapter.Name, name, StringComparison.OrdinalIgnoreCase)) return adapter;
            }
            return null;
        }

        public static AdapterInfo FindVirtualAdapter(List<AdapterInfo> adapters)
        {
            if (adapters == null) return null;
            foreach (AdapterInfo adapter in adapters)
            {
                string description = adapter.Description == null ? "" : adapter.Description;
                if (description.IndexOf("Virtual WiFi", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    description.IndexOf("Virtual Wi-Fi", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    description.IndexOf("虚拟", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return adapter;
                }
            }
            return null;
        }

        public static bool HasAddress(AdapterInfo adapter, string ip, string mask, string gateway)
        {
            if (adapter == null) return false;
            return Contains(adapter.IpAddresses, ip) &&
                   Contains(adapter.Subnets, mask) &&
                   Contains(adapter.Gateways, gateway);
        }

        public static bool Contains(string[] values, string expected)
        {
            if (values == null) return false;
            foreach (string value in values)
            {
                if (string.Equals(value, expected, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static bool IsIPv4(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            IPAddress address;
            if (!IPAddress.TryParse(value, out address)) return false;
            return address.AddressFamily == AddressFamily.InterNetwork;
        }

        public static void ApplyStatic(AdapterInfo adapter, string ip, string mask, string gateway)
        {
            if (adapter == null) throw new ArgumentNullException("adapter");
            using (ManagementObject config = GetConfigurationObject(adapter.Index))
            {
                if (config == null) throw new InvalidOperationException("找不到网卡配置对象。");

                uint code = Invoke(config, "EnableStatic", new object[]
                {
                    new string[] { ip },
                    new string[] { mask }
                });
                if (code != 0 && code != 1)
                {
                    throw new InvalidOperationException("设置静态 IP 失败，WMI 返回 " + code.ToString() + "。");
                }

                System.Threading.Thread.Sleep(600);

                if (!string.IsNullOrEmpty(gateway))
                {
                    code = Invoke(config, "SetGateways", new object[]
                    {
                        new string[] { gateway },
                        new ushort[] { 1 }
                    });
                    if (code != 0 && code != 1)
                    {
                        throw new InvalidOperationException("设置默认网关失败，WMI 返回 " + code.ToString() + "。");
                    }
                }
            }
        }

        public static void Restore(AdapterInfo snapshot)
        {
            if (snapshot == null) return;
            using (ManagementObject config = GetConfigurationObject(snapshot.Index))
            {
                if (config == null) return;

                if (snapshot.DhcpEnabled)
                {
                    Invoke(config, "EnableDHCP", new object[0]);
                    return;
                }

                List<string> addresses = new List<string>();
                List<string> masks = new List<string>();
                for (int i = 0; i < snapshot.IpAddresses.Length; i++)
                {
                    if (!IsIPv4(snapshot.IpAddresses[i])) continue;
                    addresses.Add(snapshot.IpAddresses[i]);
                    masks.Add(i < snapshot.Subnets.Length ? snapshot.Subnets[i] : "255.255.255.0");
                }
                if (addresses.Count == 0) return;

                Invoke(config, "EnableStatic", new object[]
                {
                    addresses.ToArray(),
                    masks.ToArray()
                });

                List<string> gateways = new List<string>();
                foreach (string gateway in snapshot.Gateways)
                {
                    if (IsIPv4(gateway)) gateways.Add(gateway);
                }
                if (gateways.Count > 0)
                {
                    ushort[] metrics = new ushort[gateways.Count];
                    for (int i = 0; i < metrics.Length; i++) metrics[i] = 1;
                    Invoke(config, "SetGateways", new object[]
                    {
                        gateways.ToArray(),
                        metrics
                    });
                }

                if (snapshot.DnsServers != null && snapshot.DnsServers.Length > 0)
                {
                    Invoke(config, "SetDNSServerSearchOrder", new object[]
                    {
                        snapshot.DnsServers
                    });
                }
            }
        }

        private static void FillConfiguration(AdapterInfo adapter)
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE Index = " + adapter.Index.ToString()))
                {
                    foreach (ManagementObject item in searcher.Get())
                    {
                        adapter.IpEnabled = ToBool(item["IPEnabled"]);
                        adapter.DhcpEnabled = ToBool(item["DHCPEnabled"]);
                        adapter.IpAddresses = ToArray(item["IPAddress"]);
                        adapter.Subnets = ToArray(item["IPSubnet"]);
                        adapter.Gateways = ToArray(item["DefaultIPGateway"]);
                        adapter.DnsServers = ToArray(item["DNSServerSearchOrder"]);
                        item.Dispose();
                        break;
                    }
                }
            }
            catch
            {
            }
        }

        private static ManagementObject GetConfigurationObject(uint index)
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE Index = " + index.ToString()))
            {
                foreach (ManagementObject item in searcher.Get())
                {
                    return item;
                }
            }
            return null;
        }

        private static uint Invoke(ManagementObject target, string method, object[] arguments)
        {
            object result = target.InvokeMethod(method, arguments);
            if (result == null) return 0;
            return Convert.ToUInt32(result);
        }

        private static bool ToBool(object value)
        {
            if (value == null) return false;
            try { return Convert.ToBoolean(value); }
            catch { return false; }
        }

        private static uint ToUInt(object value)
        {
            if (value == null) return 0;
            try { return Convert.ToUInt32(value); }
            catch { return 0; }
        }

        private static string[] ToArray(object value)
        {
            string[] values = value as string[];
            return values == null ? new string[0] : values;
        }
    }
}
