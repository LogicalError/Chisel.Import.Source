using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace AeternumGames.Chisel.Import.Source.ValveMapFormat2006
{
	/// <summary>
	/// The planar reflection Source's expensive water shows ($reflecttexture _rt_WaterReflection): the level mirrored in the
	/// water's plane, rendered for the view before the water is drawn (viewrender.cpp, CReflectionView). Source renders it for the
	/// water the view is in front of; here each camera gets it for the nearest reflecting water plane below it that it can see,
	/// from a companion camera (<see cref="SourceViewCompanions"/>) at the mirrored eye whose near plane lies on the water, so
	/// nothing under the water shows. The Water shader takes it (_SourceWaterReflection) where its surface lies in that plane,
	/// and the env_cubemap probe elsewhere.
	/// <para>
	/// A mirrored camera is left-handed, and URP has no per-camera switch to turn its culling around. So the companion keeps a
	/// right-handed frame - the mirrored forward and up, with its right the other way - and what it renders is the reflection
	/// flipped left to right, which the shader flips back.
	/// </para>
	/// <para>
	/// The reflecting water is found by its material: a Water material that reflects (_Reflect), on renderers in the loaded
	/// scenes, its surfaces grouped into planes by height. They are looked for again when those meshes change.
	/// </para>
	/// </summary>
	[ExecuteAlways, DisallowMultipleComponent]
	public sealed class SourceWaterReflection : MonoBehaviour
	{
		/// <summary>How far above the water the reflection's near plane lies, so the water's own surface stays out of it</summary>
		public float clipOffset = 0.02f;

		/// <summary>The reflection renders realtime shadows</summary>
		public bool shadows = false;

		/// <summary>A reflecting water surface: its height, and the box around it</summary>
		public struct WaterPlane
		{
			public float height;
			public Bounds bounds;
		}

		static readonly int kReflectionID        = Shader.PropertyToID("_SourceWaterReflection");
		static readonly int kReflectionEnabledID = Shader.PropertyToID("_SourceWaterReflectionEnabled");
		static readonly int kReflectionHeightID  = Shader.PropertyToID("_SourceWaterReflectionHeight");

		// Surfaces whose heights differ by less than this are one plane
		const float kPlaneTolerance = 0.01f;

		SourceViewCompanions companions;
		readonly List<WaterPlane> planes = new();
		readonly Dictionary<Camera, float> heights = new();
		readonly List<(MeshFilter filter, Mesh mesh, int vertexCount)> sources = new();
		readonly Plane[] frustum = new Plane[6];
		bool found;

		/// <summary>The planes found, for inspection</summary>
		public IReadOnlyList<WaterPlane> Planes => planes;

		void OnEnable()
		{
			found = false;
			companions = new SourceViewCompanions("Water reflection", Pose, shadows);
			RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
		}

		void OnDisable()
		{
			RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
			companions?.Dispose();
			companions = null;
			heights.Clear();
			Shader.SetGlobalFloat(kReflectionEnabledID, 0);
		}

		/// <summary>
		/// The eye mirrored in the water plane at this height: at the mirrored position, looking along the mirrored forward with
		/// the mirrored up. That frame is right-handed where the true mirror image is not, so it sees the reflection flipped left
		/// to right.
		/// </summary>
		public static void MirrorPose(Vector3 eye, Quaternion eyeRotation, float height, out Vector3 position, out Quaternion rotation)
		{
			position = new Vector3(eye.x, 2 * height - eye.y, eye.z);
			var forward = eyeRotation * Vector3.forward;
			var up = eyeRotation * Vector3.up;
			forward.y = -forward.y;
			up.y = -up.y;
			rotation = Quaternion.LookRotation(forward, up);
		}

		/// <summary>Adds a triangle facing up to the plane at its height, or starts one</summary>
		public static void AddToPlanes(List<WaterPlane> planes, Vector3 a, Vector3 b, Vector3 c, float tolerance)
		{
			var normal = Vector3.Cross(b - a, c - a);
			if (normal.sqrMagnitude < 1e-12f || normal.normalized.y < 0.99f)
				return;
			var height = (a.y + b.y + c.y) / 3;
			for (int i = 0; i < planes.Count; i++)
			{
				if (Mathf.Abs(planes[i].height - height) > tolerance)
					continue;
				var plane = planes[i];
				plane.bounds.Encapsulate(a);
				plane.bounds.Encapsulate(b);
				plane.bounds.Encapsulate(c);
				planes[i] = plane;
				return;
			}
			var bounds = new Bounds(a, Vector3.zero);
			bounds.Encapsulate(b);
			bounds.Encapsulate(c);
			planes.Add(new WaterPlane { height = height, bounds = bounds });
		}

		static bool Reflects(Material material)
		{
			return material != null && material.shader != null && material.shader.name == "Chisel/Source/Water" &&
				   material.HasProperty("_Reflect") && material.GetFloat("_Reflect") > 0.5f;
		}

		// Looks for the reflecting water again when a mesh it was found on has changed, or the first time
		void FindWaterWhenChanged()
		{
			if (found)
			{
				var changed = false;
				foreach (var source in sources)
				{
					if (source.filter == null || source.filter.sharedMesh != source.mesh || source.mesh == null ||
						source.mesh.vertexCount != source.vertexCount)
					{
						changed = true;
						break;
					}
				}
				if (!changed)
					return;
			}
			found = true;
			planes.Clear();
			sources.Clear();
			foreach (var renderer in FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude))
			{
				var filter = renderer.GetComponent<MeshFilter>();
				var mesh = (filter != null) ? filter.sharedMesh : null;
				if (mesh == null || !mesh.isReadable)
					continue;
				var materials = renderer.sharedMaterials;
				Vector3[] vertices = null;
				var matrix = renderer.transform.localToWorldMatrix;
				for (int s = 0; s < materials.Length && s < mesh.subMeshCount; s++)
				{
					if (!Reflects(materials[s]))
						continue;
					vertices ??= mesh.vertices;
					var triangles = mesh.GetTriangles(s);
					for (int t = 0; t + 2 < triangles.Length; t += 3)
						AddToPlanes(planes, matrix.MultiplyPoint3x4(vertices[triangles[t]]), matrix.MultiplyPoint3x4(vertices[triangles[t + 1]]),
									matrix.MultiplyPoint3x4(vertices[triangles[t + 2]]), kPlaneTolerance);
				}
				if (vertices != null)
					sources.Add((filter, mesh, mesh.vertexCount));
			}
			// A flat box can't be tested against a frustum reliably
			for (int i = 0; i < planes.Count; i++)
			{
				var plane = planes[i];
				plane.bounds.Expand(new Vector3(0, 0.1f, 0));
				planes[i] = plane;
			}
		}

		/// <summary>
		/// Renders the reflection for a camera that renders on request (RenderPipeline.SubmitRenderRequest), which begins no
		/// frame that would draw it; the camera then shows it when it renders. Returns the reflection, or null when the camera
		/// sees no reflecting water.
		/// </summary>
		public RenderTexture RenderFor(Camera camera)
		{
			return companions?.RenderNow(camera);
		}

		bool Pose(Camera camera, Camera reflection)
		{
			if (!isActiveAndEnabled)
				return false;
			FindWaterWhenChanged();

			var eye = camera.transform.position;
			GeometryUtility.CalculateFrustumPlanes(camera, frustum);
			var best = -1;
			var bestDistance = float.MaxValue;
			for (int i = 0; i < planes.Count; i++)
			{
				if (eye.y <= planes[i].height + clipOffset || !GeometryUtility.TestPlanesAABB(frustum, planes[i].bounds))
					continue;
				var distance = planes[i].bounds.SqrDistance(eye);
				if (distance < bestDistance)
				{
					best = i;
					bestDistance = distance;
				}
			}
			if (best < 0)
			{
				heights.Remove(camera);
				return false;
			}

			var height = planes[best].height;
			heights[camera] = height;
			MirrorPose(eye, camera.transform.rotation, height, out var position, out var rotation);
			reflection.transform.SetPositionAndRotation(position, rotation);
			reflection.nearClipPlane = camera.nearClipPlane;
			reflection.farClipPlane = camera.farClipPlane;
			reflection.ResetProjectionMatrix();

			// The near plane on the water, so nothing under it shows (Unity's camera space looks along -z)
			var worldToCamera = reflection.worldToCameraMatrix;
			var point = worldToCamera.MultiplyPoint(new Vector3(eye.x, height + clipOffset, eye.z));
			var normal = worldToCamera.MultiplyVector(Vector3.up).normalized;
			reflection.projectionMatrix = reflection.CalculateObliqueMatrix(new Vector4(normal.x, normal.y, normal.z, -Vector3.Dot(point, normal)));
			return true;
		}

		// A camera with a reflection shows it on water in its plane; everything else, the companions included, shows the probes
		void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
		{
			var target = (companions != null && !SourceViewCompanions.IsCompanion(camera)) ? companions.TargetOf(camera) : null;
			if (target != null && heights.TryGetValue(camera, out var height))
			{
				Shader.SetGlobalTexture(kReflectionID, target);
				Shader.SetGlobalFloat(kReflectionHeightID, height);
				Shader.SetGlobalFloat(kReflectionEnabledID, 1);
			}
			else
				Shader.SetGlobalFloat(kReflectionEnabledID, 0);
		}
	}
}
