using System;

namespace AeternumGames.Chisel.Import.Source.ValveMapFormat2006
{
	/// <summary>
	/// How bright a Source light is at a distance, as VRAD bakes it, and the Unity light that comes closest.
	/// </summary>
	/// <remarks>
	/// VRAD (utils/vrad/lightmap.cpp, SetLightFalloffParams and GatherSampleLight) lights a surface with
	/// intensity / (c + l·d + q·d²), d in units and at least 1. Without a fifty percent distance, the
	/// intensity is scaled by c + 100·l + 10000·q, so "_light" is the brightness at 100 units. With one,
	/// c, l and q come from a curve through 1 at 0, 1/2 at the fifty and 1/256 at the zero percent
	/// distance, with a fade near the end. URP's punctual lights fall off as intensity / d² (meters)
	/// inside a smooth range window, which is the same curve for the two thirds of the campaign's
	/// lights that are purely quadratic. The rest get the inverse square that is closest in log space
	/// over the distances they light.
	/// </remarks>
	public struct VmfLightFalloff
	{
		public float Constant, Linear, Quadratic;

		/// <summary>VRAD evaluates the falloff at no more than this distance (units).</summary>
		public float CapDistance;

		/// <summary>Where the fade starts and ends (units); no fade when the end is not past the start.</summary>
		public float StartFade, EndFade;

		/// <summary>"_distance" (units), 0 for none.</summary>
		/// <remarks>
		/// VRAD leaves it out of the lightmaps: it only becomes the world light's radius, past which the
		/// engine stops lighting models with it. It caps <see cref="Reach"/>, not the curve.
		/// </remarks>
		public float MaxDistance;

		/// <summary>A light's key values the falloff depends on; 0 for a key that is missing.</summary>
		public struct Keys
		{
			public float FiftyPercentDistance, ZeroPercentDistance;
			public bool HardFalloff;
			public float ConstantAttn, LinearAttn, QuadraticAttn;
			public float Distance;
		}

		public static VmfLightFalloff FromKeys(in Keys keys)
		{
			var falloff = new VmfLightFalloff { CapDistance = 1.0e22f, StartFade = 0, EndFade = -1 };
			var d50 = keys.FiftyPercentDistance;
			if (d50 != 0)
			{
				var d0 = keys.ZeroPercentDistance;
				if (d0 < d50)
					d0 = 2.0f * d50;
				float a = 0, b = 1, c = 0;
				SolveInverseQuadraticMonotonic(0, 1.0f, d50, 2.0f, d0, 256.0f, ref a, ref b, ref c);

				// Rescaled so at least the fifty percent value is right, whatever the monotonic solve did
				var v50 = c + d50 * (b + d50 * a);
				var scale = 2.0f / v50;
				falloff.Quadratic = a * scale;
				falloff.Linear    = b * scale;
				falloff.Constant  = c * scale;

				if (keys.HardFalloff)
				{
					falloff.EndFade   = d0;
					falloff.StartFade = 0.75f * d0 + 0.25f * d50;
				}
				else if (Math.Abs(falloff.Quadratic) > 0)
				{
					// Where 1/x would turn around and brighten again: hold it there and fade to 10x that
					var max = falloff.Linear / (-2.0f * falloff.Quadratic);
					if (max > 0)
					{
						falloff.CapDistance = max;
						falloff.StartFade   = max;
						falloff.EndFade     = 10.0f * max;
					}
				}
				return falloff;
			}

			falloff.Constant    = keys.ConstantAttn  < 1e-4f ? 0 : keys.ConstantAttn;	// EQUAL_EPSILON
			falloff.Linear      = keys.LinearAttn    < 1e-4f ? 0 : keys.LinearAttn;
			falloff.Quadratic   = keys.QuadraticAttn < 1e-4f ? 0 : keys.QuadraticAttn;
			falloff.MaxDistance = keys.Distance > 0 ? keys.Distance : 0;
			if (falloff.Constant == 0 && falloff.Linear == 0 && falloff.Quadratic == 0)
				falloff.Constant = 1;

			// The intensity is scaled so "_light" is the brightness at 100 units
			var ratio = falloff.Constant + 100 * falloff.Linear + 100 * 100 * falloff.Quadratic;
			if (ratio > 0)
			{
				falloff.Constant  /= ratio;
				falloff.Linear    /= ratio;
				falloff.Quadratic /= ratio;
			}
			return falloff;
		}

		/// <summary>The light's brightness at a distance (units), relative to its "_light" brightness.</summary>
		public readonly float Evaluate(float distance)
		{
			var hasFade = EndFade > StartFade;
			if (hasFade && distance > EndFade)
				return 0;

			distance = Math.Max(distance, 1);
			var evalDistance = Math.Min(distance, CapDistance);
			var value = 1.0f / (Constant + evalDistance * (Linear + evalDistance * Quadratic));
			if (hasFade)
			{
				var t = 1 - Math.Clamp((distance - StartFade) / (EndFade - StartFade), 0, 1);
				value *= t * t * t * (t * (t * 6.0f - 15.0f) + 10.0f);
			}
			return value;
		}

		/// <summary>Whether Unity's inverse square is this very curve.</summary>
		public readonly bool IsInverseSquare => Constant == 0 && Linear == 0 && Quadratic > 0 && EndFade <= StartFade;

		/// <summary>The nearest distance (units) the fit looks at: lights rarely sit closer to what they light.</summary>
		public const float NearestFitDistance = 32;

		/// <summary>
		/// The inverse square, strength / d², that is closest to this falloff in log space over the distances
		/// from <see cref="NearestFitDistance"/> to <paramref name="farthest"/> (units): strength in units².
		/// </summary>
		public readonly float FitInverseSquare(float farthest)
		{
			if (IsInverseSquare)
				return 1.0f / Quadratic;

			const int samples = 64;
			farthest = Math.Max(farthest, NearestFitDistance * 2);
			var logNear = Math.Log(NearestFitDistance);
			var logFar  = Math.Log(farthest);
			double sum = 0;
			int count = 0;
			for (int i = 0; i < samples; i++)
			{
				var distance = Math.Exp(logNear + (logFar - logNear) * (i + 0.5) / samples);
				var value = Evaluate((float)distance);
				if (!(value > 0))
					continue;
				sum += Math.Log(value * distance * distance);
				count++;
			}
			return count == 0 ? 0 : (float)Math.Exp(sum / count);
		}

		/// <summary>How far the light reaches (units) before it is dimmer than <paramref name="threshold"/>.</summary>
		/// <param name="brightness">The light's linear brightness, the one its falloff is relative to.</param>
		/// <param name="strength">Its inverse square's strength, from <see cref="FitInverseSquare"/>.</param>
		/// <param name="threshold">Linear brightness under which a light no longer shows.</param>
		public readonly float Reach(float brightness, float strength, float threshold)
		{
			var reach = (float)Math.Sqrt(Math.Max(0, brightness * strength / threshold));
			if (MaxDistance > 0)
				reach = Math.Min(reach, MaxDistance);
			if (EndFade > StartFade)
				reach = Math.Min(reach, EndFade);
			return reach;
		}

		/// <summary>
		/// The URP spot angles (full angles, degrees) that come closest to a Source spot light's cone.
		/// </summary>
		/// <remarks>
		/// ParseLightSpot: the inner cone defaults to 10 degrees and the outer one to the inner, the outer is
		/// never less than the inner, both are held to 90, and a light whose cones are both 180 is a point
		/// light. GatherSampleLight lights a direction θ off the axis with cos θ, times
		/// ((cos θ - cos outer) / (cos inner - cos outer))^exponent between the cones (an exponent of 0 is 1).
		/// URP lights its whole inner cone fully and falls off as t² between its angles, so the outer angle is
		/// kept and the inner one is placed where both lights are at half brightness at the same angle.
		/// </remarks>
		public static void SpotAngles(float innerCone, float cone, float exponent,
									  out float spotAngle, out float innerSpotAngle, out bool isPointLight)
		{
			var inner = (innerCone != 0) ? innerCone : 10;
			var outer = (cone != 0) ? cone : inner;
			if (outer < inner)
				outer = inner;
			isPointLight = inner == 180 && outer == 180;
			if (isPointLight)
			{
				spotAngle = innerSpotAngle = 0;
				return;
			}
			inner = Math.Min(inner, 90);
			outer = Math.Min(outer, 90);
			if (exponent == 0)
				exponent = 1;

			var cosInner = Math.Cos(inner * Math.PI / 180);
			var cosOuter = Math.Cos(outer * Math.PI / 180);
			double Brightness(double angle)
			{
				var cos = Math.Cos(angle);
				if (cos <= cosOuter)
					return 0;
				var fringe = (cos > cosInner) ? 1 : Math.Pow(Math.Clamp((cos - cosOuter) / (cosInner - cosOuter), 0, 1), exponent);
				return cos * fringe;
			}

			// Source's half brightness angle: its brightness falls from 1 on the axis to 0 at the outer cone
			double low = 0, high = outer * Math.PI / 180;
			for (int i = 0; i < 48; i++)
			{
				var middle = (low + high) / 2;
				if (Brightness(middle) > 0.5)
					low = middle;
				else
					high = middle;
			}
			var cosHalf = Math.Cos((low + high) / 2);

			// URP: t = (cos θ - cos outer) / (cos inner - cos outer) and t² = 1/2 at that same angle
			var cosUnityInner = cosOuter + (cosHalf - cosOuter) / Math.Sqrt(0.5);
			spotAngle = (float)Math.Min(2 * outer, 179);
			innerSpotAngle = (cosUnityInner >= 1) ? 0 : (float)Math.Min(2 * Math.Acos(cosUnityInner) * 180 / Math.PI, spotAngle);
		}

		// mathlib_base.cpp
		static bool SolveInverseQuadratic(float x1, float y1, float x2, float y2, float x3, float y3, ref float a, ref float b, ref float c)
		{
			var det = (x1 - x2) * (x1 - x3) * (x2 - x3);
			if (det == 0)
				return false;
			a = (x3 * (-y1 + y2) + x2 * (y1 - y3) + x1 * (-y2 + y3)) / det;
			b = (x3 * x3 * (y1 - y2) + x1 * x1 * (y2 - y3) + x2 * x2 * (-y1 + y3)) / det;
			c = (x1 * x3 * (-x1 + x3) * y2 + x2 * x2 * (x3 * y1 - x1 * y3) + x2 * (-(x3 * x3 * y1) + x1 * x1 * y3)) / det;
			return true;
		}

		// mathlib_base.cpp: when the curve would not be monotonic, shifts the middle point towards the straight line
		// between the ends until it is. (Its derivative test is Valve's own, at x = 1.)
		static bool SolveInverseQuadraticMonotonic(float x1, float y1, float x2, float y2, float x3, float y3, ref float a, ref float b, ref float c)
		{
			if (x1 > x2) { (x1, x2) = (x2, x1); (y1, y2) = (y2, y1); }
			if (x2 > x3) { (x2, x3) = (x3, x2); (y2, y3) = (y3, y2); }
			if (x1 > x2) { (x1, x2) = (x2, x1); (y1, y2) = (y2, y1); }
			for (float blend = 0.0f; blend <= 1.0f; blend += 0.05f)
			{
				// FLerp(y1, y3, x1, x3, x2)
				var linear = y1 + (y3 - y1) * (x2 - x1) / (x3 - x1);
				var tempY2 = (1 - blend) * y2 + blend * linear;
				if (!SolveInverseQuadratic(x1, y1, x2, tempY2, x3, y3, ref a, ref b, ref c))
					return false;
				var derivative = 2.0f * a + b;
				if (y1 < y2 && y2 < y3)
				{
					if (derivative >= 0.0f)
						return true;
				}
				else if (y1 > y2 && y2 > y3)
				{
					if (derivative <= 0.0f)
						return true;
				}
				else
					return true;
			}
			return true;
		}
	}
}
