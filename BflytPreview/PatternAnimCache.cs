using System;
using System.Collections.Generic;
using System.IO;
using BflytPreview.Compression;
using SARCExt;
using SwitchThemes.Common;
using SwitchThemes.Common.Bflan;
using static SwitchThemes.Common.Bflan.Pai1Section;

namespace BflytPreview
{
	/// <summary>
	/// Loads part sibling pattern BFLANs (anim/{part}_Pattern.bflan, _TexPattern, …)
	/// and resolves FLTP texture-pattern bindings at a given frame index.
	/// </summary>
	public sealed class PatternAnimCache
	{
		static readonly string[] PatternSuffixes =
		{
			"_Pattern.bflan",
			"_TexPattern.bflan",
			"_IconPattern.bflan",
			"_NumPattern.bflan",
			"_PicturePattern.bflan",
			"_ChangePattern.bflan",
		};
		readonly Dictionary<string, byte[]> sarcFiles;
		readonly string searchDirectory;
		readonly Dictionary<string, PatternTable> cache =
			new Dictionary<string, PatternTable>(StringComparer.OrdinalIgnoreCase);

		public PatternAnimCache(Dictionary<string, byte[]> sarcFiles, string searchDirectory = null)
		{
			this.sarcFiles = sarcFiles;
			this.searchDirectory = searchDirectory;
		}

		public static PatternAnimCache FromPartsCache(PartsLayoutCache parts)
		{
			if (parts == null)
				return null;
			return new PatternAnimCache(parts.SarcFiles, parts.SearchDirectory);
		}

		/// <summary>
		/// Build material-name → texture-name overrides for the given part at patternIndex.
		/// Returns null when no Pattern anim exists or nothing applies at that frame.
		/// </summary>
		public Dictionary<string, string> GetOverrides(string partName, int patternIndex)
		{
			var table = GetTable(partName);
			if (table == null || table.Tracks.Count == 0)
				return null;

			var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (var kv in table.Tracks)
			{
				string tex = ResolveTrack(kv.Value, table.Textures, patternIndex);
				if (!string.IsNullOrEmpty(tex))
					result[kv.Key] = tex;
			}
			return result.Count > 0 ? result : null;
		}

		PatternTable GetTable(string partName)
		{
			if (string.IsNullOrEmpty(partName))
				return null;

			string key = partName;
			if (key.EndsWith(".bflyt", StringComparison.OrdinalIgnoreCase))
				key = key.Substring(0, key.Length - 6);
			foreach (var suffix in new[] { "_Pattern", "_TexPattern", "_IconPattern", "_NumPattern", "_PicturePattern", "_ChangePattern" })
			{
				if (key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
				{
					key = key.Substring(0, key.Length - suffix.Length);
					break;
				}
			}

			if (cache.TryGetValue(key, out var cached))
				return cached;

			byte[] data = ResolveBytes(key);
			if (data == null || data.Length < 8)
			{
				cache[key] = null;
				return null;
			}

			try
			{
				data = MaybeDecompress(data);
				var bflan = new BflanFile(data);
				var table = BuildTable(bflan);
				cache[key] = table;
				return table;
			}
			catch
			{
				cache[key] = null;
				return null;
			}
		}

		static PatternTable BuildTable(BflanFile bflan)
		{
			var pai = bflan?.paiData;
			if (pai?.Textures == null || pai.Entries == null)
				return null;

			var table = new PatternTable { Textures = pai.Textures };
			foreach (var entry in pai.Entries)
			{
				if (entry.Target != PaiEntry.AnimationTarget.Material)
					continue;
				if (string.IsNullOrEmpty(entry.Name))
					continue;

				foreach (var tag in entry.Tags)
				{
					if (!string.Equals(tag.TagType, "FLTP", StringComparison.OrdinalIgnoreCase))
						continue;

					// First Image1 (target 0) track wins — matches Toolbox LTP Image1.
					PaiTagEntry best = null;
					foreach (var te in tag.Entries)
					{
						if (te.KeyFrames == null || te.KeyFrames.Count == 0)
							continue;
						if (best == null || te.AnimationTarget < best.AnimationTarget)
							best = te;
					}
					if (best == null)
						continue;

					var keys = new List<KeyFrame>(best.KeyFrames);
					keys.Sort((a, b) => a.Frame.CompareTo(b.Frame));
					table.Tracks[entry.Name.TrimEnd('\0')] = keys;
				}
			}
			return table.Tracks.Count > 0 ? table : null;
		}

		static string ResolveTrack(List<KeyFrame> keys, string[] textures, int patternIndex)
		{
			if (keys == null || keys.Count == 0 || textures == null || textures.Length == 0)
				return null;

			float frame = patternIndex;
			KeyFrame chosen = keys[0];
			for (int i = 0; i < keys.Count; i++)
			{
				if (keys[i].Frame <= frame)
					chosen = keys[i];
				else
					break;
			}

			int idx = (int)Math.Round(chosen.Value);
			if (idx < 0 || idx >= textures.Length)
				return null;
			return textures[idx];
		}

		byte[] ResolveBytes(string partName)
		{
			foreach (var suffix in PatternSuffixes)
			{
				string fileName = partName + suffix;
				string sarcKey = "anim/" + fileName;

				if (sarcFiles != null)
				{
					if (sarcFiles.TryGetValue(sarcKey, out var direct))
						return direct;
					foreach (var kv in sarcFiles)
					{
						if (kv.Key.EndsWith(fileName, StringComparison.OrdinalIgnoreCase) ||
						    string.Equals(Path.GetFileName(kv.Key), fileName, StringComparison.OrdinalIgnoreCase))
							return kv.Value;
					}
				}

				if (!string.IsNullOrEmpty(searchDirectory))
				{
					string path = Path.Combine(searchDirectory, fileName);
					if (File.Exists(path))
						return File.ReadAllBytes(path);
					string animPath = Path.Combine(searchDirectory, "anim", fileName);
					if (File.Exists(animPath))
						return File.ReadAllBytes(animPath);
					string sibling = Path.Combine(Directory.GetParent(searchDirectory)?.FullName ?? searchDirectory, "anim", fileName);
					if (File.Exists(sibling))
						return File.ReadAllBytes(sibling);
				}
			}

			return null;
		}

		static byte[] MaybeDecompress(byte[] data)
		{
			if (data == null || data.Length < 4)
				return data;
			string magic = System.Text.Encoding.ASCII.GetString(data, 0, 4);
			if (magic == "Yaz0")
				return ManagedYaz0.Decompress(data);
			if (GameZstd.IsCompressed(data))
			{
				try { return GameZstd.Instance.Decompress(data); }
				catch { return data; }
			}
			return data;
		}

		sealed class PatternTable
		{
			public string[] Textures;
			public readonly Dictionary<string, List<KeyFrame>> Tracks =
				new Dictionary<string, List<KeyFrame>>(StringComparer.OrdinalIgnoreCase);
		}
	}
}
