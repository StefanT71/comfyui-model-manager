using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ComfyModelManager
{
    /// <summary>
    /// Språkvalsdialog som visas innan huvudfönstret första gången (eller när
    /// det sparade språket inte längre finns).
    /// </summary>
    public static class LanguageDialog
    {
        public static string ShowFirstRun(bool storedLanguageMissing)
        {
            List<LanguageInfo> languages = Lang.AvailableLanguages();
            if (languages.Count <= 1) return "en";

            string selectedCode = "en";

            using (Form dialog = new Form())
            {
                dialog.Text = Lang.T("language.title");
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.StartPosition = FormStartPosition.CenterScreen;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.ClientSize = new Size(500, 250);
                dialog.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

                try
                {
                    dialog.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                }
                catch
                {
                    // standardikon duger
                }

                TableLayoutPanel layout = new TableLayoutPanel();
                layout.Dock = DockStyle.Fill;
                layout.Padding = new Padding(22, 18, 22, 14);
                layout.ColumnCount = 1;
                layout.RowCount = 4;
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                dialog.Controls.Add(layout);

                Label intro = new Label();
                intro.Text = Lang.T("language.intro");
                intro.AutoSize = true;
                intro.MaximumSize = new Size(450, 0);
                intro.Margin = new Padding(0, 0, 0, 10);
                layout.Controls.Add(intro, 0, 0);

                Label warning = new Label();
                warning.Text = Lang.T("language.currentMissing");
                warning.AutoSize = true;
                warning.MaximumSize = new Size(450, 0);
                warning.ForeColor = Color.FromArgb(165, 30, 30);
                warning.Visible = storedLanguageMissing;
                warning.Margin = new Padding(0, 0, 0, 10);
                layout.Controls.Add(warning, 0, 1);

                TableLayoutPanel chooser = new TableLayoutPanel();
                chooser.Dock = DockStyle.Top;
                chooser.AutoSize = true;
                chooser.ColumnCount = 3;
                chooser.RowCount = 1;
                chooser.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74F));
                chooser.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
                chooser.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                chooser.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
                chooser.Margin = new Padding(0);
                layout.Controls.Add(chooser, 0, 2);

                Label label = new Label();
                label.Text = Lang.T("label.language");
                label.TextAlign = ContentAlignment.MiddleLeft;
                label.Dock = DockStyle.Fill;
                label.Margin = new Padding(0, 0, 6, 0);
                chooser.Controls.Add(label, 0, 0);

                ComboBox combo = new ComboBox();
                combo.DropDownStyle = ComboBoxStyle.DropDownList;
                combo.Dock = DockStyle.Fill;
                combo.DropDownWidth = 280;
                combo.Margin = new Padding(0, 2, 8, 0);
                combo.Items.AddRange(languages.ToArray());
                for (int i = 0; i < languages.Count; i++)
                {
                    if (string.Equals(languages[i].Code, "en", StringComparison.OrdinalIgnoreCase))
                    {
                        combo.SelectedIndex = i;
                        break;
                    }
                }
                chooser.Controls.Add(combo, 1, 0);

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
                chooser.Controls.Add(openFolder, 2, 0);

                FlowLayoutPanel buttons = new FlowLayoutPanel();
                buttons.FlowDirection = FlowDirection.RightToLeft;
                buttons.WrapContents = false;
                buttons.AutoSize = true;
                buttons.Dock = DockStyle.Fill;
                buttons.Margin = new Padding(0, 10, 0, 0);
                layout.Controls.Add(buttons, 0, 3);

                Button ok = new Button();
                ok.Text = Lang.T("button.ok");
                ok.DialogResult = DialogResult.OK;
                ok.AutoSize = true;
                ok.MinimumSize = new Size(86, 28);
                ok.Margin = new Padding(6, 0, 0, 0);
                buttons.Controls.Add(ok);

                Button cancel = new Button();
                cancel.Text = Lang.T("button.cancel");
                cancel.DialogResult = DialogResult.Cancel;
                cancel.AutoSize = true;
                cancel.MinimumSize = new Size(86, 28);
                cancel.Margin = new Padding(6, 0, 0, 0);
                buttons.Controls.Add(cancel);

                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;

                if (dialog.ShowDialog() == DialogResult.OK && combo.SelectedItem != null)
                    selectedCode = ((LanguageInfo)combo.SelectedItem).Code;
            }

            return selectedCode;
        }
    }
}
