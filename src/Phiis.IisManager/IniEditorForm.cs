using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Phiis.Core;

namespace Phiis.IisManager
{
    /// <summary>
    /// php.ini editor. The Settings tab lists every directive found in the file (active and commented-out
    /// defaults) with the comment above it as description; the Advanced tab is the raw text.
    /// Both edit one <see cref="PhpIniDocument"/>, which only changes the lines that change, so comments,
    /// ordering and hand edits are kept.
    /// </summary>
    internal sealed class IniEditorForm : Form
    {
        private static readonly Regex SizeValue = new Regex(@"^-?\d+[KMGkmg]?$");
        private static readonly string[] Switches = { "On", "Off" };

        private readonly TabControl _tabs = new TabControl { Dock = DockStyle.Fill };
        private readonly TabPage _settingsTab = new TabPage("Settings");
        private readonly TabPage _rawTab = new TabPage("Advanced (text)");
        private readonly TextBox _filter = new TextBox { Width = 260 };
        private readonly CheckBox _onlySet = new CheckBox { Text = "Show only set values", AutoSize = true, Checked = true };
        private readonly Label _count = new Label { AutoSize = true, Padding = new Padding(8, 4, 0, 0) };
        private readonly DataGridView _grid = new DataGridView();
        private readonly TextBox _description = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Bottom, Height = 90 };
        private readonly TextBox _raw = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            AcceptsTab = true,
            MaxLength = 0,
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 9.5f)
        };

        private readonly string _originalText;
        private Dictionary<string, string> _originalValues;
        private string _rawShown;

        public IniEditorForm(string path)
        {
            Document = PhpIniDocument.Load(path);
            _originalText = Document.ToText();
            _originalValues = Document.GetDirectives().ToDictionary(Key, d => d.Value, StringComparer.OrdinalIgnoreCase);

            Text = "php.ini - " + path;
            Size = new Size(1000, 720);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            Icon = PhiisModule.WindowIcon;

            BuildSettingsTab();
            _rawTab.Controls.Add(_raw);
            _tabs.TabPages.Add(_settingsTab);
            _tabs.TabPages.Add(_rawTab);
            _tabs.Selecting += OnTabSelecting;

            var save = new Button { Text = "Save (backup first)", AutoSize = true };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            save.Click += delegate { OnSave(); };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            bar.Controls.AddRange(new Control[] { cancel, save });

            Controls.Add(_tabs);
            Controls.Add(bar);
            CancelButton = cancel;

            FillGrid();
        }

        /// <summary>The edited document; save it with <see cref="PhpIniDocument.Save"/>.</summary>
        public PhpIniDocument Document { get; private set; }

        public bool Changed
        {
            get { return !string.Equals(Document.ToText(), _originalText, StringComparison.Ordinal); }
        }

        // ---------- settings tab ----------

        private void BuildSettingsTab()
        {
            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.RowHeadersVisible = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.MultiSelect = false;
            _grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
            _grid.BackgroundColor = SystemColors.Window;
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Name", ReadOnly = true, Width = 260 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "Value", Width = 300 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Default", HeaderText = "Commented default", ReadOnly = true, Width = 200 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Section", HeaderText = "Section", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            _grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) { _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells["Value"]; _grid.BeginEdit(true); } };
            _grid.SelectionChanged += delegate { ShowDescription(); };
            _grid.CellValidating += OnCellValidating;
            _grid.CellEndEdit += OnCellEndEdit;
            _grid.CurrentCellDirtyStateChanged += delegate
            {
                if (_grid.CurrentCell is DataGridViewComboBoxCell)
                {
                    _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                }
            };
            _grid.DataError += (s, e) => { e.ThrowException = false; };

            _filter.TextChanged += delegate { ApplyFilter(); };
            _onlySet.CheckedChanged += delegate { ApplyFilter(); };

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
            top.Controls.AddRange(new Control[]
            {
                new Label { Text = "Filter:", AutoSize = true, Padding = new Padding(0, 4, 0, 0) },
                _filter,
                _onlySet,
                _count
            });

            _settingsTab.Controls.Add(_grid);
            _settingsTab.Controls.Add(_description);
            _settingsTab.Controls.Add(top);
        }

        private static string Key(PhpIniDirective d)
        {
            return (d.IsPerScope ? d.Section : string.Empty) + "|" + d.Name;
        }

        private void FillGrid()
        {
            _grid.SuspendLayout();
            _grid.Rows.Clear();
            foreach (var d in Document.GetDirectives())
            {
                int index = _grid.Rows.Add(d.Name, d.Value ?? string.Empty, d.CommentedValue ?? string.Empty, d.Section);
                var row = _grid.Rows[index];
                row.Tag = d;

                if (d.IsPerScope)
                {
                    // [PATH=]/[HOST=] values apply to one folder or host only: shown, edited in the Advanced tab.
                    row.ReadOnly = true;
                    row.DefaultCellStyle.ForeColor = SystemColors.GrayText;
                }
                else if (IsSwitch(d.Value) || (d.Value == null && IsSwitch(d.CommentedValue)))
                {
                    var combo = new DataGridViewComboBoxCell { FlatStyle = FlatStyle.Flat };
                    combo.Items.Add(string.Empty);
                    combo.Items.AddRange(Switches);
                    if (d.Value != null && !combo.Items.Contains(d.Value))
                    {
                        combo.Items.Add(d.Value);
                    }
                    combo.Value = d.Value ?? string.Empty;
                    row.Cells["Value"] = combo;
                }
                MarkChanged(row, d);
            }
            _grid.ResumeLayout();
            ApplyFilter();
        }

        private static bool IsSwitch(string value)
        {
            return value != null && Switches.Contains(value, StringComparer.OrdinalIgnoreCase);
        }

        private void MarkChanged(DataGridViewRow row, PhpIniDirective d)
        {
            string original;
            _originalValues.TryGetValue(Key(d), out original);
            bool changed = !string.Equals(original, d.Value, StringComparison.Ordinal);
            row.DefaultCellStyle.Font = changed ? new Font(_grid.Font, FontStyle.Bold) : null;
            if (!d.IsPerScope)
            {
                row.DefaultCellStyle.ForeColor = d.Value == null ? SystemColors.GrayText : SystemColors.WindowText;
            }
        }

        private void ApplyFilter()
        {
            var text = _filter.Text.Trim();
            _grid.CurrentCell = null;
            int visible = 0;
            foreach (DataGridViewRow row in _grid.Rows)
            {
                var d = (PhpIniDirective)row.Tag;
                bool show = (!_onlySet.Checked || d.Value != null)
                            && (text.Length == 0
                                || d.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0
                                || (d.Description ?? string.Empty).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
                row.Visible = show;
                if (show)
                {
                    visible++;
                }
            }
            _count.Text = visible + " of " + _grid.Rows.Count + " settings";
        }

        private void ShowDescription()
        {
            var row = _grid.CurrentRow;
            var d = row == null ? null : row.Tag as PhpIniDirective;
            if (d == null)
            {
                _description.Text = string.Empty;
                return;
            }
            var text = d.Description ?? "(no description in php.ini)";
            if (d.IsPerScope)
            {
                text = "Applies only to [" + d.Section + "]. Edit it in the Advanced tab.\r\n\r\n" + text;
            }
            _description.Text = d.Name + " (line " + d.LineNumber + ")\r\n" + text.Replace("\n", "\r\n").Replace("\r\r\n", "\r\n");
        }

        private void OnCellValidating(object sender, DataGridViewCellValidatingEventArgs e)
        {
            if (_grid.Columns[e.ColumnIndex].Name != "Value")
            {
                return;
            }
            var d = (PhpIniDirective)_grid.Rows[e.RowIndex].Tag;
            var value = Convert.ToString(e.FormattedValue).Trim();
            var reference = d.Value ?? d.CommentedValue;

            // Size-like settings (128M, 2G, -1) are recognised from their current value, not from a hardcoded list.
            if (value.Length > 0 && reference != null && SizeValue.IsMatch(reference) && !SizeValue.IsMatch(value))
            {
                MessageBox.Show(this, d.Name + " expects a number, optionally followed by K, M or G (for example 128M or -1).",
                    "Phiis", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true;
            }
        }

        private void OnCellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            var row = _grid.Rows[e.RowIndex];
            var d = (PhpIniDirective)row.Tag;
            var value = Convert.ToString(row.Cells["Value"].Value).Trim();

            if (value.Length == 0)
            {
                Document.RemoveValue(d.Name); // commented out, not deleted
            }
            else
            {
                Document.SetValue(d.Name, value);
            }

            var updated = Document.GetDirectives().FirstOrDefault(x => string.Equals(Key(x), Key(d), StringComparison.OrdinalIgnoreCase));
            if (updated != null)
            {
                row.Tag = updated;
                row.Cells["Value"].Value = updated.Value ?? string.Empty;
                MarkChanged(row, updated);
            }
            _count.Text = _grid.Rows.Cast<DataGridViewRow>().Count(r => r.Visible) + " of " + _grid.Rows.Count + " settings";
        }

        // ---------- tabs and saving ----------

        private void OnTabSelecting(object sender, TabControlCancelEventArgs e)
        {
            if (e.TabPage == _rawTab)
            {
                _grid.EndEdit();
                _rawShown = Document.ToText();
                _raw.Text = _rawShown;
            }
            else
            {
                SyncFromRaw();
                FillGrid();
            }
        }

        /// <summary>Takes over edits made in the Advanced tab.</summary>
        private void SyncFromRaw()
        {
            if (_rawShown != null && !string.Equals(_raw.Text, _rawShown, StringComparison.Ordinal))
            {
                Document = PhpIniDocument.FromText(_raw.Text);
            }
            _rawShown = null;
        }

        private void OnSave()
        {
            if (_tabs.SelectedTab == _rawTab)
            {
                SyncFromRaw();
            }
            else
            {
                _grid.EndEdit();
            }
            DialogResult = DialogResult.OK;
        }
    }
}
