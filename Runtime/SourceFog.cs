#if COM_UNITY_RENDER_PIPELINES_UNIVERSAL
using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace AeternumGames.Chisel.Import.Source
{
	/// <summary>
	/// Source's fog, from the map's master env_fog_controller. Linear between two distances, and capped: it is the cap
	/// that URP's own fog cannot do. Rendered by <see cref="SourceFogFeature"/>.
	/// </summary>
	/// <remarks>
	/// game/client/viewrender.cpp: CViewRender::EnableWorldFog sets MATERIAL_FOG_LINEAR with GetFogStart/GetFogEnd/
	/// GetFogMaxDensity, so the fog is saturate((distance - start) / (end - start)) * maxdensity. GetFogColor blends
	/// colorPrimary and colorSecondary by the view direction when <c>fogblend</c> is set - 0.5 * dot(forward,
	/// dirPrimary) + 0.5, with dirPrimary from <c>fogdir</c>, or from the entity's angles when <c>use_angles</c> is set
	/// (CFogController::Activate, negated) - and does it on the 0..255 values, in gamma space.
	/// <para>
	/// Which controller: CFogSystem::LevelInitPostEntity walks the entity list, takes the first as master, then lets
	/// ANY later one with SF_FOG_MASTER (spawnflags 1) replace it, so with several masters the last one wins.
	/// </para>
	/// </remarks>
	[Serializable, VolumeComponentMenu("Post-processing/Chisel/Source Fog")]
	[SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))]
	public sealed class SourceFog : VolumeComponent, IPostProcessComponent
	{
		[Tooltip("Fog the view the way Source's env_fog_controller does.")]
		public BoolParameter enabled = new BoolParameter(false);

		[Tooltip("fogcolor, as Source holds it: the blend and the fog itself are done on the gamma-encoded value.")]
		public ColorParameter color = new ColorParameter(new Color(0.725f, 0.906f, 1.0f), hdr: false, showAlpha: false, showEyeDropper: true);

		[Tooltip("fogcolor2: what the fog looks like facing away from the direction below. Only used when Blend is on.")]
		public ColorParameter colorSecondary = new ColorParameter(new Color(0.592f, 0.855f, 1.0f), hdr: false, showAlpha: false, showEyeDropper: true);

		[Tooltip("fogblend: blend the two colours by where the view is looking. Off in most maps, and then only the first colour is used.")]
		public BoolParameter blend = new BoolParameter(false);

		[Tooltip("fogdir, or the angles when use_angles is set: the direction the first colour is seen along.")]
		public Vector3Parameter direction = new Vector3Parameter(Vector3.forward);

		[Tooltip("fogstart, in metres: nothing nearer than this is fogged at all.")]
		public MinFloatParameter start = new MinFloatParameter(58.56f, 0.0f);

		[Tooltip("fogend, in metres: the distance at which the fog reaches its cap.")]
		public MinFloatParameter end = new MinFloatParameter(274.5f, 0.0f);

		[Tooltip("fogmaxdensity: how much of the view the fog may take at its thickest. Source's default is 1; bm_c2a5a uses 0.5.")]
		public ClampedFloatParameter maxDensity = new ClampedFloatParameter(1.0f, 0.0f, 1.0f);

		public bool IsActive() => enabled.value && maxDensity.value > 0 && end.value > start.value;
	}
}
#endif
