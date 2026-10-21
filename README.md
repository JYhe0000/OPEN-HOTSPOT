# OpenHotspot

Windows 7 有线网络转 Wi-Fi 热点的图形化小工具：原生 C# / WinForms 编写，自动设置静态 IP、开启无线承载网络并配置 Internet 连接共享（ICS）。不需要 PowerShell，不修改执行策略，编译后只有一个 `OpenHotspot.exe`。

> 本项目仅供在**获得授权的网络和设备**上学习和使用。
> 本项目也适用于**免驱网卡**
## 目录

- [简介](#简介)
- [系统要求](#系统要求)
- [快速开始](#快速开始)
- [默认配置](#默认配置)
- [工作原理](#工作原理)
- [项目结构](#项目结构)
- [从源码构建](#从源码构建)
- [常见问题](#常见问题)
- [安全与合规](#安全与合规)

## 简介

很多学校的教室或宿舍只提供有线网口，没有可用的 Wi-Fi。Windows 7 自带的“承载网络”（Hosted Network）可以让一张支持该功能的无线网卡同时充当热点，再通过 ICS 把有线网络共享给移动端设备以实现局域网文件传输和上网

手动完成这套配置需要：

1. 把有线网卡改成学校要求的静态 IP；
2. 用 `netsh wlan` 创建并启动承载网络；
3. 在“网络和共享中心”里把有线连接共享给热点虚拟网卡；
4. 停止时再把共享、热点和 IP 逐项还原。

OpenHotspot 把上述流程做成了一个窗口：填好参数，点击“启动热点”即可；停止时会按设置关闭共享、停止热点并恢复启动前的 IP 配置。

仓库中的 `Powershell` 文件夹保留了最初验证可行的 PowerShell 脚本版本，作为参考和备用方案；


## 系统要求

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 7 SP1（32 位 / 64 位）为主要目标；Windows 8 / 8.1 / 10 上的传统承载网络可能可用，但未做完整验证 |
| 运行时 | .NET Framework 3.5（Windows 7 自带，若被关闭需在“启用或关闭 Windows 功能”中勾选） |
| 权限 | 管理员 |
| 有线网卡 | 能正常连接学校网络，并允许修改 IPv4 配置 |
| 无线网卡 | 驱动必须支持 Hosted Network（`netsh wlan show drivers` 中显示 `Hosted network supported : Yes`） |
| 系统服务 | `WLAN AutoConfig`（WLANSVC）和 `Internet Connection Sharing`（SharedAccess）可用；被禁用时程序会临时启用并在停止热点后还原 |

> 只支持 IPv4。无线网卡需要是 USB 或内置网卡中支持承载网络的型号；部分厂商驱动会禁用该功能，此时只能更换驱动或使用厂商自带的 SoftAP 工具。

## 快速开始

### 直接运行

1. 下载或自行编译 `OpenHotspot.exe`；
2. 双击运行，在 UAC 提示中选择“是”；
3. 确认“有线连接”“热点网卡”和各项网络参数；
4. 点击“启动热点”；
5. 手机搜索配置的 Wi-Fi 名称并输入密码连接。

关闭主窗口后程序会缩到托盘；需要彻底退出时，右键托盘图标选择“退出”。

### 从源码编译

在 Windows 7 上双击 `build.bat`，或使用 Visual Studio / MSBuild 打开 `OpenHotspot.csproj`。详见[从源码构建](#从源码构建)。



## 默认配置

首次运行使用以下默认值（与原始脚本一致）：

| 配置项 | 默认值 |
| --- | --- |
| 有线连接 | `本地连接` |
| 热点网卡 | `无线网络连接 2` |
| Wi-Fi 名称 | `Student-WiFi` |
| 静态 IP | `10.40.2.155` |
| 子网掩码 | `255.255.255.128` |
| 默认网关 | `10.40.2.129` |
| IP 不一致时自动设置静态 IP | 开启 |
| 停止热点时恢复启动前的 IP 配置 | 开启 |

这些 IP 参数来自作者所在学校的具体网络环境，**换到其他网络时必须修改**。如果你的网络使用 DHCP，或学校要求保留自动获取，请取消“自动设置静态 IP”，并按实际情况填写参数。

配置保存在：

```
%APPDATA%\OpenHotspot\config.xml
```

## 工作原理

程序按以下顺序执行，任何一步失败都会触发回滚：

1. **检查权限**：确认当前进程具有管理员权限。
2. **确保服务运行**：检查并启动 `WLAN AutoConfig` 和 `Internet Connection Sharing`；服务被禁用时临时改为手动启动（含依赖服务），并在停止热点或回滚时恢复原启动类型。
3. **检查驱动能力**：执行 `netsh wlan show drivers`，识别 `Hosted network supported` 是否为 `Yes`。
4. **定位有线连接**：通过 WMI 枚举网卡，校验连接存在、已启用且能读取 IPv4 配置。
5. **校验或设置静态 IP**：
   - 已一致则直接继续；
   - 不一致且允许自动设置时，保存原配置快照，通过 WMI 调用 `EnableStatic` 和 `SetGateways`，随后最多轮询 6 秒校验；
   - 校验失败则恢复快照并终止。
6. **创建承载网络**：执行 `netsh wlan set hostednetwork mode=allow ssid=... key=... keyUsage=persistent`，然后 `netsh wlan start hostednetwork`。
7. **等待虚拟网卡**：最多等待约 15 秒，在 ICS 管理器中查找热点虚拟网卡；找不到时按驱动描述识别。
8. **配置 ICS**：
   - 有线连接设为共享来源（`EnableSharing(..., 0)`，对应“公用/Public”侧）；
   - 热点虚拟网卡设为共享目标（`EnableSharing(..., 1)`，对应“专用/Private”侧）；
   - 配置后重新枚举并确认两块网卡都处于共享状态，否则回滚。
9. **停止热点**：
   - 先关闭来源和目标网卡的 ICS；
   - 执行 `netsh wlan stop hostednetwork`；
   - 如果本次运行改过 IP 且勾选了恢复选项，则按快照恢复静态 IP 或 DHCP。


## 项目结构

```
OPEN-HOTSPOT/
├─ build.bat                Windows 7 一键编译脚本
├─ OpenHotspot.csproj       Visual Studio / MSBuild 工程
├─ OpenHotspot.net35.csproj Linux 交叉编译工程（dotnet SDK）
├─ app.manifest             管理员权限、Win7 兼容、DPI 设置
├─ app.ico                  程序与托盘图标
├─ Program.cs               入口、单实例、全局异常处理
├─ AppConfig.cs             配置模型、XML 读写、DPAPI 密码保护
├─ MainForm.cs              主界面、托盘、日志、状态轮询
├─ Core/
│  ├─ HotspotService.cs     启动/停止/诊断/回滚流程
│  ├─ IcsManager.cs         HNetCfg.HNetShare COM 调用
│  ├─ NetworkInventory.cs   WMI 网卡枚举与静态 IP 设置/恢复
│  └─ NetshRunner.cs        netsh 进程调用与输出解码
├─ Properties/
│  └─ AssemblyInfo.cs       程序集信息
└─ Powershell/              原始 PowerShell 脚本版（参考/备用）
   ├─ 开启WiFi热点.ps1
```

脚本版要求：

- 以管理员身份运行；
- 系统允许运行本地 PowerShell 脚本（必要时使用 `Bypass`）；
- 网卡名称和静态 IP 与脚本内的默认值匹配，否则需要先修改脚本。

## 从源码构建

### 方式一：build.bat（推荐，Windows 7 原生）

要求安装 .NET Framework 3.5（Windows 7 默认自带）：

```bat
build.bat
```

脚本会调用：

```
%WINDIR%\Microsoft.NET\Framework\v3.5\csc.exe
```

引用 `v2.0.50727` 目录下的基础程序集，输出 `OpenHotspot.exe`。如果提示找不到编译器，请在“启用或关闭 Windows 功能”中勾选 `.NET Framework 3.5.1`。

### 方式二：Visual Studio / MSBuild

用 Visual Studio 2010 或更高版本打开 `OpenHotspot.csproj`（需要安装 .NET Framework 3.5 目标包），选择 `Release` 后生成即可。工程目标框架为 `v3.5`，平台为 `AnyCPU`。

### 方式三：在 Linux 上交叉编译

Linux 上无法运行本工具（依赖 WinForms、WMI、netsh 和 Windows 服务），但可以生成 Windows 可用的 exe。仓库额外提供了 SDK 风格的项目文件 `OpenHotspot.net35.csproj`，它通过 NuGet 包 `Microsoft.NETFramework.ReferenceAssemblies` 在 Linux 上引用 .NET Framework 3.5 的官方参考程序集，产出的 exe 与 `build.bat` 产物一致（包含管理员清单和图标）。

前提：安装 .NET SDK 8 或更高版本（`dotnet.microsoft.com/download`）。

```bash
dotnet build OpenHotspot.net35.csproj -c Release
```

生成结果位于 `bin/Release/net35/OpenHotspot.exe`，拷贝到 Windows 7 运行即可。

备选方案是安装 Mono 并使用 `mcs` 编译（同样只在 Windows 上运行）：

```bash
mcs -target:winexe -platform:anycpu -optimize+ \
  -win32icon:app.ico -out:OpenHotspot.exe \
  -r:System.dll -r:System.Drawing.dll -r:System.Management.dll \
  -r:System.Security.dll -r:System.ServiceProcess.dll \
  -r:System.Windows.Forms.dll -r:System.Xml.dll \
  Program.cs AppConfig.cs MainForm.cs Properties/AssemblyInfo.cs \
  Core/HotspotService.cs Core/IcsManager.cs Core/NetshRunner.cs Core/NetworkInventory.cs
```

注意：`mcs` 无法嵌入 UAC 管理员清单，用这种方式生成的 exe 需右键“以管理员身份运行”；`dotnet build` 方式则会自动嵌入 `app.manifest`。


## 常见问题

### 提示“网卡驱动未报告支持承载网络”

`netsh wlan show drivers` 中 `Hosted network supported` 为 `No`，说明驱动或网卡不支持承载网络。可尝试：

- 更新无线网卡驱动（优先使用厂商官网版本，而不是系统自带驱动）；
- 更换支持承载网络的 USB 无线网卡；
- 使用网卡厂商提供的 SoftAP/热点工具。

### 提示“服务 SharedAccess/WLANSVC 无法启动”

一些精简版系统、企业或学校电脑会禁用甚至删除 `Internet Connection Sharing`（SharedAccess）服务，程序无法通过 ICS 共享网络时会抛出该错误。最新版本的程序对这种情况做了如下处理：

1. **服务被禁用**：程序会自动把启动类型临时改为“手动”并启动服务，同时停止热点或回滚时恢复原来的启动类型，不改动用户的原始配置；
2. **依赖服务被禁用**：程序会沿依赖链把被禁用的依赖服务同样临时启用后重试；
3. **服务或依赖被彻底删除**（精简版系统常见）：会给出明确提示，指出系统缺少该组件，需要使用完整的 Windows 系统镜像或联系管理员恢复。

如果自动修复后仍然失败，可按提示以管理员身份打开 `services.msc`，检查服务及其依赖的启动类型与状态，或使用 `sfc /scannow` 修复系统组件。“诊断”按钮会把每个服务的运行状态和启动类型写入日志，便于排查。


## 安全与合规

- 程序需要管理员权限，会修改网卡的 IPv4 配置和 ICS 共享设置，请在使用前了解这些改动。
- 程序不包含联网上报、遥测或自动更新功能。
- Wi-Fi 密码使用 DPAPI 加密存储在本地，但 `netsh` 同时会把承载网络密钥持久化到 Windows 配置中；共用电脑上请注意账户安全。
- 日志不会主动输出密码；排错时仍请留意截图内容。
- 请仅在**自己拥有或获得明确授权且合规**的网络和设备上开启热点。


