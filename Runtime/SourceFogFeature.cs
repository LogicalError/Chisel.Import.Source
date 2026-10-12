#if COM_UNITY_RENDER_PIPELINES_UNIVERSAL
using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using AeternumGames.Chisel.Import.Source.ValveMapFormat2006;

namespace AeternumGames.Chisel.Import.Source
{
	/// <summary>
	/// Renders <see cref="SourceFog"/> over the frame. Does nothing for a camera whose volumes don't turn it on.
	/// <para>
	/// The 3D skybox is fogged with its own numbers rather than the level's: Source's sky view divides the fog's start
	/// and end by the skybox's scale, because the miniature is that many times smaller
	/// (CSkyboxView::Enable3dSkyboxFog, viewrender.cpp). Its maxdensity is not scaled. A companion camera of a
	/// <see cref="SourceSkyCamera"/> therefore takes that camera's fog, already divided.
	/// </para>
	/// </summary>
	[DisallowMultipleRendererFeature("Source Fog")]
	public sealed class SourceFogFeature : ScriptableRendererFeature
	{
		[SerializeField] Shader m_Shader;

		/// <summary>The shader that fogs the frame (Runtime/Shaders/SourceFog.shader).</summary>
		public Shader Shader
		{
			get => m_Shader;
			set => m_Shader = value;
		}

		SourceFogPass m_Pass;
		Material m_Material;

		public override void Create()
		{
			m_Pass?.Dispose();
			// Source fogs the world before post processing: the exposure that follows measures the fogged frame
			m_Pass = new SourceFogPass { renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing };
		}

		public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
		{
			if (m_Shader == null || m_Pass == null)
				return;
			if (m_Material == null)
			{
				m_Material = CoreUtils.CreateEngineMaterial(m_Shader);
				if (m_Material == null)
					return;
			}
			m_Pass.Material = m_Material;
			// The fog needs to know how far away everything is
			m_Pass.ConfigureInput(ScriptableRenderPassInput.Depth);
			renderer.EnqueuePass(m_Pass);
		}

		protected override void Dispose(bool disposing)
		{
			m_Pass?.Dispose();
			m_Pass = null;
			CoreUtils.Destroy(m_Material);
			m_Material = null;
		}
	}

	sealed class SourceFogPass : ScriptableRenderPass, IDisposable
	{
		static readonly int s_Color          = UnityEngine.Shader.PropertyToID("_SourceFogColor");
		static readonly int s_ColorSecondary = UnityEngine.Shader.PropertyToID("_SourceFogColorSecondary");
		static readonly int s_Params         = UnityEngine.Shader.PropertyToID("_SourceFogParams");
		static readonly int s_Direction      = UnityEngine.Shader.PropertyToID("_SourceFogDirection");
		// Sampled by name rather than through SampleSceneDepth, so the pass reads the texture it declared instead of
		// whatever _CameraDepthTexture happens to hold when it runs
		static readonly int s_Depth          = UnityEngine.Shader.PropertyToID("_SourceFogDepth");

		public Material Material { get; set; }

		public SourceFogPass()
		{
			profilingSampler = new ProfilingSampler("Source Fog");
			requiresIntermediateTexture = true;
		}

		sealed class PassData
		{
			public Material Material;
			public TextureHandle Source, Depth;
			public Color Color, ColorSecondary;
			public Vector4 Params, Direction;
		}

		public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
		{
			if (Material == null)
				return;

			var resourceData = frameData.Get<UniversalResourceData>();
			var cameraData   = frameData.Get<UniversalCameraData>();
			if (resourceData.isActiveTargetBackBuffer)
				return;
			var camera = cameraData.camera;
			if (camera == null)
				return;

			// Never fog a reflection probe's bake, nor a preview. A probe's cubemap is radiance, sampled later from
			// every distance and direction; fog is a function of distance from ONE viewpoint, so fogging the bake
			// paints a single distance's haze into the texture and no later setting can take it out again.
			if (camera.cameraType == CameraType.Reflection || camera.cameraType == CameraType.Preview)
				return;

			// The 3D skybox's own fog when this is its view, the level's otherwise
			Color color, colorSecondary;
			Vector4 parameters, direction;
			if (!SourceSkyCamera.TryGetSkyFog(camera, out color, out colorSecondary, out parameters, out direction))
			{
				var settings = VolumeManager.instance.stack.GetComponent<SourceFog>();
				if (settings == null || !settings.IsActive())
					return;
				color          = settings.color.value;
				colorSecondary = settings.colorSecondary.value;
				parameters     = new Vector4(settings.start.value, settings.end.value, settings.maxDensity.value,
											 settings.blend.value ? 1 : 0);
				direction      = settings.direction.value.normalized;
			}
			if (parameters.z <= 0 || parameters.y <= parameters.x)
				return;

			// How far away everything is IS the fog. Without it every pixel would be fogged by the same amount, which
			// is not a weaker version of the effect but an opaque sheet of fog colour over the frame - and one that
			// still looks plausible in a screenshot, so it has to be refused here rather than rendered.
			var depth = resourceData.cameraDepthTexture;
			if (!depth.IsValid())
				return;

			var source = resourceData.activeColorTexture;
			var destinationDesc = renderGraph.GetTextureDesc(source);
			destinationDesc.name        = "Source Fog";
			destinationDesc.format      = GraphicsFormat.R16G16B16A16_SFloat;
			destinationDesc.msaaSamples = MSAASamples.None;
			destinationDesc.clearBuffer = false;
			var destination = renderGraph.CreateTexture(destinationDesc);

			using (var builder = renderGraph.AddRasterRenderPass<PassData>("Source Fog", out var data, profilingSampler))
			{
				data.Material       = Material;
				data.Source         = source;
				data.Depth          = depth;
				data.Color          = color;
				data.ColorSecondary = colorSecondary;
				data.Params         = parameters;
				data.Direction      = direction;

				builder.UseTexture(source, AccessFlags.Read);
				// Declaring it is what binds _CameraDepthTexture for this pass. ConfigureInput only asks the renderer
				// to produce one; without this the shader samples nothing and every pixel comes back the same.
				builder.UseTexture(depth, AccessFlags.Read);
				builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
				builder.SetRenderFunc((PassData pass, RasterGraphContext context) =>
				{
					pass.Material.SetColor(s_Color, pass.Color);
					pass.Material.SetColor(s_ColorSecondary, pass.ColorSecondary);
					pass.Material.SetVector(s_Params, pass.Params);
					pass.Material.SetVector(s_Direction, pass.Direction);
					pass.Material.SetTexture(s_Depth, pass.Depth);
					Blitter.BlitTexture(context.cmd, pass.Source, new Vector4(1, 1, 0, 0), pass.Material, 0);
				});
			}
			resourceData.cameraColor = destination;
		}

		public void Dispose() { }
	}
}
#endif
