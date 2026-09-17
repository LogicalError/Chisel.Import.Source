using System;
using System.Collections.Generic;
using Chisel.Import.Source.VPKTools;
using UnityEditor;
using UnityEngine;

namespace AeternumGames.Chisel.Import.Source.ValveMapFormat2006
{
    /// <summary>
    /// The Unity layers that stand in for Source's contents and trace masks (bspflags.h), so that a clip brush only
    /// blocks what it blocks in the game:
    /// <list type="bullet">
    /// <item>Player, NPC and PhysicsProp are the movers. Game code puts its player and NPCs on the first two; the
    /// importer puts NPCs, physics props and func_physbox on theirs.</item>
    /// <item>Clip (toolsclip: PLAYERCLIP|MONSTERCLIP) blocks players and NPCs, PlayerClip only players (MASK_PLAYERSOLID),
    /// NpcClip only NPCs (MASK_NPCSOLID). Physics props pass through all three: their physics collides with
    /// MASK_SOLID, which has no clip bits (CCollisionEvent::ShouldCollide, CBaseEntity::PhysicsSolidMaskForEntity).</item>
    /// <item>PhysicsClip (func_clip_vphysics) only blocks physics objects: it is FSOLID_NOT_SOLID to traces and movement,
    /// and its physics object only collides with moveable vphysics objects (CFuncVPhysicsClip::EntityPassesFilter).</item>
    /// </list>
    /// A clip layer collides with nothing else, the default layer included. Bullets and line of sight pass clips in
    /// Source too (MASK_SHOT, MASK_OPAQUE), so raycasts that stand in for them need a mask without the clip layers.
    /// </summary>
    public static class VmfCollisionLayers
    {
        public const string Player      = "Player";
        public const string Npc         = "NPC";
        public const string PhysicsProp = "PhysicsProp";
        public const string Clip        = "Clip";
        public const string PlayerClip  = "PlayerClip";
        public const string NpcClip     = "NpcClip";
        public const string PhysicsClip = "PhysicsClip";

        // The first layer a project may name itself
        const int kFirstUserLayer = 8;

        static readonly string[] kAll = { Player, Npc, PhysicsProp, Clip, PlayerClip, NpcClip, PhysicsClip };

        /// <summary>The movers each clip layer blocks</summary>
        public static readonly IReadOnlyDictionary<string, string[]> ClipBlocks = new Dictionary<string, string[]>
        {
            [Clip]        = new[] { Player, Npc },
            [PlayerClip]  = new[] { Player },
            [NpcClip]     = new[] { Npc },
            [PhysicsClip] = new[] { PhysicsProp },
        };

        /// <summary>The layer of each clip model, by the name the importer gives it (<see cref="VmfBrushContents.RoleOf"/>)</summary>
        public static readonly IReadOnlyDictionary<string, string> ClipLayersByModelName = new Dictionary<string, string>
        {
            ["Clip"]         = Clip,
            ["Player Clip"]  = PlayerClip,
            ["NPC Clip"]     = NpcClip,
            ["Physics Clip"] = PhysicsClip,
        };

        // Classes the game moves with vphysics that don't derive from BasePropPhysics in the FGDs
        static readonly HashSet<string> kPhysicsClasses = new(StringComparer.OrdinalIgnoreCase)
        {
            "prop_ragdoll", "prop_ragdoll_attached", "physics_prop_ragdoll", "physics_prop", "prop_physics_respawnable",
            "func_physbox", "func_physbox_multiplayer",
        };

        /// <summary>Whether two layers collide by these rules; null when neither is a clip layer, which they leave alone</summary>
        public static bool? Collides(string a, string b)
        {
            if (a != null && ClipBlocks.TryGetValue(a, out var blockedByA))
                return b != null && Array.IndexOf(blockedByA, b) >= 0;
            if (b != null && ClipBlocks.TryGetValue(b, out var blockedByB))
                return a != null && Array.IndexOf(blockedByB, a) >= 0;
            return null;
        }

        /// <summary>
        /// The mover layer of an entity class: NPC for an NPC (declared @NPCClass or based on BaseNPC, as
        /// <see cref="VmfModelCollision"/> tells them apart), PhysicsProp for what the game moves with vphysics, or null.
        /// <paramref name="classes"/> may be null, and then only the class names above are known.
        /// </summary>
        public static string MoverLayerOf(FgdEntityClasses classes, string className)
        {
            if (string.IsNullOrEmpty(className))
                return null;
            if (kPhysicsClasses.Contains(className) || (classes != null && classes.DerivesFrom(className, "BasePropPhysics")))
                return PhysicsProp;
            if (classes != null && (classes.GetKind(className) == FgdClassKind.NPC || classes.DerivesFrom(className, "BaseNPC")))
                return Npc;
            return null;
        }

        /// <summary>Puts an imported entity and everything under it on its class's mover layer, when it has one</summary>
        public static void PlaceOnMoverLayer(GameObject instance, FgdEntityClasses classes, string className)
        {
            var name = MoverLayerOf(classes, className);
            var layer = (name == null) ? -1 : LayerMask.NameToLayer(name);
            if (instance == null || layer < 0)
                return;
            foreach (var transform in instance.GetComponentsInChildren<Transform>(true))
            {
                var gameObject = transform.gameObject;
                if (gameObject.layer == layer)
                    continue;
                gameObject.layer = layer;
                if (PrefabUtility.IsPartOfPrefabInstance(gameObject))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(gameObject);
            }
        }

        /// <summary>
        /// Names the layers the project doesn't have yet, in its first free user layers, and sets how the clip layers
        /// collide. Layers the project already has keep their index, and pairs without a clip layer are left as they are.
        /// Returns what it changed, or null when everything was in place already.
        /// </summary>
        public static string Ensure()
        {
            var changes = new List<string>();
            var tagManagerAssets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (tagManagerAssets != null && tagManagerAssets.Length > 0)
            {
                var tagManager = new SerializedObject(tagManagerAssets[0]);
                var layers = tagManager.FindProperty("layers");
                foreach (var name in kAll)
                {
                    if (LayerMask.NameToLayer(name) >= 0)
                        continue;
                    var slot = -1;
                    for (int i = kFirstUserLayer; i < layers.arraySize && slot < 0; i++)
                    {
                        if (string.IsNullOrEmpty(layers.GetArrayElementAtIndex(i).stringValue))
                            slot = i;
                    }
                    if (slot < 0)
                    {
                        Debug.LogWarning($"The project has no free layer for '{name}', so imported clips can't tell it apart");
                        continue;
                    }
                    layers.GetArrayElementAtIndex(slot).stringValue = name;
                    tagManager.ApplyModifiedPropertiesWithoutUndo();
                    changes.Add($"layer {slot} = {name}");
                }
            }

            var matrixChanged = false;
            foreach (var clip in ClipBlocks)
            {
                var clipLayer = LayerMask.NameToLayer(clip.Key);
                if (clipLayer < 0)
                    continue;
                for (int other = 0; other < 32; other++)
                {
                    var ignore = Collides(clip.Key, LayerMask.LayerToName(other)) != true;
                    if (Physics.GetIgnoreLayerCollision(clipLayer, other) == ignore)
                        continue;
                    Physics.IgnoreLayerCollision(clipLayer, other, ignore);
                    matrixChanged = true;
                }
            }
            if (matrixChanged)
            {
                changes.Add("clip layer collisions set");
                var physicsSettings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/DynamicsManager.asset");
                if (physicsSettings != null && physicsSettings.Length > 0)
                    EditorUtility.SetDirty(physicsSettings[0]);
            }
            if (changes.Count == 0)
                return null;
            AssetDatabase.SaveAssets();
            return string.Join(", ", changes);
        }
    }
}
