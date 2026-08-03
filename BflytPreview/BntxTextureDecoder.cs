using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Syroot.NintenTools.NSW.Bntx;
using Syroot.NintenTools.NSW.Bntx.GFX;
using Toolbox.Library;

namespace BflytPreview
{
	/// <summary>
	/// Decodes a Syroot BNTX texture to raw RGBA8 (mip0) using Switch Toolbox deswizzle/decode.
	/// Channel remaps are NOT baked in — apply them when shading (see BntxPreviewCache).
	///
	/// Important: DirectXTex DecodeBlock + ConvertBgraToRgba mis-places channels for several
	/// single/dual-channel formats (BC5 ch0→Blue, R8G8 R→0). Those use dedicated expanders
	/// so TotK HUD swizzles like Red/Red/Red/Green and One/One/One/Red work.
	/// </summary>
	internal static class BntxTextureDecoder
	{
		public static bool TryDecodeRgba(Texture tex, out byte[] rgba, out int width, out int height)
		{
			rgba = null;
			width = 0;
			height = 0;
			try
			{
				if (tex == null || tex.Width == 0 || tex.Height == 0)
					return false;
				if (tex.TextureData == null || tex.TextureData.Count == 0 || tex.TextureData[0].Count == 0)
					return false;

				TEX_FORMAT format = ConvertFormat(tex.Format);
				byte[] linear = DeswizzleMip0(tex, format);
				if (linear == null || linear.Length == 0)
					return false;

				int w = (int)tex.Width;
				int h = (int)tex.Height;
				byte[] pixels = DecodeLinearToRgba(linear, w, h, format);
				if (pixels == null || pixels.Length < w * h * 4)
					return false;

				width = w;
				height = h;
				int stride = w * 4;

				// Flip vertically so V=0 matches layout top (GL TexImage2D treats first row as bottom).
				rgba = new byte[stride * h];
				for (int y = 0; y < h; y++)
					Buffer.BlockCopy(pixels, y * stride, rgba, (h - 1 - y) * stride, stride);
				return true;
			}
			catch
			{
				rgba = null;
				width = 0;
				height = 0;
				return false;
			}
		}

		/// <summary>
		/// Decode deswizzled linear bytes to RGBA8 with stable channel placement
		/// (R = first data channel) suitable for BNTX channel-select metadata.
		/// </summary>
		static byte[] DecodeLinearToRgba(byte[] linear, int width, int height, TEX_FORMAT format)
		{
			switch (format)
			{
				case TEX_FORMAT.BC5_UNORM:
					return DDSCompressor.DecompressBC5(linear, width, height, false, true);
				case TEX_FORMAT.BC5_SNORM:
					return DDSCompressor.DecompressBC5(linear, width, height, true, true);

				case TEX_FORMAT.BC4_UNORM:
					return ExpandBc4(linear, width, height, snorm: false);
				case TEX_FORMAT.BC4_SNORM:
					return ExpandBc4(linear, width, height, snorm: true);

				case TEX_FORMAT.R8_UNORM:
					return ExpandR8(linear, width, height);
				case TEX_FORMAT.R8G8_UNORM:
					return ExpandR8G8(linear, width, height);

				default:
					// ASTC / BC1 / RGBA / etc. DecodeBlock returns RGBA after ConvertBgraToRgba.
					return STGenericTexture.DecodeBlock(
						linear,
						(uint)width,
						(uint)height,
						format,
						Array.Empty<byte>(),
						new ImageParameters());
			}
		}

		/// <summary>R8 → R=G=B=v, A=255 (works for One/One/One/Red and Red/Red/Red/One).</summary>
		static byte[] ExpandR8(byte[] linear, int width, int height)
		{
			int n = width * height;
			if (linear.Length < n)
				return null;
			byte[] dst = new byte[n * 4];
			for (int i = 0; i < n; i++)
			{
				byte v = linear[i];
				int o = i * 4;
				dst[o] = v;
				dst[o + 1] = v;
				dst[o + 2] = v;
				dst[o + 3] = 255;
			}
			return dst;
		}

		/// <summary>R8G8 → R=ch0, G=ch1, B=0, A=255 (TotK Red/Red/Red/Green masks).</summary>
		static byte[] ExpandR8G8(byte[] linear, int width, int height)
		{
			int n = width * height;
			if (linear.Length < n * 2)
				return null;
			byte[] dst = new byte[n * 4];
			for (int i = 0; i < n; i++)
			{
				int s = i * 2;
				int o = i * 4;
				dst[o] = linear[s];
				dst[o + 1] = linear[s + 1];
				dst[o + 2] = 0;
				dst[o + 3] = 255;
			}
			return dst;
		}

		/// <summary>
		/// BC4 → grayscale RGBA (R=G=B=luma, A=255). Dedicated path avoids DirectXTex
		/// and matches Toolbox DecompressBC4 channel intent.
		/// </summary>
		static byte[] ExpandBc4(byte[] data, int width, int height, bool snorm)
		{
			// Reuse Toolbox bitmap decompressor, then read as 32bpp with R/G/B = luma.
			using (Bitmap bmp = DDSCompressor.DecompressBC4(data, width, height, snorm))
			{
				if (bmp == null)
					return null;
				return CopyBitmapRgba(bmp, width, height);
			}
		}

		static byte[] CopyBitmapRgba(Bitmap bmp, int width, int height)
		{
			var rect = new Rectangle(0, 0, Math.Min(width, bmp.Width), Math.Min(height, bmp.Height));
			var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
			try
			{
				byte[] dst = new byte[width * height * 4];
				int srcStride = data.Stride;
				byte[] row = new byte[Math.Abs(srcStride)];
				for (int y = 0; y < rect.Height; y++)
				{
					Marshal.Copy(IntPtr.Add(data.Scan0, y * srcStride), row, 0, rect.Width * 4);
					for (int x = 0; x < rect.Width; x++)
					{
						int si = x * 4;
						int di = (y * width + x) * 4;
						// Format32bppArgb memory order is B,G,R,A
						byte b = row[si], g = row[si + 1], r = row[si + 2], a = row[si + 3];
						dst[di] = r;
						dst[di + 1] = g;
						dst[di + 2] = b;
						dst[di + 3] = a;
					}
				}
				return dst;
			}
			finally
			{
				bmp.UnlockBits(data);
			}
		}

		static byte[] DeswizzleMip0(Texture tex, TEX_FORMAT format)
		{
			uint blkWidth = STGenericTexture.GetBlockWidth(format);
			uint blkHeight = STGenericTexture.GetBlockHeight(format);
			uint blkDepth = STGenericTexture.GetBlockDepth(format);
			uint bpp = STGenericTexture.GetBytesPerPixel(format);

			uint width = Math.Max(1, tex.Width);
			uint height = Math.Max(1, tex.Height);
			uint depth = Math.Max(1, tex.Depth);

			int linesPerBlockHeight = (1 << (int)tex.BlockHeightLog2) * 8;
			int blockHeightShift = 0;
			if (TegraX1Swizzle.pow2_round_up(TegraX1Swizzle.DIV_ROUND_UP(height, blkHeight)) < linesPerBlockHeight)
				blockHeightShift = 1;

			byte[] mipData = tex.TextureData[0][0];
			uint size = TegraX1Swizzle.DIV_ROUND_UP(width, blkWidth) *
			            TegraX1Swizzle.DIV_ROUND_UP(height, blkHeight) * bpp;

			byte[] result = TegraX1Swizzle.deswizzle(
				width, height, depth,
				blkWidth, blkHeight, blkDepth,
				1,
				bpp,
				(uint)tex.TileMode,
				(int)Math.Max(0, (int)tex.BlockHeightLog2 - blockHeightShift),
				mipData);

			if (result == null || result.Length < size)
				return result;

			byte[] trimmed = new byte[size];
			Array.Copy(result, 0, trimmed, 0, size);
			return trimmed;
		}

		public static TEX_FORMAT ConvertFormat(SurfaceFormat surfaceFormat)
		{
			switch (surfaceFormat)
			{
				case SurfaceFormat.BC1_UNORM: return TEX_FORMAT.BC1_UNORM;
				case SurfaceFormat.BC1_SRGB: return TEX_FORMAT.BC1_UNORM_SRGB;
				case SurfaceFormat.BC2_UNORM: return TEX_FORMAT.BC2_UNORM;
				case SurfaceFormat.BC2_SRGB: return TEX_FORMAT.BC2_UNORM_SRGB;
				case SurfaceFormat.BC3_UNORM: return TEX_FORMAT.BC3_UNORM;
				case SurfaceFormat.BC3_SRGB: return TEX_FORMAT.BC3_UNORM_SRGB;
				case SurfaceFormat.BC4_UNORM: return TEX_FORMAT.BC4_UNORM;
				case SurfaceFormat.BC4_SNORM: return TEX_FORMAT.BC4_SNORM;
				case SurfaceFormat.BC5_UNORM: return TEX_FORMAT.BC5_UNORM;
				case SurfaceFormat.BC5_SNORM: return TEX_FORMAT.BC5_SNORM;
				case SurfaceFormat.BC6_UFLOAT: return TEX_FORMAT.BC6H_UF16;
				case SurfaceFormat.BC6_FLOAT: return TEX_FORMAT.BC6H_SF16;
				case SurfaceFormat.BC7_UNORM: return TEX_FORMAT.BC7_UNORM;
				case SurfaceFormat.BC7_SRGB: return TEX_FORMAT.BC7_UNORM_SRGB;
				case SurfaceFormat.B5_G5_R5_A1_UNORM: return TEX_FORMAT.B5G5R5A1_UNORM;
				case SurfaceFormat.B5_G6_R5_UNORM: return TEX_FORMAT.B5G6R5_UNORM;
				case SurfaceFormat.B8_G8_R8_A8_SRGB: return TEX_FORMAT.B8G8R8A8_UNORM_SRGB;
				case SurfaceFormat.B8_G8_R8_A8_UNORM: return TEX_FORMAT.B8G8R8A8_UNORM;
				case SurfaceFormat.R10_G10_B10_A2_UNORM: return TEX_FORMAT.R10G10B10A2_UNORM;
				case SurfaceFormat.R4_G4_B4_A4_UNORM: return TEX_FORMAT.B4G4R4A4_UNORM;
				case SurfaceFormat.R5_G5_B5_A1_UNORM: return TEX_FORMAT.R5G5B5A1_UNORM;
				case SurfaceFormat.R5_G6_B5_UNORM: return TEX_FORMAT.B5G6R5_UNORM;
				case SurfaceFormat.R8_G8_B8_A8_SRGB: return TEX_FORMAT.R8G8B8A8_UNORM_SRGB;
				case SurfaceFormat.R8_G8_B8_A8_UNORM: return TEX_FORMAT.R8G8B8A8_UNORM;
				case SurfaceFormat.R8_G8_B8_A8_SNORM: return TEX_FORMAT.R8G8B8A8_SNORM;
				case SurfaceFormat.R8_UNORM: return TEX_FORMAT.R8_UNORM;
				case SurfaceFormat.R8_G8_UNORM: return TEX_FORMAT.R8G8_UNORM;
				case SurfaceFormat.ASTC_4x4_UNORM: return TEX_FORMAT.ASTC_4x4_UNORM;
				case SurfaceFormat.ASTC_4x4_SRGB: return TEX_FORMAT.ASTC_4x4_SRGB;
				case SurfaceFormat.ASTC_5x4_UNORM: return TEX_FORMAT.ASTC_5x4_UNORM;
				case SurfaceFormat.ASTC_5x4_SRGB: return TEX_FORMAT.ASTC_5x4_SRGB;
				case SurfaceFormat.ASTC_5x5_UNORM: return TEX_FORMAT.ASTC_5x5_UNORM;
				case SurfaceFormat.ASTC_5x5_SRGB: return TEX_FORMAT.ASTC_5x5_SRGB;
				case SurfaceFormat.ASTC_6x5_UNORM: return TEX_FORMAT.ASTC_6x5_UNORM;
				case SurfaceFormat.ASTC_6x5_SRGB: return TEX_FORMAT.ASTC_6x5_SRGB;
				case SurfaceFormat.ASTC_6x6_UNORM: return TEX_FORMAT.ASTC_6x6_UNORM;
				case SurfaceFormat.ASTC_6x6_SRGB: return TEX_FORMAT.ASTC_6x6_SRGB;
				case SurfaceFormat.ASTC_8x5_UNORM: return TEX_FORMAT.ASTC_8x5_UNORM;
				case SurfaceFormat.ASTC_8x5_SRGB: return TEX_FORMAT.ASTC_8x5_SRGB;
				case SurfaceFormat.ASTC_8x6_UNORM: return TEX_FORMAT.ASTC_8x6_UNORM;
				case SurfaceFormat.ASTC_8x6_SRGB: return TEX_FORMAT.ASTC_8x6_SRGB;
				case SurfaceFormat.ASTC_8x8_UNORM: return TEX_FORMAT.ASTC_8x8_UNORM;
				case SurfaceFormat.ASTC_8x8_SRGB: return TEX_FORMAT.ASTC_8x8_SRGB;
				case SurfaceFormat.ASTC_10x5_UNORM: return TEX_FORMAT.ASTC_10x5_UNORM;
				case SurfaceFormat.ASTC_10x5_SRGB: return TEX_FORMAT.ASTC_10x5_SRGB;
				case SurfaceFormat.ASTC_10x6_UNORM: return TEX_FORMAT.ASTC_10x6_UNORM;
				case SurfaceFormat.ASTC_10x6_SRGB: return TEX_FORMAT.ASTC_10x6_SRGB;
				case SurfaceFormat.ASTC_10x8_UNORM: return TEX_FORMAT.ASTC_10x8_UNORM;
				case SurfaceFormat.ASTC_10x8_SRGB: return TEX_FORMAT.ASTC_10x8_SRGB;
				case SurfaceFormat.ASTC_10x10_UNORM: return TEX_FORMAT.ASTC_10x10_UNORM;
				case SurfaceFormat.ASTC_10x10_SRGB: return TEX_FORMAT.ASTC_10x10_SRGB;
				case SurfaceFormat.ASTC_12x10_UNORM: return TEX_FORMAT.ASTC_12x10_UNORM;
				case SurfaceFormat.ASTC_12x10_SRGB: return TEX_FORMAT.ASTC_12x10_SRGB;
				case SurfaceFormat.ASTC_12x12_UNORM: return TEX_FORMAT.ASTC_12x12_UNORM;
				case SurfaceFormat.ASTC_12x12_SRGB: return TEX_FORMAT.ASTC_12x12_SRGB;
				default:
					throw new NotSupportedException($"Unsupported BNTX surface format: {surfaceFormat}");
			}
		}
	}
}
