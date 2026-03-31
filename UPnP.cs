using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Open.Nat; // 梦酱的老朋友

namespace NetInfoCheckerX
{
    public partial class UPnP : Form
    {
        private int _refreshCountdown = 300;
        private NatDevice _device; // 缓存发现的路由器设备
        private readonly string[] requiredFiles;
        // 在类成员变量里定义一个限速器
        private SemaphoreSlim _scanSemaphore = new SemaphoreSlim(100);
        // 用于删除确认的逻辑变量
        private bool _isConfirmingDelete = false;
        private DateTime _lastDelClickTime;

        // 导入系统底层 API 来读写 INI
        [System.Runtime.InteropServices.DllImport("kernel32")]
        private static extern long WritePrivateProfileString(string section, string key, string val, string filePath);
        [System.Runtime.InteropServices.DllImport("kernel32")]
        private static extern int GetPrivateProfileString(string section, string key, string def, System.Text.StringBuilder retVal, int size, string filePath);

        private string _iniPath = Path.Combine(Application.StartupPath, "NetInfoCheckerX.ini");

        // 简单的读取 Helper
        private string ReadConfig(string section, string key)
        {
            var temp = new System.Text.StringBuilder(255);
            GetPrivateProfileString(section, key, "", temp, 255, _iniPath);
            return temp.ToString();
        }

        public UPnP()
        {
            InitializeComponent();
            // 定义需要检查的文件列表
            requiredFiles = new string[]
            {
            "Open.Nat.dll",
            };
        }

        #region 核心逻辑：获取与筛选设备

        private async Task<NatDevice> GetNatDevice()
        {
            if (_device != null) return _device;
            NatDiscoverer discoverer = new NatDiscoverer();

            // 1. 获取基础信息
            string selectedIpStr = "";
            this.Invoke(new Action(() => { selectedIpStr = comboNIC.Text; }));
            if (string.IsNullOrEmpty(selectedIpStr)) return null;
            IPAddress selectedIp = IPAddress.Parse(selectedIpStr);
            IPAddress gatewayIp = GetGatewayIp(selectedIp, selectedIpStr);

            // --- ✨ 第一阶段：【赛跑模式】广播与常用端口同时出发 ---
            var quickStageCts = new CancellationTokenSource();

            // 任务 A：广播搜索
            var broadcastTask = Task.Run(async () =>
            {
                try
                {
                    var devices = await discoverer.DiscoverDevicesAsync(PortMapper.Upnp, new CancellationTokenSource(1500));
                    var found = devices.FirstOrDefault();
                    if (found != null) quickStageCts.Cancel();
                    return found;
                }
                catch { return null; }
            }, quickStageCts.Token);

            // 任务 B：常用端口探测
            var commonPortsTask = Task.Run(async () =>
            {
                int[] commonPorts = { 1900, 2189, 2869, 5000, 5800, 5431, 80, 8080 };
                foreach (int port in commonPorts)
                {
                    if (quickStageCts.Token.IsCancellationRequested) break;
                    if (await QuickCheckPort(gatewayIp, port))
                    {
                        try
                        {
                            var d = await discoverer.DiscoverDeviceAsync(PortMapper.Upnp, new CancellationTokenSource(500));
                            if (d != null) { quickStageCts.Cancel(); return d; }
                        }
                        catch { }
                    }
                }
                return null;
            }, quickStageCts.Token);

            // 等待快速阶段的结果
            var winner = await Task.WhenAny(broadcastTask, commonPortsTask);
            _device = await winner;
            if (_device == null) _device = (winner == broadcastTask) ? await commonPortsTask : await broadcastTask;

            // --- ✨ 第二阶段：【深度模式】全量异步扫描兜底 ---
            if (_device == null)
            {
                bool shouldFullScan = CheckFullScanConfig();
                if (shouldFullScan)
                {
                    _device = await RunFullScanLogic(discoverer, gatewayIp);
                }
            }

            this.Invoke(new Action(() => { btnRefresh.Text = "刷新列表"; }));
            return _device;
        }
        private async Task<NatDevice> RunFullScanLogic(NatDiscoverer discoverer, IPAddress gatewayIp)
        {
            var masterCts = new CancellationTokenSource();
            return await Task.Run(async () =>
            {
                using (var semaphore = new SemaphoreSlim(500)) // 并发数控制
                {
                    var scanTasks = new List<Task<NatDevice>>();
                    for (int port = 1024; port <= 65535; port++)
                    {
                        if (masterCts.IsCancellationRequested) break;
                        int targetPort = port;
                        await semaphore.WaitAsync();

                        var t = Task.Run(async () =>
                        {
                            try
                            {
                                if (masterCts.IsCancellationRequested) return null;
                                if (await QuickCheckPort(gatewayIp, targetPort))
                                {
                                    var d = await discoverer.DiscoverDeviceAsync(PortMapper.Upnp, new CancellationTokenSource(500));
                                    if (d != null) { masterCts.Cancel(); return d; }
                                }
                            }
                            catch { }
                            finally
                            {
                                semaphore.Release();
                                if (targetPort % 100 == 0) // 进度汇报
                                    this.BeginInvoke(new Action(() => { btnRefresh.Text = $"{targetPort}"; }));
                            }
                            return null;
                        });
                        scanTasks.Add(t);

                        if (scanTasks.Count > 1000) // 内存保护
                        {
                            var found = scanTasks.FirstOrDefault(st => st.IsCompleted && st.Result != null);
                            if (found != null) return found.Result;
                            scanTasks.RemoveAll(st => st.IsCompleted);
                        }
                    }
                    var results = await Task.WhenAll(scanTasks);
                    return results.FirstOrDefault(d => d != null);
                }
            });
        }
        // ✨ 新增辅助方法：极速探测端口是否开放 (不占用 CPU)
        private async Task<bool> QuickCheckPort(IPAddress ip, int port)
        {
            try
            {
                using (var client = new System.Net.Sockets.TcpClient())
                {
                    var task = client.ConnectAsync(ip, port);
                    if (await Task.WhenAny(task, Task.Delay(80)) == task && client.Connected) return true;
                }
            }
            catch { }
            return false;
        }
        /// <summary>
        /// 夢酱的寻路小雷达：根据选中的本机IP，找到对应的网关地址
        /// </summary>
        private IPAddress GetGatewayIp(IPAddress selectedIp, string selectedIpStr)
        {
            try
            {
                // 遍历电脑上所有的网卡
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    var properties = adapter.GetIPProperties();
                    // 找到包含我们选中 IP 的那张网卡
                    if (properties.UnicastAddresses.Any(ua => ua.Address.Equals(selectedIp)))
                    {
                        // 从这张网卡里找默认网关
                        var gateway = properties.GatewayAddresses
                            .Select(g => g.Address)
                            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

                        if (gateway != null) return gateway;
                    }
                }
            }
            catch { /* 哎呀，没找到 */ }

            // 如果实在找不到，就尝试假设网关是 .1 (这只是个保底方案喵)
            string[] parts = selectedIpStr.Split('.');
            if (parts.Length == 4)
            {
                return IPAddress.Parse($"{parts[0]}.{parts[1]}.{parts[2]}.1");
            }

            return null;
        }

        /// <summary>
        /// 夢酱的决策中心：处理全量扫描的询问和记忆逻辑
        /// </summary>
        private bool CheckFullScanConfig()
        {
            // 1. 先看看 INI 记录里有没有“记忆”过（读取本地记录）
            string savedChoice = ReadConfig("UPnP", "RememberFullScan"); // "Yes" 代表默认开启，"No" 代表默认关闭

            if (savedChoice == "Yes") return true;
            if (savedChoice == "No") return false;

            // 2. 如果没有记忆，开始【弹窗1】：询问本次是否开启全量扫描
            var result = MessageBox.Show(
                "正常扫描未找到UPnP设备，是否尝试全量扫描？\n将依次扫描当前选择网关的所有端口，尝试发现非标准设备",
                "扫描失败了",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);

            bool userWantsScan = (result == DialogResult.Yes);

            // 3. 【弹窗2】：根据弹窗1的选择，动态生成询问文本，问夢酱要不要记住
            string actionText = userWantsScan ? "【直接进入】" : "【不进入】";
            string rememberMessage = $"下次是否默认{actionText}全量扫描，不再询问？\n需恢复请删除运行目录\\NetInfoCheckerX.ini\\[UPnP]节";

            var remember = MessageBox.Show(
                rememberMessage,
                "是否要记住",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);

            // 4. 如果夢酱在弹窗2选了“是”，就把刚才弹窗1的选择写进 INI
            if (remember == DialogResult.Yes)
            {
                // 记录夢酱的选择（Yes 或 No），下次进来就会在第 1 步被直接读取返回啦
                WritePrivateProfileString("UPnP", "RememberFullScan", userWantsScan ? "Yes" : "No", _iniPath);
            }

            return userWantsScan;
        }

        #endregion

        #region UI 交互与状态控制

        // 按钮与组件状态控制
        private void ToggleControls(bool isEnabled, Button activeBtn = null, bool exceptButtons = false)
        {
            foreach (Control c in this.Controls)
            {
                if (c is DataGridView) continue; // 表格不禁用

                // 如果处于解除禁用第一阶段，跳过按钮和输入组件
                if (exceptButtons && (c is Button || c is ComboBox || c is RadioButton || c is TextBox))
                {
                    c.Enabled = false;
                    continue;
                }
                c.Enabled = isEnabled;
            }
            if (activeBtn != null) activeBtn.Enabled = true; // 操作中的按钮保持可用以显示文字
        }

        // 统一的异步刷新逻辑（不再通过 PerformClick 触发，避免卡顿）
        private async Task RefreshListInternal()
        {
            dataGridView1.Rows.Clear();
            try
            {
                var device = await GetNatDevice();
                if (device == null)
                {
                    // 情况1：根本没搜到支持UPnP的路由器
                    dataGridView1.Rows.Add(null, "未发现UPnP设备", "请检查路由器", "是否开启UPnP", "或相关服务是否正常", "喵", "");
                    timer1.Stop();
                    return;
                }

                var mappings = await device.GetAllMappingsAsync();
                var sortedList = mappings.OrderBy(x => x.PrivateIP.ToString())
                                         .ThenBy(x => x.PrivatePort)
                                         .ToList();

                // --- 核心优化点：检查列表是否为空 ---
                if (sortedList.Count == 0)
                {
                    // 情况2：搜到了路由器，但里面一条映射规则都没有
                    dataGridView1.Rows.Add(null, "当前没有任何映射规则", null, null, "喵", null, "");
                    return;
                }

                // 找到 RefreshListInternal 方法里的这个循环
                for (int i = 0; i < sortedList.Count; i++)
                {
                    var m = sortedList[i];

                    // 将 m.Expiration 转换为本地时间 .ToLocalTime()
                    string expireDisplay = m.Expiration.Year < 2000 ? "永久" : m.Expiration.ToLocalTime().ToString();

                    dataGridView1.Rows.Add(
                        i + 1,
                        m.PrivateIP,
                        m.PrivatePort,
                        m.PublicPort,
                        m.Protocol.ToString().ToUpper(),
                        expireDisplay, // 使用转换后的本地时间显示
                        m.Description
                    );
                }
            }
            catch (Exception ex)
            {
                dataGridView1.Rows.Add(null, "读取异常：", ex.Message, null, "喵", null, "");
            }
        }

        #endregion

        #region 按钮事件

        private async void btnRefresh_Click(object sender, EventArgs e)
        {
            // ✨ 任务 2：防止重复点击
            try
            {
                btnRefresh.Enabled = false; // 变灰，保护夢酱的程序不被打扰
                timer1.Stop();
                btnRefresh.Text = "刷新中...";
                ToggleControls(false);

                await RefreshListInternal();

                btnRefresh.Text = "刷新完毕";
                ToggleControls(true, exceptButtons: true);
                ToggleControls(true);
                btnRefresh.Text = "刷新列表";
                ResetTimer();
                timer1.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show("刷新时出错：" + ex.Message, "刷新失败了", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            }
            finally
            {
                // 扫完后记得恢复原样
                btnRefresh.Enabled = true;
                btnRefresh.Text = "刷新列表";
            }
        }

        // 创建按钮
        private async void btnCreate_Click(object sender, EventArgs e)
        {
            // 梦酱要求的描述规范检查
            string pattern = @"^[a-zA-Z0-9\x20-\x7e]+$";
            if (string.IsNullOrWhiteSpace(txtName.Text) || !System.Text.RegularExpressions.Regex.IsMatch(txtName.Text, pattern))
            {
                MessageBox.Show("描述只能用英文、数字和英文符号", "描述太坏了", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string originalText = btnCreate.Text;
            try
            {
                btnCreate.Text = "创建中";
                ToggleControls(false, btnCreate);

                var device = await GetNatDevice();
                if (device != null)
                {
                    Mapping newMap = new Mapping(
                        radioTCP.Checked ? Protocol.Tcp : Protocol.Udp,
                        IPAddress.Parse(comboCilentIP.Text),
                        int.Parse(txtClientPort.Text),
                        int.Parse(txtPublicPort.Text),
                        GetLifetimeInSeconds(comboTime.Text),
                        txtName.Text
                    );
                    await device.CreatePortMapAsync(newMap);
                    btnCreate.Text = "创建完毕";
                    await RefreshListInternal();
                }
            }
            catch (Exception ex)
            {
                string msg = ex.Message.Contains("606") ? "创建失败：Error 606: Action not authorized\n\n提示：检查路由器安全模式 (仅允许为发起请求的IP添加端口映射)，\n    确认欲映射的端口被路由器允许(大多默认拒绝映射1-1023端口)" : "创建失败：" + ex.Message;
                MessageBox.Show(msg, "创建失败了", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            }
            finally
            {
                ToggleControls(true, exceptButtons: true);
                ToggleControls(true);
                btnCreate.Text = originalText;
            }
        }

        // 删除选中行逻辑
        private async void btnDel_Click(object sender, EventArgs e)
        {
            // 1. 基础检查：是否有选中行
            if (dataGridView1.SelectedRows.Count == 0)
            {
                MessageBox.Show("请先在列表中点击选中想要删除的行喵~", "提示");
                return;
            }

            if (btnDel.Text == "删除中") return;

            // --- ✨ 核心进化：带自动恢复的确认逻辑 ---
            // 如果还没进入确认状态，或者是上一次点击已经超过 2 秒了
            if (!_isConfirmingDelete || (DateTime.Now - _lastDelClickTime).TotalSeconds > 2)
            {
                _isConfirmingDelete = true;
                _lastDelClickTime = DateTime.Now;
                btnDel.Text = "确认删除";
                btnDel.ForeColor = Color.Yellow;

                // 启动一个“后台小助手”，等 2 秒钟
                await Task.Run(async () =>
                {
                    await Task.Delay(2000); // 睡 2 秒

                    // 2 秒后，如果状态还是确认中，说明梦酱没点第二次
                    if (_isConfirmingDelete)
                    {
                        // 回到 UI 线程把按钮变回来
                        this.BeginInvoke(new Action(() =>
                        {
                            // 再次检查时间，防止在极端情况下刚好点下时被重置
                            if ((DateTime.Now - _lastDelClickTime).TotalSeconds >= 2)
                            {
                                _isConfirmingDelete = false;
                                btnDel.Text = "删除映射";
                                btnDel.ForeColor = Color.White;
                            }
                        }));
                    }
                });
                return; // 第一次点击到此为止
            }

            // --- 如果跑到了这里，说明是在 2 秒内点的第二次！ ---
            _isConfirmingDelete = false; // 立刻关闭确认状态，防止自动恢复逻辑干扰

            try
            {
                btnDel.Text = "删除中";
                btnDel.ForeColor = Color.White;
                ToggleControls(false, btnDel);

                // ... (接下来的删除逻辑保持不变)
                var device = await GetNatDevice();
                if (device == null) return;
                // 获取并排序映射列表，确保序号一致
                var mappings = await device.GetAllMappingsAsync();
                var sortedList = mappings.OrderBy(x => x.PrivateIP.ToString()).ThenBy(x => x.PrivatePort).ToList();

                int successCount = 0;
                // 遍历所有选中的行进行删除
                foreach (DataGridViewRow row in dataGridView1.SelectedRows)
                {
                    // 通过第一列的序号找到对应的 Mapping
                    if (row.Cells[0].Value != null && int.TryParse(row.Cells[0].Value.ToString(), out int seq))
                    {
                        if (seq > 0 && seq <= sortedList.Count)
                        {
                            try
                            {
                                await device.DeletePortMapAsync(sortedList[seq - 1]);
                                successCount++;
                            }
                            catch { /* 单条失败不中断 */ }
                        }
                    }
                    btnDel.Text = $"已删{successCount}条";
                }
                await Task.Delay(1000);
                await RefreshListInternal(); // 刷新列表
            }
            catch (Exception ex)
            {
                MessageBox.Show("删除出错：" + ex.Message);
            }
            finally
            {
                ToggleControls(true);
                btnDel.Text = "删除映射"; // 彻底完成后恢复文字
            }
        }

        // 全量删除逻辑
        private async void btnDelAll_Click(object sender, EventArgs e)
        {
            var result = MessageBox.Show("删除当前所有映射，点击确定", "警告", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);

            if (result != DialogResult.OK)
            {
                SystemSounds.Beep.Play(); // 用户取消或关闭窗口，发出提示音
                return;
            }

            try
            {
                btnDelAll.Enabled = false;
                btnDelAll.Text = "删除中";

                var device = await GetNatDevice();
                if (device == null) return;

                var mappings = await device.GetAllMappingsAsync();
                int total = mappings.Count();
                int success = 0;
                int fail = 0;

                foreach (var m in mappings)
                {
                    try
                    {
                        await device.DeletePortMapAsync(m);
                        success++;
                    }
                    catch { fail++; }
                }

                MessageBox.Show($"本次删除映射 {total} 条，成功 {success} 条，失败 {fail} 条", "清理完毕", MessageBoxButtons.OK, MessageBoxIcon.Information);
                await RefreshListInternal();
            }
            finally
            {
                btnDelAll.Enabled = true;
                btnDelAll.Text = "删除全部";
            }
        }

        #endregion

        #region 辅助工具与初始化

        private void comboNIC_SelectedIndexChanged(object sender, EventArgs e)
        {
            _device = null; // 换了网卡，必须清空缓存重新搜寻路由器
        }

        private void UPnP_Load(object sender, EventArgs e)
        {
            try
            {
                // 获取程序运行目录
                string appPath = Application.StartupPath;

                // 检查所有必需文件
                List<string> missingFiles = new List<string>();

                foreach (string file in requiredFiles)
                {
                    string filePath = Path.Combine(appPath, file);
                    if (!File.Exists(filePath))
                    {
                        missingFiles.Add(file);
                    }
                }

                // 如果有缺失文件，显示提示并关闭窗口
                if (missingFiles.Count > 0)
                {
                    string message = $"缺少运行UPNP中控台必要的文件：\n{string.Join("\n", missingFiles)}\n\n建议重新打开/解压查询器X/检查杀毒软件喵。";

                    MessageBox.Show(message, "文件缺失了", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    this.Close();
                    return;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"检查文件时出错：{ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                this.Close();
                return;
            }
            InitIpLists();
            ResetTimer();
            comboTime.SelectedIndex = 0;
            dataGridView1.Rows.Add("-", "请【刷新网卡】后，", "选择欲查看UPnP映射的网卡，", "再点击【获取映射】", "喵", "-", "-");
        }

        private void InitIpLists()
        {
            comboNIC.Items.Clear();
            comboCilentIP.Items.Clear();
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.OperationalStatus == OperationalStatus.Up);

            foreach (var ni in interfaces)
            {
                foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip.Address))
                    {
                        string ipStr = ip.Address.ToString();
                        comboNIC.Items.Add(ipStr);
                        comboCilentIP.Items.Add(ipStr);
                    }
                }
            }
            if (comboNIC.Items.Count > 0) { comboNIC.SelectedIndex = 0; comboCilentIP.SelectedIndex = 0; }
        }

        private async void timer1_Tick(object sender, EventArgs e)
        {
            _refreshCountdown--;

            // 更新窗口標題顯示倒計時
            this.Text = $"UPnP控制台 ✧ NetInfoCheckerX | 自动刷新倒计时: {_refreshCountdown}秒";

            if (_refreshCountdown <= 0)
            {
                // 1. 數到0了，先停下計時器，避免刷新期間還在倒計時
                timer1.Stop();

                // 2. 觸發刷新邏輯
                // 這裡建議直接調用 btnRefresh_Click 的邏輯，或者確保 RefreshListInternal 被正確異步執行
                await RefreshListInternal();

                // 3. 刷新完畢後，把倒計時撥回 300
                ResetTimer();

                // 4. 重新啟動計時器，開始新一輪的等待
                timer1.Start();
            }
        }

        private void ResetTimer() => _refreshCountdown = 300;

        private int GetLifetimeInSeconds(string s)
        {
            // 梦酱看这里：先检查是不是传了 "0" 或者为空
            if (string.IsNullOrEmpty(s) || s == "0")
            {
                return 0; // 0 在 UPnP 协议里代表“永久”
            }

            try
            {
                // 原来的逻辑：切掉最后一个字符（单位），剩下的转成数字
                int v = int.Parse(s.Substring(0, s.Length - 1));

                if (s.EndsWith("m")) return v * 60;
                if (s.EndsWith("h")) return v * 3600;
                if (s.EndsWith("d")) return v * 86400;

                return v; // 如果没有匹配单位，就返回原值
            }
            catch
            {
                return 0; // 万一梦酱填错了，默认给个 0 保证程序不崩溃
            }
        }
        private void btnRefreshNIC_Click(object sender, EventArgs e) => InitIpLists();

        #endregion

        private void txtClientPort_KeyPress(object sender, KeyPressEventArgs e)
        {
            // 只允許數字 (0-9) 和 退格鍵 (Control鍵)
            if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar))
            {
                e.Handled = true; // 設置為已處理，這樣字符就不會顯示在文本框裡喵
            }
        }

        private void txtPublicPort_KeyPress(object sender, KeyPressEventArgs e)
        {
            // 只允許數字 (0-9) 和 退格鍵 (Control鍵)
            if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar))
            {
                e.Handled = true; // 設置為已處理，這樣字符就不會顯示在文本框裡喵
            }
        }

        private void txtName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true; // 阻止系统默认处理

                // 调用按钮的点击事件
                btnCreate.PerformClick();
            }
        }

        private void txtPublicPort_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true; // 阻止系统默认处理

                // 调用按钮的点击事件
                btnCreate.PerformClick();
            }
        }

        private void txtClientPort_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true; // 阻止系统默认处理

                // 调用按钮的点击事件
                btnCreate.PerformClick();
            }
        }

    }
}