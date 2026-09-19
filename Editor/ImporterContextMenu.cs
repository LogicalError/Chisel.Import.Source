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
        /// left alone: delete its root first, or the scene ends up holding both.
        /// </summary>
        /// <returns>The new map root ("Source Map - " and the file name), or null when no path is given.</returns>
        public static GameObject ImportMap(string path)
		{
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
                    EditorUtility.DisplayProgressBar("Chisel: Importing Source Engine Map", "Parsing Valve Map Format File (*.vmf)...", 0.0f);
                    var importer = new ValveMapFormat2006.VmfImporter();
                    var map = importer.Import(path);

                    // Clip models only stop the movers they stop in the game, by layer
                    var layerChanges = ValveMapFormat2006.VmfCollisionLayers.Ensure();
                    if (layerChanges != null)
                        Debug.Log("Source map import (collision layers): " + layerChanges);

                    // create parent game object to store all of the imported content.
                    go = new GameObject("Source Map - " + Path.GetFileNameWithoutExtension(path));
                    go.SetActive(false);

                    Material skybox = null;
                    if (!string.IsNullOrEmpty(map.SkyName))
					{
						skybox = MaterialImporter.ImportSkybox(gameResources, map.SkyName);
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
                    ValveMapFormat2006.VmfWorldConverter.Import(gameResources, ChiselModelManager.Instance.CreateNewModel(go.transform), map);

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
#if COM_UNITY_RENDER_PIPELINES_UNIVERSAL
                    // and give the map the exposure, bloom and color correction it has in game.
                    ValveMapFormat2006.VmfPostProcessingConverter.Import(gameResources, go.transform, map, Path.GetFileNameWithoutExtension(path));
#endif
                }
            }
            catch (Exception ex)
            {
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("Source Engine Map Import", "An exception occurred while importing the map:\r\n" + ex.Message, "Ohno!");
                Debug.LogException(ex);
            }
            finally
            {
                if (go != null)
                {
                    // The map is kept inactive for the whole import, so this is the first moment
                    // Chisel sees any of the brushes: activating registers all of them and the
                    // initial CSG rebuild runs in one lump. None of that is visible to the
                    // converters' own timings, which is why the import looks like it finishes and
                    // then the editor sits there. Time it, and keep a progress bar up for it -
                    // ClearProgressBar used to run first, so this stretch had no bar at all.
                    EditorUtility.DisplayProgressBar("Chisel: Importing Source Engine Map", "Activating map...", 0.9f);
                    var activateStopwatch = System.Diagnostics.Stopwatch.StartNew();
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

                    if (reflectionProbes != null)
                    {
                        EditorUtility.DisplayProgressBar("Chisel: Importing Source Engine Map", "Baking reflection probes...", 0.97f);
                        activateStopwatch.Restart();
                        ValveMapFormat2006.VmfReflectionProbes.Bake(reflectionProbes, mapName);
                        Debug.Log($"Source map import (reflection probes): {activateStopwatch.Elapsed.TotalSeconds:0.0}s");
                    }
                }
                EditorUtility.ClearProgressBar();
            }
            return go;
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
