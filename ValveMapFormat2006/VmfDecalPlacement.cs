using Chisel.Core;
using Chisel.Import.Source.VPKTools;
using Unity.Mathematics;
using static Unity.Mathematics.math;

namespace AeternumGames.Chisel.Import.Source.ValveMapFormat2006
{
	/// <summary>
	/// Where a Chisel decal goes to draw a Source decal as Source places it: plain math on the map's own values in
	/// Source units and axes (see com.chisel Documentation~/Design/Decals.md).
	/// <list type="bullet">
	/// <item>info_overlay: a quad in its own basis, projected along <c>BasisNormal</c> (vbsp <c>overlay.cpp</c>). The
	/// engine keeps <c>BasisU</c> and makes V as N x U, turned to agree with the map's <c>BasisV</c>. Corner i is at
	/// origin + U x + V y of <c>uv</c>i and has texture coordinates (StartU, StartV), (StartU, EndV), (EndU, EndV),
	/// (EndU, StartV).</item>
	/// <item>infodecal: a point. Every surface whose plane is within <see cref="kDecalDistance"/> gets the image
	/// centred on the point, laid on by the surface's normal (<see cref="ComputeBasis"/>).</item>
	/// </list>
	/// A Chisel decal projects along one direction, so an infodecal becomes one decal per surface normal.
	/// </summary>
	public static class VmfDecalPlacement
	{
		/// <summary>DECAL_DISTANCE: how far from an infodecal a surface's plane may be, in Source units.</summary>
		public const double kDecalDistance = 4.0;

		/// <summary>How far past the faces a decal's box reaches, in Source units.</summary>
		public const double kDepthMargin = 1.0;

		const double kSin45 = 0.70710678118654752440;
		const double kEpsilon = 1e-6;

		/// <summary>A decal's image in Source space.</summary>
		public struct Frame
		{
			/// <summary>The image's center, on the decal's plane.</summary>
			public double3 center;
			/// <summary>The image's +x: cross(up, normal), which is where a Chisel decal looking along -normal with
			/// this up puts its local +x.</summary>
			public double3 right;
			/// <summary>The image's +y.</summary>
			public double3 up;
			/// <summary>Out of the surfaces, towards whoever looks at the decal.</summary>
			public double3 normal;
			/// <summary>Width (along right) and height (along up).</summary>
			public double2 size;
			/// <summary>Texture coordinates as a Chisel decal takes them: uvOffset + uvScale * p, p from (0,0) at
			/// the image's lower left to (1,1) at its upper right. Unity's v runs up the image, Source's down.</summary>
			public float2 uvScale;
			public float2 uvOffset;
			/// <summary>False for a quad that isn't a rectangle; the frame is then the rectangle with its first edge.</summary>
			public bool isRectangle;
		}

		static double3 InBasis(double2 point, double3 u, double3 v)
		{
			return u * point.x + v * point.y;
		}

		/// <summary>An info_overlay's image. Returns false for a degenerate basis or quad.</summary>
		/// <param name="corners">uv0 to uv3 (x and y; z is unused).</param>
		/// <param name="textureU">StartU and EndU.</param>
		/// <param name="textureV">StartV and EndV.</param>
		public static bool TryGetOverlayFrame(double3 origin, double3 basisU, double3 basisV, double3 basisNormal,
											  double2 corner0, double2 corner1, double2 corner2, double2 corner3,
											  double2 textureU, double2 textureV, out Frame frame)
		{
			frame = default;
			var n = normalizesafe(basisNormal);
			var u = normalizesafe(basisU - n * dot(basisU, n));
			if (lengthsq(n) < 0.5 || lengthsq(u) < 0.5)
				return false;
			var v = cross(n, u);
			if (dot(v, basisV) < 0)
				v = -v;

			// Texture u runs from StartU to EndU from corner 0 to corner 3, v from StartV to EndV from corner 0 to corner 1
			var edgeU = InBasis(corner3 - corner0, u, v);
			var edgeV = InBasis(corner1 - corner0, u, v);
			var width = length(edgeU);
			if (!(width > kEpsilon))
				return false;
			var uDirection = edgeU / width;
			var edgeVAcross = edgeV - uDirection * dot(edgeV, uDirection);
			var height = length(edgeVAcross);
			if (!(height > kEpsilon))
				return false;
			var vDirection = edgeVAcross / height;

			// The image's top is where Source's v is lowest
			var up    = (textureV.y < textureV.x) ? vDirection : -vDirection;
			var right = cross(up, n);

			float2 uvScale, uvOffset;
			if (dot(right, uDirection) >= 0) { uvScale.x = (float)(textureU.y - textureU.x); uvOffset.x = (float)textureU.x; }
			else                             { uvScale.x = (float)(textureU.x - textureU.y); uvOffset.x = (float)textureU.y; }
			// v_unity = 1 - v_source
			if (dot(up, vDirection) >= 0)    { uvScale.y = (float)(textureV.x - textureV.y); uvOffset.y = (float)(1 - textureV.x); }
			else                             { uvScale.y = (float)(textureV.y - textureV.x); uvOffset.y = (float)(1 - textureV.y); }

			var parallelogramError = length(corner2 - (corner1 + corner3 - corner0));
			var skew = abs(dot(normalizesafe(edgeV), uDirection));
			frame = new Frame
			{
				center      = origin + InBasis((corner0 + corner1 + corner2 + corner3) * 0.25, u, v),
				right       = right,
				up          = up,
				normal      = n,
				size        = new double2(width, height),
				uvScale     = uvScale,
				uvOffset    = uvOffset,
				isRectangle = parallelogramError <= 1e-3 * max(width, height) && skew <= 1e-3
			};
			return true;
		}

		/// <summary>
		/// The engine's texture basis for a decal on a surface (R_DecalComputeBasis without a given s axis): on floors
		/// and ceilings s runs along world X, on walls t runs down world Z. The image's s runs along s and its t down
		/// along t.
		/// </summary>
		public static void ComputeBasis(double3 surfaceNormal, out double3 s, out double3 t)
		{
			var n = surfaceNormal;
			if (abs(n.z) > kSin45)
			{
				s = new double3(1, 0, 0);
				t = cross(s, n);
				s = cross(n, t);
			} else
			{
				t = new double3(0, 0, -1);
				s = cross(n, t);
				t = cross(s, n);
			}
			s = normalizesafe(s);
			t = normalizesafe(t);
		}

		/// <summary>An infodecal's image on surfaces with the given normal, centred on the decal's position.</summary>
		/// <param name="size">The material's size: its base texture's size times $decalscale.</param>
		public static Frame GetPointDecalFrame(double3 position, double3 surfaceNormal, double2 size)
		{
			var n = normalizesafe(surfaceNormal);
			ComputeBasis(n, out _, out var t);
			var up = -t;
			return new Frame
			{
				center      = position,
				right       = cross(up, n),
				up          = up,
				normal      = n,
				size        = size,
				uvScale     = new float2(1, 1),
				uvOffset    = new float2(0, 0),
				isRectangle = true
			};
		}

		/// <summary>
		/// How far along the normal to put the decal's box center, and how deep to make the box, so that it reaches
		/// every face point from minDistance to maxDistance (signed distances along the normal from the frame's
		/// center) and every face counts as facing it. A surface takes a Chisel decal only when the box center is on
		/// its front side, so frontDistance is the least distance the center needs for that (at least maxDistance).
		/// </summary>
		public static void GetDepth(double minDistance, double frontDistance, out double centerDistance, out double depth)
		{
			centerDistance = frontDistance + kDepthMargin;
			depth = 2 * (centerDistance - minDistance + kDepthMargin);
		}

		/// <summary>
		/// The least distance along the frame's normal at which a point is on the front side of a face's plane, or
		/// negative infinity when the face turns away from the normal (no distance helps).
		/// </summary>
		public static double FrontDistance(in Frame frame, double3 faceNormal, double3 pointOnFace)
		{
			var along = dot(faceNormal, frame.normal);
			if (along <= 1e-3)
				return double.NegativeInfinity;
			return dot(faceNormal, pointOnFace - frame.center) / along;
		}

		/// <summary>Source axes and units to Unity's: x, z, y, in metres.</summary>
		public static double3 ToUnityPosition(double3 source)
		{
			return new double3(source.x, source.z, source.y) * SourceEngineUnits.VmfInvMeters;
		}

		public static double3 ToUnityDirection(double3 source)
		{
			return new double3(source.x, source.z, source.y);
		}

		public static double3 FromUnityPosition(double3 unity)
		{
			return new double3(unity.x, unity.z, unity.y) / SourceEngineUnits.VmfInvMeters;
		}

		/// <summary>
		/// The Chisel decal for a frame: its GameObject's position and rotation, in Unity space, and its settings.
		/// The decal looks along -normal with the image's up as its up, so its local +x is the frame's right.
		/// </summary>
		public static void ToChisel(in Frame frame, double centerDistance, double depth, int order,
									out float3 position, out quaternion rotation, out ChiselDecalSettings settings)
		{
			position = (float3)ToUnityPosition(frame.center);
			var forward = normalize((float3)ToUnityDirection(-frame.normal));
			var up      = normalize((float3)ToUnityDirection(frame.up));
			rotation = Unity.Mathematics.quaternion.LookRotation(forward, up);

			settings = ChiselDecalSettings.Default;
			settings.size        = (float3)(new double3(frame.size, depth) * SourceEngineUnits.VmfInvMeters);
			// Local +z points into the surfaces, so a point centerDistance in front of the image is at -centerDistance
			settings.center      = new float3(0, 0, (float)(-centerDistance * SourceEngineUnits.VmfInvMeters));
			settings.projection  = ChiselDecalProjection.Orthographic;
			settings.transparent = true;
			settings.order       = order;
			settings.uvScale     = frame.uvScale;
			settings.uvOffset    = frame.uvOffset;
		}
	}
}
