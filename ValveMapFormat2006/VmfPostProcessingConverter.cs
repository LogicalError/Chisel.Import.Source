using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

#if COM_UNITY_RENDER_PIPELINES_UNIVERSAL
using Chisel.Import.Source.VPKTools;

using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
#endif

namespace AeternumGames.Chisel.Import.Source.ValveMapFormat2006
{
	/// <summary>
	/// Recreates the look Source's HDR post processing gives a map - exposure, bloom and color
	/// correction - with the Universal Render Pipeline's post processing volumes.
	/// </summary>
	/// <remarks>
	/// Only the state a map spawns in: the inputs its logic_autos send on OnMapSpawn, and the color
	/// corrections that are enabled everywhere from the start. What triggers and relays change further
	/// into the map is not reproduced.
	/// </remarks>
	public static class VmfPostProcessingConverter
	{
		#region Spawn settings

		// The engine's own defaults (viewpostprocess.cpp): mat_autoexposure_min "0.5",
		// mat_autoexposure_max "2" and mat_bloomscale "1".
		public const float DefaultAutoExposureMin = 0.5f;
		public const float DefaultAutoExposureMax = 2.0f;
		public const float DefaultBloomScale      = 1.0f;

		/// <summary>A color correction lookup and how strongly it applies.</summary>
		public sealed class ColorCorrection
		{
			/// <summary>The lookup's file in the game, e.g. "materials/colorcorrection/c0a0.raw".</summary>
			public string Filename;
			public float  Weight;
		}

		/// <summary>Source's post processing as a map spawns.</summary>
		public sealed class SpawnSettings
		{
			/// <summary>The range the auto exposure scale is held in.</summary>
			public float AutoExposureMin = DefaultAutoExposureMin;
			public float AutoExposureMax = DefaultAutoExposureMax;
			public float BloomScale      = DefaultBloomScale;

			/// <summary>The color corrections that apply everywhere, in the order the map lists them.</summary>
			public readonly List<ColorCorrection> ColorCorrections = new List<ColorCorrection>();
		}

		// What an env_tonemap_controller networks to the client (its SendTable in env_tonemap_controller.cpp).
		sealed class TonemapController
		{
			public bool  UseAutoExposureMin, UseAutoExposureMax, UseBloomScale;
			public float AutoExposureMin, AutoExposureMax, BloomScale;

			public float LastChange = float.NegativeInfinity;
			public int   Index;
		}

		/// <summary>
		/// Works out what Source's post processing does to a map as it spawns.
		/// </summary>
		public static SpawnSettings GetSpawnSettings(VmfWorld world)
		{
			var settings = new SpawnSettings();
			var spawnInputs = GetSpawnInputs(world);

			// Whenever any of a controller's state changes, the client copies ALL of it into the globals
			// the tonemapper reads (C_EnvTonemapController::OnDataChanged). With several controllers
			// the last one to change is therefore the one in use - and of the ones that change in the
			// same update, the one the client gets to last, which is the one the map lists last.
			var controllers = new Dictionary<VmfEntity, TonemapController>();
			foreach (var (input, targets) in spawnInputs)
			{
				foreach (var target in targets)
				{
					if (target.ClassName != "env_tonemap_controller")
						continue;
					if (!controllers.TryGetValue(target, out var controller) || controller == null)
						controllers[target] = controller = new TonemapController { Index = world.Entities.IndexOf(target) };

					var value = ParseFloat(input.Parameter);
					switch (input.Input.ToLowerInvariant())
					{
						case "setautoexposuremin":		controller.AutoExposureMin = value; controller.UseAutoExposureMin = true; break;
						case "setautoexposuremax":		controller.AutoExposureMax = value; controller.UseAutoExposureMax = true; break;
						case "setbloomscale":			controller.BloomScale      = value; controller.UseBloomScale      = true; break;
						case "usedefaultautoexposure":	controller.UseAutoExposureMin = controller.UseAutoExposureMax = false; break;
						case "usedefaultbloomscale":	controller.UseBloomScale = false; break;
						default: continue;
					}
					controller.LastChange = input.Delay;
				}
			}

			TonemapController inUse = null;
			foreach (var controller in controllers.Values)
			{
				if (inUse == null || controller.LastChange > inUse.LastChange ||
					(controller.LastChange == inUse.LastChange && controller.Index > inUse.Index))
					inUse = controller;
			}

			if (inUse != null)
			{
				// GetExposureRange only takes a custom limit that is above zero.
				if (inUse.UseAutoExposureMin && inUse.AutoExposureMin > 0) settings.AutoExposureMin = inUse.AutoExposureMin;
				if (inUse.UseAutoExposureMax && inUse.AutoExposureMax > 0) settings.AutoExposureMax = inUse.AutoExposureMax;
				if (inUse.UseBloomScale) settings.BloomScale = inUse.BloomScale;
			}
			// ...and raises the maximum to the minimum when they cross.
			if (settings.AutoExposureMin > settings.AutoExposureMax)
				settings.AutoExposureMax = settings.AutoExposureMin;

			foreach (var entity in world.Entities)
			{
				// Hidden in Hammer means left out of the compile.
				if (entity.IsHidden || entity.ClassName != "color_correction")
					continue;

				var enabled = !(entity.TryGetProperty("StartDisabled", out float startDisabled) && startDisabled != 0);
				foreach (var (input, targets) in spawnInputs)
				{
					if (!targets.Contains(entity))
						continue;
					if (string.Equals(input.Input, "Enable", StringComparison.OrdinalIgnoreCase))
						enabled = true;
					else if (string.Equals(input.Input, "Disable", StringComparison.OrdinalIgnoreCase))
						enabled = false;
				}
				if (!enabled)
					continue;

				// C_ColorCorrection::ClientThink only fades a lookup with the player's distance when it
				// has a real falloff range; without one it applies at full weight everywhere.
				entity.TryGetProperty("minfalloff", out float minFalloff);
				entity.TryGetProperty("maxfalloff", out float maxFalloff);
				if (minFalloff != -1 && maxFalloff != -1 && minFalloff != maxFalloff)
					continue;

				var filename = GetString(entity, "filename");
				if (string.IsNullOrEmpty(filename))
					continue;

				settings.ColorCorrections.Add(new ColorCorrection
				{
					Filename = filename,
					Weight   = entity.TryGetProperty("maxweight", out float maxWeight) ? maxWeight : 1.0f
				});
			}
			return settings;
		}

		// The inputs a map sends as it spawns - the OnMapSpawn outputs of its logic_autos - each with the
		// entities it reaches, in the order they arrive.
		static List<(VmfConnection Input, List<VmfEntity> Targets)> GetSpawnInputs(VmfWorld world)
		{
			var inputs = new List<(VmfConnection Input, int Order)>();
			foreach (var entity in world.Entities)
			{
				if (entity.IsHidden || entity.ClassName != "logic_auto")
					continue;

				// An output keeps its connections head first (CBaseEntityOutput::AddEventAction), so it
				// fires them in the reverse of the order the map lists them in.
				for (int i = entity.Connections.Count - 1; i >= 0; i--)
				{
					var connection = entity.Connections[i];
					if (string.Equals(connection.Output, "OnMapSpawn", StringComparison.OrdinalIgnoreCase))
						inputs.Add((connection, inputs.Count));
				}
			}

			return inputs.OrderBy(item => item.Input.Delay)
						 .ThenBy(item => item.Order)
						 .Select(item => (item.Input, FindTargets(world, item.Input.Target)))
						 .ToList();
		}

		// CEventQueue::ServiceEvents looks a target up by name, where a trailing '*' matches any ending,
		// and only falls back to the class name when no entity has that name.
		static List<VmfEntity> FindTargets(VmfWorld world, string target)
		{
			var targets = new List<VmfEntity>();
			if (string.IsNullOrEmpty(target))
				return targets;

			var prefix = target.EndsWith("*") ? target.Substring(0, target.Length - 1) : null;
			foreach (var entity in world.Entities)
			{
				if (entity.IsHidden)
					continue;
				var name = GetString(entity, "targetname");
				if (string.IsNullOrEmpty(name))
					continue;
				if ((prefix != null) ? name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
									 : string.Equals(name, target, StringComparison.OrdinalIgnoreCase))
					targets.Add(entity);
			}

			if (targets.Count == 0)
			{
				foreach (var entity in world.Entities)
				{
					if (!entity.IsHidden && string.Equals(entity.ClassName, target, StringComparison.OrdinalIgnoreCase))
						targets.Add(entity);
				}
			}
			return targets;
		}

		// The parser types a value by what it looks like, so a name made of digits arrives as a number.
		static string GetString(VmfEntity entity, string key)
		{
			return entity.Properties.TryGetValue(key, out var value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
		}

		static readonly Regex s_LeadingNumber = new Regex(@"^\s*[-+]?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?");

		// An input's parameter is read with atof (variant_t::Float): the leading number, or 0.
		static float ParseFloat(string text)
		{
			var match = s_LeadingNumber.Match(text ?? "");
			return (match.Success && float.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) ? value : 0;
		}

		#endregion

		#region Color correction lookup

		/// <summary>Edge length of Source's .raw color correction lookups.</summary>
		public const int SourceLookupSize = 32;

		/// <summary>
		/// Combines Source color correction lookups into the one lookup the Universal Render Pipeline
		/// grades with, and works out how much of it applies.
		/// </summary>
		/// <remarks>
		/// URP reads its lookup the way Source does (Engine_Post_ps2x.fxc): with the clipped color, gamma
		/// encoded, on a lattice that runs from 0 to 1 inclusive, and its result is gamma encoded too.
		/// A lookup as large as Source's therefore holds Source's own values. A lookup of any other size
		/// samples Source's lattice the way the game filters between its points.
		/// Source adds the results of its lookups to the uncorrected image by weight, where URP blends a
		/// single lookup with the image by its contribution. The two agree when that lookup is the
		/// weighted average of Source's, and the contribution is their total weight (at most 1).
		/// </remarks>
		/// <param name="sourceLookups">Each lookup's .raw file: <see cref="SourceLookupSize"/>³ RGB bytes, red varying fastest.</param>
		/// <param name="weights">How strongly each lookup applies.</param>
		/// <param name="size">Edge length of the lookup to build: the pipeline's color grading lookup size.</param>
		/// <param name="contribution">How much of the lookup applies, from 0 to 1.</param>
		/// <returns>
		/// The lookup in URP's strip layout, <paramref name="size"/>² wide and <paramref name="size"/>
		/// high: one block of <paramref name="size"/> columns per blue step, red running across a block
		/// and green running up. Gamma-encoded RGB triplets, row by row from the bottom. Null when a
		/// lookup is not a .raw lookup, or when no lookup has any weight.
		/// </returns>
		public static float[] BuildLookupStrip(IReadOnlyList<byte[]> sourceLookups, IReadOnlyList<float> weights, int size, out float contribution)
		{
			contribution = 0;
			var count = sourceLookups.Count;
			for (int i = 0; i < count; i++)
			{
				if (sourceLookups[i] == null || sourceLookups[i].Length != SourceLookupSize * SourceLookupSize * SourceLookupSize * 3)
					return null;
			}

			// The weights come from the closed-source material system, which is taken to share the
			// whole image out when they add up to more than one, and otherwise to leave the rest
			// uncorrected - that only matters to the few maps with several global lookups.
			var total = 0.0;
			for (int i = 0; i < count; i++)
				total += Math.Max(0, weights[i]);
			if (total <= 0)
				return null;
			contribution = (float)Math.Min(1, total);

			// Where each lattice point of the new lookup falls on Source's lattice. For a lookup of
			// Source's own size these are whole numbers, so the samples below are exact.
			var positions = new double[size];
			for (int i = 0; i < size; i++)
				positions[i] = i * (double)(SourceLookupSize - 1) / (size - 1);

			var width  = size * size;
			var result = new float[width * size * 3];
			for (int blue = 0; blue < size; blue++)
			{
				for (int green = 0; green < size; green++)
				{
					for (int red = 0; red < size; red++)
					{
						double sumRed = 0, sumGreen = 0, sumBlue = 0;
						for (int i = 0; i < count; i++)
						{
							var weight = Math.Max(0, weights[i]) / total;
							if (weight <= 0)
								continue;
							SampleSourceLookup(sourceLookups[i], positions[red], positions[green], positions[blue], out var lookupRed, out var lookupGreen, out var lookupBlue);
							sumRed   += lookupRed   * weight;
							sumGreen += lookupGreen * weight;
							sumBlue  += lookupBlue  * weight;
						}
						var o = (green * width + blue * size + red) * 3;
						result[o    ] = (float)sumRed;
						result[o + 1] = (float)sumGreen;
						result[o + 2] = (float)sumBlue;
					}
				}
			}
			return result;
		}

		// Source samples its lookup at color * 31/32 + 0.5/32 (Engine_Post_ps2x.fxc), half a texel in from
		// either edge, so the lattice points sit on 0, 1/31, ... 1 and filtering between them is plain
		// trilinear interpolation. The positions are in lattice steps, 0 to 31.
		static void SampleSourceLookup(byte[] lookup, double red, double green, double blue, out double outRed, out double outGreen, out double outBlue)
		{
			const int n = SourceLookupSize;
			ToLattice(red,   out var r0, out var tr);
			ToLattice(green, out var g0, out var tg);
			ToLattice(blue,  out var b0, out var tb);

			outRed = outGreen = outBlue = 0;
			for (int corner = 0; corner < 8; corner++)
			{
				int dr = corner & 1, dg = (corner >> 1) & 1, db = (corner >> 2) & 1;
				var weight = (dr == 1 ? tr : 1 - tr) * (dg == 1 ? tg : 1 - tg) * (db == 1 ? tb : 1 - tb);
				if (weight == 0)
					continue;
				var index = ((r0 + dr) + n * ((g0 + dg) + n * (b0 + db))) * 3;
				outRed   += lookup[index    ] * weight;
				outGreen += lookup[index + 1] * weight;
				outBlue  += lookup[index + 2] * weight;
			}
			outRed /= 255; outGreen /= 255; outBlue /= 255;
		}

		static void ToLattice(double position, out int index, out double fraction)
		{
			position = Math.Min(SourceLookupSize - 1, Math.Max(0, position));
			index    = Math.Min((int)position, SourceLookupSize - 2);
			fraction = position - index;
		}

		#endregion

#if COM_UNITY_RENDER_PIPELINES_UNIVERSAL
		#region Universal Render Pipeline

		/// <summary>The object that holds the map's post processing volume.</summary>
		public const string VolumeObjectName = "Post Processing";

		/// <summary>Where bloom starts, gamma encoded as URP's threshold is.</summary>
		/// <remarks>
		/// Black Mesa's own bloom starts on fairly dim pixels (r_bloom_nextgen_threshold 0.1 in its
		/// skill.cfg), and a threshold of 1 left a map's lamps without the halos they have in game.
		/// 0.5 was picked against a game screenshot of that station.
		/// </remarks>
		public const float BloomThreshold = 0.5f;

		/// <summary>URP's bloom intensity for each unit of the map's bloom scale.</summary>
		/// <remarks>
		/// The station's look was picked under Post Processing v2, which turned the map's bloom scale of
		/// 0.7 into 2^(0.7 / 10) - 1 = 0.05 for each of the six levels of its bloom pyramid and added the
		/// levels up, 0.3 in all. URP blends its levels into one instead, so the same bloom needs the
		/// whole 0.3 as its intensity. Source's own bloom is linear in the scale.
		/// </remarks>
		public const float BloomIntensityPerScale = 0.43f;

		/// <summary>
		/// Gives an imported map the post processing it has in game: a global volume with the map's
		/// exposure, bloom and color correction, rendered by the scene's cameras.
		/// </summary>
		/// <param name="parent">The imported map's root, which the volume is put under; null for the scene root.</param>
		/// <param name="mapName">Names the profile asset, which a re-import updates in place.</param>
		public static void Import(GameResources gameResources, Transform parent, VmfWorld world, string mapName)
		{
			var spawnSettings = GetSpawnSettings(world);
			var profile = GetOrCreateProfile(mapName);

			// Source's automatic exposure, in the map's range. URP has none of its own, so the importer's
			// renderer feature does it, before bloom as in Source.
			var featureChanges = EnsureExposureFeature();
			if (featureChanges != null)
				Debug.Log("Source map import (post processing): " + featureChanges);
			var exposure = GetOrAdd<SourceExposure>(profile);
			exposure.enabled.Override(true);
			exposure.minimum.Override(spawnSettings.AutoExposureMin);
			exposure.maximum.Override(spawnSettings.AutoExposureMax);

			// The map's own fog, from the env_fog_controller the game would use. URP's fog has no maximum density,
			// which is the one number that decides how much of the distance survives, so the importer's feature
			// renders it (VmfFogControllers, SourceFog).
			var fogFeatureChanges = VmfFogControllers.EnsureFogFeature();
			if (fogFeatureChanges != null)
				Debug.Log("Source map import (post processing): " + fogFeatureChanges);
			var fogController = VmfFogControllers.Master(world);
			var fog = VmfFogControllers.Read(fogController);
			VmfFogControllers.Apply(in fog, GetOrAdd<SourceFog>(profile));
			if (fogController != null)
				Debug.Log($"Source map import (fog): {fogController.GetText("targetname")} - " +
						  (fog.Enabled ? $"{fog.Start:0.#} m to {fog.End:0.#} m, at most {fog.MaxDensity:0.##} dense" : "off"));

			// A fixed exposure from before it
			if (profile.TryGet(out ColorAdjustments colorAdjustments))
			{
				profile.Remove<ColorAdjustments>();
				UnityEngine.Object.DestroyImmediate(colorAdjustments, true);
			}

			var bloom = GetOrAdd<Bloom>(profile);
			bloom.threshold.Override(BloomThreshold);
			bloom.intensity.Override(BloomIntensityPerScale * spawnSettings.BloomScale);

			// Source has no tone curve, the frame simply clips.
			var tonemapping = GetOrAdd<Tonemapping>(profile);
			tonemapping.mode.Override(TonemappingMode.None);

			// The lookup is the whole grade.
			var lookup = BuildLookupTexture(gameResources, profile, spawnSettings.ColorCorrections, mapName, out var contribution);
			var colorLookup = GetOrAdd<ColorLookup>(profile);
			colorLookup.texture.Override(lookup);
			colorLookup.contribution.Override(lookup != null ? contribution : 0);

			EditorUtility.SetDirty(profile);
			AssetDatabase.SaveAssets();

			var volume = GetOrCreateVolume(parent, profile);
			RenderPostProcessingOnCameras(volume.gameObject.scene, volume.gameObject.layer);

			Debug.Log($"{mapName} post processing: auto exposure {spawnSettings.AutoExposureMin}-{spawnSettings.AutoExposureMax}, " +
					  $"bloom {spawnSettings.BloomScale}, color correction " +
					  (spawnSettings.ColorCorrections.Count == 0 ? "none" : string.Join(", ", spawnSettings.ColorCorrections.Select(c => $"{c.Filename} x{c.Weight}"))));
		}

		/// <summary>Where the importer keeps the shader <see cref="SourceExposureFeature"/> renders with.</summary>
		public const string ExposureShaderPath = "Packages/com.aeternumgames.chisel.import.source/Runtime/Shaders/SourceExposure.compute";

		/// <summary>
		/// Adds <see cref="SourceExposureFeature"/> to each renderer of the active URP asset that lacks it, the way
		/// the renderer's inspector adds a feature. It does nothing for a camera whose volumes don't turn it on.
		/// </summary>
		/// <returns>What was added, or null when nothing was.</returns>
		public static string EnsureExposureFeature()
		{
			var pipeline = UniversalRenderPipeline.asset;
			if (pipeline == null)
				return null;
			var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ExposureShaderPath);
			if (shader == null)
			{
				Debug.LogWarning($"Source exposure: {ExposureShaderPath} is missing, the maps won't be exposed.");
				return null;
			}

			var added = new List<string>();
			foreach (var rendererData in pipeline.rendererDataList)
			{
				if (rendererData == null)
					continue;
				var existing = rendererData.rendererFeatures.OfType<SourceExposureFeature>().FirstOrDefault();
				if (existing != null)
				{
					if (existing.Shader == null)
					{
						existing.Shader = shader;
						EditorUtility.SetDirty(existing);
					}
					continue;
				}

				var feature = ScriptableObject.CreateInstance<SourceExposureFeature>();
				feature.name   = "Source Exposure";
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
			return "added the Source Exposure renderer feature to " + string.Join(", ", added);
		}

#if COM_UNITY_POSTPROCESSING
		/// <summary>
		/// Converts the Post Processing v2 volume left by an older import without requiring the original
		/// VMF. The old profile asset is kept as a fallback; the converted profile is saved beside it.
		/// </summary>
		public static bool MigrateLegacyProfile(Transform parent, string mapName)
		{
			GameObject volumeObject = null;
			if (parent != null)
			{
				var child = parent.Find(VolumeObjectName);
				if (child != null)
					volumeObject = child.gameObject;
			}
			else
			{
				volumeObject = SceneManager.GetActiveScene().GetRootGameObjects()
					.FirstOrDefault(root => root.name == VolumeObjectName);
			}
			var legacyVolume = volumeObject != null
				? volumeObject.GetComponent<UnityEngine.Rendering.PostProcessing.PostProcessVolume>() : null;
			var legacyProfile = legacyVolume != null ? legacyVolume.sharedProfile : null;
			if (legacyProfile == null)
				return false;

			// The v2 profile kept the map's range as the luminance its key of 0.6 was held between: [0.6 / max, 0.6 / min]
			float autoExposureMin = DefaultAutoExposureMin, autoExposureMax = DefaultAutoExposureMax;
			if (legacyProfile.TryGetSettings<UnityEngine.Rendering.PostProcessing.AutoExposure>(out var autoExposure) &&
				autoExposure != null && autoExposure.active)
			{
				autoExposureMax = 0.6f / Mathf.Pow(2, autoExposure.minLuminance.value);
				autoExposureMin = 0.6f / Mathf.Pow(2, autoExposure.maxLuminance.value);
			}

			var bloomScale = 0.0f;
			var bloomThreshold = BloomThreshold;
			if (legacyProfile.TryGetSettings<UnityEngine.Rendering.PostProcessing.Bloom>(out var legacyBloom) &&
				legacyBloom != null && legacyBloom.active)
			{
				bloomScale = legacyBloom.intensity.value;
				bloomThreshold = legacyBloom.threshold.value;
			}

			var destinationPath = PackagePath.GetOutputPath($"postprocessing/{mapName}_URP.asset");
			var profile = UnityAssets.Load<VolumeProfile>(destinationPath);
			if (profile == null)
			{
				profile = ScriptableObject.CreateInstance<VolumeProfile>();
				UnityAssets.Save(profile, destinationPath);
			}

			var featureChanges = EnsureExposureFeature();
			if (featureChanges != null)
				Debug.Log("Source map post processing: " + featureChanges);
			var exposure = GetOrAdd<SourceExposure>(profile);
			exposure.enabled.Override(true);
			exposure.minimum.Override(autoExposureMin);
			exposure.maximum.Override(autoExposureMax);
			if (profile.TryGet(out ColorAdjustments colorAdjustments))
			{
				profile.Remove<ColorAdjustments>();
				UnityEngine.Object.DestroyImmediate(colorAdjustments, true);
			}
			var bloom = GetOrAdd<Bloom>(profile);
			bloom.threshold.Override(bloomThreshold);
			bloom.intensity.Override(BloomIntensityPerScale * bloomScale);
			GetOrAdd<Tonemapping>(profile).mode.Override(TonemappingMode.None);

			var colorLookup = GetOrAdd<ColorLookup>(profile);
			// PPv2's external Texture3D is in its internal grading space, not the strip-LUT space URP
			// consumes. Copying it directly crushes dark and middle values. A fresh VMF conversion uses
			// BuildLookupTexture and retains the original Source .raw lookup; without the VMF, leaving the
			// old lookup out is the only faithful fallback.
			// Keep a Source strip that may already have been rebuilt into this URP profile separately.
			if (colorLookup.texture.value == null)
			{
				colorLookup.texture.Override(null);
				colorLookup.contribution.Override(0);
			}

			EditorUtility.SetDirty(profile);
			AssetDatabase.SaveAssets();
			var volume = GetOrCreateVolume(parent, profile);
			RenderPostProcessingOnCameras(volume.gameObject.scene, volume.gameObject.layer);
			Debug.Log($"{mapName} legacy post processing migrated to URP: auto exposure {autoExposureMin}-{autoExposureMax}, bloom {bloomScale}. " +
					  "The PPv2-baked color LUT was omitted; re-import from the VMF to rebuild it from Source data.");
			return true;
		}
#endif

		static VolumeProfile GetOrCreateProfile(string mapName)
		{
			var destinationPath = PackagePath.GetOutputPath($"postprocessing/{mapName}.asset");
			var profile = UnityAssets.Load<VolumeProfile>(destinationPath);
			if (profile != null)
				return profile;

			// A map imported before the switch to URP has a Post Processing v2 profile there.
			if (System.IO.File.Exists(destinationPath))
				AssetDatabase.DeleteAsset(UnityAssets.GetAssetPath(destinationPath).Replace(System.IO.Path.DirectorySeparatorChar, '/'));
			profile = ScriptableObject.CreateInstance<VolumeProfile>();
			UnityAssets.Save(profile, destinationPath);
			return profile;
		}

		static T GetOrAdd<T>(VolumeProfile profile) where T : VolumeComponent
		{
			// Components are stored inside the profile asset, the way the profile inspector adds them.
			if (!profile.TryGet(out T component))
				component = UnityEditor.Rendering.VolumeProfileFactory.CreateVolumeComponent<T>(profile, overrides: false, saveAsset: false);
			component.active = true;
			return component;
		}

		static Texture2D BuildLookupTexture(GameResources gameResources, VolumeProfile profile, List<ColorCorrection> colorCorrections, string mapName, out float contribution)
		{
			contribution = 0;
			var lookups = new List<byte[]>();
			var weights = new List<float>();
			foreach (var colorCorrection in colorCorrections)
			{
				var bytes = gameResources.LoadFileAsBytes(colorCorrection.Filename);
				if (bytes == null)
				{
					Debug.LogWarning($"{mapName}: color correction lookup {colorCorrection.Filename} not found in the game files, left out.");
					continue;
				}
				lookups.Add(bytes);
				weights.Add(colorCorrection.Weight);
			}

			// URP only grades with a lookup of the size its pipeline asset asks for.
			var pipeline = UniversalRenderPipeline.asset;
			var size = (pipeline != null) ? pipeline.colorGradingLutSize : SourceLookupSize;
			var pixels = (lookups.Count > 0) ? BuildLookupStrip(lookups, weights, size, out contribution) : null;
			if (lookups.Count > 0 && pixels == null && weights.Any(weight => weight > 0))
				Debug.LogWarning($"{mapName}: a color correction lookup is not a {SourceLookupSize}³ .raw lookup, color correction left out.");

			// The lookup lives inside the profile asset, so a re-import rewrites it in place.
			var texture = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(profile)).OfType<Texture2D>().FirstOrDefault();
			if (texture != null && (pixels == null || texture.width != size * size || texture.height != size))
			{
				AssetDatabase.RemoveObjectFromAsset(texture);
				UnityEngine.Object.DestroyImmediate(texture, true);
				texture = null;
			}
			if (pixels == null)
				return null;

			if (texture == null)
			{
				// URP refuses an sRGB lookup: the values are gamma encoded already.
				texture = new Texture2D(size * size, size, TextureFormat.RGBAHalf, false, true);
				AssetDatabase.AddObjectToAsset(texture, profile);
			}
			texture.name       = $"{mapName} Color Correction";
			texture.wrapMode   = TextureWrapMode.Clamp;
			texture.filterMode = FilterMode.Bilinear;
			texture.anisoLevel = 0;

			var colors = new Color[pixels.Length / 3];
			for (int i = 0; i < colors.Length; i++)
				colors[i] = new Color(pixels[i * 3], pixels[i * 3 + 1], pixels[i * 3 + 2], 1);
			texture.SetPixels(colors);
			texture.Apply(false);
			EditorUtility.SetDirty(texture);
			return texture;
		}

		static Volume GetOrCreateVolume(Transform parent, VolumeProfile profile)
		{
			GameObject volumeObject = null;
			if (parent != null)
			{
				var existing = parent.Find(VolumeObjectName);
				if (existing != null)
					volumeObject = existing.gameObject;
			}
			else
			{
				volumeObject = SceneManager.GetActiveScene().GetRootGameObjects().FirstOrDefault(root => root.name == VolumeObjectName);
			}

			if (volumeObject == null)
			{
				volumeObject = new GameObject(VolumeObjectName);
				if (parent != null)
				{
					volumeObject.transform.SetParent(parent, false);
					volumeObject.layer = parent.gameObject.layer;
				}
			}
			RemoveBuiltInPostProcessing(volumeObject);

			if (!volumeObject.TryGetComponent(out Volume volume))
				volume = volumeObject.AddComponent<Volume>();
			volume.isGlobal      = true;
			volume.sharedProfile = profile;
			EditorUtility.SetDirty(volume);
			return volume;
		}

		// URP only renders a volume for a camera that renders post processing and includes the volume's
		// layer; a camera added from code does not render post processing.
		static void RenderPostProcessingOnCameras(Scene scene, int volumeLayer)
		{
			foreach (var root in scene.GetRootGameObjects())
			{
				foreach (var camera in root.GetComponentsInChildren<Camera>(true))
				{
					var cameraData = camera.GetUniversalAdditionalCameraData();
					if (!cameraData.renderPostProcessing)
						Debug.Log($"Turned on post processing on camera '{camera.name}' to render the map's post processing.");
					cameraData.renderPostProcessing = true;
					cameraData.volumeLayerMask |= 1 << volumeLayer;
					EditorUtility.SetDirty(cameraData);
					RemoveBuiltInPostProcessing(camera.gameObject);
				}
			}
		}

		// A map imported before the switch to URP has a Post Processing v2 volume under its root and a
		// Post-process Layer on its cameras, and URP renders neither.
		static void RemoveBuiltInPostProcessing(GameObject gameObject)
		{
#if COM_UNITY_POSTPROCESSING
			foreach (var component in gameObject.GetComponents<UnityEngine.Rendering.PostProcessing.PostProcessVolume>())
				UnityEngine.Object.DestroyImmediate(component);
			foreach (var component in gameObject.GetComponents<UnityEngine.Rendering.PostProcessing.PostProcessLayer>())
				UnityEngine.Object.DestroyImmediate(component);
#else
			// Without the package its components are missing scripts. Only the volume's own object is
			// certain to hold nothing else of the kind.
			if (gameObject.name == VolumeObjectName)
				GameObjectUtility.RemoveMonoBehavioursWithMissingScript(gameObject);
#endif
		}

		#endregion
#endif
	}
}
