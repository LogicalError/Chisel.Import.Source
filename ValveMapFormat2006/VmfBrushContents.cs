using System;
using System.Collections.Generic;
using Chisel.Core;
using Chisel.Import.Source.VPKTools;

namespace AeternumGames.Chisel.Import.Source.ValveMapFormat2006
{
    /// <summary>What an imported brush is for in Source, which decides the model it goes to</summary>
    public enum VmfBrushRole
    {
        /// <summary>Drawn or solid: part of the world model, typed by its contents</summary>
        World,
        /// <summary>
        /// Drawn or solid in a brush entity that vbsp compiles as a model of its own (func_brush, func_door...): a model
        /// per entity, typed by its contents, which never cuts the world
        /// </summary>
        Entity,
        /// <summary>A trigger volume: a convex trigger collider of its own, under its entity</summary>
        Trigger,
        /// <summary>Blocks movement without being drawn (clips, invisible brushes, ladders): colliders only</summary>
        Collider,
        /// <summary>Only blocks light: shadows only</summary>
        ShadowCaster,
        /// <summary>Only the compiler, the AI or an entity uses it (hints, origins, vis and effect volumes): no output</summary>
        Helper,
    }

    /// <summary>What one face of an imported brush produces</summary>
    public enum VmfFaceKind
    {
        /// <summary>Drawn with its own material</summary>
        Drawn,
        /// <summary>Drawn as the sky</summary>
        Sky,
        /// <summary>Only collides</summary>
        Collision,
        /// <summary>Only casts shadows and collides, as a nodraw face of a solid brush does</summary>
        ShadowOnly,
        /// <summary>Produces nothing</summary>
        Hidden,
    }

    /// <summary>
    /// What a Hammer brush is made of, as a Chisel contents type, and what it is for. vbsp decides the contents from
    /// the brush's sides: any side that is glass (a window), a grate, slime or water makes the whole brush that, and a
    /// brush with several of those gets the first of window, grate, slime, water. Such a brush is named after its type
    /// ("Glass 247412") and gets that entry of the project's contents list, which is added to the list when it isn't
    /// there yet. A brush that isn't world geometry (a trigger, a clip, a hint...) goes to a model of its own; see
    /// <see cref="RoleOf"/>.
    /// </summary>
    public static class VmfBrushContents
    {
        /// <summary>The contents index and the name of a brush with these sides</summary>
        public static (int contents, string name) Of(VmfSolid solid, GameResources gameResources)
        {
            var typeName = TypeNameOf(VMTContents.VisibleType(BrushContentsOf(SideMaterialsOf(solid), gameResources.GetMaterialContents)));
            if (typeName == null)
                return (ChiselContentsList.kSolidIndex, ChiselContentsList.kSolidName);

            var index = ChiselContentsList.FindOrAppendInProject(typeName);
            if (index < 0)
                return (ChiselContentsList.kSolidIndex, ChiselContentsList.kSolidName);
            // The list's own spelling, so the object reads like the dropdown
            return (index, ChiselContentsList.Instance.NameOf(index));
        }

        public static List<string> SideMaterialsOf(VmfSolid solid)
        {
            var materials = new List<string>(solid.Sides.Count);
            foreach (var side in solid.Sides)
                materials.Add(side.Material);
            return materials;
        }

        /// <summary>The contents vbsp gives a brush whose sides have these materials (LoadSideCallback, then BrushContents)</summary>
        public static SourceContents BrushContentsOf(IReadOnlyList<string> sideMaterials, Func<string, VMTContents> contentsOf)
        {
            var sides = new List<SourceContents>(sideMaterials.Count);
            foreach (var material in sideMaterials)
                sides.Add((contentsOf(material) ?? VMTContents.Missing).SideContents);
            return VMTContents.BrushContentsOf(sides);
        }

        /// <summary>The Chisel contents type for one of vbsp's see-through contents, or null for anything else</summary>
        public static string TypeNameOf(SourceContents visibleType)
        {
            switch (visibleType)
            {
                case SourceContents.Window: return "Glass";
                case SourceContents.Grate:  return "Grate";
                case SourceContents.Slime:  return "Slime";
                case SourceContents.Water:  return "Water";
                default:                    return null;
            }
        }

        // Brush entities whose brushes are a volume that something happens in when the player enters it
        static readonly HashSet<string> kTriggerVolumeClasses = new(StringComparer.OrdinalIgnoreCase)
        {
            "fog_volume", "color_correction_volume", "newxog_volume", "func_minefield",
            "func_capturezone", "func_changeclass", "func_nobuild", "func_nogrenades", "func_proprespawnzone",
            "func_regenerate", "func_respawnroom",
        };

        // Brush entities whose brushes only tell the engine, the compiler or an effect where something is
        static readonly HashSet<string> kHelperClasses = new(StringComparer.OrdinalIgnoreCase)
        {
            "func_areaportal", "func_areaportalwindow", "func_occluder", "func_viscluster",
            "func_dustmotes", "func_dustcloud", "func_smokevolume", "func_precipitation", "env_bubbles",
            "func_combine_ball_spawner",
        };

        // Brush entities whose brushes vbsp moves into the structural world. Every other brush entity is compiled
        // separately, so it does not cut structural world faces. In particular, func_detail is detail geometry:
        // vbsp clips it against the world after the world CSG pass, but it must not carve the world itself.
        static readonly HashSet<string> kWorldClasses = new(StringComparer.OrdinalIgnoreCase)
        {
            "func_ladder",
        };

        const SourceSurfaceFlags kNotDrawn = SourceSurfaceFlags.NoDraw | SourceSurfaceFlags.Hint | SourceSurfaceFlags.Skip | SourceSurfaceFlags.Trigger;

        /// <summary>Whether the engine draws a face with this material. A sky face is drawn, as the sky.</summary>
        public static bool IsDrawn(VMTContents side) => ((side ?? VMTContents.Missing).SurfaceFlags & kNotDrawn) == 0;

        /// <summary>
        /// What a brush is for, and the name its model and the brush itself get when it isn't world geometry.
        /// <paramref name="entityClassName"/> is null or empty for a world brush.
        /// </summary>
        /// <remarks>
        /// The class of a brush entity decides first, the way the engine gives its brushes a purpose: trigger volumes,
        /// vis and effect helpers, func_clip_vphysics. Everything else goes by what vbsp makes of the sides: a brush with
        /// a drawn face, or solid contents, is geometry - of the world, or of its entity's own model when vbsp compiles
        /// the entity on its own; otherwise clip contents make a clip, see-through contents an invisible collider,
        /// nonsolid contents a light blocker, and whatever is left (hints, skips, block-LOS) produces nothing.
        /// </remarks>
        public static (VmfBrushRole role, string name) RoleOf(string entityClassName, IReadOnlyList<string> sideMaterials, Func<string, VMTContents> contentsOf)
        {
            var anyDrawn   = false;
            var anyTrigger = false;
            var sides = new List<SourceContents>(sideMaterials.Count);
            foreach (var material in sideMaterials)
            {
                var side = contentsOf(material) ?? VMTContents.Missing;
                sides.Add(side.SideContents);
                anyDrawn   |= IsDrawn(side);
                anyTrigger |= (side.SurfaceFlags & SourceSurfaceFlags.Trigger) != 0;
            }
            var contents = VMTContents.BrushContentsOf(sides);

            // vbsp takes the origin brush out of any entity, and uses it for the entity's origin
            if (!anyDrawn && (contents & SourceContents.Origin) != 0)
                return (VmfBrushRole.Helper, "Origin");

            if (!string.IsNullOrEmpty(entityClassName))
            {
                if (entityClassName.StartsWith("trigger_", StringComparison.OrdinalIgnoreCase) ||
                    kTriggerVolumeClasses.Contains(entityClassName))
                    return (VmfBrushRole.Trigger, "Trigger");
                if (kHelperClasses.Contains(entityClassName))
                    return (VmfBrushRole.Helper, entityClassName);
                if (string.Equals(entityClassName, "func_clip_vphysics", StringComparison.OrdinalIgnoreCase))
                    return (VmfBrushRole.Collider, "Physics Clip");
            }

            var geometry = (string.IsNullOrEmpty(entityClassName) || kWorldClasses.Contains(entityClassName))
                         ? VmfBrushRole.World : VmfBrushRole.Entity;
            if (anyDrawn)
                return (geometry, null);
            // Trigger faces outside a trigger entity trigger nothing
            if (anyTrigger)
                return (VmfBrushRole.Helper, "Trigger");
            if ((contents & SourceContents.Solid) != 0)
                return (geometry, null);

            switch (contents & (SourceContents.PlayerClip | SourceContents.MonsterClip))
            {
                case SourceContents.PlayerClip | SourceContents.MonsterClip: return (VmfBrushRole.Collider, "Clip");
                case SourceContents.PlayerClip:                              return (VmfBrushRole.Collider, "Player Clip");
                case SourceContents.MonsterClip:                             return (VmfBrushRole.Collider, "NPC Clip");
            }
            if ((contents & SourceContents.Ladder) != 0)
                return (VmfBrushRole.Collider, "Ladder");
            if ((contents & (SourceContents.Window | SourceContents.Grate)) != 0)
                return (VmfBrushRole.Collider, "Invisible");
            if ((contents & SourceContents.Water) != 0)
                return (VmfBrushRole.Helper, "Water");
            if ((contents & SourceContents.Slime) != 0)
                return (VmfBrushRole.Helper, "Slime");
            if ((contents & SourceContents.Opaque) != 0)
                return (VmfBrushRole.ShadowCaster, "Light Blocker");
            if ((contents & SourceContents.BlockLOS) != 0)
                return (VmfBrushRole.Helper, "Block LOS");
            return (VmfBrushRole.Helper, "Hint");
        }

        // func_brush's "Solidity" (BrushSolidities_e)
        const int kBrushSolidNever  = 1;
        const int kBrushSolidAlways = 2;
        // func_wall_toggle's spawnflag SF_WALL_START_OFF
        const int kWallStartOff = 1;
        // "rendermode" kRenderNone
        const int kRenderNone = 10;

        /// <summary>
        /// Whether a brush entity's model is drawn, collides and is there at all when the map starts, as the game spawns
        /// the entity (Source SDK 2013): func_illusionary is never solid (CFuncIllusionary::Spawn); func_brush is not
        /// solid with "Solidity" Never, and "StartDisabled" hides it and makes it non-solid unless "Solidity" is Always
        /// (CFuncBrush::Spawn, TurnOff); func_wall_toggle's spawnflag 1 hides it and makes it non-solid
        /// (CFuncWallToggle::TurnOff); render mode 10 draws nothing. An entity hidden in Hammer isn't compiled at all.
        /// </summary>
        public static (bool render, bool collide, bool active) EntityState(VmfEntity entity)
        {
            var render  = entity.GetInteger("rendermode") != kRenderNone;
            var collide = true;
            var active  = !entity.IsHidden;
			var className = entity.ClassName ?? string.Empty;
			if (className.Equals("func_water_analog", StringComparison.OrdinalIgnoreCase))
				// This entity is a moving water volume. Its brush bounds drive the water surface,
				// but the volume is not a solid collision hull.
				collide = false;
			else if (className.Equals("func_illusionary", StringComparison.OrdinalIgnoreCase))
				collide = false;
            else if (className.Equals("func_brush", StringComparison.OrdinalIgnoreCase))
            {
                var solidity = entity.GetInteger("Solidity");
                collide = solidity != kBrushSolidNever;
                if (entity.GetInteger("StartDisabled") != 0)
                {
                    if (solidity == kBrushSolidAlways)
                        render = false;
                    else
                        active = false;
                }
            }
            else if (className.Equals("func_wall_toggle", StringComparison.OrdinalIgnoreCase) &&
                     (entity.GetInteger("spawnflags") & kWallStartOff) != 0)
                active = false;
            return (render, collide, active);
        }

        /// <summary>
        /// Whether a brush entity's model casts and receives shadows: not with "disableshadows" or
        /// "disablereceiveshadows" (the FGDs' Shadow and RenderFields keys)
        /// </summary>
        public static (bool cast, bool receive) EntityShadows(VmfEntity entity)
        {
            return (entity.GetInteger("disableshadows") == 0, entity.GetInteger("disablereceiveshadows") == 0);
        }

        /// <summary>What a face with this material produces, on a brush with this role</summary>
        public static VmfFaceKind FaceKindOf(VmfBrushRole role, VMTContents side)
        {
            switch (role)
            {
                case VmfBrushRole.Trigger:
                case VmfBrushRole.Collider:     return VmfFaceKind.Collision;
                case VmfBrushRole.ShadowCaster: return VmfFaceKind.ShadowOnly;
                case VmfBrushRole.Helper:       return VmfFaceKind.Hidden;
            }

            side ??= VMTContents.Missing;
            var surface = side.SurfaceFlags;
            if ((surface & (SourceSurfaceFlags.Sky | SourceSurfaceFlags.Sky2D)) != 0)
                return VmfFaceKind.Sky;
            if ((surface & kNotDrawn) == 0)
                return VmfFaceKind.Drawn;
            if ((surface & (SourceSurfaceFlags.Hint | SourceSurfaceFlags.Skip | SourceSurfaceFlags.Trigger)) != 0)
                return VmfFaceKind.Hidden;

            // A face that isn't drawn, on a world brush: it keeps what the material does besides being seen
            var contents = side.Contents;
            if ((contents & (SourceContents.Origin | SourceContents.BlockLOS)) != 0)
                return VmfFaceKind.Hidden;
            if ((contents & (SourceContents.PlayerClip | SourceContents.MonsterClip | SourceContents.Ladder)) != 0)
                return VmfFaceKind.Collision;
            if ((contents & SourceContents.Solid) == 0 &&
                (contents & (SourceContents.Window | SourceContents.Grate)) != 0)
                return VmfFaceKind.Collision;
            return VmfFaceKind.ShadowOnly;
        }
    }
}
