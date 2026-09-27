using System;
using System.Collections.Generic;
using Chisel.Import.Source.VPKTools;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace AeternumGames.Chisel.Import.Source.ValveMapFormat2006
{
	/// <summary>
	/// A map's env_cubemap entities as Unity reflection probes, baked by Unity like any other (Generate Lighting, or Bake
	/// Reflection Probes), and the volume that hands each Source surface the probe nearest to it
	/// (<see cref="SourceReflectionProbes"/>).
	/// <para>
	/// Source gives brush sides the env_cubemap nearest to them when the map compiles - unless an env_cubemap's "sides" key
	/// claims them, which a lookup per pixel can't follow - and models the one nearest to them as they draw.
	/// </para>
	/// </summary>
	public static class VmfReflectionProbes
	{
		/// <summary>The texels of a cubemap that asks for no size: the game's default</summary>
		const int kDefaultSize = 32;
		const int kMinResolution = 16;
		const int kMaxResolution = 2048;

		/// <summary>The most cells the nearest-probe volume has, which keeps it within a few megabytes</summary>
		const int kMaxCells = 2000000;

		/// <summary>How far the volume reaches past the map's brushes, in world units: displacements rise above the brushes they are on</summary>
		const float kVolumePadding = 4.0f;

		/// <summary>The texels an env_cubemap asks for: "cubemapsize" 0 is the default, n is 2^(n-1)</summary>
		public static int CubemapResolution(int cubemapSize)
		{
			return (cubemapSize <= 0) ? kDefaultSize : 1 << Mathf.Min(cubemapSize - 1, 11);
		}

		/// <summary>
		/// The resolution all of a map's probes are baked at: the shaders take them as one array, whose cubemaps share a size,
		/// so it is the largest any env_cubemap asks for, within what Unity bakes.
		/// </summary>
		public static int ProbeResolution(IEnumerable<int> cubemapSizes)
		{
			var resolution = 0;
			foreach (var size in cubemapSizes)
				resolution = Mathf.Max(resolution, CubemapResolution(size));
			return Mathf.Clamp(resolution == 0 ? kDefaultSize : resolution, kMinResolution, kMaxResolution);
		}

		/// <summary>The size of the volume's cells: as small as half a unit, and large enough to keep the cells within kMaxCells</summary>
		public static float CellSize(Vector3 extent)
		{
			var volume = Mathf.Max(extent.x, 1e-3f) * Mathf.Max(extent.y, 1e-3f) * Mathf.Max(extent.z, 1e-3f);
			return Mathf.Max(0.5f, Mathf.Pow(volume / kMaxCells, 1.0f / 3.0f));
		}

		/// <summary>For each cell of the volume, x first, then y, then z, the index of the probe nearest to the cell's centre</summary>
		public static ushort[] NearestProbes(IReadOnlyList<Vector3> probes, Vector3 min, Vector3Int cells, float cellSize)
		{
			// Sorted along x, a search can stop once the x distance alone is further than the nearest found
			var order = new int[probes.Count];
			for (int i = 0; i < order.Length; i++)
				order[i] = i;
			Array.Sort(order, (a, b) => probes[a].x.CompareTo(probes[b].x));
			var xs = new float[order.Length];
			for (int i = 0; i < order.Length; i++)
				xs[i] = probes[order[i]].x;

			var result = new ushort[cells.x * cells.y * cells.z];
			if (order.Length == 0)
				return result;
			var cell = 0;
			for (int z = 0; z < cells.z; z++)
			{
				for (int y = 0; y < cells.y; y++)
				{
					for (int x = 0; x < cells.x; x++, cell++)
					{
						var point = min + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * cellSize;
						var start = Array.BinarySearch(xs, point.x);
						if (start < 0)
							start = Mathf.Min(~start, xs.Length - 1);
						var best = order[start];
						var bestDistance = (probes[best] - point).sqrMagnitude;
						for (int i = start + 1; i < xs.Length; i++)
						{
							var dx = xs[i] - point.x;
							if (dx * dx > bestDistance)
								break;
							var distance = (probes[order[i]] - point).sqrMagnitude;
							if (distance < bestDistance || (distance == bestDistance && order[i] < best))
							{
								bestDistance = distance;
								best = order[i];
							}
						}
						for (int i = start - 1; i >= 0; i--)
						{
							var dx = point.x - xs[i];
							if (dx * dx > bestDistance)
								break;
							var distance = (probes[order[i]] - point).sqrMagnitude;
							if (distance < bestDistance || (distance == bestDistance && order[i] < best))
							{
								bestDistance = distance;
								best = order[i];
							}
						}
						result[cell] = (ushort)best;
					}
				}
			}
			return result;
		}

		/// <summary>The space the map's brushes take up, in Unity's world</summary>
		static Bounds MapBounds(VmfWorld world, IEnumerable<Vector3> alsoInside)
		{
			var bounds = new Bounds();
			var first = true;
			void Add(Vector3 point)
			{
				if (first)
					bounds = new Bounds(point, Vector3.zero);
				else
					bounds.Encapsulate(point);
				first = false;
			}
			void AddSolids(List<VmfSolid> solids)
			{
				foreach (var solid in solids)
				{
					foreach (var side in solid.Sides)
					{
						var plane = side.Plane;
						if (plane == null)
							continue;
						Add(VmfWorldConverter.Swizzle(plane.P1) * SourceEngineUnits.VmfInvMeters);
						Add(VmfWorldConverter.Swizzle(plane.P2) * SourceEngineUnits.VmfInvMeters);
						Add(VmfWorldConverter.Swizzle(plane.P3) * SourceEngineUnits.VmfInvMeters);
					}
				}
			}
			AddSolids(world.Solids);
			foreach (var entity in world.Entities)
				AddSolids(entity.Solids);
			foreach (var point in alsoInside)
				Add(point);
			bounds.Expand(2 * kVolumePadding);
			return bounds;
		}

		/// <summary>
		/// Places a reflection probe at every env_cubemap, under a "Reflection Probes" object with the
		/// <see cref="SourceReflectionProbes"/> that hands them to the shaders, and writes the nearest-probe volume beside the
		/// map's other assets. The probes are baked later (<see cref="Bake"/>), once the level they reflect is built. Returns
		/// null for a map without env_cubemaps.
		/// </summary>
		public static SourceReflectionProbes Import(Transform parent, VmfWorld world, string mapName)
		{
			// Imports from before Unity baked these kept each probe's cubemap in an EXR of its own, which nothing uses now
			var customBakes = PackagePath.GetAssetPath($"maps/{mapName}/reflection_probes").Replace(System.IO.Path.DirectorySeparatorChar, '/');
			if (AssetDatabase.IsValidFolder(customBakes))
				AssetDatabase.DeleteAsset(customBakes);

			var cubemaps = world.Entities.FindAll(entity => entity.ClassName == "env_cubemap");
			if (cubemaps.Count == 0)
				return null;

			var sizes = new List<int>();
			foreach (var entity in cubemaps)
			{
				if (!entity.IsHidden)
					sizes.Add(entity.GetInteger("cubemapsize"));
			}
			var resolution = ProbeResolution(sizes);

			var group = new GameObject("Reflection Probes");
			group.transform.SetParent(parent, false);
			var probes = new List<ReflectionProbe>();
			var positions = new List<Vector3>();
			foreach (var entity in cubemaps)
			{
				var probeObject = new GameObject($"Cubemap {entity.Id}");
				probeObject.SetActive(false);
				probeObject.transform.SetParent(group.transform, false);
				probeObject.transform.position = VmfWorldConverter.Swizzle(VmfRope.OriginOf(entity)) * SourceEngineUnits.VmfInvMeters;
				var probe = probeObject.AddComponent<ReflectionProbe>();
				// Baked with the scene's lighting, so a bake in the Lighting window updates it like any other probe
				probe.mode = ReflectionProbeMode.Baked;
				probe.resolution = resolution;
				probe.hdr = true;
				// Source's cubemaps reflect as if everything were infinitely far away
				probe.boxProjection = false;
				// Hidden in Hammer means the map compiler leaves it out: imported switched off, and nothing reflects it
				probeObject.SetActive(!entity.IsHidden);
				if (entity.IsHidden)
					continue;
				probes.Add(probe);
				positions.Add(probeObject.transform.position);
			}
			// Every env_cubemap hidden: the compiler leaves
			// them all out, so the map reflects as one without env_cubemaps. Their probes stay imported, switched off; a
			// nearest-probe volume with nothing to point at handed every cell probe 0 of none and threw.
			if (probes.Count == 0)
				return null;
			if (probes.Count > SourceReflectionProbes.kMaxProbes)
				Debug.LogWarning($"{mapName}: {probes.Count} env_cubemaps, of which the Source shaders take {SourceReflectionProbes.kMaxProbes}");

			var bounds = MapBounds(world, positions);
			var cellSize = CellSize(bounds.size);
			var cells = new Vector3Int(Mathf.Max(1, Mathf.CeilToInt(bounds.size.x / cellSize)),
									   Mathf.Max(1, Mathf.CeilToInt(bounds.size.y / cellSize)),
									   Mathf.Max(1, Mathf.CeilToInt(bounds.size.z / cellSize)));
			var nearest = NearestProbes(positions, bounds.min, cells, cellSize);

			// Unity's own shaders take a probe by its box, so each gets the box around the cells it is nearest to
			var boxes = new Bounds[positions.Count];
			var seen = new bool[positions.Count];
			var cell = 0;
			for (int z = 0; z < cells.z; z++)
				for (int y = 0; y < cells.y; y++)
					for (int x = 0; x < cells.x; x++, cell++)
					{
						var owner = nearest[cell];
						var centre = bounds.min + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * cellSize;
						if (!seen[owner])
							boxes[owner] = new Bounds(centre, Vector3.one * cellSize);
						else
							boxes[owner].Encapsulate(new Bounds(centre, Vector3.one * cellSize));
						seen[owner] = true;
					}
			for (int i = 0; i < probes.Count; i++)
			{
				if (!seen[i])
					continue;
				probes[i].center = boxes[i].center - positions[i];
				probes[i].size = boxes[i].size;
			}

			var component = group.AddComponent<SourceReflectionProbes>();
			component.probes = probes.ToArray();
			component.index = SaveIndex(nearest, cells, mapName);
			component.volumeMin = bounds.min;
			component.cellSize = cellSize;
			return component;
		}

		/// <summary>The project path of a file under Assets, with the forward slashes the asset database uses</summary>
		static string AssetPathOf(string fullPath)
		{
			return PackagePath.GetAssetPath(fullPath).Replace(System.IO.Path.DirectorySeparatorChar, '/');
		}

		/// <summary>
		/// The index's file name, which is also the name the texture itself has to carry.
		/// </summary>
		/// <remarks>
		/// Unity requires an asset's main object to be named after its file: CreateAsset renames it silently on the
		/// first import, so a descriptive name here was never the one on disk anyway. On every LATER import it was
		/// worse than cosmetic - CopySerialized copies the name along with the pixels, which renamed the saved asset
		/// out of agreement with its own file and made SaveAssetIfDirty log "Main Object Name ... does not match
		/// filename" on every reimport of every map.
		/// </remarks>
		const string kIndexAssetName = "reflection_probe_index";

		static Texture3D SaveIndex(ushort[] nearest, Vector3Int cells, string mapName)
		{
			var index = new Texture3D(cells.x, cells.y, cells.z, GraphicsFormat.R16_UNorm, TextureCreationFlags.None)
			{
				name       = kIndexAssetName,
				filterMode = FilterMode.Point,
				wrapMode   = TextureWrapMode.Clamp
			};
			index.SetPixelData(nearest, 0);
			index.Apply(false);

			var destinationPath = PackagePath.GetOutputPath($"maps/{mapName}/{kIndexAssetName}.asset");
			var existing = UnityAssets.Load<Texture3D>(destinationPath);
			if (existing == null)
			{
				UnityAssets.Save(index, destinationPath);
				return AssetDatabase.LoadAssetAtPath<Texture3D>(AssetPathOf(destinationPath));
			}
			// Over the one from an earlier import, which keeps its GUID and every scene's reference to it
			EditorUtility.CopySerialized(index, existing);
			UnityEngine.Object.DestroyImmediate(index);
			EditorUtility.SetDirty(existing);
			AssetDatabase.SaveAssetIfDirty(existing);
			return existing;
		}

		/// <summary>
		/// Bakes the scene's lighting from the level as it now is, as the Lighting window's Generate Lighting does, which bakes
		/// the reflection probes with it, and hands them to the shaders. Unity keeps them with the scene's lighting data, so
		/// the scene has to be saved; an unsaved one gets them from Generate Lighting once it is. While they bake the Source
		/// surfaces reflect nothing, so no probe bakes in another's reflection from an earlier import.
		/// </summary>
		public static void Bake(SourceReflectionProbes component, string mapName)
		{
			if (component == null)
				return;
			if (string.IsNullOrEmpty(component.gameObject.scene.path))
			{
				Debug.Log($"{mapName}: the reflection probes are baked with the scene's lighting: save the scene, then Generate Lighting");
				return;
			}
			component.Suspend();
			if (!Lightmapping.Bake())
				Debug.LogWarning($"{mapName}: the scene's lighting did not bake, so its reflection probes aren't baked either");
			component.Apply();
		}
	}
}
