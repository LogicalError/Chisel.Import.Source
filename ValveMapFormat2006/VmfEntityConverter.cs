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

using UnityEngine;
using System.Collections.Generic;
using Chisel.Import.Source.VPKTools;
using System;

#if COM_AETERNUMGAMES_CHISEL_DECALS // optional decals package: https://github.com/Henry00IS/Chisel.Decals
using AeternumGames.Chisel.Decals;
#endif

namespace AeternumGames.Chisel.Import.Source.ValveMapFormat2006
{
    /// <summary>
    /// Converts Hammer Entities to Unity Objects.
    /// </summary>
    public static class VmfEntityConverter
    {
        private const float lightBrightnessScalar = 0.005f;

        /// <summary>
        /// Imports the entities and attaches them to the specified parent.
        /// </summary>
        /// <param name="parent">The parent to attach entities to.</param>
        /// <param name="world">The world to be imported.</param>
        public static void Import(GameResources gameResources, Transform parent, VmfWorld world)
        {
#if COM_AETERNUMGAMES_CHISEL_DECALS // optional decals package: https://github.com/Henry00IS/Chisel.Decals
            // create a material searcher to associate materials automatically.
            MaterialSearcher materialSearcher = new MaterialSearcher();
            HashSet<string> materialSearcherWarnings = new HashSet<string>();
#endif
            var entityStopwatch = System.Diagnostics.Stopwatch.StartNew();

            ResetSpriteCaches();
            VmfModelCollision.ResetCaches();

            // A rope ends at the entity its NextKey names, which the per-entity loop below can't resolve, so ropes get a
            // pass of their own.
            ImportRopes(gameResources, parent, world);

            // Built on the first decal that needs it, since only overlays name brush faces.
            VmfDecalConverter.SideIndex decalSides = null;
            // Draws the decals on the brushes the world import built (VmfBrushIndex)
            VmfChiselDecals chiselDecals = null;

            // iterate through all entities.
            for (int e = 0; e < world.Entities.Count; e++)
            {
#if UNITY_EDITOR
                VmfWorldConverter.ReportProgress("Chisel: Importing Source Engine Map (3/3)", "Converting Hammer Entities To Unity Objects", e, world.Entities.Count);
#endif
                VmfEntity entity = world.Entities[e];

                switch (entity.ClassName)
                {
                    // Compile-time include expanded by ImporterContextMenu.ImportInstances.
                    case "func_instance":
                        break;

                    // Handled by ImportRopes above, which has to look up the entities they end at.
                    case "move_rope":
                    case "keyframe_rope":
                        break;

                    // Lights, as ImportLight makes them (and ReimportLights makes them again)
                    case "light_environment":
                    case "light":
                    case "light_spot":
                        ImportLight(parent, entity);
                        break;

					// https://developer.valvesoftware.com/wiki/Sky_camera
					// sky_camera places the 3D skybox: the miniature of the far scenery that the level is drawn over, as its
					// sky. SourceSkyCamera draws it for every camera, the Scene views included.
					case "sky_camera":
					{
						if (!TryGetEntityOrigin(entity, out Vector3 origin))
							break;
						var skyObject = new GameObject("3D Skybox (sky_camera)");
						skyObject.transform.SetParent(parent, false);
						skyObject.transform.position = origin;
						var sky = skyObject.AddComponent<SourceSkyCamera>();
						// The FGD's default scale is 16
						sky.scale = (entity.TryGetProperty("scale", out float scale) && scale > 0) ? scale : 16;
						sky.level = parent;
						sky.nearClipPlane = 2 * SourceEngineUnits.VmfInvMeters;
						// use_angles does NOT turn the sky view, and nothing here should make it: sky3dparams_t gives the client
						// only scale, origin, area and fog (playernet_vars.h:89), and CSkyCamera reads use_angles for exactly what
						// CFogController::Activate reads it for - the fog's primary direction, AngleVectors(GetAbsAngles()) negated
						// (SkyCamera.cpp:49, fogcontroller.cpp:119). a map's angles "0 180 0" mean its fog comes from -x.
						// TODO: feed the sky's fog when the 3D skybox gets fog; until then the angles are read and ignored.
						break;
					}

                    // Decals are imported as data (VmfDecal) and drawn by Chisel decals (VmfChiselDecals). With
                    // the optional decals package installed, infodecal keeps its own case further down.
#if !COM_AETERNUMGAMES_CHISEL_DECALS
                    case "infodecal":
#endif
                    case "info_overlay":
                    case "info_projecteddecal":
                    {
                        decalSides ??= new VmfDecalConverter.SideIndex(world);
                        var decal = VmfDecalConverter.Import(parent, entity, decalSides);
                        chiselDecals ??= new VmfChiselDecals(gameResources, VmfBrushIndex.Of(world));
                        chiselDecals.Create(decal);
                        break;
                    }

					// https://developer.valvesoftware.com/wiki/Env_sprite
					// env_sprite is the camera-facing glow quad the maps hang on light fixtures,
					// warning lamps, screens and anything else meant to bloom - 14353 of them
					// across the campaign, 259 in a map alone.
					case "env_sprite":
					{
						// The "model" key on a sprite is a material, not a model. It is written
						// either as a .vmt or as a legacy .spr reference, which Source resolves to
						// the .vmt of the same name - ImportSPR handles both spellings.
						if (!entity.TryGetProperty("model", out string spriteName) || string.IsNullOrEmpty(spriteName))
							break;

						var spriteMaterial = gameResources.ImportSPR(spriteName);
						if (spriteMaterial == null)
						{
							Debug.LogError($"Can't find sprite material {spriteName}");
							break;
						}

						var quad = GetSpriteQuad();
						if (quad == null)
							break;

						GameObject go = new GameObject(System.IO.Path.GetFileNameWithoutExtension(spriteName));
						go.SetActive(false);
						go.transform.parent = GetSpritesGroupOrCreate(parent);

						if (TryGetEntityOrigin(entity, out Vector3 origin))
							go.transform.position = origin;

						// A sprite is sized in texture pixels: "scale" is a multiplier on the
						// material's own dimensions, so a 128x128 glow at scale 1.2 covers 153.6
						// Source units. The billboard always faces the camera, so its rotation is
						// the camera's business and "angles" is not read.
						if (!entity.TryGetProperty("scale", out float spriteScale))
							spriteScale = 1.0f;

						if (!TryGetSpriteTextureSize(gameResources, spriteMaterial, spriteName, out var spriteSize))
						{
							Debug.LogError($"Can't determine the size of sprite {spriteName}");
							break;
						}

						go.transform.localScale = new Vector3(spriteSize.x * spriteScale * SourceEngineUnits.VmfInvMeters,
															  spriteSize.y * spriteScale * SourceEngineUnits.VmfInvMeters,
															  1.0f);

						// "rendercolor" tints the shared glow material and "renderamt" dims it.
						if (!entity.TryGetProperty("rendercolor", out VmfVector3 renderColor))
							renderColor = new(255, 255, 255);
						if (!entity.TryGetProperty("renderamt", out float renderAmount))
							renderAmount = 255;

						var tint = new Color32((byte)Mathf.Clamp(renderColor.X, 0, 255),
											   (byte)Mathf.Clamp(renderColor.Y, 0, 255),
											   (byte)Mathf.Clamp(renderColor.Z, 0, 255),
											   (byte)Mathf.Clamp(renderAmount, 0, 255));

						go.AddComponent<MeshFilter>().sharedMesh = quad;

						var spriteRenderer = go.AddComponent<MeshRenderer>();
						spriteRenderer.sharedMaterial = GetTintedSpriteMaterial(spriteMaterial, tint);
						// An additive glow neither casts nor receives anything.
						spriteRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
						spriteRenderer.receiveShadows = false;
						spriteRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
						spriteRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

						// Spawnflag 1 is "Start on". A sprite that spells out a spawnflags without
						// it starts invisible and waits to be triggered - more than half of those
						// carry a targetname, which is what does the triggering. A sprite that
						// omits the key entirely is plain decoration and stays on.
						var startsOn = !entity.TryGetProperty("spawnflags", out int spawnFlags) || (spawnFlags & 1) != 0;

						// Hidden in Hammer means hidden from the compiler too, so it is imported
						// but inactive.
						go.SetActive(startsOn && !entity.IsHidden);
						break;
					}

					// https://developer.valvesoftware.com/wiki/Env_lightglow
					// env_lightglow is glare laid over a light source - 1005 in the campaign, three in four
					// within 64 units of a light. It reacts to the viewer: it fades in with distance, can be
					// visible from the front only, and hides while the small sphere around it
					// (GlowProxySize) is out of sight.
					case "env_lightglow":
					{
						if (!TryGetEntityOrigin(entity, out Vector3 origin))
							break;

						var glowShader = FindEffectShader(ref lightGlowShader, kLightGlowShaderName);
						var glowTexture = GetEffectTexture(gameResources, kLightGlowMaterialName);
						if (glowShader == null || glowTexture == null)
							break;

						// The FGD's defaults, for anything a mapper left out.
						if (!entity.TryGetProperty("HorizontalGlowSize", out float horizontalSize)) horizontalSize = 30;
						if (!entity.TryGetProperty("VerticalGlowSize", out float verticalSize)) verticalSize = 30;
						if (!entity.TryGetProperty("MinDist", out float minDist)) minDist = 500;
						if (!entity.TryGetProperty("MaxDist", out float maxDist)) maxDist = 2000;
						if (!entity.TryGetProperty("OuterMaxDist", out float outerMaxDist)) outerMaxDist = 0;
						if (!entity.TryGetProperty("GlowProxySize", out float proxySize)) proxySize = 2;
						if (!entity.TryGetProperty("HDRColorScale", out float glowColorScale)) glowColorScale = 1;

						GameObject go = new GameObject("Light Glow");
						go.SetActive(false);
						go.transform.parent = GetEffectsGroupOrCreate(parent, "Light Glows");
						go.transform.position = origin;
						go.transform.rotation = Quaternion.LookRotation(GetEntityForward(entity));

						// The two sizes are how far the glow reaches either side of its centre.
						var width  = 2 * horizontalSize * SourceEngineUnits.VmfInvMeters;
						var height = 2 * verticalSize * SourceEngineUnits.VmfInvMeters;
						go.transform.localScale = new Vector3(width, height, Mathf.Max(width, height));

						go.AddComponent<MeshFilter>().sharedMesh = GetBillboardQuad();

						// Spawnflag 1 is "Visible only from front".
						var frontOnly = entity.TryGetProperty("spawnflags", out int glowFlags) && (glowFlags & 1) != 0;
						ConfigureEffectRenderer(go.AddComponent<MeshRenderer>()).sharedMaterial =
							GetGlowMaterial(glowShader, kLightGlowMaterialName, glowTexture, GetRenderColor(entity), glowColorScale,
											new Vector3(minDist, maxDist, outerMaxDist) * SourceEngineUnits.VmfInvMeters,
											proxySize * SourceEngineUnits.VmfInvMeters,
											frontOnly ? 1000.0f : 0.0f);

						// Hidden in Hammer means hidden from the compiler too, so it is imported but inactive.
						go.SetActive(!entity.IsHidden);
						break;
					}

					// https://developer.valvesoftware.com/wiki/Point_spotlight
					// point_spotlight is the visible shaft of a spotlight: a beam from its lamp and a halo at
					// it. Every one in the campaign sets "No Dynamic Light", and each sits on a light_spot
					// aimed the same way (270 of 271 pairs), so the light itself is already imported - this
					// is only what makes it look volumetric.
					case "point_spotlight":
					{
						if (!TryGetEntityOrigin(entity, out Vector3 origin))
							break;

						var glowShader = FindEffectShader(ref lightGlowShader, kLightGlowShaderName);
						var beamShader = FindEffectShader(ref spotlightBeamShader, kSpotlightBeamShaderName);
						var beamTexture = GetEffectTexture(gameResources, kSpotlightBeamMaterialName);
						var haloTexture = GetEffectTexture(gameResources, kSpotlightHaloMaterialName);
						if (glowShader == null || beamShader == null || beamTexture == null || haloTexture == null)
							break;

						if (!entity.TryGetProperty("spotlightlength", out float beamLength)) beamLength = 500;
						if (!entity.TryGetProperty("spotlightwidth", out float beamWidth)) beamWidth = 50;
						if (!entity.TryGetProperty("HDRColorScale", out float beamColorScale)) beamColorScale = 1;

						var beamColor = GetRenderColor(entity);
						var beamIntensity = beamColorScale * (kSpotlightBeamBrightness / 255.0f);

						GameObject go = new GameObject("Spotlight");
						go.SetActive(false);
						go.transform.parent = GetEffectsGroupOrCreate(parent, "Spotlights");
						go.transform.position = origin;
						go.transform.rotation = Quaternion.LookRotation(GetEntityForward(entity));

						var beam = new GameObject("Beam");
						beam.transform.SetParent(go.transform, false);
						// x is the width and z the length. y only sizes the bounds, which the beam can swing
						// into as it turns to face the camera.
						var beamWidthMeters = beamWidth * SourceEngineUnits.VmfInvMeters;
						beam.transform.localScale = new Vector3(beamWidthMeters, beamWidthMeters, beamLength * SourceEngineUnits.VmfInvMeters);
						beam.AddComponent<MeshFilter>().sharedMesh = GetBeamStrip();
						ConfigureEffectRenderer(beam.AddComponent<MeshRenderer>()).sharedMaterial =
							GetBeamMaterial(beamShader, kSpotlightBeamMaterialName, beamTexture, beamColor, beamIntensity);

						var halo = new GameObject("Halo");
						halo.transform.SetParent(go.transform, false);
						var haloSize = 2 * kSpotlightHaloScale * SourceEngineUnits.VmfInvMeters;
						halo.transform.localScale = new Vector3(haloSize, haloSize, haloSize);
						halo.AddComponent<MeshFilter>().sharedMesh = GetBillboardQuad();
						// Only seen when looking into the lamp: the halo fades by twice the dot product of
						// the beam and the direction to the viewer.
						ConfigureEffectRenderer(halo.AddComponent<MeshRenderer>()).sharedMaterial =
							GetGlowMaterial(glowShader, kSpotlightHaloMaterialName, haloTexture, beamColor, beamIntensity, Vector3.zero,
											kSpotlightHaloProxySize * SourceEngineUnits.VmfInvMeters, 2.0f);

						// Spawnflag 1 is "Start On".
						var startsOn = !entity.TryGetProperty("spawnflags", out int spotlightFlags) || (spotlightFlags & 1) != 0;
						go.SetActive(startsOn && !entity.IsHidden);
						break;
					}

					// Black Mesa's lens flares: up to twenty screen-space elements around a light (VmfLensFlare), drawn by
					// the LensFlare shader the way the game's client draws them.
					case "env_lensflare":
					{
						if (!TryGetEntityOrigin(entity, out Vector3 origin))
							break;

						var elements = VmfLensFlare.ReadElements(entity);
						var flareShader = FindEffectShader(ref lensFlareShader, kLensFlareShaderName);
						if (elements.Count == 0 || flareShader == null)
							break;

						var textures = VmfLensFlare.TexturesOf(elements);
						var materials = new Material[textures.Count];
						for (int i = 0; i < textures.Count; i++)
						{
							var texture = GetEffectTexture(gameResources, textures[i]);
							materials[i] = VmfEffectAssets.GetBaseMaterial(textures[i], texture, flareShader, "lens flare");
						}

						// FlareType 1 is a sun flare: the entity's angles are the sun's, as light_environment's, and the flare
						// stands wherever the camera looks toward the sun from. The client negates the pitch and places the sun
						// against that direction.
						var sunFlare = entity.GetInteger("FlareType") == 1;
						var attenuation = entity.GetFloat("FlareAttenuation") * SourceEngineUnits.VmfInvMeters;
						if (!entity.TryGetProperty("GlowProxySize", out float proxySize))
							proxySize = 2;
						var mesh = VmfLensFlare.GetMesh(elements, attenuation, proxySize * SourceEngineUnits.VmfInvMeters,
														entity.GetInteger("FlareStyle"), sunFlare);

						GameObject go = new GameObject("Lens Flare");
						go.SetActive(false);
						go.transform.parent = GetEffectsGroupOrCreate(parent, "Lens Flares");
						go.transform.position = origin;
						if (sunFlare)
						{
							if (!entity.TryGetProperty("angles", out VmfVector3 angles))
								angles = new(0, 0, 0);
							SourceEngineUnits.AngleVectors(new Vector3(-angles.X, angles.Y, angles.Z), out var lightDirection, out _, out _);
							go.transform.rotation = Quaternion.LookRotation(-SourceEngineUnits.Swizzle(lightDirection).normalized);
						}

						go.AddComponent<MeshFilter>().sharedMesh = mesh;
						ConfigureEffectRenderer(go.AddComponent<MeshRenderer>()).sharedMaterials = materials;

						// Spawnflag 1 is "Start on"; the game reads a missing key as 0.
						go.SetActive((entity.GetInteger("spawnflags") & 1) != 0 && !entity.IsHidden);
						break;
					}

					// Dust: particles that appear inside the entity's brushes and drift (VmfDustMotes). The brushes
					// themselves draw nothing, as in the game.
					case "func_dustmotes":
					case "func_dustcloud":
					{
						var brushes = new List<VmfDustMotes.HalfSpace[]>();
						foreach (var solid in entity.Solids)
						{
							var halfSpaces = VmfDustMotes.HalfSpaces(solid);
							if (halfSpaces != null)
								brushes.Add(halfSpaces);
						}
						var dustShader = FindEffectShader(ref dustMotesShader, kDustMotesShaderName);
						if (dustShader == null || !VmfDustMotes.Bounds(brushes, out var min, out var max))
							break;

						var settings = VmfDustMotes.Read(entity);
						var origin = SourceEngineUnits.Swizzle((min + max) * 0.5f) * SourceEngineUnits.VmfInvMeters;
						var mesh = VmfDustMotes.GetSpawnMesh(brushes, min, max, settings, origin, out var spawnChance);
						var material = VmfEffectAssets.GetBaseMaterial(kDustMotesMaterialName, GetEffectTexture(gameResources, kDustMotesMaterialName),
																	   dustShader, "dust");

						GameObject go = new GameObject(entity.ClassName == "func_dustcloud" ? "Dust Cloud" : "Dust Motes");
						go.SetActive(false);
						go.transform.parent = GetEffectsGroupOrCreate(parent, "Dust");
						go.transform.position = origin;
						VmfDustMotes.Configure(go.AddComponent<ParticleSystem>(), settings, mesh, spawnChance, material);
						go.SetActive(!entity.IsHidden);
						break;
					}

					default:
					{
						// NPCs, items and weapons carry no "model" key - in Source the model comes
						// from the entity class - so fall back to the game's own entity
						// definitions, which is where that mapping actually lives.
						var fromClassDefinition = false;
						if (entity.TryGetProperty("model", out string model) ||
							(fromClassDefinition = TryResolveClassModel(gameResources, entity.ClassName, out model)))
                        {
                            // The skin picks the model's material set: lights_florescent01a is off in skin 0
                            // and on in skin 1. A missing key is skin 0, as in the game.
							GameObject prefab;
							try
							{
								prefab = gameResources.ImportModel(model, skin: entity.GetInteger("skin"));
							}
							catch (Exception exception)
							{
								Debug.LogError($"[ENTITY] Failed to import model {model} for {entity.ClassName} " +
											   $"(entity {entity.Id}); skipping this entity.\n{exception}");
								break;
							}
                            if (prefab == null)
                            {
                                Debug.LogError($"Can't find model {model}");
                                break;
                            }

                            var go = UnityEditor.PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                            if (!go)
                                break;

                            // An FGD "studio()" model is used for two different things: the real
                            // model for NPCs, items and weapons, and a Hammer-only placeholder for
                            // logic and helper entities (info_node, scripted_sequence, the rope
                            // keyframes, ...). The latter live under models/editor and are never
                            // visible in game, so keep them out of Props and import them inactive
                            // rather than scattering gizmos through the scene.
                            var isEditorPlaceholder = fromClassDefinition && IsEditorOnlyModel(model);
                            go.transform.parent = isEditorPlaceholder
                                                ? GetEditorHelpersGroupOrCreate(parent)
                                                : GetPropsGroupOrCreate(parent);

                            if (entity.TryGetProperty("disableshadows", out float disableshadows) && disableshadows > 0)
                            {
                                foreach (var mesh in go.GetComponentsInChildren<MeshRenderer>())
                                {
                                    mesh.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                                    mesh.receiveShadows = false;
                                }
                            }

                            if (!entity.TryGetProperty("angles", out VmfVector3 angles))
                                angles = new(0, 0, 0);

                            if (!entity.TryGetProperty("origin", out VmfVector3 position))
                                position = new(0, 0, 0);

                            SourceEngineUnits.SetUnityTransformWithValveCoordinates(go.transform,
                                                                                    new Vector3(angles.X, angles.Y, angles.Z),
																					new Vector3(position.X, position.Y, position.Z), SourceEntity.Model);

							// The model carries each collision set the game makes of it; the entity decides which it collides with.
							ModelCollision.Use(go, VmfModelCollision.ChooseSet(gameResources, entity, model));
							// Clips only stop some movers, which are told apart by their layer
							VmfCollisionLayers.PlaceOnMoverLayer(go, gameResources.EntityClasses, entity.ClassName);

							// Only the Hammer-only helpers are inactive. NPCs, items and weapons
							// render in game and stay active - unless the mapper hid the entity, in
							// which case the compiler drops it and it is not in the game either.
							if (isEditorPlaceholder || entity.IsHidden)
								go.SetActive(false);
                        }
						break;
					}

#if COM_AETERNUMGAMES_CHISEL_DECALS // optional decals package: https://github.com/Henry00IS/Chisel.Decals
                    case "infodecal":
                    {
                        // create a new decal object:
                        GameObject go = new GameObject("Decal");
                        go.transform.parent = GetDecalsGroupOrCreate(parent);

                        // set the object position:
                        if (TryGetEntityOrigin(entity, out Vector3 origin))
                            go.transform.position = origin;

                        // add the decal component:
                        ChiselDecal decal = go.AddComponent<ChiselDecal>();

                        // assign the material:
                        if (entity.TryGetProperty("texture", out string texture))
                        {
                            Material material = FindMaterial(materialSearcher, materialSearcherWarnings, texture);
                            if (material != null)
                            {
                                go.GetComponent<MeshRenderer>().sharedMaterial = material;
                                var mainTexture = material.mainTexture;
                                if (mainTexture != null)
                                {
                                    // use the texture size to determine the size of the decal.
                                    go.transform.localScale = new Vector3(mainTexture.width * 0.008f, mainTexture.height * 0.008f, 0.1f);
                                }
                            }
                        }

                        // it should be snug against a surface- so we try to find it.
                        RaycastHit raycastHit = default;
                        bool hit = false;

                        Vector3 r = Vector3.right * 0.1f;
                        Vector3 f = Vector3.forward * 0.1f;
                        Vector3 u = Vector3.up * 0.1f;

                        // try a ray cast in all world axis to find a hit.
                        if (hit = Physics.Raycast(go.transform.position - r, r, out RaycastHit hitInfo1, 0.2f))
                            raycastHit = hitInfo1;
                        if (!hit && (hit = Physics.Raycast(go.transform.position + r, -r, out RaycastHit hitInfo2, 0.2f)))
                            raycastHit = hitInfo2;
                        if (!hit && (hit = Physics.Raycast(go.transform.position - f, f, out RaycastHit hitInfo3, 0.2f)))
                            raycastHit = hitInfo3;
                        if (!hit && (hit = Physics.Raycast(go.transform.position + f, -f, out RaycastHit hitInfo4, 0.2f)))
                            raycastHit = hitInfo4;
                        if (!hit && (hit = Physics.Raycast(go.transform.position - u, u, out RaycastHit hitInfo5, 0.2f)))
                            raycastHit = hitInfo5;
                        if (!hit && (hit = Physics.Raycast(go.transform.position + u, -u, out RaycastHit hitInfo6, 0.2f)))
                            raycastHit = hitInfo6;

                        // shouldn't not hit unless the level designer actually messed up.
                        if (hit)
                        {
                            // now we have the normal of the surface to "face align" the decal.
                            go.transform.rotation = Quaternion.LookRotation(-raycastHit.normal);
                        }

                        break;
                    }
#endif
				}
            }

            Debug.Log($"Source map import (entities): {entityStopwatch.Elapsed.TotalSeconds:0.0}s for {world.Entities.Count} entities" +
                      ((world.Entities.Count > 0) ? $" ({entityStopwatch.Elapsed.TotalMilliseconds / world.Entities.Count:0.0} ms/entity)" : ""));
            if (chiselDecals != null)
                Debug.Log(chiselDecals.Report());
        }

        /// <summary>
        /// Gets the lighting group transform or creates it.
        /// </summary>
        /// <param name="parent">The parent to create the lighting group under.</param>
        /// <returns>The transform of the lighting group</returns>
		/// <summary>
		/// Creates the Unity light of a light, light_spot or light_environment entity under the map's Lighting group.
		/// </summary>
		static void ImportLight(Transform parent, VmfEntity entity)
		{
			switch (entity.ClassName)
			{
            // The map's sun and outdoor ambient light. Source treats pitch as a separate
            // override for the first component of angles; negative pitch points the rays
            // down into the level. SourceEntity.Light performs that direction conversion.
            case "light_environment":
            {
                var go = new GameObject("Environment Light");
                go.SetActive(false);
                go.transform.parent = GetLightingGroupOrCreate(parent);

                var light = go.AddComponent<Light>();
                light.type = LightType.Directional;
#if UNITY_EDITOR
                light.lightmapBakeType = LightmapBakeType.Mixed;
#endif
                light.shadows = entity.GetFloat("SunSpreadAngle") > 0 ? LightShadows.Soft : LightShadows.Hard;
                // VRAD lights with the sun's brightness as it is: it has no falloff. A missing key is
                // the FGD's "255 255 255 200".
                if (!TryGetEffectiveLight(entity, out var environmentLight))
                    environmentLight = new VmfVector4(255, 255, 255, 200);
                ConvertLightColor(environmentLight, out var lightColor, out var lightBrightness);
                light.color = lightColor;
                light.intensity = lightBrightness;

                if (!entity.TryGetProperty("angles", out VmfVector3 angles))
                    angles = new VmfVector3(0, 0, 0);
                if (entity.TryGetProperty("pitch", out float pitch))
                    angles.X = pitch;
                if (!entity.TryGetProperty("origin", out VmfVector3 position))
                    position = new VmfVector3(0, 0, 0);
                SourceEngineUnits.SetUnityTransformWithValveCoordinates(go.transform,
                    new Vector3(angles.X, angles.Y, angles.Z),
                    new Vector3(position.X, position.Y, position.Z), SourceEntity.Light);

                if (TryGetEffectiveLight(entity, "_ambient", "_ambientHDR", "_AmbientScaleHDR", out var ambient))
                {
                    ConvertEnvironmentAmbientColor(ambient, out var ambientColor);
                    RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                    RenderSettings.ambientLight = ambientColor;
                }

                go.SetActive(!entity.IsHidden);
                if (!entity.IsHidden)
                    RenderSettings.sun = light;
                break;
            }

            // https://developer.valvesoftware.com/wiki/Light
            // light is a point entity available in all Source games. it creates an invisible, static light source that shines in all directions.
            case "light":
            {
                // create a new light object:
                GameObject go = new GameObject("Point Light");
                go.SetActive(false);

				go.transform.parent = GetLightingGroupOrCreate(parent);

                // set the object position:
                if (TryGetEntityOrigin(entity, out Vector3 origin))
                    go.transform.position = origin;

                // add a light component:
                Light light = go.AddComponent<Light>();
                light.type = LightType.Point;
#if UNITY_EDITOR
                // Source's ordinary point lights are static, but making them bake-only leaves an
                // imported map almost black until Unity has produced valid lightmaps. Mixed keeps
                // their direct light visible immediately and still lets a bake add the indirect
                // contribution, matching the spot-light import below.
                light.lightmapBakeType = LightmapBakeType.Mixed;
#endif
				light.shadows = LightShadows.Hard;
				SetLightIntensityAndRange(entity, light);

				if (!entity.TryGetProperty("angles", out VmfVector3 angles))
				{
					angles = new(0, 0, 0);
					if (entity.TryGetProperty("angle", out float angle))
					{
						if (angle >= 0) angles.Y = angle;
						else if ((int)angle == -1) angles.X = -90;
						else angles.X = 90;
					}
					if (entity.TryGetProperty("pitch", out float pitch))
					{
						angles.X = pitch;
					}
				}
				if (!entity.TryGetProperty("origin", out VmfVector3 position))
                    position = new(0, 0, 0);

				// set the object position & rotation:
				SourceEngineUnits.SetUnityTransformWithValveCoordinates(light.transform, new Vector3(angles.X, angles.Y, angles.Z), new Vector3(position.X, position.Y, position.Z), SourceEntity.Light);

				// Hidden in Hammer means hidden from the compiler too, so it is imported but inactive.
				go.SetActive(!entity.IsHidden);
				break;
            }

            // https://developer.valvesoftware.com/wiki/Light_spot
            // light_spot is a point entity available in all Source games. it is a cone-shaped, invisible light source.
            case "light_spot":
            {
                // create a new light object:
                GameObject go = new GameObject("Spot Light");
				go.SetActive(false);

				go.transform.parent = GetLightingGroupOrCreate(parent);

                // add a light component:
                Light light = go.AddComponent<Light>();
                light.type = LightType.Spot;
#if UNITY_EDITOR
                light.lightmapBakeType = LightmapBakeType.Mixed;
#endif
				light.shadows = LightShadows.Hard;
				SetLightIntensityAndRange(entity, light);

				// Source's cone, with ParseLightSpot's defaults and limits
				VmfLightFalloff.SpotAngles(entity.GetFloat("_inner_cone"), entity.GetFloat("_cone"), entity.GetFloat("_exponent"),
										   out var spotAngle, out var innerSpotAngle, out var isPointLight);
				if (isPointLight)
					light.type = LightType.Point;
				else
				{
					light.spotAngle      = spotAngle;
					light.innerSpotAngle = innerSpotAngle;
				}

                if (!entity.TryGetProperty("angles", out VmfVector3 angles))
					angles = new(0, 0, 0);

				if (!entity.TryGetProperty("origin", out VmfVector3 position))
                    position = new(0, 0, 0);

				// set the object position & rotation:
				SourceEngineUnits.SetUnityTransformWithValveCoordinates(light.transform,new Vector3(angles.X, angles.Y, angles.Z), new Vector3(position.X, position.Y, position.Z), SourceEntity.Light);
				// Hidden in Hammer means hidden from the compiler too, so it is imported but inactive.
				go.SetActive(!entity.IsHidden);
				break;
			}

			}
		}

		/// <summary>
		/// Replaces the lights of a map imported earlier with lights made from its VMF again, and leaves the rest
		/// of the map alone: for trying the light conversion on a map without importing all of it again.
		/// </summary>
		/// <param name="parent">The map's root.</param>
		/// <returns>How many lights were replaced.</returns>
		public static int ReimportLights(Transform parent, VmfWorld world)
		{
			// Only the lights: the group holds other things as well
			var group = GetLightingGroupOrCreate(parent);
			var removed = 0;
			for (int i = group.childCount - 1; i >= 0; i--)
			{
				var child = group.GetChild(i);
				if (!child.TryGetComponent<Light>(out _))
					continue;
				UnityEngine.Object.DestroyImmediate(child.gameObject);
				removed++;
			}
			foreach (var entity in world.Entities)
			{
				if (entity.ClassName is "light" or "light_spot" or "light_environment")
					ImportLight(parent, entity);
			}
			return removed;
		}

        private static Transform GetLightingGroupOrCreate(Transform parent)
        {
            Transform lighting = parent.Find("Lighting");
            if (lighting == null)
            {
                lighting = new GameObject("Lighting").transform;
                lighting.transform.parent = parent;
                return lighting;
            }
            return lighting;
		}

		/// <summary>
		/// Gets the lighting group transform or creates it.
		/// </summary>
		/// <param name="parent">The parent to create the lighting group under.</param>
		/// <returns>The transform of the lighting group</returns>
		private static Transform GetPropsGroupOrCreate(Transform parent)
		{
			Transform lighting = parent.Find("Props");
			if (lighting == null)
			{
				lighting = new GameObject("Props").transform;
				lighting.transform.parent = parent;
				return lighting;
			}
			return lighting;
		}

		/// <summary>
		/// Gets the sprites group transform or creates it.
		/// </summary>
		/// <param name="parent">The parent to create the sprites group under.</param>
		/// <returns>The transform of the sprites group</returns>
		private static Transform GetSpritesGroupOrCreate(Transform parent)
		{
			Transform sprites = parent.Find("Sprites");
			if (sprites == null)
			{
				sprites = new GameObject("Sprites").transform;
				sprites.transform.parent = parent;
				return sprites;
			}
			return sprites;
		}

#if COM_AETERNUMGAMES_CHISEL_DECALS // optional decals package: https://github.com/Henry00IS/Chisel.Decals
        /// <summary>
        /// Gets the decals group transform or creates it.
        /// </summary>
        /// <param name="parent">The parent to create the decals group under.</param>
        /// <returns>The transform of the decals group</returns>
        private static Transform GetDecalsGroupOrCreate(Transform parent)
        {
            Transform lighting = parent.Find("Decals");
            if (lighting == null)
            {
                lighting = new GameObject("Decals").transform;
                lighting.transform.parent = parent;
                return lighting;
            }
            return lighting;
        }
#endif
		/// <summary>
		/// Whether a model is one of Hammer's editor placeholders (ground_node, playerstart,
		/// scriptedsequence, the axis helpers, ...). Source games keep them under models/editor
		/// and never render them in game - they exist so logic entities are selectable in the
		/// editor - so importing them visibly would scatter gizmos through the scene.
		/// </summary>
		private static bool IsEditorOnlyModel(string modelPath)
		{
			return !string.IsNullOrEmpty(modelPath) &&
				   modelPath.Replace('\\', '/').IndexOf("models/editor/", System.StringComparison.OrdinalIgnoreCase) >= 0;
		}

		/// <summary>Gets the editor-helpers group transform or creates it.</summary>
		private static Transform GetEditorHelpersGroupOrCreate(Transform parent)
		{
			Transform helpers = parent.Find("Editor Helpers");
			if (helpers == null)
			{
				helpers = new GameObject("Editor Helpers").transform;
				helpers.transform.parent = parent;
			}
			return helpers;
		}

		static readonly HashSet<string> s_ReportedMissingClassModels = new();

		/// <summary>
		/// Resolves an entity class to its model using the game's own entity definitions.
		/// <para>
		/// NPCs, items and weapons store no model path in the .vmf - the model is chosen by the
		/// entity class - so the mapping has to come from the FGD the game ships, which is the
		/// same thing Hammer reads to draw them. No guessing, and nothing game-specific here: the
		/// definitions are discovered from whichever game is configured.
		/// </para>
		/// </summary>
		private static bool TryResolveClassModel(GameResources gameResources, string className, out string modelPath)
		{
			modelPath = gameResources.EntityClasses.GetModel(className);
			if (!string.IsNullOrEmpty(modelPath))
				return true;

			// Plenty of classes legitimately have no model (logic entities, triggers, ...), so
			// only mention the ones that look like they should.
			if ((className.StartsWith("npc_", System.StringComparison.OrdinalIgnoreCase) ||
				 className.StartsWith("item_", System.StringComparison.OrdinalIgnoreCase) ||
				 className.StartsWith("weapon_", System.StringComparison.OrdinalIgnoreCase)) &&
				s_ReportedMissingClassModels.Add(className))
				Debug.LogWarning($"[ENTITY] {className}: no model declared in the game's FGD, entity skipped.");
			return false;
		}

		/// <summary>
		/// Ropes, cables and hanging wires: one for every rope entity that draws one, built as the game builds it (see
		/// <see cref="VmfRope"/>). The entities name each other through NextKey, which the per-entity loop can't resolve,
		/// so they get a pass of their own.
		/// <para>
		/// The game draws a rope as a strip that turns to face the camera, which is what a LineRenderer is. The texture
		/// runs along the rope in V there, a fixed step per drawn point. A LineRenderer lays it out along the line in U
		/// instead, one repeat per segment in RepeatPerSegment mode, times textureScale - so the step goes into
		/// textureScale, and the Cable shader samples the two coordinates the other way around.
		/// </para>
		/// </summary>
		private static void ImportRopes(GameResources gameResources, Transform parent, VmfWorld world)
		{
			var links = VmfRope.Links(world.Entities);
			if (links.Count == 0)
				return;

			var textureHeights = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
			int created = 0;
			foreach (var link in links)
			{
				var settings = VmfRope.SettingsOf(link.Rope);
				var start = VmfRope.OriginOf(link.Rope);
				var hasEnd = link.End != null;
				// A dangling rope without an end starts out toward the world origin: nothing ever sets the end point the
				// client spreads its nodes toward.
				var end = hasEnd ? VmfRope.OriginOf(link.End) : Vector3.zero;
				var ropeLength = VmfRope.RopeLength(start, end, hasEnd);
				var springLength = VmfRope.SpringLength(ropeLength, settings.Slack, settings.Nodes);
				var nodes = VmfRope.Settle(start, end, springLength, settings.Nodes, lockEnd: !settings.Dangling);
				var points = VmfRope.RenderPoints(nodes, settings.Subdivisions, settings.Barbed);

				// A dangling rope with no length left collapses onto its start, where there is nothing to draw
				var extent = 0.0f;
				for (int i = 1; i < points.Length; i++)
					extent = Mathf.Max(extent, (points[i] - points[0]).sqrMagnitude);
				if (extent < 1e-6f)
					continue;

				var positions = new Vector3[points.Length];
				for (int i = 0; i < points.Length; i++)
					positions[i] = VmfWorldConverter.Swizzle(points[i]) * SourceEngineUnits.VmfInvMeters;

				var ropeObject = new GameObject($"Rope {link.Rope.Id}");
				ropeObject.SetActive(false);
				ropeObject.transform.parent = GetRopesGroupOrCreate(parent);

				var lineRenderer = ropeObject.AddComponent<LineRenderer>();
				lineRenderer.useWorldSpace     = true;
				lineRenderer.positionCount     = positions.Length;
				lineRenderer.SetPositions(positions);
				lineRenderer.widthMultiplier   = settings.Width * SourceEngineUnits.VmfInvMeters;
				lineRenderer.widthCurve        = AnimationCurve.Constant(0, 1, 1);
				lineRenderer.alignment         = LineAlignment.View;
				lineRenderer.textureMode       = LineTextureMode.RepeatPerSegment;
				// Negative: textures are imported upside down, and texture coordinates turned with them
				var textureHeight = RopeTextureHeight(gameResources, settings.Material, textureHeights);
				lineRenderer.textureScale      = new Vector2(-VmfRope.TextureStep(ropeLength, settings, textureHeight), 1);
				lineRenderer.generateLightingData = true;
				lineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

				var material = gameResources.ImportVMT(settings.Material);
				if (material != null)
					lineRenderer.sharedMaterial = material;

				// Hidden in Hammer means hidden from the compiler too, so it is imported but inactive
				ropeObject.SetActive(!link.Rope.IsHidden);
				created++;
			}

			Debug.Log($"Imported {created} rope(s) from {links.Count} rope entities that draw one.");
		}

		/// <summary>
		/// The height of a rope material's base texture, in texels, which the texture's step along the rope is a fraction
		/// of (C_RopeKeyframe::FinishInit reads GetMappingHeight). The VTF says, whether or not the texture is imported yet.
		/// A material that can't be found gets 1; the game would draw it with its error material and that one's height.
		/// </summary>
		private static int RopeTextureHeight(GameResources gameResources, string material, Dictionary<string, int> cache)
		{
			if (cache.TryGetValue(material, out var height))
				return height;

			var vmt = gameResources.LoadVMT(material);
			var vtf = (vmt != null && !string.IsNullOrEmpty(vmt.BaseTextureName)) ? gameResources.LoadVTF(vmt.BaseTextureName) : null;
			height = (vtf != null && vtf.Height > 0) ? vtf.Height : 1;
			cache[material] = height;
			return height;
		}

		/// <summary>
		/// Gets the ropes group transform or creates it.
		/// </summary>
		private static Transform GetRopesGroupOrCreate(Transform parent)
		{
			Transform ropes = parent.Find("Ropes");
			if (ropes == null)
			{
				ropes = new GameObject("Ropes").transform;
				ropes.transform.parent = parent;
			}
			return ropes;
		}

		#region Sprites

		/// <summary>
		/// The unit quad every sprite billboard is drawn with.
		/// </summary>
		/// <remarks>
		/// Unity's built-in quad is already a 1x1 centred on the origin, which is exactly the shape
		/// the camera-aligned shader expects, and being a real asset it costs the scene nothing to
		/// point every sprite at it.
		/// </remarks>
		private static Mesh spriteQuad;

		private static Mesh GetSpriteQuad()
		{
			if (spriteQuad != null)
				return spriteQuad;

			spriteQuad = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
			if (spriteQuad != null)
				return spriteQuad;

			// Built-in resources are looked up by file name, so a rename in some future Unity would
			// take every sprite in the map with it. Building the quad is cheap enough that it is
			// not worth letting that happen silently.
			spriteQuad = new Mesh
			{
				name      = "Sprite Quad",
				vertices  = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3( 0.5f, -0.5f, 0),
									new Vector3(-0.5f,  0.5f, 0), new Vector3( 0.5f,  0.5f, 0) },
				uv        = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) },
				triangles = new[] { 0, 2, 1, 2, 3, 1 }
			};
			spriteQuad.RecalculateNormals();
			spriteQuad.RecalculateBounds();
			return spriteQuad;
		}

		/// <summary>
		/// Tinted variants of the imported sprite materials, keyed on the tint.
		/// </summary>
		/// <remarks>
		/// A map points thousands of sprites at a handful of glow materials and then tints each one
		/// with its own rendercolor/renderamt. Those two are all that varies, so a variant per
		/// distinct tint is enough: a map's 259 sprites need 26 materials between them.
		/// </remarks>
		private static readonly Dictionary<(Material material, Color32 tint), Material> tintedSpriteMaterials = new();

		private static Material GetTintedSpriteMaterial(Material material, Color32 tint)
		{
			if (tintedSpriteMaterials.TryGetValue((material, tint), out var tinted))
				return tinted;

			// An untinted sprite can use the shared material as it is. A tinted one gets a variant of it, an asset beside
			// it that every sprite and map with the same tint shares (VmfEffectAssets).
			if (tint.r == 255 && tint.g == 255 && tint.b == 255 && tint.a == 255)
				tinted = material;
			else
			{
				// Declared as a Color, so Unity converts it to linear for us - a Source rendercolor
				// is written in gamma space, like every other colour in the format. The alpha is
				// the brightness and must NOT be converted, which is also what Unity does with it.
				tinted = VmfEffectAssets.GetMaterialVariant(material, $"{tint.r} {tint.g} {tint.b} {tint.a}",
															variant => variant.SetColor(SpriteTintColor, tint)) ?? material;
			}

			tintedSpriteMaterials[(material, tint)] = tinted;
			return tinted;
		}

		private static readonly int SpriteTintColor = Shader.PropertyToID("_TintColor");

		/// <summary>
		/// Pixel dimensions per sprite material, which is what a sprite's "scale" multiplies.
		/// </summary>
		private static readonly Dictionary<string, Vector2Int> spriteTextureSizes = new();

		/// <summary>
		/// The size, in texture pixels, that a sprite's "scale" is a multiplier on.
		/// </summary>
		/// <remarks>
		/// Deliberately not read off the imported Material: the texture asset behind it does not
		/// necessarily exist yet when the entities are converted, and a sprite whose size silently
		/// fell back to a guess would be wrong by exactly the factor between the guess and the real
		/// texture - half size for every one of the 128x128 glows, which is most of them. The VTF
		/// header in the game files is there either way, so ask that instead and only use the
		/// Material when it does have a texture to offer.
		/// </remarks>
		private static bool TryGetSpriteTextureSize(GameResources gameResources, Material spriteMaterial, string spriteName, out Vector2Int size)
		{
			if (spriteTextureSizes.TryGetValue(spriteName, out size))
				return size.x > 0 && size.y > 0;

			size = default;

			var mainTexture = spriteMaterial != null ? spriteMaterial.mainTexture : null;
			if (mainTexture != null)
				size = new Vector2Int(mainTexture.width, mainTexture.height);
			else
			{
				var spriteVmt = gameResources.LoadSPR(spriteName);
				var baseTexture = spriteVmt != null ? spriteVmt.BaseTextureName : null;
				var vtf = !string.IsNullOrEmpty(baseTexture) ? gameResources.LoadVTF(baseTexture) : null;
				if (vtf != null)
					size = new Vector2Int(vtf.Width, vtf.Height);
			}

			spriteTextureSizes[spriteName] = size;
			return size.x > 0 && size.y > 0;
		}

		/// <summary>
		/// Clears the per-import caches. The effect materials and meshes are assets now (VmfEffectAssets), but one can be
		/// deleted between imports, so each import looks them up again.
		/// </summary>
		private static void ResetSpriteCaches()
		{
			tintedSpriteMaterials.Clear();
			spriteTextureSizes.Clear();
			glowMaterials.Clear();
			beamMaterials.Clear();
			billboardQuad = null;
			beamStrip = null;
			effectTextures.Clear();
		}

		#endregion

		#region Light glows and spotlights

		// Neither entity names a material: the engine hard-codes one for each.
		private const string kLightGlowMaterialName     = "sprites/light_glow02_add_noz"; // C_LightGlow
		private const string kSpotlightBeamMaterialName = "sprites/glow_test02";         // CPointSpotlight's beam
		private const string kSpotlightHaloMaterialName = "sprites/light_glow03";        // ... and its halo

		private const string kLightGlowShaderName     = "Chisel/Import/LightGlow";
		private const string kSpotlightBeamShaderName = "Chisel/Import/SpotlightBeam";
		private const string kLensFlareShaderName     = "Chisel/Import/LensFlare";
		private const string kDustMotesShaderName     = "Chisel/Import/DustMotes";

		/// <summary>The material C_Func_Dust draws its particles with</summary>
		private const string kDustMotesMaterialName = "particle/sparkles";

		/// <summary>How far CPointSpotlight's halo reaches either side of the lamp, in Source units.</summary>
		private const float kSpotlightHaloScale = 60.0f;

		/// <summary>CPointSpotlight's beam brightness, out of 255. It is fixed - the entity's own
		/// renderamt does not reach the beam.</summary>
		private const float kSpotlightBeamBrightness = 64.0f;

		/// <summary>Occlusion sphere for a spotlight's halo, in Source units. Source gives the halo no
		/// query of its own, but the glow shader draws on top, and this small one is what keeps the halo
		/// from showing through the walls around its lamp.</summary>
		private const float kSpotlightHaloProxySize = 4.0f;

		private static Shader lightGlowShader;
		private static Shader spotlightBeamShader;
		private static Shader lensFlareShader;
		private static Shader dustMotesShader;
		private static Mesh billboardQuad;
		private static Mesh beamStrip;
		private static readonly Dictionary<string, Texture2D> effectTextures = new();
		private static readonly Dictionary<(Texture2D texture, Color32 tint, float intensity, Vector3 fadeDistances, float proxyRadius, float directionalFade), Material> glowMaterials = new();
		private static readonly Dictionary<(Texture2D texture, Color32 tint, float intensity), Material> beamMaterials = new();

		private static Shader FindEffectShader(ref Shader shader, string shaderName)
		{
			if (shader == null)
			{
				shader = Shader.Find(shaderName);
				if (shader == null)
					Debug.LogError($"Shader \"{shaderName}\" not found");
			}
			return shader;
		}

		/// <summary>
		/// The quad glows are drawn with. The shader builds it facing the camera, so the mesh only gives
		/// it a size - and the bounds culling goes by.
		/// </summary>
		/// <remarks>
		/// The bounds are a unit deep rather than flat, and the caller scales that depth to the glow's
		/// larger side: a glow turns to face the camera, and flat bounds seen edge-on would cull one that
		/// is still on screen.
		/// </remarks>
		private static Mesh GetBillboardQuad()
		{
			if (billboardQuad != null)
				return billboardQuad;

			// An asset, which every glow in every map shares, rather than a mesh in each scene
			billboardQuad = VmfEffectAssets.GetMesh("billboard quad", () =>
			{
				var mesh = new Mesh
				{
					vertices  = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0),
										new Vector3(-0.5f,  0.5f, 0), new Vector3(0.5f,  0.5f, 0) },
					uv        = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) },
					triangles = new[] { 0, 2, 1, 2, 3, 1 }
				};
				mesh.bounds = new Bounds(Vector3.zero, Vector3.one);
				return mesh;
			});
			return billboardQuad;
		}

		/// <summary>
		/// The strip a spotlight beam is drawn with: x runs across it and z from the lamp (0) to the far
		/// end (1). The shader turns it about that axis to face the camera.
		/// </summary>
		private static Mesh GetBeamStrip()
		{
			if (beamStrip != null)
				return beamStrip;

			beamStrip = VmfEffectAssets.GetMesh("beam strip", () =>
			{
				var mesh = new Mesh
				{
					vertices  = new[] { new Vector3(-0.5f, 0, 0), new Vector3(0.5f, 0, 0),
										new Vector3(-0.5f, 0, 1), new Vector3(0.5f, 0, 1) },
					triangles = new[] { 0, 2, 1, 2, 3, 1 }
				};
				// Turned about its axis, the strip can end up anywhere in the unit box around it.
				mesh.bounds = new Bounds(new Vector3(0, 0, 0.5f), Vector3.one);
				return mesh;
			});
			return beamStrip;
		}

		/// <summary>
		/// The texture behind one of the engine's hard-coded effect materials, imported when it is not on
		/// disk yet.
		/// </summary>
		/// <remarks>
		/// Only the texture is wanted - these entities are drawn with shaders of their own, not with
		/// whatever the VMT would turn into - but going through the VMT keeps the game's own choice of it.
		/// It is the material's $basetexture, which the game reads as colour, so it is claimed as colour
		/// like any texture a material samples that way (Texture2DImporter.MarkAsColor).
		/// </remarks>
		private static Texture2D GetEffectTexture(GameResources gameResources, string materialName)
		{
			if (effectTextures.TryGetValue(materialName, out var texture))
				return texture;

			var vmt = gameResources.LoadVMT(materialName);
			var textureName = (vmt != null && !string.IsNullOrEmpty(vmt.BaseTextureName)) ? vmt.BaseTextureName : materialName;
			var images = gameResources.ImportVTF(textureName, PackagePath.DefaultMaterialPaths);
			texture = (images != null && images.Length > 0) ? images[0] : null;
			if (texture == null)
				Debug.LogError($"Can't find the texture for {materialName}");
			else
				Texture2DImporter.MarkAsColor(texture);
			effectTextures[materialName] = texture;
			return texture;
		}

		/// <summary>
		/// A glow material, shared by every glow that looks the same - maps place these by the dozen with
		/// identical settings. It is a variant of the texture's glow material, and both are assets
		/// (VmfEffectAssets).
		/// </summary>
		private static Material GetGlowMaterial(Shader shader, string materialName, Texture2D texture, Color32 tint, float intensity,
												Vector3 fadeDistances, float proxyRadius, float directionalFade)
		{
			var key = (texture, tint, intensity, fadeDistances, proxyRadius, directionalFade);
			if (glowMaterials.TryGetValue(key, out var material) && material != null)
				return material;

			var glow = VmfEffectAssets.GetBaseMaterial(materialName, texture, shader, "glow");
			material = VmfEffectAssets.GetMaterialVariant(glow, FormattableString.Invariant(
							$"{tint.r} {tint.g} {tint.b} x{intensity:0.####} fade {fadeDistances.x:0.####} {fadeDistances.y:0.####} {fadeDistances.z:0.####} proxy {proxyRadius:0.####} facing {directionalFade:0.####}"),
						variant =>
						{
							variant.SetColor("_TintColor", tint);
							variant.SetFloat("_Intensity", intensity);
							variant.SetVector("_FadeDistances", fadeDistances);
							variant.SetFloat("_ProxyRadius", proxyRadius);
							variant.SetFloat("_DirectionalFade", directionalFade);
						});
			glowMaterials[key] = material;
			return material;
		}

		/// <summary>A beam material: a variant of the texture's beam material, both assets (VmfEffectAssets).</summary>
		private static Material GetBeamMaterial(Shader shader, string materialName, Texture2D texture, Color32 tint, float intensity)
		{
			var key = (texture, tint, intensity);
			if (beamMaterials.TryGetValue(key, out var material) && material != null)
				return material;

			var beam = VmfEffectAssets.GetBaseMaterial(materialName, texture, shader, "beam");
			material = VmfEffectAssets.GetMaterialVariant(beam, FormattableString.Invariant($"{tint.r} {tint.g} {tint.b} x{intensity:0.####}"),
						variant =>
						{
							variant.SetColor("_TintColor", tint);
							variant.SetFloat("_Intensity", intensity);
						});
			beamMaterials[key] = material;
			return material;
		}

		private static MeshRenderer ConfigureEffectRenderer(MeshRenderer meshRenderer)
		{
			// Additive light effects neither cast nor receive anything.
			meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
			meshRenderer.receiveShadows = false;
			meshRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
			meshRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
			return meshRenderer;
		}

		/// <summary>An entity's "rendercolor", white when it has none.</summary>
		private static Color32 GetRenderColor(VmfEntity entity)
		{
			if (!entity.TryGetProperty("rendercolor", out VmfVector3 color))
				return new Color32(255, 255, 255, 255);
			return new Color32((byte)Mathf.Clamp(color.X, 0, 255), (byte)Mathf.Clamp(color.Y, 0, 255),
							   (byte)Mathf.Clamp(color.Z, 0, 255), 255);
		}

		/// <summary>The direction an entity's "angles" point it, in Unity's axes.</summary>
		/// <remarks>
		/// Source's AngleVectors, which point entities such as these go by. light_spot is the exception -
		/// VRAD aims it with the pitch the other way round - which is why the light code does not use this.
		/// </remarks>
		private static Vector3 GetEntityForward(VmfEntity entity)
		{
			if (!entity.TryGetProperty("angles", out VmfVector3 angles))
				angles = new(0, 0, 0);
			SourceEngineUnits.AngleVectors(new Vector3(angles.X, angles.Y, angles.Z), out var forward, out _, out _);
			return SourceEngineUnits.Swizzle(forward).normalized;
		}

		private static Transform GetEffectsGroupOrCreate(Transform parent, string groupName)
		{
			var group = parent.Find(groupName);
			if (group == null)
			{
				group = new GameObject(groupName).transform;
				group.parent = parent;
			}
			return group;
		}

		#endregion

		#region Light brightness

		/// <summary>
		/// The "_light" value a light is really compiled with, in the key's own terms: gamma-space
		/// RGB in X/Y/Z and the brightness in W.
		/// </summary>
		/// <remarks>
		/// An HDR compile, which is what Black Mesa's maps are, reads "_lightHDR" in place of "_light"
		/// whenever it holds a real value, then scales whichever of the two it took by
		/// "_lightscaleHDR". The color conversion and the range both start from this, so the two
		/// cannot disagree about how bright a light is.
		/// </remarks>
		private static bool TryGetEffectiveLight(VmfEntity entity, out VmfVector4 light)
		{
			return TryGetEffectiveLight(entity, "_light", "_lightHDR", "_lightscaleHDR", out light);
		}

		private static bool TryGetEffectiveLight(VmfEntity entity, string ldrKey, string hdrKey, string hdrScaleKey,
		                                         out VmfVector4 light)
		{
			// Hammer writes "-1 -1 -1 1" for "same as _light", but what VRAD actually tests for is a
			// negative component, so "-1 -1 -1 0" means the same thing.
			if (!TryGetLightKey(entity, hdrKey, out light) || light.X < 0 || light.Y < 0 || light.Z < 0 || light.W < 0)
			{
				if (!TryGetLightKey(entity, ldrKey, out light))
					return false;
			}

			// The brightness is a plain multiplier on the light, so the scale folds straight into it.
			if (entity.TryGetProperty(hdrScaleKey, out float scaleHDR))
				light.W *= scaleHDR;
			return true;
		}

		/// <summary>
		/// Reads a "_light"-style key as RGB plus brightness, in the one-, three- and four-number
		/// shapes VRAD accepts.
		/// </summary>
		/// <remarks>
		/// VRAD takes one number as a grey and three as a color, and only scales by a fourth, so a
		/// missing brightness is 255 - the value that leaves the color as it is. The parser types
		/// those shapes as a number, a VmfVector3 and a VmfVector4, and TryGetProperty will not
		/// convert between them. The vector returned is always a copy: the color conversion rewrites
		/// it in place, and would otherwise rewrite the entity's own key along with it.
		/// </remarks>
		private static bool TryGetLightKey(VmfEntity entity, string key, out VmfVector4 light)
		{
			if (entity.TryGetProperty(key, out VmfVector4 rgbBrightness))
				light = new VmfVector4(rgbBrightness.X, rgbBrightness.Y, rgbBrightness.Z, rgbBrightness.W);
			else if (entity.TryGetProperty(key, out VmfVector3 rgb))
				light = new VmfVector4(rgb.X, rgb.Y, rgb.Z, 255.0f);
			else if (entity.TryGetProperty(key, out float grey))
				light = new VmfVector4(grey, grey, grey, 255.0f);
			else
			{
				light = null;
				return false;
			}
			return true;
		}

		/// <summary>
		/// The Light.color and brightness of a light's effective "_light".
		/// </summary>
		/// <remarks>
		/// VRAD lights with pow(color / 255, 2.2) × brightness / 255, where 1 is white. URP's lights take a
		/// gamma-space color and a linear intensity, so the key's own color goes straight across, and the
		/// brightness is the intensity: of a directional light as it is, and of a point or spot light at the
		/// distance its falloff is relative to (<see cref="SetLightIntensityAndRange"/>).
		/// </remarks>
		private static void GetLightColor(VmfEntity entity, out Color color, out float brightness)
		{
			// Plain white at full brightness for a light that states no color at all.
			if (!TryGetEffectiveLight(entity, out VmfVector4 light))
				light = new VmfVector4(255, 255, 255, 255);
			ConvertLightColor(light, out color, out brightness);
		}

		private static void ConvertLightColor(VmfVector4 light, out Color color, out float brightness)
		{
			color = new Color(Mathf.Max(0, light.X) / 255.0f, Mathf.Max(0, light.Y) / 255.0f, Mathf.Max(0, light.Z) / 255.0f);
			brightness = Mathf.Max(0, light.W) / 255.0f;
		}

		/// <summary>
		/// Approximates VRAD's baked environment fill with Unity's real-time flat ambient light.
		/// The Source value is not a full-strength global illumination color: VRAD applies it while
		/// solving lightmaps, including surface orientation and occlusion. Unity flat ambient has none
		/// of that attenuation, so a direct conversion washes out the entire map.
		/// </summary>
		private static void ConvertEnvironmentAmbientColor(VmfVector4 light, out Color color)
		{
			const float realtimeFlatAmbientScale = 0.2f;
			var intensity = Mathf.Clamp01(Mathf.Max(0, light.W) / 400.0f) * realtimeFlatAmbientScale;
			color = new Color(Mathf.Max(0, light.X) / 255.0f,
			                  Mathf.Max(0, light.Y) / 255.0f,
			                  Mathf.Max(0, light.Z) / 255.0f) * intensity;
		}

		#endregion

		#region Light falloff

		/// <summary>Linear brightness (1 is white) under which a light no longer shows: where its range ends.</summary>
		/// <remarks>
		/// URP fades a light out over the last part of its range. A light that reaches further costs more,
		/// and leaks further where it casts no shadow: past URP's shadow budget (256 shadow slices, six for a
		/// point light) only the nearest lights keep theirs.
		/// </remarks>
		private const float lightVisibleBrightness = 0.01f;

		/// <summary>Range ceiling in Source units (~19.5 m): an unbounded range is a straight performance cost.</summary>
		private const float lightMaxRangeUnits = 1024.0f;

		/// <summary>Range floor in Source units.</summary>
		private const float lightMinRangeUnits = 32.0f;

		/// <summary>
		/// Gives a point or spot light the color, brightness and reach its Source light has as VRAD bakes it
		/// (<see cref="VmfLightFalloff"/>).
		/// </summary>
		private static void SetLightIntensityAndRange(VmfEntity entity, Light light)
		{
			GetLightColor(entity, out var color, out var brightness);
			var falloff = VmfLightFalloff.FromKeys(new VmfLightFalloff.Keys
			{
				FiftyPercentDistance = entity.GetFloat("_fifty_percent_distance"),
				ZeroPercentDistance  = entity.GetFloat("_zero_percent_distance"),
				HardFalloff          = entity.GetInteger("_hardfalloff") != 0,
				ConstantAttn         = entity.GetFloat("_constant_attn"),
				LinearAttn           = entity.GetFloat("_linear_attn"),
				QuadraticAttn        = entity.GetFloat("_quadratic_attn"),
				Distance             = entity.GetFloat("_distance"),
			});

			// Source's falloff as an inverse square over the distances the light reaches, in units². URP's
			// intensity is in meters: I / d² at d meters is I · VmfMeters² / d² at d units.
			var farthest = (falloff.EndFade > falloff.StartFade) ? falloff.EndFade : lightMaxRangeUnits;
			var strength = falloff.FitInverseSquare(farthest);
			light.color     = color;
			light.intensity = brightness * strength * SourceEngineUnits.VmfInvMeters * SourceEngineUnits.VmfInvMeters;

			// How far it shows, going by its brightest channel as the renderer sees it
			var linear = color.linear;
			var brightest = Mathf.Max(linear.r, Mathf.Max(linear.g, linear.b)) * brightness;
			var reach = falloff.Reach(brightest, strength, lightVisibleBrightness);
			light.range = Mathf.Clamp(reach, lightMinRangeUnits, lightMaxRangeUnits) * SourceEngineUnits.VmfInvMeters;
		}

		#endregion

		/// <summary>
		/// A Source entity's angles as a Unity rotation, relative to the orientation angles of 0 give (Source's x, y, z are
		/// Unity's x, z, y). Identity for angles of 0.
		/// <para>
		/// NOT for turning the 3D skybox: sky3dparams_t has no angles and Source never turns the sky view. sky_camera's
		/// use_angles is the fog's direction, as env_fog_controller's is (SkyCamera.cpp, fogcontroller.cpp).
		/// </para>
		/// </summary>
		public static Quaternion SkyTurnOf(VmfVector3 angles)
		{
			SourceEngineUnits.AngleVectors(new Vector3(angles.X, angles.Y, angles.Z), out var forward, out _, out var up);
			var turned = Quaternion.LookRotation(SourceEngineUnits.Swizzle(forward), SourceEngineUnits.Swizzle(up));
			return turned * Quaternion.Inverse(Quaternion.LookRotation(Vector3.right, Vector3.up));
		}

		private static bool TryGetEntityOrigin(VmfEntity entity, out Vector3 result)
        {
            result = default;
            if (entity.TryGetProperty("origin", out VmfVector3 v))
            {
                result = VmfWorldConverter.Swizzle(v) * SourceEngineUnits.VmfInvMeters;
                return true;
            }
            return false;
        }

        private static bool TryGetEntityRotation(VmfEntity entity, out Quaternion result)
        {
            result = new Quaternion();
            bool success = false;
            if (entity.TryGetProperty("angles", out VmfVector3 angles))
            {
                result = Quaternion.Euler(-angles.X, -angles.Y + 90, angles.Z);
                success = true;
            }
            if (entity.TryGetProperty("pitch", out float pitch))
            {
                if (pitch != 0.0f)
                {
                    result.eulerAngles = new Vector3(-pitch, result.eulerAngles.y, result.eulerAngles.z);
                }
                success = true;
            }
            return success;
        }

#if COM_AETERNUMGAMES_CHISEL_DECALS // optional decals package: https://github.com/Henry00IS/Chisel.Decals
        private static Material FindMaterial(MaterialSearcher materialSearcher, HashSet<string> materialSearcherWarnings, string name)
        {
            // find the material in the unity project automatically.
            Material material;

            // try finding the fully qualified texture name with '/' replaced by '.' so 'BRICK.BRICKWALL052D'.
            string materialName = name.Replace("/", ".");
            if (materialName.Contains("."))
            {
                // try finding both 'BRICK.BRICKWALL052D' and 'BRICKWALL052D'.
                string tiny = materialName.Substring(materialName.LastIndexOf('.') + 1);
                material = materialSearcher.FindMaterial(new string[] { materialName, tiny });
                if (material == null && materialSearcherWarnings.Add(materialName))
                    Debug.Log("Chisel: Tried to find material '" + materialName + "' and also as '" + tiny + "' but it couldn't be found in the project.");
            }
            else
            {
                // only try finding 'BRICKWALL052D'.
                material = materialSearcher.FindMaterial(new string[] { materialName });
                if (material == null && materialSearcherWarnings.Add(materialName))
                    Debug.Log("Chisel: Tried to find material '" + materialName + "' but it couldn't be found in the project.");
            }

            return material;
        }
#endif
    }
}
