using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Web.Management.Client.Win32;
using Microsoft.Web.Management.Server;
using Phiis.Core;

namespace Phiis.IisManager
{
    /// <summary>
    /// The Phiis page in IIS Manager. A thin UI over Phiis.Core: the same code paths as phiis.exe,
    /// so the GUI and the command line can never disagree.
    /// </summary>
    public sealed class PhiisPage : ModulePage
    {
        private readonly Label _summary = new Label { AutoSize = true, Padding = new Padding(0, 0, 0, 8) };
        private readonly ListView _findings = CreateList("Severity", 80, "Finding", 520, "Suggestion", 420);
        private readonly ListView _extensions = CreateList("Extension", 200, "Type", 80, "Installed", 80);
        private readonly Button _register = new Button { Text = "Register PHP version...", AutoSize = true };
        private readonly Button _openIni = new Button { Text = "Open php.ini", AutoSize = true };
        private readonly Button _refresh = new Button { Text = "Refresh", AutoSize = true };
        private readonly ComboBox _versions = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 520 };
        private readonly Button _switch = new Button { Text = "Switch", AutoSize = true };
        private readonly Button _unregister = new Button { Text = "Unregister...", AutoSize = true };
        private readonly Label _mode = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 0, 0, 6) };

        private PhpStatus _status;
        private bool _loading;

        public PhiisPage()
        {
            _extensions.CheckBoxes = true;
            _extensions.ItemChecked += OnExtensionChecked;
            _register.Click += delegate { RegisterPhp(); };
            _openIni.Click += delegate { OpenIni(); };
            _refresh.Click += delegate { Reload(); };
            _switch.Click += delegate { SwitchPhp(); };
            _unregister.Click += delegate { UnregisterPhp(); };
            _versions.SelectedIndexChanged += delegate { UpdateSwitchButton(); };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
            buttons.Controls.AddRange(new Control[] { _register, _openIni, _refresh });

            var switcher = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
            switcher.Controls.AddRange(new Control[]
            {
                new Label { Text = "PHP version:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) },
                _versions,
                _switch,
                _unregister
            });

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            layout.Controls.Add(_summary);
            layout.Controls.Add(switcher);
            layout.Controls.Add(_mode);
            layout.Controls.Add(buttons);
            layout.Controls.Add(Heading("Health check"));
            layout.Controls.Add(_findings);
            layout.Controls.Add(Heading("Extensions (tick to enable; changes are saved to php.ini with a backup)"));
            layout.Controls.Add(_extensions);
            Controls.Add(layout);
        }

        protected override void OnActivated(bool initialActivation)
        {
            base.OnActivated(initialActivation);
            if (initialActivation)
            {
                Reload();
            }
        }

        /// <summary>null = server level; otherwise the site selected in IIS Manager.</summary>
        private string IisSite
        {
            get
            {
                var path = Connection.ConfigurationPath;
                return path.PathType == ConfigurationPathType.Server ? null : path.SiteName;
            }
        }

        private void Reload()
        {
            if (!Connection.IsLocalConnection)
            {
                _summary.Text = "Phiis currently supports local IIS Manager connections only.";
                SetEnabled(false);
                return;
            }

            try
            {
                _status = new IisPhpConfiguration().GetStatus(IisSite);
                var findings = new Doctor(PhpLifecycle.Load(), () => DateTime.Today).Check(_status);
                findings.AddRange(Doctor.CheckSnapins(SnapinRegistration.GetPhpProviders()));
                ShowSummary();
                ShowVersions();
                ShowFindings(findings);
                ShowExtensions();
                SetEnabled(true);
            }
            catch (Exception ex)
            {
                _summary.Text = "Could not read the PHP configuration: " + ex.Message;
            }
        }

        private void ShowSummary()
        {
            var scope = _status.Site == null ? "Server" : "Site: " + _status.Site;
            if (_status.ActiveHandler == null)
            {
                _summary.Text = scope + "\r\nPHP is not registered at this level. Click 'Register PHP version...'.";
                return;
            }

            string version = "unknown";
            try
            {
                var build = PhpBuild.Inspect(_status.ActiveHandler.ScriptProcessor.Split('|')[0]);
                version = build.VersionText + " (" + build.Architecture + ", " + (build.ThreadSafe ? "TS" : "NTS") + ")";
            }
            catch (Exception)
            {
                // The health check below explains why the executable cannot be read.
            }

            _summary.Text = scope
                            + "\r\nPHP version: " + version
                            + "\r\nExecutable: " + _status.ActiveHandler.ScriptProcessor
                            + "\r\nphp.ini: " + (_status.IniPath ?? "-");
        }

        /// <summary>One entry of the version drop-down. Build == null is the site's "Global default" entry.</summary>
        private sealed class VersionChoice
        {
            public PhpBuild Build;
            public string GlobalDefaultText;
            public bool Active;
            public bool NotGlobal;

            public override string ToString()
            {
                var text = Build == null
                    ? "Global default" + (GlobalDefaultText == null ? " (none registered)" : " (" + GlobalDefaultText + ")")
                    : "PHP " + Build.VersionText + " (" + Build.Architecture + ", " + (Build.ThreadSafe ? "TS" : "NTS") + ")  " + Build.ExecutablePath;
                return text + (NotGlobal ? "  [no longer registered globally]" : "") + (Active ? "  [active]" : "");
            }
        }

        private static string Describe(PhpBuild b)
        {
            return "PHP " + b.VersionText + " " + b.Architecture + " " + (b.ThreadSafe ? "TS" : "NTS");
        }

        private static bool Same(string exe, PhpBuild build)
        {
            return exe != null && string.Equals(Path.GetFullPath(Environment.ExpandEnvironmentVariables(exe.Trim('"'))),
                build.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }

        private void ShowVersions()
        {
            bool site = _status.Site != null;
            var activeExe = _status.ActiveHandler == null ? null : _status.ActiveHandler.ScriptProcessor.Split('|')[0];
            var globalExe = _status.GlobalDefault == null ? null : _status.GlobalDefault.ScriptProcessor.Split('|')[0];
            var builds = IisPhpConfiguration.GetRegisteredBuilds(_status);

            _versions.BeginUpdate();
            _versions.Items.Clear();
            if (site)
            {
                var globalBuild = builds.FirstOrDefault(b => Same(globalExe, b));
                _versions.Items.Add(new VersionChoice
                {
                    GlobalDefaultText = globalBuild != null ? Describe(globalBuild) : globalExe,
                    Active = _status.Mode == PhpScopeMode.GlobalDefault
                });
            }
            foreach (var build in builds)
            {
                // A site's own version is "active" only when pinned; a frozen copy is offered for conversion instead.
                bool active = Same(activeExe, build) && (!site || _status.Mode == PhpScopeMode.Pinned);
                _versions.Items.Add(new VersionChoice
                {
                    Build = build,
                    Active = active,
                    NotGlobal = !IisPhpConfiguration.IsGloballyRegistered(_status, build.ExecutablePath)
                });
            }
            for (int i = 0; i < _versions.Items.Count; i++)
            {
                if (((VersionChoice)_versions.Items[i]).Active)
                {
                    _versions.SelectedIndex = i;
                }
            }
            if (_versions.SelectedIndex < 0 && _versions.Items.Count > 0)
            {
                _versions.SelectedIndex = 0;
            }
            _versions.EndUpdate();

            _register.Visible = !site;       // sites choose from the server-level list
            _unregister.Visible = !site;     // only the server level owns that list
            _mode.Text = ModeText();
            UpdateSwitchButton();
        }

        private string ModeText()
        {
            switch (_status.Mode)
            {
                case PhpScopeMode.Server:
                    return "Global default for every site that does not choose its own version. Sites pick from this list.";
                case PhpScopeMode.GlobalDefault:
                    return "This site uses the global default and follows it when the server level switches.";
                case PhpScopeMode.Pinned:
                    return "This site uses its own PHP version. Server-level switches and unregistering do not affect it.";
                case PhpScopeMode.FullCopy:
                    return "This site holds a frozen copy of the server's handler list. Choosing a version (or Global default) converts it to a normal site.";
                default:
                    return string.Empty;
            }
        }

        private void UpdateSwitchButton()
        {
            var choice = _versions.SelectedItem as VersionChoice;
            bool canSwitch = choice != null && (!choice.Active || (_status != null && _status.Mode == PhpScopeMode.FullCopy));
            _switch.Enabled = _versions.Enabled && canSwitch;
            _unregister.Enabled = _versions.Enabled && choice != null && choice.Build != null && !choice.Active;
        }

        private void SwitchPhp()
        {
            var choice = _versions.SelectedItem as VersionChoice;
            if (choice == null)
            {
                return;
            }
            if (choice.Build == null)
            {
                var site = IisSite;
                ApplyChange(dryRun => new IisPhpConfiguration().UseGlobalDefault(site, dryRun));
            }
            else
            {
                ApplyPhp(choice.Build.ExecutablePath);
            }
        }

        private void UnregisterPhp()
        {
            var choice = _versions.SelectedItem as VersionChoice;
            if (choice != null && choice.Build != null)
            {
                var exe = choice.Build.ExecutablePath;
                ApplyChange(dryRun => new IisPhpConfiguration().Unregister(exe, dryRun));
            }
        }

        /// <summary>Shows what a change would do (dry run), asks, then applies it and reloads.</summary>
        private void ApplyChange(Func<bool, ChangeResult> change)
        {
            try
            {
                var preview = change(true);
                if (!preview.Changed)
                {
                    MessageBox.Show(this, "Nothing to change; already configured.", "Phiis");
                    return;
                }
                var question = "Phiis will make these changes (backups are created first):\r\n\r\n- "
                               + string.Join("\r\n- ", preview.Changes) + "\r\n\r\nContinue?";
                if (MessageBox.Show(this, question, "Phiis", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                {
                    return;
                }
                change(false);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Phiis", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            Reload();
        }

        private void ShowFindings(System.Collections.Generic.IEnumerable<Finding> findings)
        {
            _findings.BeginUpdate();
            _findings.Items.Clear();
            foreach (var f in findings.OrderByDescending(x => x.Severity))
            {
                var item = new ListViewItem(new[] { f.Severity.ToString(), f.Message, f.Hint ?? string.Empty });
                item.ForeColor = f.Severity == Severity.Error ? Color.Firebrick
                    : f.Severity == Severity.Warning ? Color.DarkOrange
                    : f.Severity == Severity.Ok ? Color.SeaGreen
                    : SystemColors.WindowText;
                _findings.Items.Add(item);
            }
            _findings.EndUpdate();
        }

        private void ShowExtensions()
        {
            _loading = true;
            _extensions.BeginUpdate();
            _extensions.Items.Clear();
            try
            {
                if (_status.IniPath == null || !File.Exists(_status.IniPath))
                {
                    return;
                }

                var ini = PhpIniDocument.Load(_status.IniPath);
                var extDir = Doctor.ResolveExtensionDir(ini, null, _status.IniPath);
                var known = ini.GetExtensions().GroupBy(e => e.Name).ToDictionary(g => g.Key, g => g.ToList());
                var onDisk = Directory.Exists(extDir)
                    ? Directory.GetFiles(extDir, "php_*.dll").Select(PhpIniDocument.NormalizeExtensionName)
                    : Enumerable.Empty<string>();

                foreach (var name in known.Keys.Union(onDisk).OrderBy(n => n))
                {
                    var entries = known.ContainsKey(name) ? known[name] : null;
                    bool zend = entries != null && entries.Any(e => e.IsZend);
                    bool installed = entries == null || entries.Any(e => Doctor.ResolveExtensionFile(e, extDir) != null);
                    var item = new ListViewItem(new[] { name, zend ? "zend" : "", installed ? "yes" : "MISSING" })
                    {
                        Checked = entries != null && entries.Any(e => e.Enabled),
                        Tag = zend
                    };
                    _extensions.Items.Add(item);
                }
            }
            finally
            {
                _extensions.EndUpdate();
                _loading = false;
            }
        }

        private void OnExtensionChecked(object sender, ItemCheckedEventArgs e)
        {
            if (_loading || _status == null || _status.IniPath == null)
            {
                return;
            }
            try
            {
                var ini = PhpIniDocument.Load(_status.IniPath);
                var name = e.Item.Text;
                bool changed = e.Item.Checked ? ini.EnableExtension(name, (bool)e.Item.Tag) : ini.DisableExtension(name);
                if (changed)
                {
                    ini.Save(_status.IniPath, Backup.DefaultDirectory);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Phiis", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Reload();
            }
        }

        private void RegisterPhp()
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "Select php-cgi.exe",
                Filter = "PHP FastCGI executable (php-cgi.exe)|php-cgi.exe",
                CheckFileExists = true
            })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    ApplyPhp(dialog.FileName);
                }
            }
        }

        /// <summary>Registers (or re-activates) a php-cgi.exe for the current scope after showing what will change.</summary>
        private void ApplyPhp(string phpCgiPath)
        {
            {
                try
                {
                    var iis = new IisPhpConfiguration();
                    var preview = iis.Register(phpCgiPath, IisSite, true);
                    var build = PhpBuild.Inspect(phpCgiPath);
                    PhpIniSetup.Prepare(build, build.DefaultIniPath, preview);

                    if (!preview.Changed)
                    {
                        MessageBox.Show(this, "This PHP version is already registered and active.", "Phiis");
                        Reload();
                        return;
                    }

                    var question = "Phiis will make these changes (backups are created first):\r\n\r\n- "
                                   + string.Join("\r\n- ", preview.Changes) + "\r\n\r\nContinue?";
                    if (MessageBox.Show(this, question, "Phiis", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                    {
                        return;
                    }

                    var result = iis.Register(phpCgiPath, IisSite, false);
                    PhpIniSetup.Prepare(build, iis.GetStatus(IisSite).IniPath ?? build.DefaultIniPath, result);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Phiis", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                Reload();
            }
        }

        private void OpenIni()
        {
            if (_status == null || _status.IniPath == null || !File.Exists(_status.IniPath))
            {
                return;
            }

            // IIS Manager blocks modules from starting processes ("The process creation has been blocked"),
            // and an unhandled exception here terminates InetMgr. So edit the file in-process instead.
            try
            {
                using (var editor = new IniEditorForm(_status.IniPath))
                {
                    if (editor.ShowDialog(this) == DialogResult.OK && editor.Changed)
                    {
                        // Backup first, then an atomic temp-file swap (see PhpIniDocument.Save).
                        editor.Document.Save(_status.IniPath, Backup.DefaultDirectory);
                        Reload();
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Phiis", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SetEnabled(bool enabled)
        {
            _register.Enabled = enabled;
            _versions.Enabled = enabled && _versions.Items.Count > 0;
            UpdateSwitchButton();
            _openIni.Enabled = enabled && _status != null && _status.IniPath != null;
            _extensions.Enabled = enabled;
        }

        private static Label Heading(string text)
        {
            return new Label { Text = text, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Padding = new Padding(0, 10, 0, 4) };
        }

        private static ListView CreateList(params object[] columns)
        {
            var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false };
            for (int i = 0; i < columns.Length; i += 2)
            {
                list.Columns.Add((string)columns[i], (int)columns[i + 1]);
            }
            return list;
        }
    }
}
