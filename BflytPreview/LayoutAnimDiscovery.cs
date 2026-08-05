using System;
using System.Collections.Generic;
using System.IO;
using BflytPreview.Compression;
using SwitchThemes.Common.Bflan;

namespace BflytPreview
{
	/// <summary>
	/// A BFLAN discovered next to an open layout (anim/ siblings).
	/// </summary>
	public sealed class LayoutAnimRef
	{
		public string DisplayName;
		public string ArchiveKey;
		public string FilePath;
		public byte[] Data;
		public IFileWriter Writer;

		public override string ToString() => DisplayName ?? "(unnamed)";
	}

	/// <summary>
	/// Finds anim/{layout}_*.bflan next to a layout via SARC map and/or filesystem.
	/// </summary>
	public static class LayoutAnimDiscovery
	{
		public static List<LayoutAnimRef> FindForLayout(
			string layoutName,
			Dictionary<string, byte[]> sarcFiles,
			string searchDirectory,
			IFileWriter layoutWriter)
		{
			var results = new List<LayoutAnimRef>();
			string baseName = StripBflyt(layoutName);
			if (string.IsNullOrEmpty(baseName))
				return results;

			string prefix = baseName + "_";
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			if (sarcFiles != null)
			{
				foreach (var kv in sarcFiles)
				{
					string file = Path.GetFileName(kv.Key);
					if (string.IsNullOrEmpty(file) ||
						!file.EndsWith(".bflan", StringComparison.OrdinalIgnoreCase))
						continue;
					if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
						!string.Equals(Path.GetFileNameWithoutExtension(file), baseName, StringComparison.OrdinalIgnoreCase))
						continue;

					string key = NormalizeKey(kv.Key);
					if (!seen.Add(key))
						continue;

					results.Add(new LayoutAnimRef
					{
						DisplayName = Path.GetFileNameWithoutExtension(file),
						ArchiveKey = kv.Key,
						Data = MaybeDecompress(kv.Value),
						Writer = new SarcEntryWriter(sarcFiles, kv.Key),
					});
				}
			}

			string animDir = null;
			if (!string.IsNullOrEmpty(searchDirectory))
			{
				animDir = Path.Combine(searchDirectory, "anim");
				if (!Directory.Exists(animDir))
					animDir = searchDirectory;
			}
			else if (layoutWriter is DiskFileProvider disk && !string.IsNullOrEmpty(disk.Path))
			{
				string dir = Path.GetDirectoryName(disk.Path);
				if (!string.IsNullOrEmpty(dir))
				{
					string siblingAnim = Path.Combine(dir, "anim");
					if (Directory.Exists(siblingAnim))
						animDir = siblingAnim;
					else if (Directory.Exists(Path.Combine(Directory.GetParent(dir)?.FullName ?? "", "anim")))
						animDir = Path.Combine(Directory.GetParent(dir).FullName, "anim");
					else
						animDir = dir;
				}
			}

			if (!string.IsNullOrEmpty(animDir) && Directory.Exists(animDir))
			{
				foreach (var path in Directory.EnumerateFiles(animDir, "*.bflan"))
				{
					string file = Path.GetFileName(path);
					if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
						!string.Equals(Path.GetFileNameWithoutExtension(file), baseName, StringComparison.OrdinalIgnoreCase))
						continue;

					string key = "fs:" + path;
					if (!seen.Add(key))
						continue;

					try
					{
						results.Add(new LayoutAnimRef
						{
							DisplayName = Path.GetFileNameWithoutExtension(file),
							FilePath = path,
							Data = MaybeDecompress(File.ReadAllBytes(path)),
							Writer = new DiskFileProvider(path),
						});
					}
					catch
					{
						// skip unreadable
					}
				}
			}

			results.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
			return results;
		}

		public static BflanFile TryLoad(LayoutAnimRef animRef)
		{
			if (animRef?.Data == null || animRef.Data.Length < 8)
				return null;
			try
			{
				return new BflanFile(animRef.Data);
			}
			catch
			{
				return null;
			}
		}

		static string StripBflyt(string name)
		{
			if (string.IsNullOrEmpty(name))
				return name;
			if (name.EndsWith(".bflyt", StringComparison.OrdinalIgnoreCase))
				return name.Substring(0, name.Length - 6);
			return name;
		}

		static string NormalizeKey(string key) =>
			(key ?? "").Replace('\\', '/');

		static byte[] MaybeDecompress(byte[] data)
		{
			if (data == null || data.Length < 4)
				return data;
			string magic = System.Text.Encoding.ASCII.GetString(data, 0, 4);
			if (magic == "Yaz0")
				return SwitchThemes.Common.ManagedYaz0.Decompress(data);
			if (GameZstd.IsCompressed(data))
			{
				try { return GameZstd.Instance.Decompress(data); }
				catch { }
			}
			return data;
		}
	}
}
