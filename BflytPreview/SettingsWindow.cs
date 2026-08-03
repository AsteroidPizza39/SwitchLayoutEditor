using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using BflytPreview.Compression;

namespace BflytPreview
{
    public partial class SettingsWindow : Form
    {
        public SettingsWindow()
        {
            InitializeComponent();
        }

        private void SettingsWindow_Load(object sender, EventArgs e)
        {
            pickColor.BackColor = Settings.Default.PaneColor;
            selectedColor.BackColor = Settings.Default.SelectedColor;
            outlineColor.BackColor = Settings.Default.OutlineColor;
            txtZsDicPath.Text = Settings.Default.ZsDicPackPath ?? string.Empty;
            chkPreviewSubLayouts.Checked = Settings.Default.PreviewSubLayouts;
            if (!string.IsNullOrEmpty(Settings.Default.BgFileName) && File.Exists(Settings.Default.BgFileName))
            {
                pictureBox1.BackgroundImage = Image.FromFile(Settings.Default.BgFileName);
                EditorView.texture = EditorView.LoadBgImage(Settings.Default.BgFileName, false, true);
            }
            if (Settings.Default.ShowImage)
                showImg.Text = "Hide Image";
        }

        private void SettingsWindow_FormClosing(object sender, FormClosingEventArgs e)
        {
            Settings.Default.PaneColor = pickColor.BackColor;
            Settings.Default.SelectedColor = selectedColor.BackColor;
            Settings.Default.OutlineColor = outlineColor.BackColor;
            var zsDicPath = (txtZsDicPath.Text ?? string.Empty).Trim();
            var zsDicChanged = !string.Equals(Settings.Default.ZsDicPackPath ?? string.Empty, zsDicPath, StringComparison.OrdinalIgnoreCase);
            Settings.Default.ZsDicPackPath = zsDicPath;
            Settings.Default.PreviewSubLayouts = chkPreviewSubLayouts.Checked;
            Settings.Default.Save();
            if (zsDicChanged)
                GameZstd.ReloadFromSettings();
        }

        private void pickColor_Click(object sender, EventArgs e)
        {
            ColorDialog MyDialog = new ColorDialog();
            MyDialog.ShowHelp = true;
            MyDialog.Color = Settings.Default.PaneColor;
            
            if (MyDialog.ShowDialog() == DialogResult.OK)
                Settings.Default.PaneColor = pickColor.BackColor = MyDialog.Color;
        }

        private void selectedColor_Click(object sender, EventArgs e)
        {
            ColorDialog MyDialog = new ColorDialog();
            MyDialog.ShowHelp = true;
            MyDialog.Color = Settings.Default.SelectedColor;

            if (MyDialog.ShowDialog() == DialogResult.OK)
                Settings.Default.SelectedColor = selectedColor.BackColor = MyDialog.Color;
        }

        private void outlineColor_Click(object sender, EventArgs e)
        {
            ColorDialog MyDialog = new ColorDialog();
            MyDialog.ShowHelp = true;
            MyDialog.Color = Settings.Default.OutlineColor;

            if (MyDialog.ShowDialog() == DialogResult.OK)
                Settings.Default.OutlineColor = outlineColor.BackColor = MyDialog.Color;
        }

        private void loadBackgroundImage_Click(object sender, EventArgs e)
        {
            OpenFileDialog opn = new OpenFileDialog() { Filter = "Supported files (jpg,jpeg,png)|*.jpg;*.jpeg;*.png|All files|*.*" };
            if (opn.ShowDialog() != DialogResult.OK) return;
            
            pictureBox1.BackgroundImage = Image.FromFile(opn.FileName);
            Settings.Default.BgFileName = opn.FileName;
            EditorView.texture = EditorView.LoadBgImage(opn.FileName, false, true);
        }

        private void showImg_Click(object sender, EventArgs e)
        {
            if (!Settings.Default.ShowImage)
            {
                Settings.Default.ShowImage = true;
                showImg.Text = "Hide Image";
            }
            else
            {
                Settings.Default.ShowImage = false;
                showImg.Text = "Show Image";
            }
        }

        private void btnBrowseZsDic_Click(object sender, EventArgs e)
        {
            OpenFileDialog opn = new OpenFileDialog()
            {
                Filter = "ZsDic pack (*.zs;*.pack)|*.zs;*.pack|All files|*.*",
                FileName = string.IsNullOrEmpty(txtZsDicPath.Text) ? "ZsDic.pack.zs" : Path.GetFileName(txtZsDicPath.Text)
            };
            if (!string.IsNullOrEmpty(txtZsDicPath.Text))
            {
                try { opn.InitialDirectory = Path.GetDirectoryName(txtZsDicPath.Text); } catch { }
            }
            if (opn.ShowDialog() != DialogResult.OK) return;
            txtZsDicPath.Text = opn.FileName;
        }
    }
}
