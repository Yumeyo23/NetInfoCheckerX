using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NetInfoCheckerX
{
    public partial class HWInfoWMI : Form
    {
        //自由拖拽1
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern bool SendMessage(IntPtr hwnd, int wMsg, int wParam, int lParam);

        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_MOVE = 0xF010;
        private const int HTCAPTION = 0x0002;

        private void MyMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(this.Handle, WM_SYSCOMMAND, SC_MOVE + HTCAPTION, 0);
            }
        }

        public HWInfoWMI()
        {
            InitializeComponent();

            var existingForm = Application.OpenForms.OfType<HWInfoWMI>()
                                  .FirstOrDefault(f => f != this);

            if (existingForm != null)
            {
                existingForm.BringToFront();
                existingForm.Focus();
                this.Dispose();
            }
            else
            {
                base.Show();
            }
        }

        private async Task ApplyHWInfoThemeAsync()
        {
            bool isLight = Global.isThemelight;

            this.BackColor = isLight ? Global.themeLight : Global.themeBlack;

            Color yumeyoColor = isLight ? ColorTranslator.FromHtml("#8e8cd8") : ColorTranslator.FromHtml("#a8a5ff");
            Label[] yumeyoLabels = { lblPCName, lblExeName };
            foreach (var lbl in yumeyoLabels) { if (lbl != null) lbl.ForeColor = yumeyoColor; }

            Color contrastColor = isLight ? Color.Black : Color.White;

            Label[] contrastLabels = { lblCheckTime, lblSysInsTime, lblSysUpTime };
            foreach (var lbl in contrastLabels) { if (lbl != null) lbl.ForeColor = contrastColor; }

            if (txtPCINFO != null)
            {
                txtPCINFO.ForeColor = contrastColor;
                txtPCINFO.BackColor = isLight ? Global.themeLight : Global.themeBlack;
            }
        }

        // ==================== WMI 硬件检测核心方法 ====================

        /// <summary>
        /// 获取系统信息
        /// </summary>
        private (string caption, string build, string bitness, string osType) GetSystemInfo()
        {
            try
            {
                var os = new ManagementObjectSearcher(
                    "SELECT Caption, BuildNumber FROM Win32_OperatingSystem")
                    .Get().Cast<ManagementObject>().FirstOrDefault();

                string caption = "Windows 未知";
                string build = "未知";
                if (os != null)
                {
                    caption = os["Caption"]?.ToString()?.Trim() ?? "Windows 未知";
                    build = os["BuildNumber"]?.ToString() ?? "未知";
                }
                else
                {
                    // 回退：从注册表读取
                    try
                    {
                        caption = Registry.GetValue(
                            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                            "ProductName", "Windows 未知") as string ?? "Windows 未知";
                        build = Registry.GetValue(
                            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                            "CurrentBuild", "未知") as string ?? "未知";
                    }
                    catch { }
                }

                string bitness = Environment.Is64BitOperatingSystem ? "64 位" : "32 位";

                string osType = "未知";
                try
                {
                    // 方案1: Win32_SystemEnclosure.ChassisTypes
                    var chassisList = new ManagementObjectSearcher(
                        "SELECT ChassisTypes FROM Win32_SystemEnclosure")
                        .Get().Cast<ManagementObject>().ToList();

                    foreach (var chassis in chassisList)
                    {
                        object obj = chassis["ChassisTypes"];
                        if (obj is Array arr && arr.Length > 0)
                        {
                            ushort val = Convert.ToUInt16(arr.GetValue(0));
                            if (val >= 1 && val <= 30)
                            {
                                osType = GetChassisTypeName(val);
                                break;
                            }
                        }
                    }

                    // 方案2: 如果方案1失败，尝试 Win32_ComputerSystem.PCSystemType
                    if (osType == "未知")
                    {
                        var cs = new ManagementObjectSearcher(
                            "SELECT PCSystemType FROM Win32_ComputerSystem")
                            .Get().Cast<ManagementObject>().FirstOrDefault();
                        if (cs != null)
                        {
                            object sysType = cs["PCSystemType"];
                            if (sysType != null)
                            {
                                ushort st = Convert.ToUInt16(sysType);
                                // PCSystemType: 1=Desktop, 2=Mobile/Laptop, 3=Workstation, 4=Enterprise Server, etc.
                                switch (st)
                                {
                                    case 1: osType = "台式机"; break;
                                    case 2: osType = "笔记本"; break;
                                    case 3: osType = "工作站"; break;
                                    case 4: osType = "企业服务器"; break;
                                    case 5: osType = "SOHO服务器"; break;
                                    case 6: osType = "平板电脑"; break;
                                    default: break;
                                }
                            }
                        }
                    }
                }
                catch { }

                return (caption, build, bitness, osType);
            }
            catch
            {
                return ("获取失败", "获取失败", "获取失败", "获取失败");
            }
        }

        /// <summary>
        /// 获取 CPU 信息
        /// </summary>
        private List<(string name, int cores, int threads)> GetCPUInfo()
        {
            try
            {
                var cpus = new ManagementObjectSearcher(
                    "SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor")
                    .Get().Cast<ManagementObject>().ToList();

                var result = new List<(string, int, int)>();

                foreach (var cpu in cpus)
                {
                    string name = cpu["Name"]?.ToString()?.Trim() ?? "未知";
                    // 清理 CPU 名称中多余的空格
                    name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+", " ");

                    int cores = 0;
                    int.TryParse(cpu["NumberOfCores"]?.ToString(), out cores);

                    int threads = 0;
                    int.TryParse(cpu["NumberOfLogicalProcessors"]?.ToString(), out threads);

                    result.Add((name, cores, threads));
                }

                return result;
            }
            catch
            {
                return new List<(string, int, int)>();
            }
        }

        /// <summary>
        /// 获取主板和 BIOS 信息
        /// </summary>
        private (string boardModel, string boardBrand, string biosVersion) GetMainboardInfo()
        {
            try
            {
                var board = new ManagementObjectSearcher(
                    "SELECT Product, Manufacturer FROM Win32_BaseBoard")
                    .Get().Cast<ManagementObject>().FirstOrDefault();

                var bios = new ManagementObjectSearcher(
                    "SELECT SMBIOSBIOSVersion, Version FROM Win32_BIOS")
                    .Get().Cast<ManagementObject>().FirstOrDefault();

                string boardModel = board?["Product"]?.ToString()?.Trim() ?? "未知";
                string boardBrand = board?["Manufacturer"]?.ToString()?.Trim() ?? "未知";
                string biosVersion = bios?["SMBIOSBIOSVersion"]?.ToString()?.Trim()
                                  ?? bios?["Version"]?.ToString()?.Trim()
                                  ?? "未知";

                return (boardModel, boardBrand, biosVersion);
            }
            catch
            {
                return ("获取失败", "获取失败", "获取失败");
            }
        }

        /// <summary>
        /// 获取内存信息
        /// </summary>
        private (int totalGB, List<(string capacity, string speed, string brand)> details) GetMemoryInfo()
        {
            try
            {
                var memories = new ManagementObjectSearcher(
                    "SELECT Capacity, Speed, Manufacturer, PartNumber FROM Win32_PhysicalMemory")
                    .Get().Cast<ManagementObject>().ToList();

                int totalGB = 0;
                var details = new List<(string capacity, string speed, string brand)>();

                foreach (var mem in memories)
                {
                    long.TryParse(mem["Capacity"]?.ToString(), out long bytes);
                    long gb = bytes / 1024 / 1024 / 1024;
                    totalGB += (int)gb;

                    string capacity = $"{gb}GB";
                    string speed = mem["Speed"]?.ToString() ?? "未知";
                    string manufacturer = mem["Manufacturer"]?.ToString()?.Trim() ?? "未知";

                    // 如果制造商为通用名，尝试用 PartNumber 中的信息
                    if (manufacturer == "未知" || manufacturer == "0000" || manufacturer == "Undefined")
                    {
                        string part = mem["PartNumber"]?.ToString()?.Trim() ?? "";
                        if (!string.IsNullOrEmpty(part))
                        {
                            // 尝试从 PartNumber 提取品牌
                            manufacturer = ExtractBrandFromPartNumber(part);
                        }
                    }

                    details.Add((capacity, speed, manufacturer));
                }

                return (totalGB, details);
            }
            catch
            {
                return (0, new List<(string, string, string)> { ("获取失败", "", "") });
            }
        }

        /// <summary>
        /// 获取显卡信息
        /// </summary>
        private List<(string name, string memory, string driver)> GetGPUInfo()
        {
            try
            {
                var gpus = new ManagementObjectSearcher(
                    "SELECT Name, AdapterRAM, DriverVersion FROM Win32_VideoController")
                    .Get().Cast<ManagementObject>().ToList();

                var result = new List<(string, string, string)>();

                foreach (var gpu in gpus)
                {
                    string name = gpu["Name"]?.ToString()?.Trim() ?? "未知显卡";
                    long.TryParse(gpu["AdapterRAM"]?.ToString(), out long bytes);
                    string driver = gpu["DriverVersion"]?.ToString() ?? "未知";

                    string memory;
                    if (bytes > 0)
                    {
                        long mb = bytes / 1024 / 1024;
                        memory = $"{mb}MB";
                    }
                    else
                    {
                        // 共享显存或无独立显存
                        memory = "共享";
                    }

                    result.Add((name, memory, driver));
                }

                return result;
            }
            catch
            {
                return new List<(string, string, string)>();
            }
        }

        /// <summary>
        /// 获取显示器信息 (含 EDID 解析)
        /// </summary>
        private List<(string name, string mfrAndId, string size, string date)> GetMonitorInfo()
        {
            var result = new List<(string, string, string, string)>();
            var seenHardwareIds = new HashSet<string>();

            try
            {
                // 方案1: 从注册表枚举 DISPLAY 设备获取 EDID
                var edidMonitors = GetMonitorsFromRegistryEDID(seenHardwareIds);
                result.AddRange(edidMonitors);

                // 方案2: 如果注册表没找到，尝试 WMI DesktopMonitor
                if (result.Count == 0)
                {
                    var monitors = new ManagementObjectSearcher(
                        "SELECT Name, PNPDeviceID FROM Win32_DesktopMonitor")
                        .Get().Cast<ManagementObject>().ToList();

                    foreach (var mon in monitors)
                    {
                        string pnpId = mon["PNPDeviceID"]?.ToString()?.Trim() ?? "";
                        if (string.IsNullOrEmpty(pnpId)) continue;

                        string hwId = ExtractHardwareId(pnpId);
                        if (string.IsNullOrEmpty(hwId) || !seenHardwareIds.Add(hwId)) continue;

                        byte[] edid = ReadEDIDFromRegistry(pnpId);
                        if (edid != null)
                        {
                            var parsed = ParseEDID(edid, hwId);
                            result.Add(parsed);
                        }
                        else
                        {
                            string name = mon["Name"]?.ToString()?.Trim() ?? "未知显示器";
                            // 如果 WMI 返回的是通用名且有硬件ID，用硬件ID
                            string displayName = (name == "通用即插即用监视器" || name == "Generic PnP Monitor")
                                ? hwId : name;
                            result.Add((displayName, hwId, "未知", "未知"));
                        }
                    }
                }

                // 方案3: Win32_PnPEntity (Monitor 类)
                if (result.Count == 0)
                {
                    var pnpMonitors = new ManagementObjectSearcher(
                        @"SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPClass = 'Monitor'")
                        .Get().Cast<ManagementObject>().ToList();

                    foreach (var mon in pnpMonitors)
                    {
                        string pnpId = mon["PNPDeviceID"]?.ToString()?.Trim() ?? "";
                        if (string.IsNullOrEmpty(pnpId)) continue;

                        string hwId = ExtractHardwareId(pnpId);
                        if (string.IsNullOrEmpty(hwId) || !seenHardwareIds.Add(hwId)) continue;

                        byte[] edid = ReadEDIDFromRegistry(pnpId);
                        if (edid != null)
                        {
                            var parsed = ParseEDID(edid, hwId);
                            result.Add(parsed);
                        }
                        else
                        {
                            string name = mon["Name"]?.ToString()?.Trim() ?? "未知显示器";
                            result.Add((name, hwId, "未知", "未知"));
                        }
                    }
                }
            }
            catch { }

            return result;
        }

        /// <summary>
        /// 获取硬盘信息
        /// </summary>
        private List<(string model, string size)> GetDiskInfo()
        {
            try
            {
                var disks = new ManagementObjectSearcher(
                    "SELECT Model, Size FROM Win32_DiskDrive")
                    .Get().Cast<ManagementObject>().ToList();

                var result = new List<(string, string)>();

                foreach (var disk in disks)
                {
                    string model = disk["Model"]?.ToString()?.Trim() ?? "未知硬盘";
                    long.TryParse(disk["Size"]?.ToString(), out long bytes);

                    string size = "未知";
                    if (bytes > 0)
                    {
                        double gb = bytes / 1024.0 / 1024.0 / 1024.0;
                        size = $"{gb:F0}GB";
                    }

                    // 跳过可移动设备 (Size=0 的通常是读卡器)
                    if (bytes <= 0) continue;

                    result.Add((model, size));
                }

                return result;
            }
            catch
            {
                return new List<(string, string)>();
            }
        }

        /// <summary>
        /// 获取网卡信息 (含类型判断和实时握手速率)
        /// </summary>
        private List<(string name, string type, string mac, string speed)> GetNetworkAdapterInfo()
        {
            try
            {
                // 获取网络适配器列表 (物理适配器)
                var adapters = new ManagementObjectSearcher(
                    "SELECT Name, AdapterType, Speed, Index, PNPDeviceID, NetConnectionID, GUID " +
                    "FROM Win32_NetworkAdapter " +
                    "WHERE PhysicalAdapter = true")
                    .Get().Cast<ManagementObject>().ToList();

                // 获取网络适配器配置 (MAC地址)
                var configs = new ManagementObjectSearcher(
                    "SELECT Index, MACAddress, IPEnabled FROM Win32_NetworkAdapterConfiguration")
                    .Get().Cast<ManagementObject>()
                    .ToDictionary(c => Convert.ToInt32(c["Index"]), c => c);

                // 尝试从 StandardCimv2 获取实时链路速率
                Dictionary<int, long> linkSpeeds = null;
                try
                {
                    var msftAdapters = new ManagementObjectSearcher(
                        "ROOT\\StandardCimv2",
                        "SELECT InterfaceIndex, TransmitLinkSpeed, ReceiveLinkSpeed FROM MSFT_NetAdapter " +
                        "WHERE TransmitLinkSpeed > 0")
                        .Get().Cast<ManagementObject>();
                    linkSpeeds = new Dictionary<int, long>();
                    foreach (var ma in msftAdapters)
                    {
                        int idx = Convert.ToInt32(ma["InterfaceIndex"]);
                        long txSpeed = 0;
                        long.TryParse(ma["TransmitLinkSpeed"]?.ToString(), out txSpeed);
                        if (txSpeed > 0 && !linkSpeeds.ContainsKey(idx))
                            linkSpeeds[idx] = txSpeed;
                    }
                }
                catch { }

                var result = new List<(string, string, string, string)>();

                foreach (var adapter in adapters)
                {
                    int index = Convert.ToInt32(adapter["Index"]);
                    string pnpId = adapter["PNPDeviceID"]?.ToString()?.Trim() ?? "";
                    string connId = adapter["NetConnectionID"]?.ToString()?.Trim() ?? "";
                    string hwName = adapter["Name"]?.ToString()?.Trim() ?? "";

                    // 跳过蓝牙、虚拟设备
                    string lowerName = hwName.ToLower();
                    if (lowerName.Contains("bluetooth") || lowerName.Contains("蓝牙")) continue;

                    // 获取 MAC 地址
                    string mac = "未知";
                    if (configs.ContainsKey(index))
                    {
                        mac = configs[index]["MACAddress"]?.ToString() ?? "未知";
                    }

                    if (string.IsNullOrEmpty(mac) || mac == "未知") continue;

                    // 使用硬件名称 (Name) 作为显示名，NetConnectionID 作为回退
                    string name = hwName;
                    if (string.IsNullOrEmpty(name))
                        name = connId;
                    if (string.IsNullOrEmpty(name))
                        name = "未知网卡";

                    string adapterType = adapter["AdapterType"]?.ToString()?.Trim() ?? "";
                    // 类型判断: 综合 AdapterType、硬件名、连接名、PNPID
                    string type = GetAdapterTypeName(adapterType, pnpId, hwName, connId);

                    // 速度: 优先用 MSFT_NetAdapter 的实时速率，然后回退 Win32_NetworkAdapter.Speed
                    string speed = "未知";
                    if (linkSpeeds != null && linkSpeeds.ContainsKey(index))
                    {
                        speed = FormatSpeed(linkSpeeds[index]);
                    }
                    else
                    {
                        long.TryParse(adapter["Speed"]?.ToString(), out long bps);
                        if (bps > 0) speed = FormatSpeed(bps);
                    }

                    result.Add((name, type, mac, speed));
                }

                return result;
            }
            catch
            {
                return new List<(string, string, string, string)>();
            }
        }

        /// <summary>
        /// 获取声卡信息
        /// </summary>
        private List<string> GetSoundCardInfo()
        {
            try
            {
                var soundcards = new ManagementObjectSearcher(
                    "SELECT Name FROM Win32_SoundDevice")
                    .Get().Cast<ManagementObject>().ToList();

                var result = new List<string>();

                foreach (var sc in soundcards)
                {
                    string name = sc["Name"]?.ToString()?.Trim() ?? "未知声卡";
                    if (!string.IsNullOrEmpty(name))
                    {
                        result.Add(name);
                    }
                }

                return result;
            }
            catch
            {
                return new List<string>();
            }
        }

        // ==================== 辅助方法 ====================

        /// <summary>
        /// 将 SMBIOS 机箱类型代码转换为友好名称
        /// 参考: DMTF SMBIOS Reference Specification, Type 3 (System Enclosure)
        /// </summary>
        private string GetChassisTypeName(ushort type)
        {
            switch (type)
            {
                // 台式机系列
                case 3: return "台式机";           // Desktop
                case 4: return "小型台式机";        // Low Profile Desktop
                case 5: return "小型台式机";        // Pizza Box
                case 6: return "迷你塔式机箱";      // Mini Tower
                case 7: return "塔式机箱";          // Tower

                // 笔记本系列
                case 8: return "便携式计算机";      // Portable
                case 9: return "便携式计算机";            // Laptop
                case 10: return "笔记本";           // Notebook
                case 14: return "超便携笔记本";      // Sub Notebook

                // 平板系列
                case 30: return "平板电脑";         // Tablet
                case 31: return "可变形式平板";      // Convertible
                case 32: return "可拆卸式平板";      // Detachable

                // 一体机
                case 13: return "一体机";           // All in One

                // 迷你/小型电脑
                case 15: return "小型机";           // Space-saving
                case 35: return "迷你电脑";         // Mini PC
                case 36: return "电脑棒";           // Stick PC

                // 服务器系列
                case 17: return "主系统机箱";        // Main System Chassis
                case 23: return "机架式服务器";      // Rack Mount Chassis
                case 28: return "刀片服务器";        // Blade
                case 29: return "刀片机箱";          // Blade Enclosure

                // 手持/嵌入式
                case 11: return "手持设备";         // Hand Held
                case 33: return "IoT网关";          // IoT Gateway
                case 34: return "嵌入式电脑";        // Embedded PC

                // 扩展/外设
                case 12: return "扩展坞";           // Docking Station
                case 18: return "扩展机箱";          // Expansion Chassis
                case 21: return "外设机箱";          // Peripheral Chassis

                // 存储
                case 22: return "存储机箱";          // Storage Chassis

                // 其他通用
                case 1: return "其他";
                case 2: return "未知类型";
                case 16: return "便携式机箱";        // Lunch Box
                case 19: return "子机箱";            // SubChassis
                case 20: return "总线扩展机箱";       // Bus Expansion Chassis
                case 24: return "密封机箱";          // Sealed Case PC
                case 25: return "多系统机箱";         // Multi-system Chassis
                case 26: return "Compact PCI";
                case 27: return "AdvancedTCA";

                default: return "未知类型";
            }
        }

        /// <summary>
        /// 从注册表 DISPLAY 枚举读取 EDID 获取显示器信息
        /// </summary>
        private List<(string name, string mfrAndId, string size, string date)> GetMonitorsFromRegistryEDID(
            HashSet<string> seenHardwareIds)
        {
            var result = new List<(string, string, string, string)>();

            try
            {
                using (var displayKey = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Enum\DISPLAY"))
                {
                    if (displayKey == null) return result;

                    foreach (string hwId in displayKey.GetSubKeyNames())
                    {
                        // hwId 格式: "BOE0A92", "SAM0D20" 等
                        if (string.IsNullOrEmpty(hwId)) continue;
                        if (!seenHardwareIds.Add(hwId)) continue;

                        using (var hwKey = displayKey.OpenSubKey(hwId))
                        {
                            if (hwKey == null) continue;

                            // 遍历实例子键
                            foreach (string instanceId in hwKey.GetSubKeyNames())
                            {
                                string fullPath = @"SYSTEM\CurrentControlSet\Enum\DISPLAY\" +
                                                  hwId + "\\" + instanceId + @"\Device Parameters";
                                using (var devKey = Registry.LocalMachine.OpenSubKey(fullPath))
                                {
                                    if (devKey == null) continue;

                                    byte[] edid = devKey.GetValue("EDID") as byte[];
                                    if (edid != null && edid.Length >= 128)
                                    {
                                        var parsed = ParseEDID(edid, hwId);
                                        result.Add(parsed);
                                        break; // 找到一个实例的 EDID 就够了
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            return result;
        }

        /// <summary>
        /// 从注册表读取 EDID
        /// </summary>
        private byte[] ReadEDIDFromRegistry(string pnpDeviceId)
        {
            try
            {
                // PNPDeviceID 如: DISPLAY\BOE0A92\5&1234567&0&UID_12345
                string regPath = @"SYSTEM\CurrentControlSet\Enum\" + pnpDeviceId + @"\Device Parameters";
                using (var key = Registry.LocalMachine.OpenSubKey(regPath))
                {
                    if (key != null)
                    {
                        byte[] edid = key.GetValue("EDID") as byte[];
                        if (edid != null && edid.Length >= 128) return edid;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 解析 EDID 数据
        /// </summary>
        private (string name, string mfrAndId, string size, string date) ParseEDID(byte[] edid, string hwId)
        {
            if (edid == null || edid.Length < 128)
                return ("未知显示器", hwId, "未知", "未知");

            // 验证 EDID 头部
            byte[] header = { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };
            bool validHeader = true;
            for (int i = 0; i < 8; i++)
            {
                if (edid[i] != header[i]) { validHeader = false; break; }
            }
            if (!validHeader)
                return ("未知显示器", hwId, "未知", "未知");

            // 解析制造商ID (字节 8-9)
            string manufacturer = DecodeEDIDManufacturer(edid);

            // 解析产品代码 (字节 10-11, little-endian)
            ushort productCode = (ushort)(edid[10] | (edid[11] << 8));

            // 获取制造商中文名
            string mfrDisplay = GetMonitorManufacturerName(manufacturer);

            // 产品ID
            string productId = $"{manufacturer}{productCode:X4}";

            // 生成 mfrAndId 字符串: "京东方 BOE0A92"
            string mfrAndId;
            if (mfrDisplay != manufacturer)
                mfrAndId = $"{mfrDisplay} {productId}";
            else
                mfrAndId = hwId;

            // 解析生产日期 (字节 16: 周, 字节 17: 年-1990)
            int week = edid[16];
            int year = edid[17] + 1990;
            string date;
            if (week >= 1 && week <= 53 && year >= 2000 && year <= 2100)
                date = $"{year}年第{week}周";
            else
                date = "未知";

            // 解析屏幕物理尺寸 (字节 21: 宽cm, 字节 22: 高cm)
            int widthCm = edid[21];
            int heightCm = edid[22];
            string size;
            if (widthCm > 0 && heightCm > 0)
            {
                double diagonalCm = Math.Sqrt(widthCm * widthCm + heightCm * heightCm);
                double diagonalInch = diagonalCm / 2.54;
                // 舍入到 0.5 英寸精度
                double rounded = Math.Round(diagonalInch * 2) / 2;
                if (Math.Abs(rounded - Math.Round(diagonalInch)) < 0.3)
                    rounded = Math.Round(diagonalInch);
                // 格式化: 整数显示整数，小数显示一位小数
                if (Math.Abs(rounded - Math.Round(rounded)) < 0.01)
                    size = $"{(int)Math.Round(rounded)}英寸";
                else
                    size = $"{rounded:F1}英寸";
            }
            else
            {
                size = "未知";
            }

            // 解析显示器名称 (从 EDID 描述符块)
            string monitorName = ExtractEDIDMonitorName(edid);
            if (string.IsNullOrEmpty(monitorName) || monitorName == "未知")
                monitorName = hwId;

            return (monitorName, mfrAndId, size, date);
        }

        /// <summary>
        /// 解码 EDID 制造商3字母代码
        /// </summary>
        private string DecodeEDIDManufacturer(byte[] edid)
        {
            // 字节8: bit7(保留)=0, bit6-2=字母1(5bit), bit1-0=字母2高2位
            // 字节9: bit7-5=字母2低3位(5bit总), bit4-0=字母3(5bit)
            char c1 = (char)(((edid[8] >> 2) & 0x1F) + 'A' - 1);
            char c2 = (char)((((edid[8] & 0x03) << 3) | ((edid[9] >> 5) & 0x07)) + 'A' - 1);
            char c3 = (char)((edid[9] & 0x1F) + 'A' - 1);

            return $"{c1}{c2}{c3}";
        }

        /// <summary>
        /// 从 EDID 描述符块提取显示器名称
        /// </summary>
        private string ExtractEDIDMonitorName(byte[] edid)
        {
            // 4个描述符块，每个18字节，起始于字节54
            // 先找 0xFC (显示器名称), 再找 0xFE (其他文本) 作为回退
            for (int pass = 0; pass < 2; pass++)
            {
                byte targetTag = (pass == 0) ? (byte)0xFC : (byte)0xFE;

                for (int block = 0; block < 4; block++)
                {
                    int offset = 54 + block * 18;
                    if (offset + 18 > edid.Length) break;

                    // 描述符块前2字节为0x0000表示非时序描述符
                    // 不要求 byte2 和 byte4 必须为0x00 (部分EDID实现会设非零值)
                    if (edid[offset] == 0x00 && edid[offset + 1] == 0x00 &&
                        edid[offset + 3] == targetTag)
                    {
                        byte[] nameBytes = new byte[13];
                        Array.Copy(edid, offset + 5, nameBytes, 0, 13);
                        // 查找换行符终止符 (0x0A)
                        int endIdx = Array.IndexOf(nameBytes, (byte)0x0A);
                        string text;
                        if (endIdx >= 0)
                            text = Encoding.ASCII.GetString(nameBytes, 0, endIdx).Trim();
                        else
                            text = Encoding.ASCII.GetString(nameBytes).Trim('\0', ' ');

                        if (!string.IsNullOrEmpty(text))
                            return text;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 将 EDID 制造商3字母代码映射为中文名
        /// </summary>
        private string GetMonitorManufacturerName(string code)
        {
            switch (code)
            {
                case "AUO": return "友达光电";
                case "BOE": return "京东方";
                case "CMN": return "奇美";
                case "CMO": return "奇美";
                case "LGD": return "LG Display";
                case "SAM": return "三星";
                case "SEC": return "三星";
                case "SHP": return "夏普";
                case "IVO": return "龙腾光电";
                case "LEN": return "联想";
                case "HSD": return "瀚宇彩晶";
                case "INX": return "群创光电";
                case "CHR": return "中华映管";
                case "CPT": return "中华映管";
                case "TOS": return "东芝";
                case "HWP": return "惠普";
                case "HPN": return "惠普";
                case "DEL": return "戴尔";
                case "ACR": return "宏碁";
                case "AOC": return "冠捷";
                case "PHL": return "飞利浦";
                case "VSC": return "优派";
                case "SNY": return "索尼";
                case "GSM": return "LG电子";
                case "MSI": return "微星";
                case "ASU": return "华硕";
                case "APP": return "苹果";
                case "NEC": return "NEC";
                case "MIT": return "三菱";
                case "EIZ": return "EIZO";
                case "PIO": return "先锋";
                case "PAN": return "松下";
                case "MEI": return "明基";
                case "PRS": return "ProScan";
                case "RTK": return "瑞昱";
                case "SKY": return "skyworth";
                case "HKC": return "惠科";
                case "KTC": return "康冠";
                default: return code;
            }
        }

        /// <summary>
        /// 从 PNPDeviceID 提取硬件ID (如 "DISPLAY\BOE0A92\..." -> "BOE0A92")
        /// </summary>
        private string ExtractHardwareId(string pnpDeviceId)
        {
            if (string.IsNullOrEmpty(pnpDeviceId)) return "";
            string[] parts = pnpDeviceId.Split('\\');
            if (parts.Length >= 2)
                return parts[1];
            return pnpDeviceId;
        }

        /// <summary>
        /// 判断网卡类型
        /// </summary>
        private string GetAdapterTypeName(string adapterType, string pnpDeviceId,
            string hardwareName, string connId)
        {
            // 综合判断: 硬件名权重最高，然后连接名，再 AdapterType

            string hwLower = (hardwareName ?? "").ToLower();
            string connLower = (connId ?? "").ToLower();

            // 硬件名或连接名明确包含无线标识
            if (hwLower.Contains("wireless") || hwLower.Contains("wlan") ||
                hwLower.Contains("wi-fi") || hwLower.Contains("wifi") ||
                hwLower.Contains("802.11") ||
                connLower.Contains("wlan") || connLower.Contains("wi-fi") ||
                connLower.Contains("wifi") || connLower.Contains("无线"))
                return "WLAN";

            // 硬件名或连接名明确包含有线标识
            if (hwLower.Contains("ethernet") || hwLower.Contains("gbe") ||
                hwLower.Contains("pcie") || hwLower.Contains("pci-e") ||
                connLower.Contains("以太") || connLower.Contains("ethernet"))
                return "以太网";

            // AdapterType 包含明确标识
            if (!string.IsNullOrEmpty(adapterType))
            {
                string lower = adapterType.ToLower();
                if (lower.Contains("802.11") || lower.Contains("wireless") ||
                    lower.Contains("wi-fi") || lower.Contains("wifi"))
                    return "WLAN";
                if (lower.Contains("802.3") || lower.Contains("ethernet"))
                    return "以太网";
            }

            // PNPDeviceID 标识: USB 通常是无线网卡
            string pnpLower = (pnpDeviceId ?? "").ToLower();
            if (pnpLower.Contains("usb"))
            {
                if (hwLower.Contains("wireless") || hwLower.Contains("wlan") ||
                    hwLower.Contains("wi-fi") || hwLower.Contains("wifi") ||
                    connLower.Contains("wlan") || connLower.Contains("wi-fi") ||
                    hwLower.Contains("bluetooth") == false)
                {
                    // USB 网卡大多是无线 (但也可以是有线USB网卡)
                    if (hwLower.Contains("802.3") || hwLower.Contains("ethernet") ||
                        hwLower.Contains("gbe"))
                        return "以太网";
                    return "WLAN";
                }
            }

            // 兜底: 默认以太网
            return "以太网";
        }

        /// <summary>
        /// 格式化网络速率
        /// </summary>
        private string FormatSpeed(long bps)
        {
            if (bps >= 1000000000)
                return $"{bps / 1000000000.0:F1}Gbps";
            else if (bps >= 1000000)
                return $"{bps / 1000000}Mbps";
            else if (bps >= 1000)
                return $"{bps / 1000}Kbps";
            else
                return $"{bps}bps";
        }

        /// <summary>
        /// 从内存 PartNumber 提取品牌
        /// </summary>
        private string ExtractBrandFromPartNumber(string partNumber)
        {
            if (string.IsNullOrEmpty(partNumber)) return "未知";

            string upper = partNumber.ToUpper().Trim();
            // 常见内存品牌前缀
            if (upper.StartsWith("KF") || upper.StartsWith("KVR") || upper.StartsWith("KHX") ||
                upper.StartsWith("HX") || upper.StartsWith("FURY"))
                return "Kingston";
            if (upper.StartsWith("CM") && (upper.Contains("Corsair") || upper.Contains("CORSAIR")))
                return "Corsair";
            if (upper.StartsWith("F4-") || upper.StartsWith("F5-") || upper.Contains("GSKILL"))
                return "G.Skill";
            if (upper.StartsWith("BL") || upper.Contains("BALLISTIX"))
                return "Crucial";
            if (upper.StartsWith("CT") || upper.Contains("CRUCIAL"))
                return "Crucial";
            if (upper.StartsWith("M378") || upper.StartsWith("M471") || upper.StartsWith("M393"))
                return "Samsung";
            if (upper.StartsWith("HMA") || upper.StartsWith("HMT") || upper.StartsWith("HMAA"))
                return "Hynix";
            if (upper.StartsWith("MT") && upper.Length >= 6 && char.IsDigit(upper[2]))
                return "Micron";
            if (upper.StartsWith("WPBH") || upper.StartsWith("WPBS"))
                return "SpecTek";
            if (upper.StartsWith("NT") || upper.StartsWith("NEMIX"))
                return "Nemix";
            if (upper.StartsWith("AD") || upper.Contains("ADATA"))
                return "ADATA";
            if (upper.Contains("KINGSTON"))
                return "Kingston";
            if (upper.Contains("SAMSUNG"))
                return "Samsung";
            if (upper.Contains("HYNIX") || upper.Contains("SK HYNIX"))
                return "Hynix";
            if (upper.Contains("MICRON"))
                return "Micron";
            if (upper.Contains("CRUCIAL"))
                return "Crucial";
            if (upper.Contains("G.SKILL") || upper.Contains("GSKILL"))
                return "G.Skill";
            if (upper.Contains("CORSAIR"))
                return "Corsair";
            if (upper.Contains("ADATA") || upper.Contains("A-DATA"))
                return "ADATA";

            return partNumber.Trim();
        }

        // ==================== WMI 服务检测 ====================

        /// <summary>
        /// 检测 WMI 服务是否可用
        /// </summary>
        private bool IsWmiAvailable()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT InstallDate FROM Win32_OperatingSystem"))
                {
                    searcher.Get();
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ==================== 窗体事件 ====================

        private async void HWInfoWMI_Load(object sender, EventArgs e)
        {
            _ = ApplyHWInfoThemeAsync();

            this.MouseDown += MyMouseDown;
            pictureBox1.MouseDown += MyMouseDown;

            lblExeName.Text = Global.exeName + " " + Global.Version;

            txtPCINFO.Text = "🔰   正在读取配置(纯WMI实现)   🔰\r\n" +
                             "Tips: 本工具直接使用系统 WMI 服务获取硬件信息，无外部依赖，不会被杀毒软件误判。";

            lblPCName.Text = Environment.MachineName;

            // 已运行时长
            UpdateUptimeDisplay();

            // 配置检测时间
            lblCheckTime.Text = "检测时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            // 系统安装时间
            try
            {
                var os = new ManagementObjectSearcher(
                    "SELECT InstallDate FROM Win32_OperatingSystem").Get()
                    .Cast<ManagementObject>().First();
                var installDate = ManagementDateTimeConverter
                    .ToDateTime(os["InstallDate"].ToString());
                lblSysInsTime.Text = "系统安装: " + installDate.ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch
            {
                lblSysInsTime.Text = "系统安装: 无法获取(WMI服务未开启)";
            }

            // 检测 WMI 服务是否可用
            if (!IsWmiAvailable())
            {
                txtPCINFO.Text = "          🔴 硬件检测失败 🔴\r\n" +
                                 "错误信息: WMI服务未开启或不可用\r\n\r\n" +
                                 "可能原因:\r\n" +
                                 "1. WMI服务被禁用或删除\r\n" +
                                 "      此处配置检测完全依赖系统WMI服务，请检查后使用\r\n" +
                                 "      或使用“本机配置检测(推荐)”无需WMI服务\r\n" +
                                 "2. 权限不足，以管理员运行试试\r\n" +
                                 "3. 被安全软件拦截\r\n" +
                                 "4. 系统问题";
                return;
            }


            // 异步加载硬件信息
            await Task.Run(() => LoadHardwareInfo());
        }

        /// <summary>
        /// 加载所有硬件信息并格式化显示
        /// </summary>
        private void LoadHardwareInfo()
        {
            try
            {
                StringBuilder sb = new StringBuilder();

                // === 1. 系统信息 ===
                {
                    var (caption, build, bitness, osType) = GetSystemInfo();
                    sb.AppendLine($"系统:\t{caption} [{build}/{bitness}/{osType}]");
                }

                // === 2. CPU信息 ===
                {
                    var cpus = GetCPUInfo();
                    if (cpus.Count == 1)
                    {
                        var cpu = cpus[0];
                        sb.AppendLine($"CPU:\t{cpu.name} [{cpu.cores}C/{cpu.threads}T]");
                    }
                    else if (cpus.Count > 1)
                    {
                        for (int i = 0; i < cpus.Count; i++)
                        {
                            if (i == 0)
                                sb.AppendLine($"CPU:\t[{i + 1}] {cpus[i].name} [{cpus[i].cores}C/{cpus[i].threads}T]");
                            else
                                sb.AppendLine($"\t[{i + 1}] {cpus[i].name} [{cpus[i].cores}C/{cpus[i].threads}T]");
                        }
                    }
                    else
                    {
                        sb.AppendLine("CPU:\t获取失败");
                    }
                }

                // === 3. 主板信息 ===
                {
                    var (boardModel, boardBrand, biosVersion) = GetMainboardInfo();
                    sb.AppendLine($"主板:\t{boardModel} [{boardBrand}/{biosVersion}]");
                }

                // === 4. 内存信息 ===
                {
                    var (totalGB, details) = GetMemoryInfo();
                    if (details.Count > 0 && totalGB > 0)
                    {
                        var parts = new List<string>();
                        foreach (var d in details)
                            parts.Add($"{d.capacity}-{d.speed}MHz/{d.brand}");

                        sb.AppendLine($"内存:\t{totalGB}GB\t[{string.Join(" | ", parts)}]");
                    }
                    else
                    {
                        sb.AppendLine("内存:\t获取失败");
                    }
                }

                // === 5. 显卡信息 ===
                {
                    var gpus = GetGPUInfo();
                    if (gpus.Count == 1)
                    {
                        sb.AppendLine($"显卡:\t{gpus[0].name} [{gpus[0].memory}/{gpus[0].driver}]");
                    }
                    else if (gpus.Count > 1)
                    {
                        for (int i = 0; i < gpus.Count; i++)
                        {
                            if (i == 0)
                                sb.AppendLine($"显卡:\t[{i + 1}] {gpus[i].name} [{gpus[i].memory}/{gpus[i].driver}]");
                            else
                                sb.AppendLine($"\t[{i + 1}] {gpus[i].name} [{gpus[i].memory}/{gpus[i].driver}]");
                        }
                    }
                    else
                    {
                        sb.AppendLine("显卡:\t未检测到");
                    }
                }

                // === 6. 屏幕信息 ===
                {
                    var monitors = GetMonitorInfo();
                    if (monitors.Count == 1)
                    {
                        sb.AppendLine($"屏幕:\t{monitors[0].name} [{monitors[0].mfrAndId}/{monitors[0].size}/{monitors[0].date}]");
                    }
                    else if (monitors.Count > 1)
                    {
                        for (int i = 0; i < monitors.Count; i++)
                        {
                            if (i == 0)
                                sb.AppendLine($"屏幕:\t[{i + 1}] {monitors[i].name} [{monitors[i].mfrAndId}/{monitors[i].size}/{monitors[i].date}]");
                            else
                                sb.AppendLine($"\t[{i + 1}] {monitors[i].name} [{monitors[i].mfrAndId}/{monitors[i].size}/{monitors[i].date}]");
                        }
                    }
                    else
                    {
                        sb.AppendLine("屏幕:\t未检测到");
                    }
                }

                // === 7. 硬盘信息 ===
                {
                    var disks = GetDiskInfo();
                    if (disks.Count == 1)
                    {
                        sb.AppendLine($"硬盘:\t{disks[0].model} / {disks[0].size}");
                    }
                    else if (disks.Count > 1)
                    {
                        for (int i = 0; i < disks.Count; i++)
                        {
                            if (i == 0)
                                sb.AppendLine($"硬盘:\t[{i + 1}] {disks[i].model} / {disks[i].size}");
                            else
                                sb.AppendLine($"\t[{i + 1}] {disks[i].model} / {disks[i].size}");
                        }
                    }
                    else
                    {
                        sb.AppendLine("硬盘:\t未检测到");
                    }
                }

                // === 8. 网卡信息 ===
                {
                    var nics = GetNetworkAdapterInfo();
                    if (nics.Count == 1)
                    {
                        sb.AppendLine($"网卡:\t{nics[0].name}");
                        sb.AppendLine($"\t        [{nics[0].type} / {nics[0].mac} / {nics[0].speed}]");
                    }
                    else if (nics.Count > 1)
                    {
                        for (int i = 0; i < nics.Count; i++)
                        {
                            if (i == 0)
                                sb.AppendLine($"网卡:\t[{i + 1}] {nics[i].name}");
                            else
                                sb.AppendLine($"\t[{i + 1}] {nics[i].name}");
                            sb.AppendLine($"\t        [{nics[i].type} / {nics[i].mac} / {nics[i].speed}]");
                        }
                    }
                    else
                    {
                        sb.AppendLine("网卡:\t未检测到");
                    }
                }

                // === 9. 声卡信息 ===
                {
                    var soundcards = GetSoundCardInfo();
                    if (soundcards.Count == 1)
                    {
                        sb.AppendLine($"声卡:\t{soundcards[0]}");
                    }
                    else if (soundcards.Count > 1)
                    {
                        for (int i = 0; i < soundcards.Count; i++)
                        {
                            if (i == 0)
                                sb.AppendLine($"声卡:\t[{i + 1}] {soundcards[i]}");
                            else
                                sb.AppendLine($"\t[{i + 1}] {soundcards[i]}");
                        }
                    }
                    else
                    {
                        sb.AppendLine("声卡:\t未检测到");
                    }
                }

                // 更新UI
                this.Invoke((Action)(() =>
                {
                    txtPCINFO.Text = sb.ToString();
                }));
            }
            catch (Exception ex)
            {
                string errorMsg = "🔴 硬件检测失败 🔴\r\n" +
                                  $"错误信息: {ex.Message}\r\n\r\n" +
                                  "可能原因:\r\n" +
                                  "1. WMI服务被禁用\r\n" +
                                  "2. 权限不足\r\n" +
                                  "3. 系统组件损坏";
                this.Invoke((Action)(() =>
                {
                    txtPCINFO.Text = errorMsg;
                }));
            }
        }

        private void UpdateUptimeDisplay()
        {
            long ms = Environment.TickCount;
            TimeSpan up = TimeSpan.FromMilliseconds(ms);
            DateTime bootTime = DateTime.Now - up;

            var parts = new List<string>();
            if (up.Days > 0) parts.Add($"{up.Days}天");
            if (up.Hours > 0) parts.Add($"{up.Hours}时");
            if (up.Minutes > 0) parts.Add($"{up.Minutes}分");
            parts.Add($"{up.Seconds}秒");

            string uptimeStr = string.Join("", parts);
            lblSysUpTime.Text = $"系统开机: {bootTime:yyyy-MM-dd HH:mm:ss} (开机{uptimeStr})";
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            UpdateUptimeDisplay();
        }
    }
}
