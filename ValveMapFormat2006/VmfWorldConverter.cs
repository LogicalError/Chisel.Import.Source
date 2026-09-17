///////////////////////////////////////////////////////////////////////////////////////////////////
// MIT License
//
// Copyright(c) 2018-2020 Henry de Jongh
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
////////////////////// https://github.com/Henry00IS/ ////////////////// http://aeternumgames.com //

using Chisel.Components;
using Chisel.Core;
using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Chisel.Import.Source.VPKTools;

namespace AeternumGames.Chisel.Import.Source.ValveMapFormat2006
{
    /// <summary>
    /// Converts a Hammer Map to Chisel Brushes.
    /// </summary>
    public static class VmfWorldConverter
	{
		private struct DisplacementSide
		{
			public VmfSolidSide side;
			public ChiselSurface surface;
			public int descriptionIndex;
		}

		public static Vector3 Swizzle(Vector3 input) 
		{
			return SourceEngineUnits.Swizzle(input);
		}
        public static Vector3 Swizzle(VmfVector3 input)
		{
			var inputV = new Vector3(input.X, input.Y, input.Z);
			return SourceEngineUnits.Swizzle(inputV);
		}

		// EditorUtility.DisplayProgressBar forces an editor repaint, so calling it once per solid
		// costs considerably more than the work it is reporting on. A few updates a second is
		// indistinguishable to look at.
		const double kProgressIntervalSeconds = 0.1;
		static readonly System.Diagnostics.Stopwatch s_ProgressTimer = System.Diagnostics.Stopwatch.StartNew();
		static double s_NextProgressUpdate;

		internal static void ReportProgress(string title, string message, int index, int total)
		{
#if UNITY_EDITOR
			var now = s_ProgressTimer.Elapsed.TotalSeconds;
			if (index > 0 && index < total - 1 && now < s_NextProgressUpdate)
				return;
			s_NextProgressUpdate = now + kProgressIntervalSeconds;
			UnityEditor.EditorUtility.DisplayProgressBar(title, $"{message} ({index + 1} / {total})...", (total <= 0) ? 0f : index / (float)total);
#endif
		}

		// Reading material.mainTexture forces the texture asset to load, and the same handful of
		// materials is shared across tens of thousands of sides. The size cannot change during an
		// import, so resolve it once per material.
		const int kDefaultTextureSize = 256;
		static readonly int kMainTexPropertyID = Shader.PropertyToID("_MainTex");
		static readonly Dictionary<Material, Vector2Int> s_TextureSizes = new();

		static void GetTextureSize(Material material, out int width, out int height)
		{
			if (material == null)
			{
				width = height = kDefaultTextureSize;
				return;
			}
			if (!s_TextureSizes.TryGetValue(material, out var size))
			{
				size = new Vector2Int(kDefaultTextureSize, kDefaultTextureSize);
				if (material.HasProperty(kMainTexPropertyID) &&
					material.mainTexture != null)
					size = new Vector2Int(material.mainTexture.width, material.mainTexture.height);
				s_TextureSizes[material] = size;
			}
			width = size.x;
			height = size.y;
		}

		static decimal Sqrt(decimal x, decimal epsilon = 0.000000000000000001M)
		{
			var current = (decimal)math.sqrt((double)x);
			decimal previous;
			do
			{
				previous = current;
				if (previous == 0.0M) return 0;
				current = (previous + x / previous) / 2;
			} while (Math.Abs(previous - current) > epsilon);
			return current;
		}

		public static UnityEngine.Plane VmfPointsToUnityPlane(VmfPlane input)
		{
			//*
			var p0 = Swizzle(input.P1);
			var p1 = Swizzle(input.P2);
			var p2 = Swizzle(input.P3);

			var p0x = (decimal)p0.x;
			var p0y = (decimal)p0.y;
			var p0z = (decimal)p0.z;

			var p1x = (decimal)p1.x;
			var p1y = (decimal)p1.y;
			var p1z = (decimal)p1.z;

			var p2x = (decimal)p2.x;
			var p2y = (decimal)p2.y;
			var p2z = (decimal)p2.z;

			var ax = p0x;
			var ay = p0y;
			var az = p0z;

			var bx = p1x;
			var by = p1y;
			var bz = p1z;

			var cx = p2x;
			var cy = p2y;
			var cz = p2z;

			var abx = (bx - ax);
			var aby = (by - ay);
			var abz = (bz - az);

			var acx = (cx - ax);
			var acy = (cy - ay);
			var acz = (cz - az);

			var normalx = aby * acz - abz * acy;
			var normaly = abz * acx - abx * acz;
			var normalz = abx * acy - aby * acx;

			var sqrmagnitude	= (normalx * normalx) + (normaly * normaly) + (normalz * normalz);
			var magnitude		= Sqrt(sqrmagnitude);

			normalx /= magnitude;
			normaly /= magnitude;
			normalz /= magnitude;

			var a = normalx;
			var b = normaly;
			var c = normalz;
			var d = (float)(((normalx * p1x) + (normaly * p1y) + (normalz * p1z)) * (Decimal)SourceEngineUnits.VmfInvMeters);

			UnityEngine.Plane plane = new()
			{
				distance = d * (SourceEngineUnits.InvertPlanes ? 1 : -1),
				normal = new Vector3((float)a, (float)b, (float)c) * (SourceEngineUnits.InvertPlanes ? -1 : 1)
			};
			return plane;
		}

        /// <summary>
        /// Imports the specified world into the Chisel model.
        /// </summary>
        /// <param name="model">The model to import into.</param>
        /// <param name="world">The world to be imported.</param>
        public static void Import(GameResources gameResources, ChiselModelComponent model, VmfWorld world)
        {
            // create a material searcher to associate materials automatically.
            MaterialSearcher materialSearcher = new MaterialSearcher();
            HashSet<string> materialSearcherWarnings = new HashSet<string>();
			List<DisplacementSide> DisplacementSurfaces = new List<DisplacementSide>();

			s_TextureSizes.Clear();
			var importStopwatch = System.Diagnostics.Stopwatch.StartNew();

			// Every brush is imported. The ones that aren't world geometry (triggers, clips, hints...) go to models
			// of their own next to the world model, so they never cut it.
			var brushModels = new VmfBrushModels(model != null ? model.transform.parent : null);

			// Which brush and surface shows each side, for the decals the entity import puts on them
			var brushIndex = VmfBrushIndex.Begin(world);

			// iterate through all world solids.
			for (int i = 0; i < world.Solids.Count; i++)
            {
#if UNITY_EDITOR
                ReportProgress("Chisel: Importing Source Engine Map (1/3)", "Converting Hammer Solids To Chisel Brushes", i, world.Solids.Count);
#endif
				try
				{
					VmfSolid solid = world.Solids[i];
					var (role, roleName) = VmfBrushContents.RoleOf(null, VmfBrushContents.SideMaterialsOf(solid), gameResources.GetMaterialContents);


					// prepare for any displacements.
					DisplacementSurfaces.Clear();

					// prepare for uv calculations of clip planes after cutting.
					var planes = new float4[solid.Sides.Count];
					var planeSurfaces = new ChiselSurface[solid.Sides.Count];
					var sideDrawn = new bool[solid.Sides.Count];

					var center = Vector3.zero;
					for (int j = 0; j < solid.Sides.Count; j++)
					{
						center.x += solid.Sides[j].Plane.P1.X;
						center.y += solid.Sides[j].Plane.P1.Y;
						center.z += solid.Sides[j].Plane.P1.Z;

						center.x += solid.Sides[j].Plane.P2.X;
						center.y += solid.Sides[j].Plane.P2.Y;
						center.z += solid.Sides[j].Plane.P2.Z;

						center.x += solid.Sides[j].Plane.P3.X;
						center.y += solid.Sides[j].Plane.P3.Y;
						center.z += solid.Sides[j].Plane.P3.Z;
					}
					center /= (float)(solid.Sides.Count * 3);

					var unfixedOrigin = center;
					center = Swizzle(center) * SourceEngineUnits.VmfInvMeters;

					bool enabled = true;

					// compute all the sides of the brush that will be clipped.
					for (int j = solid.Sides.Count; j-- > 0;)
					{
						VmfSolidSide side = solid.Sides[j];

						// detect excluded polygons.
						//if (IsExcludedMaterial(side.Material))
						//polygon.UserExcludeFromFinal = true;
						// detect collision-only brushes.
						//if (IsInvisibleMaterial(side.Material))
						//pr.IsVisible = false;

						// Only a displacement blends a second layer; any other face gets the material as vbsp patches it.
						Material material = FaceMaterial(gameResources, role, side, brushFace: side.Displacement == null);
						// A displacement replaces its face, so decals can't go on the brush there
						sideDrawn[j] = side.Displacement == null && FaceKind(gameResources, role, side) == VmfFaceKind.Drawn;

						// fallback to default material.
						if (material == null)
						{
							// Unity resolves a full stack trace for every one of these, so the
							// call count here matters as much as the time: if it is in the
							// hundreds, the warning itself is the cost and wants de-duplicating
							// per missing material name.
							//if (!side.Material.StartsWith("TOOLS"))
								Debug.LogWarning($"Could not find the Material {side.Material}");
							material = ChiselDefaultMaterials.Defaults.defaultFloorMaterial;
						}

						// create chisel surface for the clip.
						ChiselSurface surface = ChiselSurface.Create(material);

						// calculate the clipping planes.
						Plane clip = VmfPointsToUnityPlane(side.Plane);
						/*
						var normal = clip.normal;
						clip.distance += (normal.x * center.x) +
										 (normal.y * center.y) +
										 (normal.z * center.z);
						*/
						planes[j] = new float4(clip.normal, clip.distance);

						// check whether this surface is a displacement.
						if (side.Displacement != null)
						{
							//surface.DestinationFlags = SurfaceDestinationFlags.None;

							// keep track of the surface used to cut the mesh.
							DisplacementSurfaces.Add(new DisplacementSide { side = side, surface = surface, descriptionIndex = j });
							enabled = false;

							//surface = ChiselSurface.Create(ChiselDefaultMaterials.ShadowOnlyMaterial);
						}
						planeSurfaces[j] = surface;
					}


					// build a very large cube brush.
					ChiselBrushComponent go = CreateBrush(gameResources, brushModels, model, null, solid, role, roleName);

					// Hidden in Hammer means the map compiler skips it too (vbsp has no handler for
					// the `hidden` block either), so it is imported but left inactive.
					if (solid.IsHidden) go.gameObject.SetActive(false);

					// TODO: should output all sides that are not displaced, but visible
					if (!enabled) go.enabled = false;
					go.surfaceArray = new ChiselSurfaceArray();
					go.surfaceArray.EnsureSize(planes.Length);

					// cut all the clipping planes out of the brush in one go.
					BrushMesh brushMesh;
					BrushMeshFactory.CreateFromPlanes(planes, new Bounds(Vector3.zero, new Vector3(8192, 8192, 8192)), ref go.surfaceArray, out brushMesh);
					if (brushMesh.polygons == null ||
						brushMesh.polygons.Length == 0)
						continue;

					go.definition.BrushOutline = brushMesh;

					var surfaceDefinitions = go.surfaceArray;
					for (int j = 0; j < brushMesh.polygons.Length; j++)
					{
						surfaceDefinitions.surfaces[j] = planeSurfaces[brushMesh.polygons[j].descriptionIndex];
				}

					for (int j = solid.Sides.Count; j-- > 0;)
					{
						VmfSolidSide side = solid.Sides[j];
						var surface = planeSurfaces[j];

						// calculate the texture coordinates.
						GetTextureSize(surface.RenderMaterial, out int w, out int h);

						var clip = new Plane(planes[j].xyz, planes[j].w);
						CalculateTextureCoordinates(go, unfixedOrigin, surface, clip, w, h, side.UAxis, side.VAxis);
					}

					// build displacements.
					foreach (DisplacementSide displacement in DisplacementSurfaces)
					{
						// find the brush mesh polygon:
						for (int polyidx = 0; polyidx < brushMesh.polygons.Length; polyidx++)
						{
							if (brushMesh.polygons[polyidx].descriptionIndex != displacement.descriptionIndex)
								continue;

							// find the polygon plane.
							Plane plane = new(brushMesh.planes[polyidx].xyz, brushMesh.planes[polyidx].w);

							// find all vertices that belong to this polygon:
							List<Vector3> vertices = new();
							{
								var polygon = brushMesh.polygons[polyidx];
								var firstEdge = polygon.firstEdge;
								var edgeCount = polygon.edgeCount;
								var lastEdge = firstEdge + edgeCount;
								for (int e = firstEdge; e < lastEdge; e++)
								{
									vertices.Add(brushMesh.vertices[brushMesh.halfEdges[e].vertexIndex]);
								}
							}

							// build displacement:
							BuildDisplacementSurface(go, displacement.side, displacement.surface, vertices, plane);
							surfaceDefinitions.surfaces[displacement.descriptionIndex] = ChiselSurface.Create(ChiselDefaultMaterials.Defaults.shadowOnlySurfacesMaterial);

							break;
						}
					}

					for (int j = 0; j < brushMesh.polygons.Length; j++)
					{
						surfaceDefinitions.surfaces[j] = planeSurfaces[brushMesh.polygons[j].descriptionIndex];
					}

					// finalize the brush by snapping planes and centering the pivot point.
					go.transform.position += brushMesh.CenterAndSnapPlanes(ref surfaceDefinitions);
					brushIndex.Add(solid, go, brushMesh, sideDrawn);

                foreach (Transform child in go.transform)
                    child.position -= go.transform.position;

				}
				catch(Exception ex)
				{
					Debug.Log("brush failed");
					Debug.LogException(ex);
				}
			}

			var solidsLoopSeconds = importStopwatch.Elapsed.TotalSeconds;

            // iterate through all entities.
            for (int e = 0; e < world.Entities.Count; e++)
            {
#if UNITY_EDITOR
                ReportProgress("Chisel: Importing Source Engine Map (2/3)", "Converting Hammer Entities To Chisel Brushes", e, world.Entities.Count);
#endif
				try
				{ 
					VmfEntity entity = world.Entities[e];

					// iterate through all entity solids.
					for (int i = 0; i < entity.Solids.Count; i++)
					{
						VmfSolid solid = entity.Solids[i];
						var (role, roleName) = VmfBrushContents.RoleOf(entity.ClassName, VmfBrushContents.SideMaterialsOf(solid), gameResources.GetMaterialContents);

						var center = Vector3.zero;
						for (int j = 0; j < solid.Sides.Count; j++)
						{
							center.x += solid.Sides[j].Plane.P1.X;
							center.y += solid.Sides[j].Plane.P1.Y;
							center.z += solid.Sides[j].Plane.P1.Z;

							center.x += solid.Sides[j].Plane.P2.X;
							center.y += solid.Sides[j].Plane.P2.Y;
							center.z += solid.Sides[j].Plane.P2.Z;

							center.x += solid.Sides[j].Plane.P3.X;
							center.y += solid.Sides[j].Plane.P3.Y;
							center.z += solid.Sides[j].Plane.P3.Z;
						}
						center /= (float)(solid.Sides.Count * 3);

						var unfixedOrigin = center;
						center = Swizzle(center) * SourceEngineUnits.VmfInvMeters;

						// prepare for uv calculations of clip planes after cutting.
						var planes = new float4[solid.Sides.Count];
						var planeSurfaces = new ChiselSurface[solid.Sides.Count];
						var sideDrawn = new bool[solid.Sides.Count];
						// clip all the sides out of the brush.
						for (int j = solid.Sides.Count; j-- > 0;)
						{
							VmfSolidSide side = solid.Sides[j];

							// detect excluded polygons.
							//if (IsExcludedMaterial(side.Material))
							//    polygon.UserExcludeFromFinal = true;
							// detect collision-only brushes.
							//if (IsInvisibleMaterial(side.Material))
							//    pr.IsVisible = false;

							// Displacements only exist on world brushes, so every face here gets the material as vbsp
							// patches it for a plain brush face.
							Material material = FaceMaterial(gameResources, role, side, brushFace: true);
							sideDrawn[j] = FaceKind(gameResources, role, side) == VmfFaceKind.Drawn;

							// fallback to default material.
							if (material == null)
							{
								//if (!side.Material.StartsWith("TOOLS"))
									Debug.LogWarning($"Could not find the Material {side.Material}");
								material = ChiselDefaultMaterials.Defaults.defaultFloorMaterial;
							}

							// create chisel surface for the clip.
							ChiselSurface surface = ChiselSurface.Create(material);

							// calculate the clipping planes.
							Plane clip = VmfPointsToUnityPlane(side.Plane);
							/*
							var normal = clip.normal;
							clip.distance += (normal.x * center.x) +
											 (normal.y * center.y) +
											 (normal.z * center.z);
							*/
							planes[j] = new float4(clip.normal, clip.distance);
							planeSurfaces[j] = surface;
						}

						ChiselBrushComponent go = CreateBrush(gameResources, brushModels, model, entity, solid, role, roleName);

						// Hidden in Hammer means the map compiler skips it too (vbsp has no handler for
						// the `hidden` block either), so it is imported but left inactive.
						if (solid.IsHidden) go.gameObject.SetActive(false);
						go.surfaceArray = new ChiselSurfaceArray();
						go.surfaceArray.EnsureSize(planes.Length);

						// cut all the clipping planes out of the brush in one go.
						BrushMesh brushMesh;
						BrushMeshFactory.CreateFromPlanes(planes, new Bounds(Vector3.zero, new Vector3(8192, 8192, 8192)), ref go.surfaceArray, out brushMesh);
						go.definition.BrushOutline = brushMesh;
						var surfaceDefinitions = go.surfaceArray;

						for (int j = 0; j < brushMesh.polygons.Length; j++)
						{
							surfaceDefinitions.surfaces[j] = planeSurfaces[brushMesh.polygons[j].descriptionIndex];
					}

					for (int j = solid.Sides.Count; j-- > 0;)
                    {
                        VmfSolidSide side = solid.Sides[j];
						var surface = planeSurfaces[j];

                        // calculate the texture coordinates.
                        GetTextureSize(surface.RenderMaterial, out int w, out int h);

						var clip = new Plane(planes[j].xyz, planes[j].w);
                        CalculateTextureCoordinates(go, center, surface, clip, w, h, side.UAxis, side.VAxis);
                    }

                    // finalize the brush by snapping planes and centering the pivot point.
                    go.transform.position += brushMesh.CenterAndSnapPlanes(ref surfaceDefinitions);
                    brushIndex.Add(solid, go, brushMesh, sideDrawn);

                    // detail brushes that do not affect the CSG world.
                    //if (entity.ClassName == "func_detail")
						//pr.IsNoCSG = true;
						// collision only brushes.
						//if (entity.ClassName == "func_vehicleclip")
						//pr.IsVisible = false;
					}
				}
				catch (Exception ex)
				{
					Debug.LogException(ex);
				}
			}

			var totalSeconds = importStopwatch.Elapsed.TotalSeconds;
			Debug.Log($"Source map import (world brushes): {totalSeconds:0.0}s total" +
					  ((world.Solids.Count > 0) ? $" ({solidsLoopSeconds * 1000.0 / world.Solids.Count:0.0} ms/solid)" : "") +
					  $" - {world.Solids.Count} solids, {world.Entities.Count} entity brushes, {s_TextureSizes.Count} materials");
        }

        private static void CalculateTextureCoordinates(ChiselBrushComponent pr, Vector3 unfixedOrigin, ChiselSurface surface, Plane clip, int textureWidth, int textureHeight, VmfAxis UAxis, VmfAxis VAxis)
		{
			var localToPlaneSpace = MathExtensions.GenerateLocalToPlaneSpaceMatrix(new float4(clip.normal, clip.distance));
			var planeSpaceToLocal = math.inverse(localToPlaneSpace);

			var uscale =  1.0f / textureWidth;
			var vscale = -1.0f / textureHeight;

            var uVector = new float3(UAxis.Vector.X, UAxis.Vector.Y, UAxis.Vector.Z);
			var vVector = new float3(VAxis.Vector.X, VAxis.Vector.Y, VAxis.Vector.Z);

			uVector /= UAxis.Scale;
			vVector /= VAxis.Scale;

            var uoffset = UAxis.Translation;
            var voffset = VAxis.Translation;

			uVector = new float3(UAxis.Vector.X, UAxis.Vector.Y, UAxis.Vector.Z);
			vVector = new float3(VAxis.Vector.X, VAxis.Vector.Y, VAxis.Vector.Z);

			uVector = Swizzle(uVector);
			vVector = Swizzle(vVector);

			uVector /= UAxis.Scale;
			vVector /= VAxis.Scale;

			uVector /= SourceEngineUnits.VmfInvMeters;
			vVector /= SourceEngineUnits.VmfInvMeters;
			var umatrix = new double4(uVector, uoffset) * uscale;
			var vmatrix = new double4(vVector, voffset) * vscale;
		

			var matTex = Matrix4x4.identity;
			matTex.SetRow(0, (float4)umatrix);
			matTex.SetRow(1, (float4)vmatrix);
			matTex.SetRow(2, float4.zero);

			var uvMatrix = new UVMatrix(math.mul(matTex, planeSpaceToLocal));
			surface.surfaceDetails.UV0 = uvMatrix;
		}

		/// <summary>
		/// Determines whether the specified name is an excluded material.
		/// </summary>
		/// <param name="name">The name of the material.</param>
		/// <returns><c>true</c> if the specified name is an excluded material; otherwise, <c>false</c>.</returns>
		public static bool IsExcludedMaterial(string name)
        {
            switch (name.ToUpperInvariant().Replace('\\', '/'))
            {
                case "TOOLS/TOOLSNODRAW":
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Determines whether the specified name is an invisible material.
        /// </summary>
        /// <param name="name">The name of the material.</param>
        /// <returns><c>true</c> if the specified name is an invisible material; otherwise, <c>false</c>.</returns>
        public static bool IsInvisibleMaterial(string name)
        {
            switch (name.ToUpperInvariant().Replace('\\', '/'))
            {
                case "TOOLS/TOOLSCLIP":
                case "TOOLS/TOOLSNPCCLIP":
                case "TOOLS/TOOLSPLAYERCLIP":
                case "TOOLS/TOOLSGRENDADECLIP":
                case "TOOLS/TOOLSSTAIRS":
				case "TOOLS/TOOLS_XOGVOLUME":
					return true;
            }
            return false;
		}

		// The material a face gets, from what the face produces in Source (VmfBrushContents.FaceKindOf)
		static VmfFaceKind FaceKind(GameResources gameResources, VmfBrushRole role, VmfSolidSide side)
		{
			return VmfBrushContents.FaceKindOf(role, gameResources.GetMaterialContents(side.Material));
		}

		static Material FaceMaterial(GameResources gameResources, VmfBrushRole role, VmfSolidSide side, bool brushFace)
		{
			switch (FaceKind(gameResources, role, side))
			{
				case VmfFaceKind.Sky:        return RenderSettings.skybox;
				case VmfFaceKind.Collision:  return ChiselDefaultMaterials.Defaults.collisionSurfacesMaterial;
				case VmfFaceKind.ShadowOnly: return ChiselDefaultMaterials.Defaults.shadowOnlySurfacesMaterial;
				case VmfFaceKind.Hidden:     return ChiselDefaultMaterials.Defaults.userHiddenSurfacesMaterial;
				// find the material in the unity project automatically.
				default:                     return gameResources.ImportVMT(side.Material, brushFace: brushFace);
			}
		}

		// A world brush goes to the world model and is named after what it is made of, as vbsp decides it: a brush
		// with any glass, grate, slime or water side is that ("Glass 247412"). Any other brush goes to the model for
		// its role, named after the role ("Clip 1234", "Trigger 5678"), and keeps the default contents: it never
		// meets the world's brushes.
		static ChiselBrushComponent CreateBrush(GameResources gameResources, VmfBrushModels brushModels, ChiselModelComponent world,
												VmfEntity entity, VmfSolid solid, VmfBrushRole role, string roleName)
		{
			if (role != VmfBrushRole.World)
				return ChiselComponentFactory.Create<ChiselBrushComponent>($"{roleName} {solid.Id}", brushModels.ModelFor(role, roleName, entity, solid));

			var (contents, contentsName) = VmfBrushContents.Of(solid, gameResources);
			var brush = ChiselComponentFactory.Create<ChiselBrushComponent>($"{contentsName} {solid.Id}", world);
			brush.Contents = contents;
			return brush;
		}

		/// <summary>
		/// A displacement vertex's colour: white, with Hammer's painted alpha (0..255) as its alpha - the
		/// weight a blending material mixes its second layer in by. A displacement nobody painted is 0
		/// throughout, as it is in Source, which shows the first layer only.
		/// </summary>
		public static Color DisplacementVertexColor(VmfSolidSideDisplacement displacement, int row, int column)
		{
			var alphas = displacement.Alphas;
			var alpha = (row < alphas.Count && column < alphas[row].Count) ? alphas[row][column] : 0.0f;
			return new Color(1, 1, 1, Mathf.Clamp01(alpha / 255.0f));
		}

		private static void BuildDisplacementSurface(ChiselBrushComponent go, VmfSolidSide side, ChiselSurface surface, List<Vector3> vertices, Plane plane)
        {
            // create a new game object for the displacement.
            GameObject dgo = new GameObject("Displacement");
            dgo.transform.parent = go.transform;
            MeshRenderer meshRenderer = dgo.AddComponent<MeshRenderer>();
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.TwoSided;
			MeshFilter meshFilter = dgo.AddComponent<MeshFilter>();
            MeshCollider meshCollider = dgo.AddComponent<MeshCollider>();

			// create a mesh.
			Mesh mesh = new()
			{
				name = "Displacement"
			};

			var localToPlaneSpace = (Matrix4x4)MathExtensions.GenerateLocalToPlaneSpaceMatrix(new float4(plane.normal, plane.distance));
			var uvmatrix = surface.surfaceDetails.UV0.ToMatrix4x4();
			uvmatrix *= localToPlaneSpace;

            var vmfStartPosition = Swizzle(side.Displacement.StartPosition) * SourceEngineUnits.VmfInvMeters;

			int[] vertex_winding_indices = new int[4];
			vertex_winding_indices[0] = 0;
			vertex_winding_indices[1] = 1;
			vertex_winding_indices[2] = 2;
			vertex_winding_indices[3] = 3;

			for (int ff = 0; ff < vertices.Count; ff++)
			{
				float diff1 = math.abs(vertices[ff].x - vmfStartPosition.x);
				float diff2 = math.abs(vertices[ff].y - vmfStartPosition.y);
				float diff3 = math.abs(vertices[ff].z - vmfStartPosition.z);

				if ((diff1 < 0.001f) && (diff2 < 0.001f) && (diff3 < 0.001f))
				{
					vertex_winding_indices[0] = (ff    );
					vertex_winding_indices[1] = (ff + 1) % 4;
					vertex_winding_indices[2] = (ff + 2) % 4;
					vertex_winding_indices[3] = (ff + 3) % 4;
					break;
				}
			}

			int power = (int)math.pow(2, (int)side.Displacement.Power) + 1;
			float step = 1.0f / (power - 1);

            var vertex0 = vertices[vertex_winding_indices[0]];
			var vertex1 = vertices[vertex_winding_indices[1]];
			var vertex2 = vertices[vertex_winding_indices[2]];
			var vertex3 = vertices[vertex_winding_indices[3]];

			var eastAxis  = (vertex2 - vertex3) * step;
			var westAxis  = (vertex1 - vertex0) * step;

			var meshVertices = new Vector3[power * power];
			var meshUVs = new Vector2[power * power];
			var meshColors = new Color[power * power];

			var faceNormal = -plane.normal;
			for (int v = 0, x = 0; x < power ; x++)
			{
				var normals = side.Displacement.Normals[x];
				var offsets = side.Displacement.Offsets[x];
				var distances = side.Displacement.Distances[x];

				var eastVector = vertex3 + (eastAxis * x);
				var westVector = vertex0 + (westAxis * x);

				var axis = (eastVector - westVector) * step;
				for (int z = 0; z < power; z++, v++)
				{
					var vertex = (axis * (float)z) + westVector;
					var result = (faceNormal * side.Displacement.Elevation);
					var normal = Swizzle(normals[z]);
					var offset = Swizzle(offsets[z]);
					var distance = distances[z];
					meshVertices[v] = vertex + ((result + (normal * distance)) * SourceEngineUnits.VmfInvMeters) + (offset * SourceEngineUnits.VmfInvMeters);
					meshUVs[v] = uvmatrix.MultiplyPoint(vertex);
					meshColors[v] = DisplacementVertexColor(side.Displacement, x, z);
				}
			}

			int quadCount = ( power - 1 ) * ( power - 1);
            var meshTriangles = new int[quadCount * 6];
            for (int polystart = 0, t = 0, x = 0; x < (power-1); x++, polystart++)
            {
                for (int z = 0; z < (power-1); z++, polystart++, t += 6)
                {
                    var quadIndex0 = polystart;
                    var quadIndex1 = polystart + 1;
                    var quadIndex2 = polystart + power + 1;
                    var quadIndex3 = polystart + power;

                    meshTriangles[t + 0] = quadIndex0;
                    meshTriangles[t + 1] = quadIndex2;
                    meshTriangles[t + 2] = quadIndex1;

                    meshTriangles[t + 3] = quadIndex0;
                    meshTriangles[t + 4] = quadIndex3;
                    meshTriangles[t + 5] = quadIndex2;
				}
            }

			mesh.SetVertices(meshVertices);
            mesh.SetUVs(0, meshUVs);
            mesh.SetColors(meshColors);
            mesh.SetTriangles(meshTriangles, 0);

            // center the mesh.
            {
                mesh.RecalculateBounds();
                Vector3 meshCenter = mesh.bounds.center;
                for (int i = 0; i < meshVertices.Length; i++)
                {
                    meshVertices[i] -= meshCenter;
                }
                mesh.SetVertices(meshVertices);
                mesh.RecalculateBounds();
                dgo.transform.position = meshCenter;
            }

            mesh.RecalculateNormals();
            mesh.RecalculateTangents();

            meshFilter.sharedMesh = mesh;
            meshCollider.sharedMesh = mesh;
            meshRenderer.sharedMaterial = surface.RenderMaterial;
        }
    }
}