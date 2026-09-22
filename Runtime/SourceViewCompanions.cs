using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace AeternumGames.Chisel.Import.Source.ValveMapFormat2006
{
	/// <summary>
	/// A second view drawn for every camera that shows the level, before that camera draws, into a texture the size of its view:
	/// Source draws its 3D skybox that way, and its water's planar reflection. URP can't render a camera from inside another's
	/// rendering ("Recursive rendering is not supported in SRP"), and can't stack cameras while the level renders deferred
	/// (UniversalRenderer: no overlay cameras with deferred lighting), nor in the Scene view. So every such camera gets a hidden
	/// companion camera:
	/// <list type="bullet">
	/// <item>A game camera's companion is an ordinary enabled camera with a lower depth, which URP renders first in the same
	/// frame. It is posed when the frame begins (<see cref="RenderPipelineManager.beginContextRendering"/>), before any camera
	/// renders; a camera seen for the first time gets its companion from the next frame on. This needs a PLAYER LOOP: in the
	/// editor with nothing playing there isn't one, a Game view repaint draws only the cameras that target the screen, and a
	/// companion draws into a texture - so in edit mode it renders on request from EditorApplication.update instead.</item>
	/// <item>A Scene view's companion renders on request just before the Scene view does (SceneView.beforeSceneGui).</item>
	/// </list>
	/// </summary>
	public sealed class SourceViewCompanions : IDisposable
	{
		/// <summary>Poses a camera's companion (position, rotation, projection) before it renders; false leaves it out</summary>
		public delegate bool PoseCompanion(Camera camera, Camera companion);

		sealed class View
		{
			public Camera companion;
			public RenderTexture target;
			// Where the camera was when this was last rendered, and when. A companion is expensive - it draws the
			// level a second time - so in edit mode it is redrawn when the view it stands in for has actually moved,
			// and otherwise only at a slow idle rate, which is what picks up an edit to the level itself.
			public Matrix4x4 renderedFor = Matrix4x4.zero;
			public double renderedAt;
		}

		/// <summary>
		/// Something renders the companions by itself: the player loop, which draws every enabled camera each frame in
		/// depth order. In the editor with nothing playing there is no player loop, and a Game view REPAINT is not one -
		/// it draws the cameras that target the screen, which a companion never does.
		/// </summary>
		static bool PlayerLoopRenders
		{
#if UNITY_EDITOR
			get { return Application.isPlaying; }
#else
			get { return true; }
#endif
		}

		// How long an unmoved view may go without being redrawn in edit mode. Short enough that an edit to the level
		// shows up as a matter of course, long enough that an idle editor is not drawing the level twice per tick.
		const double kIdleRedrawSeconds = 0.25;

		readonly string name;
		readonly PoseCompanion pose;
		readonly bool shadows;
		readonly Dictionary<Camera, View> views = new();

		// Every companion camera, of every kind of view: a companion never gets one of its own (the sky view has no reflection,
		// the reflection no sky view of its own)
		static readonly HashSet<Camera> companions = new();

		/// <param name="name">What the companions and their textures are called</param>
		/// <param name="pose">Poses a companion for its camera</param>
		/// <param name="shadows">The companions render shadows</param>
		public SourceViewCompanions(string name, PoseCompanion pose, bool shadows)
		{
			this.name = name;
			this.pose = pose;
			this.shadows = shadows;
			RenderPipelineManager.beginContextRendering += OnBeginContextRendering;
#if UNITY_EDITOR
			UnityEditor.SceneView.beforeSceneGui += OnBeforeSceneGui;
			UnityEditor.EditorApplication.update += OnEditorUpdate;
#endif
		}

		public void Dispose()
		{
			RenderPipelineManager.beginContextRendering -= OnBeginContextRendering;
#if UNITY_EDITOR
			UnityEditor.SceneView.beforeSceneGui -= OnBeforeSceneGui;
			UnityEditor.EditorApplication.update -= OnEditorUpdate;
#endif
			foreach (var view in views.Values)
				Release(view);
			views.Clear();
		}

		/// <summary>The camera is a companion, of these views or of any other kind</summary>
		public static bool IsCompanion(Camera camera)
		{
			return camera != null && companions.Contains(camera);
		}

		/// <summary>
		/// The camera is one of THESE views' companions, rather than another kind's. A sky view and a water reflection
		/// are both companions, and only the sky view's is drawn with the 3D skybox's own fog, so the two have to be
		/// told apart (<see cref="SourceSkyCamera.TryGetSkyFog"/>).
		/// </summary>
		public bool IsOwnCompanion(Camera camera)
		{
			if (camera == null)
				return false;
			foreach (var view in views.Values)
			{
				if (view.companion == camera)
					return true;
			}
			return false;
		}

		/// <summary>What the camera's companion rendered for it, or null when it has none yet</summary>
		public RenderTexture TargetOf(Camera camera)
		{
			if (camera == null || !views.TryGetValue(camera, out var view) || view.target == null || !view.target.IsCreated())
				return null;
			return view.target;
		}

		// A camera the level is drawn for: the game's cameras and the Scene views. Not previews, reflection probes being baked,
		// cameras rendering into textures of their own, nor the companions themselves.
		bool Serves(Camera camera)
		{
			if (camera == null || companions.Contains(camera))
				return false;
			if (camera.cameraType == CameraType.SceneView)
				return true;
			return camera.cameraType == CameraType.Game && camera.targetTexture == null;
		}

		void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras)
		{
			BeginFrame(cameras);
		}

		/// <summary>Poses the companions of the game cameras a frame is about to render, and forgets the cameras destroyed since</summary>
		internal void BeginFrame(List<Camera> cameras)
		{
			foreach (var camera in cameras)
			{
				if (camera.cameraType != CameraType.Game || !Serves(camera))
					continue;
				var view = ViewOf(camera);
				if (!PlayerLoopRenders)
					continue;   // OnEditorUpdate renders this one on request; leave its camera alone
				view.companion.targetTexture = view.target;
				view.companion.enabled = pose(camera, view.companion);
			}
			ForgetDestroyedCameras();
		}

		/// <summary>
		/// Renders the camera's companion now. For a Scene view, and for a camera that renders on request
		/// (RenderPipeline.SubmitRenderRequest): URP begins no frame for those, so nothing else poses their companions.
		/// Returns what the companion rendered, or null when the camera isn't one it serves or the pose leaves it out.
		/// </summary>
		public RenderTexture RenderNow(Camera camera)
		{
			if (!Serves(camera))
				return null;
			var view = ViewOf(camera);
			view.companion.enabled = false;
			view.companion.targetTexture = null;
			if (!pose(camera, view.companion))
				return null;
			var request = new UniversalRenderPipeline.SingleCameraRequest { destination = view.target };
			if (!RenderPipeline.SupportsRenderRequest(view.companion, request))
				return null;
			RenderPipeline.SubmitRenderRequest(view.companion, request);
			view.renderedFor = camera.transform.localToWorldMatrix;
			view.renderedAt = Time.realtimeSinceStartupAsDouble;
			return view.target;
		}

#if UNITY_EDITOR
		/// <summary>
		/// Renders the game cameras' companions in edit mode, where no player loop does. It has to happen here rather
		/// than in <see cref="OnBeginContextRendering"/> because that is already inside RenderPipeline.Render, and a
		/// render request there would nest one render inside another - which is the thing companions exist to avoid.
		/// </summary>
		void OnEditorUpdate()
		{
			if (PlayerLoopRenders)
				return;
			var now = Time.realtimeSinceStartupAsDouble;
			foreach (var camera in Camera.allCameras)
			{
				if (camera == null || camera.cameraType != CameraType.Game || !Serves(camera))
					continue;
				if (views.TryGetValue(camera, out var view) &&
					view.renderedFor == camera.transform.localToWorldMatrix &&
					now - view.renderedAt < kIdleRedrawSeconds)
					continue;
				RenderNow(camera);
			}
			ForgetDestroyedCameras();
		}

		void OnBeforeSceneGui(UnityEditor.SceneView sceneView)
		{
			if (Event.current == null || Event.current.type != EventType.Repaint)
				return;
			RenderNow(sceneView.camera);
		}
#endif

		View ViewOf(Camera camera)
		{
			if (!views.TryGetValue(camera, out var view))
			{
				var gameObject = new GameObject(name + " for " + camera.name) { hideFlags = HideFlags.HideAndDontSave };
				var companion = gameObject.AddComponent<Camera>();
				companion.enabled = false;
				var data = gameObject.AddComponent<UniversalAdditionalCameraData>();
				data.renderPostProcessing = false;
				data.renderShadows = shadows;
				data.antialiasing = AntialiasingMode.None;
				data.requiresColorOption = CameraOverrideOption.Off;
				// A companion draws a scene, and a scene is fogged by how far away things are (SourceFogFeature), so
				// it needs its own depth texture. With this off the fog pass has nothing to measure and skips the view
				// entirely, which does not look like a missing feature: the sky view comes out unfogged and is then
				// composited into a level that IS fogged, so the vista reads sharp exactly where a sky face shows it.
				data.requiresDepthOption = CameraOverrideOption.On;
				view = new View { companion = companion };
				views.Add(camera, view);
				companions.Add(companion);
			}

			var width = Mathf.Max(1, camera.pixelWidth);
			var height = Mathf.Max(1, camera.pixelHeight);
			if (view.target == null || view.target.width != width || view.target.height != height)
			{
				ReleaseTarget(view);
				view.target = new RenderTexture(width, height, 24, RenderTextureFormat.DefaultHDR)
				{
					name = name + " for " + camera.name,
					hideFlags = HideFlags.HideAndDontSave
				};
				view.target.Create();
			}

			// What the view shares with its camera; the pose sets where it looks from
			var companionCamera = view.companion;
			companionCamera.depth = camera.depth - 1;
			companionCamera.clearFlags = CameraClearFlags.Skybox;
			companionCamera.cullingMask = camera.cullingMask;
			companionCamera.allowHDR = true;
			companionCamera.allowMSAA = false;
			companionCamera.orthographic = camera.orthographic;
			companionCamera.orthographicSize = camera.orthographicSize;
			companionCamera.fieldOfView = camera.fieldOfView;
			companionCamera.aspect = camera.aspect;
			return view;
		}

		void ForgetDestroyedCameras()
		{
			List<Camera> destroyed = null;
			foreach (var camera in views.Keys)
			{
				if (camera == null)
					(destroyed ??= new List<Camera>()).Add(camera);
			}
			if (destroyed == null)
				return;
			foreach (var camera in destroyed)
			{
				Release(views[camera]);
				views.Remove(camera);
			}
		}

		void Release(View view)
		{
			ReleaseTarget(view);
			if (view.companion != null)
			{
				companions.Remove(view.companion);
				Destroy(view.companion.gameObject);
			}
		}

		static void ReleaseTarget(View view)
		{
			if (view.target == null)
				return;
			// A camera may not render into a texture that is released: a game camera's companion does, from the frame it was posed
			// for on
			if (view.companion != null && view.companion.targetTexture == view.target)
				view.companion.targetTexture = null;
			view.target.Release();
			Destroy(view.target);
			view.target = null;
		}

		static void Destroy(UnityEngine.Object obj)
		{
			if (Application.isPlaying)
				UnityEngine.Object.Destroy(obj);
			else
				UnityEngine.Object.DestroyImmediate(obj);
		}
	}
}
