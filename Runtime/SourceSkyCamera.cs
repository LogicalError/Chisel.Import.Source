using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace AeternumGames.Chisel.Import.Source.ValveMapFormat2006
{
	/// <summary>
	/// Source's 3D skybox (sky_camera): a miniature of the far scenery, drawn as the sky behind the level. Source renders it
	/// first for every view: from the sky_camera's origin plus the eye's position divided by the scale, so it moves a little
	/// as the eye moves, looking where the eye looks; then it draws the level over it, and the level's sky faces draw nothing,
	/// so the miniature shows through them (viewrender.cpp, CSkyboxView).
	/// <para>
	/// Here a hidden camera renders that view for each camera that shows the level, just before it does, into a texture of its
	/// size (<see cref="SourceViewCompanions"/>). The sky's materials (Skybox/Cubemap Skybox behind everything, Chisel/Source/Sky
	/// Face on the sky faces) show that texture where they are drawn, and the 2D sky where there is none. The global data
	/// (_SourceSky3D, _SourceSky3DEnabled) is set per camera, so of several of these the one enabled last is the one in use.
	/// </para>
	/// </summary>
	[ExecuteAlways, DisallowMultipleComponent]
	public sealed class SourceSkyCamera : MonoBehaviour
	{
		/// <summary>sky_camera's scale: the level is this many times the size of the miniature</summary>
		public float scale = 16;

		/// <summary>Where Source's world origin is: the imported map's root. Without one, the world's origin</summary>
		public Transform level;

		/// <summary>The sky view's near plane: Source draws the sky from 2 units out (CSkyboxView::DrawInternal)</summary>
		public float nearClipPlane = 0.038f;

		/// <summary>
		/// sky_camera's own fog, which the 3D skybox is drawn with instead of the level's. Its distances are the map's
		/// fogstart and fogend DIVIDED BY THE SCALE, because the miniature is that many times smaller - Source does the
		/// division at render time (CSkyboxView::Enable3dSkyboxFog: <c>FogStart(GetSkyboxFogStart() * 1/scale)</c>), and
		/// the importer does it here so the numbers stored are the ones this view is drawn with. maxDensity is NOT
		/// scaled. See <see cref="SourceFog"/> for what the numbers mean.
		/// </summary>
		[Header("sky_camera's own fog")]
		public bool  fogEnabled;
		public Color fogColor          = new Color(0.725f, 0.906f, 1.0f);
		public Color fogColorSecondary = new Color(0.592f, 0.855f, 1.0f);
		public bool  fogBlend;
		public Vector3 fogDirection = Vector3.forward;
		public float fogStart      = 3.66f;
		public float fogEnd        = 17.16f;
		public float fogMaxDensity = 1.0f;

		// Every sky camera in the scene, so the fog pass can ask whether a camera is one of their views
		static readonly List<SourceSkyCamera> s_All = new List<SourceSkyCamera>();

		/// <summary>
		/// The fog a camera draws the 3D skybox with, when the camera is a sky view's own companion. False for every
		/// other camera, which is then fogged with the level's own <see cref="SourceFog"/>.
		/// </summary>
		public static bool TryGetSkyFog(Camera camera, out Color color, out Color colorSecondary,
										out Vector4 parameters, out Vector4 direction)
		{
			color = default; colorSecondary = default; parameters = default; direction = default;
			if (camera == null)
				return false;
			foreach (var sky in s_All)
			{
				if (sky == null || !sky.isActiveAndEnabled || sky.companions == null || !sky.companions.IsOwnCompanion(camera))
					continue;
				if (!sky.fogEnabled)
					return false;
				color          = sky.fogColor;
				colorSecondary = sky.fogColorSecondary;
				parameters     = new Vector4(sky.fogStart, sky.fogEnd, sky.fogMaxDensity, sky.fogBlend ? 1 : 0);
				direction      = ((Vector4)sky.fogDirection.normalized);
				return true;
			}
			return false;
		}

		static readonly int kSkyTextureID = Shader.PropertyToID("_SourceSky3D");
		static readonly int kSkyEnabledID = Shader.PropertyToID("_SourceSky3DEnabled");

		SourceViewCompanions companions;

		void OnEnable()
		{
			companions = new SourceViewCompanions("3D skybox", Pose, shadows: false);
			RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
			if (!s_All.Contains(this))
				s_All.Add(this);
		}

		void OnDisable()
		{
			RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
			s_All.Remove(this);
			companions?.Dispose();
			companions = null;
			Shader.SetGlobalFloat(kSkyEnabledID, 0);
		}

		/// <summary>
		/// Where Source's sky view looks from for an eye (CSkyboxView::DrawInternal): the sky_camera's origin plus the eye's
		/// position in the level divided by the scale, looking exactly where the eye looks.
		/// <para>
		/// The view is never turned. sky3dparams_t carries only scale, origin, area and fog (playernet_vars.h), and
		/// sky_camera's use_angles sets the fog's direction, not the view's - see VmfEntityConverter's sky_camera case.
		/// </para>
		/// </summary>
		public static void SkyPose(Vector3 eye, Quaternion eyeRotation, Vector3 levelOrigin, Vector3 skyOrigin, float scale,
								   out Vector3 position, out Quaternion rotation)
		{
			position = skyOrigin + ((eye - levelOrigin) / Mathf.Max(scale, 1e-3f));
			rotation = eyeRotation;
		}

		/// <summary>
		/// Renders the sky view for a camera that renders on request (RenderPipeline.SubmitRenderRequest), which begins no frame
		/// that would draw it; the camera then shows it when it renders. Returns the sky view, or null when there is none.
		/// </summary>
		public RenderTexture RenderFor(Camera camera)
		{
			return companions?.RenderNow(camera);
		}

		bool Pose(Camera camera, Camera sky)
		{
			if (!isActiveAndEnabled)
				return false;
			var eye = camera.transform;
			SkyPose(eye.position, eye.rotation, level ? level.position : Vector3.zero, transform.position, scale,
					out var position, out var rotation);
			sky.transform.SetPositionAndRotation(position, rotation);
			sky.nearClipPlane = nearClipPlane;
			sky.farClipPlane = Mathf.Max(camera.farClipPlane, nearClipPlane * 2);
			return true;
		}

		// Each camera that has a sky view shows it; everything else, the sky views themselves included, shows the 2D sky
		void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
		{
			var target = (companions != null && !SourceViewCompanions.IsCompanion(camera)) ? companions.TargetOf(camera) : null;
			if (target != null)
				Shader.SetGlobalTexture(kSkyTextureID, target);
			Shader.SetGlobalFloat(kSkyEnabledID, (target != null) ? 1 : 0);
		}
	}
}
