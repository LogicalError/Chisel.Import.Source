using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

#if COM_UNITY_POSTPROCESSING
using Chisel.Import.Source.VPKTools;

using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.SceneManagement;
#endif

namespace AeternumGames.Chisel.Import.Source.ValveMapFormat2006
{
	/// <summary>
	/// Recreates the look Source's HDR post processing gives a map - auto exposure, bloom and color
	/// correction - with Unity's Post Processing package.
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
					if (!controllers.TryGetValue(target, out var controller))
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
			return entity.Properties.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
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

		/// <summary>Edge length of the lookup Post Processing grades with.</summary>
		/// <remarks>
		/// Post Processing indexes an external lookup with LogC-encoded linear color, and the 0..1 that
		/// Source grades only fills [0.064, 0.569] of each axis of that. 65 samples keep 32 steps across
		/// it, as many as the Source lookup has.
		/// </remarks>
		public const int PostProcessLookupSize = 65;

		// LinearToLogC in Post Processing's Colors.hlsl (Alexa LogC El1000, USE_PRECISE_LOGC off), which
		// is how its uber shader addresses an external lookup.
		const double LogC_a = 5.555556, LogC_b = 0.047996, LogC_c = 0.244161, LogC_d = 0.386036;

		/// <summary>
		/// Converts Source color correction lookups into the external lookup Post Processing grades with.
		/// </summary>
		/// <param name="sourceLookups">Each lookup's .raw file: <see cref="SourceLookupSize"/>³ RGB bytes, red varying fastest.</param>
		/// <param name="weights">How strongly each lookup applies.</param>
		/// <param name="size">Edge length of the lookup to build.</param>
		/// <returns>
		/// <paramref name="size"/>³ linear RGB triplets, red varying fastest, or null when a lookup is not
		/// a .raw lookup.
		/// </returns>
		public static float[] BuildPostProcessLookup(IReadOnlyList<byte[]> sourceLookups, IReadOnlyList<float> weights, int size = PostProcessLookupSize)
		{
			var count = sourceLookups.Count;
			for (int i = 0; i < count; i++)
			{
				if (sourceLookups[i] == null || sourceLookups[i].Length != SourceLookupSize * SourceLookupSize * SourceLookupSize * 3)
					return null;
			}

			// Source adds the lookups' results to the uncorrected image by weight (Engine_Post_ps2x.fxc).
			// The weights themselves come from the closed-source material system, which is taken to
			// share the whole image out when they add up to more than one, and otherwise to leave the
			// rest uncorrected - that only matters to the few maps with several global lookups.
			var total = 0.0;
			for (int i = 0; i < count; i++)
				total += Math.Max(0, weights[i]);
			var lookupScale    = (total > 1) ? 1 / total : 1;
			var uncorrected    = (total > 1) ? 0 : 1 - total;

			// Every axis samples the same values, so decode them once: the grid position from LogC back
			// to linear, clipped to the 0..1 Source renders into, and encoded the way Source's lookup is
			// addressed.
			var axis = new double[size];
			for (int i = 0; i < size; i++)
				axis[i] = LinearToSrgb(Math.Min(1, Math.Max(0, LogCToLinear(i / (double)(size - 1)))));

			var result = new float[size * size * size * 3];
			for (int b = 0, o = 0; b < size; b++)
			{
				for (int g = 0; g < size; g++)
				{
					for (int r = 0; r < size; r++, o += 3)
					{
						var red   = axis[r] * uncorrected;
						var green = axis[g] * uncorrected;
						var blue  = axis[b] * uncorrected;
						for (int i = 0; i < count; i++)
						{
							var weight = Math.Max(0, weights[i]) * lookupScale;
							if (weight <= 0)
								continue;
							SampleSourceLookup(sourceLookups[i], axis[r], axis[g], axis[b], out var lookupRed, out var lookupGreen, out var lookupBlue);
							red   += lookupRed   * weight;
							green += lookupGreen * weight;
							blue  += lookupBlue  * weight;
						}
						result[o    ] = (float)SrgbToLinear(red);
						result[o + 1] = (float)SrgbToLinear(green);
						result[o + 2] = (float)SrgbToLinear(blue);
					}
				}
			}
			return result;
		}

		// Source samples its lookup at color * 31/32 + 0.5/32 (Engine_Post_ps2x.fxc), half a texel in from
		// either edge, so the lattice points sit on 0, 1/31, ... 1 and filtering between them is plain
		// trilinear interpolation. The lookup is addressed with the framebuffer's own gamma-encoded
		// values: neither the framebuffer nor the lookup is read as sRGB there.
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

		static void ToLattice(double value, out int index, out double fraction)
		{
			var position = Math.Min(1, Math.Max(0, value)) * (SourceLookupSize - 1);
			index    = Math.Min((int)position, SourceLookupSize - 2);
			fraction = position - index;
		}

		static double LogCToLinear(double value) => (Math.Pow(10, (value - LogC_d) / LogC_c) - LogC_b) / LogC_a;
		static double LinearToSrgb(double value) => (value <= 0.0031308) ? 12.92 * value : 1.055 * Math.Pow(value, 1 / 2.4) - 0.055;
		static double SrgbToLinear(double value) => (value <= 0.04045) ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

		#endregion

#if COM_UNITY_POSTPROCESSING
		#region Post Processing

		/// <summary>The object that holds the map's post processing volume.</summary>
		public const string VolumeObjectName = "Post Processing";

		// mat_tonemap_percent_target "60": where Source puts the frame's bright pixels.
		const float ExposureTarget = 0.6f;

		/// <summary>
		/// Gives an imported map the post processing it has in game: a global volume with auto exposure,
		/// bloom and color grading, and a Post-process Layer on the scene's cameras to render it.
		/// </summary>
		/// <param name="parent">The imported map's root, which the volume is put under; null for the scene root.</param>
		/// <param name="mapName">Names the profile asset, which a re-import updates in place.</param>
		public static void Import(GameResources gameResources, Transform parent, VmfWorld world, string mapName)
		{
			var spawnSettings = GetSpawnSettings(world);
			var profile = GetOrCreateProfile(mapName);

			// Source scales the frame so that the pixel brighter than all but 2% of the others lands on
			// 60% (mat_tonemap_percent_bright_pixels, mat_tonemap_percent_target), and holds that scale
			// inside the map's range. Post Processing divides a key by the average luminance of a slice
			// of the frame's histogram, and holds that AVERAGE inside a range instead. A thin slice
			// around the 98th percentile with a key of 0.6 is the same rule, as long as the range moves
			// with it: a scale in [min, max] is a luminance in [0.6 / max, 0.6 / min].
			var autoExposure = GetOrAddSettings<AutoExposure>(profile);
			autoExposure.filtering.Override(new Vector2(97, 99));
			autoExposure.keyValue.Override(ExposureTarget);
			autoExposure.minLuminance.Override(ToEV(ExposureTarget / spawnSettings.AutoExposureMax));
			autoExposure.maxLuminance.Override(ToEV(ExposureTarget / spawnSettings.AutoExposureMin));
			autoExposure.eyeAdaptation.Override(EyeAdaptation.Progressive);

			// Black Mesa's own bloom starts on fairly dim pixels (r_bloom_nextgen_threshold 0.1 in its
			// skill.cfg), and a threshold of 1 left a map's lamps without the halos they have in game.
			// 0.5 was picked against a game screenshot of that station, with the map's own bloom scale as
			// the intensity.
			var bloom = GetOrAddSettings<Bloom>(profile);
			bloom.intensity.Override(spawnSettings.BloomScale);
			bloom.threshold.Override(0.5f);

			// The lookup is the whole grade: Source has no tone curve, the frame simply clips.
			var lookup = BuildLookupTexture(gameResources, profile, spawnSettings.ColorCorrections, mapName);
			var colorGrading = GetOrAddSettings<ColorGrading>(profile);
			colorGrading.enabled.Override(lookup != null);
			colorGrading.gradingMode.Override(GradingMode.External);
			colorGrading.externalLut.Override(lookup);

			EditorUtility.SetDirty(profile);
			AssetDatabase.SaveAssets();

			var volume = GetOrCreateVolume(parent, profile);
			AddLayerToCameras(volume.gameObject.scene, volume.gameObject.layer);

			Debug.Log($"{mapName} post processing: auto exposure {spawnSettings.AutoExposureMin}-{spawnSettings.AutoExposureMax}, " +
					  $"bloom {spawnSettings.BloomScale}, color correction " +
					  (spawnSettings.ColorCorrections.Count == 0 ? "none" : string.Join(", ", spawnSettings.ColorCorrections.Select(c => $"{c.Filename} x{c.Weight}"))));
		}

		static float ToEV(float luminance)
		{
			// The range of Post Processing's exposure histogram (LogHistogram.rangeMin/rangeMax).
			return Mathf.Clamp(Mathf.Log(luminance, 2), -9, 9);
		}

		static PostProcessProfile GetOrCreateProfile(string mapName)
		{
			var destinationPath = PackagePath.GetOutputPath($"postprocessing/{mapName}.asset");
			var profile = UnityAssets.Load<PostProcessProfile>(destinationPath);
			if (profile == null)
			{
				profile = ScriptableObject.CreateInstance<PostProcessProfile>();
				UnityAssets.Save(profile, destinationPath);
			}
			return profile;
		}

		static T GetOrAddSettings<T>(PostProcessProfile profile) where T : PostProcessEffectSettings
		{
			if (!profile.TryGetSettings(out T settings))
			{
				settings = profile.AddSettings<T>();
				// Effect settings are stored inside the profile asset, the way the profile inspector adds them.
				AssetDatabase.AddObjectToAsset(settings, profile);
			}
			settings.enabled.Override(true);
			return settings;
		}

		static Texture3D BuildLookupTexture(GameResources gameResources, PostProcessProfile profile, List<ColorCorrection> colorCorrections, string mapName)
		{
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

			var pixels = (lookups.Count > 0) ? BuildPostProcessLookup(lookups, weights) : null;
			if (lookups.Count > 0 && pixels == null)
				Debug.LogWarning($"{mapName}: a color correction lookup is not a {SourceLookupSize}³ .raw lookup, color correction left out.");

			// The lookup lives inside the profile asset, so a re-import rewrites it in place.
			var texture = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(profile)).OfType<Texture3D>().FirstOrDefault();
			if (texture != null && (pixels == null || texture.width != PostProcessLookupSize))
			{
				AssetDatabase.RemoveObjectFromAsset(texture);
				UnityEngine.Object.DestroyImmediate(texture, true);
				texture = null;
			}
			if (pixels == null)
				return null;

			if (texture == null)
			{
				texture = new Texture3D(PostProcessLookupSize, PostProcessLookupSize, PostProcessLookupSize, TextureFormat.RGBAHalf, false);
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

		static PostProcessVolume GetOrCreateVolume(Transform parent, PostProcessProfile profile)
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

			if (!volumeObject.TryGetComponent(out PostProcessVolume volume))
				volume = volumeObject.AddComponent<PostProcessVolume>();
			volume.isGlobal      = true;
			volume.sharedProfile = profile;
			return volume;
		}

		// A volume renders nothing unless the camera looking at it has a Post-process Layer that
		// includes the volume's layer.
		static void AddLayerToCameras(Scene scene, int volumeLayer)
		{
			PostProcessResources resources = null;
			foreach (var root in scene.GetRootGameObjects())
			{
				foreach (var camera in root.GetComponentsInChildren<Camera>(true))
				{
					if (!camera.TryGetComponent(out PostProcessLayer layer))
					{
						// Added from code, the layer does not get the resources the inspector assigns it.
						resources ??= FindPostProcessResources();
						layer = camera.gameObject.AddComponent<PostProcessLayer>();
						layer.Init(resources);
						layer.volumeTrigger = camera.transform;
						Debug.Log($"Added a Post-process Layer to camera '{camera.name}' to render the map's post processing.");
					}
					layer.volumeLayer |= 1 << volumeLayer;
					EditorUtility.SetDirty(layer);
				}
			}
		}

		static PostProcessResources FindPostProcessResources()
		{
			foreach (var guid in AssetDatabase.FindAssets("t:PostProcessResources"))
			{
				var resources = AssetDatabase.LoadAssetAtPath<PostProcessResources>(AssetDatabase.GUIDToAssetPath(guid));
				if (resources != null)
					return resources;
			}
			return null;
		}

		#endregion
#endif
	}
}
