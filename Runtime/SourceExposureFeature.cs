#if COM_UNITY_RENDER_PIPELINES_UNIVERSAL
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace AeternumGames.Chisel.Import.Source
{
	/// <summary>
	/// Renders <see cref="SourceExposure"/>: measures each frame and scales it before URP's post processing. Does
	/// nothing for a camera whose volumes don't turn it on.
	/// </summary>
	[DisallowMultipleRendererFeature("Source Exposure")]
	public sealed class SourceExposureFeature : ScriptableRendererFeature
	{
		[SerializeField] ComputeShader m_Shader;

		/// <summary>The compute shader that measures and scales the frame (Runtime/Shaders/SourceExposure.compute).</summary>
		public ComputeShader Shader
		{
			get => m_Shader;
			set => m_Shader = value;
		}

		SourceExposurePass m_Pass;

		public override void Create()
		{
			m_Pass?.Dispose();
			m_Pass = new SourceExposurePass { renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing };
		}

		public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
		{
			if (m_Shader == null || m_Pass == null)
				return;
			var settings = VolumeManager.instance.stack.GetComponent<SourceExposure>();
			if (settings == null || !settings.IsActive())
				return;
			m_Pass.Shader = m_Shader;
			renderer.EnqueuePass(m_Pass);
		}

		protected override void Dispose(bool disposing)
		{
			m_Pass?.Dispose();
			m_Pass = null;
		}
	}

	sealed class SourceExposurePass : ScriptableRenderPass, IDisposable
	{
		const int kBins = 160;		// SourceExposure.compute

		static readonly int s_Source      = UnityEngine.Shader.PropertyToID("_Source");
		static readonly int s_Destination = UnityEngine.Shader.PropertyToID("_Destination");
		static readonly int s_Histogram   = UnityEngine.Shader.PropertyToID("_Histogram");
		static readonly int s_Exposure    = UnityEngine.Shader.PropertyToID("_Exposure");
		static readonly int s_Region      = UnityEngine.Shader.PropertyToID("_Region");
		static readonly int s_Count       = UnityEngine.Shader.PropertyToID("_Count");
		static readonly int s_Params      = UnityEngine.Shader.PropertyToID("_Params");
		static readonly int s_Targets     = UnityEngine.Shader.PropertyToID("_Targets");

		// Source measures gamma-encoded luminance; the histogram holds linear luminance, so its targets are linear.
		// The brightest 2% at 60%, Source's sticky bin around it ((11/16)^1.5 to (12/16)^1.5 of its 16 bins), and
		// the median at 3% at least.
		static readonly Vector4 s_TargetValues = new Vector4(Mathf.GammaToLinearSpace(0.6f),
															 Mathf.GammaToLinearSpace(Mathf.Pow(11 / 16.0f, 1.5f)),
															 Mathf.GammaToLinearSpace(Mathf.Pow(12 / 16.0f, 1.5f)),
															 Mathf.GammaToLinearSpace(0.03f));

		// The center of the frame Source measures (mat_exposure_center_region_x/y), every other pixel of it
		const float kRegionX = 0.9f, kRegionY = 0.85f;
		const int kStep = 2;

		sealed class CameraState
		{
			public GraphicsBuffer Exposure;
			public float Time;
		}

		// Each camera's scale, kept on the GPU from one frame to the next
		readonly Dictionary<Camera, CameraState> m_Cameras = new Dictionary<Camera, CameraState>();
		readonly List<Camera> m_Removed = new List<Camera>();

		ComputeShader m_Shader;
		int m_Clear, m_Histogram, m_ExposureKernel, m_Apply;

		public ComputeShader Shader
		{
			set
			{
				if (m_Shader == value)
					return;
				m_Shader         = value;
				m_Clear          = value.FindKernel("KClear");
				m_Histogram      = value.FindKernel("KHistogram");
				m_ExposureKernel = value.FindKernel("KExposure");
				m_Apply          = value.FindKernel("KApply");
			}
		}

		public SourceExposurePass()
		{
			profilingSampler = new ProfilingSampler("Source Exposure");
			requiresIntermediateTexture = true;
		}

		sealed class PassData
		{
			public ComputeShader Shader;
			public int Clear, Histogram, Exposure, Apply;
			public TextureHandle Source, Destination;
			public BufferHandle HistogramBuffer, ExposureBuffer;
			public Vector4 Region, Count, Params;
			public int GroupsX, GroupsY, ApplyX, ApplyY;
		}

		public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
		{
			var settings = VolumeManager.instance.stack.GetComponent<SourceExposure>();
			if (m_Shader == null || settings == null || !settings.IsActive())
				return;

			var resourceData = frameData.Get<UniversalResourceData>();
			var cameraData   = frameData.Get<UniversalCameraData>();
			if (resourceData.isActiveTargetBackBuffer || !cameraData.postProcessEnabled)
				return;
			var camera = cameraData.camera;
			if (camera == null)
				return;

			// How far the scale may move towards the frame's this time: all the way outside play mode
			var state = GetState(camera);
			var now = UnityEngine.Time.realtimeSinceStartup;
			float towardsBrighter = 1, towardsDarker = 1;
			if (Application.isPlaying)
			{
				var seconds = Mathf.Clamp(now - state.Time, 0, 1);
				towardsBrighter = 1 - Mathf.Exp(-seconds * settings.speed.value);
				towardsDarker   = 1 - Mathf.Exp(-seconds * settings.speed.value * settings.speedDownFactor.value);
			}
			state.Time = now;

			var descriptor = cameraData.cameraTargetDescriptor;
			var width  = descriptor.width;
			var height = descriptor.height;
			var countX = Mathf.Max(1, Mathf.FloorToInt(width  * kRegionX / kStep));
			var countY = Mathf.Max(1, Mathf.FloorToInt(height * kRegionY / kStep));

			var source = resourceData.activeColorTexture;
			var destinationDesc = renderGraph.GetTextureDesc(source);
			destinationDesc.name              = "Source Exposure";
			destinationDesc.format            = GraphicsFormat.R16G16B16A16_SFloat;
			destinationDesc.enableRandomWrite = true;
			destinationDesc.msaaSamples       = MSAASamples.None;
			destinationDesc.clearBuffer       = false;
			var destination = renderGraph.CreateTexture(destinationDesc);
			var histogram   = renderGraph.CreateBuffer(new BufferDesc(kBins, sizeof(uint), GraphicsBuffer.Target.Structured) { name = "Source Exposure Histogram" });
			var exposure    = renderGraph.ImportBuffer(state.Exposure);

			using (var builder = renderGraph.AddComputePass<PassData>("Source Exposure", out var data, profilingSampler))
			{
				data.Shader          = m_Shader;
				data.Clear           = m_Clear;
				data.Histogram       = m_Histogram;
				data.Exposure        = m_ExposureKernel;
				data.Apply           = m_Apply;
				data.Source          = source;
				data.Destination     = destination;
				data.HistogramBuffer = histogram;
				data.ExposureBuffer  = exposure;
				data.Region  = new Vector4(Mathf.Floor(width * (1 - kRegionX) / 2), Mathf.Floor(height * (1 - kRegionY) / 2), kStep, kStep);
				data.Count   = new Vector4(countX, countY, width, height);
				data.Params  = new Vector4(settings.minimum.value, Mathf.Max(settings.minimum.value, settings.maximum.value), towardsBrighter, towardsDarker);
				data.GroupsX = (countX + 15) / 16;
				data.GroupsY = (countY + 15) / 16;
				data.ApplyX  = (width  + 7) / 8;
				data.ApplyY  = (height + 7) / 8;

				builder.UseTexture(source, AccessFlags.Read);
				builder.UseTexture(destination, AccessFlags.Write);
				builder.UseBuffer(histogram, AccessFlags.ReadWrite);
				builder.UseBuffer(exposure, AccessFlags.ReadWrite);
				builder.SetRenderFunc((PassData pass, ComputeGraphContext context) =>
				{
					var cmd = context.cmd;
					cmd.SetComputeBufferParam(pass.Shader, pass.Clear, s_Histogram, pass.HistogramBuffer);
					cmd.DispatchCompute(pass.Shader, pass.Clear, (kBins + 63) / 64, 1, 1);

					cmd.SetComputeVectorParam(pass.Shader, s_Region, pass.Region);
					cmd.SetComputeVectorParam(pass.Shader, s_Count, pass.Count);
					cmd.SetComputeVectorParam(pass.Shader, s_Params, pass.Params);
					cmd.SetComputeVectorParam(pass.Shader, s_Targets, s_TargetValues);

					cmd.SetComputeTextureParam(pass.Shader, pass.Histogram, s_Source, pass.Source);
					cmd.SetComputeBufferParam(pass.Shader, pass.Histogram, s_Histogram, pass.HistogramBuffer);
					cmd.DispatchCompute(pass.Shader, pass.Histogram, pass.GroupsX, pass.GroupsY, 1);

					cmd.SetComputeBufferParam(pass.Shader, pass.Exposure, s_Histogram, pass.HistogramBuffer);
					cmd.SetComputeBufferParam(pass.Shader, pass.Exposure, s_Exposure, pass.ExposureBuffer);
					cmd.DispatchCompute(pass.Shader, pass.Exposure, 1, 1, 1);

					cmd.SetComputeTextureParam(pass.Shader, pass.Apply, s_Source, pass.Source);
					cmd.SetComputeTextureParam(pass.Shader, pass.Apply, s_Destination, pass.Destination);
					cmd.SetComputeBufferParam(pass.Shader, pass.Apply, s_Exposure, pass.ExposureBuffer);
					cmd.DispatchCompute(pass.Shader, pass.Apply, pass.ApplyX, pass.ApplyY, 1);
				});
			}
			resourceData.cameraColor = destination;
		}

		CameraState GetState(Camera camera)
		{
			// Forget the cameras that are gone
			m_Removed.Clear();
			foreach (var pair in m_Cameras)
			{
				if (pair.Key == null)
					m_Removed.Add(pair.Key);
			}
			foreach (var gone in m_Removed)
			{
				m_Cameras[gone].Exposure?.Release();
				m_Cameras.Remove(gone);
			}

			if (!m_Cameras.TryGetValue(camera, out var state) || state == null)
			{
				// 0: no scale yet, the first frame goes straight to the frame's own
				var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float)) { name = "Source Exposure " + camera.name };
				buffer.SetData(new[] { 0.0f });
				m_Cameras[camera] = state = new CameraState { Exposure = buffer, Time = UnityEngine.Time.realtimeSinceStartup };
			}
			return state;
		}

		public void Dispose()
		{
			foreach (var state in m_Cameras.Values)
				state.Exposure?.Release();
			m_Cameras.Clear();
		}
	}
}
#endif
