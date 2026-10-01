using System.Collections.Generic;
using Chisel.Components;
using Chisel.Core;
using Chisel.Import.Source.VPKTools;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;

namespace AeternumGames.Chisel.Import.Source.ValveMapFormat2006
{
	/// <summary>
	/// Makes the Chisel decals that draw the imported Source decals (<see cref="VmfDecal"/>), placed as Source places
	/// them (<see cref="VmfDecalPlacement"/>) and limited to the faces Source draws them on. They go in a "Decals"
	/// group under the model of the brushes they draw on.
	/// <list type="bullet">
	/// <item>info_overlay: one decal, limited to the drawn faces its <c>sides</c> key lists.</item>
	/// <item>infodecal: one decal per normal among the drawn faces whose plane is within
	/// <see cref="VmfDecalPlacement.kDecalDistance"/> and that reach the image, limited to those faces.</item>
	/// <item>info_projecteddecal: like an infodecal at the end of its ray (origin + Distance along its angles),
	/// which is where the client centres it.</item>
	/// </list>
	/// A named infodecal or projected decal is applied when it is fired, so its decals start inactive.
	/// </summary>
	public sealed class VmfChiselDecals
	{
		const string kGroupName = "Decals";
		// Faces whose normals are this close share a decal
		const double kSameNormal = 0.9999;
		// The size of a decal whose material has no texture to measure, in Source units
		const int kFallbackTextureSize = 64;

		struct Face
		{
			public ChiselBrushComponent brush;
			public int                  surfaceIndex;
			public double3              normal;      // Source axes, out of the brush
			public double3[]            points;      // Source units
			public double3              min, max;
		}

		struct DecalMaterial
		{
			public Material material;
			public double2  size;
		}

		readonly GameResources gameResources;
		readonly VmfBrushIndex brushes;
		readonly Dictionary<string, DecalMaterial> materials = new();
		readonly Dictionary<ChiselModelComponent, Transform> groups = new();
		readonly Dictionary<(int solidId, int sideIndex), Face> overlayFaces = new();
		List<Face> faces;

		int overlays, pointDecals, created, withoutFaces, withoutMaterial, notRectangles;

		public VmfChiselDecals(GameResources gameResources, VmfBrushIndex brushes)
		{
			this.gameResources = gameResources;
			this.brushes       = brushes;
		}

		public string Report()
		{
			return $"Source map import (decals): {overlays} overlays and {pointDecals} point decals made {created} Chisel decals; " +
				   $"{withoutFaces} reach no drawn face, {withoutMaterial} have no material, {notRectangles} overlays aren't rectangles " +
				   "and are drawn as the rectangle on their first edge";
		}

		/// <summary>Makes the Chisel decals for an imported decal. Returns how many it made.</summary>
		public int Create(VmfDecal data)
		{
			if (data == null || brushes == null)
				return 0;
			var before = created;
			switch (data.type)
			{
				case VmfDecal.DecalType.InfoOverlay:
					overlays++;
					CreateOverlay(data);
					break;
				case VmfDecal.DecalType.InfoDecal:
					pointDecals++;
					CreatePointDecal(data, ToDouble(data.origin));
					break;
				case VmfDecal.DecalType.InfoProjectedDecal:
				{
					pointDecals++;
					SourceEngineUnits.AngleVectors(data.angles, out var forward, out _, out _);
					CreatePointDecal(data, ToDouble(data.origin) + ToDouble(forward) * data.distance);
					break;
				}
			}
			return created - before;
		}

		/// <summary>
		/// Makes the Chisel decals for all of a map's imported decals, as <see cref="Create"/> makes them one by one, but with
		/// their materials written together first (<see cref="GameResources.PrepareMaterials"/>): one refresh between them,
		/// where each decal asking for its own took one - 58 of a map's first import, its 29 decal materials and
		/// their textures. Returns how many it made.
		/// </summary>
		public int CreateAll(IReadOnlyList<VmfDecal> decals)
		{
			if (decals == null || brushes == null)
				return 0;
			gameResources.PrepareMaterials(MaterialRequests(decals));
			var before = created;
			foreach (var data in decals)
				Create(data);
			return created - before;
		}

		/// <summary>
		/// The materials <see cref="Create"/> asks for when it makes these decals, as <see cref="GameResources.ImportVMT"/>
		/// is asked for them: a point decal's always, as it is asked for before its faces are looked for, and an overlay's
		/// only when the overlay covers a drawn face. One the import would not ask for would be an asset it never made.
		/// </summary>
		public List<(string entryName, bool isSprite, bool brushFace)> MaterialRequests(IEnumerable<VmfDecal> decals)
		{
			var requests = new List<(string entryName, bool isSprite, bool brushFace)>();
			if (decals == null || brushes == null)
				return requests;
			foreach (var data in decals)
			{
				if (data == null || string.IsNullOrEmpty(data.material))
					continue;
				switch (data.type)
				{
					case VmfDecal.DecalType.InfoOverlay:
						if (!TryGetOverlayTargets(data, out _, out var targets) || targets.Count == 0)
							continue;
						break;
					case VmfDecal.DecalType.InfoDecal:
					case VmfDecal.DecalType.InfoProjectedDecal:
						break;
					default:
						continue;
				}
				requests.Add((data.material, false, false));
			}
			return requests;
		}

		static double3 ToDouble(Vector3 vector) => new double3(vector.x, vector.y, vector.z);

		#region Overlays
		void CreateOverlay(VmfDecal data)
		{
			if (!TryGetOverlayTargets(data, out var frame, out var targets))
			{
				withoutFaces++;
				return;
			}
			if (!frame.isRectangle)
				notRectangles++;
			CreateDecals(data, frame, targets);
		}

		// Where an overlay is placed and the drawn faces it covers of those its sides key lists; false when its corners
		// make no frame
		bool TryGetOverlayTargets(VmfDecal data, out VmfDecalPlacement.Frame frame, out List<Face> targets)
		{
			frame = default;
			targets = null;
			var corners = data.corners;
			if (corners == null || corners.Length < 4 ||
				!VmfDecalPlacement.TryGetOverlayFrame(ToDouble(data.basisOrigin), ToDouble(data.basisU), ToDouble(data.basisV),
													  ToDouble(data.basisNormal),
													  new double2(corners[0].x, corners[0].y), new double2(corners[1].x, corners[1].y),
													  new double2(corners[2].x, corners[2].y), new double2(corners[3].x, corners[3].y),
													  new double2(data.textureU.x, data.textureU.y),
													  new double2(data.textureV.x, data.textureV.y), out frame))
				return false;

			targets = new List<Face>();
			foreach (var side in data.sides ?? System.Array.Empty<VmfDecal.Side>())
			{
				if (TryGetOverlayFace(side.solidId, side.sideIndex, out var face))
					targets.Add(face);
			}
			return true;
		}

		bool TryGetOverlayFace(int solidId, int sideIndex, out Face face)
		{
			if (overlayFaces.TryGetValue((solidId, sideIndex), out face))
				return face.brush != null;
			face = default;
			if (brushes.TryGetDrawnSurface(solidId, sideIndex, out var brush, out var surfaceIndex))
				TryGetFace(brush, surfaceIndex, out face);
			overlayFaces[(solidId, sideIndex)] = face;
			return face.brush != null;
		}
		#endregion

		#region Point decals
		void CreatePointDecal(VmfDecal data, double3 position)
		{
			var material = GetMaterial(data.material);
			if (material.material == null)
			{
				withoutMaterial++;
				return;
			}

			// The faces the engine would put the decal on: plane within reach, and some of it under the image
			EnsureFaces();
			var reach = length(material.size) * 0.5 + VmfDecalPlacement.kDecalDistance;
			var queryMin = position - reach;
			var queryMax = position + reach;
			var byNormal = new List<(double3 normal, List<Face> faces)>();
			foreach (var face in faces)
			{
				if (any(face.max < queryMin) || any(face.min > queryMax))
					continue;
				var planeDistance = dot(face.normal, position - face.points[0]);
				if (abs(planeDistance) > VmfDecalPlacement.kDecalDistance)
					continue;
				var faceFrame = VmfDecalPlacement.GetPointDecalFrame(position, face.normal, material.size);
				if (!UnderImage(faceFrame, face))
					continue;

				var faceNormal = face.normal;
				var index = byNormal.FindIndex(g => dot(g.normal, faceNormal) >= kSameNormal);
				if (index < 0)
				{
					byNormal.Add((faceNormal, new List<Face>()));
					index = byNormal.Count - 1;
				}
				byNormal[index].faces.Add(face);
			}

			if (byNormal.Count == 0)
			{
				withoutFaces++;
				return;
			}
			for (int g = 0; g < byNormal.Count; g++)
			{
				var frame = VmfDecalPlacement.GetPointDecalFrame(position, byNormal[g].normal, material.size);
				CreateDecals(data, frame, byNormal[g].faces, byNormal.Count > 1 ? g + 1 : 0);
			}
		}

		// Whether any of the face lies under the image, by the bounds of both across the image
		static bool UnderImage(in VmfDecalPlacement.Frame frame, in Face face)
		{
			double2 min = double.PositiveInfinity, max = double.NegativeInfinity;
			foreach (var point in face.points)
			{
				var offset = point - frame.center;
				var across = new double2(dot(offset, frame.right), dot(offset, frame.up));
				min = math.min(min, across);
				max = math.max(max, across);
			}
			var half = frame.size * 0.5;
			return all(max > -half) && all(min < half);
		}

		void EnsureFaces()
		{
			if (faces != null)
				return;
			faces = new List<Face>();
			foreach (var entry in brushes.Entries)
			{
				if (entry.brush == null)
					continue;
				for (int side = 0; side < entry.sideSurfaces.Length; side++)
				{
					var surfaceIndex = entry.sideSurfaces[side];
					if (surfaceIndex == VmfBrushIndex.kNoSurface ||
						(entry.sideDrawn != null && side < entry.sideDrawn.Length && !entry.sideDrawn[side]))
						continue;
					if (TryGetFace(entry.brush, surfaceIndex, out var face))
						faces.Add(face);
				}
			}
		}
		#endregion

		// A drawn face of an active brush, in Source space
		static bool TryGetFace(ChiselBrushComponent brush, int surfaceIndex, out Face face)
		{
			face = default;
			if (brush == null || !brush.gameObject.activeSelf)
				return false;
			var mesh = brush.definition.BrushOutline;
			if (mesh?.polygons == null || mesh.planes == null ||
				surfaceIndex < 0 || surfaceIndex >= mesh.polygons.Length || surfaceIndex >= mesh.planes.Length)
				return false;

			var transform = brush.transform;
			var polygon = mesh.polygons[surfaceIndex];
			if (polygon.edgeCount < 3)
				return false;
			var points = new double3[polygon.edgeCount];
			double3 min = double.PositiveInfinity, max = double.NegativeInfinity;
			for (int e = 0; e < polygon.edgeCount; e++)
			{
				var local = mesh.vertices[mesh.halfEdges[polygon.firstEdge + e].vertexIndex];
				var world = transform.TransformPoint(local);
				var point = VmfDecalPlacement.FromUnityPosition(new double3(world.x, world.y, world.z));
				points[e] = point;
				min = math.min(min, point);
				max = math.max(max, point);
			}
			var worldNormal = transform.TransformDirection(((float4)mesh.planes[surfaceIndex]).xyz);
			var normal = normalizesafe(VmfDecalPlacement.ToUnityDirection(new double3(worldNormal.x, worldNormal.y, worldNormal.z)));
			if (lengthsq(normal) < 0.5)
				return false;

			face = new Face
			{
				brush        = brush,
				surfaceIndex = surfaceIndex,
				normal       = normal,
				points       = points,
				min          = min,
				max          = max
			};
			return true;
		}

		// One Chisel decal per model the faces are in, reaching all of them
		void CreateDecals(VmfDecal data, in VmfDecalPlacement.Frame frame, List<Face> targets, int number = 0)
		{
			if (targets.Count == 0)
			{
				withoutFaces++;
				return;
			}
			var material = GetMaterial(data.material);
			if (material.material == null)
			{
				withoutMaterial++;
				return;
			}

			var byModel = new Dictionary<ChiselModelComponent, List<Face>>();
			foreach (var face in targets)
			{
				var model = face.brush.GetComponentInParent<ChiselModelComponent>(true);
				if (model == null)
					continue;
				if (!byModel.TryGetValue(model, out var list))
					byModel[model] = list = new List<Face>();
				list.Add(face);
			}

			foreach (var pair in byModel)
			{
				double minDistance = double.PositiveInfinity, frontDistance = double.NegativeInfinity;
				var surfaceTargets = new List<ChiselDecalSurfaceTarget>(pair.Value.Count);
				foreach (var face in pair.Value)
				{
					foreach (var point in face.points)
					{
						var distance = dot(point - frame.center, frame.normal);
						minDistance   = math.min(minDistance, distance);
						frontDistance = math.max(frontDistance, distance);
					}
					frontDistance = math.max(frontDistance, VmfDecalPlacement.FrontDistance(frame, face.normal, face.points[0]));
					surfaceTargets.Add(new ChiselDecalSurfaceTarget { brush = face.brush, surfaceIndex = face.surfaceIndex });
				}
				VmfDecalPlacement.GetDepth(minDistance, frontDistance, out var centerDistance, out var depth);
				VmfDecalPlacement.ToChisel(frame, centerDistance, depth, data.renderOrder,
										   out var position, out var rotation, out var settings);

				var name = $"{data.gameObject.name}" + (number > 0 ? $" ({number})" : "");
				var gameObject = new GameObject(name);
				gameObject.transform.SetParent(GetGroup(pair.Key), false);
				gameObject.transform.SetPositionAndRotation(position, rotation);
				// A named decal is applied when it is fired, and a hidden one is not in the game at all
				if (!data.gameObject.activeSelf ||
					(data.type != VmfDecal.DecalType.InfoOverlay && !string.IsNullOrEmpty(data.targetName)))
					gameObject.SetActive(false);

				var decal = gameObject.AddComponent<ChiselDecalComponent>();
				decal.Material = material.material;
				decal.Settings = settings;
				decal.SetTargets(surfaceTargets);
				created++;
			}
		}

		Transform GetGroup(ChiselModelComponent model)
		{
			if (groups.TryGetValue(model, out var group) && group != null)
				return group;
			group = model.transform.Find(kGroupName);
			if (group == null)
			{
				group = new GameObject(kGroupName).transform;
				group.SetParent(model.transform, false);
			}
			groups[model] = group;
			return group;
		}

		// The decal's material, and its size in Source units: the base texture's size times $decalscale
		DecalMaterial GetMaterial(string name)
		{
			name ??= "";
			if (materials.TryGetValue(name, out var result))
				return result;
			result = default;
			if (!string.IsNullOrEmpty(name))
			{
				result.material = gameResources.ImportVMT(name);
				var vmt = gameResources.LoadVMT(name);
				var vtf = (vmt != null && !string.IsNullOrEmpty(vmt.BaseTextureName)) ? gameResources.LoadVTF(vmt.BaseTextureName) : null;
				var width  = (vtf != null && vtf.Width  > 0) ? vtf.Width  : kFallbackTextureSize;
				var height = (vtf != null && vtf.Height > 0) ? vtf.Height : kFallbackTextureSize;
				var scale  = (vmt != null && vmt.DecalScale.HasValue) ? vmt.DecalScale.Value : 1.0f;
				result.size = new double2(width, height) * scale;
			}
			materials[name] = result;
			return result;
		}
	}
}
