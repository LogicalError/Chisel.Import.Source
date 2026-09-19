#if COM_UNITY_RENDER_PIPELINES_UNIVERSAL
using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace AeternumGames.Chisel.Import.Source
{
	/// <summary>
	/// Source's automatic exposure, which URP doesn't have: the frame is scaled so that the pixel brighter than all
	/// but 2% of the others lands on 60% luminance, gamma encoded, within the map's range. Rendered by
	/// <see cref="SourceExposureFeature"/>, before bloom as in Source.
	/// </summary>
	/// <remarks>
	/// game/client/viewpostprocess.cpp: CLuminanceHistogramSystem measures the gamma-encoded frame before bloom
	/// over its center 90% × 85% (mat_exposure_center_region_x/y), GetTargetTonemapScalar puts the brightest 2%
	/// (mat_tonemap_percent_bright_pixels) at 60% (mat_tonemap_percent_target) and the median at 3% at least
	/// (mat_tonemap_min_avglum), keeps the scale while the brightest 2% land in the target's own histogram bin, and
	/// the scale is held in [min, max] (GetExposureRange). How fast it moves there lives in the closed material
	/// system; only its "adjust down faster" factor, mat_accelerate_adjust_exposure_down 3, is known.
	/// </remarks>
	[Serializable, VolumeComponentMenu("Post-processing/Chisel/Source Exposure")]
	[SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))]
	public sealed class SourceExposure : VolumeComponent, IPostProcessComponent
	{
		[Tooltip("Scale the frame the way Source's automatic exposure does.")]
		public BoolParameter enabled = new BoolParameter(false);

		[Tooltip("The smallest scale: mat_autoexposure_min, or the map's SetAutoExposureMin.")]
		public MinFloatParameter minimum = new MinFloatParameter(0.5f, 0.001f);

		[Tooltip("The largest scale: mat_autoexposure_max, or the map's SetAutoExposureMax.")]
		public MinFloatParameter maximum = new MinFloatParameter(2.0f, 0.001f);

		[Tooltip("How quickly the scale follows a brighter frame down and a darker frame up, per second, in play mode. Outside play mode it follows at once.")]
		public MinFloatParameter speed = new MinFloatParameter(1.0f, 0.0f);

		[Tooltip("How much quicker it follows when the frame gets brighter (mat_accelerate_adjust_exposure_down).")]
		public MinFloatParameter speedDownFactor = new MinFloatParameter(3.0f, 0.0f);

		public bool IsActive() => enabled.value;
	}
}
#endif
