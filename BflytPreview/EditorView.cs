using SwitchThemes.Common;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using OpenTK.Platform;
using BflytPreview.EditorForms;
using BflytPreview.Rendering;
using System.Threading.Tasks;
using SwitchThemes.Common.Bflyt;
using static SwitchThemes.Common.Bflyt.BflytFile;

namespace BflytPreview
{
	public partial class EditorView : Form
	{
		BflytFile layout;
		/// <summary>Layout used for material/texture index lookup (host or active part).</summary>
		BflytFile activeLayout;
		PartsLayoutCache partsCache;
		PatternAnimCache patternAnimCache;
		/// <summary>Active FLTP overrides (material name → texture name) while drawing a part instance.</summary>
		Dictionary<string, string> activePatternTextures;
		IFileWriter _saveTo;
		readonly BntxPreviewCache bntxPreview = new BntxPreviewCache();
		readonly Dictionary<string, int> textGlTextures = new Dictionary<string, int>();
		public IFileWriter SaveTo
		{
			get => _saveTo;
			set
			{
				_saveTo?.EditorClosed();
				_saveTo = value;
				saveToolStripMenuItem.Visible = _saveTo != null;
				this.Text = value?.ToString() ?? "";
			}
		}

		double zoomFactor => zoomSlider.Value / 10f;

		float x = 640, y = -360;
		private Point firstPoint = new Point();

		bool canMoveView;

		OpenTK.GLControl glControl;

		public static int texture;

		enum CanvasDragMode
		{
			None,
			Marquee,
			Pan,
			MoveObject
		}

		CanvasDragMode canvasDragMode;
		Point marqueeStartClient;
		Point marqueeEndClient;
		readonly HashSet<Pan1Pane> marqueePreviewHits = new HashSet<Pan1Pane>();
		readonly Dictionary<Pan1Pane, PaneScreenQuad> paneScreenBounds = new Dictionary<Pan1Pane, PaneScreenQuad>();

		struct PaneScreenQuad
		{
			public float X0, Y0, X1, Y1, X2, Y2, X3, Y3;

			public bool FullyInside(int selX0, int selY0, int selX1, int selY1) =>
				CornerInside(X0, Y0, selX0, selY0, selX1, selY1) &&
				CornerInside(X1, Y1, selX0, selY0, selX1, selY1) &&
				CornerInside(X2, Y2, selX0, selY0, selX1, selY1) &&
				CornerInside(X3, Y3, selX0, selY0, selX1, selY1);

			static bool CornerInside(float x, float y, int x0, int y0, int x1, int y1) =>
				x >= x0 && x <= x1 && y >= y0 && y <= y1;
		}

		// This represents the whole file while the other treeview roots represent the logical hierarchy
		TreeNode AllPanesRoot;
		TreeNode Pan1Root;
		TreeNode Grp1Root;
		TreeNode TexturesRoot;
		TreeNode MaterialsRoot;

		// When set, panes whose materials use this palette color are drawn with SelectedColor
		bool hasPaletteHighlight;
		RGBAColor paletteHighlightColor;
		PaletteWindow paletteWindow;
		MaterialPaletteTag currentPalette;
		static readonly Color FilterRootOutlineColor = Color.FromArgb(0, 180, 220);

		internal PaneColorFilter PaneFilter { get; } = new PaneColorFilter();
		readonly LayoutUndoStack undoStack = new LayoutUndoStack();
		byte[] propertyGridBaseline;
		TreeNode checkRangeAnchor;
		bool suppressTreeCheckEvents;

		public EditorView(BflytFile _layout, IFileWriter saveTo, byte[] bntxData = null, PartsLayoutCache parts = null)
		{
			KeyPreview = true;

			InitializeComponent();
			layout = _layout;
			activeLayout = _layout;
			partsCache = parts;
			patternAnimCache = PatternAnimCache.FromPartsCache(parts);
			if (bntxData != null)
				bntxPreview.Load(bntxData);

			treeView1.NodeMouseClick += (sender, args) => treeView1.SelectedNode = args.Node;

			zoomSlider.BringToFront();

			glControl = new OpenTK.GLControl();
			glControl.Dock = DockStyle.Fill;
			panel1.Controls.Add(glControl);
			glControl.KeyDown += new KeyEventHandler(glControl_KeyDown);
			glControl.Resize += new EventHandler(glControl_Resize);
			glControl.Paint += GlControl_Paint;
			glControl.MouseDown += glControl_MouseDown;
			glControl.MouseMove += glControl_MouseMove;
			glControl.MouseUp += GlControl_MouseUp;
			glControl.MouseEnter += (s, e) => glControl.Focus();
			glControl.MouseWheel += glControl_MouseWheel;

			SaveTo = saveTo;
			showSubpanesToolStripMenuItem.Checked = Settings.Default.PreviewSubLayouts;
		}

        #region OnLoad

        private void EditorView_Load(object sender, System.EventArgs e)
		{
			bringToFront();
			glControl_Resize(glControl, EventArgs.Empty);

			UpdateView();
			Render();

			Task ignRes = setMoveView();

			/*Text =
				GL.GetString(StringName.Vendor) + " " +
				GL.GetString(StringName.Renderer) + " " +
				GL.GetString(StringName.Version);*/
		}

		private async Task setMoveView()
		{
			await Task.Delay(500);
			canMoveView = true;
		}

		#endregion

		#region GLControl.Resize event handler

		void glControl_Resize(object sender, EventArgs e)
		{
			OpenTK.GLControl c = sender as OpenTK.GLControl;

			if (c.ClientSize.Height == 0)
				c.ClientSize = new System.Drawing.Size(c.ClientSize.Width, 1);

			GL.Viewport(0, 0, c.ClientSize.Width, c.ClientSize.Height);
			glControl.Invalidate();
			/*float aspect_ratio = panel1.Width / (float)panel1.Height;
            Matrix4 perpective = Matrix4.CreatePerspectiveFieldOfView(MathHelper.PiOver4, aspect_ratio, 1, 64);
            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadMatrix(ref perpective);*/
		}

		#endregion

		#region GLControl.KeyDown event handler

		void glControl_KeyDown(object sender, KeyEventArgs e)
		{
			/*switch (e.KeyData)
            {
                case Keys.Escape:
                    this.Close();
                    break;
            }*/
		}

		#endregion

		#region GLControl.Paint event handler

		private void GlControl_Paint(object sender, PaintEventArgs e)
		{
			this.glControl.Context.MakeCurrent(this.glControl.WindowInfo);
			Render();
		}

		#endregion

		#region private void Render()

		private void Render()
		{
			GL.MatrixMode(MatrixMode.Projection);
			GL.LoadIdentity();
			GL.Ortho(0, glControl.Width, glControl.Height, 0, -1, 1);
			GL.MatrixMode(MatrixMode.Modelview);
			GL.LoadIdentity();

			GL.Clear(ClearBufferMask.ColorBufferBit);
			GL.ClearColor(160 / 255f, 160 / 255f, 160 / 255f, 1); //Control dark color

			if (texture != 0 && Settings.Default.ShowImage)
				DrawBgImage();

			RenderPanes();
			DrawMarqueeOverlay();

			glControl.SwapBuffers();
		}

		#endregion

		#region Draw Background Image
		void DrawBgImage()
		{
			GL.Enable(EnableCap.Texture2D);
			GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
			GL.MatrixMode(MatrixMode.Modelview);
			GL.LoadIdentity();
			GL.PushMatrix();
			GL.Scale(1 * zoomFactor, -1 * zoomFactor, 1);
			GL.Translate(x, y, 0);

			GL.Color4(Color.White);

			GL.BindTexture(TextureTarget.Texture2D, texture);
			GL.Begin(PrimitiveType.Quads);

			GL.TexCoord2(-1, -1);
			GL.Vertex3(-640, -360, 0);

			GL.TexCoord2(0, -1);
			GL.Vertex3(640, -360, 0);

			GL.TexCoord2(0, 0);
			GL.Vertex3(640, 360, 0);

			GL.TexCoord2(-1, 0);
			GL.Vertex3(-640, 360, 0);
			GL.End();
			GL.BindTexture(TextureTarget.Texture2D, 0);
			GL.PopMatrix();
		}
		#endregion

		void RenderPanes()
		{
			float[] DrawOnTopTransform = new float[16];
			Pan1Pane DrawOnTop = null;
			paneScreenBounds.Clear();

			GL.Enable(EnableCap.Blend);
			GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
			GL.Enable(EnableCap.Texture2D);

			void RecursiveRenderPane(Pan1Pane p)
			{
				if (!p.ParentVisibility)
					return;

				var color = Settings.Default.PaneColor;

				GL.PushMatrix();
				GL.Translate(p.Position.X, p.Position.Y, 0);
				GL.Rotate(p.Rotation.Z, p.Rotation.X, p.Rotation.Y, p.Rotation.Z);
				GL.Scale(p.Scale.X, p.Scale.Y, 1);

				if (p.ViewInEditor)
				{
					CachePaneScreenBounds(p);

					bool isTreeSelected = treeView1.SelectedNode != null && (p == treeView1.SelectedNode.Tag as Pan1Pane);
					bool isPaletteHit = hasPaletteHighlight && PaneUsesPaletteColor(p, paletteHighlightColor);
					bool isFilterRoot = PaneFilter.IsFilterRoot(p);
					bool isMarqueeHit = marqueePreviewHits.Contains(p);

					if (p is Pic1Pane pic)
						DrawPicturePane(pic);
					else if (p is Wnd1Pane wnd)
						DrawWindowPane(wnd);
					else if (p is Txt1Pane txt)
						DrawTextPane(txt);
					else if (p is Prt1Pane prt)
						DrawPartsPane(prt);

					GL.Disable(EnableCap.Texture2D);
					if (showPaneFramesToolStripMenuItem.Checked || isMarqueeHit)
					{
						if (isTreeSelected && showPaneFramesToolStripMenuItem.Checked)
						{
							DrawOnTop = p;
							GL.GetFloat(GetPName.ModelviewMatrix, DrawOnTopTransform);
						}
						else if (isMarqueeHit)
							DrawPane(p.transformedRect, Settings.Default.SelectedColor);
						else if (showPaneFramesToolStripMenuItem.Checked)
						{
							if (isPaletteHit)
								DrawPane(p.transformedRect, Settings.Default.SelectedColor);
							else if (isFilterRoot)
								DrawPane(p.transformedRect, FilterRootOutlineColor);
							else
								DrawPane(p.transformedRect, color);
						}
					}
					GL.Enable(EnableCap.Texture2D);
				}

				foreach (var c in p.Children.Where(x => x is Pan1Pane))
					RecursiveRenderPane((Pan1Pane)c);
				GL.PopMatrix();
			}

			GL.Scale(1 * zoomFactor, -1 * zoomFactor, 1);
			GL.Translate(x, y, 0);

			RecursiveRenderPane(layout.ElementsRoot);
			if (showPaneFramesToolStripMenuItem.Checked)
			{
				var root = layout.ElementsRoot;
				int rw = Math.Max(1, (int)root.Size.X);
				int rh = Math.Max(1, (int)root.Size.Y);
				DrawPane(new CusRectangle(-rw / 2, -rh / 2, rw, rh), Settings.Default.OutlineColor);
			}

			if (DrawOnTop != null && showPaneFramesToolStripMenuItem.Checked)
			{
				GL.LoadMatrix(DrawOnTopTransform);
                DrawPane(DrawOnTop.transformedRect, Settings.Default.SelectedColor);
                DrawPaneMiddlePoint(DrawOnTop.transformedRect, Settings.Default.SelectedColor);
            }

			GL.Disable(EnableCap.Texture2D);
			GL.Disable(EnableCap.Blend);
		}

		/// <summary>
		/// Pic1 fill matching Switch Toolbox shading intent on fixed-function GL:
		/// channel swizzle + white/black interpolate are baked into the bound texture
		/// (see BntxPreviewCache); draw modulates by vertex color and applies UV SRT.
		/// Custom GLSL is intentionally avoided — it repeatedly blanked the OpenTK preview.
		/// </summary>
		void DrawPicturePane(Pic1Pane pic)
		{
			BflytMaterial mat = null;
			if (activeLayout.Mat1?.Materials != null && pic.MaterialIndex < activeLayout.Mat1.Materials.Count)
				mat = activeLayout.Mat1.Materials[pic.MaterialIndex];

			// SwitchThemesCommon names are misleading: BFLYT stores BlackColor then WhiteColor
			// (same order as Switch Toolbox). ForegroundColor == Black, BackgroundColor == White.
			var black = mat != null ? ToVec4(mat.ForegroundColor) : new Vector4(0f, 0f, 0f, 0f);
			var white = mat != null ? ToVec4(mat.BackgroundColor) : new Vector4(1f, 1f, 1f, 1f);
			if (white.W <= 0f)
				white.W = 1f;

			bool hasTexture = false;
			int texId = 0;
			var wrapS = BflytMaterial.TextureReference.WRAPS.Clamp;
			var wrapT = BflytMaterial.TextureReference.WRAPS.Clamp;
			Matrix4 texTransform = LayoutPic1Shader.IdentityTransform;

			if (mat?.Textures != null && mat.Textures.Length > 0)
			{
				hasTexture = bntxPreview.BindPic1Texture(activeLayout, pic, white, black, out texId, out wrapS, out wrapT);
				if (mat.TextureTransformations != null && mat.TextureTransformations.Length > 0)
				{
					var t = mat.TextureTransformations[0];
					texTransform = LayoutPic1Shader.BuildTextureTransform(t.X, t.Y, t.Rotation, t.ScaleX, t.ScaleY);
				}
			}

			float paneAlpha = pic.Alpha / 255f;
			if (paneAlpha <= 0f)
				paneAlpha = 1f;

			Vector4 cTL = ToVertexVec4(pic.ColorTopLeft, paneAlpha);
			Vector4 cTR = ToVertexVec4(pic.ColorTopRight, paneAlpha);
			Vector4 cBL = ToVertexVec4(pic.ColorBottomLeft, paneAlpha);
			Vector4 cBR = ToVertexVec4(pic.ColorBottomRight, paneAlpha);
			if (cTL.W <= 0f && cTR.W <= 0f && cBL.W <= 0f && cBR.W <= 0f)
			{
				cTL.W = cTR.W = cBL.W = cBR.W = paneAlpha;
			}

			var rect = pic.transformedRect;
			var uv = (pic.UVCoords != null && pic.UVCoords.Length > 0)
				? pic.UVCoords[0]
				: new Pic1Pane.UVCoord
				{
					TopLeft = (0, 0),
					TopRight = (1, 0),
					BottomLeft = (0, 1),
					BottomRight = (1, 1)
				};

			GL.ActiveTexture(TextureUnit.Texture0);
			if (hasTexture)
			{
				GL.Enable(EnableCap.Texture2D);
				GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)ToGlWrap(wrapS));
				GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)ToGlWrap(wrapT));
			}
			else
			{
				GL.BindTexture(TextureTarget.Texture2D, 0);
				GL.Disable(EnableCap.Texture2D);
			}

			// Shading already baked into texture; only modulate by vertex color.
			GL.Begin(PrimitiveType.Quads);
			if (hasTexture)
			{
				GL.Color4(cTL.X, cTL.Y, cTL.Z, cTL.W);
				EmitTransformedTexCoord(texTransform, uv.TopLeft.X, uv.TopLeft.Y);
				GL.Vertex2(rect.x, rect.y);
				GL.Color4(cTR.X, cTR.Y, cTR.Z, cTR.W);
				EmitTransformedTexCoord(texTransform, uv.TopRight.X, uv.TopRight.Y);
				GL.Vertex2(rect.x + rect.width, rect.y);
				GL.Color4(cBR.X, cBR.Y, cBR.Z, cBR.W);
				EmitTransformedTexCoord(texTransform, uv.BottomRight.X, uv.BottomRight.Y);
				GL.Vertex2(rect.x + rect.width, rect.y + rect.height);
				GL.Color4(cBL.X, cBL.Y, cBL.Z, cBL.W);
				EmitTransformedTexCoord(texTransform, uv.BottomLeft.X, uv.BottomLeft.Y);
				GL.Vertex2(rect.x, rect.y + rect.height);
			}
			else
			{
				// Untextured: Toolbox tex=1 → fill white*vertex (black unused).
				Vector4 Tint(Vector4 c) => new Vector4(c.X * white.X, c.Y * white.Y, c.Z * white.Z, c.W * white.W);
				var t = Tint(cTL);
				GL.Color4(t.X, t.Y, t.Z, t.W); GL.Vertex2(rect.x, rect.y);
				t = Tint(cTR);
				GL.Color4(t.X, t.Y, t.Z, t.W); GL.Vertex2(rect.x + rect.width, rect.y);
				t = Tint(cBR);
				GL.Color4(t.X, t.Y, t.Z, t.W); GL.Vertex2(rect.x + rect.width, rect.y + rect.height);
				t = Tint(cBL);
				GL.Color4(t.X, t.Y, t.Z, t.W); GL.Vertex2(rect.x, rect.y + rect.height);
			}
			GL.End();

			GL.BindTexture(TextureTarget.Texture2D, 0);
			GL.Enable(EnableCap.Texture2D);
		}

		/// <summary>
		/// Cafe window draw aligned with Switch-Toolbox BxlytToGL.DrawWindowPane.
		/// HorizontalNoContent keeps the LoadingFade side-rail path; Around uses
		/// FrameCount 1/4/8 corner (and side) pieces with TextureFlip.
		/// </summary>
		void DrawWindowPane(Wnd1Pane wnd)
		{
			if (wnd?.Content == null)
				return;

			float paneAlpha = wnd.Alpha / 255f;
			if (paneAlpha <= 0f)
				paneAlpha = 1f;

			Vector4 cTL = ToVertexVec4(wnd.Content.ColorTopLeft, paneAlpha);
			Vector4 cTR = ToVertexVec4(wnd.Content.ColorTopRight, paneAlpha);
			Vector4 cBL = ToVertexVec4(wnd.Content.ColorBottomLeft, paneAlpha);
			Vector4 cBR = ToVertexVec4(wnd.Content.ColorBottomRight, paneAlpha);
			if (cTL.W <= 0f && cTR.W <= 0f && cBL.W <= 0f && cBR.W <= 0f)
				cTL.W = cTR.W = cBL.W = cBR.W = paneAlpha;

			var rect = wnd.transformedRect;
			float dX = rect.x;
			// Toolbox DrawQuad uses Y-up with (x,y) as the top edge and height extending downward.
			// transformedRect.y is the bottom edge; convert so piece math matches Toolbox.
			float dYTop = rect.y + rect.height;
			float dYBottom = rect.y;
			float paneW = rect.width;
			float paneH = rect.height;
			if (paneW <= 0f || paneH <= 0f)
				return;

			float frameLeft = wnd.FrameElementLeft;
			float frameRight = wnd.FrameElementRight;
			float frameTop = wnd.FrameElementTop;
			float frameBottom = wnd.FrameElementBottom;

			if (wnd.Frames != null && wnd.Frames.Count > 0)
			{
				// Match Toolbox BxlytToGL: FrameCount 1/2/4/8 take strip sizes from the
				// bound textures first, then fall back to FrameElement when size is 0.
				if ((wnd.FrameCount == 1 || wnd.FrameCount == 2) &&
					bntxPreview.TryGetTextureSize(activeLayout, wnd.Frames[0].MaterialIndex, out float oneW, out float oneH))
				{
					frameLeft = frameRight = oneW;
					frameTop = frameBottom = oneH;
				}
				else if ((wnd.FrameCount == 4 || wnd.FrameCount == 8) && wnd.Frames.Count >= 4)
				{
					if (bntxPreview.TryGetTextureSize(activeLayout, wnd.Frames[0].MaterialIndex, out float fl, out float ft))
					{
						frameLeft = fl;
						frameTop = ft;
					}
					if (bntxPreview.TryGetTextureSize(activeLayout, wnd.Frames[3].MaterialIndex, out float fr, out float fb))
					{
						frameRight = fr;
						frameBottom = fb;
					}
				}
			}

			if (frameLeft <= 0f) frameLeft = wnd.FrameElementLeft;
			if (frameRight <= 0f) frameRight = wnd.FrameElementRight;
			if (frameTop <= 0f) frameTop = wnd.FrameElementTop;
			if (frameBottom <= 0f) frameBottom = wnd.FrameElementBottom;

			float contentW = ((wnd.StretchLeft + (paneW - frameLeft)) - frameRight) + wnd.StretchRight;
			float contentH = wnd.Kind == Wnd1Pane.WindowKind.Horizontal
				? paneH
				: ((wnd.StretchTop + (paneH - frameTop)) - frameBottom) + wnd.StretchBottom;

			// Content (not drawn for HorizontalNoContent — matches Toolbox).
			if (wnd.Kind != Wnd1Pane.WindowKind.HorizontalNoContent)
			{
				var uv = (wnd.Content.UVCoords != null && wnd.Content.UVCoords.Length > 0)
					? wnd.Content.UVCoords[0]
					: new Pic1Pane.UVCoord
					{
						TopLeft = (0, 0),
						TopRight = (1, 0),
						BottomLeft = (0, 1),
						BottomRight = (1, 1)
					};

				float contentX = dX + frameLeft - wnd.StretchLeft;
				float contentTop = wnd.Kind == Wnd1Pane.WindowKind.Horizontal
					? dYTop
					: dYTop - frameTop + wnd.StretchTop;

				DrawWindowQuad(
					wnd.Content.MaterialIndex,
					contentX, contentTop, contentW, contentH,
					uv.TopLeft.X, uv.TopLeft.Y,
					uv.TopRight.X, uv.TopRight.Y,
					uv.BottomRight.X, uv.BottomRight.Y,
					uv.BottomLeft.X, uv.BottomLeft.Y,
					cTL, cTR, cBR, cBL,
					Wnd1Pane.WindowFrameTexFlip.None);
			}

			if (wnd.Frames == null || wnd.Frames.Count == 0)
				return;

			Vector4[] frameColors = wnd.UseVertexColorForAll
				? new[] { cTL, cTR, cBR, cBL }
				: new[]
				{
					new Vector4(1f, 1f, 1f, paneAlpha),
					new Vector4(1f, 1f, 1f, paneAlpha),
					new Vector4(1f, 1f, 1f, paneAlpha),
					new Vector4(1f, 1f, 1f, paneAlpha)
				};

			if (wnd.Kind == Wnd1Pane.WindowKind.HorizontalNoContent)
			{
				// LoadingSideBG is an alpha mask (BNTX swizzle RGB=One, A=Red): transparent
				// jagged edge on the left, cream body on the right of the strip. Do NOT paint
				// an opaque fill under the strip — that kills the alpha edge (map/BG must show).
				float stripW = frameLeft > 0f ? frameLeft : frameRight;
				if (stripW <= 0f &&
					bntxPreview.TryGetTextureSize(activeLayout, wnd.Frames[0].MaterialIndex, out float tw, out float th) &&
					th > 0f)
				{
					stripW = tw * (paneH / th);
				}
				if (stripW <= 0f)
					stripW = paneW;
				stripW = Math.Min(stripW, paneW);

				// Cream / White8 only for the opaque remainder to the right of the mask strip.
				if (paneW - stripW > 0.5f)
				{
					ushort fillMat = wnd.Frames.Count >= 2
						? wnd.Frames[1].MaterialIndex
						: wnd.Content.MaterialIndex;
					DrawTexturedQuad(
						fillMat,
						dX + stripW, dYBottom, paneW - stripW, paneH,
						0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f,
						cTL, cTR, cBR, cBL);
				}

				// Vertex colors tint the opaque texels (cream); alpha comes from the baked mask.
				DrawTexturedQuad(
					wnd.Frames[0].MaterialIndex,
					dX, dYBottom, stripW, paneH,
					0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f,
					cTL, cTR, cBR, cBL);
			}
			else if (wnd.Kind == Wnd1Pane.WindowKind.Horizontal)
			{
				float fl = Math.Max(1f, frameLeft);
				float fr = Math.Max(1f, frameRight);
				DrawTexturedQuad(
					wnd.Frames[0].MaterialIndex,
					dX, dYBottom, fl, paneH,
					0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f,
					frameColors[1], frameColors[1], frameColors[2], frameColors[2]);

				ushort rightMat = wnd.Frames.Count >= 2
					? wnd.Frames[1].MaterialIndex
					: wnd.Frames[0].MaterialIndex;
				DrawTexturedQuad(
					rightMat,
					dX + fr + contentW, dYBottom, fr, paneH,
					1f, 0f, 0f, 0f, 0f, 1f, 1f, 1f,
					frameColors[0], frameColors[0], frameColors[3], frameColors[3]);
			}
			else
			{
				// Around — port of Toolbox FrameCount 1 / 4 / 8.
				DrawAroundWindowFrames(
					wnd, dX, dYTop, paneW, paneH,
					frameLeft, frameRight, frameTop, frameBottom,
					contentW, contentH, frameColors);
			}
		}

		void DrawAroundWindowFrames(
			Wnd1Pane wnd,
			float dX, float dYTop, float paneW, float paneH,
			float frameLeft, float frameRight, float frameTop, float frameBottom,
			float contentW, float contentH,
			Vector4[] colors)
		{
			int count = Math.Min(wnd.FrameCount, wnd.Frames.Count);
			if (count <= 0)
				return;

			float fl = Math.Max(1f, frameLeft);
			float fr = Math.Max(1f, frameRight);
			float ft = Math.Max(1f, frameTop);
			float fb = Math.Max(1f, frameBottom);

			if (count >= 8)
			{
				// Corners (unit UVs) + side stretches.
				DrawWindowQuad(wnd.Frames[0].MaterialIndex, dX, dYTop, fl, ft,
					0, 0, 1, 0, 1, 1, 0, 1, colors[0], colors[1], colors[2], colors[3], wnd.Frames[0].TextureFlip);
				DrawWindowQuad(wnd.Frames[1].MaterialIndex, dX + paneW - fr, dYTop, fr, ft,
					0, 0, 1, 0, 1, 1, 0, 1, colors[0], colors[1], colors[2], colors[3], wnd.Frames[1].TextureFlip);
				DrawWindowQuad(wnd.Frames[2].MaterialIndex, dX, dYTop - paneH + ft, fl, fb,
					0, 0, 1, 0, 1, 1, 0, 1, colors[0], colors[1], colors[2], colors[3], wnd.Frames[2].TextureFlip);
				DrawWindowQuad(wnd.Frames[3].MaterialIndex, dX + paneW - fl, dYTop - paneH + fb, fr, fb,
					0, 0, 1, 0, 1, 1, 0, 1, colors[0], colors[1], colors[2], colors[3], wnd.Frames[3].TextureFlip);

				float uSide = (paneW - fl) / fl;
				float vSide = (paneH - ft) / ft;
				DrawWindowQuad(wnd.Frames[4].MaterialIndex, dX + fl, dYTop, contentW, ft,
					0, 0, uSide, 0, uSide, 1, 0, 1, colors[0], colors[1], colors[2], colors[3], wnd.Frames[4].TextureFlip);
				DrawWindowQuad(wnd.Frames[5].MaterialIndex, dX + fr, dYTop - (paneH - fb), contentW, ft,
					1 - uSide, 0, 1, 0, 1, 1, 1 - uSide, 1, colors[0], colors[1], colors[2], colors[3], wnd.Frames[5].TextureFlip);
				DrawWindowQuad(wnd.Frames[6].MaterialIndex, dX, dYTop - ft, fl, contentH,
					0, 1 - vSide, 1, 1 - vSide, 1, 1, 0, 1, colors[0], colors[1], colors[2], colors[3], wnd.Frames[6].TextureFlip);
				DrawWindowQuad(wnd.Frames[7].MaterialIndex, dX + paneW - fr, dYTop - ft, fr, contentH,
					0, 0, 1, 0, 1, vSide, 0, vSide, colors[0], colors[1], colors[2], colors[3], wnd.Frames[7].TextureFlip);
				return;
			}

			if (count >= 4)
			{
				float uExtent = (paneW - fl) / fl;
				float vExtent = (paneH - ft) / ft;

				// TL — top strip across (width - right frame)
				DrawWindowQuad(wnd.Frames[0].MaterialIndex,
					dX, dYTop, paneW - fr, ft,
					0, 0, uExtent, 0, uExtent, 1, 0, 1,
					colors[0], colors[1], colors[2], colors[3], wnd.Frames[0].TextureFlip);

				// TR — right strip down from top
				DrawWindowQuad(wnd.Frames[1].MaterialIndex,
					dX + paneW - fr, dYTop, fr, paneH - fb,
					0, 0, 1, 0, 1, vExtent, 0, vExtent,
					colors[0], colors[1], colors[2], colors[3], wnd.Frames[1].TextureFlip);

				// BL — left strip below the top frame
				DrawWindowQuad(wnd.Frames[2].MaterialIndex,
					dX, dYTop - ft, fl, paneH - ft,
					0, 1 - vExtent, 1, 1 - vExtent, 1, 1, 0, 1,
					colors[0], colors[1], colors[2], colors[3], wnd.Frames[2].TextureFlip);

				// BR — bottom strip from left frame to right edge
				DrawWindowQuad(wnd.Frames[3].MaterialIndex,
					dX + fl, dYTop - paneH + fb, paneW - fl, fb,
					1 - uExtent, 0, 1, 0, 1, 1, 1 - uExtent, 1,
					colors[0], colors[1], colors[2], colors[3], wnd.Frames[3].TextureFlip);
				return;
			}

			// FrameCount 1 Around: single material wraps all four edges (Toolbox case 1).
			float u1 = (paneW - fl) / fl;
			float vTop = (paneH - ft) / ft;
			float vBot = (paneH - fb) / fb;
			var flip0 = wnd.Frames[0].TextureFlip;
			ushort mat0 = wnd.Frames[0].MaterialIndex;

			DrawWindowQuad(mat0, dX, dYTop, paneW - fr, ft,
				0, 0, u1, 0, u1, 1, 0, 1, colors[0], colors[1], colors[2], colors[3], flip0);
			DrawWindowQuad(mat0, dX + paneW - fr, dYTop, fr, paneH - fb,
				1, 0, 0, 0, 0, vTop, 1, vTop, colors[0], colors[1], colors[2], colors[3], flip0);
			DrawWindowQuad(mat0, dX, dYTop - ft, fl, paneH - ft,
				0, vBot, 1, vBot, 1, 0, 0, 0, colors[0], colors[1], colors[2], colors[3], flip0);
			DrawWindowQuad(mat0, dX + fl, dYTop - paneH + fb, paneW - fl, fb,
				u1, 1, 0, 1, 0, 0, u1, 0, colors[0], colors[1], colors[2], colors[3], flip0);
		}

		/// <summary>
		/// Draw a window piece using Toolbox Y-up (x, yTop, w, h) coordinates.
		/// Vertex winding matches BxlytToGL.DrawQuad (TL→TR→BR→BL, height downward).
		/// V is inverted after TextureFlip because BntxTextureDecoder flips uploads so GL
		/// V=0 is the image bottom, while Cafe/Toolbox window UVs treat V=0 as image top.
		/// </summary>
		void DrawWindowQuad(
			ushort materialIndex,
			float x, float yTop, float w, float h,
			float u0, float v0, float u1, float v1, float u2, float v2, float u3, float v3,
			Vector4 c0, Vector4 c1, Vector4 c2, Vector4 c3,
			Wnd1Pane.WindowFrameTexFlip flip)
		{
			if (w <= 0f || h <= 0f)
				return;

			ApplyWindowTextureFlip(flip, ref u0, ref v0);
			ApplyWindowTextureFlip(flip, ref u1, ref v1);
			ApplyWindowTextureFlip(flip, ref u2, ref v2);
			ApplyWindowTextureFlip(flip, ref u3, ref v3);
			v0 = 1f - v0;
			v1 = 1f - v1;
			v2 = 1f - v2;
			v3 = 1f - v3;

			BflytMaterial mat = null;
			if (activeLayout.Mat1?.Materials != null && materialIndex < activeLayout.Mat1.Materials.Count)
				mat = activeLayout.Mat1.Materials[materialIndex];

			var black = mat != null ? ToVec4(mat.ForegroundColor) : new Vector4(0f, 0f, 0f, 0f);
			var white = mat != null ? ToVec4(mat.BackgroundColor) : new Vector4(1f, 1f, 1f, 1f);
			if (white.W <= 0f)
				white.W = 1f;

			bool hasTexture = false;
			var wrapS = BflytMaterial.TextureReference.WRAPS.Clamp;
			var wrapT = BflytMaterial.TextureReference.WRAPS.Clamp;
			Matrix4 texTransform = LayoutPic1Shader.IdentityTransform;

			if (mat?.Textures != null && mat.Textures.Length > 0)
			{
				hasTexture = bntxPreview.BindMaterialTexture(activeLayout, materialIndex, white, black, out _, out wrapS, out wrapT);
				if (mat.TextureTransformations != null && mat.TextureTransformations.Length > 0)
				{
					var t = mat.TextureTransformations[0];
					texTransform = LayoutPic1Shader.BuildTextureTransform(t.X, t.Y, t.Rotation, t.ScaleX, t.ScaleY);
				}
			}

			GL.ActiveTexture(TextureUnit.Texture0);
			if (hasTexture)
			{
				GL.Enable(EnableCap.Texture2D);
				GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)ToGlWrap(wrapS));
				GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)ToGlWrap(wrapT));
			}
			else
			{
				GL.BindTexture(TextureTarget.Texture2D, 0);
				GL.Disable(EnableCap.Texture2D);
			}

			// Toolbox DrawQuad: top edge at yTop, extending downward by h.
			GL.Begin(PrimitiveType.Quads);
			if (hasTexture)
			{
				GL.Color4(c0.X, c0.Y, c0.Z, c0.W);
				EmitTransformedTexCoord(texTransform, u0, v0);
				GL.Vertex2(x, yTop);
				GL.Color4(c1.X, c1.Y, c1.Z, c1.W);
				EmitTransformedTexCoord(texTransform, u1, v1);
				GL.Vertex2(x + w, yTop);
				GL.Color4(c2.X, c2.Y, c2.Z, c2.W);
				EmitTransformedTexCoord(texTransform, u2, v2);
				GL.Vertex2(x + w, yTop - h);
				GL.Color4(c3.X, c3.Y, c3.Z, c3.W);
				EmitTransformedTexCoord(texTransform, u3, v3);
				GL.Vertex2(x, yTop - h);
			}
			else
			{
				Vector4 Tint(Vector4 c) => new Vector4(c.X * white.X, c.Y * white.Y, c.Z * white.Z, c.W * white.W);
				var t = Tint(c0);
				GL.Color4(t.X, t.Y, t.Z, t.W); GL.Vertex2(x, yTop);
				t = Tint(c1);
				GL.Color4(t.X, t.Y, t.Z, t.W); GL.Vertex2(x + w, yTop);
				t = Tint(c2);
				GL.Color4(t.X, t.Y, t.Z, t.W); GL.Vertex2(x + w, yTop - h);
				t = Tint(c3);
				GL.Color4(t.X, t.Y, t.Z, t.W); GL.Vertex2(x, yTop - h);
			}
			GL.End();

			GL.BindTexture(TextureTarget.Texture2D, 0);
			GL.Enable(EnableCap.Texture2D);
		}

		static void ApplyWindowTextureFlip(Wnd1Pane.WindowFrameTexFlip flip, ref float u, ref float v)
		{
			// Matches Toolbox Rev Shader SetFlip (rotateUV with +degrees).
			switch (flip)
			{
				case Wnd1Pane.WindowFrameTexFlip.FlipH:
					u = 1f - u;
					break;
				case Wnd1Pane.WindowFrameTexFlip.FlipV:
					v = 1f - v;
					break;
				case Wnd1Pane.WindowFrameTexFlip.Rotate90:
					{
						float nu = v;
						float nv = 1f - u;
						u = nu;
						v = nv;
						break;
					}
				case Wnd1Pane.WindowFrameTexFlip.Rotate180:
					u = 1f - u;
					v = 1f - v;
					break;
				case Wnd1Pane.WindowFrameTexFlip.Rotate270:
					{
						float nu = 1f - v;
						float nv = u;
						u = nu;
						v = nv;
						break;
					}
			}
		}

		void DrawPartsPane(Prt1Pane prt)
		{
			if (!Settings.Default.PreviewSubLayouts)
				return;
			if (partsCache == null || !partsCache.CanPreviewSiblingLayouts)
				return;
			if (string.IsNullOrEmpty(prt.PartName))
				return;

			var partLayout = partsCache.Get(prt.PartName);
			if (partLayout?.ElementsRoot == null)
				return;

			float mx = prt.SectionsSacle.X;
			float my = prt.SectionsSacle.Y;
			if (Math.Abs(mx) < 1e-6f) mx = 1f;
			if (Math.Abs(my) < 1e-6f) my = 1f;

			// Pa_Sage_03 → variant 3, etc. Parts hide alternate icons behind anim; pick by instance index.
			int variantIndex = ParseTrailingIndex(prt.PaneName);

			GL.Scale(mx, my, 1f);
			var previous = activeLayout;
			activeLayout = partLayout;
			try
			{
				RenderPaneSubtree(partLayout.ElementsRoot, variantIndex, forceVisible: false);
			}
			finally
			{
				activeLayout = previous;
			}
		}

		static int ParseTrailingIndex(string paneName)
		{
			if (string.IsNullOrEmpty(paneName))
				return 0;
			int i = paneName.Length - 1;
			while (i >= 0 && char.IsDigit(paneName[i]))
				i--;
			if (i == paneName.Length - 1)
				return 0;
			if (int.TryParse(paneName.Substring(i + 1), out int n))
				return n;
			return 0;
		}

		void RenderPaneSubtree(Pan1Pane p, int variantIndex = 0, bool forceVisible = false)
		{
			// forceVisible: reveal a normally-hidden part variant (e.g. sage icons).
			if (!forceVisible && !p.ParentVisibility)
				return;
			if (p.Scale.X == 0 || p.Scale.Y == 0)
				return;

			GL.PushMatrix();
			GL.Translate(p.Position.X, p.Position.Y, 0);
			GL.Rotate(p.Rotation.Z, p.Rotation.X, p.Rotation.Y, p.Rotation.Z);
			GL.Scale(p.Scale.X, p.Scale.Y, 1);

			if (p.ViewInEditor)
			{
				if (p is Pic1Pane pic)
					DrawPicturePane(pic);
				else if (p is Wnd1Pane wnd)
					DrawWindowPane(wnd);
				else if (p is Txt1Pane txt)
					DrawTextPane(txt);
				else if (p is Prt1Pane nested)
					DrawPartsPane(nested);
			}

			var kids = p.Children.OfType<Pan1Pane>().ToList();
			// Mutually exclusive variants: one visible at rest, others hidden for anim switching.
			bool exclusiveVariants = kids.Count > 1
				&& kids.Count(k => k.Visible) == 1
				&& kids.Any(k => !k.Visible);

			if (exclusiveVariants)
			{
				int idx = Math.Max(0, Math.Min(variantIndex, kids.Count - 1));
				RenderPaneSubtree(kids[idx], variantIndex, forceVisible: true);
			}
			else
			{
				foreach (var c in kids)
					RenderPaneSubtree(c, variantIndex, forceVisible: false);
			}
			GL.PopMatrix();
		}

		void DrawTextPane(Txt1Pane txt)
		{
			string text = txt.Text;
			if (string.IsNullOrEmpty(text))
				return;
			text = text.TrimEnd('\0');
			if (text.Length == 0)
				return;

			var rect = txt.transformedRect;
			if (rect.width <= 0 || rect.height <= 0)
				return;

			float paneAlpha = txt.Alpha / 255f;
			if (paneAlpha <= 0f)
				paneAlpha = 1f;

			Color top = Color.FromArgb(
				Math.Max(1, (int)(txt.FontTopColor.A * paneAlpha)),
				txt.FontTopColor.R, txt.FontTopColor.G, txt.FontTopColor.B);
			Color bottom = Color.FromArgb(
				Math.Max(1, (int)(txt.FontBottomColor.A * paneAlpha)),
				txt.FontBottomColor.R, txt.FontBottomColor.G, txt.FontBottomColor.B);

			float fontH = Math.Max(8f, txt.FontXYSize.Y > 0 ? txt.FontXYSize.Y : rect.height * 0.75f);
			string cacheKey = string.Format("{0}|{1}x{2}|{3}|{4}|{5:0.#}",
				text, rect.width, rect.height, top.ToArgb(), bottom.ToArgb(), fontH);

			if (!textGlTextures.TryGetValue(cacheKey, out int texId))
			{
				texId = UploadTextTexture(text, Math.Max(1, rect.width), Math.Max(1, rect.height), fontH, top, bottom, txt);
				if (texId == 0)
					return;
				textGlTextures[cacheKey] = texId;
			}

			GL.Enable(EnableCap.Texture2D);
			GL.BindTexture(TextureTarget.Texture2D, texId);
			GL.Color4(1f, 1f, 1f, paneAlpha);
			GL.Begin(PrimitiveType.Quads);
			GL.TexCoord2(0, 0); GL.Vertex2(rect.x, rect.y);
			GL.TexCoord2(1, 0); GL.Vertex2(rect.x + rect.width, rect.y);
			GL.TexCoord2(1, 1); GL.Vertex2(rect.x + rect.width, rect.y + rect.height);
			GL.TexCoord2(0, 1); GL.Vertex2(rect.x, rect.y + rect.height);
			GL.End();
			GL.BindTexture(TextureTarget.Texture2D, 0);
		}

		int UploadTextTexture(string text, int width, int height, float fontH, Color top, Color bottom, Txt1Pane txt)
		{
			using (var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
			using (var g = Graphics.FromImage(bmp))
			{
				g.Clear(Color.Transparent);
				g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
				g.SmoothingMode = SmoothingMode.AntiAlias;

				Color fill = (top.A == bottom.A && top.R == bottom.R && top.G == bottom.G && top.B == bottom.B)
					? top
					: Color.FromArgb((top.A + bottom.A) / 2, (top.R + bottom.R) / 2, (top.G + bottom.G) / 2, (top.B + bottom.B) / 2);

				using (var font = new Font("Segoe UI", Math.Max(6f, fontH * 0.75f), FontStyle.Regular, GraphicsUnit.Pixel))
				using (var brush = new SolidBrush(fill))
				{
					var format = new StringFormat();
					switch (txt.HorizontalAlignment)
					{
						case Pan1Pane.OriginX.Left: format.Alignment = StringAlignment.Near; break;
						case Pan1Pane.OriginX.Right: format.Alignment = StringAlignment.Far; break;
						default: format.Alignment = StringAlignment.Center; break;
					}
					format.LineAlignment = StringAlignment.Center;

					g.DrawString(text, font, brush, new RectangleF(0, 0, width, height), format);
				}

				var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
				try
				{
					GL.GenTextures(1, out int tex);
					GL.BindTexture(TextureTarget.Texture2D, tex);
					GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height, 0,
						OpenTK.Graphics.OpenGL.PixelFormat.Bgra, PixelType.UnsignedByte, data.Scan0);
					GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
					GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
					GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
					GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
					return tex;
				}
				finally
				{
					bmp.UnlockBits(data);
				}
			}
		}

		void DrawTexturedQuad(
			ushort materialIndex,
			float x, float y, float w, float h,
			float u0, float v0, float u1, float v1, float u2, float v2, float u3, float v3,
			Vector4 c0, Vector4 c1, Vector4 c2, Vector4 c3)
		{
			if (w <= 0f || h <= 0f)
				return;

			BflytMaterial mat = null;
			if (activeLayout.Mat1?.Materials != null && materialIndex < activeLayout.Mat1.Materials.Count)
				mat = activeLayout.Mat1.Materials[materialIndex];

			var black = mat != null ? ToVec4(mat.ForegroundColor) : new Vector4(0f, 0f, 0f, 0f);
			var white = mat != null ? ToVec4(mat.BackgroundColor) : new Vector4(1f, 1f, 1f, 1f);
			if (white.W <= 0f)
				white.W = 1f;

			bool hasTexture = false;
			var wrapS = BflytMaterial.TextureReference.WRAPS.Clamp;
			var wrapT = BflytMaterial.TextureReference.WRAPS.Clamp;
			Matrix4 texTransform = LayoutPic1Shader.IdentityTransform;

			if (mat?.Textures != null && mat.Textures.Length > 0)
			{
				hasTexture = bntxPreview.BindMaterialTexture(activeLayout, materialIndex, white, black, out _, out wrapS, out wrapT);
				if (mat.TextureTransformations != null && mat.TextureTransformations.Length > 0)
				{
					var t = mat.TextureTransformations[0];
					texTransform = LayoutPic1Shader.BuildTextureTransform(t.X, t.Y, t.Rotation, t.ScaleX, t.ScaleY);
				}
			}

			GL.ActiveTexture(TextureUnit.Texture0);
			if (hasTexture)
			{
				GL.Enable(EnableCap.Texture2D);
				GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)ToGlWrap(wrapS));
				GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)ToGlWrap(wrapT));
			}
			else
			{
				GL.BindTexture(TextureTarget.Texture2D, 0);
				GL.Disable(EnableCap.Texture2D);
			}

			GL.Begin(PrimitiveType.Quads);
			if (hasTexture)
			{
				GL.Color4(c0.X, c0.Y, c0.Z, c0.W);
				EmitTransformedTexCoord(texTransform, u0, v0);
				GL.Vertex2(x, y);
				GL.Color4(c1.X, c1.Y, c1.Z, c1.W);
				EmitTransformedTexCoord(texTransform, u1, v1);
				GL.Vertex2(x + w, y);
				GL.Color4(c2.X, c2.Y, c2.Z, c2.W);
				EmitTransformedTexCoord(texTransform, u2, v2);
				GL.Vertex2(x + w, y + h);
				GL.Color4(c3.X, c3.Y, c3.Z, c3.W);
				EmitTransformedTexCoord(texTransform, u3, v3);
				GL.Vertex2(x, y + h);
			}
			else
			{
				Vector4 Tint(Vector4 c) => new Vector4(c.X * white.X, c.Y * white.Y, c.Z * white.Z, c.W * white.W);
				var t = Tint(c0);
				GL.Color4(t.X, t.Y, t.Z, t.W); GL.Vertex2(x, y);
				t = Tint(c1);
				GL.Color4(t.X, t.Y, t.Z, t.W); GL.Vertex2(x + w, y);
				t = Tint(c2);
				GL.Color4(t.X, t.Y, t.Z, t.W); GL.Vertex2(x + w, y + h);
				t = Tint(c3);
				GL.Color4(t.X, t.Y, t.Z, t.W); GL.Vertex2(x, y + h);
			}
			GL.End();

			GL.BindTexture(TextureTarget.Texture2D, 0);
			GL.Enable(EnableCap.Texture2D);
		}

		static void EmitTransformedTexCoord(Matrix4 transform, float u, float v)
		{
			var vec = new Vector4(u, v, 0f, 1f);
			Vector4.Transform(ref vec, ref transform, out var result);
			GL.TexCoord2(0.5f + result.X, 0.5f + result.Y);
		}

		static Vector4 ToVec4(RGBAColor c, float alphaMul = 1f) =>
			new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, (c.A / 255f) * alphaMul);

		/// <summary>
		/// Vertex colors for GL modulation — same sRGB gamma lift as material black/white in
		/// <see cref="BntxPreviewCache"/> (cream SideBG etc. otherwise stay too yellow/dark).
		/// </summary>
		static Vector4 ToVertexVec4(RGBAColor c, float alphaMul = 1f) =>
			new Vector4(
				LayoutDisplayColor.LiftChannel(c.R / 255f),
				LayoutDisplayColor.LiftChannel(c.G / 255f),
				LayoutDisplayColor.LiftChannel(c.B / 255f),
				(c.A / 255f) * alphaMul);

		/// <summary>
		/// Toolbox stores wrap in the low 2 bits of the flag byte (filter in the upper bits).
		/// </summary>
		static TextureWrapMode ToGlWrap(BflytMaterial.TextureReference.WRAPS wrap)
		{
			switch ((BflytMaterial.TextureReference.WRAPS)((byte)wrap & 0x3))
			{
				case BflytMaterial.TextureReference.WRAPS.NearRepeat:
					return TextureWrapMode.Repeat;
				case BflytMaterial.TextureReference.WRAPS.NearMirror:
				case BflytMaterial.TextureReference.WRAPS.GX2MirrorOnce:
					return TextureWrapMode.MirroredRepeat;
				default:
					return TextureWrapMode.ClampToEdge;
			}
		}

		bool PaneUsesPaletteColor(Pan1Pane p, RGBAColor color)
		{
			if (p is Pic1Pane pic)
			{
				return pic.ColorTopLeft == color || pic.ColorTopRight == color
					|| pic.ColorBottomLeft == color || pic.ColorBottomRight == color
					|| MaterialUsesColor(pic.MaterialIndex, color);
			}
			if (p is Txt1Pane txt)
			{
				return txt.FontTopColor == color || txt.FontBottomColor == color
					|| txt.ShadowTopColor == color || txt.ShadowBottomColor == color
					|| MaterialUsesColor(txt.MaterialIndex, color);
			}
			if (p is Wnd1Pane wnd)
			{
				if (wnd.Content != null &&
					(wnd.Content.ColorTopLeft == color || wnd.Content.ColorTopRight == color
					 || wnd.Content.ColorBottomLeft == color || wnd.Content.ColorBottomRight == color))
					return true;
				if (wnd.Content != null && MaterialUsesColor(wnd.Content.MaterialIndex, color))
					return true;
				if (wnd.Frames != null)
				{
					foreach (var fr in wnd.Frames)
					{
						if (MaterialUsesColor(fr.MaterialIndex, color))
							return true;
					}
				}
			}
			return false;
		}

		bool MaterialUsesColor(ushort matIndex, RGBAColor color)
		{
			if (layout.Mat1?.Materials == null || matIndex >= layout.Mat1.Materials.Count)
				return false;
			var mat = layout.Mat1.Materials[matIndex];
			return mat.ForegroundColor == color || mat.BackgroundColor == color;
		}

        void DrawPaneMiddlePoint(CusRectangle rect, Color color)
        {
            GL.Color3(color);
            GL.Begin(PrimitiveType.Lines);
            GL.Vertex2(rect.x + (rect.width / 2) - 1, rect.y + (rect.height / 2) - 1);
            GL.Vertex2(rect.x + (rect.width / 2) - 1, rect.y + (rect.height / 2) + 1);
            GL.Vertex2(rect.x + (rect.width / 2) - 1, rect.y + (rect.height / 2) + 1);
            GL.Vertex2(rect.x + (rect.width / 2) + 1, rect.y + (rect.height / 2) + 1);
            GL.Vertex2(rect.x + (rect.width / 2) + 1, rect.y + (rect.height / 2) + 1);
            GL.Vertex2(rect.x + (rect.width / 2) + 1, rect.y + (rect.height / 2) - 1);
            GL.Vertex2(rect.x + (rect.width / 2) + 1, rect.y + (rect.height / 2) - 1);
            GL.Vertex2(rect.x + (rect.width / 2) - 1, rect.y + (rect.height / 2) - 1);
            GL.End();
        }

		void DrawPane(CusRectangle rect, Color color)
		{
			GL.Color3(color);
			GL.Begin(PrimitiveType.Lines);
			GL.Vertex2(rect.x, rect.y);
			GL.Vertex2(rect.x, rect.y + rect.height);
			GL.Vertex2(rect.x, rect.y + rect.height);
			GL.Vertex2(rect.x + rect.width, rect.y + rect.height);
			GL.Vertex2(rect.x + rect.width, rect.y + rect.height);
			GL.Vertex2(rect.x + rect.width, rect.y);
			GL.Vertex2(rect.x + rect.width, rect.y);
			GL.Vertex2(rect.x, rect.y);
			GL.End();
		}

		object TryGetSelectedFocusTag() 
		{
			if (treeView1.SelectedNode == null) return null;
			if (treeView1.SelectedNode.Tag is BasePane) return treeView1.SelectedNode.Tag;
			if (treeView1.SelectedNode.Tag is TextureTag) return ((TextureTag)treeView1.SelectedNode.Tag).TexName;
			if (treeView1.SelectedNode.Tag is BflytMaterial) return treeView1.SelectedNode.Tag;
			if (treeView1.SelectedNode.Tag is MaterialPaletteTag) return treeView1.SelectedNode.Tag;
			return null;
		}

		TreeNode FindRoot(TreeNode item)
		{	
			if (item == null) return null;
			while (item.Parent != null)
				item = item.Parent;
			return item;
		}

		public void UpdateView(object focus = null)
		{
			TreeNode focusElement = null;
			
			if (focus == null)
			{
				// if there is no explicit focus change, try to keep the current one
				focus = TryGetSelectedFocusTag();
			}

			treeView1.SuspendLayout();
			treeView1.Nodes.Clear();

			{
				string target = focus as string;
				TexturesRoot = treeView1.Nodes.Add("Textures");
				TexturesRoot.Tag = new TextureTag();
				int index = 0;
				if (layout.Tex1 != null)
					foreach (var t in layout.Tex1.Textures)
					{
						var n = TexturesRoot.Nodes.Add($"{index++} : {t}");
						n.Tag = new TextureTag(t);
						if (target != null && t == target) 
							focusElement = n;
					}
			}

			{
				var target = focus as BflytMaterial;
				MaterialsRoot = treeView1.Nodes.Add("Materials");
				int index = 0;
				currentPalette = null;
				if (layout.Mat1 != null)
				{
					foreach (var t in layout.Mat1.Materials)
					{
						var n = MaterialsRoot.Nodes.Add($"{index++} : {t}");
						n.Tag = t;
						if (target != null & t == target)
							focusElement = n;
					}

					var paletteNode = MaterialsRoot.Nodes.Add("Palette");
					currentPalette = new MaterialPaletteTag(layout) { Filter = PaneFilter };
					paletteNode.Tag = currentPalette;
					if (focus is MaterialPaletteTag)
						focusElement = paletteNode;
				}
				RefreshPaletteWindow();
			}

			Pan1Root = null;
			RecursiveAddNode(layout.ElementsRoot, treeView1.Nodes, focus as BasePane, ref focusElement, ref Pan1Root);

			Grp1Root = null;
			RecursiveAddNode(layout.RootGroup, treeView1.Nodes, focus as BasePane, ref focusElement, ref Grp1Root);

			AllPanesRoot = treeView1.Nodes.Add("Full hierarchy");
			foreach (var r in layout.RootPanes)
			{
				// We don't care about ref AllPanesRoot here, since it is not null it won't be changed
				RecursiveAddNode(r, AllPanesRoot.Nodes, focus as BasePane, ref focusElement, ref AllPanesRoot);
			}

			SyncFilterChecksToTree();
			treeView1.ResumeLayout();
			glControl.Invalidate();

			if (focusElement != null)
			{
				treeView1.SelectedNode = focusElement;
				focusElement.EnsureVisible();
			}
		}

		/*void RenderImg()
        {
            using (Graphics gfx = Graphics.FromImage(b)) Do not remove this as it can be used to render the layout as an image
            {
                gfx.Clear(Color.LightGray);

                gfx.DrawRectangle(new Pen(Brushes.Red, 2), new Rectangle(0, 0, 1280, 720));

                Stack<Matrix> CurMatrix = new Stack<Matrix>();
                Random r = new Random();
                void RecursiveRenderPane(BflytFile.Pan1Pane p)
                {
                    if (!p.ParentVisibility)
                        return;
                    CurMatrix.Push(gfx.Transform.Clone());
                    gfx.TranslateTransform(p.Position.X, p.Position.Y);
                    gfx.RotateTransform(p.Rotation.Z);
                    gfx.ScaleTransform(p.Scale.X, p.Scale.Y);

                    Rectangle transformedRect = new Rectangle(p.transformedRect.x, p.transformedRect.y, p.transformedRect.width, p.transformedRect.height);

                    var pen = new Pen(Brushes.Black, 2);
                    var HighlightedPen = new Pen(Brushes.Red, 4);

                    if (p.ViewInEditor)
                    {
                        if (treeView1.SelectedNode != null && p == treeView1.SelectedNode.Tag as BflytFile.Pan1Pane)
                            pen = HighlightedPen;
                        gfx.DrawRectangle(pen, transformedRect);
                    }

                    foreach (var c in p.Children.Where(x => x is BflytFile.Pan1Pane))
                        RecursiveRenderPane((BflytFile.Pan1Pane)c);
                    gfx.Transform = CurMatrix.Pop();
                }

                gfx.ScaleTransform(1, -1);
                gfx.TranslateTransform(640, -360);
                RecursiveRenderPane(ElementsRoot);

            }
            pictureBox1.Image = b;
        }*/

		public static void RecursiveAddNode(BflytFile.BasePane p, TreeNodeCollection node, BasePane focus, ref TreeNode focusElement, ref TreeNode outRoot)
		{
			var TargetNode = node.Add(p.ToString());
			if (outRoot == null)
				outRoot = TargetNode;

			TargetNode.Tag = p;

			if (focus == p && focusElement == null)
				focusElement = TargetNode;

			foreach (var c in p.Children)
				RecursiveAddNode(c, TargetNode.Nodes, focus, ref focusElement, ref outRoot);
		}

		private void propertyGrid1_PropertyValueChanged(object s, PropertyValueChangedEventArgs e)
		{
			// Baseline was captured when the object was selected (state before this edit).
			if (propertyGridBaseline != null)
				undoStack.Push(propertyGridBaseline);
			propertyGridBaseline = CaptureLayoutSnapshot();
			UpdateUndoMenuState();

			if (e.ChangedItem.Label == "PaneName")
				UpdateView();
			glControl.Invalidate();
		}

		private void treeView1_AfterSelect(object sender, TreeViewEventArgs e)
		{
			if (treeView1.SelectedNode?.Tag is MaterialPaletteTag)
			{
				propertyGrid1.SelectedObject = null;
				ShowPaletteWindow();
			}
			else
			{
				propertyGrid1.SelectedObject = treeView1.SelectedNode?.Tag;
			}
			propertyGridBaseline = CaptureLayoutSnapshot();
			glControl.Invalidate();
		}

		private void treeView1_BeforeCheck(object sender, TreeViewCancelEventArgs e)
		{
			if (!(e.Node.Tag is BasePane))
				e.Cancel = true;
		}

		private void treeView1_AfterCheck(object sender, TreeViewEventArgs e)
		{
			if (suppressTreeCheckEvents || !(e.Node.Tag is BasePane))
				return;

			bool check = e.Node.Checked;
			suppressTreeCheckEvents = true;
			try
			{
				if (ModifierKeys.HasFlag(Keys.Shift) &&
				    checkRangeAnchor != null &&
				    checkRangeAnchor.TreeView == treeView1 &&
				    checkRangeAnchor.Tag is BasePane)
				{
					foreach (TreeNode n in GetVisiblePaneNodesInRange(checkRangeAnchor, e.Node))
					{
						n.Checked = check;
						SetDescendantPaneChecks(n, check);
					}
				}
				else
				{
					SetDescendantPaneChecks(e.Node, check);
					checkRangeAnchor = e.Node;
				}
			}
			finally
			{
				suppressTreeCheckEvents = false;
			}
		}

		/// <summary>
		/// Visible BasePane nodes from <paramref name="a"/> through <paramref name="b"/> in tree display order.
		/// </summary>
		List<TreeNode> GetVisiblePaneNodesInRange(TreeNode a, TreeNode b)
		{
			var visible = new List<TreeNode>();
			CollectVisiblePaneNodes(treeView1.Nodes, visible);
			int i0 = visible.IndexOf(a);
			int i1 = visible.IndexOf(b);
			if (i0 < 0) i0 = i1;
			if (i1 < 0) i1 = i0;
			if (i0 < 0 || i1 < 0)
				return new List<TreeNode> { b };

			if (i0 > i1)
			{
				int tmp = i0;
				i0 = i1;
				i1 = tmp;
			}

			var range = new List<TreeNode>(i1 - i0 + 1);
			for (int i = i0; i <= i1; i++)
				range.Add(visible[i]);
			return range;
		}

		static void CollectVisiblePaneNodes(TreeNodeCollection nodes, List<TreeNode> list)
		{
			foreach (TreeNode n in nodes)
			{
				if (n.Tag is BasePane)
					list.Add(n);
				if (n.IsExpanded)
					CollectVisiblePaneNodes(n.Nodes, list);
			}
		}

		static void SetDescendantPaneChecks(TreeNode node, bool check)
		{
			foreach (TreeNode child in node.Nodes)
			{
				if (child.Tag is BasePane)
					child.Checked = check;
				SetDescendantPaneChecks(child, check);
			}
		}

		private void treeView1_KeyDown(object sender, KeyEventArgs e)
		{
			if (e.KeyCode == Keys.H)
			{
				var target = treeView1.SelectedNode.Tag as Pan1Pane;
				if (target == null) return;
				target.ViewInEditor = !target.ViewInEditor;
				glControl.Invalidate();
			}
			else if (e.KeyCode == Keys.Q)
			{
				var target = treeView1.SelectedNode.Tag as IInspectable;
				if (target == null) return;
				HexEditorForm.Show(target.GetData());
			}
		}

        void SaveAs()
		{
			SaveFileDialog sav = new SaveFileDialog() { Filter = "Binary cafe layout (*.bflyt)|*.bflyt" };
			if (sav.ShowDialog() != DialogResult.OK) return;
			SaveTo = new DiskFileProvider(sav.FileName);
			SaveTo.Save(layout.SaveFile());
		}

		private void saveAsToolStripMenuItem_Click(object sender, EventArgs e) =>
			SaveAs();

		private void bringToFront()
		{
			this.Activate();
			this.BringToFront();
			this.Focus();
		}

		private void EditorView_Click(object sender, System.EventArgs e)
		{
			bringToFront();
		}

		private void EditorView_Resize(object sender, System.EventArgs e)
		{
			bringToFront();
		}

		private void EditorView_LocationChanged(object sender, System.EventArgs e)
		{
			bringToFront();
		}

		private void zoomSlider_Scroll(object sender, EventArgs e)
		{
			glControl.Invalidate();
		}

		private void glControl_MouseWheel(object sender, MouseEventArgs e)
		{
			if (zoomSlider == null || glControl == null)
				return;

			int steps = e.Delta / SystemInformation.MouseWheelScrollDelta;
			if (steps == 0)
				steps = e.Delta > 0 ? 1 : -1;

			int oldValue = zoomSlider.Value;
			int newValue = Math.Max(zoomSlider.Minimum, Math.Min(zoomSlider.Maximum, oldValue + steps));
			if (newValue == oldValue)
				return;

			double oldZoom = oldValue / 10.0;
			double newZoom = newValue / 10.0;
			// Keep the layout point under the cursor fixed (ortho is top-left origin).
			double invDelta = 1.0 / oldZoom - 1.0 / newZoom;
			x -= (float)(e.X * invDelta);
			y += (float)(e.Y * invDelta);

			zoomSlider.Value = newValue;
			glControl.Invalidate();
		}

		private void SetupCursorXYZ(Point res)
		{
			// Screen-pixel drag → layout units (Scale is applied after Translate).
			float z = (float)zoomFactor;
			if (z < 1e-6f) z = 1e-6f;
			x -= res.X / z;
			y += res.Y / z;
		}

		private void SetupObjectXYZ(Pan1Pane p, Point res)
		{
			float z = (float)zoomFactor;
			if (z < 1e-6f) z = 1e-6f;
			p.Position = new SwitchThemes.Common.Vector3(
				p.Position.X - res.X / z,
				p.Position.Y + res.Y / z,
				0);
		}

		private void glControl_MouseDown(object sender, MouseEventArgs e)
		{
			firstPoint = Control.MousePosition;

			if (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Right)
			{
				canvasDragMode = CanvasDragMode.Pan;
				return;
			}

			if (e.Button != MouseButtons.Left)
				return;

			Pan1Pane target = treeView1.SelectedNode?.Tag as Pan1Pane;
			if (ModifierKeys.HasFlag(Keys.Control) && target != null)
			{
				PushUndoState();
				canvasDragMode = CanvasDragMode.MoveObject;
				DraggedObject = false;
				return;
			}

			canvasDragMode = CanvasDragMode.Marquee;
			marqueeStartClient = e.Location;
			marqueeEndClient = e.Location;
			marqueePreviewHits.Clear();
		}

		private void helpToolStripMenuItem_Click(object sender, EventArgs e)
		{
			MessageBox.Show(
                "Quick guide:\n\n" +
				"- Select panes: left-drag a box that fully contains a pane's frame (Shift adds to the selection). Checked panes become palette whitelist roots.\n\n" +
				"- Pan: middle-drag or right-drag on the canvas\n\n" +
                "- Zoom: scroll the mouse wheel over the preview (or use the trackbar on the bottom left)\n\n" +
                "- Dragging objects: select a pane in the tree, then Ctrl+left-drag it in the canvas\n\n" +
				"- Undo/Redo: Ctrl+Z / Ctrl+Y for layout edits (moves, palette, property grid, structure)\n\n" +
				"- The green box: The green box represents the screen bounds, it's always at (0,0) and has the screen size.");
		}

		private void saveToSZSToolStripMenuItem_Click(object sender, EventArgs e)
		{
			if (_saveTo != null)
				_saveTo.Save(layout.SaveFile());
			else SaveAs();
		}

		private void EditorView_FormClosed(object sender, FormClosedEventArgs e)
		{
			foreach (var id in textGlTextures.Values)
			{
				int tex = id;
				try { GL.DeleteTextures(1, ref tex); } catch { }
			}
			textGlTextures.Clear();
			bntxPreview.Dispose();
			if (paletteWindow != null)
			{
				paletteWindow.Dispose();
				paletteWindow = null;
			}
			SaveTo?.EditorClosed();
			Settings.Default.ShowImage = false;
		}

		bool DraggedObject = false;
		private void glControl_MouseMove(object sender, MouseEventArgs e)
		{
			if (!canMoveView || canvasDragMode == CanvasDragMode.None)
				return;

			Point temp = Control.MousePosition;
			Point res = new Point(firstPoint.X - temp.X, firstPoint.Y - temp.Y);

			if (canvasDragMode == CanvasDragMode.Marquee)
			{
				marqueeEndClient = e.Location;
				UpdateMarqueePreviewHits();
				firstPoint = temp;
				glControl.Invalidate();
				return;
			}

			if (canvasDragMode == CanvasDragMode.MoveObject)
			{
				Pan1Pane target = treeView1.SelectedNode?.Tag as Pan1Pane;
				if (target != null)
				{
					SetupObjectXYZ(target, res);
					DraggedObject = true;
				}
			}
			else if (canvasDragMode == CanvasDragMode.Pan)
			{
				SetupCursorXYZ(res);
			}

			firstPoint = temp;
			glControl.Invalidate();
		}

		public static int LoadBgImage(string path, bool flip_x = false, bool flip_y = false)
		{
			if (!File.Exists(path))
				throw new FileNotFoundException("File not found at '" + path + "'");

			Bitmap bitmap = new Bitmap(path);

			if (flip_y)
				bitmap.RotateFlip(RotateFlipType.RotateNoneFlipY);
			if (flip_x)
				bitmap.RotateFlip(RotateFlipType.RotateNoneFlipX);

			int tex;
			GL.Hint(HintTarget.PerspectiveCorrectionHint, HintMode.Nicest);

			GL.GenTextures(1, out tex);
			GL.BindTexture(TextureTarget.Texture2D, tex);

			BitmapData data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
				ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

			GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, data.Width, data.Height, 0,
				OpenTK.Graphics.OpenGL.PixelFormat.Bgra, PixelType.UnsignedByte, data.Scan0);
			bitmap.UnlockBits(data);


			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);

			return tex;
		}

		private void settingsToolStripMenuItem_Click(object sender, EventArgs e)
		{
			SettingsWindow set = new SettingsWindow();
			set.ShowDialog(this);
			set.Dispose();
			glControl?.Invalidate();
		}

		private void expandAllToolStripMenuItem_Click(object sender, EventArgs e)
		{
			treeView1.ExpandAll();
		}

		private void collapseAllToolStripMenuItem_Click(object sender, EventArgs e)
		{
			treeView1.CollapseAll();
		}

		private void paletteToolStripMenuItem_Click(object sender, EventArgs e)
		{
			ShowPaletteWindow();
		}

		private void showPaneFramesToolStripMenuItem_Click(object sender, EventArgs e)
		{
			glControl?.Invalidate();
		}

		private void showSubpanesToolStripMenuItem_Click(object sender, EventArgs e)
		{
			Settings.Default.PreviewSubLayouts = showSubpanesToolStripMenuItem.Checked;
			Settings.Default.Save();
			glControl?.Invalidate();
		}

		private void addCheckedToFilterToolStripMenuItem_Click(object sender, EventArgs e)
		{
			ApplyCheckedPanesAsFilter();
		}

		private void clearPaneFilterToolStripMenuItem_Click(object sender, EventArgs e)
		{
			ClearPaneFilter();
		}

		public void ShowPaletteWindow()
		{
			EnsurePaletteWindow();
			if (currentPalette == null && layout.Mat1 != null)
				currentPalette = new MaterialPaletteTag(layout) { Filter = PaneFilter };
			if (currentPalette != null)
				paletteWindow.BindPalette(currentPalette);
			if (!paletteWindow.Visible)
			{
				if (paletteWindow.Location == Point.Empty || paletteWindow.Location.X < 0)
				{
					paletteWindow.StartPosition = FormStartPosition.Manual;
					paletteWindow.Location = new Point(Right - 40, Top + 40);
				}
				paletteWindow.Show(this);
			}
			else
			{
				paletteWindow.BringToFront();
				paletteWindow.Focus();
			}
		}

		void EnsurePaletteWindow()
		{
			if (paletteWindow != null && !paletteWindow.IsDisposed)
				return;
			paletteWindow = new PaletteWindow(this);
			paletteWindow.RefreshStatus();
		}

		void RefreshPaletteWindow()
		{
			if (paletteWindow == null || paletteWindow.IsDisposed || currentPalette == null)
				return;
			paletteWindow.BindPalette(currentPalette);
		}

		public void SetPaletteHighlight(RGBAColor color)
		{
			hasPaletteHighlight = true;
			paletteHighlightColor = color;
			glControl?.Invalidate();
		}

		public void OnPaletteEdited()
		{
			bntxPreview.InvalidateShaded();
			glControl?.Invalidate();
			RefreshPaletteWindow();
			propertyGridBaseline = CaptureLayoutSnapshot();
			UpdateUndoMenuState();
		}

		/// <summary>Call before mutating the layout (palette writes, structural edits).</summary>
		public void PushUndoState()
		{
			undoStack.Push(CaptureLayoutSnapshot());
			UpdateUndoMenuState();
		}

		byte[] CaptureLayoutSnapshot()
		{
			try
			{
				return layout?.SaveFile();
			}
			catch
			{
				return null;
			}
		}

		void UpdateUndoMenuState()
		{
			if (undoToolStripMenuItem != null)
				undoToolStripMenuItem.Enabled = undoStack.CanUndo;
			if (redoToolStripMenuItem != null)
				redoToolStripMenuItem.Enabled = undoStack.CanRedo;
		}

		private void undoToolStripMenuItem_Click(object sender, EventArgs e) => PerformUndo();

		private void redoToolStripMenuItem_Click(object sender, EventArgs e) => PerformRedo();

		void PerformUndo()
		{
			byte[] prev = undoStack.Undo(CaptureLayoutSnapshot());
			if (prev == null) return;
			RestoreLayoutSnapshot(prev);
			UpdateUndoMenuState();
		}

		void PerformRedo()
		{
			byte[] next = undoStack.Redo(CaptureLayoutSnapshot());
			if (next == null) return;
			RestoreLayoutSnapshot(next);
			UpdateUndoMenuState();
		}

		void RestoreLayoutSnapshot(byte[] data)
		{
			if (data == null || data.Length == 0) return;

			var filterNames = new List<string>();
			foreach (var root in PaneFilter.Roots)
			{
				if (root is INamedPane named && !string.IsNullOrEmpty(named.PaneName))
					filterNames.Add(named.PaneName);
			}
			var filterMode = PaneFilter.Mode;
			bool hadFilter = PaneFilter.IsActive;

			layout = new BflytFile(data);
			activeLayout = layout;
			currentPalette = null;
			bntxPreview.InvalidateShaded();

			PaneFilter.Clear();
			if (hadFilter && filterNames.Count > 0)
			{
				PaneFilter.Mode = filterMode;
				var roots = new List<BasePane>();
				foreach (var name in filterNames)
				{
					var pane = layout[name];
					if (pane != null)
						roots.Add(pane);
				}
				PaneFilter.SetRoots(roots);
			}

			UpdateView();
			propertyGridBaseline = CaptureLayoutSnapshot();
			RefreshPaletteWindow();
			paletteWindow?.RefreshStatus();
		}

		public void OnPaneFilterChanged()
		{
			if (currentPalette != null)
				currentPalette.Filter = PaneFilter;
			RefreshPaletteWindow();
			glControl?.Invalidate();
		}

		public void ApplyCheckedPanesAsFilter()
		{
			PaneFilter.SetRoots(CollectCheckedPanes());
			SyncFilterChecksToTree();
			OnPaneFilterChanged();
			paletteWindow?.RefreshStatus();
		}

		public void ClearPaneFilter()
		{
			PaneFilter.Clear();
			suppressTreeCheckEvents = true;
			try
			{
				UncheckAllTreeNodes(treeView1.Nodes);
			}
			finally
			{
				suppressTreeCheckEvents = false;
			}
			checkRangeAnchor = null;
			OnPaneFilterChanged();
			paletteWindow?.RefreshStatus();
		}

		List<BasePane> CollectCheckedPanes()
		{
			var list = new List<BasePane>();
			CollectCheckedPanes(treeView1.Nodes, list);
			return list;
		}

		static void CollectCheckedPanes(TreeNodeCollection nodes, List<BasePane> list)
		{
			foreach (TreeNode n in nodes)
			{
				if (n.Checked && n.Tag is BasePane pane)
					list.Add(pane);
				CollectCheckedPanes(n.Nodes, list);
			}
		}

		void SyncFilterChecksToTree()
		{
			suppressTreeCheckEvents = true;
			try
			{
				UncheckAllTreeNodes(treeView1.Nodes);
				if (!PaneFilter.IsActive)
					return;
				CheckFilterRoots(treeView1.Nodes);
			}
			finally
			{
				suppressTreeCheckEvents = false;
			}
		}

		void CheckFilterRoots(TreeNodeCollection nodes)
		{
			foreach (TreeNode n in nodes)
			{
				if (n.Tag is BasePane pane && PaneFilter.Roots.Contains(pane))
					n.Checked = true;
				CheckFilterRoots(n.Nodes);
			}
		}

		static void UncheckAllTreeNodes(TreeNodeCollection nodes)
		{
			foreach (TreeNode n in nodes)
			{
				n.Checked = false;
				UncheckAllTreeNodes(n.Nodes);
			}
		}

		private void GlControl_MouseUp(object sender, MouseEventArgs e)
		{
			if (canvasDragMode == CanvasDragMode.Marquee && e.Button == MouseButtons.Left)
			{
				marqueeEndClient = e.Location;
				CommitMarqueeSelection(addToSelection: ModifierKeys.HasFlag(Keys.Shift));
				canvasDragMode = CanvasDragMode.None;
				marqueePreviewHits.Clear();
				glControl.Invalidate();
				return;
			}

			if (canvasDragMode == CanvasDragMode.MoveObject && DraggedObject)
			{
				DraggedObject = false;
				propertyGrid1.Refresh();
			}

			canvasDragMode = CanvasDragMode.None;
			marqueePreviewHits.Clear();
		}

		void DrawMarqueeOverlay()
		{
			if (canvasDragMode != CanvasDragMode.Marquee)
				return;

			NormalizeClientRect(marqueeStartClient, marqueeEndClient, out int x0, out int y0, out int x1, out int y1);
			if (x1 - x0 < 2 && y1 - y0 < 2)
				return;

			GL.Disable(EnableCap.Texture2D);
			GL.Enable(EnableCap.Blend);
			GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
			GL.MatrixMode(MatrixMode.Modelview);
			GL.PushMatrix();
			GL.LoadIdentity();

			GL.Color4(0.2f, 0.55f, 1f, 0.15f);
			GL.Begin(PrimitiveType.Quads);
			GL.Vertex2(x0, y0);
			GL.Vertex2(x1, y0);
			GL.Vertex2(x1, y1);
			GL.Vertex2(x0, y1);
			GL.End();

			GL.Color4(0.2f, 0.55f, 1f, 0.95f);
			GL.Begin(PrimitiveType.LineLoop);
			GL.Vertex2(x0, y0);
			GL.Vertex2(x1, y0);
			GL.Vertex2(x1, y1);
			GL.Vertex2(x0, y1);
			GL.End();

			GL.PopMatrix();
			GL.Enable(EnableCap.Texture2D);
		}

		static void NormalizeClientRect(Point a, Point b, out int x0, out int y0, out int x1, out int y1)
		{
			x0 = Math.Min(a.X, b.X);
			y0 = Math.Min(a.Y, b.Y);
			x1 = Math.Max(a.X, b.X);
			y1 = Math.Max(a.Y, b.Y);
		}

		void UpdateMarqueePreviewHits()
		{
			marqueePreviewHits.Clear();
			NormalizeClientRect(marqueeStartClient, marqueeEndClient, out int x0, out int y0, out int x1, out int y1);
			if (x1 - x0 < 3 || y1 - y0 < 3)
				return;
			foreach (var pane in CollectFullyEnclosedPanes(x0, y0, x1, y1))
				marqueePreviewHits.Add(pane);
		}

		void CommitMarqueeSelection(bool addToSelection)
		{
			NormalizeClientRect(marqueeStartClient, marqueeEndClient, out int x0, out int y0, out int x1, out int y1);
			if (x1 - x0 < 3 || y1 - y0 < 3)
				return;

			var hits = CollectFullyEnclosedPanes(x0, y0, x1, y1);
			if (!addToSelection)
			{
				UncheckAllTreeNodes(treeView1.Nodes);
				PaneFilter.Mode = PaneFilterMode.Whitelist;
				PaneFilter.SetRoots(hits);
			}
			else
			{
				var combined = new HashSet<BasePane>(PaneFilter.Roots);
				foreach (var p in hits)
					combined.Add(p);
				PaneFilter.Mode = PaneFilterMode.Whitelist;
				PaneFilter.SetRoots(combined);
			}

			SyncFilterChecksToTree();
			OnPaneFilterChanged();
			paletteWindow?.RefreshStatus();

			if (hits.Count > 0)
			{
				TreeNode node = FindTreeNodeForPane(hits[0]);
				if (node != null)
				{
					treeView1.SelectedNode = node;
					node.EnsureVisible();
				}
			}
		}

		List<Pan1Pane> CollectFullyEnclosedPanes(int selX0, int selY0, int selX1, int selY1)
		{
			var hits = new List<Pan1Pane>();
			foreach (var kv in paneScreenBounds)
			{
				if (kv.Value.FullyInside(selX0, selY0, selX1, selY1))
					hits.Add(kv.Key);
			}
			return hits;
		}

		void CachePaneScreenBounds(Pan1Pane pane)
		{
			var r = pane.transformedRect;
			if (r.width <= 0 || r.height <= 0)
				return;

			float[] mv = new float[16];
			GL.GetFloat(GetPName.ModelviewMatrix, mv);

			// Column-major modelview (same space DrawPane vertices use).
			void Xform(float lx, float ly, out float sx, out float sy)
			{
				sx = mv[0] * lx + mv[4] * ly + mv[12];
				sy = mv[1] * lx + mv[5] * ly + mv[13];
			}

			Xform(r.x, r.y, out float x0, out float y0);
			Xform(r.x + r.width, r.y, out float x1, out float y1);
			Xform(r.x + r.width, r.y + r.height, out float x2, out float y2);
			Xform(r.x, r.y + r.height, out float x3, out float y3);

			paneScreenBounds[pane] = new PaneScreenQuad
			{
				X0 = x0, Y0 = y0,
				X1 = x1, Y1 = y1,
				X2 = x2, Y2 = y2,
				X3 = x3, Y3 = y3
			};
		}

		TreeNode FindTreeNodeForPane(BasePane pane)
		{
			return FindTreeNodeForPane(treeView1.Nodes, pane);
		}

		static TreeNode FindTreeNodeForPane(TreeNodeCollection nodes, BasePane pane)
		{
			foreach (TreeNode n in nodes)
			{
				if (ReferenceEquals(n.Tag, pane))
					return n;
				TreeNode child = FindTreeNodeForPane(n.Nodes, pane);
				if (child != null)
					return child;
			}
			return null;
		}

		private void removeToolStripMenuItem_Click(object sender, EventArgs e)
		{
			if (treeView1.SelectedNode != null)
			{
				var parent = ((BasePane)treeView1.SelectedNode.Tag).Parent;
				if (parent == null)
				{
					MessageBox.Show("You can't remove a root pane");
					return;
				}

				PushUndoState();
				layout.RemovePane((BasePane)treeView1.SelectedNode.Tag);
				
				UpdateView(parent);
			}
		}

		private void nullPaneToolStripMenuItem_Click(object sender, EventArgs e) =>
			AddPane(new Pan1Pane("pan1", layout.FileByteOrder));

		private void pic1PaneToolStripMenuItem_Click(object sender, EventArgs e) =>
			AddPane(new Pic1Pane(layout.FileByteOrder));

		private void txtPaneToolStripMenuItem_Click(object sender, EventArgs e) =>
			AddPane(new Txt1Pane(layout.FileByteOrder));

		private void clonePaneToolStripMenuItem_Click(object sender, EventArgs e)
		{
			Pan1Pane pane = treeView1.SelectedNode.Tag as Pan1Pane;
			PushUndoState();
			layout.AddPane(-1, pane.Parent, pane.Clone());
			UpdateView(pane);
		}

		void AddPane(BasePane p)
		{
			if (treeView1.SelectedNode.Tag as Pan1Pane == null) return;
			PushUndoState();
			layout.AddPane(-1, treeView1.SelectedNode.Tag as Pan1Pane, p);
			UpdateView(p);
		}

		private void AddGroupToolStripMenuItem_Click(object sender, EventArgs e)
		{
			Grp1Pane pane = new Grp1Pane(layout.Version);
			pane.GroupName = "New group";
			PushUndoState();
			layout.AddPane(-1, treeView1.SelectedNode.Tag as Grp1Pane, pane);
			UpdateView(pane);
		}

		private void TreeView1_NodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
		{
			if (e.Node.Tag is Grp1Pane)
				treeView1.ContextMenuStrip = GroupMenuStrip;
			else if (e.Node.Tag is Pan1Pane)
				treeView1.ContextMenuStrip = PaneMenuStrip;
			else if (e.Node.Tag is TextureTag)
				treeView1.ContextMenuStrip = TextureMenuStrip;
			else if (e.Node.Tag is BflytMaterial)
				treeView1.ContextMenuStrip = MaterialMenuStrip;
			else
				treeView1.ContextMenuStrip = null;
		}

		private void NewTextureToolStripMenuItem_Click(object sender, EventArgs e)
		{
			string name = "New_texture";
			if (InputDialog.Show("Add new texture", "Enter a name for the new texture.", ref name) != DialogResult.OK) return;
			PushUndoState();
			layout.GetTexturesSection().Textures.Add(name);
			UpdateView(name);
		}

		private void RemoveTexture_Click(object sender, EventArgs e)
		{
			if (treeView1.SelectedNode.Parent == null) return; //the texture must be in the root textures node
			PushUndoState();
			layout.Tex1.Textures.Remove(((TextureTag)treeView1.SelectedNode.Tag).TexName);
			UpdateView();
		}

		private void RemoveMaterial_Click(object sender, EventArgs e)
		{
			if (treeView1.SelectedNode.Parent == null) return;
			PushUndoState();
			layout.Mat1.Materials.Remove((BflytMaterial)treeView1.SelectedNode.Tag);
			UpdateView();
		}

		private void CloneMaterialToolStripMenuItem_Click(object sender, EventArgs e)
		{
			if (treeView1.SelectedNode.Parent == null) return;
			var selected = (BflytMaterial)treeView1.SelectedNode.Tag;
			var next = new BflytMaterial(selected.Write(layout.Version, layout.FileByteOrder), layout.FileByteOrder, layout.Version);
			if (next.Name.Length < 27)
				next.Name += "_";
			else
				next.Name = next.Name.Substring(0,26) + "_";
			PushUndoState();
			layout.GetMaterialsSection().Materials.Add(next);
			UpdateView(next);
		}

		private void MoveUpToolStripMenuItem_Click(object sender, EventArgs e)
		{
			var p = treeView1.SelectedNode.Tag as BasePane;
			if (p == null || p.Parent == null) return;
			PushUndoState();
			layout.MovePane(p, p.Parent, p.Parent.Children.IndexOf(p) - 1);
			UpdateView(p);
		}

		private void MoveDownToolStripMenuItem_Click(object sender, EventArgs e)
		{
			var p = treeView1.SelectedNode.Tag as BasePane;
			if (p == null || p.Parent == null) return;
			PushUndoState();
			layout.MovePane(p, p.Parent, p.Parent.Children.IndexOf(p) + 1);
			UpdateView(p);
		}

		#region Pane drag and drop
		// Reference https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.treeview.itemdrag?view=windowsdesktop-8.0
		private void treeView1_ItemDrag(object sender, ItemDragEventArgs e)
		{
			// Only allow dragging pan1 panes
			if (e.Item is TreeNode node && node.Tag is Pan1Pane pane)
			{
				// Only when they are not a root
				if (pane.Parent == null) return;
				// Only from the logical hierarchy, in theory this check doesn't really matter
				if (FindRoot(node) != Pan1Root) return;

				DoDragDrop(e.Item, DragDropEffects.Move);
			}
		}

		private void treeView1_DragEnter(object sender, DragEventArgs e)
		{
			e.Effect = e.AllowedEffect;
		}

		private void treeView1_DragOver(object sender, DragEventArgs e)
		{
			// Retrieve the client coordinates of the mouse position.
			Point targetPoint = treeView1.PointToClient(new Point(e.X, e.Y));

			// Select the node at the mouse position.
			treeView1.SelectedNode = treeView1.GetNodeAt(targetPoint);
		}

		private void treeView1_DragDrop(object sender, DragEventArgs e)
		{
			// Retrieve the client coordinates of the drop location.
			Point targetPoint = treeView1.PointToClient(new Point(e.X, e.Y));

			// Retrieve the node at the drop location.
			BasePane target = treeView1.GetNodeAt(targetPoint)?.Tag as BasePane;

			// Retrieve the node that was dragged.
			BasePane dragged = ((TreeNode)e.Data.GetData(typeof(TreeNode))).Tag as BasePane;

			if (target == null || dragged == null) return;

			// Confirm that the node at the drop location is not 
			// the dragged node or a descendant of the dragged node.
			if (target == dragged || dragged.ContainsChild(target)) return;

			PushUndoState();
			layout.RemovePane(dragged);
			layout.AddPane(-1, target, dragged);
			UpdateView(dragged);
		}
		#endregion

		private void EditorView_KeyDown(object sender, KeyEventArgs e)
        {
			e.SuppressKeyPress = true;
			if (e.Shift && e.Control && e.KeyCode == Keys.S) saveBFLYTToolStripMenuItem.PerformClick();
			else if (e.Control && e.KeyCode == Keys.S) saveToolStripMenuItem.PerformClick();
			else if (e.Control && e.Shift && e.KeyCode == Keys.Z) { PerformRedo(); }
			else if (e.Control && e.KeyCode == Keys.Z) { PerformUndo(); }
			else if (e.Control && e.KeyCode == Keys.Y) { PerformRedo(); }
			else if (e.Control && e.KeyCode == Keys.L) treeView1.ExpandAll();
			else if (e.Control && e.KeyCode == Keys.K) treeView1.CollapseAll();
			else e.SuppressKeyPress = false;
		}
    }

	//used for tagging and root node
	internal class TextureTag
	{
		internal string TexName;
		public TextureTag(string n = null) { TexName = n; }
		public override bool Equals(object obj)
		{
			if (obj is string)
				return TexName == (string)obj;
			else if (obj is TextureTag)
				return ((TextureTag)obj).TexName == TexName;
			return base.Equals(obj);
		}

		public static bool operator ==(TextureTag a, TextureTag b) => a.Equals(b);
		public static bool operator !=(TextureTag a, TextureTag b) => !a.Equals(b);
	}

	/// <summary>
	/// Palette tree node: unique material Black/White colors and pane/window/text vertex colors.
	/// Editing a color replaces matching slots; when a PaneColorFilter is active, only in-scope
	/// panes (and materials they reference) are updated.
	/// </summary>
	internal class MaterialPaletteTag
	{
		public struct PaletteRow
		{
			public int Index;
			public bool IsVertex;
			public RGBAColor Color;
			public int Usages;
			public int ScopedUsages;
			public string UsesText;
		}

		readonly BflytFile layout;
		readonly List<BflytMaterial> materials;
		readonly List<RGBAColor> materialColors = new List<RGBAColor>();
		readonly List<RGBAColor> vertexColors = new List<RGBAColor>();

		public PaneColorFilter Filter { get; set; }

		public int MaterialCount => materialColors.Count;
		public int VertexCount => vertexColors.Count;

		public MaterialPaletteTag(BflytFile layout)
		{
			this.layout = layout;
			materials = layout.Mat1?.Materials ?? new List<BflytMaterial>();
			RescanColors();
		}

		void RescanColors()
		{
			materialColors.Clear();
			vertexColors.Clear();

			var matSeen = new HashSet<RGBAColor>();
			foreach (var mat in materials)
			{
				if (matSeen.Add(mat.ForegroundColor))
					materialColors.Add(mat.ForegroundColor);
				if (matSeen.Add(mat.BackgroundColor))
					materialColors.Add(mat.BackgroundColor);
			}

			var vtxSeen = new HashSet<RGBAColor>();
			void AddVtx(RGBAColor c)
			{
				if (vtxSeen.Add(c))
					vertexColors.Add(c);
			}

			if (layout.ElementsRoot != null)
			{
				foreach (var pane in layout.EnumeratePanes(layout.ElementsRoot))
				{
					if (pane is Pic1Pane pic)
					{
						AddVtx(pic.ColorTopLeft);
						AddVtx(pic.ColorTopRight);
						AddVtx(pic.ColorBottomLeft);
						AddVtx(pic.ColorBottomRight);
					}
					else if (pane is Wnd1Pane wnd && wnd.Content != null)
					{
						AddVtx(wnd.Content.ColorTopLeft);
						AddVtx(wnd.Content.ColorTopRight);
						AddVtx(wnd.Content.ColorBottomLeft);
						AddVtx(wnd.Content.ColorBottomRight);
					}
					else if (pane is Txt1Pane txt)
					{
						AddVtx(txt.FontTopColor);
						AddVtx(txt.FontBottomColor);
						AddVtx(txt.ShadowTopColor);
						AddVtx(txt.ShadowBottomColor);
					}
				}
			}
		}

		public List<PaletteRow> GetRows()
		{
			bool filterOn = Filter != null && Filter.IsActive;
			HashSet<int> scopedMats = filterOn ? CollectInScopeMaterialIndices() : null;

			var rows = new List<PaletteRow>(materialColors.Count + vertexColors.Count);
			for (int i = 0; i < materialColors.Count; i++)
			{
				var c = materialColors[i];
				int total = MaterialUsageCount(c, null);
				int scoped = filterOn ? MaterialUsageCount(c, scopedMats) : total;
				rows.Add(new PaletteRow
				{
					Index = i,
					IsVertex = false,
					Color = c,
					Usages = total,
					ScopedUsages = scoped,
					UsesText = filterOn ? $"{scoped}/{total}" : total.ToString()
				});
			}
			for (int i = 0; i < vertexColors.Count; i++)
			{
				var c = vertexColors[i];
				int total = VertexUsageCount(c, scopedOnly: false);
				int scoped = filterOn ? VertexUsageCount(c, scopedOnly: true) : total;
				rows.Add(new PaletteRow
				{
					Index = i,
					IsVertex = true,
					Color = c,
					Usages = total,
					ScopedUsages = scoped,
					UsesText = filterOn ? $"{scoped}/{total}" : total.ToString()
				});
			}
			return rows;
		}

		public void SetColor(int index, bool isVertex, RGBAColor newColor)
		{
			if (isVertex)
				SetVertexColor(index, newColor);
			else
				SetMaterialColor(index, newColor);
			RescanColors();
		}

		void SetMaterialColor(int index, RGBAColor newColor)
		{
			if (index < 0 || index >= materialColors.Count) return;
			RGBAColor oldColor = materialColors[index];
			if (oldColor == newColor) return;

			HashSet<int> scoped = null;
			if (Filter != null && Filter.IsActive)
				scoped = CollectInScopeMaterialIndices();

			for (int i = 0; i < materials.Count; i++)
			{
				if (scoped != null && !scoped.Contains(i))
					continue;
				var mat = materials[i];
				if (mat.ForegroundColor == oldColor)
					mat.ForegroundColor = newColor;
				if (mat.BackgroundColor == oldColor)
					mat.BackgroundColor = newColor;
			}
		}

		void SetVertexColor(int index, RGBAColor newColor)
		{
			if (index < 0 || index >= vertexColors.Count) return;
			RGBAColor oldColor = vertexColors[index];
			if (oldColor == newColor) return;

			if (layout.ElementsRoot == null)
				return;

			foreach (var pane in layout.EnumeratePanes(layout.ElementsRoot))
			{
				if (Filter != null && !Filter.IsPaneInScope(pane))
					continue;

				if (pane is Pic1Pane pic)
				{
					if (pic.ColorTopLeft == oldColor) pic.ColorTopLeft = newColor;
					if (pic.ColorTopRight == oldColor) pic.ColorTopRight = newColor;
					if (pic.ColorBottomLeft == oldColor) pic.ColorBottomLeft = newColor;
					if (pic.ColorBottomRight == oldColor) pic.ColorBottomRight = newColor;
				}
				else if (pane is Wnd1Pane wnd && wnd.Content != null)
				{
					if (wnd.Content.ColorTopLeft == oldColor) wnd.Content.ColorTopLeft = newColor;
					if (wnd.Content.ColorTopRight == oldColor) wnd.Content.ColorTopRight = newColor;
					if (wnd.Content.ColorBottomLeft == oldColor) wnd.Content.ColorBottomLeft = newColor;
					if (wnd.Content.ColorBottomRight == oldColor) wnd.Content.ColorBottomRight = newColor;
				}
				else if (pane is Txt1Pane txt)
				{
					if (txt.FontTopColor == oldColor) txt.FontTopColor = newColor;
					if (txt.FontBottomColor == oldColor) txt.FontBottomColor = newColor;
					if (txt.ShadowTopColor == oldColor) txt.ShadowTopColor = newColor;
					if (txt.ShadowBottomColor == oldColor) txt.ShadowBottomColor = newColor;
				}
			}
		}

		HashSet<int> CollectInScopeMaterialIndices()
		{
			var set = new HashSet<int>();
			if (layout.ElementsRoot == null)
				return set;

			foreach (var pane in layout.EnumeratePanes(layout.ElementsRoot))
			{
				if (Filter != null && !Filter.IsPaneInScope(pane))
					continue;

				if (pane is Pic1Pane pic)
					set.Add(pic.MaterialIndex);
				else if (pane is Txt1Pane txt)
					set.Add(txt.MaterialIndex);
				else if (pane is Wnd1Pane wnd)
				{
					if (wnd.Content != null)
						set.Add(wnd.Content.MaterialIndex);
					if (wnd.Frames != null)
					{
						foreach (var fr in wnd.Frames)
							set.Add(fr.MaterialIndex);
					}
				}
			}
			return set;
		}

		int MaterialUsageCount(RGBAColor color, HashSet<int> onlyIndices)
		{
			int count = 0;
			for (int i = 0; i < materials.Count; i++)
			{
				if (onlyIndices != null && !onlyIndices.Contains(i))
					continue;
				var mat = materials[i];
				if (mat.ForegroundColor == color) count++;
				if (mat.BackgroundColor == color) count++;
			}
			return count;
		}

		int VertexUsageCount(RGBAColor color, bool scopedOnly)
		{
			int count = 0;
			if (layout.ElementsRoot == null)
				return 0;
			foreach (var pane in layout.EnumeratePanes(layout.ElementsRoot))
			{
				if (scopedOnly && Filter != null && !Filter.IsPaneInScope(pane))
					continue;

				if (pane is Pic1Pane pic)
				{
					if (pic.ColorTopLeft == color) count++;
					if (pic.ColorTopRight == color) count++;
					if (pic.ColorBottomLeft == color) count++;
					if (pic.ColorBottomRight == color) count++;
				}
				else if (pane is Wnd1Pane wnd && wnd.Content != null)
				{
					if (wnd.Content.ColorTopLeft == color) count++;
					if (wnd.Content.ColorTopRight == color) count++;
					if (wnd.Content.ColorBottomLeft == color) count++;
					if (wnd.Content.ColorBottomRight == color) count++;
				}
				else if (pane is Txt1Pane txt)
				{
					if (txt.FontTopColor == color) count++;
					if (txt.FontBottomColor == color) count++;
					if (txt.ShadowTopColor == color) count++;
					if (txt.ShadowBottomColor == color) count++;
				}
			}
			return count;
		}
	}
}
