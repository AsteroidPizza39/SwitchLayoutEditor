using System;
using System.Drawing;
using System.Windows.Forms;
using SwitchThemes.Common;

namespace BflytPreview
{
	/// <summary>
	/// Spreadsheet-style palette editor: Uses | File preview + value | In-game preview + value.
	/// Editing the file value updates the in-game columns; editing in-game applies inverse gamma
	/// and writes the file color (no dialog).
	/// </summary>
	internal sealed class PaletteSheet : DataGridView
	{
		MaterialPaletteTag palette;
		bool suppressEvents;

		public event Action<RGBAColor> HighlightColorChanged;
		public event Action BeforePaletteChanged;
		public event Action PaletteChanged;

		public PaletteSheet()
		{
			AllowUserToAddRows = false;
			AllowUserToDeleteRows = false;
			AllowUserToResizeRows = false;
			MultiSelect = false;
			SelectionMode = DataGridViewSelectionMode.FullRowSelect;
			RowHeadersVisible = false;
			AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
			BackgroundColor = SystemColors.Window;
			BorderStyle = BorderStyle.None;
			CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
			ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
			RowTemplate.Height = 24;
			EditMode = DataGridViewEditMode.EditOnEnter;

			Columns.Add(new DataGridViewTextBoxColumn
			{
				Name = "Kind",
				HeaderText = "Kind",
				ReadOnly = true,
				FillWeight = 55,
				MinimumWidth = 48
			});
			Columns.Add(new DataGridViewTextBoxColumn
			{
				Name = "Uses",
				HeaderText = "Uses",
				ReadOnly = true,
				FillWeight = 40,
				MinimumWidth = 36
			});
			Columns.Add(new DataGridViewTextBoxColumn
			{
				Name = "FilePreview",
				HeaderText = "File",
				ReadOnly = true,
				FillWeight = 35,
				MinimumWidth = 28
			});
			Columns.Add(new DataGridViewTextBoxColumn
			{
				Name = "FileValue",
				HeaderText = "File value",
				FillWeight = 100,
				MinimumWidth = 72
			});
			Columns.Add(new DataGridViewTextBoxColumn
			{
				Name = "GamePreview",
				HeaderText = "In-game",
				ReadOnly = true,
				FillWeight = 35,
				MinimumWidth = 28
			});
			Columns.Add(new DataGridViewTextBoxColumn
			{
				Name = "GameValue",
				HeaderText = "In-game value",
				FillWeight = 100,
				MinimumWidth = 72
			});

			CellFormatting += PaletteSheet_CellFormatting;
			CellEndEdit += PaletteSheet_CellEndEdit;
			SelectionChanged += PaletteSheet_SelectionChanged;
			CellParsing += PaletteSheet_CellParsing;
		}

		public void Bind(MaterialPaletteTag tag)
		{
			palette = tag;
			Reload();
		}

		public void ClearPalette()
		{
			palette = null;
			suppressEvents = true;
			try
			{
				Rows.Clear();
			}
			finally
			{
				suppressEvents = false;
			}
		}

		public void Reload()
		{
			if (palette == null)
			{
				ClearPalette();
				return;
			}

			suppressEvents = true;
			try
			{
				Rows.Clear();
				foreach (var row in palette.GetRows())
					AddRow(row);
			}
			finally
			{
				suppressEvents = false;
			}

			if (Rows.Count > 0 && CurrentRow == null)
				ClearSelection();
		}

		void AddRow(MaterialPaletteTag.PaletteRow row)
		{
			var preview = LayoutDisplayColor.Lift(row.Color);
			int index = Rows.Add(
				row.IsVertex ? "Vertex" : "Material",
				row.UsesText ?? row.Usages.ToString(),
				"",
				row.Color.ToString(),
				"",
				preview.ToString());
			Rows[index].Tag = row;
		}

		void PaletteSheet_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
		{
			if (e.RowIndex < 0 || e.RowIndex >= Rows.Count)
				return;
			if (!(Rows[e.RowIndex].Tag is MaterialPaletteTag.PaletteRow row))
				return;

			string name = Columns[e.ColumnIndex].Name;
			if (name == "FilePreview")
			{
				e.CellStyle.BackColor = Color.FromArgb(255, row.Color.R, row.Color.G, row.Color.B);
				e.CellStyle.SelectionBackColor = e.CellStyle.BackColor;
				e.CellStyle.ForeColor = ContrastText(e.CellStyle.BackColor);
				e.CellStyle.SelectionForeColor = e.CellStyle.ForeColor;
				e.Value = "";
				e.FormattingApplied = true;
			}
			else if (name == "GamePreview")
			{
				var preview = LayoutDisplayColor.Lift(row.Color);
				e.CellStyle.BackColor = Color.FromArgb(255, preview.R, preview.G, preview.B);
				e.CellStyle.SelectionBackColor = e.CellStyle.BackColor;
				e.CellStyle.ForeColor = ContrastText(e.CellStyle.BackColor);
				e.CellStyle.SelectionForeColor = e.CellStyle.ForeColor;
				e.Value = "";
				e.FormattingApplied = true;
			}
		}

		static Color ContrastText(Color bg)
		{
			double luma = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
			return luma > 0.55 ? Color.Black : Color.White;
		}

		void PaletteSheet_CellParsing(object sender, DataGridViewCellParsingEventArgs e)
		{
			// Keep as string; apply in CellEndEdit.
			e.ParsingApplied = true;
		}

		void PaletteSheet_CellEndEdit(object sender, DataGridViewCellEventArgs e)
		{
			if (suppressEvents || palette == null || e.RowIndex < 0)
				return;
			if (!(Rows[e.RowIndex].Tag is MaterialPaletteTag.PaletteRow row))
				return;

			string name = Columns[e.ColumnIndex].Name;
			string text = Convert.ToString(Rows[e.RowIndex].Cells[e.ColumnIndex].Value)?.Trim() ?? "";

			RGBAColor newFileColor;
			try
			{
				if (name == "FileValue")
				{
					newFileColor = ParseRgb(text, row.Color.A);
					BeforePaletteChanged?.Invoke();
					palette.SetColor(row.Index, row.IsVertex, newFileColor);
				}
				else if (name == "GameValue")
				{
					RGBAColor appearance = ParseRgb(text, row.Color.A);
					newFileColor = LayoutDisplayColor.InverseLift(appearance);
					BeforePaletteChanged?.Invoke();
					palette.SetColor(row.Index, row.IsVertex, newFileColor);
				}
				else
					return;
			}
			catch
			{
				RefreshRow(e.RowIndex);
				return;
			}

			// Indices may shift after a scoped edit rescans unique colors.
			Reload();
			HighlightColorChanged?.Invoke(newFileColor);
			PaletteChanged?.Invoke();
		}

		void RefreshRow(int gridRow)
		{
			if (!(Rows[gridRow].Tag is MaterialPaletteTag.PaletteRow row))
				return;
			foreach (var r in palette.GetRows())
			{
				if (r.IsVertex == row.IsVertex && r.Index == row.Index)
				{
					row = r;
					Rows[gridRow].Tag = row;
					break;
				}
			}
			var preview = LayoutDisplayColor.Lift(row.Color);
			suppressEvents = true;
			try
			{
				Rows[gridRow].Cells["Kind"].Value = row.IsVertex ? "Vertex" : "Material";
				Rows[gridRow].Cells["Uses"].Value = row.UsesText ?? row.Usages.ToString();
				Rows[gridRow].Cells["FileValue"].Value = row.Color.ToString();
				Rows[gridRow].Cells["GameValue"].Value = preview.ToString();
			}
			finally
			{
				suppressEvents = false;
			}
			InvalidateRow(gridRow);
		}

		void PaletteSheet_SelectionChanged(object sender, EventArgs e)
		{
			if (suppressEvents)
				return;
			if (CurrentRow?.Tag is MaterialPaletteTag.PaletteRow row)
				HighlightColorChanged?.Invoke(row.Color);
		}

		static RGBAColor ParseRgb(string text, byte fallbackAlpha)
		{
			if (text.StartsWith("@", StringComparison.Ordinal))
				text = text.Substring(1).Trim();
			string[] parts = text.Split(';');
			if (parts.Length < 3 || parts.Length > 4)
				throw new FormatException();
			byte r = byte.Parse(parts[0].Trim());
			byte g = byte.Parse(parts[1].Trim());
			byte b = byte.Parse(parts[2].Trim());
			byte a = parts.Length == 4 ? byte.Parse(parts[3].Trim()) : fallbackAlpha;
			return new RGBAColor(r, g, b, a);
		}
	}
}
