using System;
using System.Collections.Generic;
using System.IO;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using Syroot.NintenTools.NSW.Bntx;
using Syroot.NintenTools.NSW.Bntx.GFX;
using SwitchThemes.Common.Bflyt;

namespace BflytPreview
{
	/// <summary>
	/// BNTX preview cache. Uploads textures with layout shading baked in:
	/// channel swizzle + white/black interpolate (with sRGB gamma on both colors), so drawing
	/// can stay on fixed-function GL (OpenTK GLControl does not reliably run our custom Pic1
	/// shaders).
	/// </summary>
	internal sealed class BntxPreviewCache : IDisposable
	{
		struct RawTexture
		{
			public byte[] Rgba; // decoded mip0, vertically flipped, RGBA8
			public int Width;
			public int Height;
			public ChannelType Red;
			public ChannelType Green;
			public ChannelType Blue;
			public ChannelType Alpha;
		}

		struct ShadedKey : IEquatable<ShadedKey>
		{
			public string Name;
			public int Wr, Wg, Wb, Wa;
			public int Br, Bg, Bb, Ba;

			public bool Equals(ShadedKey other) =>
				Name == other.Name &&
				Wr == other.Wr && Wg == other.Wg && Wb == other.Wb && Wa == other.Wa &&
				Br == other.Br && Bg == other.Bg && Bb == other.Bb && Ba == other.Ba;

			public override bool Equals(object obj) => obj is ShadedKey k && Equals(k);

			public override int GetHashCode()
			{
				unchecked
				{
					int h = Name?.GetHashCode() ?? 0;
					h = h * 397 ^ Wr; h = h * 397 ^ Wg; h = h * 397 ^ Wb; h = h * 397 ^ Wa;
					h = h * 397 ^ Br; h = h * 397 ^ Bg; h = h * 397 ^ Bb; h = h * 397 ^ Ba;
					return h;
				}
			}
		}

		readonly Dictionary<string, RawTexture> rawTextures = new Dictionary<string, RawTexture>(StringComparer.Ordinal);
		readonly Dictionary<ShadedKey, int> shadedGl = new Dictionary<ShadedKey, int>();
		readonly HashSet<string> failed = new HashSet<string>(StringComparer.Ordinal);
		BntxFile bntxFile;
		bool disposed;

		public bool HasBntx => bntxFile != null;

		public void Load(byte[] bntxData)
		{
			DisposeGlTextures();
			rawTextures.Clear();
			failed.Clear();
			bntxFile = null;

			if (bntxData == null || bntxData.Length < 8)
				return;

			try
			{
				using (var ms = new MemoryStream(bntxData, writable: false))
					bntxFile = new BntxFile(ms);
			}
			catch
			{
				bntxFile = null;
			}
		}

		/// <summary>
		/// Bind a Pic1 texture with channel swizzle + white/black interpolate baked.
		/// </summary>
		public bool BindPic1Texture(
			BflytFile layout,
			Pic1Pane pane,
			Vector4 white,
			Vector4 black,
			out int texId,
			out BflytMaterial.TextureReference.WRAPS wrapS,
			out BflytMaterial.TextureReference.WRAPS wrapT)
		{
			if (pane == null)
			{
				texId = 0;
				wrapS = BflytMaterial.TextureReference.WRAPS.Clamp;
				wrapT = BflytMaterial.TextureReference.WRAPS.Clamp;
				return false;
			}
			return BindMaterialTexture(layout, pane.MaterialIndex, white, black, out texId, out wrapS, out wrapT);
		}

		/// <summary>
		/// Bind material texture 0 with channel swizzle + white/black interpolate baked
		/// (Pic1 / window frames / content).
		/// </summary>
		public bool BindMaterialTexture(
			BflytFile layout,
			ushort materialIndex,
			Vector4 white,
			Vector4 black,
			out int texId,
			out BflytMaterial.TextureReference.WRAPS wrapS,
			out BflytMaterial.TextureReference.WRAPS wrapT)
		{
			texId = 0;
			wrapS = BflytMaterial.TextureReference.WRAPS.Clamp;
			wrapT = BflytMaterial.TextureReference.WRAPS.Clamp;

			if (layout?.Mat1?.Materials == null || layout.Tex1?.Textures == null)
				return false;
			if (materialIndex >= layout.Mat1.Materials.Count)
				return false;

			var mat = layout.Mat1.Materials[materialIndex];
			if (mat.Textures == null || mat.Textures.Length == 0)
				return false;

			var texRef = mat.Textures[0];
			wrapS = texRef.WrapS;
			wrapT = texRef.WrapT;
			if (texRef.TextureId >= layout.Tex1.Textures.Count)
				return false;

			string name = layout.Tex1.Textures[texRef.TextureId];
			if (!TryGetRaw(name, out var raw))
				return false;

			var key = MakeKey(name, white, black);
			if (!shadedGl.TryGetValue(key, out texId))
			{
				byte[] shaded = BakeShaded(raw, white, black);
				texId = UploadRgba(shaded, raw.Width, raw.Height);
				shadedGl[key] = texId;
			}

			GL.BindTexture(TextureTarget.Texture2D, texId);
			return true;
		}

		public bool TryGetTextureSize(BflytFile layout, ushort materialIndex, out float width, out float height)
		{
			width = 0;
			height = 0;
			if (layout?.Mat1?.Materials == null || layout.Tex1?.Textures == null)
				return false;
			if (materialIndex >= layout.Mat1.Materials.Count)
				return false;
			var mat = layout.Mat1.Materials[materialIndex];
			if (mat.Textures == null || mat.Textures.Length == 0)
				return false;
			var texRef = mat.Textures[0];
			if (texRef.TextureId >= layout.Tex1.Textures.Count)
				return false;
			if (!TryGetRaw(layout.Tex1.Textures[texRef.TextureId], out var raw))
				return false;
			width = raw.Width;
			height = raw.Height;
			return true;
		}

		static ShadedKey MakeKey(string name, Vector4 white, Vector4 black) => new ShadedKey
		{
			Name = name,
			Wr = Quantize(white.X), Wg = Quantize(white.Y), Wb = Quantize(white.Z), Wa = Quantize(white.W),
			Br = Quantize(black.X), Bg = Quantize(black.Y), Bb = Quantize(black.Z), Ba = Quantize(black.W),
		};

		static int Quantize(float v) => (int)Math.Round(Math.Max(0f, Math.Min(1f, v)) * 255f);

		bool TryGetRaw(string textureName, out RawTexture raw)
		{
			raw = default;
			if (disposed || bntxFile == null || string.IsNullOrEmpty(textureName))
				return false;

			if (rawTextures.TryGetValue(textureName, out raw))
				return true;

			if (failed.Contains(textureName))
				return false;

			Texture tex = FindTexture(textureName);
			if (tex == null || !BntxTextureDecoder.TryDecodeRgba(tex, out byte[] rgba, out int width, out int height))
			{
				failed.Add(textureName);
				return false;
			}

			raw = new RawTexture
			{
				Rgba = rgba,
				Width = width,
				Height = height,
				Red = tex.ChannelRed,
				Green = tex.ChannelGreen,
				Blue = tex.ChannelBlue,
				Alpha = tex.ChannelAlpha
			};
			rawTextures[textureName] = raw;
			return true;
		}

		/// <summary>
		/// Apply BNTX channel select then interpolate: rgb = white*tex + black*(1-tex),
		/// a = tex.a*white.a. Both white and black get the same sRGB gamma lift that legacy
		/// Bflyt.frag applied only to white — without it, BlackColor HUD fills (hearts etc.)
		/// stay chocolate-dark vs the muted tan the game shows.
		///
		/// Caller must pass WhiteColor/BlackColor in Nintendo order (not SwitchThemesCommon's
		/// misnamed Foreground/Background — those are Black then White in the file).
		/// </summary>
		static byte[] BakeShaded(RawTexture raw, Vector4 white, Vector4 black)
		{
			float wr = LayoutDisplayColor.LiftChannel(white.X);
			float wg = LayoutDisplayColor.LiftChannel(white.Y);
			float wb = LayoutDisplayColor.LiftChannel(white.Z);
			float wa = white.W > 0f ? white.W : 1f;

			float br = LayoutDisplayColor.LiftChannel(black.X);
			float bg = LayoutDisplayColor.LiftChannel(black.Y);
			float bb = LayoutDisplayColor.LiftChannel(black.Z);

			byte[] src = raw.Rgba;
			byte[] dst = new byte[src.Length];
			for (int i = 0; i < src.Length; i += 4)
			{
				float r = src[i] / 255f;
				float g = src[i + 1] / 255f;
				float b = src[i + 2] / 255f;
				float a = src[i + 3] / 255f;

				float sr = Select(raw.Red, r, g, b, a);
				float sg = Select(raw.Green, r, g, b, a);
				float sb = Select(raw.Blue, r, g, b, a);
				float sa = Select(raw.Alpha, r, g, b, a);

				float or = wr * sr + br * (1f - sr);
				float og = wg * sg + bg * (1f - sg);
				float ob = wb * sb + bb * (1f - sb);
				float oa = sa * wa;

				dst[i] = ToByte(or);
				dst[i + 1] = ToByte(og);
				dst[i + 2] = ToByte(ob);
				dst[i + 3] = ToByte(oa);
			}
			return dst;
		}

		static float Select(ChannelType ch, float r, float g, float b, float a)
		{
			switch (ch)
			{
				case ChannelType.Red: return r;
				case ChannelType.Green: return g;
				case ChannelType.Blue: return b;
				case ChannelType.Alpha: return a;
				case ChannelType.Zero: return 0f;
				case ChannelType.One: return 1f;
				default: return r;
			}
		}

		static byte ToByte(float v) =>
			(byte)Math.Max(0, Math.Min(255, (int)Math.Round(v * 255f)));

		Texture FindTexture(string name)
		{
			Texture exact = null;
			Texture ignoreCase = null;
			Texture withoutSuffix = null;
			string baseName = StripLayoutSuffix(name);

			foreach (var tex in bntxFile.Textures)
			{
				if (exact == null && string.Equals(tex.Name, name, StringComparison.Ordinal))
					exact = tex;
				else if (ignoreCase == null && string.Equals(tex.Name, name, StringComparison.OrdinalIgnoreCase))
					ignoreCase = tex;
				else if (withoutSuffix == null && baseName != null &&
				         string.Equals(StripLayoutSuffix(tex.Name) ?? tex.Name, baseName, StringComparison.OrdinalIgnoreCase))
					withoutSuffix = tex;
			}

			return exact ?? ignoreCase ?? withoutSuffix;
		}

		static string StripLayoutSuffix(string name)
		{
			if (string.IsNullOrEmpty(name))
				return null;
			int caret = name.IndexOf('^');
			return caret > 0 ? name.Substring(0, caret) : null;
		}

		static int UploadRgba(byte[] rgba, int width, int height)
		{
			GL.GenTextures(1, out int tex);
			GL.BindTexture(TextureTarget.Texture2D, tex);
			GL.TexImage2D(
				TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
				width, height, 0,
				PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
			return tex;
		}

		void DisposeGlTextures()
		{
			foreach (var id in shadedGl.Values)
			{
				int tex = id;
				try { GL.DeleteTextures(1, ref tex); }
				catch { /* GL context may already be gone */ }
			}
			shadedGl.Clear();
		}

		public void Dispose()
		{
			if (disposed) return;
			disposed = true;
			DisposeGlTextures();
			rawTextures.Clear();
			bntxFile = null;
		}
	}
}
