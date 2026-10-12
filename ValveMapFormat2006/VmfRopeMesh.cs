using UnityEngine;

namespace AeternumGames.Chisel.Import.Source.ValveMapFormat2006
{
	/// <summary>
	/// The strip a rope is drawn as: two vertices per render point, with everything the Cable shader needs to widen it
	/// into a camera-facing ribbon at draw time.
	/// <para>
	/// A <see cref="LineRenderer"/> cannot be used for this: its width is WORLD space and its quads are expanded on the
	/// CPU, so by the time a shader sees a rope there is no centre line left to widen: a distant cable falls below a
	/// pixel, samples through, and shimmers. Source does not have this problem because it decides a rope's width in
	/// SCREEN space, per point, per camera (<c>c_rope.cpp</c>'s <c>ShouldUseFakeAA</c>): below a floor it holds the
	/// rope at that width and takes the difference out of the alpha instead.
	/// </para>
	/// <para>
	/// Doing that needs the centre line to survive into the vertex shader, which is the reason for this mesh. It
	/// also makes a rope right in several cameras at once - the sky view and the Scene view draw the same ropes in the
	/// same frame, and a width computed on the CPU can only ever be correct for one of them.
	/// </para>
	/// </summary>
	public static class VmfRopeMesh
	{
		/// <summary>
		/// Builds the strip, in the rope object's own space.
		/// <list type="bullet">
		/// <item><c>POSITION</c> - the point on the rope's centre line, the SAME for both vertices of a pair. The
		/// shader moves them apart; nothing here knows how far.</item>
		/// <item><c>TANGENT</c> - xyz along the rope, w the side (-1 or +1), which is what tells the two apart.</item>
		/// <item><c>TEXCOORD0</c> - x along the rope (Source's V, see <see cref="VmfRope.TextureStep"/>), y across it.
		/// The Cable shader samples the two the other way around, as it always has.</item>
		/// <item><c>TEXCOORD1</c> - x the rope's half width in metres, so a scene can hold ropes of different widths
		/// without a material each.</item>
		/// </list>
		/// </summary>
		/// <param name="points">The rope's render points, in the rope object's space</param>
		/// <param name="halfWidth">Half the rope's width, in metres</param>
		/// <param name="textureStep">How far the texture advances per point (negative: textures import upside down)</param>
		public static Mesh Build(Vector3[] points, float halfWidth, float textureStep)
		{
			if (points == null || points.Length < 2)
				return null;

			var count     = points.Length;
			var vertices  = new Vector3[count * 2];
			var tangents  = new Vector4[count * 2];
			var uv        = new Vector2[count * 2];
			var uv2       = new Vector2[count * 2];
			var triangles = new int[(count - 1) * 6];

			for (int i = 0; i < count; i++)
			{
				// Along the rope: the direction through this point, so the two ends use the one segment they have and
				// every point between them uses the average. A kink that used its own segment would twist the ribbon.
				var previous = points[Mathf.Max(i - 1, 0)];
				var next     = points[Mathf.Min(i + 1, count - 1)];
				var along    = next - previous;
				// Two points on top of each other leave no direction to take; borrow the nearest one that has any
				for (int step = 1; along.sqrMagnitude < 1e-12f && step < count; step++)
				{
					previous = points[Mathf.Max(i - step, 0)];
					next     = points[Mathf.Min(i + step, count - 1)];
					along    = next - previous;
				}
				along = along.sqrMagnitude < 1e-12f ? Vector3.forward : along.normalized;

				var v = i * 2;
				vertices[v]     = points[i];
				vertices[v + 1] = points[i];
				tangents[v]     = new Vector4(along.x, along.y, along.z, -1f);
				tangents[v + 1] = new Vector4(along.x, along.y, along.z,  1f);
				uv[v]           = new Vector2(textureStep * i, 0f);
				uv[v + 1]       = new Vector2(textureStep * i, 1f);
				uv2[v]          = new Vector2(halfWidth, 0f);
				uv2[v + 1]      = new Vector2(halfWidth, 0f);

				if (i + 1 < count)
				{
					var t = i * 6;
					triangles[t]     = v;
					triangles[t + 1] = v + 1;
					triangles[t + 2] = v + 2;
					triangles[t + 3] = v + 1;
					triangles[t + 4] = v + 3;
					triangles[t + 5] = v + 2;
				}
			}

			var mesh = new Mesh { name = "Rope" };
			mesh.indexFormat = count * 2 > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32
												 : UnityEngine.Rendering.IndexFormat.UInt16;
			mesh.vertices  = vertices;
			mesh.tangents  = tangents;
			mesh.uv        = uv;
			mesh.uv2       = uv2;
			mesh.triangles = triangles;
			// Normals are not authored: the strip faces the camera, so the shader has to work them out at draw time
			// for the same reason it works the width out there. What is here would only be wrong.
			mesh.RecalculateBounds();
			// The shader moves vertices off the centre line, and culling does not know that. At distance the widening
			// is in screen space and can be metres, so the bounds are padded by enough that a rope is not culled while
			// part of it is still on screen. A rope is a handful of vertices; generous bounds cost nothing.
			var bounds = mesh.bounds;
			bounds.Expand(2f * Mathf.Max(halfWidth, 0.5f) + 2f);
			mesh.bounds = bounds;
			return mesh;
		}
	}
}
