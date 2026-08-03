using System;
using System.Collections.Generic;
using System.IO;
using BflytPreview.Compression;
using SARCExt;
using SwitchThemes.Common;
using SwitchThemes.Common.Bflyt;

namespace BflytPreview
{
	/// <summary>
	/// Resolves prt1 LayoutFileName → BflytFile (Toolbox PartsManager equivalent for preview).
	/// Looks up blyt/{name}.bflyt in an open SARC (and optionally a fallback directory).
	/// </summary>
	public sealed class PartsLayoutCache
	{
		readonly Dictionary<string, BflytFile> cache = new Dictionary<string, BflytFile>(StringComparer.OrdinalIgnoreCase);
		readonly Dictionary<string, byte[]> sarcFiles;
		readonly string searchDirectory;

		PartsLayoutCache(Dictionary<string, byte[]> sarcFiles, string searchDirectory, bool fromArchive, int layoutFileCount)
		{
			this.sarcFiles = sarcFiles;
			this.searchDirectory = searchDirectory;
			FromArchive = fromArchive;
			LayoutFileCount = layoutFileCount;
		}

		/// <summary>True when this cache was built from a SARC/SZS archive.</summary>
		public bool FromArchive { get; }

		/// <summary>Number of .bflyt entries found in the archive (or directory).</summary>
		public int LayoutFileCount { get; }

		/// <summary>
		/// Suitable for "preview sub-layouts": archive-backed with more than one layout file.
		/// </summary>
		public bool CanPreviewSiblingLayouts => FromArchive && LayoutFileCount > 1;

		public static PartsLayoutCache FromSarc(SarcData sarc, string fallbackDirectory = null)
		{
			if (sarc?.Files == null)
				return null;

			int count = 0;
			foreach (var key in sarc.Files.Keys)
			{
				if (key.EndsWith(".bflyt", StringComparison.OrdinalIgnoreCase))
					count++;
			}

			return new PartsLayoutCache(sarc.Files, fallbackDirectory, fromArchive: true, layoutFileCount: count);
		}

		public BflytFile Get(string partName)
		{
			if (string.IsNullOrEmpty(partName))
				return null;

			string key = partName;
			if (key.EndsWith(".bflyt", StringComparison.OrdinalIgnoreCase))
				key = key.Substring(0, key.Length - 6);

			if (cache.TryGetValue(key, out var cached))
				return cached;

			byte[] data = ResolveBytes(key);
			if (data == null || data.Length < 8)
				return null;

			try
			{
				data = MaybeDecompress(data);
				var layout = new BflytFile(data);
				cache[key] = layout;
				return layout;
			}
			catch
			{
				return null;
			}
		}

		byte[] ResolveBytes(string partName)
		{
			string fileName = partName + ".bflyt";
			string sarcKey = "blyt/" + fileName;

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
	}
}
