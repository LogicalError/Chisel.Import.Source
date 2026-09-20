#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using Chisel.Import.Source.VPKTools;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace AeternumGames.Chisel.Import.Source.ValveMapFormat2006
{
	/// <summary>
	/// Sets an imported map up so its lighting can be baked into Adaptive Probe Volumes: a global probe volume, a
	/// baking set for the scene, lighting settings that bake indirect light, and the models marked as taking part.
	/// Nothing here bakes - that is Generate Lighting, and the user's to press.
	/// </summary>
	/// <remarks>
	/// Source's lighting is baked, and a map imported without a bake has only its realtime lights and a flat ambient
	/// stand-in: no bounce, and no occlusion of the sky. APV is what gives the port both back. It is probes rather
	/// than lightmaps because Chisel's meshes are rebuilt whenever the level is edited, which would throw a lightmap
	/// away every time, and because a map of this size wants far more lightmap than it can have (a single Chisel model
	/// is one renderer, and Unity clamps a renderer's lightmap at 4096).
	/// <para>
	/// Which of a model's renderers take part is decided per renderer from the surfaces' flags, so a sky face - drawn,
	/// but casting no shadows - is kept out of the bake and stops sealing the level in
	/// (<see cref="Chisel.Core.SurfaceDestinationFlags.ExcludedFromGlobalIllumination"/>).
	/// </para>
	/// </remarks>
	public static class VmfProbeVolumes
	{
		public const string kProbeVolumeName = "Adaptive Probe Volume";

		/// <summary>
		/// Marks a model as taking part in the scene's baked lighting. Which of its renderers actually do is Chisel's
		/// to decide from their surfaces; this only says the model itself is level geometry rather than a prop.
		/// </summary>
		public static void MarkModelInTheBakedLighting(GameObject modelGameObject)
		{
			if (modelGameObject == null)
				return;
			var flags = GameObjectUtility.GetStaticEditorFlags(modelGameObject);
			if ((flags & StaticEditorFlags.ContributeGI) == StaticEditorFlags.ContributeGI)
				return;
			GameObjectUtility.SetStaticEditorFlags(modelGameObject, flags | StaticEditorFlags.ContributeGI);
		}

		/// <summary>
		/// Gives the imported map what a bake needs, and returns what it set up for the import log.
		/// </summary>
		public static string Setup(Transform mapRoot, string mapName)
		{
			if (mapRoot == null || string.IsNullOrEmpty(mapName))
				return null;
			var changes = new List<string>();

			// One global volume: the level is the interesting part of the scene, and APV's own dilation and virtual
			// offset handle the empty space around it far better than hand-placed volumes would.
			if (mapRoot.Find(kProbeVolumeName) == null)
			{
				var volumeObject = new GameObject(kProbeVolumeName);
				volumeObject.transform.SetParent(mapRoot, false);
				var volume = volumeObject.AddComponent<ProbeVolume>();
				volume.mode = ProbeVolume.Mode.Global;
				changes.Add("a global probe volume");
			}

			var settings = EnsureLightingSettings(mapName);
			if (settings != null)
				changes.Add("lighting settings '" + settings.name + "'");

			var set = EnsureBakingSet(mapName, out var bakingSetNote);
			if (bakingSetNote != null)
				changes.Add(bakingSetNote);

			if (changes.Count == 0)
				return null;
			return string.Join(", ", changes) + (set == null ? "; bake with Generate Lighting once the scene is saved"
			                                                 : "; bake with Generate Lighting");
		}

		/// <summary>
		/// The scene's lighting settings: baked indirect light, no realtime GI. Mixed lights keep giving their direct
		/// light in realtime, as Source's do, and the bake adds only what bounces - which is the part a port is
		/// missing. An imported scene has none, and the getter throws rather than returning null when so.
		/// </summary>
		static LightingSettings EnsureLightingSettings(string mapName)
		{
			LightingSettings settings = null;
			try { settings = Lightmapping.lightingSettings; } catch (System.Exception) { }
			if (settings != null)
				return null;

			var path = PackagePath.GetAssetPath($"maps/{mapName}/{mapName} lighting.asset").Replace('\\', '/');
			settings = AssetDatabase.LoadAssetAtPath<LightingSettings>(path);
			if (settings == null)
			{
				settings = new LightingSettings { name = mapName + " lighting" };
				settings.bakedGI       = true;
				settings.realtimeGI    = false;
				settings.mixedBakeMode = MixedLightingMode.IndirectOnly;
				settings.lightmapper   = LightingSettings.Lightmapper.ProgressiveGPU;
				EnsureFolderOf(path);
				AssetDatabase.CreateAsset(settings, path);
			}
			Lightmapping.lightingSettings = settings;
			return settings;
		}

		/// <summary>
		/// The baking set the scene's probe data goes into. A scene belongs to exactly one, so a scene that already
		/// has one keeps it; a scene that has never been saved has no GUID to register and gets none until it is.
		/// </summary>
		static ProbeVolumeBakingSet EnsureBakingSet(string mapName, out string note)
		{
			note = null;
			var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
			var sceneGuid = string.IsNullOrEmpty(scene.path) ? null : AssetDatabase.AssetPathToGUID(scene.path);
			if (string.IsNullOrEmpty(sceneGuid))
			{
				note = "no baking set yet (the scene has never been saved)";
				return null;
			}

			foreach (var guid in AssetDatabase.FindAssets("t:ProbeVolumeBakingSet"))
			{
				var found = AssetDatabase.LoadAssetAtPath<ProbeVolumeBakingSet>(AssetDatabase.GUIDToAssetPath(guid));
				if (found != null && found.sceneGUIDs.Contains(sceneGuid))
					return found;
			}

			var path = PackagePath.GetAssetPath($"maps/{mapName}/{mapName} probe volumes.asset").Replace('\\', '/');
			var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
			EnsureFolderOf(path);
			AssetDatabase.CreateAsset(set, path);
			if (!set.TryAddScene(sceneGuid))
			{
				AssetDatabase.DeleteAsset(path);
				note = "no baking set (the scene would not join one)";
				return null;
			}
			EditorUtility.SetDirty(set);
			AssetDatabase.SaveAssetIfDirty(set);
			note = "a baking set";
			return set;
		}

		static void EnsureFolderOf(string assetPath)
		{
			var folder = System.IO.Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
			if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder))
				return;
			var parts = folder.Split('/');
			var built = parts[0];
			for (int i = 1; i < parts.Length; i++)
			{
				var next = built + "/" + parts[i];
				if (!AssetDatabase.IsValidFolder(next))
					AssetDatabase.CreateFolder(built, parts[i]);
				built = next;
			}
		}
	}
}
#endif
