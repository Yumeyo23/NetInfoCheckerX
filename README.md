<img width="3543" height=<img width="3543" height="1025" alt="查询器X-Banner_看图王" src="https://github.com/user-attachments/assets/cf9ba0ef-cbc1-42a7-ba9f-5b0919c4f2fa" />
"1025" alt="查询器X-Banner_看图王" src="https://github.com/user-attachments/assets/749a3d82-7435-4086-b04c-d795c500b682" />
# NetInfoCheckerX 网络综合查询器X
Next Generation of [NetInfoChecker](https://github.com/Yumeyo23/NetInfoChecker) By Yumeyo
![查询器X图标_03](https://github.com/user-attachments/assets/c5ed66de-ff2a-48cb-94ac-7a5a9b40cbbd)

## 注意/Caution
- 本程序主要为中国用户设计/This program is mainly designed for Chinese users.
- 本程序是 [**网络综合查询器**](https://github.com/Yumeyo23/NetInfoChecker)(易语言版，简称**旧版查询器**) 的**C#重制版**，简称 **查询器X** 。
- 本程序是**专业向工具**，非**专业工具**，所有功能均以个人日常需求开发。
- 作者**非计算机/网络相关专业，无编程背景一切皆为兴趣**，只了解易语言，**为重制查询器才开始了解C#，大部分均为AI代替完成**(当然设计思路是自己的，也有自己完成的部分, 人力/AI比例可能三七?二八?)。故代码丑陋，初期不乐意开源，还请见谅。想喷的伙计轻点儿(x。欢迎大佬帮助开发/优化，有需要大佬联系可以提供源码
- 本程序架构**C# NET Framework 4.7.2 WinForm窗口程序**，且作者**无任何升/换框架/跨平台欲望**。
- 理由：1. 我菜；
    2. 生活中多数电脑都是Windows，NET4.7.2体积小巧、兼容性好，无需依赖即可运行，更便携，打包后10MB体积完成后面的一坨功能，~~不像NatTypeTester那样100MB只能测NAT一件事~~
    3. 手上只有手机，多数时候只需最常用的找AP、多出口、NAT测试就可以了，直接使用[查询器多出口在线版](https://yumeyo23.github.io/NetInfoChecker/checker-web.html)等网站/软件，**没有电脑的大多数情况下**需要的功能都可以解决。
- 感谢陪伴awa

## 灵感/致谢
- NAT类型测试灵感源自于[NatTypeTester](https://github.com/HMSBridge/NatTypeTester)(未使用其代码)，并加入了大量的优化功能：例如**显示debug信息、设置端口模式/起手、列出可用网卡+指定IP测试**  ~~(其实我也忍不了一个纯NAT测试要100MB)~~
- 本机配置检测，为了开发方便+数据准确，全部使用**图吧工具箱**公开的硬件检测SDK (包括WMI版和C++预览版都有使用)。
- iPerf工具来自于最新的[iperf3-win-builds](https://github.com/ar51an/iperf3-win-builds/releases)，受制于技术，只做了设置GUI用于拼接启动代码。
- 感谢程序里使用到的所有API提供商，因为数量太多不一一列举，可进入程序查看
- 用到了再感谢...
- 感谢所有AI教我写程序!!!

## To do list 
  Updated 25.12.19
- [x] 国内出口IP, 国外出口/走代理IP  **//25.11.19开工，待完工**
- [x] 本机硬件检测    **//25.11.20完工**
- [x] IPERF测速工具GUI     **//25.11.21完工**
- [x] RFC3489/5780NAT测试     **//25.11.22完工**
- [x] TCP/UDP Tracert+自定义网卡   **//25.11.22完工**
- [x] 本机所有网卡, 可快速复制IPV6/打开默认网关 **//25.11.28完工**
- [x] 快速Ping/Tracert/Nslookup剪贴板  **//25.11.29完工**
- [x] 一键/Renew [ipcfg/release/renew/flushdns] **//25.11.29完工**
- [x] 快速跳转控制面板  **//25.11.29完工**
- [x] IPV6有效期  **//25.11.29完工**
- [x] WakeOnLan  **//25.12.17完工**
- [x] 修改本机网卡信息  **//25.12.19已完工**
- [x] 深/浅色切换  **//25.12.18开工，待完工**
- [ ] 多出口测试精简版（名站+三大+教育+测漏）
- [ ] 多出口测试完整版（名站+三大+教育+测漏+双栈+UDP）
- [ ] 记录/读取国内IP
- [ ] 查国内IP在记录中出现次数
- [ ] 手动查IP/MAC
- [ ] UDP/ICMP Ping 延迟测试[CS完美平台/全球网测骨干节点]
- [ ] 自由查询
- [ ] 悬浮信息时间显示
- [ ] ~~最大连接数测试~~
- [ ] ~~UPnP打洞板~~

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
