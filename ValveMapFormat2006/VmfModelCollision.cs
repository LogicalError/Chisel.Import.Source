using System;
using System.Collections.Generic;
using System.IO;
using Chisel.Import.Source.VPKTools;

namespace AeternumGames.Chisel.Import.Source.ValveMapFormat2006
{
	/// <summary>
	/// Chooses which of its model's collision sets (<see cref="ModelCollisionSet"/>) an entity collides with, the way the
	/// game creates each entity's physics (Source SDK 2013). Where the code of a class is not public, the FGD's
	/// declaration of the class stands in for it.
	/// <list type="number">
	/// <item>vbsp compiles <c>prop_static</c> into the map, with collision unless its "solid" is 0 (staticprop.cpp), and
	/// <c>prop_detail</c> into detail props, which have none (detailobjects.cpp).</item>
	/// <item>A filter is logic, with no collision.</item>
	/// <item>An NPC - declared <c>@NPCClass</c>, or based on <c>BaseNPC</c> - collides with its movement hull, a box
	/// rather than its model's collision (CAI_BaseNPC::SetupVPhysicsHull). The hull is not imported.</item>
	/// <item><c>prop_ragdoll</c> collides with every solid on its bone (CRagdollProp, RagdollCreate).</item>
	/// <item><c>prop_dynamic_ornament</c> is never solid (COrnamentProp::DetachFromOwner).</item>
	/// <item>A dynamic prop - <c>prop_dynamic</c>, <c>prop_dynamic_override</c>, or any class based on
	/// <c>prop_dynamic_base</c> - has no collision when its "solid" is 0. Otherwise, when its model has more than one
	/// solid and "DisableBoneFollowers" is not set, it collides through bone followers, one per solid on its bone. Those
	/// stay solid even with spawnflag 256, "Start with collision disabled", which only makes the prop itself non-solid -
	/// and so leaves a prop without followers with no collision. (CDynamicProp::Spawn, CreateVPhysics and
	/// CreateBoneFollowers.)</item>
	/// <item>Everything else collides as a prop: the model's first solid, at the entity (PhysModelCreate), which is how
	/// CPhysicsProp creates its physics.</item>
	/// </list>
	/// Two things the game does are not imported. A prop whose "solid" is 2 collides with a box around its model, measured
	/// by engine code that is not public, so it keeps its model's own solid instead. And a model can name the bones that
	/// get followers in its keyvalues, which none of Black Mesa's do, so followers are always made for every solid.
	/// </summary>
	public static class VmfModelCollision
	{
		// SF_DYNAMICPROP_DISABLE_COLLISION.
		const int kDynamicPropDisableCollision = 256;

		static readonly Dictionary<string, int> s_SolidCounts = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>Forgets the solid counts read so far, so the next import reads the game's files again.</summary>
		public static void ResetCaches()
		{
			s_SolidCounts.Clear();
		}

		public static ModelCollisionSet ChooseSet(GameResources gameResources, VmfEntity entity, string model)
		{
			return ChooseSet(gameResources.EntityClasses, entity, () => SolidCount(gameResources, model));
		}

		/// <param name="modelSolidCount">How many solids the model's .phy holds. Only called when that decides.</param>
		public static ModelCollisionSet ChooseSet(FgdEntityClasses classes, VmfEntity entity, Func<int> modelSolidCount)
		{
			var className = entity.ClassName;

			// vbsp recognises these with strcmp.
			if (className == "prop_static" || className == "static_prop")
				return (entity.GetInteger("solid") == 0) ? ModelCollisionSet.None : ModelCollisionSet.Prop;
			if (className == "prop_detail" || className == "detail_prop")
				return ModelCollisionSet.None;

			var kind = classes.GetKind(className);
			if (kind == FgdClassKind.Filter || kind == FgdClassKind.NPC || classes.DerivesFrom(className, "BaseNPC"))
				return ModelCollisionSet.None;

			if (IsClass(className, "prop_ragdoll") || IsClass(className, "physics_prop_ragdoll") || IsClass(className, "prop_ragdoll_attached"))
				return ModelCollisionSet.Ragdoll;
			if (IsClass(className, "prop_dynamic_ornament"))
				return ModelCollisionSet.None;

			if (IsClass(className, "prop_dynamic") || IsClass(className, "dynamic_prop") || IsClass(className, "prop_dynamic_override") ||
				classes.DerivesFrom(className, "prop_dynamic_base"))
			{
				if (entity.GetInteger("solid") == 0)
					return ModelCollisionSet.None;
				if (entity.GetInteger("DisableBoneFollowers") == 0 && modelSolidCount() > 1)
					return ModelCollisionSet.Ragdoll;
				if ((entity.GetInteger("spawnflags") & kDynamicPropDisableCollision) != 0)
					return ModelCollisionSet.None;
			}
			return ModelCollisionSet.Prop;
		}

		// The game finds an entity's class whatever its case.
		static bool IsClass(string className, string name)
		{
			return string.Equals(className, name, StringComparison.OrdinalIgnoreCase);
		}

		// The number of solids in the model's .phy: vcollide_t.solidCount, which CDynamicProp::CreateBoneFollowers counts.
		static int SolidCount(GameResources gameResources, string model)
		{
			if (s_SolidCounts.TryGetValue(model, out var count))
				return count;

			var entry = gameResources.GetEntry(Path.ChangeExtension(model, PackagePath.ExtensionPHY));
			count = (entry != null) ? (gameResources.LoadPHY(entry)?.Solids.Count ?? 0) : 0;
			s_SolidCounts[model] = count;
			return count;
		}
	}
}
