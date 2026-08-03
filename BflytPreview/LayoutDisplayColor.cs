using System;
using System.Drawing;
using SwitchThemes.Common;

namespace BflytPreview
{
	/// <summary>
	/// Shared display-gamma lift used by texture bake, vertex modulation, and the palette preview.
	/// Matches the pow(channel, 1/2.2) treatment that makes TotK HUD colors match in-game.
	/// </summary>
	internal static class LayoutDisplayColor
	{
		public const float InvGamma = 1f / 2.2f;

		public static float LiftChannel(float c01) =>
			(float)Math.Pow(Math.Max(c01, 0f), InvGamma);

		public static byte LiftByte(byte c) =>
			(byte)Math.Max(0, Math.Min(255, (int)Math.Round(LiftChannel(c / 255f) * 255f)));

		public static RGBAColor Lift(RGBAColor c) =>
			new RGBAColor(LiftByte(c.R), LiftByte(c.G), LiftByte(c.B), c.A);

		public static Color Lift(Color c) =>
			Color.FromArgb(c.A, LiftByte(c.R), LiftByte(c.G), LiftByte(c.B));
	}
}
