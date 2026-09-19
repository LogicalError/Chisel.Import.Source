using System.Collections.Generic;
using Chisel.Components;
using UnityEngine;

namespace AeternumGames.Chisel.Import.Source.ValveMapFormat2006
{
    /// <summary>
    /// The models for the brushes that aren't world geometry, made when the first one needs them, next to the world
    /// model under the map's root:
    /// <list type="bullet">
    /// <item>"Clips": a model per kind of clip or invisible collider, which only makes colliders;</item>
    /// <item>"Triggers": an object per trigger entity, with a model per brush that only makes a convex trigger collider
    /// (a trigger collider has to be convex, and a brush is);</item>
    /// <item>"Tool Brushes": light blockers, which only cast shadows, and everything else that makes nothing: hints,
    /// origins, and a model per vis or effect entity.</item>
    /// <item>"Brush Entities": a model per brush entity that vbsp compiles on its own (func_brush, func_door...), which
    /// draws and collides as the entity does when the map starts (<see cref="VmfBrushContents.EntityState"/>).</item>
    /// </list>
    /// Everything sits at the root's origin, as the world model does, so the brushes' map coordinates hold.
    /// </summary>
    public sealed class VmfBrushModels
    {
        const string kClipsGroup    = "Clips";
        const string kTriggersGroup = "Triggers";
        const string kToolsGroup    = "Tool Brushes";

        // Unity layers for the clips, which only block some movers (VmfCollisionLayers), when the project has them
        static readonly IReadOnlyDictionary<string, string> kClipLayers = VmfCollisionLayers.ClipLayersByModelName;

        readonly Transform root;
        readonly Dictionary<string, Transform> groups = new();
        readonly Dictionary<string, ChiselModelComponent> models = new();
        readonly Dictionary<VmfEntity, Transform> entities = new();

        public VmfBrushModels(Transform root)
        {
            this.root = root;
        }

        /// <summary>The model a brush with this role goes to. <paramref name="entity"/> is null for a world brush.</summary>
        public ChiselModelComponent ModelFor(VmfBrushRole role, string name, VmfEntity entity, VmfSolid solid)
        {
            switch (role)
            {
                case VmfBrushRole.Trigger:
                {
                    var model = CreateModel($"{name} {solid.Id}", EntityObject(kTriggersGroup, entity), render: false, collide: true);
                    model.colliderSettings.isTrigger = true;
                    model.colliderSettings.convex    = true;
                    return model;
                }
                case VmfBrushRole.Collider:
                    return SharedModel(kClipsGroup, name, render: false, collide: true);
                case VmfBrushRole.ShadowCaster:
                    return SharedModel(kToolsGroup, name, render: true, collide: false);
                case VmfBrushRole.Entity:
                {
                    var state = VmfBrushContents.EntityState(entity);
                    var model = SharedModel("Brush Entities", EntityName(entity), state.render, state.collide, !state.active);
                    (model.renderSettings.castShadows, model.renderSettings.receiveShadows) = VmfBrushContents.EntityShadows(entity);
                    // func_physbox is a physics object, which a physics clip stops
                    VmfCollisionLayers.PlaceOnMoverLayer(model.gameObject, null, entity.ClassName);
                    return model;
                }
                default:
                    // A helper entity keeps its brushes together, under its own name
                    if (entity != null && name == entity.ClassName)
                        return SharedModel(kToolsGroup, EntityName(entity), render: false, collide: false, entity.IsHidden);
                    return SharedModel(kToolsGroup, name, render: false, collide: false);
            }
        }

        static string EntityName(VmfEntity entity)
        {
            var name = $"{entity.ClassName} {entity.Id}";
            var targetName = entity.GetText("targetname");
            return string.IsNullOrEmpty(targetName) ? name : $"{name} {targetName}";
        }

        ChiselModelComponent SharedModel(string group, string name, bool render, bool collide, bool hidden = false)
        {
            var key = group + "/" + name;
            if (models.TryGetValue(key, out var model) && model != null)
                return model;

            model = CreateModel(name, Group(group), render, collide);
            if (kClipLayers.TryGetValue(name, out var layerName))
            {
                var layer = LayerMask.NameToLayer(layerName);
                if (layer >= 0)
                    model.gameObject.layer = layer;
            }
            if (hidden)
                model.gameObject.SetActive(false);
            models[key] = model;
            return model;
        }

        Transform EntityObject(string group, VmfEntity entity)
        {
            if (entities.TryGetValue(entity, out var entityObject) && entityObject != null)
                return entityObject;

            entityObject = new GameObject(EntityName(entity)).transform;
            entityObject.SetParent(Group(group), false);
            // Hidden in Hammer means the compiler skips it
            if (entity.IsHidden)
                entityObject.gameObject.SetActive(false);
            entities[entity] = entityObject;
            return entityObject;
        }

        Transform Group(string name)
        {
            if (groups.TryGetValue(name, out var group) && group != null)
                return group;

            group = new GameObject(name).transform;
            group.SetParent(root, false);
            groups[name] = group;
            return group;
        }

        static ChiselModelComponent CreateModel(string name, Transform parent, bool render, bool collide)
        {
            var model = ChiselComponentFactory.Create<ChiselModelComponent>(name, parent);
            model.CreateRenderComponents   = render;
            model.CreateColliderComponents = collide;
			if (model.colliderSettings == null)
            {
                model.colliderSettings = new ChiselGeneratedColliderSettings();
                model.colliderSettings.Reset();
            }
            if (model.renderSettings == null)
            {
                model.renderSettings = new ChiselGeneratedRenderSettings();
                model.renderSettings.Reset();
            }
            return model;
        }
    }
}
