using System;
using System.Collections.Generic;
using UnityEngine;
using Chisel.Import.Source.VPKTools;

namespace AeternumGames.Chisel.Import.Source.ValveMapFormat2006
{
	/// <summary>
	/// func_dustmotes and func_dustcloud: particles that appear at random inside the entity's brushes, drift, and fade in and
	/// out, as the SDK's client spawns and draws them (C_Func_Dust in c_func_dust.cpp) from the keys its server reads
	/// (CFunc_Dust in func_dust.cpp). A Unity ParticleSystem runs them, and the "Chisel/Import/DustMotes" shader does what the
	/// client does per camera as it draws them: the alpha over a particle's life, the fade out to DistMax, and for
	/// func_dustmotes a size that grows with the distance, so a mote stays as large on screen.
	/// </summary>
	/// <remarks>
	/// The client eases a particle's horizontal speed toward env_wind's wind. env_wind isn't imported, so there is no wind,
	/// and the particles only rise or fall.
	/// </remarks>
	public static class VmfDustMotes
	{
		[Flags]
		public enum DustFlags
		{
			None       = 0,
			On         = 1,  // DUSTFLAGS_ON: spawning, unless StartDisabled
			ScaleMotes = 2,  // DUSTFLAGS_SCALEMOTES: func_dustmotes, sized on screen
			Frozen     = 4   // DUSTFLAGS_FROZEN: SpawnRate particles at once, which never move or fade
		}

		/// <summary>The entity's keys as the client receives them</summary>
		public struct Settings
		{
			/// <summary>Color, with Alpha as its alpha</summary>
			public Color32   Color;
			/// <summary>Particles a second, or all there are when frozen</summary>
			public int       SpawnRate;
			public float     SizeMin;
			public float     SizeMax;
			public int       SpeedMax;
			public int       LifetimeMin;
			public int       LifetimeMax;
			public int       DistMax;
			public float     FallSpeed;
			public DustFlags Flags;

			public bool Frozen     => (Flags & DustFlags.Frozen) != 0;
			public bool ScaleMotes => (Flags & DustFlags.ScaleMotes) != 0;
		}

		/// <summary>A side of a brush: inside is where dot(Normal, x) &lt;= Distance, in Source's coordinates</summary>
		public struct HalfSpace
		{
			public Vector3 Normal;
			public float   Distance;
		}

		/// <summary>How fast the client eases a particle's horizontal speed toward the wind, in units per second squared (DUST_ACCEL)</summary>
		public const float kDustAccel = 50;

		/// <summary>How many random points a spawn attempt of the client tries before it gives the particle up</summary>
		public const int kSpawnTests = 10;

		/// <summary>
		/// The keys as the server reads them (CFunc_Dust's datadesc and KeyValue) and sends them: its network table carries
		/// SpawnRate and SpeedMax in 12 bits, DistMax in 16 and the lifetimes in 4, so a LifetimeMax of 20 reaches the client
		/// as 4.
		/// </summary>
		public static Settings Read(VmfEntity entity)
		{
			var flags = DustFlags.On;
			if (StartsWithOne(entity.GetText("StartDisabled")))
				flags &= ~DustFlags.On;
			if (StartsWithOne(entity.GetText("Frozen")))
				flags |= DustFlags.Frozen;
			if (string.Equals(entity.ClassName, "func_dustmotes", StringComparison.OrdinalIgnoreCase))
				flags |= DustFlags.ScaleMotes;

			// A color32 keyfield is read with sscanf("%d %d %d %d"), and Spawn puts Alpha in place of the fourth
			var color = ScanIntegers(entity.GetText("Color"), 3);
			return new Settings
			{
				Color       = new Color32((byte)color[0], (byte)color[1], (byte)color[2], (byte)entity.GetInteger("Alpha")),
				SpawnRate   = entity.GetInteger("SpawnRate") & 0xFFF,
				SizeMin     = entity.GetFloat("SizeMin"),
				SizeMax     = entity.GetFloat("SizeMax"),
				SpeedMax    = entity.GetInteger("SpeedMax") & 0xFFF,
				LifetimeMin = entity.GetInteger("LifetimeMin") & 0xF,
				LifetimeMax = entity.GetInteger("LifetimeMax") & 0xF,
				DistMax     = entity.GetInteger("DistMax") & 0xFFFF,
				FallSpeed   = entity.GetFloat("FallSpeed"),
				Flags       = flags
			};
		}

		static bool StartsWithOne(string text) => !string.IsNullOrEmpty(text) && text[0] == '1';

		/// <summary>sscanf with "%d %d ...": whole numbers until one fails to read, and 0 for those after it</summary>
		public static int[] ScanIntegers(string text, int count)
		{
			var values = new int[count];
			if (string.IsNullOrEmpty(text))
				return values;
			var i = 0;
			for (int n = 0; n < count; n++)
			{
				while (i < text.Length && char.IsWhiteSpace(text[i]))
					i++;
				var negative = i < text.Length && text[i] == '-';
				if (i < text.Length && (text[i] == '-' || text[i] == '+'))
					i++;
				if (i >= text.Length || text[i] < '0' || text[i] > '9')
					break;
				long value = 0;
				for (; i < text.Length && text[i] >= '0' && text[i] <= '9'; i++)
					value = Math.Min(value * 10 + (text[i] - '0'), (long)int.MaxValue + 1);
				values[n] = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, negative ? -value : value));
			}
			return values;
		}

		/// <summary>A brush's sides as half-spaces; null for a brush with a side that isn't a plane</summary>
		public static HalfSpace[] HalfSpaces(VmfSolid solid)
		{
			var result = new HalfSpace[solid.Sides.Count];
			for (int i = 0; i < result.Length; i++)
			{
				var plane = solid.Sides[i].Plane;
				if (plane == null)
					return null;
				var p1 = new Vector3(plane.P1.X, plane.P1.Y, plane.P1.Z);
				var p2 = new Vector3(plane.P2.X, plane.P2.Y, plane.P2.Z);
				var p3 = new Vector3(plane.P3.X, plane.P3.Y, plane.P3.Z);
				var normal = Vector3.Cross(p3 - p1, p2 - p1);
				if (normal.sqrMagnitude < 1e-12f)
					return null;
				normal.Normalize();
				result[i] = new HalfSpace { Normal = normal, Distance = Vector3.Dot(normal, p1) };
			}
			return result;
		}

		static bool Inside(HalfSpace[] brush, Vector3 point)
		{
			foreach (var side in brush)
			{
				if (Vector3.Dot(side.Normal, point) > side.Distance)
					return false;
			}
			return true;
		}

		static bool InsideAny(IReadOnlyList<HalfSpace[]> brushes, Vector3 point)
		{
			foreach (var brush in brushes)
			{
				if (Inside(brush, point))
					return true;
			}
			return false;
		}

		/// <summary>
		/// The bounds of the brushes' corners, which is what the brush model's bounds are: every point where three sides meet
		/// that is inside the brush. Returns false when there is no volume.
		/// </summary>
		public static bool Bounds(IReadOnlyList<HalfSpace[]> brushes, out Vector3 min, out Vector3 max)
		{
			min = Vector3.positiveInfinity;
			max = Vector3.negativeInfinity;
			var found = false;
			foreach (var brush in brushes)
			{
				for (int a = 0; a < brush.Length; a++)
				for (int b = a + 1; b < brush.Length; b++)
				for (int c = b + 1; c < brush.Length; c++)
				{
					if (!Intersect(brush[a], brush[b], brush[c], out var corner))
						continue;
					var inside = true;
					foreach (var side in brush)
					{
						// Corners are computed, so they are inside to within their rounding
						if (Vector3.Dot(side.Normal, corner) - side.Distance > 0.01f)
						{
							inside = false;
							break;
						}
					}
					if (!inside)
						continue;
					min = Vector3.Min(min, corner);
					max = Vector3.Max(max, corner);
					found = true;
				}
			}
			return found && min.x < max.x && min.y < max.y && min.z < max.z;
		}

		static bool Intersect(HalfSpace a, HalfSpace b, HalfSpace c, out Vector3 point)
		{
			var bc = Vector3.Cross(b.Normal, c.Normal);
			var determinant = Vector3.Dot(a.Normal, bc);
			if (Mathf.Abs(determinant) < 1e-6f)
			{
				point = default;
				return false;
			}
			point = (a.Distance * bc + b.Distance * Vector3.Cross(c.Normal, a.Normal) + c.Distance * Vector3.Cross(a.Normal, b.Normal)) / determinant;
			return true;
		}

		/// <summary>
		/// The client's spawn attempt (AttemptSpawnNewParticle): up to ten random points in the bounds, the first one inside a
		/// brush. False when all ten miss, and the client makes no particle.
		/// </summary>
		public static bool AttemptSpawn(IReadOnlyList<HalfSpace[]> brushes, Vector3 min, Vector3 max, System.Random random, out Vector3 point)
		{
			for (int test = 0; test < kSpawnTests; test++)
			{
				point = new Vector3(Mathf.Lerp(min.x, max.x, (float)random.NextDouble()),
									Mathf.Lerp(min.y, max.y, (float)random.NextDouble()),
									Mathf.Lerp(min.z, max.z, (float)random.NextDouble()));
				if (InsideAny(brushes, point))
					return true;
			}
			point = default;
			return false;
		}

		/// <summary>
		/// Where the particles appear, in Source's coordinates, drawn up front with a fixed seed: the ParticleSystem emits from
		/// these points. A frozen entity makes its particles once, SpawnRate attempts, and these are exactly them. Otherwise
		/// they are a pool the particles pick from at random, and <paramref name="spawnChance"/> is how often an attempt found
		/// a point, which scales the rate. Each pool point is also moved on by the horizontal push a particle starts with: a
		/// speed of up to SpeedMax along each axis, which the client eases to a stop at DUST_ACCEL (there is no wind), within a
		/// third of a second at most, while the particle has hardly faded in.
		/// </summary>
		public static List<Vector3> SpawnPoints(IReadOnlyList<HalfSpace[]> brushes, Vector3 min, Vector3 max, Settings settings, int seed,
												out float spawnChance)
		{
			var random = new System.Random(seed);
			var points = new List<Vector3>();
			if (settings.Frozen)
			{
				for (int i = 0; i < settings.SpawnRate; i++)
				{
					if (AttemptSpawn(brushes, min, max, random, out var point))
						points.Add(point);
				}
				spawnChance = settings.SpawnRate > 0 ? points.Count / (float)settings.SpawnRate : 0;
				return points;
			}

			// Enough that two living particles hardly ever share a point
			var alive = Mathf.Max(1, settings.SpawnRate) * Mathf.Max(1, settings.LifetimeMax);
			var wanted = Mathf.Clamp(alive * 8, 256, 4096);
			var attempts = 0;
			while (points.Count < wanted && attempts < wanted * 8)
			{
				attempts++;
				if (!AttemptSpawn(brushes, min, max, random, out var point))
					continue;
				point.x += Push(settings.SpeedMax, random);
				point.y += Push(settings.SpeedMax, random);
				points.Add(point);
			}
			spawnChance = attempts > 0 ? points.Count / (float)attempts : 0;
			return points;
		}

		/// <summary>How far a particle travels along a horizontal axis before the client's easing stops it: v|v| / 2a</summary>
		static float Push(int speedMax, System.Random random)
		{
			var speed = ((float)random.NextDouble() * 2 - 1) * speedMax;
			return speed * Mathf.Abs(speed) / (2 * kDustAccel);
		}

		/// <summary>
		/// The spawn points (see SpawnPoints) as a mesh asset for the ParticleSystem's shape (effects/dust/&lt;hash&gt;.asset),
		/// shared by every entity with the same brushes and the same settings they depend on, which also seed them.
		/// </summary>
		public static Mesh GetSpawnMesh(IReadOnlyList<HalfSpace[]> brushes, Vector3 min, Vector3 max, Settings settings, Vector3 origin,
										out float spawnChance)
		{
			var key = new System.Text.StringBuilder();
			key.Append(FormattableString.Invariant($"{settings.SpawnRate} {settings.LifetimeMax} {settings.SpeedMax} {settings.Frozen}"));
			foreach (var brush in brushes)
			{
				key.Append('|');
				foreach (var side in brush)
					key.Append(FormattableString.Invariant($"{side.Normal.x:R} {side.Normal.y:R} {side.Normal.z:R} {side.Distance:R};"));
			}
			var name = VmfEffectAssets.ContentName(key.ToString());

			var points = SpawnPoints(brushes, min, max, settings, Convert.ToInt32(name.Substring(0, 8), 16), out spawnChance);
			if (points.Count == 0)
				return null;
			return VmfEffectAssets.GetMesh($"dust/{name}", () => BuildSpawnMesh(points, origin));
		}

		/// <summary>
		/// The spawn points around <paramref name="origin"/> (Unity's coordinates) as a mesh for the ParticleSystem's shape,
		/// which takes triangles only: each point is a triangle of its own, and in Vertex mode the particles pick among the
		/// vertices.
		/// </summary>
		public static Mesh BuildSpawnMesh(IReadOnlyList<Vector3> points, Vector3 origin)
		{
			var vertices = new Vector3[points.Count];
			var indices = new int[points.Count * 3];
			for (int i = 0; i < vertices.Length; i++)
			{
				vertices[i] = SourceEngineUnits.Swizzle(points[i]) * SourceEngineUnits.VmfInvMeters - origin;
				indices[i * 3] = indices[i * 3 + 1] = indices[i * 3 + 2] = i;
			}
			var mesh = new Mesh { name = "Dust Spawn Points" };
			if (vertices.Length > 65535)
				mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
			mesh.SetVertices(vertices);
			mesh.SetIndices(indices, MeshTopology.Triangles, 0);
			mesh.RecalculateBounds();
			return mesh;
		}

		/// <summary>The vertex data the DustMotes shader reads, in the order it packs them (see its Attributes)</summary>
		public static readonly List<ParticleSystemVertexStream> kVertexStreams = new()
		{
			ParticleSystemVertexStream.Position,
			ParticleSystemVertexStream.Color,
			ParticleSystemVertexStream.UV,
			ParticleSystemVertexStream.SizeX,
			ParticleSystemVertexStream.AgePercent,
			ParticleSystemVertexStream.Custom1XYZW,
			ParticleSystemVertexStream.Center
		};

		/// <summary>
		/// Sets the ParticleSystem up as the client runs the dust: emitting from the spawn points, SpawnRate a second (times the
		/// chance an attempt finds a point) or all at once when frozen, each particle living LifetimeMin to LifetimeMax seconds
		/// and rising or falling at a speed of up to SpeedMax, less FallSpeed. The rest is the shader's, which gets DistMax
		/// (meters), the frozen flag and the scale-motes flag in Custom1.
		/// </summary>
		public static void Configure(ParticleSystem system, Settings settings, Mesh spawnMesh, float spawnChance, Material material)
		{
			var toMeters = SourceEngineUnits.VmfInvMeters;
			var count = spawnMesh != null ? spawnMesh.vertexCount : 0;

			var main = system.main;
			// Frozen particles are made when the entity is, whether it's on or not
			main.playOnAwake = settings.Frozen || (settings.Flags & DustFlags.On) != 0;
			main.loop = !settings.Frozen;
			main.duration = 1;
			main.simulationSpace = ParticleSystemSimulationSpace.World;
			main.startSpeed = 0;
			main.gravityModifier = 0;
			main.startRotation = 0;
			main.startColor = (Color)settings.Color;
			main.startLifetime = settings.Frozen ? new ParticleSystem.MinMaxCurve(1.0e8f)
												 : new ParticleSystem.MinMaxCurve(settings.LifetimeMin, settings.LifetimeMax);
			// func_dustmotes' sizes are a fraction of the distance (the shader multiplies), func_dustcloud's are units
			main.startSize = settings.ScaleMotes ? new ParticleSystem.MinMaxCurve(settings.SizeMin / 10000.0f, settings.SizeMax / 10000.0f)
												 : new ParticleSystem.MinMaxCurve(settings.SizeMin * toMeters, settings.SizeMax * toMeters);
			main.maxParticles = settings.Frozen ? Mathf.Max(1, count)
												: Mathf.Max(1, Mathf.CeilToInt(settings.SpawnRate * spawnChance * (settings.LifetimeMax + 1)) + 16);

			var emission = system.emission;
			emission.enabled = true;
			if (settings.Frozen)
			{
				emission.rateOverTime = 0;
				emission.SetBursts(new[] { new ParticleSystem.Burst(0, (short)Mathf.Min(count, short.MaxValue)) });
			}
			else
			{
				emission.rateOverTime = settings.SpawnRate * spawnChance;
				emission.SetBursts(Array.Empty<ParticleSystem.Burst>());
			}

			var shape = system.shape;
			shape.enabled = count > 0;
			shape.shapeType = ParticleSystemShapeType.Mesh;
			shape.meshShapeType = ParticleSystemMeshShapeType.Vertex;
			shape.mesh = spawnMesh;
			// Frozen particles take every point once
			shape.meshSpawnMode = settings.Frozen ? ParticleSystemShapeMultiModeValue.Loop : ParticleSystemShapeMultiModeValue.Random;
			shape.randomDirectionAmount = 0;
			shape.randomPositionAmount = 0;
			shape.normalOffset = 0;

			// Up or down at a speed of up to SpeedMax, less FallSpeed: the client leaves the vertical speed as it starts
			var velocity = system.velocityOverLifetime;
			velocity.enabled = !settings.Frozen;
			velocity.space = ParticleSystemSimulationSpace.World;
			velocity.x = new ParticleSystem.MinMaxCurve(0, 0);
			velocity.y = new ParticleSystem.MinMaxCurve((-settings.SpeedMax - settings.FallSpeed) * toMeters, (settings.SpeedMax - settings.FallSpeed) * toMeters);
			velocity.z = new ParticleSystem.MinMaxCurve(0, 0);

			var custom = system.customData;
			custom.enabled = true;
			custom.SetMode(ParticleSystemCustomData.Custom1, ParticleSystemCustomDataMode.Vector);
			custom.SetVectorComponentCount(ParticleSystemCustomData.Custom1, 4);
			custom.SetVector(ParticleSystemCustomData.Custom1, 0, settings.DistMax * toMeters);
			custom.SetVector(ParticleSystemCustomData.Custom1, 1, settings.Frozen ? 1 : 0);
			custom.SetVector(ParticleSystemCustomData.Custom1, 2, settings.ScaleMotes ? 1 : 0);
			custom.SetVector(ParticleSystemCustomData.Custom1, 3, 0);

			var renderer = system.GetComponent<ParticleSystemRenderer>();
			renderer.renderMode = ParticleSystemRenderMode.Billboard;
			renderer.alignment = ParticleSystemRenderSpace.View;
			renderer.sortMode = ParticleSystemSortMode.Distance;
			renderer.sharedMaterial = material;
			renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
			renderer.receiveShadows = false;
			renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
			renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
			renderer.SetActiveVertexStreams(kVertexStreams);
		}
	}
}
