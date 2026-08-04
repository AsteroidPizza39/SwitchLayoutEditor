using SARCExt;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using BflytPreview.Compression;
using SwitchThemes.Common;
using SwitchThemes.Common.Patching;

namespace BflytPreview.EditorForms
{
    public partial class SzsEditor : Form
    {
        public class SzsFileProvider : IFileWriter
        {
            private SzsEditor Parent;
            internal Form EditorForm;
            public string Path { get; internal set; }

            public SzsFileProvider(SzsEditor parent, string path) =>
                (Parent, Path) = (parent, path);

            public void EditorClosed() =>
                Parent.CloseFileProvider(this);

            public void Save(byte[] Data) =>
                Parent.SaveFromProvider(this, Data);

            public override string ToString() => $"Szs file : {Path}";
        }

        internal List<SzsFileProvider> FileProviders = new List<SzsFileProvider>();

        internal void CloseFileProvider(SzsFileProvider file) =>
            FileProviders.Remove(file);

        internal void SaveFromProvider(SzsFileProvider file, byte[] Data)
        {
            if (!FileProviders.Contains(file)) throw new Exception("file is not a registered IFileWriter");
            loadedSarc.Files[file.Path] = Data;
        }

        IFileWriter _saveTo;
        public IFileWriter SaveTo
        {
            get => _saveTo;
            set
            {
                _saveTo?.EditorClosed();
                _saveTo = value;
                saveToSzsToolStripMenuItem.Visible = _saveTo != null;
                this.Text = value?.ToString() ?? "";
            }
        }

        SarcData loadedSarc;
        MainForm MainForm;
        ArchiveCompression archiveCompression;
        int zstdDictionaryId;

        public SzsEditor(
            SARCExt.SarcData _sarc,
            IFileWriter saveTo,
            MainForm _parentForm,
            ArchiveCompression compression = ArchiveCompression.None,
            int zstdDictionaryId = -1)
        {
            InitializeComponent();
            loadedSarc = _sarc;
            MainForm = _parentForm;
            archiveCompression = compression;
            this.zstdDictionaryId = zstdDictionaryId;
            SaveTo = saveTo;
            ApplyCompressionUi();
        }

        void ApplyCompressionUi()
        {
            switch (archiveCompression)
            {
                case ArchiveCompression.Zstd:
                    label1.Text = "ZSTD level [1-22] :";
                    numericUpDown1.Minimum = 1;
                    numericUpDown1.Maximum = 22;
                    numericUpDown1.Value = Math.Min(22, Math.Max(1, GameZstd.Instance.CompressionLevel));
                    break;
                case ArchiveCompression.Yaz0:
                    label1.Text = "Yaz0 level [0-9] :";
                    numericUpDown1.Minimum = 0;
                    numericUpDown1.Maximum = 9;
                    if (numericUpDown1.Value < 1)
                        numericUpDown1.Value = 3;
                    break;
                default:
                    label1.Text = "Compression (0=none) :";
                    numericUpDown1.Minimum = 0;
                    numericUpDown1.Maximum = 9;
                    numericUpDown1.Value = 0;
                    break;
            }
        }

        private void SzsEditor_Load(object sender, EventArgs e)
        {
#if !DEBUG
			extractNamesInClipboardToolStripMenuItem.Visible = false;
			exportFileListToClipboardToolStripMenuItem.Visible = false;
#endif

            if (loadedSarc == null)
            {
                MessageBox.Show("No sarc has been loaded");
                this.Close();
            }
            else
            {
                listBox1.Items.AddRange(loadedSarc.Files.Keys.ToArray());
                FormBringToFront();
            }
        }

        private void SzsEditor_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (FileProviders.Count != 0)
            {
                if (MessageBox.Show("There are some files of this SZS opened, this will close all of them and all the unsaved edits will be lost, do you want to continue ?", this.Text, MessageBoxButtons.YesNo) == DialogResult.No)
                    e.Cancel = true;
                else
                {
                    foreach (var k in FileProviders.ToArray())
                        k.EditorForm?.Close();
                    if (FileProviders.Count != 0)
                    {
                        MessageBox.Show($"Failed to close {FileProviders.Count} editors");
                        e.Cancel = true;
                    }
                }
            }
        }

        private void extractAllFilesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ExtractMultipleFiles(loadedSarc.Files.Keys.ToArray());
        }

        void ExtractMultipleFiles(IEnumerable<string> files)
        {
            var dlg = new FolderBrowserDialog();
            if (dlg.ShowDialog() != DialogResult.OK)
                return;
            foreach (string f in files)
            {
                string fOut = Path.Combine(dlg.SelectedPath, f);
                DirectoryInfo dir = new DirectoryInfo(Path.GetDirectoryName(fOut));
                if (!dir.Exists)
                    dir.Create();
                File.WriteAllBytes(fOut, loadedSarc.Files[f]);
            }
        }

        private void extractToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (listBox1.SelectedItems.Count > 1)
                ExtractMultipleFiles(listBox1.SelectedItems.Cast<string>());
            else
            {
                var sav = new SaveFileDialog() { FileName = listBox1.SelectedItem.ToString() };
                if (sav.ShowDialog() != DialogResult.OK)
                    return;
                File.WriteAllBytes(sav.FileName, loadedSarc.Files[listBox1.SelectedItem.ToString()]);
            }
        }

        private void deleteToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (loadedSarc.HashOnly)
            {
                MessageBox.Show("Can't remove files from a hash only sarc");
                return;
            }
            string[] Targets = listBox1.SelectedItems.Cast<string>().ToArray();
            foreach (var item in Targets)
            {
                loadedSarc.Files.Remove(item);
                listBox1.Items.Remove(item);
            }
        }

        private void addFilesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (loadedSarc.HashOnly)
            {
                MessageBox.Show("Can't add files to a hash only sarc");
                return;
            }
            var opn = new OpenFileDialog() { Multiselect = true };
            if (opn.ShowDialog() != DialogResult.OK)
                return;
            foreach (var f in opn.FileNames)
            {
                string name = Path.GetFileName(f);
                if (InputDialog.Show("File name", "Write the name for this file, use / to place it in a folder", ref name) != DialogResult.OK)
                    return;

                if (loadedSarc.Files.ContainsKey(name))
                {
                    MessageBox.Show($"File {name} already in szs");
                    continue;
                }
                loadedSarc.Files.Add(name, File.ReadAllBytes(f));
                listBox1.Items.Add(name);
            }
        }

        byte[] PackArchive()
        {
            // If the output path is clearly a TotK .zs but we somehow lost wrap info, prefer ZSTD.
            if (archiveCompression == ArchiveCompression.None &&
                SaveTo?.Path != null &&
                SaveTo.Path.EndsWith(".zs", StringComparison.OrdinalIgnoreCase))
            {
                archiveCompression = ArchiveCompression.Zstd;
                ApplyCompressionUi();
            }

            var packed = SARC.Pack(loadedSarc);
            byte[] sarcBytes = packed.Item2;

            switch (archiveCompression)
            {
                case ArchiveCompression.Zstd:
                    try
                    {
                        GameZstd.Instance.CompressionLevel = (int)numericUpDown1.Value;
                        return GameZstd.Instance.Compress(sarcBytes, zstdDictionaryId);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(
                            "ZSTD compress failed. Ensure Settings → ZsDic.pack.zs points at your game dump.\n\n" + ex.Message,
                            "Save failed",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                        return null;
                    }
                case ArchiveCompression.Yaz0:
                    int level = (int)numericUpDown1.Value;
                    if (level <= 0)
                        return sarcBytes;
                    return ManagedYaz0.Compress(sarcBytes, level, packed.Item1);
                default:
                    if (numericUpDown1.Value > 0)
                        return ManagedYaz0.Compress(sarcBytes, (int)numericUpDown1.Value, packed.Item1);
                    return sarcBytes;
            }
        }

        void SaveSzsAs()
        {
            var sav = new SaveFileDialog()
            {
                Filter =
                    "ZSTD (.zs)|*.zs|" +
                    "Yaz0 (.szs)|*.szs|" +
                    "Uncompressed SARC|*.sarc;*.blarc;*.pack|All files|*.*"
            };
            if (SaveTo?.Path != null)
                sav.FileName = Path.GetFileName(SaveTo.Path);
            if (sav.ShowDialog() != DialogResult.OK)
                return;

            string path = sav.FileName;
            string ext = Path.GetExtension(path)?.ToLowerInvariant() ?? "";
            if (sav.FilterIndex == 1 || ext == ".zs")
                archiveCompression = ArchiveCompression.Zstd;
            else if (sav.FilterIndex == 2 || ext == ".szs")
                archiveCompression = ArchiveCompression.Yaz0;
            else
                archiveCompression = ArchiveCompression.None;
            ApplyCompressionUi();

            SaveTo = new DiskFileProvider(path);
            byte[] data = PackArchive();
            if (data != null)
                SaveTo.Save(data);
        }

        private void saveAsToolStripMenuItem_Click(object sender, EventArgs e) =>
            SaveSzsAs();

        private void listBox1_DoubleClick(object sender, EventArgs e)
        {
            if (listBox1.SelectedItem == null)
                return;
            var Fname = listBox1.SelectedItem.ToString();

            var alreadyOpened = FileProviders.Where(x => x.Path == Fname);
            if (alreadyOpened.FirstOrDefault() != null)
            {
                alreadyOpened.FirstOrDefault().EditorForm.Focus();
                return;
            }

            var provider = new SzsFileProvider(this, Fname);
            byte[] bntxData = null;
            if (Fname.EndsWith(".bflyt", StringComparison.OrdinalIgnoreCase))
                bntxData = FindPreviewBntx();

            var form = MainForm.OpenFile(loadedSarc.Files[Fname], provider, bntxData, loadedSarc);
            if (form != null)
            {
                provider.EditorForm = form;
                FileProviders.Add(provider);
            }
        }

        byte[] FindPreviewBntx()
        {
            const string combined = "timg/__Combined.bntx";
            if (loadedSarc.Files.TryGetValue(combined, out var combinedBytes))
                return combinedBytes;

            // TotK UI archives usually ship one fat combined BNTX under timg/ (not always
            // named __Combined). Prefer the largest .bntx so a tiny stub cannot win.
            byte[] best = null;
            int bestLen = -1;
            foreach (var kv in loadedSarc.Files)
            {
                if (!kv.Key.EndsWith(".bntx", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (kv.Value != null && kv.Value.Length > bestLen)
                {
                    best = kv.Value;
                    bestLen = kv.Value.Length;
                }
            }
            return best;
        }

        private void replaceToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            if (listBox1.SelectedItem == null) return;
            var opn = new OpenFileDialog();
            if (opn.ShowDialog() != DialogResult.OK) return;
            loadedSarc.Files[listBox1.SelectedItem.ToString()] = File.ReadAllBytes(opn.FileName);
        }

        private void copyNameToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (listBox1.SelectedItem == null) return;
            Clipboard.SetText(listBox1.SelectedItem.ToString());
        }

        private void renameToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (listBox1.SelectedItem == null) return;
            string originalName = listBox1.SelectedItem.ToString();
            string name = Path.GetFileName(originalName);
            if (InputDialog.Show("File name", "Write the name for this file, use / to place it in a folder", ref name) != DialogResult.OK)
                return;

            if (loadedSarc.Files.ContainsKey(name))
            {
                MessageBox.Show($"File {name} already in szs");
                return;
            }
            loadedSarc.Files.Add(name, loadedSarc.Files[originalName]);
            loadedSarc.Files.Remove(originalName);
            listBox1.Items.Add(name);
            listBox1.Items.Remove(originalName);
        }

        private void saveToSzsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (SaveTo == null)
            {
                SaveSzsAs();
                return;
            }
            byte[] data = PackArchive();
            if (data != null)
                SaveTo.Save(data);
        }

        void FormBringToFront()
        {
            this.Activate();
            this.BringToFront();
            this.Focus();
        }

        private void SzsEditor_Click(object sender, EventArgs e) => FormBringToFront();
        private void SzsEditor_LocationChanged(object sender, EventArgs e) => FormBringToFront();

        private void SzsEditor_FormClosed(object sender, FormClosedEventArgs e) =>
            SaveTo?.EditorClosed();

        private void thisFileIsTheOriginalSzsToolStripMenuItem_Click(object sender, EventArgs e)
            => new LayoutDiffForm(loadedSarc, null).ShowDialog();

        private void thisFileIsTheEditedSzsToolStripMenuItem_Click(object sender, EventArgs e)
            => new LayoutDiffForm(null, loadedSarc).ShowDialog();

        private void SzsEditor_KeyDown(object sender, KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            if (e.Shift && e.Control && e.KeyCode == Keys.S)
                saveAsToolStripMenuItem.PerformClick();
            else if (e.Control && e.KeyCode == Keys.S)
                saveToSzsToolStripMenuItem.PerformClick();
            else e.SuppressKeyPress = false;
        }

        private void loadJSONPatchToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var opn = new OpenFileDialog();
            if (opn.ShowDialog() != DialogResult.OK) return;

            SzsPatcher P = new SzsPatcher(loadedSarc);
            LayoutPatch JSONLayout = LayoutPatch.Load(File.ReadAllText(opn.FileName));

            if (P.PatchLayouts(JSONLayout))
            {
                P.FinalizeBntx();
                MessageBox.Show("Loaded JSON patch");
            }
            else MessageBox.Show("Failed to load the JSON patch.");
        }

        private void listBox1_KeyDown(object sender, KeyEventArgs e)
        {
            if (listBox1.SelectedItem == null) return;
            if (e.KeyCode == Keys.Q)
                HexEditorForm.Show(loadedSarc.Files[listBox1.SelectedItem as string]);
            else if (e.KeyCode == Keys.Return)
                listBox1_DoubleClick(sender, null);
        }

        private void tb_search_TextChanged(object sender, EventArgs e)
        {
            listBox1.Items.Clear();
            if (tb_search.Text.Trim() == "")
                listBox1.Items.AddRange(loadedSarc.Files.Keys.ToArray());
            else
                foreach (var k in loadedSarc.Files.Keys)
                    if (k.IndexOf(tb_search.Text, StringComparison.InvariantCultureIgnoreCase) != -1)
                        listBox1.Items.Add(k);
        }

        private void extractNamesInClipboardToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var names = Clipboard.GetText()
                .Split('\n')
                .Select(x => x.Trim())
                .Where(x => x != "")
                .ToArray();

            ExtractMultipleFiles(names);
        }

        private void exportFileListToClipboardToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var names = loadedSarc.Files.Keys
                .Select(x => x.Trim())
                .Where(x => x != "")
                .ToArray();
            Clipboard.SetText(string.Join("\n", names));
        }

        private void checkLayoutCompatibilityToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using var open = new OpenFileDialog()
            {
                Filter = "Layout Patch|*.json",
                Multiselect = false
            };

            if (open.ShowDialog() != DialogResult.OK)
                return;

            LayoutPatch patch = LayoutPatch.Load(File.ReadAllText(open.FileName));

            var res = LayoutCompatibility.ValidateLayout(loadedSarc, patch);
            if (res.Count == 0)
                MessageBox.Show("No compatibility issues found.");
            else
            {
                var asString = LayoutCompatibility.StringifyIssues(res);
                MainForm.OpenForm(new TextView(this.Text + " - layout compat result", asString));
            }
        }
    }
}
