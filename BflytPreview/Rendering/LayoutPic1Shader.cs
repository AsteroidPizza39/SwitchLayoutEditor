using System;
using OpenTK;

namespace BflytPreview.Rendering
{
	/// <summary>
	/// UV SRT helpers matching Switch Toolbox BxlytShader.LoadTextureUniforms.
	/// (Custom GLSL Pic1 programs are not used — OpenTK GLControl immediate-mode preview
	/// is unreliable with shaders; shading is baked in BntxPreviewCache instead.)
	/// </summary>
	internal static class LayoutPic1Shader
	{
		public static Matrix4 BuildTextureTransform(float translateX, float translateY, float rotateDeg, float scaleX, float scaleY)
		{
			if (Math.Abs(scaleX) < 1e-6f) scaleX = 1e-6f;
			if (Math.Abs(scaleY) < 1e-6f) scaleY = 1e-6f;

			var matScale = Matrix4.CreateScale(scaleX, scaleY, 1f);
			var matRotate = Matrix4.CreateFromAxisAngle(Vector3.UnitZ, MathHelper.DegreesToRadians(rotateDeg));
			var matTranslate = Matrix4.CreateTranslation(
				translateX / scaleX - 0.5f,
				translateY / scaleY - 0.5f,
				0f);
			return matRotate * matTranslate * matScale;
		}

		public static Matrix4 IdentityTransform =>
			BuildTextureTransform(0f, 0f, 0f, 1f, 1f);
	}
}
