using System;
using System.Collections.Generic;
using System.Management;
using System.Reflection;
using Microsoft.Win32;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using System.Threading;

namespace OpenHotspot
{
    public delegate void LogHandler(string message);

    public sealed class HotspotState
    {
        public bool Running { get; set; }
        public string Ssid { get; set; }
        public string WiredName { get; set; }
        public string HostedName { get; set; }
        public string WiredIp { get; set; }
        public string Detail { get; set; }

        public HotspotState()
        {
            Ssid = "";
            WiredName = "";
            HostedName = "";
            WiredIp = "";
            Detail = "";
        }
    }

    public sealed class OperationResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public HotspotState State { get; set; }
    }

    public sealed class HotspotService
    {
        private AdapterInfo _wiredSnapshot;
        private bool _ipChanged;
        private bool _sharingTouched;
        private string _activeWiredName;
        private string _activeHostedName;
        private readonly List<ServiceStartRecord> _serviceStartRecords = new List<ServiceStartRecord>();

        private sealed class ServiceStartRecord
        {
            public string Name;
            public int StartType;
        }

        public OperationResult Start(AppConfig config, LogHandler log)
        {
            if (log == null) log = delegate(string message) { };
            if (!_ipChanged) _wiredSnapshot = null;
            _sharingTouched = false;
            _activeWiredName = "";
            _activeHostedName = "";
            _serviceStartRecords.Clear();

            try
            {
                ValidateConfig(config);
                EnsureAdministrator();

                EnsureService("WLANSVC", log);
                EnsureService("SharedAccess", log);

                log("正在检查无线网卡是否支持承载网络……");
                string driverOutput;
                int supported = CheckHostedNetworkSupport(out driverOutput);
                if (supported == 0)
                {
                    return Fail("网卡驱动不支持承载网络（Hosted network supported: No）。请更换驱动，或改用网卡厂商的 SoftAP 工具。\r\n\r\n" + driverOutput);
                }
                if (supported < 0)
                {
                    log("警告：无法从驱动输出确认承载网络支持状态（可能未检测到无线网卡，或系统码页导致输出无法解析）。将继续尝试启动，若失败请以 netsh 报错为准。\r\n" + driverOutput);
                }
                else
                {
                    log("承载网络支持检查通过。");
                }

                List<AdapterInfo> adapters = NetworkInventory.GetAdapters();
                AdapterInfo wired = NetworkInventory.FindByName(adapters, config.WiredName);
                if (wired == null)
                {
                    return Fail("找不到有线连接“" + config.WiredName + "”。系统检测到：" + JoinAdapterNames(adapters));
                }
                if (!wired.Enabled)
                {
                    return Fail("有线连接“" + config.WiredName + "”已禁用。");
                }
                if (!wired.IpEnabled)
                {
                    return Fail("无法读取有线连接“" + config.WiredName + "”的 IPv4 配置。");
                }

                log("有线连接：" + wired.Name + "，当前 IP " + Join(wired.IpAddresses) + "，网关 " + Join(wired.Gateways));

                if (!NetworkInventory.HasAddress(wired, config.Ip, config.Mask, config.Gateway))
                {
                    if (!config.AutoSetStaticIp)
                    {
                        return Fail("有线 IP、掩码或网关与设定值不一致，且未启用自动设置。\r\n当前：" + Describe(wired));
                    }

                    log("正在应用静态 IP " + config.Ip + " / " + config.Mask + "，网关 " + config.Gateway + "……");
                    _wiredSnapshot = wired;
                    _ipChanged = true;
                    NetworkInventory.ApplyStatic(wired, config.Ip, config.Mask, config.Gateway);

                    bool verified = false;
                    for (int attempt = 0; attempt < 6; attempt++)
                    {
                        Thread.Sleep(1000);
                        wired = NetworkInventory.FindByName(NetworkInventory.GetAdapters(), config.WiredName);
                        if (wired != null && NetworkInventory.HasAddress(wired, config.Ip, config.Mask, config.Gateway))
                        {
                            verified = true;
                            break;
                        }
                    }
                    if (!verified)
                    {
                        Rollback(log);
                        return Fail("静态 IP 设置后校验失败，已回滚。");
                    }
                    log("静态 IP 设置成功。");
                }
                else
                {
                    log("有线静态 IP、掩码和网关校验通过。");
                }

                string key = config.Password;
                log("正在清理遗留热点……");
                NetshRunner.Run("wlan stop hostednetwork");
                Thread.Sleep(800);

                log("正在配置承载网络……");
                ProcessResult setResult = NetshRunner.Run(
                    "wlan set hostednetwork mode=allow ssid=" + QuoteNetsh(config.Ssid) +
                    " key=" + QuoteNetsh(key) + " keyUsage=persistent");
                if (!setResult.Success)
                {
                    Rollback(log);
                    return Fail("设置承载网络失败。\r\n" + setResult.Output);
                }

                log("正在启动热点……");
                ProcessResult startResult = NetshRunner.Run("wlan start hostednetwork");
                if (!startResult.Success)
                {
                    Rollback(log);
                    return Fail("启动承载网络失败。请检查无线网卡驱动与 WLAN AutoConfig 服务。\r\n" + startResult.Output);
                }
                log("承载网络已启动，等待虚拟网卡就绪……");

                IcsManager ics = new IcsManager();
                List<IcsConnection> connections = null;
                IcsConnection source = null;
                IcsConnection target = null;
                string hostedName = config.HostedName;

                for (int attempt = 0; attempt < 15; attempt++)
                {
                    connections = ics.GetConnections();
                    source = FindConnection(connections, config.WiredName);
                    target = FindConnection(connections, hostedName);
                    if (source != null && target != null) break;
                    Thread.Sleep(1000);
                }

                if (target == null)
                {
                    AdapterInfo virtualAdapter = NetworkInventory.FindVirtualAdapter(NetworkInventory.GetAdapters());
                    if (virtualAdapter != null &&
                        !string.Equals(virtualAdapter.Name, hostedName, StringComparison.OrdinalIgnoreCase))
                    {
                        log("未找到“" + hostedName + "”，改用检测到的热点网卡“" + virtualAdapter.Name + "”。");
                        hostedName = virtualAdapter.Name;
                        connections = ics.GetConnections();
                        target = FindConnection(connections, hostedName);
                    }
                }

                if (connections == null) connections = ics.GetConnections();
                if (source == null) source = FindConnection(connections, config.WiredName);

                if (source == null)
                {
                    Rollback(log);
                    return Fail("无法在连接共享管理器中找到有线连接“" + config.WiredName + "”。");
                }
                if (target == null)
                {
                    Rollback(log);
                    return Fail("热点虚拟网卡没有出现。当前连接：" + JoinConnectionNames(connections) + "。请修正热点网卡名称后重试。");
                }
                if (string.Equals(source.Name, target.Name, StringComparison.OrdinalIgnoreCase) ||
                    CountConnections(connections, source.Name) != 1 ||
                    CountConnections(connections, target.Name) != 1)
                {
                    Rollback(log);
                    return Fail("无法唯一识别有线共享来源和热点虚拟网卡，请检查网络连接名称。");
                }

                List<IcsConnection> unexpected = new List<IcsConnection>();
                foreach (IcsConnection connection in connections)
                {
                    if (connection.SharingEnabled &&
                        !string.Equals(connection.Name, source.Name, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(connection.Name, target.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        unexpected.Add(connection);
                    }
                }
                if (unexpected.Count > 0)
                {
                    Rollback(log);
                    return Fail("检测到其他连接正在使用 ICS：" + JoinConnectionNames(unexpected) + "。请先关闭无关共享后重试。");
                }

                log("正在启用 Internet 连接共享……");
                _sharingTouched = true;
                _activeWiredName = source.Name;
                _activeHostedName = target.Name;
                try
                {
                    if (source.SharingEnabled) ics.DisableSharing(source);
                    if (target.SharingEnabled) ics.DisableSharing(target);
                    ics.EnableSharing(source, 0);
                    ics.EnableSharing(target, 1);
                }
                catch (Exception ex)
                {
                    Rollback(log);
                    return Fail("启用 ICS 失败：" + ErrorMessage(ex));
                }

                Thread.Sleep(2000);
                List<IcsConnection> after = ics.GetConnections();
                IcsConnection sourceAfter = FindConnection(after, source.Name);
                IcsConnection targetAfter = FindConnection(after, target.Name);
                if (sourceAfter == null || targetAfter == null ||
                    !sourceAfter.SharingEnabled || !targetAfter.SharingEnabled)
                {
                    Rollback(log);
                    return Fail("ICS 配置未能确认，已关闭热点并撤销本次共享设置。");
                }

                AdapterInfo verify = NetworkInventory.FindByName(NetworkInventory.GetAdapters(), config.WiredName);
                string verifyIp = verify == null ? "" : Join(verify.IpAddresses);
                if (verify != null && !NetworkInventory.Contains(verify.IpAddresses, config.Ip))
                {
                    log("警告：有线 IP 当前为 " + verifyIp + "，与设定值 " + config.Ip + " 不一致。");
                }

                HotspotState state = new HotspotState();
                state.Running = true;
                state.Ssid = config.Ssid;
                state.WiredName = source.Name;
                state.HostedName = target.Name;
                state.WiredIp = verifyIp;
                state.Detail = "热点已启动";

                log("热点已启动。SSID：" + config.Ssid + "，来源：" + source.Name + "，目标：" + target.Name);
                return Success(state, "热点已启动。");
            }
            catch (Exception ex)
            {
                Rollback(log);
                return Fail(ErrorMessage(ex));
            }
        }

        public OperationResult Stop(AppConfig config, LogHandler log)
        {
            if (log == null) log = delegate(string message) { };

            try
            {
                IcsManager ics = null;
                try { ics = new IcsManager(); }
                catch (Exception ex) { log("无法打开共享管理组件：" + ErrorMessage(ex)); }

                if (ics != null)
                {
                    try
                    {
                        List<IcsConnection> connections = ics.GetConnections();
                        List<AdapterInfo> adapters = NetworkInventory.GetAdapters();

                        IcsConnection source = FindConnection(connections, config.WiredName);
                        IcsConnection target = FindConnection(connections, config.HostedName);
                        if (target == null)
                        {
                            AdapterInfo virtualAdapter = NetworkInventory.FindVirtualAdapter(adapters);
                            if (virtualAdapter != null) target = FindConnection(connections, virtualAdapter.Name);
                        }

                        log("正在关闭 Internet 连接共享……");
                        if (source != null && source.SharingEnabled)
                        {
                            try { ics.DisableSharing(source); }
                            catch (Exception ex) { log("关闭有线共享失败：" + ErrorMessage(ex)); }
                        }
                        if (target != null && target.SharingEnabled)
                        {
                            try { ics.DisableSharing(target); }
                            catch (Exception ex) { log("关闭热点共享失败：" + ErrorMessage(ex)); }
                        }
                    }
                    catch (Exception ex)
                    {
                        log("读取共享配置失败：" + ErrorMessage(ex));
                    }
                }

                log("正在停止热点……");
                ProcessResult stopResult = NetshRunner.Run("wlan stop hostednetwork");
                if (!stopResult.Success && !string.IsNullOrEmpty(stopResult.Output))
                {
                    log(stopResult.Output.Trim());
                }

                if (config.RestoreOnStop && _ipChanged && _wiredSnapshot != null)
                {
                    log("正在恢复启动前的 IP 配置……");
                    try
                    {
                        NetworkInventory.Restore(_wiredSnapshot);
                        _ipChanged = false;
                        log("IP 配置已恢复。");
                    }
                    catch (Exception ex)
                    {
                        log("恢复 IP 配置失败：" + ErrorMessage(ex));
                    }
                }

                RestoreServiceStartTypes(log);

                _sharingTouched = false;
                HotspotState state = GetStatus(config);
                log("热点已停止。");
                return Success(state, "热点已停止。");
            }
            catch (Exception ex)
            {
                RestoreServiceStartTypes(log);
                return Fail(ErrorMessage(ex));
            }
        }

        public HotspotState GetStatus(AppConfig config)
        {
            HotspotState state = new HotspotState();
            if (config != null)
            {
                state.Ssid = config.Ssid;
                state.WiredName = config.WiredName;
                state.HostedName = config.HostedName;
            }

            try
            {
                ProcessResult result = NetshRunner.Run("wlan show hostednetwork");
                string text = result.Output == null ? "" : result.Output;

                state.Running = Regex.IsMatch(text,
                    @"(?im)^\s*(Status|状态|狀態)\s*[:：]\s*(Started|已启动|已啟動)");
                if (!state.Running)
                {
                    state.Running = Regex.IsMatch(text,
                        @"(?im)(Hosted network status|承载网络状态|乘載網路狀態)[\s\S]{0,60}?(Started|已启动|已啟動)");
                }

                Match ssid = Regex.Match(text,
                    @"(?im)^\s*(SSID name|SSID 名称|SSID 名稱)\s*[:：]\s*(.+?)\s*$");
                if (ssid.Success) state.Ssid = ssid.Groups[2].Value.Trim().Trim('"');

                List<AdapterInfo> adapters = NetworkInventory.GetAdapters();
                AdapterInfo wired = NetworkInventory.FindByName(adapters, state.WiredName);
                if (wired != null) state.WiredIp = Join(wired.IpAddresses);

                AdapterInfo hosted = NetworkInventory.FindByName(adapters, state.HostedName);
                if (hosted == null) hosted = NetworkInventory.FindVirtualAdapter(adapters);
                if (hosted != null) state.HostedName = hosted.Name;

                state.Detail = state.Running ? "热点运行中" : "热点未启动";
            }
            catch (Exception ex)
            {
                state.Running = false;
                state.Detail = ex.Message;
            }
            return state;
        }

        public void Diagnose(AppConfig config, LogHandler log)
        {
            if (log == null) log = delegate(string message) { };

            log("=== 诊断开始 ===");
            try
            {
                log("管理员权限：" + (IsAdministrator() ? "是" : "否"));
                log("WLAN AutoConfig 服务：" + ServiceState("WLANSVC"));
                log("Internet 连接共享服务：" + ServiceState("SharedAccess"));

                string driverOutput;
                int supported = CheckHostedNetworkSupport(out driverOutput);
                log("承载网络支持：" + DescribeSupport(supported));

                ProcessResult hosted = NetshRunner.Run("wlan show hostednetwork");
                if (!string.IsNullOrEmpty(hosted.Output)) log(hosted.Output.Trim());

                List<AdapterInfo> adapters = NetworkInventory.GetAdapters();
                foreach (AdapterInfo adapter in adapters)
                {
                    log("网卡：" + adapter.Name + " | " + adapter.Description +
                        " | IP " + Join(adapter.IpAddresses) +
                        " | 网关 " + Join(adapter.Gateways) +
                        " | DHCP " + (adapter.DhcpEnabled ? "是" : "否") +
                        " | 启用 " + (adapter.Enabled ? "是" : "否"));
                }

                IcsManager ics = new IcsManager();
                foreach (IcsConnection connection in ics.GetConnections())
                {
                    log("共享：" + connection.Name + " | 已启用 " + (connection.SharingEnabled ? "是" : "否") +
                        " | 类型 " + connection.SharingType.ToString());
                }
            }
            catch (Exception ex)
            {
                log("诊断出错：" + ex.Message);
            }
            log("=== 诊断结束 ===");
        }

        private void Rollback(LogHandler log)
        {
            try { NetshRunner.Run("wlan stop hostednetwork"); }
            catch { }

            if (_sharingTouched)
            {
                try
                {
                    IcsManager ics = new IcsManager();
                    List<IcsConnection> connections = ics.GetConnections();
                    IcsConnection source = FindConnection(connections, _activeWiredName);
                    IcsConnection target = FindConnection(connections, _activeHostedName);
                    if (source != null && source.SharingEnabled) ics.DisableSharing(source);
                    if (target != null && target.SharingEnabled) ics.DisableSharing(target);
                }
                catch
                {
                }
            }

            if (_ipChanged && _wiredSnapshot != null)
            {
                try
                {
                    log("正在恢复启动前的 IP 配置……");
                    NetworkInventory.Restore(_wiredSnapshot);
                }
                catch
                {
                }
            }

            _sharingTouched = false;
            _ipChanged = false;
            RestoreServiceStartTypes(log);
        }

        private static void ValidateConfig(AppConfig config)
        {
            if (config == null) throw new ArgumentNullException("config");
            if (config.WiredName == null || config.WiredName.Trim().Length == 0)
            {
                throw new ArgumentException("请填写有线连接名称。");
            }
            if (config.HostedName == null || config.HostedName.Trim().Length == 0)
            {
                throw new ArgumentException("请填写热点网卡名称。");
            }
            if (string.Equals(config.WiredName.Trim(), config.HostedName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("有线连接和热点网卡不能是同一个连接。");
            }
            if (config.Ssid == null || config.Ssid.Length == 0 || config.Ssid.Length > 32)
            {
                throw new ArgumentException("Wi-Fi 名称须为 1 到 32 个字符。");
            }
            if (config.Ssid.IndexOf('"') >= 0)
            {
                throw new ArgumentException("Wi-Fi 名称不能包含双引号。");
            }

            string key = config.Password;
            if (key == null || key.Length < 8 || key.Length > 63)
            {
                throw new ArgumentException("Wi-Fi 密码须为 8 到 63 个字符。");
            }
            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                if (c < (char)0x21 || c > (char)0x7E || c == '"')
                {
                    throw new ArgumentException("Wi-Fi 密码只能包含可见 ASCII 字符，不能包含空格或双引号。");
                }
            }

            if (!NetworkInventory.IsIPv4(config.Ip)) throw new ArgumentException("静态 IP 格式不正确。");
            if (!NetworkInventory.IsIPv4(config.Mask)) throw new ArgumentException("子网掩码格式不正确。");
            if (!NetworkInventory.IsIPv4(config.Gateway)) throw new ArgumentException("默认网关格式不正确。");
        }

        private static void EnsureAdministrator()
        {
            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new WindowsPrincipal(identity);
            if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
            {
                throw new InvalidOperationException("必须以管理员身份运行 OpenHotspot。");
            }
        }

        private static bool IsAdministrator()
        {
            try
            {
                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private void EnsureService(string name, LogHandler log)
        {
            if (log == null) log = delegate(string message) { };

            int startType = ReadStartType(name);
            if (startType < 0)
            {
                throw new InvalidOperationException(
                    "系统未安装服务 " + name + "（" + ServiceTitle(name) + "）。\r\n\r\n" +
                    "精简版系统或优化脚本可能删除了该服务的组件，导致共享网络功能不可用。\r\n" +
                    "请使用完整的 Windows 系统镜像，或联系管理员恢复该服务。");
            }

            try
            {
                using (ServiceController controller = new ServiceController(name))
                {
                    if (controller.Status == ServiceControllerStatus.Running) return;

                    if (startType == 4)
                    {
                        log("服务 " + ServiceTitle(name) + " 处于禁用状态，正在临时改为手动启动……");
                        EnableServiceStart(name, startType);
                        log("服务 " + name + " 已临时改为手动启动，停止热点时恢复原设置。");
                    }

                    if (controller.Status != ServiceControllerStatus.StartPending)
                    {
                        StartWithRecovery(controller, name, log);
                    }

                    try
                    {
                        controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                    }
                    catch (Exception waitError)
                    {
                        throw BuildStartFailure(name, waitError);
                    }
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("服务 " + name + " 无法启动：" + ex.Message, ex);
            }
        }

        private void StartWithRecovery(ServiceController controller, string name, LogHandler log)
        {
            try
            {
                log("正在启动服务 " + name + "……");
                controller.Start();
                return;
            }
            catch
            {
            }

            bool fixedDependencies;
            try
            {
                fixedDependencies = FixDisabledDependencies(name, log);
            }
            catch (Exception fixError)
            {
                throw BuildStartFailure(name, fixError);
            }

            Thread.Sleep(500);

            try
            {
                if (fixedDependencies)
                {
                    log("已临时启用相关依赖服务，正在重试启动 " + name + "……");
                }
                else
                {
                    log("第 1 次启动失败，正在重试 " + name + "……");
                }
                controller.Start();
            }
            catch (Exception startError)
            {
                throw BuildStartFailure(name, startError);
            }
        }

        private bool FixDisabledDependencies(string name, LogHandler log)
        {
            List<string> visited = new List<string>();
            return FixDisabledDependencies(name, visited, log);
        }

        private bool FixDisabledDependencies(string name, List<string> visited, LogHandler log)
        {
            bool changed = false;
            visited.Add(name);
            string[] dependencies = ReadDependencies(name);
            foreach (string dependency in dependencies)
            {
                int start = ReadStartType(dependency);
                if (start == 4)
                {
                    log("依赖服务 " + dependency + " 处于禁用状态，正在临时改为手动启动……");
                    EnableServiceStart(dependency, start);
                    changed = true;
                    log("依赖服务 " + dependency + " 已临时改为手动启动。");
                }
                if (!visited.Contains(dependency) && FixDisabledDependencies(dependency, visited, log))
                {
                    changed = true;
                }
            }
            return changed;
        }

        private void EnableServiceStart(string name, int originalStart)
        {
            bool recorded = false;
            foreach (ServiceStartRecord record in _serviceStartRecords)
            {
                if (string.Equals(record.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    recorded = true;
                    break;
                }
            }
            if (!recorded)
            {
                ServiceStartRecord record = new ServiceStartRecord();
                record.Name = name;
                record.StartType = originalStart;
                _serviceStartRecords.Add(record);
            }
            ChangeServiceStart(name, 3);
        }

        public void RestoreServiceStartTypes(LogHandler log)
        {
            if (_serviceStartRecords.Count == 0) return;
            List<ServiceStartRecord> records = new List<ServiceStartRecord>(_serviceStartRecords);
            _serviceStartRecords.Clear();
            foreach (ServiceStartRecord record in records)
            {
                try
                {
                    if (log != null) log("正在恢复服务 " + record.Name + " 的启动类型……");
                    ChangeServiceStart(record.Name, record.StartType);
                    if (log != null) log("服务 " + record.Name + " 的启动类型已恢复。");
                }
                catch (Exception ex)
                {
                    if (log != null) log("恢复服务 " + record.Name + " 的启动类型失败：" + ex.Message);
                }
            }
        }

        private static void ChangeServiceStart(string name, int startType)
        {
            if (ChangeStartModeViaWmi(name, startType)) return;

            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(ServiceKeyPath(name), true))
                {
                    if (key == null)
                    {
                        throw new InvalidOperationException("服务注册表项不存在。");
                    }
                    key.SetValue("Start", startType, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("无法修改服务 " + name + " 的启动类型：" + ex.Message, ex);
            }
        }

        private static bool ChangeStartModeViaWmi(string name, int startType)
        {
            string mode = StartModeName(startType);
            if (mode == null) return false;
            try
            {
                using (ManagementObject service = FindService(name))
                {
                    if (service == null) return false;
                    ManagementBaseObject inParameters = service.GetMethodParameters("ChangeStartMode");
                    inParameters["StartMode"] = mode;
                    ManagementBaseObject outParameters = service.InvokeMethod("ChangeStartMode", inParameters, null);
                    if (outParameters == null) return false;
                    return Convert.ToUInt32(outParameters["ReturnValue"]) == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private static ManagementObject FindService(string name)
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_Service WHERE Name = '" + name + "'"))
            {
                foreach (ManagementObject item in searcher.Get())
                {
                    return item;
                }
            }
            return null;
        }

        private static string ServiceKeyPath(string name)
        {
            return "SYSTEM\\CurrentControlSet\\Services\\" + name;
        }

        private static string StartModeName(int startType)
        {
            switch (startType)
            {
                case 0: return "Boot";
                case 1: return "System";
                case 2: return "Automatic";
                case 3: return "Manual";
                case 4: return "Disabled";
                default: return null;
            }
        }

        private static string DescribeStartType(int startType)
        {
            switch (startType)
            {
                case 0: return "Boot";
                case 1: return "System";
                case 2: return "自动";
                case 3: return "手动";
                case 4: return "已禁用";
                default: return null;
            }
        }

        private static int ReadStartType(string name)
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(ServiceKeyPath(name)))
            {
                if (key == null) return -1;
                object value = key.GetValue("Start");
                try
                {
                    return value == null ? 3 : Convert.ToInt32(value);
                }
                catch
                {
                    return 3;
                }
            }
        }

        private static string[] ReadDependencies(string name)
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(ServiceKeyPath(name)))
            {
                if (key == null) return new string[0];
                string[] values = key.GetValue("DependOnService") as string[];
                if (values == null) return new string[0];
                List<string> result = new List<string>();
                foreach (string value in values)
                {
                    if (value == null) continue;
                    string trimmed = value.Trim();
                    if (trimmed.Length == 0) continue;
                    result.Add(trimmed);
                }
                return result.ToArray();
            }
        }

        private static string DescribeDependencies(string name)
        {
            string[] dependencies = ReadDependencies(name);
            if (dependencies.Length == 0) return "无";
            List<string> items = new List<string>();
            foreach (string dependency in dependencies)
            {
                if (ReadStartType(dependency) < 0) items.Add(dependency + "（服务不存在）");
                else items.Add(dependency);
            }
            return string.Join("、", items.ToArray());
        }

        private static string ServiceTitle(string name)
        {
            switch (name)
            {
                case "SharedAccess": return "Internet Connection Sharing（Internet 连接共享）";
                case "WLANSVC": return "WLAN AutoConfig（WLAN 自动配置）";
                default: return name;
            }
        }

        private static InvalidOperationException BuildStartFailure(string name, Exception error)
        {
            List<string> lines = new List<string>();
            lines.Add("服务 " + name + "（" + ServiceTitle(name) + "）无法启动：" + ErrorMessage(error));
            lines.Add("");
            lines.Add("依赖服务：" + DescribeDependencies(name));
            lines.Add("");
            lines.Add("常见原因：");
            lines.Add("1. 该服务或其依赖服务被组策略、安全软件或系统优化工具禁用，本工具已尝试临时启用仍未成功；");
            lines.Add("2. 精简版系统删除了服务所需的组件，服务无法加载；");
            lines.Add("3. 服务的注册表配置损坏。");
            lines.Add("请以管理员身份打开 services.msc，检查该服务的启动类型、状态和“依赖”选项卡；");
            lines.Add("也可以使用 sfc /scannow 修复系统组件后重试。");
            return new InvalidOperationException(string.Join("\r\n", lines.ToArray()), error);
        }

        private static string ServiceState(string name)
        {
            try
            {
                using (ServiceController controller = new ServiceController(name))
                {
                    string state = controller.Status.ToString();
                    string startType = DescribeStartType(ReadStartType(name));
                    if (startType == null) return state;
                    return state + "，启动类型 " + startType;
                }
            }
            catch (Exception ex)
            {
                return "不可用（" + ex.Message + "）";
            }
        }

        private static int CheckHostedNetworkSupport(out string output)
        {
            ProcessResult result = NetshRunner.Run("wlan show drivers");
            output = result.Output == null ? "" : result.Output;
            return ParseHostedNetworkSupport(output);
        }

        private const int SupportYes = 1;
        private const int SupportNo = 0;
        private const int SupportUnknown = -1;

        private static int ParseHostedNetworkSupport(string text)
        {
            if (string.IsNullOrEmpty(text)) return SupportUnknown;

            bool anyYes = false;
            string[] lines = text.Split(new string[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                bool hosted = Regex.IsMatch(line,
                    @"(?i)hosted\s*network\s*supported" +
                    @"|支持.{0,3}承载|支援.{0,3}(承載|裝載|乘載|装载)" +
                    @"|(承载|承載|裝載|乘載|装载).{0,5}(支持|支援)");
                if (!hosted) continue;

                Match value = Regex.Match(line, @"[:：]\s*(.+?)\s*$");
                if (!value.Success) continue;
                string token = value.Groups[1].Value;

                if (Regex.IsMatch(token, @"(?i)^yes(\W.*)?$") || Regex.IsMatch(token, @"^是(\W.*)?$"))
                {
                    anyYes = true;
                    continue;
                }
                if (Regex.IsMatch(token, @"(?i)^no(\W.*)?$") || Regex.IsMatch(token, @"^否(\W.*)?$"))
                {
                    return SupportNo;
                }
            }

            return anyYes ? SupportYes : SupportUnknown;
        }

        private static string DescribeSupport(int supported)
        {
            if (supported == SupportYes) return "是";
            if (supported == SupportNo) return "否";
            return "未知";
        }

        private static IcsConnection FindConnection(List<IcsConnection> connections, string name)
        {
            if (connections == null || name == null) return null;
            foreach (IcsConnection connection in connections)
            {
                if (string.Equals(connection.Name, name, StringComparison.OrdinalIgnoreCase)) return connection;
            }
            return null;
        }

        private static int CountConnections(List<IcsConnection> connections, string name)
        {
            if (connections == null || name == null) return 0;
            int count = 0;
            foreach (IcsConnection connection in connections)
            {
                if (string.Equals(connection.Name, name, StringComparison.OrdinalIgnoreCase)) count++;
            }
            return count;
        }

        private static string QuoteNetsh(string value)
        {
            return "\"" + (value == null ? "" : value) + "\"";
        }

        private static string Join(string[] values)
        {
            if (values == null || values.Length == 0) return "无";
            return string.Join(", ", values);
        }

        private static string Describe(AdapterInfo adapter)
        {
            return "IP " + Join(adapter.IpAddresses) +
                   "，掩码 " + Join(adapter.Subnets) +
                   "，网关 " + Join(adapter.Gateways);
        }

        private static string JoinAdapterNames(List<AdapterInfo> adapters)
        {
            List<string> names = new List<string>();
            foreach (AdapterInfo adapter in adapters) names.Add(adapter.Name);
            return names.Count == 0 ? "无" : string.Join("、", names.ToArray());
        }

        private static string JoinConnectionNames(List<IcsConnection> connections)
        {
            if (connections == null) return "无";
            List<string> names = new List<string>();
            foreach (IcsConnection connection in connections)
            {
                names.Add(string.IsNullOrEmpty(connection.Name) ? "(未命名)" : connection.Name);
            }
            return names.Count == 0 ? "无" : string.Join("、", names.ToArray());
        }

        private static string ErrorMessage(Exception exception)
        {
            if (exception == null) return "未知错误。";
            TargetInvocationException invocation = exception as TargetInvocationException;
            if (invocation != null && invocation.InnerException != null)
            {
                return invocation.InnerException.Message;
            }
            return exception.Message;
        }

        private static OperationResult Success(HotspotState state, string message)
        {
            OperationResult result = new OperationResult();
            result.Success = true;
            result.Message = message;
            result.State = state;
            return result;
        }

        private static OperationResult Fail(string message)
        {
            OperationResult result = new OperationResult();
            result.Success = false;
            result.Message = message;
            result.State = null;
            return result;
        }
    }
}
