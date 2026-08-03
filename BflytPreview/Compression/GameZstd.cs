using System;
using System.Collections.Generic;
using System.IO;
using SARCExt;
using ZstdSharp;

namespace BflytPreview.Compression
{
	/// <summary>
	/// TotK-style ZSTD helper adapted from TKMM's TkZstd (ZstdSharp + ZsDic.pack dictionaries).
	/// </summary>
	public sealed class GameZstd : IDisposable
	{
		public const uint ZstdMagic = 0xFD2FB528;
		private const uint DictMagic = 0xEC30A437;
		private const uint SarcMagic = 0x43524153;

		private static readonly object Sync = new object();
		private static GameZstd _instance;

		private readonly Decompressor _defaultDecompressor = new Decompressor();
		private readonly Dictionary<int, Decompressor> _decompressors = new Dictionary<int, Decompressor>();
		private bool _disposed;

		public static GameZstd Instance
		{
			get
			{
				lock (Sync)
				{
					if (_instance == null)
						_instance = CreateFromSettings();
					return _instance;
				}
			}
		}

		public static void ReloadFromSettings()
		{
			lock (Sync)
			{
				_instance?.Dispose();
				_instance = CreateFromSettings();
			}
		}

		private static GameZstd CreateFromSettings()
		{
			var zstd = new GameZstd();
			var path = Settings.Default.ZsDicPackPath;
			if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
			{
				try
				{
					zstd.LoadDictionariesFromFile(path);
				}
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine("Failed to load ZsDic pack: " + ex);
				}
			}
			return zstd;
		}

		public static bool IsCompressed(byte[] data)
		{
			return data != null && data.Length > 3 && ReadUInt32(data, 0) == ZstdMagic;
		}

		public byte[] Decompress(byte[] data)
		{
			if (!IsCompressed(data))
				return data;

			var size = GetDecompressedSize(data);
			var result = new byte[size];
			Decompress(data, result, out _);
			return result;
		}

		public void Decompress(byte[] data, byte[] dst, out int zsDictionaryId)
		{
			if (!IsCompressed(data))
			{
				zsDictionaryId = -1;
				return;
			}

			zsDictionaryId = GetDictionaryId(data);
			try
			{
				lock (_decompressors)
				{
					if (_decompressors.TryGetValue(zsDictionaryId, out var decompressor))
					{
						decompressor.Unwrap(data, dst);
						return;
					}
				}

				lock (_defaultDecompressor)
				{
					_defaultDecompressor.Unwrap(data, dst);
				}
			}
			catch (Exception ex)
			{
				var hint = zsDictionaryId >= 0
					? " This frame requires ZSTD dictionary id " + zsDictionaryId +
					  ". Set Settings → ZsDic.pack.zs to your game dump's Pack\\ZsDic.pack.zs."
					: string.Empty;
				throw new InvalidOperationException("Failed to decompress ZSTD data." + hint, ex);
			}
		}

		public void LoadDictionariesFromFile(string path)
		{
			LoadDictionaries(File.ReadAllBytes(path));
		}

		public void LoadDictionaries(byte[] data)
		{
			if (data == null || data.Length == 0)
				return;

			byte[] working = data;
			if (IsCompressed(working))
			{
				var size = GetDecompressedSize(working);
				var decompressed = new byte[size];
				// ZsDic.pack.zs itself uses the default (no-dict) decompressor.
				lock (_defaultDecompressor)
				{
					_defaultDecompressor.Unwrap(working, decompressed);
				}
				working = decompressed;
			}

			if (TryLoadDictionary(working))
				return;

			if (working.Length < 8 || ReadUInt32(working, 0) != SarcMagic)
				return;

			var sarc = SARC.Unpack(working);
			foreach (var file in sarc.Files.Values)
				TryLoadDictionary(file);
		}

		private bool TryLoadDictionary(byte[] buffer)
		{
			if (buffer == null || buffer.Length < 8 || ReadUInt32(buffer, 0) != DictMagic)
				return false;

			var dictId = BitConverter.ToInt32(buffer, 4);
			var decompressor = new Decompressor();
			decompressor.LoadDictionary(buffer);
			lock (_decompressors)
			{
				if (_decompressors.TryGetValue(dictId, out var old))
					old.Dispose();
				_decompressors[dictId] = decompressor;
			}
			return true;
		}

		public static int GetDecompressedSize(byte[] data) => GetFrameContentSize(data);

		public static int GetDictionaryId(byte[] buffer)
		{
			var descriptor = buffer[4];
			var windowDescriptorSize = ((descriptor & 0b00100000) >> 5) ^ 0b1;
			var dictionaryIdFlag = descriptor & 0b00000011;
			var offset = 5 + windowDescriptorSize;

			switch (dictionaryIdFlag)
			{
				case 0x0:
					return -1;
				case 0x1:
					return buffer[offset];
				case 0x2:
					return BitConverter.ToUInt16(buffer, offset);
				case 0x3:
					return BitConverter.ToInt32(buffer, offset);
				default:
					throw new OverflowException("Invalid ZSTD dictionary id flag.");
			}
		}

		private static int GetFrameContentSize(byte[] buffer)
		{
			var descriptor = buffer[4];
			var windowDescriptorSize = ((descriptor & 0b00100000) >> 5) ^ 0b1;
			var dictionaryIdFlag = descriptor & 0b00000011;
			var frameContentFlag = descriptor >> 6;

			int offset;
			switch (dictionaryIdFlag)
			{
				case 0x0:
					offset = 5 + windowDescriptorSize;
					break;
				case 0x1:
					offset = 5 + windowDescriptorSize + 1;
					break;
				case 0x2:
					offset = 5 + windowDescriptorSize + 2;
					break;
				case 0x3:
					offset = 5 + windowDescriptorSize + 4;
					break;
				default:
					throw new OverflowException("Invalid ZSTD dictionary id flag.");
			}

			switch (frameContentFlag)
			{
				case 0x0:
					return buffer[offset];
				case 0x1:
					return BitConverter.ToUInt16(buffer, offset) + 0x100;
				case 0x2:
					return BitConverter.ToInt32(buffer, offset);
				default:
					throw new NotSupportedException("64-bit ZSTD frame sizes are not supported.");
			}
		}

		private static uint ReadUInt32(byte[] data, int offset) =>
			BitConverter.ToUInt32(data, offset);

		public void Dispose()
		{
			if (_disposed)
				return;
			_disposed = true;
			_defaultDecompressor.Dispose();
			lock (_decompressors)
			{
				foreach (var decompressor in _decompressors.Values)
					decompressor.Dispose();
				_decompressors.Clear();
			}
		}
	}
}
