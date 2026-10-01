///////////////////////////////////////////////////////////////////////////////////////////////////
// MIT License
//
// Copyright(c) 2018-2020 Henry de Jongh
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
////////////////////// https://github.com/Henry00IS/ ////////////////// http://aeternumgames.com //

#if UNITY_EDITOR

using Chisel.Components;
using Chisel.Import.Source.VPKTools;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AeternumGames.Chisel.Import.Source.Editor
{
    public class ImporterContextMenu
    {
        static GameResources gameResources;

        /// <summary>
        /// The exception that stopped the last <see cref="ImportMap"/>, or null when it ran to the end. The map it returns
        /// then holds only what was imported before the exception.
        /// </summary>
        public static Exception LastImportException { get; private set; }

		[MenuItem("GameObject/Chisel/Import/Valve Map Format 2006...")]
        private static void ImportValveMapFormat2006()
		{
            string path = EditorUtility.OpenFilePanel("Import Source Engine Map", "", "vmf");
            if (path.Length != 0)
                ImportMap(path);
        }

        /// <summary>
        /// Imports a Valve Map Format (*.vmf) file into the open scene exactly the way the menu does, so a
        /// map can also be imported - or re-imported - from a script. An earlier import of the same map is
        /// removed first (RemovePreviousImport), so importing twice replaces rather than accumulates.
        /// </summary>
        /// <param name="showErrorDialog">Whether an exception during the import is also shown in a modal dialog. A script
        /// that imports without anyone watching passes false: the dialog would stop the editor until someone clicks it. The
        /// exception is logged either way, and kept in <see cref="LastImportException"/>.</param>
        /// <returns>The new map root ("Source Map - " and the file name), or null when no path is given. After an exception
        /// it is the part that was imported before it: check <see cref="LastImportException"/> before saving it.</returns>
        public static GameObject ImportMap(string path, bool showErrorDialog = true)
		{
            LastImportException = null;
            // TODO: have some way to set this
    	    SourceGameTitle gameTitle = SourceGameTitle.BlackMesa;

            if (gameResources == null)
			{
				var inputPaths = SourceGame.GetVPKPathsForTitle(gameTitle);

				gameResources = new();
				gameResources.InitializePaths(inputPaths);
			}

	    	GameObject go = null;
            ValveMapFormat2006.SourceReflectionProbes reflectionProbes = null;
            var mapName = string.IsNullOrEmpty(path) ? null : Path.GetFileNameWithoutExtension(path);
            try
            {
                if (!string.IsNullOrEmpty(path))
                {
                    // Before anything is parsed or created: see RemovePreviousImport for why it has to be here.
                    EditorUtility.DisplayProgressBar("Chisel: Importing Source Engine Map", "Removing the previous import...", 0.0f);
                    var previousImport = RemovePreviousImport(mapName);
                    if (previousImport != null)
                        Debug.Log("Source map import (previous import): " + previousImport);

                    EditorUtility.DisplayProgressBar("Chisel: Importing Source Engine Map", "Parsing Valve Map Format File (*.vmf)...", 0.0f);
                    var importer = new ValveMapFormat2006.VmfImporter();
                    var map = importer.Import(path);

                    // Clip models only stop the movers they stop in the game, by layer
                    var layerChanges = ValveMapFormat2006.VmfCollisionLayers.Ensure();
                    if (layerChanges != null)
                        Debug.Log("Source map import (collision layers): " + layerChanges);

                    // Textures have a tree of their own; an older import left them next to the materials
                    var textureChanges = TextureFolders.MoveTexturesOutOfMaterials();
                    if (textureChanges != null)
                        Debug.Log("Source map import (textures): " + textureChanges);

                    // create parent game object to store all of the imported content.
                    go = new GameObject("Source Map - " + Path.GetFileNameWithoutExtension(path));
                    go.SetActive(false);

                    Material skybox = null;
                    if (!string.IsNullOrEmpty(map.SkyName))
					{
						// A sky that cannot be built costs the map its sky, not the whole import: this call used to
						// throw straight out to the catch below, before a single brush had been imported
						try
						{
							skybox = MaterialImporter.ImportSkybox(gameResources, map.SkyName);
						}
						catch (Exception exception)
						{
							Debug.LogError("Source map import: the sky '" + map.SkyName + "' could not be imported; the map continues without it.");
							Debug.LogException(exception);
						}
					}

                    if (skybox)
                    {
                        RenderSettings.skybox = skybox;
                        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Skybox;
                    } else
                    {
                        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                    }

                    // A map without a light_environment has no sky light at all in Source: its
                    // interiors are lit by their own lights and VRAD's bounce. Skybox ambient has no
                    // occlusion, so it floods every interior instead - in a map's tram station it
                    // was about 60% of all the light in view. Until the map is baked, a dim fixed fill
                    // stands in for the bounce light, picked against a game screenshot of that station.
                    // This is a stand-in for VRAD's bounced light, not exposure compensation. Mixed
                    // lights provide realtime direct light before a bake, but without this fill their
                    // missing indirect contribution leaves the whole map nearly black. Unity reads
                    // ambient colors as gamma, so the linear values are converted.
                    if (!map.Entities.Exists(entity => entity.ClassName == "light_environment"))
                    {
                        const float fill = 0.3f;
                        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
                        RenderSettings.ambientSkyColor     = new Color(0.035f * fill, 0.055f * fill, 0.094f * fill).gamma;
                        RenderSettings.ambientEquatorColor = new Color(0.0505f * fill, 0.0643f * fill, 0.0873f * fill).gamma;
                        RenderSettings.ambientGroundColor  = new Color(0.0378f * fill, 0.034f * fill, 0.0303f * fill).gamma;
                    }

					// create chisel model and import all of the brushes.
					EditorUtility.DisplayProgressBar("Chisel: Importing Source Engine Map", "Preparing Material Searcher...", 0.0f);
                    var worldModel = ChiselModelManager.Instance.CreateNewModel(go.transform);
                    // The world is what a bake is mostly of
                    ValveMapFormat2006.VmfProbeVolumes.MarkModelInTheBakedLighting(worldModel.gameObject);
                    // Every material the world's brushes are drawn with, written together: one refresh between them, not
                    // one each (about 0.12 s apiece in this project)
                    gameResources.PrepareMaterials(ValveMapFormat2006.VmfWorldConverter.MaterialRequests(gameResources, map), (i, count) =>
                    {
                        if (i % 32 == 0)
                            EditorUtility.DisplayProgressBar("Chisel: Importing Source Engine Map", "Importing the world's materials...", (float)i / count);
                    });
                    ValveMapFormat2006.VmfWorldConverter.Import(gameResources, worldModel, map);

#if COM_AETERNUMGAMES_CHISEL_DECALS // optional decals package: https://github.com/Henry00IS/Chisel.Decals
                    // rebuild the world as we need the collision mesh for decals.
                    EditorUtility.DisplayProgressBar("Chisel: Importing Source Engine Map", "Rebuilding the world...", 0.5f);
                    go.SetActive(true);
                    ChiselNodeHierarchyManager.Rebuild();
#endif
                    // begin converting hammer entities to unity objects.
                    ValveMapFormat2006.VmfEntityConverter.Import(gameResources, go.transform, map);
                    // func_instance is a compile-time VMF include, not a runtime entity. Import its
                    // world brushes and entities under a transformed child; otherwise maps such as
                    // a map lose their entire water setup (instances/a_water.vmf).
                    ImportInstances(gameResources, go.transform, path, map,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(path) }, 0);
                    // A reflection probe at every env_cubemap; they are baked once the level they reflect is built (below)
                    reflectionProbes = ValveMapFormat2006.VmfReflectionProbes.Import(go.transform, map, mapName);
                    // Expensive water's planar reflection ($reflecttexture), drawn for every camera; it finds the water itself
                    go.AddComponent<ValveMapFormat2006.SourceWaterReflection>();
#if COM_UNITY_RENDER_PIPELINES_UNIVERSAL
                    // and give the map the exposure, bloom and color correction it has in game.
                    ValveMapFormat2006.VmfPostProcessingConverter.Import(gameResources, go.transform, map, Path.GetFileNameWithoutExtension(path));
#endif
                }
            }
            catch (Exception ex)
            {
                EditorUtility.ClearProgressBar();
                LastImportException = ex;
                if (showErrorDialog)
                    EditorUtility.DisplayDialog("Source Engine Map Import", "An exception occurred while importing the map:\r\n" + ex.Message, "Ohno!");
                Debug.LogException(ex);
            }
            finally
            {
                if (go != null)
                {
                    // Nothing in here may leave ImportMap. An exception out of a finally is out of the catch above too:
                    // a map's, from packing its reflection probes inside the bake, went straight to the caller, which
                    // undid the whole import. What fails here is logged and kept like any other.
                    var activateStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        // The map is kept inactive for the whole import, so this is the first moment
                        // Chisel sees any of the brushes: activating registers all of them and the
                        // initial CSG rebuild runs in one lump. None of that is visible to the
                        // converters' own timings, which is why the import looks like it finishes and
                        // then the editor sits there. Time it, and keep a progress bar up for it -
                        // ClearProgressBar used to run first, so this stretch had no bar at all.
                        EditorUtility.DisplayProgressBar("Chisel: Importing Source Engine Map", "Activating map...", 0.9f);
                        go.SetActive(true);
                        var activateSeconds = activateStopwatch.Elapsed.TotalSeconds;

                        // Rebuild here rather than leaving it to the next editor update: it has to
                        // happen either way, and this keeps it inside the progress bar and the timing
                        // instead of stalling the editor immediately afterwards.
                        EditorUtility.DisplayProgressBar("Chisel: Importing Source Engine Map", "Rebuilding the world...", 0.95f);
                        activateStopwatch.Restart();
                        ChiselNodeHierarchyManager.Rebuild();
                        Debug.Log($"Source map import (activate): {activateSeconds:0.0}s, " +
                                  $"(initial CSG rebuild): {activateStopwatch.Elapsed.TotalSeconds:0.0}s");
                    }
                    catch (Exception ex)
                    {
                        LastImportException ??= ex;
                        Debug.LogException(ex);
                    }

                    // What a bake needs: a probe volume, a baking set and lighting settings. The bake itself is
                    // Generate Lighting, which is the user's to press - it takes minutes and wants the map finished.
                    // One that cannot be set up costs the map its probe volume, not the import (as the sky does).
                    try
                    {
                        var probeVolumeChanges = ValveMapFormat2006.VmfProbeVolumes.Setup(go.transform, mapName);
                        if (probeVolumeChanges != null)
                            Debug.Log("Source map import (probe volumes): " + probeVolumeChanges);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError("Source map import: " + mapName + "'s probe volume could not be set up; the map continues without it.");
                        Debug.LogException(ex);
                    }

                    // A bake that fails costs the map its reflections, not the import (VmfReflectionProbes.Bake)
                    if (reflectionProbes != null)
                    {
                        EditorUtility.DisplayProgressBar("Chisel: Importing Source Engine Map", "Baking reflection probes...", 0.97f);
                        activateStopwatch.Restart();
                        ValveMapFormat2006.VmfReflectionProbes.Bake(reflectionProbes, mapName);
                        Debug.Log($"Source map import (reflection probes): {activateStopwatch.Elapsed.TotalSeconds:0.0}s");
                    }
                }
                ReleaseImportCaches();
                EditorUtility.ClearProgressBar();
            }
            return go;
        }

        // What an import cached of the game's materials and textures is that import's own. Kept in statics it stayed
        // loaded until the domain reloaded, every import adding to it: gameResources held 72, then 272, then 370
        // materials over one session, and with them their textures (VmfImportCachesTests). The game's
        // files stay found: gameResources itself is kept.
        static void ReleaseImportCaches()
        {
            gameResources?.ReleaseImportedMaterials();
            ValveMapFormat2006.VmfWorldConverter.ReleaseImportCaches();
            ValveMapFormat2006.VmfEntityConverter.ReleaseImportCaches();
        }

        /// <summary>
        /// Removes what an earlier import of the same map left behind, out of every loaded scene.
        /// </summary>
        /// <remarks>
        /// This runs BEFORE the VMF is parsed and before a single object is created, because the point is to
        /// never hold two copies at once. A map the size of a map is hundreds of megabytes of meshes,
        /// colliders and CSG trees; importing it a second time next to the first one takes the editor to the
        /// point where it stops responding rather than finishing. The assets have to go with the objects:
        /// Chisel's generated meshes are DontSave, so nothing reclaims them until they are explicitly
        /// unloaded, and destroying the hierarchy alone frees almost none of the memory.
        /// Deliberately not Undo.DestroyObjectImmediate - an undo record keeps the entire copy alive, which
        /// is exactly the memory this is here to release.
        /// </remarks>
        /// <returns>What was removed, or null when no loaded scene held an earlier import of this map.</returns>
        static string RemovePreviousImport(string mapName)
        {
            var rootName = "Source Map - " + mapName;
            var removed = new List<string>();
            for (int sceneIndex = 0; sceneIndex < UnityEngine.SceneManagement.SceneManager.sceneCount; sceneIndex++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(sceneIndex);
                if (!scene.isLoaded)
                    continue;
                // GetRootGameObjects returns a copy, so destroying while walking it is safe.
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root.name != rootName)
                        continue;
                    removed.Add($"{scene.name}/{root.name} ({root.transform.childCount} children)");
                    UnityEngine.Object.DestroyImmediate(root);
                }
            }

            if (removed.Count == 0)
                return null;

            var before = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
            EditorUtility.UnloadUnusedAssetsImmediate();
            var freed = (before - UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong()) / (1024.0 * 1024.0);
            return $"removed {removed.Count} earlier import(s) of {mapName} and freed {freed:0} MB: "
                 + string.Join(", ", removed);
        }

        const int kMaxInstanceDepth = 16;

        /// <summary>
        /// Expands Hammer func_instance entities. Each included VMF gets its own Chisel model so its local IDs cannot
        /// collide with the containing map's IDs, while the instance transform places all of its brushes and entities.
        /// </summary>
        static void ImportInstances(GameResources resources, Transform parent, string containingMapPath,
                                    ValveMapFormat2006.VmfWorld world, HashSet<string> ancestry, int depth)
        {
            if (world == null || depth >= kMaxInstanceDepth)
            {
                if (depth >= kMaxInstanceDepth)
                    Debug.LogWarning($"Source map instance nesting exceeded {kMaxInstanceDepth} levels in {containingMapPath}");
                return;
            }

            var directory = Path.GetDirectoryName(containingMapPath) ?? string.Empty;
            foreach (var instance in world.Entities.Where(entity =>
                         string.Equals(entity.ClassName, "func_instance", StringComparison.OrdinalIgnoreCase)))
            {
                var relativePath = instance.GetText("file");
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    Debug.LogWarning($"func_instance {instance.Id} has no file");
                    continue;
                }

                var instancePath = Path.GetFullPath(Path.Combine(directory,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!File.Exists(instancePath))
                {
                    Debug.LogWarning($"func_instance {instance.Id} could not find {instancePath}");
                    continue;
                }
                if (!ancestry.Add(instancePath))
                {
                    Debug.LogWarning($"Recursive func_instance include skipped: {instancePath}");
                    continue;
                }

                try
                {
                    var included = new ValveMapFormat2006.VmfImporter().Import(instancePath);
                    var instanceObject = new GameObject($"func_instance {instance.Id} {Path.GetFileNameWithoutExtension(instancePath)}");
                    instanceObject.SetActive(false);
                    instanceObject.transform.SetParent(parent, false);

                    var angles = instance.TryGetProperty("angles", out ValveMapFormat2006.VmfVector3 sourceAngles)
                        ? new Vector3(sourceAngles.X, sourceAngles.Y, sourceAngles.Z)
                        : Vector3.zero;
                    var origin = instance.TryGetProperty("origin", out ValveMapFormat2006.VmfVector3 sourceOrigin)
                        ? new Vector3(sourceOrigin.X, sourceOrigin.Y, sourceOrigin.Z)
                        : Vector3.zero;
                    SourceEngineUnits.SetUnityTransformWithValveCoordinates(
                        instanceObject.transform, angles, origin, SourceEntity.Brush);

                    var includedModel = ChiselModelManager.Instance.CreateNewModel(instanceObject.transform);
                    // An instance's brushes are world geometry that happens to live in another file
                    ValveMapFormat2006.VmfProbeVolumes.MarkModelInTheBakedLighting(includedModel.gameObject);
                    resources.PrepareMaterials(ValveMapFormat2006.VmfWorldConverter.MaterialRequests(resources, included));
                    ValveMapFormat2006.VmfWorldConverter.Import(resources, includedModel, included);
                    ValveMapFormat2006.VmfEntityConverter.Import(resources, instanceObject.transform, included);
                    ImportInstances(resources, instanceObject.transform, instancePath, included, ancestry, depth + 1);

                    // The containing map stays inactive during import, but explicitly activating the child makes
                    // its intended state correct even when an instance is imported under an already-active parent.
                    instanceObject.SetActive(!instance.IsHidden);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Failed to import func_instance {instance.Id} from {instancePath}");
                    Debug.LogException(ex);
                }
                finally
                {
                    ancestry.Remove(instancePath);
                }
            }
        }

        /// <summary>
        /// Makes the lights of a map that was imported earlier again from its VMF, without importing the rest again.
        /// </summary>
        [MenuItem("Chisel/Source Importer/Reimport Map Lights...")]
        private static void ReimportMapLights()
        {
            string path = EditorUtility.OpenFilePanel("Reimport Source Engine Map Lights", "", "vmf");
            if (path.Length == 0)
                return;

            var map = new ValveMapFormat2006.VmfImporter().Import(path);
            var mapName = Path.GetFileNameWithoutExtension(path);
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var mapRoot = scene.GetRootGameObjects().FirstOrDefault(root => root.name == "Source Map - " + mapName);
            if (mapRoot == null)
            {
                Debug.LogWarning($"The active scene has no \"Source Map - {mapName}\" to reimport the lights of.");
                return;
            }

            var replaced = ValveMapFormat2006.VmfEntityConverter.ReimportLights(mapRoot.transform, map);
            Debug.Log($"{mapName}: replaced {replaced} lights with lights made from {path}.");
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
        }

#if COM_UNITY_RENDER_PIPELINES_UNIVERSAL
        /// <summary>
        /// Gives a map that was imported earlier its post processing, without importing it again.
        /// </summary>
        [MenuItem("Chisel/Source Importer/Import Map Post Processing...")]
        private static void ImportMapPostProcessing()
        {
            string path = EditorUtility.OpenFilePanel("Import Source Engine Map Post Processing", "", "vmf");
            if (path.Length == 0)
                return;

            if (gameResources == null)
            {
                gameResources = new();
                gameResources.InitializePaths(SourceGame.GetVPKPathsForTitle(SourceGameTitle.BlackMesa));
            }

            var map = new ValveMapFormat2006.VmfImporter().Import(path);
            var mapName = Path.GetFileNameWithoutExtension(path);

            // Under the object the import created for the map, when the open scene holds it.
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            Transform mapRoot = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == "Source Map - " + mapName)
                {
                    mapRoot = root.transform;
                    break;
                }
            }

            ValveMapFormat2006.VmfPostProcessingConverter.Import(gameResources, mapRoot, map, mapName);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
        }

#if COM_UNITY_POSTPROCESSING
        /// <summary>Converts the active scene's legacy PPv2 map volume when its original VMF is unavailable.</summary>
        [MenuItem("Chisel/Source Importer/Migrate Active Scene Post Processing to URP")]
        private static void MigrateActiveScenePostProcessing()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
            {
                var legacyVolume = root.GetComponentsInChildren<UnityEngine.Rendering.PostProcessing.PostProcessVolume>(true)
                                       .FirstOrDefault(volume => volume.sharedProfile != null);
                if (legacyVolume == null)
                    continue;

                var parent = legacyVolume.transform.parent;
                var mapName = legacyVolume.sharedProfile.name;
                if (ValveMapFormat2006.VmfPostProcessingConverter.MigrateLegacyProfile(parent, mapName))
                {
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
                    return;
                }
            }
            Debug.LogWarning("No legacy Post Processing v2 volume was found in the active scene.");
        }
#endif
#endif
    }
}

#endif
