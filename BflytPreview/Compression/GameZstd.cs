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
		private readonly Compressor _defaultCompressor;
		private readonly Dictionary<int, Compressor> _compressors = new Dictionary<int, Compressor>();
		private int _compressionLevel = 7;
		private bool _disposed;

		public int CompressionLevel
		{
			get => _compressionLevel;
			set
			{
				_compressionLevel = value;
				_defaultCompressor.Level = value;
				lock (_compressors)
				{
					foreach (var compressor in _compressors.Values)
						compressor.Level = value;
				}
			}
		}

		public GameZstd()
		{
			_defaultCompressor = new Compressor(_compressionLevel);
		}

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

		public byte[] Decompress(byte[] data) => Decompress(data, out _);

		public byte[] Decompress(byte[] data, out int zsDictionaryId)
		{
			if (!IsCompressed(data))
			{
				zsDictionaryId = -1;
				return data;
			}

			var size = GetDecompressedSize(data);
			var result = new byte[size];
			Decompress(data, result, out zsDictionaryId);
			return result;
		}

		public byte[] Compress(byte[] data, int zsDictionaryId = -1)
		{
			if (data == null)
				throw new ArgumentNullException(nameof(data));

			int bound = Compressor.GetCompressBound(data.Length);
			var buffer = new byte[bound];
			int written = Compress(data, buffer, zsDictionaryId);
			if (written == buffer.Length)
				return buffer;
			var result = new byte[written];
			Buffer.BlockCopy(buffer, 0, result, 0, written);
			return result;
		}

		public int Compress(byte[] data, byte[] dst, int zsDictionaryId = -1)
		{
			lock (_compressors)
			{
				if (_compressors.TryGetValue(zsDictionaryId, out var compressor))
					return compressor.Wrap(data, dst);
			}
			lock (_defaultCompressor)
			{
				return _defaultCompressor.Wrap(data, dst);
			}
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
				if (_decompressors.TryGetValue(dictId, out var oldDec))
					oldDec.Dispose();
				_decompressors[dictId] = decompressor;
			}

			var compressor = new Compressor(_compressionLevel);
			compressor.LoadDictionary(buffer);
			lock (_compressors)
			{
				if (_compressors.TryGetValue(dictId, out var oldComp))
					oldComp.Dispose();
				_compressors[dictId] = compressor;
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
			_defaultCompressor.Dispose();
			lock (_decompressors)
			{
				foreach (var decompressor in _decompressors.Values)
					decompressor.Dispose();
				_decompressors.Clear();
			}
			lock (_compressors)
			{
				foreach (var compressor in _compressors.Values)
					compressor.Dispose();
				_compressors.Clear();
			}
		}
	}
}
