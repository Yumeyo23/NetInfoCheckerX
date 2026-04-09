
<img width="3543" height="1025" alt="查询器X-Banner_看图王" src="https://github.com/user-attachments/assets/39782dbc-4389-4852-99fa-df164e69d337" />

# 网络综合查询器X / NetInfoCheckerX 

Next Generation of [NetInfoChecker](https://github.com/Yumeyo23/NetInfoChecker) By Yumeyo

<img width="3000" height="1875" alt="功能预览260406" src="https://github.com/user-attachments/assets/3a7d3019-be1f-43a3-8baa-afed93bd87fd" />

《网络综合查询器X》是由與夢Yumeyo原创开发、基于易语言版《网络综合查询器》重制、以“小而美”为设计理念的全能向网络工具箱，适合网络爱好者常备日常使用。

软件核心功能为查询/记录/修改本机IP、多出口测试(简洁版/Dashboard)、NAT类型测试、TCP/UDP/ICMP全协议Ping+/Trace+、最大连接数测试、DNS劫持测试、DNS解析测速、UPnP控制台、IPERF内网测速、本机配置检测、延迟(到骨干网/CS2)测试、一键/Renew、IPv6有效期查询、手动查询IP/MAC地址、快速跳转控制面板、速查剪贴板、悬浮显示信息等。

> [!CAUTION]
> **本软件主要为中国大陆的中文用户设计. 非中文/中国大陆用户可能无法获得最佳体验, 甚至无法使用.**
> 
> **This software is designed for Chinese-speaking users in mainland China.**
>
> **Non-Chinese/mainland China users may not have the best experience, or may even be unable to use.**

> [!NOTE]
>
> 1. 本程序是 [**网络综合查询器**](https://github.com/Yumeyo23/NetInfoChecker)(易语言版，简称**旧版查询器**) 的**C#重制版**，简称 **查询器X** .
>2. 本程序正在开发中，暂未正式公测. 介绍、实际功能等可能随时改变.

> [!IMPORTANT]
>
> 本项目为**部分开源**。作者**不会**从本项目主动获取**任何**利益和用户隐私等不当信息，作者衷心地向所有API、服务器等提供商表示感谢，程序中已列出用到的所有API。
>
> 为防API轻易滥用及保护软件更新动力，本项目采用部分开源：**公开绝大多数测试的核心方法供交流学习，UI 等高原创部分暂时保持不开源**。

## 视频介绍/Wiki

https://www.bilibili.com/video/BV1cyibBkEbJ/

Wiki持续更新中...可在[Wiki](https://github.com/Yumeyo23/NetInfoCheckerX/wiki)查看

## 下载
- **因个人习惯，GitHub页面更新不是很及时，最新版软件可+QQ群：1013442261下载**
- GitHub请在[release](https://github.com/Yumeyo23/NetInfoCheckerX/releases)下载最新版.
- **软件完全绿色，但用到了一些外部依赖（后面会一一列出），故release时为WINRAR自解压文件**
- **既可双击临时运行，也可解压后固定目录运行**。长期使用建议解压后运行
> [!NOTE]
>
> 由于用到了外部依赖，**部分依赖存在报毒问题**，建议关闭或者信任后使用，
>
> 发布程序使用[ConfuserEx](https://github.com/mkaring/ConfuserEx)做基本保护。
>
> 如对本程序安全问题存在任何疑惑，可自行决定是否使用本项目。

## 注意
- 本程序是**专业向工具**，非**专业工具**，所有功能均以个人日常需求开发。
- 作者**非计算机/网络相关专业，无编程背景一切皆为兴趣**，只了解易语言，**为重制查询器才开始了解C#，大部分均为AI代替完成**，vibe coding还在发力中（当然设计思路是自己的，也有相当自己完成的部分）。 
- 故代码丑陋，~~想喷的伙计轻点儿(x~~。 初期不乐意开源，还请见谅**。欢迎大佬帮助优化~
- 本程序架构**C# NET Framework 4.7.2 WinForm窗口程序**，且作者**无任何升/换框架/跨平台欲望**.
- 理由：1. 我菜；
    2. 生活中多数电脑都是Windows，NET4.7.2体积小巧、兼容性好，无需依赖即可运行，更便携，打包后10几MB体积完成后面的一坨功能，~~不像NatTypeTester那样100MB只能测NAT一件事(虽然也不是那位作者想这样的)~~
    3. 手上只有手机，多数时候只需最常用的找AP、多出口、NAT测试就可以了，直接使用[查询器多出口在线版](https://yumeyo23.github.io/NetInfoChecker/checker-web.html)等网站/软件，**没有电脑的大多数情况下**需要的功能都可以解决.
- 感谢陪伴awa

## 灵感/致谢
- NAT类型测试灵感源自于[NatTypeTester](https://github.com/HMBSbige/NatTypeTester)(未使用其代码)，并加入了大量的优化功能：例如**显示debug信息、设置端口模式/起手、列出可用网卡+指定IP测试** ~~(其实我也忍不了一个纯NAT测试要100MB)~~
- 本机配置检测，为了开发方便+数据准确，全部使用[图吧工具箱](https://www.tbtool.cn/sdk/index.html)公开的硬件检测SDK (包括WMI版和C++预览版都有使用).
- iPerf工具来自于最新的[iperf3-win-builds](https://github.com/ar51an/iperf3-win-builds/releases)，受制于技术，只做了设置GUI用于拼接启动代码.
- 感谢程序里使用到的所有API提供商，因为数量太多在此不一一列举，可进入程序查看
- 感谢[IEEE官方MAC地址表](http://standards-oui.ieee.org/oui/oui.csv)，[WireShark提供的MAC地址表](https://www.wireshark.org/download/automated/data/manuf.gz)
- 感谢所有AI教我写程序!!!
- 如有未尽之处，深表歉意，衷心感谢.

## 依赖列表

release的程序完整包使用到了以下依赖及NuGet，在此致谢：

| 名字                                                         | 用途                                                         | 备注                                                         |
| ------------------------------------------------------------ | ------------------------------------------------------------ | ------------------------------------------------------------ |
| [图吧工具箱硬件检测引擎](https://www.tbtool.cn/sdk/index.html) | 本机配置检测(推荐、备用)                                     | 直接使用图吧工具箱提供的专业API，配置检测更准，开发更快。<br />(推荐)使用的是C++版，直接使用<br />(备用)使用的是WMI版，编译了图吧官方提供例程使用 |
| [iperf3-win-builds](https://github.com/ar51an/iperf3-win-builds/releases) | iPerf3测速工具                                               | 未修改                                                       |
| [IEEE官方MAC地址表](http://standards-oui.ieee.org/oui/oui.csv)<br />[WireShark提供的MAC地址表](https://www.wireshark.org/download/automated/data/manuf.gz) | 手动查询MAC地址                                              | 未修改                                                       |
| [Curl for Windows](https://curl.se/windows/)                 | 备用, 访问部分API使用                                        | 未修改，带证书                                               |
| [Open.Nat](https://www.nuget.org/packages/Open.NAT/2.1.0?_src=template) | UPnP控制台                                                   | NuGet直接使用                                                |
| [IPAddressControl](https://www.nuget.org/packages/IPAddressControl/1.2.4446.28646?_src=template) | 程序内所有IPv4编辑框                                         | 同上                                                         |
| [IP2Region](https://www.nuget.org/packages/IP2Region.Net/3.0.2?_src=template) | 程序内置的IP地理位置数据库(主窗口、手动查询IP、Trace+使用)   | 同上，其还带有一些附属NuGet包此处省略                        |
| [DnsClient](https://www.nuget.org/packages/DnsClient/1.8.0?_src=template) | DNS劫持测试                                                  | 同上                                                         |
| [obfuscar](https://www.nuget.org/packages/Obfuscar)          | 备用混淆器, 发布版目前使用[ConfuserEx](https://github.com/mkaring/ConfuserEx) | 同上                                                         |



## To do list

- [x] 国内出口IP, 国外出口/走代理IP  **//25.11.19开工，已完工**

- [x] 本机硬件检测    **//25.11.20完工**

- [x] IPERF测速工具GUI     **//25.11.21完工**

- [x] RFC3489/5780NAT测试     **//25.11.22完工**

- [x] TCP/UDP/ICMP Tracert+ (自定义网卡)   **//25.11.22完工**

- [x] 本机所有网卡, 可快速复制IPV6/打开默认网关 **//25.11.28完工**

- [x] 快速Ping/Tracert/Nslookup剪贴板  **//25.11.29完工**

- [x] 一键/Renew [ipcfg/release/renew/flushdns] **//25.11.29完工**

- [x] 快速跳转控制面板  **//25.11.29完工**

- [x] IPV6有效期  **//25.11.29完工**

- [x] WakeOnLan  **//25.12.17完工**

- [x] 修改本机网卡信息  **//25.12.19完工**

- [x] 深/浅色切换  **//25.12.18开工，待完工**

- [x] 记录/读取国内IP/查国内IP记录次数  **//25.12.20完工**

- [x] 自由查询  **//25.12.20完工**

- [x] TCP/UDP/ICMP Ping+ (自定义网卡)  **//25.12.20完工**

- [x] 手动查IP/MAC  **//25.12.21完工**

- [x] DNS劫持测试   **//25.12.24完工**

- [x] Ping延迟测试(全球网测节点/CS完美平台) **//26.1.5完工**

- [x] 悬浮信息时间显示  **//25.12.21开工，待完工**

- [x] 最大连接数测试  **//26.1.7完工**

- [x] UPnP控制台  **//26.1.11完工**

- [x] 子网掩码计算器  **//26.1.30完工**

- [x] 单IP-TCP端口扫描 **//26.1.31完工**

- [x] DNS真选(解析延迟测试) **//26.3.29完工**

- [x] 多出口测试精简版（名站+三大+教育+测漏） **//26.3.13完工**

- [x] 多出口测试教育网 **//26.3.13完工**

- [X] 多出口测试完整版（精简版功能+双栈+UDP+主流延迟）  **//26.4.10完工**

  

## NOT TO DO list

- 内网扫描（已有标准答案，个人无需再做）
  1. 很多个人开发的同类软件，默认只能扫ipv4/24，虽然/24最常用，但确实显得比较鸡肋，万一碰到大段或小段还是不能扫. 大多数人做也只是icmp ping，最多加个arp，更进一步就没有了. 也鸡肋.
  2. 该需求已有[Network Scanner](https://www.softperfect.com/products/networkscanner/)这种标杆软件，体积也只有10MB左右，可以扫/8以上的大段+ipv6，可以扫端口、arp、tcp/udp等. **这是有现成的满分答案，我没必要再做**.


## NetInfoChecker (E.ver)  To Do List (完工)

- 国内出口IP, 国外出口/走代理IP //**2024年7月27日**（V1.0 支持CN查询和GFW查询）
- 一键/Renew [ipcfg/release/renew/flushdns] //**2024年7月28日**（V1.1 新增一键ipconfig /release + /renew；flushdns于**2024年11月22日**V4.5a加入）
- 本机所有网卡 //**2024年8月23日**（V2.3 本机查询可一次查看所有网卡）
- 记录/读取国内IP //**2024年8月23日**（V2.1 增加记录IP功能）
- 快速跳转控制面板/常用网站 //**2024年8月30日**（V3.0 A3/A4 增加网维常用网址快速跳转）
- 本机硬件检测 //**2024年8月29日**（V3.0 A2 增加WMI系统配置检测）
- RFC3489/5780NAT测试 //**2024年10月11日**（V4.0 RFC3489上线；RFC5780于**2025年1月28日**V5.0加入）
- IPERF测速工具GUI //**2024年10月13日**（V4.1 增加IPERF局域网测速功能）
- 手动查IP/MAC //**2024年10月13日**（V4.1 增加手动查询IP；MAC查询于**2024年11月22日**V4.5a加入）
- 查国内IP在记录中出现次数 //**2024年11月25日**（V4.6a 查询CN IPV4在记录中出现次数）
- 自由查询 //**2024年12月27日**（V4.94a 自由查询窗口独立并支持深浅色模式）
- 悬浮信息时间显示 //**2024年12月21日**（V4.91a 双击标题栏打开悬浮窗显示系统时间和IP）
- 快速Ping/Tracert/Nslookup剪贴板 //**2024年12月27日**（V4.94a 支持快速ping/tra/nslookup剪贴板）
- IPV6有效期 //**2025年1月5日**（V4.98a 右键本机V6/临时V6查询IPV6地址有效期）
- WakeOnLan //**2025年2月8日**（V5 增加WOL功能）
- 多出口完整版（名站+三大+教育+国内外+测漏+双栈+UDP） //**2025年2月21日**（V5 增加多出口和UDP测试）
- 多出口精简版（名站+三大+教育+国内外+测漏） //**2025年7月26日**（V6.2508.6 增加多出口简洁版）
- 延迟测试[CS完美平台/全球网测骨干节点] //**2025年8月19日**（V7.2509.1 新增CS完美/全球测网延迟测试）
- **项目主体功能全部完成，等待查询器X发布前，进入维护阶段**...
