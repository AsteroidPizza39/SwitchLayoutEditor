using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SwitchThemes.Common;
using SwitchThemes.Common.Bflan;
using static SwitchThemes.Common.Bflan.Pai1Section;

namespace BflytPreview
{
	/// <summary>
	/// FLPA channel indices (Cafe LPATarget).
	/// </summary>
	public enum AnimLpaTarget : byte
	{
		TranslateX = 0,
		TranslateY = 1,
		TranslateZ = 2,
		RotateX = 3,
		RotateY = 4,
		RotateZ = 5,
		ScaleX = 6,
		ScaleY = 7,
		SizeX = 8,
		SizeY = 9,
	}

	/// <summary>
	/// FLVI channel indices.
	/// </summary>
	public enum AnimLviTarget : byte
	{
		Visibility = 0,
	}

	/// <summary>
	/// FLVC channel indices (Cafe LVCTarget).
	/// </summary>
	public enum AnimLvcTarget : byte
	{
		LeftTopRed = 0,
		LeftTopGreen = 1,
		LeftTopBlue = 2,
		LeftTopAlpha = 3,
		RightTopRed = 4,
		RightTopGreen = 5,
		RightTopBlue = 6,
		RightTopAlpha = 7,
		LeftBottomRed = 8,
		LeftBottomGreen = 9,
		LeftBottomBlue = 10,
		LeftBottomAlpha = 11,
		RightBottomRed = 12,
		RightBottomGreen = 13,
		RightBottomBlue = 14,
		RightBottomAlpha = 15,
		PaneAlpha = 16,
	}

	/// <summary>
	/// FLMC channel indices (Cafe LMCTarget — Black then White RGBA).
	/// </summary>
	public enum AnimLmcTarget : byte
	{
		BlackColorRed = 0,
		BlackColorGreen = 1,
		BlackColorBlue = 2,
		BlackColorAlpha = 3,
		WhiteColorRed = 4,
		WhiteColorGreen = 5,
		WhiteColorBlue = 6,
		WhiteColorAlpha = 7,
	}

	/// <summary>
	/// Non-destructive overlay produced by evaluating a BFLAN at a given frame.
	/// </summary>
	public sealed class LayoutAnimState
	{
		public readonly Dictionary<string, Dictionary<AnimLpaTarget, float>> PaneSrt =
			new Dictionary<string, Dictionary<AnimLpaTarget, float>>(StringComparer.OrdinalIgnoreCase);
		public readonly Dictionary<string, bool> PaneVisibility =
			new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
		public readonly Dictionary<string, Dictionary<AnimLvcTarget, float>> PaneVertexColors =
			new Dictionary<string, Dictionary<AnimLvcTarget, float>>(StringComparer.OrdinalIgnoreCase);
		public readonly Dictionary<string, Dictionary<AnimLmcTarget, float>> MaterialColors =
			new Dictionary<string, Dictionary<AnimLmcTarget, float>>(StringComparer.OrdinalIgnoreCase);

		public bool TryGetPaneSrt(string paneName, AnimLpaTarget target, out float value)
		{
			value = 0;
			return !string.IsNullOrEmpty(paneName)
				&& PaneSrt.TryGetValue(paneName, out var map)
				&& map.TryGetValue(target, out value);
		}

		public bool TryGetPaneVisible(string paneName, out bool visible)
		{
			visible = true;
			return !string.IsNullOrEmpty(paneName)
				&& PaneVisibility.TryGetValue(paneName, out visible);
		}

		public bool TryGetMaterialColor(string materialName, AnimLmcTarget target, out float value)
		{
			value = 0;
			return !string.IsNullOrEmpty(materialName)
				&& MaterialColors.TryGetValue(materialName, out var map)
				&& map.TryGetValue(target, out value);
		}

		/// <summary>
		/// Build Black/White colors from base material colors with FLMC overrides applied.
		/// </summary>
		public void ApplyMaterialColors(string materialName, ref RGBAColor black, ref RGBAColor white)
		{
			if (string.IsNullOrEmpty(materialName) || !MaterialColors.TryGetValue(materialName, out var map))
				return;

			black = ApplyRgba(black, map,
				AnimLmcTarget.BlackColorRed, AnimLmcTarget.BlackColorGreen,
				AnimLmcTarget.BlackColorBlue, AnimLmcTarget.BlackColorAlpha);
			white = ApplyRgba(white, map,
				AnimLmcTarget.WhiteColorRed, AnimLmcTarget.WhiteColorGreen,
				AnimLmcTarget.WhiteColorBlue, AnimLmcTarget.WhiteColorAlpha);
		}

		public void ApplyVertexColorCorner(string paneName, bool topLeft, bool topRight, bool bottomLeft, bool bottomRight,
			ref RGBAColor color)
		{
			if (string.IsNullOrEmpty(paneName) || !PaneVertexColors.TryGetValue(paneName, out var map))
				return;

			if (topLeft)
				color = ApplyRgba(color, map, AnimLvcTarget.LeftTopRed, AnimLvcTarget.LeftTopGreen, AnimLvcTarget.LeftTopBlue, AnimLvcTarget.LeftTopAlpha);
			else if (topRight)
				color = ApplyRgba(color, map, AnimLvcTarget.RightTopRed, AnimLvcTarget.RightTopGreen, AnimLvcTarget.RightTopBlue, AnimLvcTarget.RightTopAlpha);
			else if (bottomLeft)
				color = ApplyRgba(color, map, AnimLvcTarget.LeftBottomRed, AnimLvcTarget.LeftBottomGreen, AnimLvcTarget.LeftBottomBlue, AnimLvcTarget.LeftBottomAlpha);
			else if (bottomRight)
				color = ApplyRgba(color, map, AnimLvcTarget.RightBottomRed, AnimLvcTarget.RightBottomGreen, AnimLvcTarget.RightBottomBlue, AnimLvcTarget.RightBottomAlpha);
		}

		public bool TryGetPaneAlpha(string paneName, out float alpha)
		{
			alpha = 255f;
			return !string.IsNullOrEmpty(paneName)
				&& PaneVertexColors.TryGetValue(paneName, out var map)
				&& map.TryGetValue(AnimLvcTarget.PaneAlpha, out alpha);
		}

		static RGBAColor ApplyRgba(RGBAColor baseColor, Dictionary<AnimLmcTarget, float> map,
			AnimLmcTarget r, AnimLmcTarget g, AnimLmcTarget b, AnimLmcTarget a)
		{
			byte R = Channel(map, r, baseColor.R);
			byte G = Channel(map, g, baseColor.G);
			byte B = Channel(map, b, baseColor.B);
			byte A = Channel(map, a, baseColor.A);
			return new RGBAColor(R, G, B, A);
		}

		static RGBAColor ApplyRgba(RGBAColor baseColor, Dictionary<AnimLvcTarget, float> map,
			AnimLvcTarget r, AnimLvcTarget g, AnimLvcTarget b, AnimLvcTarget a)
		{
			byte R = Channel(map, r, baseColor.R);
			byte G = Channel(map, g, baseColor.G);
			byte B = Channel(map, b, baseColor.B);
			byte A = Channel(map, a, baseColor.A);
			return new RGBAColor(R, G, B, A);
		}

		static byte Channel<T>(Dictionary<T, float> map, T key, byte fallback)
		{
			if (!map.TryGetValue(key, out float v))
				return fallback;
			if (v < 0f) v = 0f;
			if (v > 255f) v = 255f;
			return (byte)Math.Round(v);
		}
	}

	/// <summary>
	/// One editable curve in a BFLAN (flat list row).
	/// </summary>
	public sealed class LayoutAnimTrack
	{
		public string TargetName;
		public PaiEntry.AnimationTarget TargetKind;
		public string TagType;
		public byte ChannelIndex;
		public string ChannelName;
		public PaiTagEntry Entry;
		public PaiTag Tag;
		public PaiEntry PaiEntry;

		public string DisplayName =>
			TargetName + " · " + TagType + " · " + ChannelName;
	}

	/// <summary>
	/// Samples BFLAN keyframes and lists typed tracks for the Animations panel.
	/// </summary>
	public static class LayoutAnimEvaluator
	{
		static readonly string[] SupportedTags = { "FLPA", "FLVI", "FLVC", "FLMC" };

		public static bool IsSupportedTag(string tagType) =>
			!string.IsNullOrEmpty(tagType) && SupportedTags.Any(t =>
				string.Equals(t, tagType, StringComparison.OrdinalIgnoreCase));

		public static string ChannelLabel(string tagType, byte channel)
		{
			if (string.Equals(tagType, "FLPA", StringComparison.OrdinalIgnoreCase)
				&& Enum.IsDefined(typeof(AnimLpaTarget), channel))
				return ((AnimLpaTarget)channel).ToString();
			if (string.Equals(tagType, "FLVI", StringComparison.OrdinalIgnoreCase))
				return "Visibility";
			if (string.Equals(tagType, "FLVC", StringComparison.OrdinalIgnoreCase)
				&& Enum.IsDefined(typeof(AnimLvcTarget), channel))
				return ((AnimLvcTarget)channel).ToString();
			if (string.Equals(tagType, "FLMC", StringComparison.OrdinalIgnoreCase)
				&& Enum.IsDefined(typeof(AnimLmcTarget), channel))
				return ((AnimLmcTarget)channel).ToString();
			return "Ch" + channel.ToString(CultureInfo.InvariantCulture);
		}

		public static List<LayoutAnimTrack> ListTracks(BflanFile bflan)
		{
			var list = new List<LayoutAnimTrack>();
			var pai = bflan?.paiData;
			if (pai?.Entries == null)
				return list;

			foreach (var entry in pai.Entries)
			{
				if (entry?.Tags == null) continue;
				foreach (var tag in entry.Tags)
				{
					if (!IsSupportedTag(tag.TagType) || tag.Entries == null)
						continue;
					foreach (var te in tag.Entries)
					{
						list.Add(new LayoutAnimTrack
						{
							TargetName = entry.Name ?? "",
							TargetKind = entry.Target,
							TagType = tag.TagType,
							ChannelIndex = te.AnimationTarget,
							ChannelName = ChannelLabel(tag.TagType, te.AnimationTarget),
							Entry = te,
							Tag = tag,
							PaiEntry = entry,
						});
					}
				}
			}
			return list;
		}

		public static LayoutAnimState Evaluate(BflanFile bflan, float frame)
		{
			var state = new LayoutAnimState();
			var pai = bflan?.paiData;
			if (pai?.Entries == null)
				return state;

			foreach (var entry in pai.Entries)
			{
				if (entry?.Tags == null || string.IsNullOrEmpty(entry.Name))
					continue;

				foreach (var tag in entry.Tags)
				{
					if (tag?.Entries == null) continue;
					string tt = tag.TagType ?? "";

					if (string.Equals(tt, "FLPA", StringComparison.OrdinalIgnoreCase)
						&& entry.Target == PaiEntry.AnimationTarget.Pane)
					{
						var map = GetOrCreate(state.PaneSrt, entry.Name);
						foreach (var te in tag.Entries)
						{
							if (!Enum.IsDefined(typeof(AnimLpaTarget), te.AnimationTarget))
								continue;
							map[(AnimLpaTarget)te.AnimationTarget] = Sample(te.KeyFrames, frame);
						}
					}
					else if (string.Equals(tt, "FLVI", StringComparison.OrdinalIgnoreCase)
						&& entry.Target == PaiEntry.AnimationTarget.Pane)
					{
						foreach (var te in tag.Entries)
						{
							float v = Sample(te.KeyFrames, frame);
							state.PaneVisibility[entry.Name] = Math.Abs(v) >= 0.5f;
						}
					}
					else if (string.Equals(tt, "FLVC", StringComparison.OrdinalIgnoreCase)
						&& entry.Target == PaiEntry.AnimationTarget.Pane)
					{
						var map = GetOrCreate(state.PaneVertexColors, entry.Name);
						foreach (var te in tag.Entries)
						{
							if (!Enum.IsDefined(typeof(AnimLvcTarget), te.AnimationTarget))
								continue;
							map[(AnimLvcTarget)te.AnimationTarget] = Sample(te.KeyFrames, frame);
						}
					}
					else if (string.Equals(tt, "FLMC", StringComparison.OrdinalIgnoreCase)
						&& entry.Target == PaiEntry.AnimationTarget.Material)
					{
						var map = GetOrCreate(state.MaterialColors, entry.Name);
						foreach (var te in tag.Entries)
						{
							if (!Enum.IsDefined(typeof(AnimLmcTarget), te.AnimationTarget))
								continue;
							map[(AnimLmcTarget)te.AnimationTarget] = Sample(te.KeyFrames, frame);
						}
					}
				}
			}

			return state;
		}

		public static float Sample(IList<KeyFrame> keys, float frame) =>
			KeyFrame.SampleKeyframes(keys, frame);

		static Dictionary<TKey, float> GetOrCreate<TKey>(
			Dictionary<string, Dictionary<TKey, float>> root, string name)
		{
			if (!root.TryGetValue(name, out var map))
			{
				map = new Dictionary<TKey, float>();
				root[name] = map;
			}
			return map;
		}
	}
}
