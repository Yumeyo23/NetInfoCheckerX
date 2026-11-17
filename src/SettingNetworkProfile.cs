using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NetInfoCheckerX
{
    public partial class SettingNetworkProfile : Form
    {
        private const string ProfilesRegistryPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles";
        private const string ProfilesPowerShellPath = @"Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles";
        private bool _isBusy;
        private bool _isConfirmingDelete;
        private DateTime _lastDeleteClickTime;
        private bool _isConfirmingDeleteAll;
        private DateTime _lastDeleteAllClickTime;

        private sealed class NetworkProfileInfo
        {
            public string KeyName { get; set; }
            public string Name { get; set; }
            public int Category { get; set; }
        }

        private enum ElevatedOperationResult
        {
            Succeeded,
            Cancelled,
            Failed
        }

        public SettingNetworkProfile()
        {
            InitializeComponent();
        }

        private async void SettingNetworkProfile_Load(object sender, EventArgs e)
        {
            ApplyNetworkProfileTheme();
            ApplyHighDpiGridMetrics();

            if (!IsAdministrator())
            {
                DialogResult result = MessageBox.Show(
                    "当前不是管理员，无法使用网络配置管理。\n是否立刻以管理员身份重启程序？\n【是】尝试提权重启程序\n【否】暂不重启",
                    "权限不够了", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button1);

                if (result == DialogResult.Yes && RestartApplicationAsAdministrator())
                {
                    return;
                }

                Close();
                return;
            }

            await RefreshProfilesWithStateAsync();
        }

        private static bool IsAdministrator()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool RestartApplicationAsAdministrator()
        {
            try
            {
                int currentProcessId = Process.GetCurrentProcess().Id;
                Process.Start(new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    Arguments = "--wait-for-process " + currentProcessId,
                    WorkingDirectory = Application.StartupPath,
                    UseShellExecute = true,
                    Verb = "runas"
                });

                Application.Exit();
                return true;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return false;
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法以管理员身份重启程序：\n" + ex.Message, "重启失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private void ApplyNetworkProfileTheme()
        {
            bool isLight = Global.isThemelight;
            Color foreground = isLight ? Global.colorBlack : Global.colorWhite;
            Color formBackground = isLight ? Global.themeLight : Global.themeBlack;
            Color controlBackground = isLight ? SystemColors.Window : Color.FromArgb(32, 32, 32);
            Color headerBackground = isLight ? SystemColors.Control : Color.FromArgb(45, 45, 48);
            Color alternateBackground = isLight ? Color.FromArgb(248, 248, 248) : Color.FromArgb(25, 25, 25);
            Color accent = isLight ? Global.Yumeyo : Global.Yumeyo2;
            Color accentText = GetReadableTextColor(accent);

            BackColor = formBackground;
            ForeColor = foreground;

            foreach (Button button in new[]
            {
                btnRefresh, btnRename, btnPublic, btnPrivate,
                btnDel, btnDelAll
            })
            {
                ApplyButtonTheme(button);
            }

            dataGridView1.EnableHeadersVisualStyles = false;
            dataGridView1.BackgroundColor = controlBackground;
            dataGridView1.GridColor = isLight ? Color.FromArgb(210, 210, 210) : Color.FromArgb(65, 65, 65);
            dataGridView1.ColumnHeadersDefaultCellStyle.BackColor = headerBackground;
            dataGridView1.ColumnHeadersDefaultCellStyle.ForeColor = foreground;
            dataGridView1.ColumnHeadersDefaultCellStyle.SelectionBackColor = headerBackground;
            dataGridView1.ColumnHeadersDefaultCellStyle.SelectionForeColor = foreground;
            dataGridView1.DefaultCellStyle.BackColor = controlBackground;
            dataGridView1.DefaultCellStyle.ForeColor = foreground;
            dataGridView1.DefaultCellStyle.SelectionBackColor = accent;
            dataGridView1.DefaultCellStyle.SelectionForeColor = accentText;
            dataGridView1.RowsDefaultCellStyle.BackColor = controlBackground;
            dataGridView1.RowsDefaultCellStyle.ForeColor = foreground;
            dataGridView1.RowsDefaultCellStyle.SelectionBackColor = accent;
            dataGridView1.RowsDefaultCellStyle.SelectionForeColor = accentText;
            dataGridView1.AlternatingRowsDefaultCellStyle.BackColor = alternateBackground;
            dataGridView1.AlternatingRowsDefaultCellStyle.ForeColor = foreground;
            dataGridView1.AlternatingRowsDefaultCellStyle.SelectionBackColor = accent;
            dataGridView1.AlternatingRowsDefaultCellStyle.SelectionForeColor = accentText;
        }

        private void ApplyHighDpiGridMetrics()
        {
            float dpiScale;
            using (Graphics graphics = CreateGraphics())
            {
                dpiScale = Math.Max(1F, graphics.DpiY / 96F);
            }

            int verticalPadding = Math.Max(1, (int)Math.Ceiling(1F * dpiScale));
            int rowHeight = Math.Max(
                (int)Math.Ceiling(23F * dpiScale),
                dataGridView1.Font.Height + verticalPadding * 2 + 2);
            int headerHeight = Math.Max(
                (int)Math.Ceiling(27F * dpiScale),
                dataGridView1.ColumnHeadersDefaultCellStyle.Font?.Height + verticalPadding * 2 + 4 ?? rowHeight);

            dataGridView1.DefaultCellStyle.Padding = new Padding(2, verticalPadding, 2, verticalPadding);
            dataGridView1.RowTemplate.MinimumHeight = rowHeight;
            dataGridView1.RowTemplate.Height = rowHeight;
            dataGridView1.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            dataGridView1.ColumnHeadersHeight = headerHeight;

            foreach (DataGridViewRow row in dataGridView1.Rows)
            {
                row.MinimumHeight = rowHeight;
                row.Height = rowHeight;
            }
        }

        private void ApplyButtonTheme(Button button)
        {
            bool isLight = Global.isThemelight;
            if (isLight)
            {
                button.ForeColor = SystemColors.ControlText;
                button.BackColor = SystemColors.Control;
                button.FlatStyle = FlatStyle.Standard;
                button.UseVisualStyleBackColor = true;
            }
            else
            {
                button.ForeColor = Global.colorWhite;
                button.UseVisualStyleBackColor = false;
                button.FlatStyle = FlatStyle.Flat;
                button.BackColor = Color.FromArgb(60, 60, 60);
                button.FlatAppearance.BorderColor = Color.FromArgb(120, 120, 120);
                button.FlatAppearance.MouseOverBackColor = Global.Yumeyo2;
            }
        }

        private static Color GetReadableTextColor(Color background)
        {
            int brightness = (background.R * 299 + background.G * 587 + background.B * 114) / 1000;
            return brightness >= 150 ? Color.Black : Color.White;
        }

        private async Task RefreshProfilesWithStateAsync()
        {
            if (_isBusy) return;

            _isBusy = true;
            SetControlsEnabled(false);
            string originalText = btnRefresh.Text;
            btnRefresh.Text = "刷新中...";
            try
            {
                await RefreshProfilesAsync();
            }
            finally
            {
                btnRefresh.Text = originalText;
                SetControlsEnabled(true);
                _isBusy = false;
            }
        }

        private async Task RefreshProfilesAsync()
        {
            List<NetworkProfileInfo> profiles;
            try
            {
                profiles = await Task.Run(() => ReadNetworkProfiles());
            }
            catch (Exception ex)
            {
                dataGridView1.Rows.Clear();
                MessageBox.Show("读取网络配置文件失败：\n" + ex.Message, "读取失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            dataGridView1.Rows.Clear();
            for (int i = 0; i < profiles.Count; i++)
            {
                NetworkProfileInfo profile = profiles[i];
                int rowIndex = dataGridView1.Rows.Add(
                    i + 1,
                    profile.Name,
                    GetCategoryText(profile.Category));
                dataGridView1.Rows[rowIndex].Tag = profile;
            }

            if (dataGridView1.Rows.Count > 0)
            {
                dataGridView1.Rows[0].Selected = true;
                dataGridView1.CurrentCell = dataGridView1.Rows[0].Cells[colProfileName.Index];
            }
        }

        private static List<NetworkProfileInfo> ReadNetworkProfiles()
        {
            List<NetworkProfileInfo> profiles = new List<NetworkProfileInfo>();
            RegistryView view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32;

            using (RegistryKey localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
            using (RegistryKey profilesKey = localMachine.OpenSubKey(ProfilesRegistryPath, false))
            {
                if (profilesKey == null) return profiles;

                foreach (string keyName in profilesKey.GetSubKeyNames())
                {
                    using (RegistryKey profileKey = profilesKey.OpenSubKey(keyName, false))
                    {
                        if (profileKey == null) continue;

                        string profileName = Convert.ToString(profileKey.GetValue("ProfileName"));
                        int category;
                        try { category = Convert.ToInt32(profileKey.GetValue("Category", 0)); }
                        catch { category = -1; }

                        string displayName = string.IsNullOrWhiteSpace(profileName) ? "（未命名配置）" : profileName;

                        profiles.Add(new NetworkProfileInfo
                        {
                            KeyName = keyName,
                            Name = displayName,
                            Category = category
                        });
                    }
                }
            }

            return profiles.OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static string GetCategoryText(int category)
        {
            switch (category)
            {
                case 0: return "公用";
                case 1: return "专用";
                case 2: return "域网络";
                default: return "未知";
            }
        }

        private List<NetworkProfileInfo> GetSelectedProfiles()
        {
            return dataGridView1.SelectedRows.Cast<DataGridViewRow>()
                .OrderBy(row => row.Index)
                .Select(row => row.Tag as NetworkProfileInfo)
                .Where(profile => profile != null)
                .ToList();
        }

        private void SetControlsEnabled(bool enabled)
        {
            foreach (Button button in new[]
            {
                btnRefresh, btnRename, btnPublic, btnPrivate,
                btnDel, btnDelAll
            })
            {
                button.Enabled = enabled;
            }
            dataGridView1.Enabled = enabled;
        }

        private async void btnRefresh_Click(object sender, EventArgs e)
        {
            await RefreshProfilesWithStateAsync();
        }

        private async void btnRename_Click(object sender, EventArgs e)
        {
            List<NetworkProfileInfo> selectedProfiles = GetSelectedProfiles();
            if (selectedProfiles.Count != 1)
            {
                MessageBox.Show("一次只支持重命名一个配置", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            NetworkProfileInfo profile = selectedProfiles[0];
            string newName = ShowRenameDialog(profile.Name);
            if (newName == null) return;

            newName = newName.Trim();
            if (newName.Length == 0)
            {
                MessageBox.Show("配置文件名不能为空", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string command = "Set-ItemProperty -LiteralPath " + QuotePowerShellLiteral(GetPowerShellProfilePath(profile)) +
                             " -Name 'ProfileName' -Value " + QuotePowerShellLiteral(newName) + " -Force";
            await ExecuteProfileOperationAsync(command, "重命名");
        }

        private async void btnPublic_Click(object sender, EventArgs e)
        {
            await SetSelectedCategoryAsync(0, "设为公用");
        }

        private async void btnPrivate_Click(object sender, EventArgs e)
        {
            await SetSelectedCategoryAsync(1, "设为专用");
        }

        private async Task SetSelectedCategoryAsync(int category, string operationName)
        {
            List<NetworkProfileInfo> selectedProfiles = GetSelectedProfiles();
            if (selectedProfiles.Count == 0)
            {
                MessageBox.Show("请先在列表中选择配置文件。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string commands = string.Join(";", selectedProfiles.Select(profile =>
                "Set-ItemProperty -LiteralPath " + QuotePowerShellLiteral(GetPowerShellProfilePath(profile)) +
                " -Name 'Category' -Value " + category + " -Force"));
            await ExecuteProfileOperationAsync(commands, operationName);
        }

        private async void btnDel_Click(object sender, EventArgs e)
        {
            if (_isBusy) return;

            List<NetworkProfileInfo> selectedProfiles = GetSelectedProfiles();
            if (selectedProfiles.Count == 0)
            {
                MessageBox.Show("请先选择需要删除的配置文件", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!_isConfirmingDelete || (DateTime.Now - _lastDeleteClickTime).TotalSeconds > 2)
            {
                _isConfirmingDelete = true;
                _lastDeleteClickTime = DateTime.Now;
                btnDel.Text = "确认删除";
                btnDel.ForeColor = Color.Yellow;

                await Task.Delay(2000);
                if (_isConfirmingDelete &&
                    (DateTime.Now - _lastDeleteClickTime).TotalSeconds >= 2 &&
                    !IsDisposed && !Disposing)
                {
                    _isConfirmingDelete = false;
                    RestoreDeleteButton();
                }
                return;
            }

            _isConfirmingDelete = false;

            string commands = string.Join(";", selectedProfiles.Select(profile =>
                "Remove-Item -LiteralPath " + QuotePowerShellLiteral(GetPowerShellProfilePath(profile)) + " -Recurse -Force"));
            try
            {
                btnDel.Text = "删除中";
                ApplyButtonTheme(btnDel);
                await ExecuteProfileOperationAsync(commands, "删除");
            }
            finally
            {
                RestoreDeleteButton();
            }
        }

        private async void btnDelAll_Click(object sender, EventArgs e)
        {
            if (_isBusy) return;

            List<NetworkProfileInfo> allProfiles = dataGridView1.Rows.Cast<DataGridViewRow>()
                .Select(row => row.Tag as NetworkProfileInfo)
                .Where(profile => profile != null)
                .ToList();

            if (allProfiles.Count == 0)
            {
                MessageBox.Show("当前没有可删除的配置文件", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!_isConfirmingDeleteAll || (DateTime.Now - _lastDeleteAllClickTime).TotalSeconds > 2)
            {
                _isConfirmingDeleteAll = true;
                _lastDeleteAllClickTime = DateTime.Now;
                btnDelAll.Text = "确认全删";
                btnDelAll.UseVisualStyleBackColor = false;
                btnDelAll.FlatStyle = FlatStyle.Flat;
                btnDelAll.BackColor = Color.Red;
                btnDelAll.ForeColor = Color.Yellow;

                await Task.Delay(2000);
                if (_isConfirmingDeleteAll &&
                    (DateTime.Now - _lastDeleteAllClickTime).TotalSeconds >= 2 &&
                    !IsDisposed && !Disposing)
                {
                    _isConfirmingDeleteAll = false;
                    RestoreDeleteAllButton();
                }
                return;
            }

            _isConfirmingDeleteAll = false;

            string commands = string.Join(";", allProfiles.Select(profile =>
                "Remove-Item -LiteralPath " + QuotePowerShellLiteral(GetPowerShellProfilePath(profile)) + " -Recurse -Force"));
            try
            {
                btnDelAll.Text = "删除中";
                ApplyButtonTheme(btnDelAll);
                await ExecuteProfileOperationAsync(commands, "删除全部");
            }
            finally
            {
                RestoreDeleteAllButton();
            }
        }

        private void RestoreDeleteButton()
        {
            btnDel.Text = "删除选中";
            ApplyButtonTheme(btnDel);
        }

        private void RestoreDeleteAllButton()
        {
            btnDelAll.Text = "删除全部";
            ApplyButtonTheme(btnDelAll);
        }

        private async Task ExecuteProfileOperationAsync(string commands, string operationName)
        {
            if (_isBusy) return;

            _isBusy = true;
            SetControlsEnabled(false);
            try
            {
                ElevatedOperationResult result = await RunElevatedPowerShellAsync(commands);
                if (result == ElevatedOperationResult.Cancelled)
                {
                    MessageBox.Show("已取消管理员授权，未执行“" + operationName + "”操作。", "操作已取消",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else if (result == ElevatedOperationResult.Failed)
                {
                    MessageBox.Show("“" + operationName + "操作失败！请确认当前是管理员", "操作失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            finally
            {
                await RefreshProfilesAsync();
                SetControlsEnabled(true);
                _isBusy = false;
            }
        }

        private static Task<ElevatedOperationResult> RunElevatedPowerShellAsync(string commands)
        {
            return Task.Run(() =>
            {
                string script = "$ErrorActionPreference='Stop';" + commands;
                string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encodedCommand,
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                try
                {
                    using (Process process = Process.Start(startInfo))
                    {
                        process.WaitForExit();
                        return process.ExitCode == 0 ? ElevatedOperationResult.Succeeded : ElevatedOperationResult.Failed;
                    }
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    return ElevatedOperationResult.Cancelled;
                }
                catch
                {
                    return ElevatedOperationResult.Failed;
                }
            });
        }

        private static string GetPowerShellProfilePath(NetworkProfileInfo profile)
        {
            return ProfilesPowerShellPath + "\\" + profile.KeyName;
        }

        private static string QuotePowerShellLiteral(string value)
        {
            return "'" + (value ?? string.Empty).Replace("'", "''") + "'";
        }

        private string ShowRenameDialog(string currentName)
        {
            bool isLight = Global.isThemelight;
            Color foreground = isLight ? Global.colorBlack : Global.colorWhite;
            Color background = isLight ? Global.themeLight : Global.themeBlack;
            Color inputBackground = isLight ? SystemColors.Window : Color.FromArgb(32, 32, 32);

            using (Form dialog = new Form())
            using (Label label = new Label())
            using (TextBox input = new TextBox())
            using (Button okButton = new Button())
            using (Button cancelButton = new Button())
            {
                dialog.Text = "重命名配置 ✧ NetInfoCheckerX";
                dialog.ClientSize = new Size(360, 112);
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.Font = Font;
                dialog.BackColor = background;
                dialog.ForeColor = foreground;

                label.AutoSize = true;
                label.Location = new Point(12, 12);
                label.Text = "请输入新的配置文件名";
                label.ForeColor = foreground;

                input.Location = new Point(15, 35);
                input.Size = new Size(330, 23);
                input.Text = currentName;
                input.BackColor = inputBackground;
                input.ForeColor = foreground;
                input.BorderStyle = isLight ? BorderStyle.Fixed3D : BorderStyle.FixedSingle;

                okButton.Location = new Point(189, 73);
                okButton.Size = new Size(75, 27);
                okButton.Text = "确定";
                okButton.DialogResult = DialogResult.OK;

                cancelButton.Location = new Point(270, 73);
                cancelButton.Size = new Size(75, 27);
                cancelButton.Text = "取消";
                cancelButton.DialogResult = DialogResult.Cancel;

                ApplyButtonTheme(okButton);
                ApplyButtonTheme(cancelButton);
                dialog.Controls.AddRange(new Control[] { label, input, okButton, cancelButton });
                dialog.AcceptButton = okButton;
                dialog.CancelButton = cancelButton;
                dialog.Shown += (sender, args) =>
                {
                    input.Focus();
                    input.SelectAll();
                };

                return dialog.ShowDialog(this) == DialogResult.OK ? input.Text : null;
            }
        }
    }
}
