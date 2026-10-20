# Windows 7：有线网络 -> USB 无线网卡热点 + ICS 共享
# 运行前请确认下面两个连接名称与“网络连接”窗口中的名称一致。
# 本脚本会检查有线 IP/掩码/网关；不符合预期时会停止，不会继续设置共享。

$ErrorActionPreference = 'Stop'

# ===== 配置区：按需修改 =====
$WiredName = '本地连接'          # 有线连接名称
$HostedName = '无线网络连接 2'    # Hosted Network 对应的虚拟无线连接名称
$SSID = 'Student-WiFi'           # Wi-Fi 名称（建议不要包含空格）
$ExpectedIP = '10.40.2.155'
$ExpectedMask = '255.255.255.128'
$ExpectedGateway = '10.40.2.129'
# ============================

function Stop-Script([string]$Message) {
    Write-Host ''
    Write-Host ('停止：' + $Message) -ForegroundColor Red
    Write-Host '没有继续配置网络共享。按任意键退出。'
    $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
    exit 1
}

function Get-ShareConnections($Manager) {
    $items = @()
    foreach ($connection in $Manager.EnumEveryConnection) {
        $props = $Manager.NetConnectionProps.Invoke($connection)
        $config = $Manager.INetSharingConfigurationForINetConnection.Invoke($connection)
        $enabled = $false
        $sharingType = -1
        try { $enabled = [bool]$config.SharingEnabled } catch { }
        if ($enabled) {
            try { $sharingType = [int]$config.SharingType } catch { }
        }
        $items += New-Object PSObject -Property @{
            Name = [string]$props.Name
            Sharing = $enabled
            Type = $sharingType
            Config = $config
        }
    }
    return $items
}

# 必须以管理员身份运行
$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Stop-Script '请通过“开启WiFi热点.bat”启动，并允许管理员权限提示。'
}

# 检查无线 Hosted Network 支持
Write-Host '正在检查 Windows 7 无线热点支持情况……'
$driverText = (& netsh wlan show drivers 2>&1 | Out-String)
if ($driverText -notmatch '(?im)(Hosted network supported|支持的承载网络)\s*[:：]\s*(Yes|是)(?:\s|$)') {
    Write-Host $driverText
    Stop-Script '网卡驱动未报告 Hosted Network supported: Yes。请先安装支持 Windows 7 热点模式的驱动，或使用网卡厂商的 SoftAP 工具。'
}

# 安全检查：有线接口必须匹配名称和原始地址，避免把共享配到错误网卡上
$wiredAdapters = @(Get-WmiObject Win32_NetworkAdapter | Where-Object { $_.NetConnectionID -eq $WiredName -and $_.NetEnabled })
if ($wiredAdapters.Count -ne 1) {
    $names = @(Get-WmiObject Win32_NetworkAdapter | Where-Object { $_.NetConnectionID } | ForEach-Object { $_.NetConnectionID })
    Write-Host ('系统检测到的网络连接：' + ($names -join '、'))
    Stop-Script ('找不到唯一的有线连接“' + $WiredName + '”。请在脚本配置区修改 $WiredName。')
}
$wiredConfig = @(Get-WmiObject Win32_NetworkAdapterConfiguration | Where-Object { $_.Index -eq $wiredAdapters[0].Index -and $_.IPEnabled })
if ($wiredConfig.Count -ne 1) {
    Stop-Script '无法读取有线网卡的 IPv4 配置。请先在“网络连接”中检查有线连接。'
}
if (-not (@($wiredConfig[0].IPAddress) -contains $ExpectedIP) -or
    -not (@($wiredConfig[0].IPSubnet) -contains $ExpectedMask) -or
    -not (@($wiredConfig[0].DefaultIPGateway) -contains $ExpectedGateway)) {
    Write-Host ('当前有线 IP：' + (@($wiredConfig[0].IPAddress) -join ', '))
    Write-Host ('当前子网掩码：' + (@($wiredConfig[0].IPSubnet) -join ', '))
    Write-Host ('当前默认网关：' + (@($wiredConfig[0].DefaultIPGateway) -join ', '))
    Stop-Script '有线 IP/掩码/网关与脚本配置区的预期值不一致。为避免影响学生机网络，已中止。请先恢复原有配置，或确认实际参数后再修改配置区。'
}

# 密码不硬编码在脚本里；避免使用空格，兼容 netsh 参数解析
$wifiKey = Read-Host '请输入 Wi-Fi 密码（8–63 个可见 ASCII 字符，不含空格）'
if ($wifiKey.Length -lt 8 -or $wifiKey.Length -gt 63 -or $wifiKey -match '[^!-~]') {
    Stop-Script 'Wi-Fi 密码格式不符合要求：长度须为 8–63 个可见 ASCII 字符，且不能包含空格。'
}

Write-Host ''
Write-Host ('有线连接检查通过：' + $ExpectedIP + ' / ' + $ExpectedMask + '，网关 ' + $ExpectedGateway)
Write-Host '正在配置并启动 Wi-Fi 热点……'
& netsh wlan set hostednetwork mode=allow ("ssid=" + $SSID) ("key=" + $wifiKey) keyUsage=persistent
if ($LASTEXITCODE -ne 0) {
    Stop-Script '设置 Hosted Network 失败。'
}
& netsh wlan start hostednetwork
if ($LASTEXITCODE -ne 0) {
    Stop-Script '启动 Hosted Network 失败。请检查无线网卡驱动与 WLAN AutoConfig 服务。'
}

# 获取 ICS COM 对象，并等待虚拟热点网卡出现
try {
    $shareManager = New-Object -ComObject HNetCfg.HNetShare
} catch {
    & netsh wlan stop hostednetwork | Out-Null
    Stop-Script '无法创建 Windows Internet 连接共享管理对象（HNetCfg.HNetShare）。'
}
$connections = @()
$targetFound = $false
for ($attempt = 0; $attempt -lt 12; $attempt++) {
    $connections = @(Get-ShareConnections $shareManager)
    if (@($connections | Where-Object { $_.Name -eq $HostedName }).Count -eq 1) {
        $targetFound = $true
        break
    }
    Start-Sleep -Seconds 1
}
if (-not $targetFound) {
    & netsh wlan stop hostednetwork | Out-Null
    Write-Host ('当前检测到的连接：' + ((@($connections | ForEach-Object { $_.Name })) -join '、'))
    Stop-Script ('热点虚拟网卡没有以“' + $HostedName + '”这个名字出现。脚本已关闭热点，未修改 ICS。请在配置区修正 $HostedName 后重试。')
}

$source = @($connections | Where-Object { $_.Name -eq $WiredName })
$target = @($connections | Where-Object { $_.Name -eq $HostedName })
if ($source.Count -ne 1 -or $target.Count -ne 1) {
    & netsh wlan stop hostednetwork | Out-Null
    Stop-Script '无法唯一识别有线共享来源和热点虚拟网卡。'
}

# 不碰其他网卡的 ICS 配置；如发现第三块网卡也正在共享，则停止并要求先人工检查
$unexpectedSharing = @($connections | Where-Object { $_.Sharing -and $_.Name -ne $WiredName -and $_.Name -ne $HostedName })
if ($unexpectedSharing.Count -gt 0) {
    & netsh wlan stop hostednetwork | Out-Null
    Stop-Script ('检测到其他连接正在使用 ICS：' + ((@($unexpectedSharing | ForEach-Object { $_.Name })) -join '、') + '。请先在网络连接属性中检查并关闭无关共享，再运行本脚本。')
}

try {
    # 只重置这两块目标网卡的共享角色，防止来源/目标角色之前配反。
    $sourceConfig = $source[0].Config
    $targetConfig = $target[0].Config
    if ($source[0].Sharing) { $sourceConfig.DisableSharing() }
    if ($target[0].Sharing) { $targetConfig.DisableSharing() }

    # 0 = 公用/Internet 来源；1 = 专用/家庭网络目标
    $sourceConfig.EnableSharing(0)
    $targetConfig.EnableSharing(1)
} catch {
    Write-Host ('启用 ICS 时发生错误：' + $_.Exception.Message) -ForegroundColor Red
    # 尝试撤销本次对这两块指定网卡的共享设置，避免留下半配置状态。
    try { if ($sourceConfig.SharingEnabled) { $sourceConfig.DisableSharing() } } catch { }
    try { if ($targetConfig.SharingEnabled) { $targetConfig.DisableSharing() } } catch { }
    & netsh wlan stop hostednetwork | Out-Null
    Write-Host '已尝试关闭本次热点和这两块网卡上的共享；未改写有线网卡的 IP/DNS。' -ForegroundColor Yellow
    Stop-Script 'ICS 配置未能完整确认。请重新检查有线连接的原始 IP/掩码/网关。'
}

Start-Sleep -Seconds 2
Write-Host ''
Write-Host '热点与共享设置命令已完成。' -ForegroundColor Green
Write-Host ('Wi-Fi 名称：' + $SSID)
Write-Host ('有线来源：' + $WiredName + '（脚本已核对原始 IPv4 参数）')
Write-Host ('热点目标：' + $HostedName)
Write-Host '现在用手机连接 Wi-Fi，并保持 IP 设置为“自动获取”。'
Write-Host '最后再次核对有线网卡 IP 是否仍为 10.40.2.155；如果不是，请立即取消共享并恢复原始配置。'
Write-Host ''
& ipconfig
Write-Host ''
Write-Host '按任意键退出。'
$null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
