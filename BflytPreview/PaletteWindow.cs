using System;
using System.Drawing;
using System.Windows.Forms;

namespace BflytPreview
{
	/// <summary>
	/// Floating palette editor. Owns the sheet UI; EditorView owns layout/filter state.
	/// </summary>
	internal sealed class PaletteWindow : Form
	{
		readonly EditorView owner;
		readonly PaletteSheet sheet;
		readonly ComboBox modeCombo;
		readonly Label statusLabel;
		readonly Label warnLabel;
		readonly Button setFromCheckedButton;
		readonly Button clearFilterButton;

		public PaletteSheet Sheet => sheet;

		public PaletteWindow(EditorView owner)
		{
			this.owner = owner ?? throw new ArgumentNullException(nameof(owner));

			Text = "Palette";
			FormBorderStyle = FormBorderStyle.SizableToolWindow;
			ShowInTaskbar = false;
			StartPosition = FormStartPosition.Manual;
			MinimumSize = new Size(420, 280);
			Size = new Size(560, 420);
			Font = SystemFonts.MessageBoxFont;

			var top = new Panel
			{
				Dock = DockStyle.Top,
				Height = 72,
				Padding = new Padding(8)
			};

			modeCombo = new ComboBox
			{
				DropDownStyle = ComboBoxStyle.DropDownList,
				Location = new Point(8, 8),
				Width = 120
			};
			modeCombo.Items.AddRange(new object[] { "Whitelist", "Blacklist" });
			modeCombo.SelectedIndex = 1;
			modeCombo.SelectedIndexChanged += (s, e) =>
			{
				owner.PaneFilter.Mode = modeCombo.SelectedIndex == 0
					? PaneFilterMode.Whitelist
					: PaneFilterMode.Blacklist;
				owner.OnPaneFilterChanged();
				RefreshStatus();
			};

			setFromCheckedButton = new Button
			{
				Text = "Set filter from checked",
				Location = new Point(140, 6),
				Width = 150,
				Height = 26
			};
			setFromCheckedButton.Click += (s, e) =>
			{
				owner.ApplyCheckedPanesAsFilter();
				RefreshStatus();
			};

			clearFilterButton = new Button
			{
				Text = "Clear filter",
				Location = new Point(300, 6),
				Width = 100,
				Height = 26
			};
			clearFilterButton.Click += (s, e) =>
			{
				owner.ClearPaneFilter();
				RefreshStatus();
			};

			statusLabel = new Label
			{
				AutoSize = false,
				Location = new Point(8, 38),
				Size = new Size(520, 18),
				Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
				Text = "Filter: off (all panes)"
			};

			warnLabel = new Label
			{
				AutoSize = false,
				Location = new Point(8, 54),
				Size = new Size(520, 16),
				Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
				ForeColor = Color.DarkGoldenrod,
				Text = "Shared materials still change on every pane that uses them."
			};

			top.Controls.Add(modeCombo);
			top.Controls.Add(setFromCheckedButton);
			top.Controls.Add(clearFilterButton);
			top.Controls.Add(statusLabel);
			top.Controls.Add(warnLabel);
			// Grow top panel so warn fits
			top.Height = 78;

			sheet = new PaletteSheet { Dock = DockStyle.Fill };
			sheet.HighlightColorChanged += color => owner.SetPaletteHighlight(color);
			sheet.PaletteChanged += () => owner.OnPaletteEdited();

			Controls.Add(sheet);
			Controls.Add(top);

			FormClosing += PaletteWindow_FormClosing;
		}

		void PaletteWindow_FormClosing(object sender, FormClosingEventArgs e)
		{
			if (e.CloseReason == CloseReason.UserClosing)
			{
				e.Cancel = true;
				Hide();
			}
		}

		public void RefreshStatus()
		{
			statusLabel.Text = owner.PaneFilter.Describe();
			modeCombo.SelectedIndex = owner.PaneFilter.Mode == PaneFilterMode.Whitelist ? 0 : 1;
		}

		public void BindPalette(MaterialPaletteTag tag)
		{
			sheet.Bind(tag);
			RefreshStatus();
		}

		protected override void OnShown(EventArgs e)
		{
			base.OnShown(e);
			RefreshStatus();
		}
	}
}
