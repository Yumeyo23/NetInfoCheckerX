using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DnsClient;

//DNS劫持测试

namespace NetInfoCheckerX
{
    public partial class DNSTest : Form
    {
        private readonly string[] requiredFiles;

        //  这里定义一个取消令牌 用来在窗口关闭时强制结束测试
        private CancellationTokenSource _cts = new CancellationTokenSource();

        /// <summary>
        /// 核心查询方法
        /// </summary>
        /// <param name="dnsServer">DNS服务器IP 如果传null则使用系统DNS</param>
        /// <param name="domain">要查询的域名</param>
        /// <param name="targetLabel">显示结果的Label</param>
        private async Task PerformDnsTest(string dnsServer, string domain, Label targetLabel)
        {
            try
            {
                LookupClient client;
                if (string.IsNullOrEmpty(dnsServer))
                {
                    // 使用系统默认DNS
                    client = new LookupClient();
                }
                else
                {
                    // 使用指定的DNS服务器
                    client = new LookupClient(IPAddress.Parse(dnsServer));
                }

                // 开始异步查询 传入 _cts.Token 这样窗口关闭时能立即停止
                var result = await client.QueryAsync(domain, QueryType.A, cancellationToken: _cts.Token);

                if (result.HasError)
                {
                    targetLabel.Invoke(new Action(() => targetLabel.Text = $"解析失败：{result.ErrorMessage}"));
                    return;
                }

                //只显示前2个IPv4地址
                var ips = result.Answers.ARecords()
                    .Select(a => a.Address.ToString())
                    .Take(2)
                    .ToList();

                string displayText = ips.Count > 0 ? string.Join(", ", ips) : "未找到IPv4记录";

                // 回到主线程更新UI
                targetLabel.Invoke(new Action(() => targetLabel.Text = displayText));
            }
            catch (OperationCanceledException)
            {
                // 这是正常取消 不需要报错
            }
            catch (Exception ex)
            {
                targetLabel.Invoke(new Action(() => targetLabel.Text = $"解析失败(?)\n {ex.Message}"));
            }
        }

        public DNSTest()
        {
            // 定义需要检查的文件列表
            requiredFiles = new string[]
            {
            "System.Buffers.dll",
            "DnsClient.dll",
            };
            InitializeComponent();
        }
        private async Task ApplyDNSTestThemeAsync()
        {
            await Task.Yield();

            bool isLight = Global.isThemelight;
            Color contrastColor = isLight ? Color.Black : Color.White;
            Color yumeyoColor = isLight ? ColorTranslator.FromHtml("#8e8cd8") : ColorTranslator.FromHtml("#a8a5ff");

            // 1. 窗口整体背景颜色
            this.BackColor = isLight ? Global.themeLight : Global.themeBlack;

            // 2. 分类标题标签组
            Control[] titleLabels = {
        lblSystem, lbl223, lbl114, lblGoogle, lblMS, lblWrong,
        lblBaidu, lblQQ
    };
            foreach (var l in titleLabels)
            {
                if (l != null) l.ForeColor = yumeyoColor;
            }

            // 3. 测试结果数值标签组 (黑白对比色)
            Control[] resultLabels = {
        lblSysBaidu, lblSysQQ, lbl223Baidu, lbl223QQ,
        lbl114Baidu, lbl114QQ, lblGoogleBaidu, lblGoogleQQ,
        lblWrongBaidu, lblWrongQQ, lblMSBaidu, lblMSQQ
    };
            foreach (var r in resultLabels)
            {
                if (r != null) r.ForeColor = contrastColor;
            }

            // 4. 图片框处理
            if (pictureBox1 != null)
            {
                pictureBox1.BackColor = Color.Transparent;
            }
        }
        private void DNSTest_Load(object sender, EventArgs e)
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

                // 如果有缺失文件 显示提示并关闭窗口
                if (missingFiles.Count > 0)
                {
                    string message = $"缺少运行DNS劫持测试必要的文件：\n{string.Join("\n", missingFiles)}\n\n建议重新打开/解压查询器X/检查杀毒软件喵。";

                    MessageBox.Show(message, "文件缺失了",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            _ = ApplyDNSTestThemeAsync();
            string NowTime = Others.GetCurrentTime();
            lblVersion.Text = Global.exeName + " " + Global.Version + " | " + NowTime;

            //  我们要同时开始12个任务 互不干扰
            Task.Run(() =>
            {
                // 系统DNS
                _ = PerformDnsTest(null, "www.baidu.com", lblSysBaidu);
                _ = PerformDnsTest(null, "www.taobao.com", lblSysQQ);

                // 阿里 DNS (223.5.5.5)
                _ = PerformDnsTest("223.5.5.5", "www.baidu.com", lbl223Baidu);
                _ = PerformDnsTest("223.5.5.5", "www.taobao.com", lbl223QQ);

                // 114 DNS
                _ = PerformDnsTest("114.114.114.114", "www.baidu.com", lbl114Baidu);
                _ = PerformDnsTest("114.114.114.114", "www.taobao.com", lbl114QQ);

                // Google DNS
                _ = PerformDnsTest("8.8.8.8", "www.baidu.com", lblGoogleBaidu);
                _ = PerformDnsTest("8.8.8.8", "www.taobao.com", lblGoogleQQ);

                // Microsoft/Level3 DNS
                _ = PerformDnsTest("4.2.2.1", "www.baidu.com", lblMSBaidu);
                _ = PerformDnsTest("4.2.2.1", "www.taobao.com", lblMSQQ);

                // 错误 DNS (模拟失败)
                _ = PerformDnsTest("73.55.60.8", "www.baidu.com", lblWrongBaidu);
                _ = PerformDnsTest("73.55.60.8", "www.taobao.com", lblWrongQQ);
            });
        }

        private void DNSTest_FormClosing(object sender, FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            _cts.Cancel();
            _cts.Dispose();
        }

        private void lblSysBaidu_TextChanged(object sender, EventArgs e)
        {
            toolTip1.SetToolTip(lblSysBaidu, lblSysBaidu.Text);
        }

        private void lblSysQQ_TextChanged(object sender, EventArgs e)
        {
            toolTip1.SetToolTip(lblSysQQ, lblSysQQ.Text);
        }

        private void lbl223Baidu_TextChanged(object sender, EventArgs e)
        {
            toolTip1.SetToolTip(lbl223Baidu, lbl223Baidu.Text);
        }

        private void lbl223QQ_TextChanged(object sender, EventArgs e)
        {
            toolTip1.SetToolTip(lbl223QQ, lbl223QQ.Text);
        }
        private void lbl114Baidu_TextChanged(object sender, EventArgs e)
        {
            toolTip1.SetToolTip(lbl114Baidu, lbl114Baidu.Text);
        }

        private void lbl114QQ_TextChanged(object sender, EventArgs e)
        {
            toolTip1.SetToolTip(lbl114QQ, lbl114QQ.Text);
        }

        private void lblGoogleBaidu_TextChanged(object sender, EventArgs e)
        {
            toolTip1.SetToolTip(lblGoogleBaidu, lblGoogleBaidu.Text);
        }

        private void lblGoogleQQ_TextChanged(object sender, EventArgs e)
        {
            toolTip1.SetToolTip(lblGoogleQQ, lblGoogleQQ.Text);
        }

        private void lblMSBaidu_TextChanged(object sender, EventArgs e)
        {
            toolTip1.SetToolTip(lblMSBaidu, lblMSBaidu.Text);
        }

        private void lblMSQQ_TextChanged(object sender, EventArgs e)
        {
            toolTip1.SetToolTip(lblMSQQ, lblMSQQ.Text);
        }

        private void lblWrongBaidu_TextChanged(object sender, EventArgs e)
        {
            toolTip1.SetToolTip(lblWrongBaidu, lblWrongBaidu.Text);
        }

        private void lblWrongQQ_TextChanged(object sender, EventArgs e)
        {
            toolTip1.SetToolTip(lblWrongQQ, lblWrongQQ.Text);
        }

        //            toolTip1.SetToolTip(lblBaidu, lblQQ.Text);
    }
}
