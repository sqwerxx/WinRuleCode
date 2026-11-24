using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.Button;

namespace WinRule
{
    public partial class Разрешения : Form
    {
        private string _targetFilePath;
        private CancellationTokenSource _cancellationTokenSource;

        // ===== Shell API для иконок =====
        [StructLayout(LayoutKind.Sequential)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon;
            public IntPtr iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        };

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            out SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        private const uint SHGFI_ICON = 0x100;
        private const uint SHGFI_SMALLICON = 0x1;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        private const int ICON_FOLDER = 0;
        private const int ICON_DRIVE = 1;
        private const int ICON_SYSTEM = 2;

        private bool _iconsInitialized = false;
        private ImageList imageListFolders;
        private const string DUMMY_NODE_TEXT = "Загрузка...";

        public Разрешения(string filePath = null)
        {
            InitializeComponent();
            _targetFilePath = filePath;

            button2.Click += button2_Click;
            button3.Click += button3_Click;

            radioButton16.CheckedChanged += radioButton16_CheckedChanged;
            radioButton17.CheckedChanged += radioButton17_CheckedChanged;

            treeView1.BeforeExpand += treeView1_BeforeExpand;

            RegisterSilentStartup();
            ContextMenuHelper.Register();

            if (!string.IsNullOrEmpty(_targetFilePath) && File.Exists(_targetFilePath))
            {
                ApplyFileContext(_targetFilePath);
                LoadRuleToUI();
            }
            else
            {
                label2.Text = "Выберите .exe файл через ПКМ или перетащите сюда.";
                label1.Text = "Сценарии доступа для выбранного файла.";
            }

            StartProcessMonitor();
        }

        private async void StartProcessMonitor()
        {
            _cancellationTokenSource = new CancellationTokenSource();

            System.Diagnostics.Debug.WriteLine("[WinRule] ========================================");
            System.Diagnostics.Debug.WriteLine("[WinRule] Запуск ProcessMonitor из формы Разрешения");
            System.Diagnostics.Debug.WriteLine("[WinRule] ========================================");

            try
            {
                await ProcessMonitor.StartMonitoringAsync(_cancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WinRule] ОШИБКА ProcessMonitor: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[WinRule] Stack: {ex.StackTrace}");
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _cancellationTokenSource?.Cancel();
            base.OnFormClosing(e);
        }

        private void RegisterSilentStartup()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                string startupCommand = $"\"{Application.ExecutablePath}\" --silent";
                if ((string)key.GetValue("WinRule", "") != startupCommand)
                {
                    key.SetValue("WinRule", startupCommand);
                }
            }
            catch { }
        }

        private void ApplyFileContext(string path)
        {
            var fileName = Path.GetFileName(path);
            label2.Text = $"Настройки разрешений для: {fileName}";
            label1.Text = $"Сценарии доступа для: {fileName}";
            this.Text = $"WinRule — {fileName}";
            try { this.Icon = Icon.ExtractAssociatedIcon(path); } catch { }
        }

        // ===== ИНИЦИАЛИЗАЦИЯ ИКОНОК =====

        private void InitializeImageList()
        {
            if (_iconsInitialized) return;

            imageListFolders = new ImageList();
            imageListFolders.ColorDepth = ColorDepth.Depth32Bit;
            imageListFolders.ImageSize = new Size(16, 16);
            treeView1.ImageList = imageListFolders;

            AddIconFromPath(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), ICON_FOLDER);
            AddIconFromPath("C:\\", ICON_DRIVE);
            AddIconFromPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows), ICON_SYSTEM);

            _iconsInitialized = true;
        }

        private void AddIconFromPath(string path, int index)
        {
            SHFILEINFO shinfo = new SHFILEINFO();
            uint flags = SHGFI_ICON | SHGFI_SMALLICON;

            if (!Directory.Exists(path) && !File.Exists(path))
            {
                flags |= 0x10;
            }

            IntPtr hImgSmall = SHGetFileInfo(path, FILE_ATTRIBUTE_DIRECTORY, out shinfo,
                (uint)Marshal.SizeOf(shinfo), flags);

            if (hImgSmall != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
            {
                try
                {
                    Icon icon = Icon.FromHandle(shinfo.hIcon);
                    imageListFolders.Images.Add(icon.ToBitmap());
                }
                finally
                {
                    DestroyIcon(shinfo.hIcon);
                }
            }
            else
            {
                Image fallbackIcon = null;
                switch (index)
                {
                    case ICON_FOLDER:
                        fallbackIcon = SystemIcons.Application.ToBitmap();
                        break;
                    case ICON_DRIVE:
                        fallbackIcon = SystemIcons.WinLogo.ToBitmap();
                        break;
                    case ICON_SYSTEM:
                        fallbackIcon = SystemIcons.Shield.ToBitmap();
                        break;
                }

                if (fallbackIcon != null)
                {
                    imageListFolders.Images.Add(fallbackIcon);
                }
            }
        }

        // ===== LAZY LOADING TREEVIEW =====

        private void LoadDrivesToTreeView()
        {
            treeView1.Nodes.Clear();
            treeView1.CheckBoxes = true;
            InitializeImageList();

            try
            {
                var thisPC = new TreeNode("💻 Этот компьютер");
                thisPC.Tag = "MY_COMPUTER";
                thisPC.ImageIndex = ICON_DRIVE;
                thisPC.SelectedImageIndex = ICON_DRIVE;

                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (drive.IsReady &&
                        drive.DriveType != DriveType.CDRom &&
                        drive.DriveType != DriveType.Network &&
                        drive.DriveType != DriveType.Removable)
                    {
                        string displayName = $"{drive.Name} ({GetDriveTypeString(drive.DriveType)})";

                        var driveNode = new TreeNode(displayName);
                        driveNode.Tag = drive.Name;
                        driveNode.ImageIndex = ICON_DRIVE;
                        driveNode.SelectedImageIndex = ICON_DRIVE;

                        var dummyNode = new TreeNode(DUMMY_NODE_TEXT);
                        dummyNode.Tag = null;
                        driveNode.Nodes.Add(dummyNode);

                        thisPC.Nodes.Add(driveNode);
                    }
                }

                treeView1.Nodes.Add(thisPC);
                thisPC.Expand();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка загрузки дисков: {ex.Message}");
            }
        }

        private string GetDriveTypeString(DriveType type)
        {
            return type switch
            {
                DriveType.Fixed => "Локальный диск",
                DriveType.Removable => "Съёмный диск",
                DriveType.Network => "Сетевой диск",
                DriveType.CDRom => "CD/DVD",
                DriveType.Ram => "RAM диск",
                _ => "Диск"
            };
        }

        private void treeView1_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            var node = e.Node;

            if (node.Nodes.Count == 1 && node.Nodes[0].Text == DUMMY_NODE_TEXT)
            {
                node.Nodes.Clear();

                string path = node.Tag?.ToString();
                if (!string.IsNullOrEmpty(path) && path != "MY_COMPUTER")
                {
                    LoadSubDirectories(node, path);
                }
            }
        }

        private void LoadSubDirectories(TreeNode parentNode, string parentPath)
        {
            Cursor.Current = Cursors.WaitCursor;

            try
            {
                var directories = Directory.GetDirectories(parentPath);

                foreach (var dir in directories)
                {
                    try
                    {
                        var dirInfo = new DirectoryInfo(dir);

                        if ((dirInfo.Attributes & FileAttributes.Hidden) != 0)
                            continue;

                        string dirName = dirInfo.Name;
                        var dirNode = new TreeNode(dirName);
                        dirNode.Tag = dir;
                        dirNode.ImageIndex = IsSystemFolder(dir) ? ICON_SYSTEM : ICON_FOLDER;
                        dirNode.SelectedImageIndex = dirNode.ImageIndex;

                        var dummyNode = new TreeNode(DUMMY_NODE_TEXT);
                        dummyNode.Tag = null;
                        dirNode.Nodes.Add(dummyNode);

                        parentNode.Nodes.Add(dirNode);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        continue;
                    }
                    catch
                    {
                        continue;
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                var accessNode = new TreeNode("⛔ Доступ запрещён");
                accessNode.ForeColor = Color.Red;
                parentNode.Nodes.Add(accessNode);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка загрузки подпапок {parentPath}: {ex.Message}");
            }
            finally
            {
                Cursor.Current = Cursors.Default;
            }
        }

        private bool IsSystemFolder(string path)
        {
            var systemFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Windows", "Program Files", "Program Files (x86)",
                "ProgramData", "$Recycle.Bin", "System Volume Information",
                "Recovery", "MSOCache", "PerfLogs"
            };

            string folderName = Path.GetFileName(path);
            return systemFolders.Contains(folderName);
        }

        // ===== ПОЛУЧЕНИЕ ВЫБРАННЫХ ПУТЕЙ =====

        private List<string> GetSelectedPaths()
        {
            var result = new List<string>();
            GetSelectedPathsRecursive(treeView1.Nodes, result);
            return result;
        }

        private void GetSelectedPathsRecursive(TreeNodeCollection nodes, List<string> result)
        {
            foreach (TreeNode node in nodes)
            {
                if (node.Checked && node.Tag != null && node.Tag.ToString() != "MY_COMPUTER")
                {
                    result.Add(node.Tag.ToString());
                }
                GetSelectedPathsRecursive(node.Nodes, result);
            }
        }

        // ===== УСТАНОВКА ВЫБРАННЫХ ПУТЕЙ =====

        private void SetSelectedPaths(List<string> paths)
        {
            if (paths == null || paths.Count == 0) return;

            foreach (var path in paths)
            {
                TreeNode node = FindAndExpandPath(treeView1.Nodes, path);
                if (node != null)
                {
                    node.Checked = true;
                }
            }
        }

        private TreeNode FindAndExpandPath(TreeNodeCollection nodes, string targetPath)
        {
            foreach (TreeNode node in nodes)
            {
                if (node.Tag != null &&
                    node.Tag.ToString().Equals(targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    return node;
                }

                if (node.Tag != null &&
                    targetPath.StartsWith(node.Tag.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    if (node.Nodes.Count == 1 && node.Nodes[0].Text == DUMMY_NODE_TEXT)
                    {
                        node.Expand();
                    }

                    TreeNode found = FindAndExpandPath(node.Nodes, targetPath);
                    if (found != null) return found;
                }
            }
            return null;
        }

        // ===== ОБРАБОТЧИКИ RADIOBUTTON ПАМЯТИ =====

        private void radioButton17_CheckedChanged(object sender, EventArgs e)
        {
            if (radioButton17.Checked)
            {
                treeView1.Enabled = false;
            }
        }

        private void radioButton16_CheckedChanged(object sender, EventArgs e)
        {
            if (radioButton16.Checked)
            {
                treeView1.Enabled = true;
                if (treeView1.Nodes.Count == 0)
                {
                    LoadDrivesToTreeView();
                }
            }
        }

        // ===== ЗАГРУЗКА И СОХРАНЕНИЕ ПРАВИЛ =====

        private void LoadRuleToUI()
        {
            var rules = RuleManager.LoadRules();
            var rule = rules.FirstOrDefault(r => r.ExecutablePath.Equals(_targetFilePath, StringComparison.OrdinalIgnoreCase));
            if (rule == null) return;

            // Сеть
            if (rule.Network == PermissionState.Deny) radioButton12.Checked = true;
            else if (rule.Network == PermissionState.Allow) radioButton11.Checked = true;
            else radioButton10.Checked = true;

            // Камера
            if (rule.Camera == PermissionState.Deny) radioButton3.Checked = true;
            else if (rule.Camera == PermissionState.Allow) radioButton2.Checked = true;
            else radioButton1.Checked = true;

            // Микрофон
            if (rule.Microphone == PermissionState.Deny) radioButton6.Checked = true;
            else if (rule.Microphone == PermissionState.Allow) radioButton5.Checked = true;
            else radioButton4.Checked = true;

            // Память (Disk)
            if (rule.Disk == PermissionState.Deny)
            {
                radioButton16.Checked = true;
                treeView1.Enabled = true;

                if (treeView1.Nodes.Count == 0)
                {
                    LoadDrivesToTreeView();
                }

                if (rule.BlockedPaths != null && rule.BlockedPaths.Count > 0)
                {
                    if (this.IsHandleCreated)
                    {
                        this.BeginInvoke((MethodInvoker)delegate
                        {
                            SetSelectedPaths(rule.BlockedPaths);
                        });
                    }
                    else
                    {
                        this.Load += (s, ev) =>
                        {
                            SetSelectedPaths(rule.BlockedPaths);
                        };
                    }
                }
            }
            else
            {
                radioButton17.Checked = true;
                treeView1.Enabled = false;
            }

            // 🔧 ИСПРАВЛЕНО: Права администратора (groupBox11: 31=Ask, 32=Allow, 33=Deny)
            if (rule.AdminRights == PermissionState.Deny) radioButton33.Checked = true;
            else if (rule.AdminRights == PermissionState.Allow) radioButton32.Checked = true;
            else radioButton31.Checked = true;

            // 🆕 Местоположение (groupBox3: 7=Ask, 8=Allow, 9=Deny)
            if (rule.Location == PermissionState.Deny) radioButton9.Checked = true;
            else if (rule.Location == PermissionState.Allow) radioButton8.Checked = true;
            else radioButton7.Checked = true;

            // 🔧 ИСПРАВЛЕНО: Уведомления (groupBox5: 18=Ask, 19=Allow, 20=Deny)
            if (rule.Notifications == PermissionState.Deny) radioButton20.Checked = true;
            else if (rule.Notifications == PermissionState.Allow) radioButton19.Checked = true;
            else radioButton18.Checked = true;
        }

        private void button1_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_targetFilePath))
            {
                MessageBox.Show("Сначала выберите файл!", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                var rule = new AppRule
                {
                    ExecutablePath = _targetFilePath,
                    ExecutableName = Path.GetFileName(_targetFilePath),
                    Network = radioButton12.Checked ? PermissionState.Deny : (radioButton11.Checked ? PermissionState.Allow : PermissionState.Ask),
                    Camera = radioButton3.Checked ? PermissionState.Deny : (radioButton2.Checked ? PermissionState.Allow : PermissionState.Ask),
                    Microphone = radioButton6.Checked ? PermissionState.Deny : (radioButton5.Checked ? PermissionState.Allow : PermissionState.Ask),
                    Disk = radioButton16.Checked ? PermissionState.Deny : PermissionState.Allow,
                    BlockedPaths = radioButton16.Checked ? GetSelectedPaths() : new List<string>(),

                    // AdminRights 
                    AdminRights = radioButton33.Checked ? PermissionState.Deny : (radioButton32.Checked ? PermissionState.Allow : PermissionState.Ask),

                    // Местоположение
                    Location = radioButton9.Checked ? PermissionState.Deny : (radioButton8.Checked ? PermissionState.Allow : PermissionState.Ask),

                    // Уведомления
                    Notifications = radioButton20.Checked ? PermissionState.Deny : (radioButton19.Checked ? PermissionState.Allow : PermissionState.Ask)
                };

                var allRules = RuleManager.LoadRules();
                allRules.RemoveAll(r => r.ExecutablePath.Equals(rule.ExecutablePath, StringComparison.OrdinalIgnoreCase));
                allRules.Add(rule);
                RuleManager.SaveRules(allRules);

                FirewallHelper.ApplyNetworkRule(rule.ExecutablePath, rule.ExecutableName, rule.Network);
                PermissionHelper.SetDiskPermission(rule.ExecutablePath, rule.Disk);
                PermissionHelper.SetAdminPermission(rule.ExecutablePath, rule.ExecutableName, rule.AdminRights);

                PermissionHelper.SetHardwarePermission(rule.ExecutablePath, "webcam", rule.Camera);
                PermissionHelper.SetHardwarePermission(rule.ExecutablePath, "microphone", rule.Microphone);

                string blockedInfo = rule.BlockedPaths.Count > 0
                    ? $"\n\n🚫 Заблокировано {rule.BlockedPaths.Count} папок"
                    : "";

                MessageBox.Show($"Правила для {rule.ExecutableName} успешно сохранены и применены!{blockedInfo}",
                    "Успех", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (UnauthorizedAccessException)
            {
                MessageBox.Show("Операция отменена: необходимы права администратора.", "Доступ запрещен", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void button2_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_targetFilePath))
            {
                MessageBox.Show("Сначала выберите файл!", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                var allRules = RuleManager.LoadRules();
                allRules.RemoveAll(r => r.ExecutablePath.Equals(_targetFilePath, StringComparison.OrdinalIgnoreCase));
                RuleManager.SaveRules(allRules);

                string exeName = Path.GetFileName(_targetFilePath);
                FirewallHelper.ApplyNetworkRule(_targetFilePath, exeName, PermissionState.Allow);
                PermissionHelper.SetDiskPermission(_targetFilePath, PermissionState.Allow);
                PermissionHelper.SetAdminPermission(_targetFilePath, exeName, PermissionState.Allow);

                PermissionHelper.SetHardwarePermission(_targetFilePath, "webcam", PermissionState.Allow);
                PermissionHelper.SetHardwarePermission(_targetFilePath, "microphone", PermissionState.Allow);

                // Сброс всех radioButton в Ask/Allow
                radioButton11.Checked = true;  // Network = Allow
                radioButton1.Checked = true;   // Camera = Ask
                radioButton4.Checked = true;   // Microphone = Ask
                radioButton17.Checked = true;  // Disk = Allow (разрешать всегда)
                radioButton7.Checked = true;  // Location = Ask
                radioButton31.Checked = true;
                radioButton18.Checked = true;

                treeView1.Enabled = false;
                treeView1.Nodes.Clear();

                MessageBox.Show("Правила сброшены в состояние по умолчанию.", "Сброс", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (UnauthorizedAccessException)
            {
                MessageBox.Show("Операция отменена: необходимы права администратора.", "Доступ запрещен", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void button3_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        #region Пустые обработчики
        private void Разрешения_Load(object sender, EventArgs e) { }
        private void panel1_Paint(object sender, PaintEventArgs e) { }
        private void tabPage1_Click(object sender, EventArgs e) { }
        private void label1_Click(object sender, EventArgs e) { }
        private void label2_Click(object sender, EventArgs e) { }
        private void tabPage2_Click(object sender, EventArgs e) { }
        private void groupBox6_Enter(object sender, EventArgs e) { }
        private void groupBox10_Enter(object sender, EventArgs e) { }
        #endregion

        private void groupBox3_Enter(object sender, EventArgs e) { }
        private void groupBox7_Enter(object sender, EventArgs e) { }
    }
}