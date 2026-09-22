#if UNITY_EDITOR && COM_UNITY_RENDER_PIPELINES_UNIVERSAL
using System.Collections.Generic;
using System.Linq;
using Chisel.Import.Source.VPKTools;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace AeternumGames.Chisel.Import.Source.ValveMapFormat2006
{
	/// <summary>
	/// The map's fog: env_fog_controller for the level, and sky_camera's own for the 3D skybox.
	/// </summary>
	/// <remarks>
	/// The values are read as the game reads them; what they mean is documented on <see cref="SourceFog"/>, and how
	/// they are rendered in Runtime/Shaders/SourceFog.shader.
	/// </remarks>
	public static class VmfFogControllers
	{
		public const string FogShaderPath = "Packages/com.aeternumgames.chisel.import.source/Runtime/Shaders/SourceFog.shader";

		// env_fog_controller's "Master" spawnflag (fogcontroller.h: SF_FOG_MASTER)
		const int kMasterSpawnFlag = 1;

		/// <summary>One controller's fog, in Unity's units and orientation.</summary>
		public readonly struct Fog
		{
			public readonly bool    Enabled, Blend;
			public readonly Color   Color, ColorSecondary;
			public readonly Vector3 Direction;
			public readonly float   Start, End, MaxDensity;

			public Fog(bool enabled, Color color, Color colorSecondary, bool blend, Vector3 direction,
					   float start, float end, float maxDensity)
			{
				Enabled = enabled; Color = color; ColorSecondary = colorSecondary; Blend = blend;
				Direction = direction; Start = start; End = end; MaxDensity = maxDensity;
			}
		}

		/// <summary>
		/// The controller the game would use. CFogSystem::LevelInitPostEntity takes the first env_fog_controller it
		/// finds, then lets ANY later one carrying the Master spawnflag replace it - so with several masters the LAST
		/// wins, and with none at all the first does. a map has three, two of them masters, and the last of those
		/// (c2a5a_fog_control) is the one whose numbers match its sky_camera's.
		/// </summary>
		public static VmfEntity Master(VmfWorld world)
		{
			VmfEntity master = null;
			foreach (var entity in world.Entities)
			{
				if (entity == null || !string.Equals(entity.ClassName, "env_fog_controller", System.StringComparison.OrdinalIgnoreCase))
					continue;
				if (master == null || (entity.GetInteger("spawnflags") & kMasterSpawnFlag) != 0)
					master = entity;
			}
			return master;
		}

		/// <summary>
		/// What an entity's fog keys say, with the distances in metres. <paramref name="scale"/> divides them, which is
		/// what the sky view does with sky_camera's (CSkyboxView::Enable3dSkyboxFog) because the miniature is that many
		/// times smaller; the level's fog passes 1. maxdensity is never scaled.
		/// </summary>
		public static Fog Read(VmfEntity entity, float scale = 1)
		{
			if (entity == null)
				return default;

			var color          = ColorOf(entity, "fogcolor",  new Color(1, 1, 1));
			var colorSecondary = ColorOf(entity, "fogcolor2", color);
			var blend   = entity.GetInteger("fogblend") != 0;
			var enabled = entity.GetInteger("fogenable") != 0;

			// fogdir, or the entity's own angles when use_angles is set. CFogController::Activate takes
			// AngleVectors(GetAbsAngles()) and NEGATES it, and CSkyCamera does the same for the sky's fog.
			Vector3 direction;
			if (entity.GetInteger("use_angles") != 0 && entity.TryGetProperty("angles", out VmfVector3 angles))
				direction = -(VmfEntityConverter.SkyTurnOf(angles) * Vector3.forward);
			else if (entity.TryGetProperty("fogdir", out VmfVector3 fogdir))
				direction = VmfWorldConverter.Swizzle(fogdir).normalized;
			else
				direction = Vector3.forward;

			var divide = Mathf.Max(scale, 1e-3f);
			var start  = entity.GetFloat("fogstart") * SourceEngineUnits.VmfInvMeters / divide;
			var end    = entity.GetFloat("fogend")   * SourceEngineUnits.VmfInvMeters / divide;

			// Source's default is 1, and a key of 0 means no fog at all rather than "unset"
			var maxDensity = entity.TryGetProperty("fogmaxdensity", out float density) ? Mathf.Clamp01(density) : 1.0f;

			return new Fog(enabled, color, colorSecondary, blend, direction, start, end, maxDensity);
		}

		/// <summary>fogcolor and fogcolor2 are "R G B" in 0..255, gamma encoded, and stay that way.</summary>
		static Color ColorOf(VmfEntity entity, string key, Color fallback)
		{
			if (!entity.TryGetProperty(key, out VmfVector3 value) || value == null)
				return fallback;
			return new Color(Mathf.Clamp01(value.X / 255.0f), Mathf.Clamp01(value.Y / 255.0f), Mathf.Clamp01(value.Z / 255.0f));
		}

		/// <summary>Writes a controller's fog onto the map's volume profile.</summary>
		public static void Apply(in Fog fog, SourceFog settings)
		{
			settings.enabled.Override(fog.Enabled);
			settings.color.Override(fog.Color);
			settings.colorSecondary.Override(fog.ColorSecondary);
			settings.blend.Override(fog.Blend);
			settings.direction.Override(fog.Direction);
			settings.start.Override(fog.Start);
			settings.end.Override(fog.End);
			settings.maxDensity.Override(fog.MaxDensity);
		}

		/// <summary>
		/// Adds <see cref="SourceFogFeature"/> to each renderer of the active URP asset that lacks it, the way
		/// <see cref="VmfPostProcessingConverter.EnsureExposureFeature"/> adds its own.
		/// </summary>
		/// <returns>What was added, or null when nothing was.</returns>
		public static string EnsureFogFeature()
		{
			var pipeline = UniversalRenderPipeline.asset;
			if (pipeline == null)
				return null;
			var shader = AssetDatabase.LoadAssetAtPath<Shader>(FogShaderPath);
			if (shader == null)
			{
				Debug.LogWarning($"Source fog: {FogShaderPath} is missing, the maps won't be fogged.");
				return null;
			}

			var added = new List<string>();
			foreach (var rendererData in pipeline.rendererDataList)
			{
				if (rendererData == null)
					continue;
				var existing = rendererData.rendererFeatures.OfType<SourceFogFeature>().FirstOrDefault();
				if (existing != null)
				{
					if (existing.Shader == null)
					{
						existing.Shader = shader;
						EditorUtility.SetDirty(existing);
					}
					continue;
				}

				var feature = ScriptableObject.CreateInstance<SourceFogFeature>();
				feature.name   = "Source Fog";
				feature.Shader = shader;
				AssetDatabase.AddObjectToAsset(feature, rendererData);
				AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);

				// The feature list and its map of local ids, as ScriptableRendererDataEditor keeps them
				var serialized = new SerializedObject(rendererData);
				var features = serialized.FindProperty("m_RendererFeatures");
				var map      = serialized.FindProperty("m_RendererFeatureMap");
				features.arraySize++;
				features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = feature;
				map.arraySize++;
				map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
				serialized.ApplyModifiedProperties();
				rendererData.SetDirty();
				EditorUtility.SetDirty(rendererData);
				added.Add(rendererData.name);
			}
			if (added.Count == 0)
				return null;
			AssetDatabase.SaveAssets();
			return "added the Source Fog renderer feature to " + string.Join(", ", added);
		}
	}
}
#endif
