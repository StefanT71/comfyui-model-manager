using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ComfyModelManager
{
    public class ModelRow
    {
        public ModelFile Model;
        public bool Selected { get; set; }

        public string FileName { get { return Model.FileName; } }
        public string RelativePath { get { return Model.RelativePath; } }
        public long Size { get { return Model.Size; } }
        public string StatusText
        {
            get
            {
                if (Model.IsDuplicate) return Lang.T("status.duplicate");
                if (Model.IsArchived) return Lang.T("status.archived");
                return Model.Used ? Lang.T("status.used") : Lang.T("status.unused");
            }
        }

        public int StatusRank
        {
            get
            {
                if (Model.IsDuplicate) return 3;
                if (Model.IsArchived) return 2;
                return Model.Used ? 0 : 1;
            }
        }

        public int MatchCount { get { return Model.MatchedWorkflows.Count; } }
    }

    internal class WorkflowItem
    {
        public string FullPath;
        public string Display;

        public override string ToString()
        {
            return Display;
        }
    }

    public class MainForm : Form
    {
        private readonly string _iniPath;
        private IniFile _ini;

        private TextBox _txtModels;
        private TextBox _txtWorkflows;
        private TextBox _txtArchive;
        private readonly List<Button> _browseButtons = new List<Button>();

        private Button _btnScan;
        private Button _btnCancel;
        private Button _btnArchive;
        private Button _btnRestore;
        private Button _btnDelete;
        private ComboBox _cbFilter;
        private TextBox _txtSearch;

        private DataGridView _grid;
        private Label _lblDetail;
        private ListBox _lstWorkflows;
        private ToolStripStatusLabel _lblSummary;
        private ToolStripStatusLabel _lblStatus;
        private ToolStripProgressBar _progress;
        private ToolTip _toolTip;

        private readonly List<ModelRow> _rows = new List<ModelRow>();
        private BindingList<ModelRow> _view = new BindingList<ModelRow>();
        private ScanReport _report;

        private CancellationTokenSource _cts;
        private bool _busy;
        private int _sortColumn = 1;
        private bool _sortAscending = true;
        private bool _suppressSummary;
        private Font _boldFont;
        private CheckBox _headerCheck;
        private bool _syncingHeaderCheck;

        public MainForm()
        {
            _iniPath = Path.Combine(Application.StartupPath, "ComfyUI Model Manager.ini");

            Text = Lang.T("app.title");
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            ClientSize = new Size(1080, 740);
            MinimumSize = new Size(840, 580);
            StartPosition = FormStartPosition.CenterScreen;

            _boldFont = new Font(Font, FontStyle.Bold);
            ApplyEmbeddedIcon();

            KeyPreview = true;
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.F1)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    ShowAbout();
                }
            };

            BuildUi();
            LoadSettings();

            ApplyFilter();
            UpdateDetails();
            _lblStatus.Text = Lang.T("app.ready");
            FormClosing += Form_FormClosing;
            FormClosed += delegate
            {
                if (_boldFont != null) _boldFont.Dispose();
                if (_toolTip != null) _toolTip.Dispose();
                if (Icon != null) Icon.Dispose();
            };
        }

        // ------------------------------------------------------------------ UI

        private void ApplyEmbeddedIcon()
        {
            this.Icon = LoadAppIcon(SystemInformation.SmallIconSize);
        }

        private static System.Drawing.Icon LoadAppIcon(Size size)
        {
            try
            {
                Stream stream = typeof(MainForm).Assembly.GetManifestResourceStream("ComfyUI.ModelManager.AppIcon.ico");
                if (stream != null)
                {
                    using (stream) return new System.Drawing.Icon(stream, size);
                }
            }
            catch
            {
                // faller tillbaka på exe-filens ikon nedan
            }

            try
            {
                System.Drawing.Icon extracted = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (extracted != null) return extracted;
            }
            catch
            {
                // standardikonen duger
            }

            return (System.Drawing.Icon)SystemIcons.Application.Clone();
        }

        private void BuildUi()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 5;
            root.Padding = new Padding(10, 8, 10, 0);
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            // -- mappval
            TableLayoutPanel folders = new TableLayoutPanel();
            folders.Dock = DockStyle.Fill;
            folders.ColumnCount = 3;
            folders.RowCount = 3;
            folders.Margin = new Padding(0, 0, 0, 4);
            folders.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
            folders.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            folders.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104F));
            folders.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            folders.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            folders.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            root.Controls.Add(folders, 0, 0);

            _txtModels = AddFolderRow(folders, 0, Lang.T("label.models"), BrowseModels);
            _txtWorkflows = AddFolderRow(folders, 1, Lang.T("label.workflows"), BrowseWorkflows);
            _txtArchive = AddFolderRow(folders, 2, Lang.T("label.archive"), BrowseArchive);

            // -- knapprad
            TableLayoutPanel buttonRow = new TableLayoutPanel();
            buttonRow.Dock = DockStyle.Fill;
            buttonRow.ColumnCount = 2;
            buttonRow.RowCount = 1;
            buttonRow.Margin = new Padding(0, 0, 0, 2);
            buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            buttonRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.Controls.Add(buttonRow, 0, 1);

            FlowLayoutPanel leftTools = new FlowLayoutPanel();
            leftTools.Dock = DockStyle.Fill;
            leftTools.WrapContents = false;
            leftTools.Margin = new Padding(0);
            buttonRow.Controls.Add(leftTools, 0, 0);

            _btnScan = MakeButton(Lang.T("button.scan"), 100);
            _btnScan.Click += BtnScan_Click;
            leftTools.Controls.Add(_btnScan);

            _btnCancel = MakeButton(Lang.T("button.cancel"), 84);
            _btnCancel.Enabled = false;
            _btnCancel.Click += BtnCancel_Click;
            leftTools.Controls.Add(_btnCancel);

            _btnArchive = MakeButton(Lang.T("button.archive"), 110);
            _btnArchive.Enabled = false;
            _btnArchive.Click += BtnArchive_Click;
            leftTools.Controls.Add(_btnArchive);

            _btnRestore = MakeButton(Lang.T("button.restore"), 110);
            _btnRestore.Enabled = false;
            _btnRestore.Click += BtnRestore_Click;
            leftTools.Controls.Add(_btnRestore);

            _btnDelete = MakeButton(Lang.T("button.delete"), 100);
            _btnDelete.Enabled = false;
            _btnDelete.Click += BtnDelete_Click;
            leftTools.Controls.Add(_btnDelete);

            FlowLayoutPanel helpPanel = new FlowLayoutPanel();
            helpPanel.AutoSize = true;
            helpPanel.WrapContents = false;
            helpPanel.Margin = new Padding(0);
            helpPanel.Anchor = AnchorStyles.Right;
            buttonRow.Controls.Add(helpPanel, 1, 0);

            _toolTip = new ToolTip();
            _toolTip.SetToolTip(_btnScan, Lang.T("tooltip.scan"));
            _toolTip.SetToolTip(_btnArchive, Lang.T("tooltip.archive"));
            _toolTip.SetToolTip(_btnRestore, Lang.T("tooltip.restore"));
            _toolTip.SetToolTip(_btnDelete, Lang.T("tooltip.delete"));
            Button aboutButton = new Button();
            aboutButton.Text = "?";
            aboutButton.Font = new Font(Font.FontFamily, 10F, FontStyle.Bold);
            aboutButton.Size = new Size(28, 26);
            aboutButton.Margin = new Padding(0, 0, 0, 0);
            aboutButton.TabStop = false;
            aboutButton.Click += delegate { ShowAbout(); };
            _toolTip.SetToolTip(aboutButton, Lang.T("tooltip.about"));
            helpPanel.Controls.Add(aboutButton);

            // -- filterrad
            TableLayoutPanel filterRow = new TableLayoutPanel();
            filterRow.Dock = DockStyle.Fill;
            filterRow.ColumnCount = 2;
            filterRow.RowCount = 1;
            filterRow.Margin = new Padding(0, 0, 0, 4);
            filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            filterRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.Controls.Add(filterRow, 0, 2);

            Label hintLabel = new Label();
            hintLabel.Text = Lang.T("hint.selectAll");
            hintLabel.AutoSize = false;
            hintLabel.Dock = DockStyle.Fill;
            hintLabel.AutoEllipsis = true;
            hintLabel.TextAlign = ContentAlignment.MiddleLeft;
            hintLabel.ForeColor = SystemColors.GrayText;
            hintLabel.Margin = new Padding(0, 6, 10, 0);
            filterRow.Controls.Add(hintLabel, 0, 0);

            FlowLayoutPanel rightTools = new FlowLayoutPanel();
            rightTools.AutoSize = true;
            rightTools.WrapContents = false;
            rightTools.Margin = new Padding(0);
            rightTools.Anchor = AnchorStyles.Right;
            filterRow.Controls.Add(rightTools, 1, 0);

            Label filterLabel = new Label();
            filterLabel.Text = Lang.T("label.filter");
            filterLabel.AutoSize = true;
            filterLabel.Margin = new Padding(0, 9, 4, 0);
            rightTools.Controls.Add(filterLabel);

            _cbFilter = new ComboBox();
            _cbFilter.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbFilter.Width = 104;
            _cbFilter.Margin = new Padding(0, 6, 10, 0);
            _cbFilter.Items.AddRange(new object[]
            {
                Lang.T("filter.all"),
                Lang.T("filter.used"),
                Lang.T("filter.unused"),
                Lang.T("filter.archived"),
                Lang.T("filter.duplicates")
            });
            _cbFilter.SelectedIndex = 0;
            _cbFilter.SelectedIndexChanged += Filter_Changed;
            rightTools.Controls.Add(_cbFilter);

            Label searchLabel = new Label();
            searchLabel.Text = Lang.T("label.search");
            searchLabel.AutoSize = true;
            searchLabel.Margin = new Padding(0, 9, 4, 0);
            rightTools.Controls.Add(searchLabel);

            _txtSearch = new TextBox();
            _txtSearch.Width = 190;
            _txtSearch.Margin = new Padding(0, 6, 0, 0);
            _txtSearch.TextChanged += Filter_Changed;
            rightTools.Controls.Add(_txtSearch);

            // -- lista + detaljer
            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            split.SplitterWidth = 6;
            split.Panel1MinSize = 180;
            split.Panel2MinSize = 110;
            root.Controls.Add(split, 0, 3);

            _grid = new DataGridView();
            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.RowHeadersVisible = false;
            _grid.BackgroundColor = SystemColors.Window;
            _grid.BorderStyle = BorderStyle.FixedSingle;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.MultiSelect = false;
            _grid.AutoGenerateColumns = false;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.RowTemplate.Height = 24;
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
            _grid.CellFormatting += Grid_CellFormatting;
            _grid.CellValueChanged += Grid_CellValueChanged;
            _grid.CurrentCellDirtyStateChanged += Grid_CurrentCellDirtyStateChanged;
            _grid.CellBeginEdit += Grid_CellBeginEdit;
            _grid.SelectionChanged += Grid_SelectionChanged;
            _grid.ColumnHeaderMouseClick += Grid_ColumnHeaderMouseClick;
            _grid.CellDoubleClick += Grid_CellDoubleClick;
            split.Panel1.Controls.Add(_grid);

            DataGridViewCheckBoxColumn colSelect = new DataGridViewCheckBoxColumn();
            colSelect.HeaderText = "";
            colSelect.DataPropertyName = "Selected";
            colSelect.Width = 34;
            colSelect.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
            colSelect.SortMode = DataGridViewColumnSortMode.NotSortable;
            colSelect.Resizable = DataGridViewTriState.False;
            _grid.Columns.Add(colSelect);

            _grid.Columns.Add(MakeTextColumn(Lang.T("column.fileName"), "FileName", 40, DataGridViewContentAlignment.MiddleLeft));
            _grid.Columns.Add(MakeTextColumn(Lang.T("column.relativePath"), "RelativePath", 40, DataGridViewContentAlignment.MiddleLeft));
            _grid.Columns.Add(MakeTextColumn(Lang.T("column.size"), "Size", 12, DataGridViewContentAlignment.MiddleRight));
            _grid.Columns.Add(MakeTextColumn(Lang.T("column.status"), "StatusText", 12, DataGridViewContentAlignment.MiddleLeft));
            _grid.Columns.Add(MakeTextColumn(Lang.T("column.matches"), "MatchCount", 9, DataGridViewContentAlignment.MiddleCenter));

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(Lang.T("menu.openModelFolder"), null, delegate { OpenModelFolder(); });
            menu.Items.Add(Lang.T("menu.openWorkflow"), null, delegate { OpenWorkflow(_lstWorkflows.SelectedItem as WorkflowItem); });
            _grid.ContextMenuStrip = menu;

            _headerCheck = new CheckBox();
            _headerCheck.AutoSize = false;
            _headerCheck.Size = new Size(15, 15);
            _headerCheck.Text = "";
            _headerCheck.TabStop = false;
            _headerCheck.FlatStyle = FlatStyle.Standard;
            _headerCheck.BackColor = SystemColors.Control;
            _headerCheck.CheckAlign = ContentAlignment.MiddleCenter;
            _headerCheck.CheckedChanged += HeaderCheck_CheckedChanged;
            _grid.Controls.Add(_headerCheck);
            _headerCheck.BringToFront();
            _toolTip.SetToolTip(_headerCheck, Lang.T("tooltip.headerCheck"));

            _grid.SizeChanged += delegate { PositionHeaderCheckbox(); };
            _grid.Scroll += delegate { PositionHeaderCheckbox(); };
            _grid.ColumnWidthChanged += delegate { PositionHeaderCheckbox(); };
            _grid.DataBindingComplete += delegate { PositionHeaderCheckbox(); SyncHeaderCheckbox(); };
            _grid.Layout += delegate { PositionHeaderCheckbox(); };

            TableLayoutPanel details = new TableLayoutPanel();
            details.Dock = DockStyle.Fill;
            details.ColumnCount = 1;
            details.RowCount = 2;
            details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            details.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            split.Panel2.Controls.Add(details);

            _lblDetail = new Label();
            _lblDetail.AutoSize = true;
            _lblDetail.Padding = new Padding(2, 6, 2, 4);
            _lblDetail.Text = Lang.T("details.select");
            details.Controls.Add(_lblDetail, 0, 0);

            GroupBox workflowBox = new GroupBox();
            workflowBox.Text = Lang.T("details.usedBy");
            workflowBox.Dock = DockStyle.Fill;
            details.Controls.Add(workflowBox, 0, 1);

            _lstWorkflows = new ListBox();
            _lstWorkflows.Dock = DockStyle.Fill;
            _lstWorkflows.IntegralHeight = false;
            _lstWorkflows.BorderStyle = BorderStyle.None;
            _lstWorkflows.DoubleClick += delegate { OpenWorkflow(_lstWorkflows.SelectedItem as WorkflowItem); };
            workflowBox.Controls.Add(_lstWorkflows);

            // -- statusrad
            StatusStrip strip = new StatusStrip();
            strip.Dock = DockStyle.Fill;
            strip.SizingGrip = true;
            _lblSummary = new ToolStripStatusLabel();
            _lblSummary.Spring = true;
            _lblSummary.TextAlign = ContentAlignment.MiddleLeft;
            _lblStatus = new ToolStripStatusLabel();
            _lblStatus.TextAlign = ContentAlignment.MiddleRight;
            _progress = new ToolStripProgressBar();
            _progress.Width = 190;
            _progress.Visible = false;
            strip.Items.Add(_lblSummary);
            strip.Items.Add(_lblStatus);
            strip.Items.Add(_progress);
            root.Controls.Add(strip, 0, 4);

            Shown += delegate
            {
                try
                {
                    split.SplitterDistance = Math.Max(split.Panel1MinSize, split.Height - 150);
                }
                catch
                {
                    // layouten kan vara för liten vid start; ignorera
                }

                PositionHeaderCheckbox();
                SyncHeaderCheckbox();
            };
        }

        private static Button MakeButton(string text, int minWidth)
        {
            Button button = new Button();
            button.Text = text;
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.MinimumSize = new Size(minWidth, 28);
            button.Margin = new Padding(0, 2, 6, 0);
            return button;
        }

        private static DataGridViewTextBoxColumn MakeTextColumn(string header, string property, int fillWeight, DataGridViewContentAlignment alignment)
        {
            DataGridViewTextBoxColumn column = new DataGridViewTextBoxColumn();
            column.HeaderText = header;
            column.DataPropertyName = property;
            column.FillWeight = fillWeight;
            column.SortMode = DataGridViewColumnSortMode.Programmatic;
            column.ReadOnly = true;
            column.DefaultCellStyle.Alignment = alignment;
            return column;
        }

        private TextBox AddFolderRow(TableLayoutPanel panel, int row, string caption, EventHandler browseHandler)
        {
            Label label = new Label();
            label.Text = caption;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Dock = DockStyle.Fill;
            label.AutoSize = false;
            label.Margin = new Padding(0, 3, 6, 3);

            TextBox textBox = new TextBox();
            textBox.Dock = DockStyle.Fill;
            textBox.Margin = new Padding(0, 3, 6, 3);
            textBox.TextChanged += delegate { SaveSettings(); };

            Button button = new Button();
            button.Text = Lang.T("button.browse");
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(0, 3, 0, 3);
            button.Click += browseHandler;
            _browseButtons.Add(button);

            panel.Controls.Add(label, 0, row);
            panel.Controls.Add(textBox, 1, row);
            panel.Controls.Add(button, 2, row);
            return textBox;
        }

        // --------------------------------------------------------- inställningar

        private void LoadSettings()
        {
            // Flytta ett gammalt settings.ini om det finns kvar från tidigare version.
            string legacy = Path.Combine(Application.StartupPath, "settings.ini");
            if (!File.Exists(_iniPath) && File.Exists(legacy))
            {
                try { File.Move(legacy, _iniPath); }
                catch { }
            }

            _ini = new IniFile(_iniPath);
            _suppressSummary = true;
            _txtModels.Text = _ini.Get("Folders", "Models", "");
            _txtWorkflows.Text = _ini.Get("Folders", "Workflows", "");
            _txtArchive.Text = _ini.Get("Folders", "Archive", "");
            _suppressSummary = false;
        }

        private void SaveSettings()
        {
            if (_ini == null || _suppressSummary) return;
            _ini.Set("Folders", "Models", _txtModels.Text.Trim());
            _ini.Set("Folders", "Workflows", _txtWorkflows.Text.Trim());
            _ini.Set("Folders", "Archive", _txtArchive.Text.Trim());
            _ini.Save();
        }

        private void Form_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveSettings();
            if (_cts != null)
            {
                try { _cts.Cancel(); }
                catch { }
            }
        }

        // -------------------------------------------------------------- mappval

        private void BrowseModels(object sender, EventArgs e)
        {
            string path = PickFolder(_txtModels.Text);
            if (path != null) { _txtModels.Text = path; SaveSettings(); }
        }

        private void BrowseWorkflows(object sender, EventArgs e)
        {
            string path = PickFolder(_txtWorkflows.Text);
            if (path != null) { _txtWorkflows.Text = path; SaveSettings(); }
        }

        private void BrowseArchive(object sender, EventArgs e)
        {
            string path = PickFolder(_txtArchive.Text);
            if (path != null) { _txtArchive.Text = path; SaveSettings(); }
        }

        private string PickFolder(string current)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = Lang.T("validation.selectFolder");
                dialog.ShowNewFolderButton = true;
                if (!string.IsNullOrEmpty(current) && Directory.Exists(current)) dialog.SelectedPath = current;
                if (dialog.ShowDialog(this) == DialogResult.OK) return dialog.SelectedPath;
            }
            return null;
        }

        // --------------------------------------------------------------- skanning

        private async void BtnScan_Click(object sender, EventArgs e)
        {
            await ScanAsync();
        }

        private async Task<bool> ScanAsync()
        {
            if (!ValidateFolders(false)) return false;

            SaveSettings();
            _cts = new CancellationTokenSource();
            SetBusy(true);

            _lblStatus.Text = Lang.T("status.scanning");
            _progress.Value = 0;

            string modelsRoot = _txtModels.Text.Trim();
            string workflowsRoot = _txtWorkflows.Text.Trim();
            string archiveRoot = _txtArchive.Text.Trim();
            CancellationToken token = _cts.Token;
            Progress<ScanProgress> progress = new Progress<ScanProgress>(OnProgress);

            bool success = false;
            try
            {
                ScanReport report = await Task.Run(delegate
                {
                    return FileScan.Scan(modelsRoot, workflowsRoot, archiveRoot, progress, token);
                });

                _report = report;
                PopulateFromReport(report);
                _progress.Value = 100;
                _lblStatus.Text = Lang.F("status.scanDone",
                    report.Models.Count, report.WorkflowFiles.Count, report.Duration.TotalSeconds);

                if (report.Errors.Count > 0) ShowErrorList(report.Errors);
                success = true;
            }
            catch (OperationCanceledException)
            {
                _lblStatus.Text = Lang.T("status.scanCancelled");
            }
            catch (Exception ex)
            {
                _lblStatus.Text = Lang.T("status.scanError");
                MessageBox.Show(this, ex.Message, Lang.T("error.scanTitle"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
                if (_cts != null)
                {
                    _cts.Dispose();
                    _cts = null;
                }
                _progress.Visible = false;
                UpdateSummary();
            }

            return success;
        }

        private void OnProgress(ScanProgress progress)
        {
            int percent = Math.Max(0, Math.Min(100, progress.Percent));
            if (_progress.Style != ProgressBarStyle.Continuous) _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = percent;
            if (!string.IsNullOrEmpty(progress.Message)) _lblStatus.Text = progress.Message;
        }

        private void PopulateFromReport(ScanReport report)
        {
            _rows.Clear();
            foreach (ModelFile model in report.Models)
            {
                ModelRow row = new ModelRow();
                row.Model = model;
                row.Selected = false;
                _rows.Add(row);
            }
            SortRows();
            ApplyFilter();
            if (_grid.Rows.Count > 0)
            {
                _grid.ClearSelection();
                _grid.Rows[0].Selected = true;
                _grid.CurrentCell = _grid.Rows[0].Cells[1];
            }
        }

        // ------------------------------------------------------------------ arkiv

        private async void BtnArchive_Click(object sender, EventArgs e)
        {
            List<ModelRow> marked = new List<ModelRow>();
            foreach (ModelRow row in _rows)
                if (row.Selected && !row.Model.Used && !row.Model.IsArchived) marked.Add(row);

            if (marked.Count == 0)
            {
                MessageBox.Show(this, Lang.T("status.noFileSelected"), Lang.T("button.archive"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!ValidateFolders(true)) return;

            string modelsRoot = _txtModels.Text.Trim();
            string archiveRoot = _txtArchive.Text.Trim();

            long bytes = 0;
            foreach (ModelRow row in marked) bytes += row.Model.Size;

            string extra = "";
            if (!string.Equals(Path.GetPathRoot(archiveRoot), Path.GetPathRoot(modelsRoot), StringComparison.OrdinalIgnoreCase))
                extra = Environment.NewLine + Environment.NewLine + Lang.T("archive.confirmExtra");

            string message = Lang.F("archive.confirm",
                marked.Count, FormatSize(bytes), modelsRoot, archiveRoot) + extra;

            DialogResult answer = MessageBox.Show(this, message, Lang.T("archive.confirmTitle"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            SaveSettings();
            _cts = new CancellationTokenSource();
            SetBusy(true);
            _lblStatus.Text = Lang.T("status.archiving");
            _progress.Value = 0;

            List<ModelFile> files = new List<ModelFile>();
            foreach (ModelRow row in marked) files.Add(row.Model);

            CancellationToken token = _cts.Token;
            Progress<ArchiveProgress> progress = new Progress<ArchiveProgress>(delegate(ArchiveProgress p)
            {
                _progress.Value = Math.Max(0, Math.Min(100, p.Percent));
                if (!string.IsNullOrEmpty(p.Message)) _lblStatus.Text = p.Message;
            });

            try
            {
                ArchiveReport report = await Task.Run(delegate
                {
                    return FileScan.Archive(files, modelsRoot, archiveRoot, progress, token);
                });

                StringBuilder sb = new StringBuilder();
                sb.Append(Lang.F("archive.done", report.Moved.Count, FormatSize(report.BytesMoved)));
                if (report.Failures.Count > 0)
                {
                    sb.Append(Environment.NewLine).Append(Environment.NewLine)
                      .Append(Lang.F("result.failures", report.Failures.Count));
                    int shown = Math.Min(10, report.Failures.Count);
                    for (int i = 0; i < shown; i++) sb.Append(Environment.NewLine).Append(" · ").Append(report.Failures[i]);
                    if (report.Failures.Count > shown) sb.Append(Environment.NewLine).Append(" ").Append(Lang.T("result.more"));
                }
                MessageBox.Show(this, sb.ToString(), Lang.T("archive.doneTitle"),
                    MessageBoxButtons.OK, report.Failures.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
            catch (OperationCanceledException)
            {
                _lblStatus.Text = Lang.T("status.archiveCancelled");
            }
            catch (Exception ex)
            {
                _lblStatus.Text = Lang.T("status.archiveError");
                MessageBox.Show(this, ex.Message, Lang.T("error.archiveTitle"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
                if (_cts != null)
                {
                    _cts.Dispose();
                    _cts = null;
                }
                _progress.Visible = false;
            }

            // Filerna kan ha flyttats även om något gick fel eller avbröts: skanna alltid om.
            await ScanAsync();
        }

        private async void BtnDelete_Click(object sender, EventArgs e)
        {
            List<ModelRow> marked = new List<ModelRow>();
            foreach (ModelRow row in _rows)
                if (row.Selected && row.Model.IsDuplicate) marked.Add(row);

            if (marked.Count == 0)
            {
                MessageBox.Show(this, Lang.T("status.noDuplicateSelected"), Lang.T("delete.doneTitle"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!ValidateFolders(true)) return;

            string modelsRoot = _txtModels.Text.Trim();
            string archiveRoot = _txtArchive.Text.Trim();

            long bytes = 0;
            foreach (ModelRow row in marked) bytes += row.Model.Size;

            StringBuilder examples = new StringBuilder();
            int exampleCount = Math.Min(10, marked.Count);
            for (int i = 0; i < exampleCount; i++)
                examples.Append("\r\n · ").Append(marked[i].Model.RelativePath);
            if (marked.Count > exampleCount) examples.Append("\r\n …");

            string message = Lang.F("delete.confirm", marked.Count, FormatSize(bytes)) + examples.ToString();

            DialogResult answer = MessageBox.Show(this, message, Lang.T("delete.confirmTitle"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            SaveSettings();
            _cts = new CancellationTokenSource();
            SetBusy(true);
            _lblStatus.Text = Lang.T("status.deleting");
            _progress.Value = 0;

            List<ModelFile> files = new List<ModelFile>();
            foreach (ModelRow row in marked) files.Add(row.Model);

            CancellationToken token = _cts.Token;
            Progress<ArchiveProgress> progress = new Progress<ArchiveProgress>(delegate(ArchiveProgress p)
            {
                _progress.Value = Math.Max(0, Math.Min(100, p.Percent));
                if (!string.IsNullOrEmpty(p.Message)) _lblStatus.Text = p.Message;
            });

            try
            {
                DeleteReport report = await Task.Run(delegate
                {
                    return FileScan.DeleteDuplicates(files, modelsRoot, archiveRoot, progress, token);
                });

                StringBuilder sb = new StringBuilder();
                sb.Append(Lang.F("delete.done", report.Deleted.Count, FormatSize(report.BytesDeleted)));
                if (report.Failures.Count > 0)
                {
                    sb.Append(Environment.NewLine).Append(Environment.NewLine)
                      .Append(Lang.F("result.failures", report.Failures.Count));
                    int failureCount = Math.Min(10, report.Failures.Count);
                    for (int i = 0; i < failureCount; i++) sb.Append(Environment.NewLine).Append(" · ").Append(report.Failures[i]);
                    if (report.Failures.Count > failureCount) sb.Append(Environment.NewLine).Append(" ").Append(Lang.T("result.more"));
                }
                MessageBox.Show(this, sb.ToString(), Lang.T("delete.doneTitle"),
                    MessageBoxButtons.OK, report.Failures.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
            catch (OperationCanceledException)
            {
                _lblStatus.Text = Lang.T("status.deleteCancelled");
            }
            catch (Exception ex)
            {
                _lblStatus.Text = Lang.T("status.deleteError");
                MessageBox.Show(this, ex.Message, Lang.T("error.deleteTitle"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
                if (_cts != null)
                {
                    _cts.Dispose();
                    _cts = null;
                }
                _progress.Visible = false;
            }

            await ScanAsync();
        }

        private void BtnCancel_Click(object sender, EventArgs e)
        {
            if (_cts == null) return;
            _btnCancel.Enabled = false;
            _lblStatus.Text = Lang.T("status.cancelling");
            try { _cts.Cancel(); }
            catch { }
        }

        // ------------------------------------------------------------ återställning

        private enum RestoreOutcome
        {
            Restore,
            Skip,
            Cancel
        }

        private async void BtnRestore_Click(object sender, EventArgs e)
        {
            List<ModelRow> marked = new List<ModelRow>();
            foreach (ModelRow row in _rows)
                if (row.Selected && row.Model.IsArchived && !row.Model.IsDuplicate) marked.Add(row);

            if (marked.Count == 0)
            {
                MessageBox.Show(this, Lang.T("status.noArchivedSelected"), Lang.T("button.restore"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!ValidateFolders(true)) return;

            string modelsRoot = _txtModels.Text.Trim();
            string archiveRoot = _txtArchive.Text.Trim();

            List<RestoreRequest> requests = new List<RestoreRequest>();
            string lastFolder = null;

            for (int i = 0; i < marked.Count; i++)
            {
                RestoreRequest request;
                RestoreOutcome outcome = ShowRestoreDialog(marked[i].Model, modelsRoot, i + 1, marked.Count, ref lastFolder, out request);

                if (outcome == RestoreOutcome.Cancel)
                {
                    _lblStatus.Text = Lang.T("status.restoreCancelled");
                    return;
                }
                if (outcome == RestoreOutcome.Restore)
                {
                    requests.Add(request);
                    if (!request.KeepStructure) lastFolder = request.TargetFolder;
                }
            }

            if (requests.Count == 0)
            {
                _lblStatus.Text = Lang.T("status.noRestoreChoices");
                return;
            }

            SaveSettings();
            _cts = new CancellationTokenSource();
            SetBusy(true);
            _lblStatus.Text = Lang.T("status.restoring");
            _progress.Value = 0;

            CancellationToken token = _cts.Token;
            Progress<ArchiveProgress> progress = new Progress<ArchiveProgress>(delegate(ArchiveProgress p)
            {
                _progress.Value = Math.Max(0, Math.Min(100, p.Percent));
                if (!string.IsNullOrEmpty(p.Message)) _lblStatus.Text = p.Message;
            });

            try
            {
                ArchiveReport report = await Task.Run(delegate
                {
                    return FileScan.Restore(requests, modelsRoot, archiveRoot, progress, token);
                });

                StringBuilder sb = new StringBuilder();
                sb.Append(Lang.F("restore.done", report.Moved.Count, FormatSize(report.BytesMoved)));
                if (report.Failures.Count > 0)
                {
                    sb.Append(Environment.NewLine).Append(Environment.NewLine)
                      .Append(Lang.F("result.failures", report.Failures.Count));
                    int shown = Math.Min(10, report.Failures.Count);
                    for (int i = 0; i < shown; i++) sb.Append(Environment.NewLine).Append(" · ").Append(report.Failures[i]);
                    if (report.Failures.Count > shown) sb.Append(Environment.NewLine).Append(" ").Append(Lang.T("result.more"));
                }
                MessageBox.Show(this, sb.ToString(), Lang.T("restore.doneTitle"),
                    MessageBoxButtons.OK, report.Failures.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
            catch (OperationCanceledException)
            {
                _lblStatus.Text = Lang.T("status.restoreCancelled");
            }
            catch (Exception ex)
            {
                _lblStatus.Text = Lang.T("status.restoreError");
                MessageBox.Show(this, ex.Message, Lang.T("error.restoreTitle"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
                if (_cts != null)
                {
                    _cts.Dispose();
                    _cts = null;
                }
                _progress.Visible = false;
            }

            await ScanAsync();
        }

        private RestoreOutcome ShowRestoreDialog(ModelFile file, string modelsRoot, int index, int total, ref string lastFolder, out RestoreRequest request)
        {
            request = null;
            RestoreOutcome outcome = RestoreOutcome.Cancel;

            string structureTarget = FileScan.GetRestoreTarget(file, modelsRoot, true, null);
            string defaultFolder = lastFolder;
            if (string.IsNullOrEmpty(defaultFolder) || !Directory.Exists(Paths.Long(defaultFolder)))
            {
                string parent = Path.GetDirectoryName(structureTarget);
                defaultFolder = (!string.IsNullOrEmpty(parent) && Directory.Exists(Paths.Long(parent))) ? parent : modelsRoot;
            }

            Font titleFont = new Font(Font.FontFamily, 11F, FontStyle.Bold);

            using (Form dialog = new Form())
            {
                dialog.Text = Lang.T("restore.dialogTitle");
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.ClientSize = new Size(640, 370);
                dialog.Font = Font;
                dialog.Icon = Icon;
                dialog.FormClosed += delegate { titleFont.Dispose(); };

                TableLayoutPanel layout = new TableLayoutPanel();
                layout.Dock = DockStyle.Fill;
                layout.Padding = new Padding(22, 18, 22, 14);
                layout.ColumnCount = 1;
                layout.RowCount = 11;
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
                for (int i = 0; i < 9; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                dialog.Controls.Add(layout);

                Label title = new Label();
                title.Text = total == 1
                    ? Lang.T("restore.dialogTitle")
                    : Lang.F("restore.dialogTitleIndexed", index, total);
                title.Font = titleFont;
                title.AutoSize = true;
                title.Margin = new Padding(0, 0, 0, 6);
                layout.Controls.Add(title, 0, 0);

                Label fileInfo = new Label();
                fileInfo.Text = Lang.F("restore.fileInfo", file.FileName, FormatSize(file.Size));
                fileInfo.AutoSize = true;
                fileInfo.Margin = new Padding(0, 0, 0, 2);
                layout.Controls.Add(fileInfo, 0, 1);

                Label archiveInfo = new Label();
                archiveInfo.Text = Lang.F("restore.archiveInfo", file.RelativePath);
                archiveInfo.AutoSize = true;
                archiveInfo.MaximumSize = new Size(560, 0);
                archiveInfo.ForeColor = SystemColors.GrayText;
                archiveInfo.Margin = new Padding(0, 0, 0, 14);
                layout.Controls.Add(archiveInfo, 0, 2);

                RadioButton rbStructure = new RadioButton();
                rbStructure.Text = Lang.T("restore.structure");
                rbStructure.AutoSize = true;
                rbStructure.Checked = true;
                rbStructure.Margin = new Padding(0, 0, 0, 2);
                layout.Controls.Add(rbStructure, 0, 3);

                Label structureLabel = new Label();
                structureLabel.Text = Lang.F("restore.structureTarget", structureTarget);
                structureLabel.AutoSize = true;
                structureLabel.MaximumSize = new Size(560, 0);
                structureLabel.ForeColor = SystemColors.GrayText;
                structureLabel.Margin = new Padding(22, 0, 0, 10);
                layout.Controls.Add(structureLabel, 0, 4);

                RadioButton rbFolder = new RadioButton();
                rbFolder.Text = Lang.T("restore.folder");
                rbFolder.AutoSize = true;
                rbFolder.Margin = new Padding(0, 0, 0, 4);
                layout.Controls.Add(rbFolder, 0, 5);

                TableLayoutPanel folderPanel = new TableLayoutPanel();
                folderPanel.Dock = DockStyle.Fill;
                folderPanel.ColumnCount = 2;
                folderPanel.RowCount = 1;
                folderPanel.Height = 28;
                folderPanel.Margin = new Padding(22, 0, 0, 10);
                folderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
                folderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120F));
                folderPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
                layout.Controls.Add(folderPanel, 0, 6);

                TextBox txtFolder = new TextBox();
                txtFolder.Text = defaultFolder;
                txtFolder.Dock = DockStyle.Fill;
                txtFolder.Margin = new Padding(0, 0, 6, 0);
                folderPanel.Controls.Add(txtFolder, 0, 0);

                Button btnBrowse = new Button();
                btnBrowse.Text = Lang.T("button.browse");
                btnBrowse.Dock = DockStyle.Fill;
                btnBrowse.Margin = new Padding(0);
                folderPanel.Controls.Add(btnBrowse, 1, 0);

                Label lblTarget = new Label();
                lblTarget.Text = Lang.F("restore.target", structureTarget);
                lblTarget.AutoSize = true;
                lblTarget.MaximumSize = new Size(580, 0);
                lblTarget.Font = _boldFont;
                lblTarget.Margin = new Padding(0, 0, 0, 4);
                layout.Controls.Add(lblTarget, 0, 7);

                Label lblWarning = new Label();
                lblWarning.AutoSize = true;
                lblWarning.MaximumSize = new Size(580, 0);
                lblWarning.ForeColor = Color.FromArgb(165, 30, 30);
                lblWarning.Visible = false;
                lblWarning.Margin = new Padding(0, 0, 0, 4);
                layout.Controls.Add(lblWarning, 0, 8);

                FlowLayoutPanel buttons = new FlowLayoutPanel();
                buttons.Dock = DockStyle.Fill;
                buttons.FlowDirection = FlowDirection.RightToLeft;
                buttons.WrapContents = false;
                buttons.AutoSize = true;
                buttons.Margin = new Padding(0, 10, 0, 0);
                layout.Controls.Add(buttons, 0, 10);

                Button btnRestore = new Button();
                btnRestore.Text = Lang.T("button.restore");
                btnRestore.AutoSize = true;
                btnRestore.MinimumSize = new Size(100, 28);
                btnRestore.Margin = new Padding(6, 0, 0, 0);
                buttons.Controls.Add(btnRestore);

                Button btnSkip = new Button();
                btnSkip.Text = Lang.T("button.skip");
                btnSkip.AutoSize = true;
                btnSkip.MinimumSize = new Size(110, 28);
                btnSkip.Margin = new Padding(6, 0, 0, 0);
                buttons.Controls.Add(btnSkip);

                Button btnCancel = new Button();
                btnCancel.Text = Lang.T("button.cancel");
                btnCancel.AutoSize = true;
                btnCancel.MinimumSize = new Size(90, 28);
                btnCancel.DialogResult = DialogResult.Cancel;
                btnCancel.Margin = new Padding(6, 0, 0, 0);
                buttons.Controls.Add(btnCancel);

                EventHandler refresh = delegate
                {
                    bool folderMode = rbFolder.Checked;
                    txtFolder.Enabled = folderMode;
                    btnBrowse.Enabled = folderMode;

                    string target = null;
                    string warning = null;

                    if (folderMode)
                    {
                        string folder = txtFolder.Text.Trim();
                        if (folder.Length == 0)
                            warning = Lang.T("restore.warningChoose");
                        else if (!FileScan.IsValidRestoreFolder(folder, modelsRoot))
                            warning = Lang.T("restore.warningOutside");
                        else
                            target = FileScan.GetRestoreTarget(file, modelsRoot, false, folder);
                    }
                    else
                    {
                        target = structureTarget;
                    }

                    bool exists = false;
                    if (target != null)
                    {
                        try
                        {
                            exists = File.Exists(Paths.Long(target));
                        }
                        catch
                        {
                            warning = Lang.T("restore.warningInvalid");
                        }
                    }

                    if (target == null)
                    {
                        lblTarget.Text = Lang.T("restore.targetEmpty");
                    }
                    else
                    {
                        lblTarget.Text = Lang.F("restore.target", target);
                        if (exists)
                            warning = Lang.T("restore.warningExists");
                    }

                    btnRestore.Enabled = target != null && !exists && warning == null;
                    lblWarning.Text = warning == null ? "" : warning;
                    lblWarning.Visible = warning != null;
                };

                rbStructure.CheckedChanged += refresh;
                rbFolder.CheckedChanged += refresh;
                txtFolder.TextChanged += refresh;

                btnBrowse.Click += delegate
                {
                    using (FolderBrowserDialog picker = new FolderBrowserDialog())
                    {
                        picker.Description = Lang.T("restore.folderPicker");
                        picker.ShowNewFolderButton = true;
                        string current = txtFolder.Text.Trim();
                        if (current.Length > 0 && Directory.Exists(Paths.Long(current))) picker.SelectedPath = current;
                        else picker.SelectedPath = modelsRoot;

                        if (picker.ShowDialog(dialog) != DialogResult.OK) return;

                        if (!FileScan.IsValidRestoreFolder(picker.SelectedPath, modelsRoot))
                        {
                            MessageBox.Show(dialog, Lang.T("restore.invalidFolder"),
                                Lang.T("restore.invalidFolderTitle"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        txtFolder.Text = picker.SelectedPath;
                    }
                };

                btnRestore.Click += delegate
                {
                    if (!btnRestore.Enabled) return;
                    outcome = RestoreOutcome.Restore;
                    dialog.DialogResult = DialogResult.OK;
                    dialog.Close();
                };

                btnSkip.Click += delegate
                {
                    outcome = RestoreOutcome.Skip;
                    dialog.DialogResult = DialogResult.Ignore;
                    dialog.Close();
                };

                dialog.AcceptButton = btnRestore;
                dialog.CancelButton = btnCancel;

                refresh(null, EventArgs.Empty);

                dialog.ShowDialog(this);

                if (outcome == RestoreOutcome.Restore)
                {
                    request = new RestoreRequest();
                    request.File = file;
                    request.KeepStructure = rbStructure.Checked;
                    if (!request.KeepStructure) request.TargetFolder = txtFolder.Text.Trim();
                }
            }

            return outcome;
        }

        // ------------------------------------------------------------------ lista

        private void ApplyFilter()
        {
            string query = _txtSearch.Text.Trim();
            int mode = _cbFilter.SelectedIndex;

            List<ModelRow> filtered = new List<ModelRow>();
            foreach (ModelRow row in _rows)
            {
                if (mode == 1 && (row.Model.IsArchived || !row.Model.Used)) continue;
                if (mode == 2 && (row.Model.IsArchived || row.Model.Used)) continue;
                if (mode == 3 && (!row.Model.IsArchived || row.Model.IsDuplicate)) continue;
                if (mode == 4 && !row.Model.IsDuplicate) continue;
                if (query.Length > 0 &&
                    row.Model.FileName.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 &&
                    row.Model.RelativePath.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                filtered.Add(row);
            }

            _view = new BindingList<ModelRow>(filtered);
            _grid.DataSource = _view;

            if (_sortColumn >= 0 && _sortColumn < _grid.Columns.Count)
                _grid.Columns[_sortColumn].HeaderCell.SortGlyphDirection =
                    _sortAscending ? SortOrder.Ascending : SortOrder.Descending;

            PositionHeaderCheckbox();
            UpdateSummary();
            UpdateDetails();
        }

        private void Filter_Changed(object sender, EventArgs e)
        {
            ApplyFilter();
        }

        private void SortRows()
        {
            Comparison<ModelRow> comparison;
            switch (_sortColumn)
            {
                case 2:
                    comparison = delegate(ModelRow a, ModelRow b)
                    { return string.Compare(a.Model.RelativePath, b.Model.RelativePath, StringComparison.OrdinalIgnoreCase); };
                    break;
                case 3:
                    comparison = delegate(ModelRow a, ModelRow b) { return a.Model.Size.CompareTo(b.Model.Size); };
                    break;
                case 4:
                    comparison = delegate(ModelRow a, ModelRow b)
                    { return a.StatusRank.CompareTo(b.StatusRank); };
                    break;
                case 5:
                    comparison = delegate(ModelRow a, ModelRow b)
                    { return a.Model.MatchedWorkflows.Count.CompareTo(b.Model.MatchedWorkflows.Count); };
                    break;
                default:
                    comparison = delegate(ModelRow a, ModelRow b)
                    { return string.Compare(a.Model.FileName, b.Model.FileName, StringComparison.OrdinalIgnoreCase); };
                    break;
            }

            _rows.Sort(comparison);
            if (!_sortAscending) _rows.Reverse();
        }

        private void Grid_ColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex <= 0) return;

            if (_sortColumn == e.ColumnIndex) _sortAscending = !_sortAscending;
            else
            {
                _sortColumn = e.ColumnIndex;
                _sortAscending = true;
            }

            foreach (DataGridViewColumn column in _grid.Columns)
                column.HeaderCell.SortGlyphDirection = SortOrder.None;

            SortRows();
            ApplyFilter();
        }

        private void GetHeaderSelectionState(out bool hasSelectable, out bool allSelected)
        {
            hasSelectable = false;
            allSelected = true;

            foreach (DataGridViewRow gridRow in _grid.Rows)
            {
                ModelRow row = gridRow.DataBoundItem as ModelRow;
                if (row == null || !CanMark(row)) continue;

                hasSelectable = true;
                if (!row.Selected)
                {
                    allSelected = false;
                    break;
                }
            }

            if (!hasSelectable) allSelected = false;
        }

        private void HeaderCheck_CheckedChanged(object sender, EventArgs e)
        {
            if (_syncingHeaderCheck || _grid == null) return;

            bool select = _headerCheck.Checked;
            foreach (DataGridViewRow gridRow in _grid.Rows)
            {
                ModelRow row = gridRow.DataBoundItem as ModelRow;
                if (row == null || !CanMark(row)) continue;
                row.Selected = select;
            }

            _view.ResetBindings();
            UpdateSummary();
        }

        private void SyncHeaderCheckbox()
        {
            if (_headerCheck == null) return;

            bool hasSelectable;
            bool allSelected;
            GetHeaderSelectionState(out hasSelectable, out allSelected);

            _syncingHeaderCheck = true;
            _headerCheck.Enabled = hasSelectable;
            _headerCheck.Checked = hasSelectable && allSelected;
            _syncingHeaderCheck = false;
        }

        private void PositionHeaderCheckbox()
        {
            if (_grid == null || _headerCheck == null || _grid.Columns.Count == 0) return;

            Rectangle cell;
            try
            {
                cell = _grid.GetCellDisplayRectangle(0, -1, true);
            }
            catch
            {
                return;
            }

            if (cell.Width <= 0 || cell.Height <= 0)
            {
                _headerCheck.Visible = false;
                return;
            }

            const int size = 15;
            Point location = new Point(
                cell.Left + (cell.Width - size) / 2,
                cell.Top + (cell.Height - size) / 2);

            if (_headerCheck.Location != location) _headerCheck.Location = location;
            if (_headerCheck.Width != size || _headerCheck.Height != size)
                _headerCheck.Size = new Size(size, size);
            if (!_headerCheck.Visible) _headerCheck.Visible = true;
            _headerCheck.BringToFront();
        }

        private void Grid_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0) return;
            ModelRow row = _grid.Rows[e.RowIndex].DataBoundItem as ModelRow;
            if (row == null) return;

            if (e.ColumnIndex == 3 && e.Value is long)
            {
                e.Value = FormatSize((long)e.Value);
                e.FormattingApplied = true;
                return;
            }

            if (e.ColumnIndex == 4)
            {
                if (row.Model.IsDuplicate)
                    e.CellStyle.ForeColor = Color.FromArgb(185, 95, 0);
                else if (row.Model.IsArchived)
                    e.CellStyle.ForeColor = Color.FromArgb(0, 70, 160);
                else if (row.Model.Used)
                    e.CellStyle.ForeColor = Color.FromArgb(0, 110, 60);
                else
                    e.CellStyle.ForeColor = Color.FromArgb(165, 30, 30);
                e.CellStyle.Font = _boldFont;
            }

            if (e.ColumnIndex == 0 && !CanMark(row))
            {
                e.CellStyle.BackColor = SystemColors.Control;
            }
        }

        private static bool CanMark(ModelRow row)
        {
            return row.Model.IsArchived || !row.Model.Used;
        }

        private void Grid_CellBeginEdit(object sender, DataGridViewCellCancelEventArgs e)
        {
            if (e.RowIndex < 0) return;
            ModelRow row = _grid.Rows[e.RowIndex].DataBoundItem as ModelRow;
            if (row != null && !CanMark(row)) e.Cancel = true;
        }

        private void Grid_CurrentCellDirtyStateChanged(object sender, EventArgs e)
        {
            if (_grid.IsCurrentCellDirty)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        }

        private void Grid_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (e.ColumnIndex != 0 || e.RowIndex < 0) return;
            ModelRow row = _grid.Rows[e.RowIndex].DataBoundItem as ModelRow;
            if (row == null) return;
            object value = _grid.Rows[e.RowIndex].Cells[0].Value;
            bool isChecked = value is bool && (bool)value;
            if (isChecked && !CanMark(row))
            {
                row.Selected = false;
                _grid.Rows[e.RowIndex].Cells[0].Value = false;
                UpdateSummary();
                return;
            }
            row.Selected = isChecked;
            UpdateSummary();
        }

        private void Grid_SelectionChanged(object sender, EventArgs e)
        {
            UpdateDetails();
        }

        private void Grid_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex == 0) return;
            OpenModelFolder();
        }

        private ModelRow CurrentRow()
        {
            if (_grid.CurrentRow == null) return null;
            return _grid.CurrentRow.DataBoundItem as ModelRow;
        }

        private void UpdateDetails()
        {
            ModelRow row = CurrentRow();
            _lstWorkflows.BeginUpdate();
            try
            {
                _lstWorkflows.Items.Clear();

                if (row == null)
                {
                    _lblDetail.Text = Lang.T("details.select");
                    return;
                }

                string path = row.Model.IsArchived
                    ? Lang.F("details.archivePrefix", row.Model.RelativePath)
                    : row.Model.RelativePath;

                _lblDetail.Text = Lang.F("details.info",
                    row.Model.FileName, path, FormatSize(row.Model.Size), row.StatusText);

                if (row.Model.IsDuplicate && !string.IsNullOrEmpty(row.Model.DuplicateOfRelative))
                    _lblDetail.Text += "   ·   " + Lang.F("details.duplicateOf", row.Model.DuplicateOfRelative);

                foreach (string workflowPath in row.Model.MatchedWorkflows)
                {
                    WorkflowItem item = new WorkflowItem();
                    item.FullPath = workflowPath;
                    item.Display = MakeDisplayPath(workflowPath);
                    _lstWorkflows.Items.Add(item);
                }

                if (row.Model.MatchedWorkflows.Count == 0)
                    _lblDetail.Text += "   ·   " + Lang.T("details.noWorkflow");
            }
            finally
            {
                _lstWorkflows.EndUpdate();
            }
        }

        private string MakeDisplayPath(string fullPath)
        {
            if (_report == null || string.IsNullOrEmpty(_report.WorkflowsRoot)) return fullPath;
            string root = _report.WorkflowsRoot.TrimEnd('\\', '/');
            if (fullPath.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                return fullPath.Substring(root.Length + 1);
            return fullPath;
        }

        private void UpdateSummary()
        {
            int total = _rows.Count;
            int used = 0;
            int unused = 0;
            int duplicates = 0;
            int toArchive = 0;
            int toRestore = 0;
            int toDelete = 0;
            long usedBytes = 0;
            long unusedBytes = 0;
            long duplicateBytes = 0;

            foreach (ModelRow row in _rows)
            {
                if (row.Model.IsDuplicate)
                {
                    duplicates++;
                    duplicateBytes += row.Model.Size;
                    if (row.Selected) toDelete++;
                }
                else if (row.Model.IsArchived)
                {
                    if (row.Selected) toRestore++;
                }
                else if (row.Model.Used)
                {
                    used++;
                    usedBytes += row.Model.Size;
                }
                else
                {
                    unused++;
                    unusedBytes += row.Model.Size;
                    if (row.Selected) toArchive++;
                }
            }

            StringBuilder sb = new StringBuilder();
            sb.Append(Lang.F("summary.base",
                total, Lang.Plural("summary.model", total),
                used, Lang.Plural("summary.used", used), FormatSize(usedBytes),
                unused, Lang.Plural("summary.unused", unused), FormatSize(unusedBytes)));
            if (duplicates > 0)
                sb.Append(Lang.F("summary.duplicatesPart",
                    duplicates, Lang.Plural("summary.duplicate", duplicates), FormatSize(duplicateBytes)));
            if (toArchive > 0) sb.Append(Lang.F("summary.toArchivePart", toArchive));
            if (toRestore > 0) sb.Append(Lang.F("summary.toRestorePart", toRestore));
            if (toDelete > 0) sb.Append(Lang.F("summary.toDeletePart", toDelete));
            _lblSummary.Text = sb.ToString();

            _btnArchive.Text = toArchive > 0 ? Lang.F("button.archiveCount", toArchive) : Lang.T("button.archive");
            _btnRestore.Text = toRestore > 0 ? Lang.F("button.restoreCount", toRestore) : Lang.T("button.restore");
            _btnDelete.Text = toDelete > 0 ? Lang.F("button.deleteCount", toDelete) : Lang.T("button.delete");
            _btnArchive.Enabled = !_busy && toArchive > 0;
            _btnRestore.Enabled = !_busy && toRestore > 0;
            _btnDelete.Enabled = !_busy && toDelete > 0;

            SyncHeaderCheckbox();
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _btnScan.Enabled = !busy;
            _btnCancel.Enabled = busy;
            _txtModels.Enabled = !busy;
            _txtWorkflows.Enabled = !busy;
            _txtArchive.Enabled = !busy;
            foreach (Button button in _browseButtons) button.Enabled = !busy;

            _progress.Visible = busy;
            if (busy)
            {
                _progress.Style = ProgressBarStyle.Continuous;
                _progress.Value = 0;
            }

            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
            UpdateSummary();
        }

        // ------------------------------------------------------------------ övrigt

        private void ShowAbout()
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            string versionText = version.Major.ToString(CultureInfo.InvariantCulture) + "." +
                                 version.Minor.ToString(CultureInfo.InvariantCulture) + "." +
                                 version.Build.ToString(CultureInfo.InvariantCulture);
            if (version.Revision > 0) versionText += "." + version.Revision.ToString(CultureInfo.InvariantCulture);

            string selectedCode = Lang.CurrentCode;
            List<LanguageInfo> languages = Lang.AvailableLanguages();

            using (Form dialog = new Form())
            {
                dialog.Text = Lang.T("app.about");
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.ClientSize = new Size(540, 434);
                dialog.Font = Font;

                TableLayoutPanel layout = new TableLayoutPanel();
                layout.Dock = DockStyle.Fill;
                layout.Padding = new Padding(24, 20, 24, 16);
                layout.ColumnCount = 2;
                layout.RowCount = 8;
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86F));
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72F));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 12F));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                dialog.Controls.Add(layout);

                PictureBox picture = new PictureBox();
                picture.Size = new Size(64, 64);
                picture.SizeMode = PictureBoxSizeMode.Zoom;
                picture.Margin = new Padding(0, 4, 20, 0);
                layout.Controls.Add(picture, 0, 0);

                Panel header = new Panel();
                header.Dock = DockStyle.Fill;
                header.Margin = new Padding(0);
                layout.Controls.Add(header, 1, 0);

                Label appName = new Label();
                appName.Text = Lang.T("app.title");
                appName.Font = new Font(Font.FontFamily, 13F, FontStyle.Bold);
                appName.AutoSize = true;
                appName.Location = new Point(0, 9);
                header.Controls.Add(appName);

                Label appVersion = new Label();
                appVersion.Text = Lang.F("app.version", versionText);
                appVersion.AutoSize = true;
                appVersion.ForeColor = SystemColors.GrayText;
                appVersion.Location = new Point(2, 39);
                header.Controls.Add(appVersion);

                Panel separator = new Panel();
                separator.Height = 1;
                separator.Dock = DockStyle.Top;
                separator.BackColor = SystemColors.ControlDark;
                separator.Margin = new Padding(0, 2, 0, 10);
                layout.Controls.Add(separator, 0, 1);
                layout.SetColumnSpan(separator, 2);

                Label description = new Label();
                description.Text = Lang.T("app.description");
                description.AutoSize = true;
                description.MaximumSize = new Size(492, 0);
                description.Margin = new Padding(0, 4, 0, 6);
                layout.Controls.Add(description, 0, 2);
                layout.SetColumnSpan(description, 2);

                Panel descriptionGap = new Panel();
                descriptionGap.Margin = new Padding(0);
                layout.Controls.Add(descriptionGap, 0, 3);
                layout.SetColumnSpan(descriptionGap, 2);

                TableLayoutPanel languagePanel = new TableLayoutPanel();
                languagePanel.Dock = DockStyle.Fill;
                languagePanel.AutoSize = true;
                languagePanel.ColumnCount = 3;
                languagePanel.RowCount = 1;
                languagePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90F));
                languagePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
                languagePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                languagePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
                languagePanel.Margin = new Padding(0, 4, 0, 4);
                layout.Controls.Add(languagePanel, 0, 4);
                layout.SetColumnSpan(languagePanel, 2);

                Label languageLabel = new Label();
                languageLabel.Text = Lang.T("label.language");
                languageLabel.TextAlign = ContentAlignment.MiddleLeft;
                languageLabel.Dock = DockStyle.Fill;
                languageLabel.Margin = new Padding(0, 0, 6, 0);
                languagePanel.Controls.Add(languageLabel, 0, 0);

                ComboBox languageCombo = new ComboBox();
                languageCombo.DropDownStyle = ComboBoxStyle.DropDownList;
                languageCombo.Dock = DockStyle.Fill;
                languageCombo.DropDownWidth = 260;
                languageCombo.Margin = new Padding(0, 2, 8, 0);
                languageCombo.Items.AddRange(languages.ToArray());
                for (int i = 0; i < languages.Count; i++)
                {
                    if (string.Equals(languages[i].Code, Lang.CurrentCode, StringComparison.OrdinalIgnoreCase))
                    {
                        languageCombo.SelectedIndex = i;
                        break;
                    }
                }
                languagePanel.Controls.Add(languageCombo, 1, 0);

                Button openFolder = new Button();
                openFolder.Text = Lang.T("button.openLanguageFolder");
                openFolder.AutoSize = true;
                openFolder.MinimumSize = new Size(150, 26);
                openFolder.Margin = new Padding(0);
                openFolder.Click += delegate
                {
                    try
                    {
                        Lang.OpenLanguageFolder();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(dialog, ex.Message, Lang.T("validation.openFolderError"),
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                };
                languagePanel.Controls.Add(openFolder, 2, 0);

                Label languageHint = new Label();
                languageHint.Text = Lang.T("about.languageHint");
                languageHint.AutoSize = true;
                languageHint.MaximumSize = new Size(480, 0);
                languageHint.ForeColor = SystemColors.GrayText;
                languageHint.Margin = new Padding(0, 0, 0, 10);
                layout.Controls.Add(languageHint, 0, 5);
                layout.SetColumnSpan(languageHint, 2);

                FlowLayoutPanel credits = new FlowLayoutPanel();
                credits.FlowDirection = FlowDirection.TopDown;
                credits.WrapContents = false;
                credits.AutoSize = true;
                credits.Dock = DockStyle.Fill;
                credits.Margin = new Padding(0, 0, 0, 10);
                layout.Controls.Add(credits, 0, 6);
                layout.SetColumnSpan(credits, 2);

                Label author = new Label();
                author.Text = Lang.T("app.author");
                author.AutoSize = true;
                author.Margin = new Padding(0, 0, 0, 2);
                credits.Controls.Add(author);

                Label copyright = new Label();
                copyright.Text = Lang.T("app.copyright");
                copyright.AutoSize = true;
                copyright.ForeColor = SystemColors.GrayText;
                copyright.Margin = new Padding(0, 0, 0, 2);
                credits.Controls.Add(copyright);

                LinkLabel licenseLink = new LinkLabel();
                licenseLink.Text = Lang.T("app.license");
                licenseLink.AutoSize = true;
                licenseLink.LinkBehavior = LinkBehavior.HoverUnderline;
                licenseLink.Margin = new Padding(0);
                licenseLink.LinkClicked += delegate { ShowLicense(); };
                credits.Controls.Add(licenseLink);

                FlowLayoutPanel buttons = new FlowLayoutPanel();
                buttons.FlowDirection = FlowDirection.RightToLeft;
                buttons.WrapContents = false;
                buttons.AutoSize = true;
                buttons.Dock = DockStyle.Fill;
                buttons.Margin = new Padding(0);
                layout.Controls.Add(buttons, 0, 7);
                layout.SetColumnSpan(buttons, 2);

                Button okButton = new Button();
                okButton.Text = Lang.T("button.ok");
                okButton.DialogResult = DialogResult.OK;
                okButton.AutoSize = true;
                okButton.MinimumSize = new Size(86, 28);
                okButton.Margin = new Padding(6, 0, 0, 0);
                buttons.Controls.Add(okButton);

                Button cancelButton = new Button();
                cancelButton.Text = Lang.T("button.cancel");
                cancelButton.DialogResult = DialogResult.Cancel;
                cancelButton.AutoSize = true;
                cancelButton.MinimumSize = new Size(86, 28);
                cancelButton.Margin = new Padding(6, 0, 0, 0);
                buttons.Controls.Add(cancelButton);

                dialog.AcceptButton = okButton;
                dialog.CancelButton = cancelButton;

                // Höjden räknas fram från innehållet i stället för att vara hårdkodad, så att
                // beskrivningen inte lämnar ett stort tomrum ovanför språkvalet.
                layout.PerformLayout();
                int contentHeight = layout.GetPreferredSize(new Size(492, 0)).Height;
                dialog.ClientSize = new Size(540, contentHeight);

                using (System.Drawing.Icon smallIcon = LoadAppIcon(SystemInformation.SmallIconSize))
                using (System.Drawing.Icon appIcon = LoadAppIcon(new Size(64, 64)))
                using (Bitmap iconBitmap = appIcon.ToBitmap())
                {
                    dialog.Icon = smallIcon;
                    picture.Image = iconBitmap;

                    if (dialog.ShowDialog(this) == DialogResult.OK && languageCombo.SelectedItem != null)
                        selectedCode = ((LanguageInfo)languageCombo.SelectedItem).Code;
                }
            }

            if (!string.Equals(selectedCode, Lang.CurrentCode, StringComparison.OrdinalIgnoreCase))
            {
                _ini.Set("General", "Language", selectedCode);
                _ini.Save();

                DialogResult restart = MessageBox.Show(this, Lang.T("about.restartPrompt"), Lang.T("about.restartTitle"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
                if (restart == DialogResult.Yes)
                {
                    try
                    {
                        Application.Restart();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, ex.Message, Lang.T("error.unexpectedTitle"),
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    return;
                }

                _lblStatus.Text = Lang.T("status.languageSaved");
            }
        }

        /// <summary>
        /// Visar licensvillkoren. Texten läses i första hand från den inbäddade
        /// resursen, så den fungerar även om LICENSE-filen inte följer med exe-filen.
        /// </summary>
        private void ShowLicense()
        {
            string text = LoadLicenseText();

            using (Form dialog = new Form())
            {
                dialog.Text = Lang.T("app.licenseTitle");
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.ClientSize = new Size(620, 470);
                dialog.Font = Font;

                TableLayoutPanel layout = new TableLayoutPanel();
                layout.Dock = DockStyle.Fill;
                layout.Padding = new Padding(16, 14, 16, 12);
                layout.ColumnCount = 1;
                layout.RowCount = 2;
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                dialog.Controls.Add(layout);

                TextBox textBox = new TextBox();
                textBox.Multiline = true;
                textBox.ReadOnly = true;
                textBox.TabStop = false;
                textBox.ScrollBars = ScrollBars.Vertical;
                textBox.WordWrap = true;
                textBox.Dock = DockStyle.Fill;
                textBox.BackColor = SystemColors.Window;
                textBox.Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);
                textBox.Text = text;
                textBox.Margin = new Padding(0, 0, 0, 10);
                layout.Controls.Add(textBox, 0, 0);

                FlowLayoutPanel buttons = new FlowLayoutPanel();
                buttons.FlowDirection = FlowDirection.RightToLeft;
                buttons.WrapContents = false;
                buttons.AutoSize = true;
                buttons.Dock = DockStyle.Fill;
                buttons.Margin = new Padding(0);
                layout.Controls.Add(buttons, 0, 1);

                Button okButton = new Button();
                okButton.Text = Lang.T("button.ok");
                okButton.DialogResult = DialogResult.OK;
                okButton.AutoSize = true;
                okButton.MinimumSize = new Size(86, 28);
                okButton.Margin = new Padding(0);
                buttons.Controls.Add(okButton);

                dialog.AcceptButton = okButton;
                dialog.CancelButton = okButton;

                using (System.Drawing.Icon smallIcon = LoadAppIcon(SystemInformation.SmallIconSize))
                {
                    dialog.Icon = smallIcon;
                    dialog.ShowDialog(this);
                }
            }
        }

        private static string LoadLicenseText()
        {
            const string resourceName = "ComfyUI.ModelManager.License.txt";

            try
            {
                Assembly assembly = typeof(MainForm).Assembly;
                using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream != null)
                    {
                        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                            return NormalizeNewlines(reader.ReadToEnd().Trim());
                    }
                }
            }
            catch
            {
                // faller tillbaka på filen bredvid exe-filen
            }

            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LICENSE");
                if (File.Exists(path)) return NormalizeNewlines(File.ReadAllText(path, Encoding.UTF8).Trim());
            }
            catch
            {
                // sista utvägen är den korta texten nedan
            }

            return "MIT License - see the LICENSE file.";
        }

        /// <summary>
        /// En TextBox tolkar bara CR LF som radbrytning, inte ensamt LF.
        /// </summary>
        private static string NormalizeNewlines(string text)
        {
            return text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
        }

        private void OpenModelFolder()
        {
            ModelRow row = CurrentRow();
            if (row == null) return;
            try
            {
                Process.Start("explorer.exe", "/select,\"" + row.Model.FullPath + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Lang.T("validation.openFolderError"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OpenWorkflow(WorkflowItem item)
        {
            if (item == null)
            {
                ModelRow row = CurrentRow();
                if (row == null || row.Model.MatchedWorkflows.Count == 0) return;
                item = new WorkflowItem();
                item.FullPath = row.Model.MatchedWorkflows[0];
                item.Display = MakeDisplayPath(item.FullPath);
            }

            try
            {
                ProcessStartInfo info = new ProcessStartInfo(item.FullPath);
                info.UseShellExecute = true;
                Process.Start(info);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Lang.T("validation.openWorkflowError"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ShowErrorList(List<string> errors)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Lang.F("error.unreadableCount", errors.Count));
            int shown = Math.Min(15, errors.Count);
            for (int i = 0; i < shown; i++) sb.Append(Environment.NewLine).Append(" · ").Append(errors[i]);
            if (errors.Count > shown) sb.Append(Environment.NewLine).Append(" ").Append(Lang.T("result.more"));
            MessageBox.Show(this, sb.ToString(), Lang.T("error.warning"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private bool ValidateFolders(bool requireArchive)
        {
            string models = _txtModels.Text.Trim();
            string workflows = _txtWorkflows.Text.Trim();
            string archive = _txtArchive.Text.Trim();

            if (models.Length == 0 || !Directory.Exists(models))
            {
                MessageBox.Show(this, Lang.T("validation.modelsMissing"), Lang.T("validation.folderMissingTitle"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _txtModels.Focus();
                return false;
            }
            if (workflows.Length == 0 || !Directory.Exists(workflows))
            {
                MessageBox.Show(this, Lang.T("validation.workflowsMissing"), Lang.T("validation.folderMissingTitle"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _txtWorkflows.Focus();
                return false;
            }
            if (requireArchive && archive.Length == 0)
            {
                MessageBox.Show(this, Lang.T("validation.archiveMissing"), Lang.T("validation.folderMissingTitle"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _txtArchive.Focus();
                return false;
            }
            if (archive.Length > 0)
            {
                try
                {
                    string fullArchive = Path.GetFullPath(archive).TrimEnd('\\', '/');
                    string fullModels = Path.GetFullPath(models).TrimEnd('\\', '/');
                    if (string.Equals(fullArchive, fullModels, StringComparison.OrdinalIgnoreCase) ||
                        fullArchive.StartsWith(fullModels + "\\", StringComparison.OrdinalIgnoreCase) ||
                        fullModels.StartsWith(fullArchive + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        MessageBox.Show(this,
                            Lang.T("validation.invalidArchive"),
                            Lang.T("validation.invalidArchiveTitle"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        _txtArchive.Focus();
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, Lang.T("validation.invalidPathTitle"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }
            return true;
        }

        public static string FormatSize(long bytes)
        {
            string[] units = new string[] { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            if (unit == 0) return bytes.ToString(Lang.Culture) + " B";
            return value.ToString("0.0", Lang.Culture) + " " + units[unit];
        }
    }
}
