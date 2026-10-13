## 第三方依赖及许可

程序完整包使用到了以下依赖及NuGet，在此致谢：

| 名字 | 协议 | 备注 |
|---|---|---|
| [图吧工具箱硬件检测引擎](https://www.tbtool.cn/sdk/index.html) | 免费使用 | 本机配置检测。直接使用图吧官方提供的 C++ 版，未修改；原 WMI 版本已停用，改为自有实现 |
| [iperf3-win-builds](https://github.com/ar51an/iperf3-win-builds/releases) | Apache-2.0 | iPerf3 测速工具，未修改。实际构建可能包含其他第三方组件，相关许可另行适用 |
| [LibreSpeed](https://github.com/librespeed/speedtest) | LGPL-3.0-or-later | LibreSpeed 测速服务。精简部分组件、修改 UI 及数据块大小以符合软件需求 |
| [OpenSpeedTest](https://github.com/openspeedtest/Speed-Test) | MIT | LibreSpeed 开服器 DLC（不附带，可选组件），未修改 |
| [Open.NAT](https://www.nuget.org/packages/Open.NAT/2.1.0) | MIT | UPnP 控制台，NuGet 包直接使用，未修改 |
| [EmbedIO](https://www.nuget.org/packages/EmbedIO) | MIT | LibreSpeed 服务端，NuGet 包直接使用，未修改 |
| [ScottPlot.WinForms](https://www.nuget.org/packages/ScottPlot.WinForms) | MIT | Ping 统计图绘制，NuGet 包直接使用，未修改。 |
| [IP2Region.Net](https://www.nuget.org/packages/IP2Region.Net/) | Apache-2.0 | 程序内置 IP 地理位置数据库读取功能，用于主窗口、手动查询 IP 和 Trace+；NuGet 包直接使用，未修改   |
| [MaxMind.DB](https://www.nuget.org/packages/MaxMind.DB) | Apache-2.0 | IP 地理位置数据库读取功能，用于主窗口、手动查询 IP 和 Trace+；NuGet 包直接使用，未修改。实际数据库使用 [GeoCN](https://github.com/ljxi/GeoCN)，其再分发授权待确认。 |
| [DnsClient](https://www.nuget.org/packages/DnsClient/) | Apache-2.0 | DNS 劫持测试，NuGet 包直接使用，未修改 |
| [WinDivert](https://github.com/basil00/WinDivert) | LGPL-3.0 或 GPL-2.0 （本项目选择并整改符合 LGPL-3.0） | Trace+ 高精度测试，未修改 |
| [Merged-IP-Data](https://github.com/NetworkCats/Merged-IP-Data) | MIT (项目), GeoLite2/IPinfo (CC BY-SA 4.0), DB-IP (CC BY 4.0), Origin ASN (CDLA-Permissive 2.0), OpenProxyDB (CC0 1.0), X4B (MIT), NordVPN | 本地 IP 数据库 DLC（不附带，可选组件），未修改 |
| [Obfuscar](https://www.nuget.org/packages/Obfuscar) | MIT | 备用混淆器，目前发布版使用 [ConfuserEx](https://github.com/mkaring/ConfuserEx) |
| [ConfuserEx](https://github.com/mkaring/ConfuserEx) | MIT | 当前发布版使用的混淆器 |
| [SpeedTest.Net 节点](https://github.com/SukkaW/speedtest-net-servers) | MIT | 多出口测试 SpeedTest.Net 节点列表|
| [IEEE 官方 MAC 地址表](http://standards-oui.ieee.org/oui/oui.csv)、[Wireshark MAC 地址表](https://www.wireshark.org/download/automated/data/manuf.gz) | 官方数据 | 用于手动查询 MAC 地址，数据文件未修改 |