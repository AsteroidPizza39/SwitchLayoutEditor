using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Design;
using System.Globalization;
using SwitchThemes.Common;

namespace BflytPreview
{
	/// <summary>
	/// Palette property value: editable file color plus read-only display-gamma preview.
	/// PropertyGrid paints both swatches side-by-side (file | preview).
	/// </summary>
	[TypeConverter(typeof(PaletteColorViewConverter))]
	[Editor(typeof(PaletteColorViewEditor), typeof(UITypeEditor))]
	internal struct PaletteColorView
	{
		public RGBAColor Stored;

		public RGBAColor Preview => LayoutDisplayColor.Lift(Stored);

		public PaletteColorView(RGBAColor stored)
		{
			Stored = stored;
		}

		public override string ToString()
		{
			var preview = Preview;
			if (Stored == preview)
				return Stored.ToString();
			return $"{Stored} → {preview}";
		}
	}

	internal sealed class PaletteColorViewConverter : TypeConverter
	{
		public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType) =>
			sourceType == typeof(string) || sourceType == typeof(RGBAColor);

		public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType) =>
			destinationType == typeof(string);

		public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
		{
			if (value is RGBAColor rgba)
				return new PaletteColorView(rgba);
			if (value is string s)
			{
				// Accept either "R;G;B" or "R;G;B → R;G;B" (edit the file side).
				string left = s;
				int arrow = s.IndexOf('→');
				if (arrow < 0)
					arrow = s.IndexOf("->", StringComparison.Ordinal);
				if (arrow >= 0)
					left = s.Substring(0, arrow).Trim();

				string[] parts = left.Split(';');
				if (parts.Length < 3 || parts.Length > 4)
					throw new FormatException("Expected R;G;B or R;G;B;A");
				byte r = byte.Parse(parts[0].Trim());
				byte g = byte.Parse(parts[1].Trim());
				byte b = byte.Parse(parts[2].Trim());
				byte a = parts.Length == 4 ? byte.Parse(parts[3].Trim()) : (byte)255;
				return new PaletteColorView(new RGBAColor(r, g, b, a));
			}
			return base.ConvertFrom(context, culture, value);
		}

		public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
		{
			if (destinationType == typeof(string) && value is PaletteColorView view)
				return view.ToString();
			return base.ConvertTo(context, culture, value, destinationType);
		}
	}

	internal sealed class PaletteColorViewEditor : UITypeEditor
	{
		public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext context) =>
			UITypeEditorEditStyle.Modal;

		public override bool GetPaintValueSupported(ITypeDescriptorContext context) => true;

		public override void PaintValue(PaintValueEventArgs e)
		{
			if (!(e.Value is PaletteColorView view))
			{
				base.PaintValue(e);
				return;
			}

			Rectangle bounds = e.Bounds;
			int mid = bounds.X + bounds.Width / 2;

			using (var storedBrush = new SolidBrush(view.Stored.Color))
			using (var previewBrush = new SolidBrush(view.Preview.Color))
			{
				e.Graphics.FillRectangle(storedBrush,
					new Rectangle(bounds.X, bounds.Y, mid - bounds.X, bounds.Height));
				e.Graphics.FillRectangle(previewBrush,
					new Rectangle(mid, bounds.Y, bounds.Right - mid, bounds.Height));
			}

			e.Graphics.DrawRectangle(Pens.Black, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
			e.Graphics.DrawLine(Pens.Gray, mid, bounds.Y, mid, bounds.Bottom - 1);
		}

		public override object EditValue(ITypeDescriptorContext context, IServiceProvider provider, object value)
		{
			if (!(value is PaletteColorView view))
				return value;

			using (var dialog = new System.Windows.Forms.ColorDialog())
			{
				dialog.Color = view.Stored.Color;
				dialog.FullOpen = true;
				dialog.AnyColor = true;
				if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
					return new PaletteColorView(new RGBAColor(dialog.Color));
			}

			return value;
		}
	}
}
