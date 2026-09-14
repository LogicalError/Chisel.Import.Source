using System;
using System.Collections.Generic;
using System.Globalization;
using Chisel.Import.Source.VPKTools;
using UnityEngine;

namespace AeternumGames.Chisel.Import.Source.ValveMapFormat2006
{
	/// <summary>
	/// Imports Source's decal entities as <see cref="VmfDecal"/> data, without drawing them. See
	/// <see cref="VmfDecal"/> for how differently Source places each kind.
	/// <para>
	/// Values are read the way vbsp and the game read them: keys regardless of case (see
	/// <see cref="VmfEntity.Properties"/>), a missing number as 0 - what atof and atoi make of the empty string
	/// a missing key reads as - and integer keys without their fraction, as atoi and atol stop at the point.
	/// </para>
	/// </summary>
	public static class VmfDecalConverter
	{
		const string kGroupName     = "Decals";
		const string kEditorOnlyTag = "EditorOnly";

		/// <summary>Whether the entity is one of the decal entities this converter imports.</summary>
		public static bool IsDecal(VmfEntity entity)
		{
			switch (entity?.ClassName)
			{
				case "infodecal":
				case "info_overlay":
				case "info_projecteddecal":
					return true;
				default:
					return false;
			}
		}

		/// <summary>
		/// Where every brush side of a map is: its solid, its position on that solid, and the brush entity
		/// that owns the solid. An info_overlay names the faces it covers by side id alone.
		/// </summary>
		public sealed class SideIndex
		{
			readonly Dictionary<int, VmfDecal.Side> sides = new();

			public SideIndex(VmfWorld world)
			{
				Add(world.Solids, -1);
				foreach (var entity in world.Entities)
					Add(entity.Solids, entity.Id);
			}

			void Add(List<VmfSolid> solids, int entityId)
			{
				foreach (var solid in solids)
				{
					for (int i = 0; i < solid.Sides.Count; i++)
					{
						sides[solid.Sides[i].Id] = new VmfDecal.Side
						{
							sideId    = solid.Sides[i].Id,
							solidId   = solid.Id,
							sideIndex = i,
							entityId  = entityId
						};
					}
				}
			}

			public VmfDecal.Side Resolve(int sideId)
			{
				return sides.TryGetValue(sideId, out var side) ? side : Unresolved(sideId);
			}
		}

		/// <summary>
		/// Creates the decal's GameObject under the map's Decals group and fills in its <see cref="VmfDecal"/>.
		/// Returns null for an entity that is not a decal.
		/// </summary>
		public static VmfDecal Import(Transform parent, VmfEntity entity, SideIndex sideIndex)
		{
			if (!IsDecal(entity))
				return null;

			var type = entity.ClassName switch
			{
				"info_overlay"        => VmfDecal.DecalType.InfoOverlay,
				"info_projecteddecal" => VmfDecal.DecalType.InfoProjectedDecal,
				_                     => VmfDecal.DecalType.InfoDecal
			};

			var material   = GetText(entity, (type == VmfDecal.DecalType.InfoOverlay) ? "material" : "texture");
			var gameObject = new GameObject($"{entity.ClassName} {entity.Id} {material}");
			gameObject.transform.SetParent(GetDecalsGroup(parent), false);
			// Editor data for now: the tag keeps it out of builds. The group is left untagged, because whatever
			// draws decals may well put objects there that a build does need.
			gameObject.tag = kEditorOnlyTag;

			var decal = gameObject.AddComponent<VmfDecal>();
			decal.type       = type;
			decal.entityId   = entity.Id;
			decal.targetName = GetText(entity, "targetname");
			decal.material   = material;
			decal.origin     = GetVector(entity, "origin");
			decal.spawnFlags = GetInteger(entity, "spawnflags");

			var position = decal.origin;
			var rotation = Quaternion.identity;
			switch (type)
			{
				case VmfDecal.DecalType.InfoDecal:
				{
					// The engine is given only the point, so there is no direction to import.
					decal.lowPriority = GetInteger(entity, "LowPriority") != 0;
					break;
				}
				case VmfDecal.DecalType.InfoProjectedDecal:
				{
					decal.angles   = GetVector(entity, "angles");
					decal.distance = GetFloat(entity, "Distance");

					// Looking along the forward direction of its angles.
					SourceEngineUnits.AngleVectors(decal.angles, out var forward, out _, out var up);
					rotation = LookRotation(ToUnityDirection(forward), ToUnityDirection(up));
					break;
				}
				case VmfDecal.DecalType.InfoOverlay:
				{
					decal.basisOrigin = GetVector(entity, "BasisOrigin");
					decal.basisU      = GetVector(entity, "BasisU");
					decal.basisV      = GetVector(entity, "BasisV");
					decal.basisNormal = GetVector(entity, "BasisNormal");
					for (int i = 0; i < decal.corners.Length; i++)
						decal.corners[i] = GetVector(entity, "uv" + i);

					decal.textureU        = new Vector2(GetFloat(entity, "StartU"), GetFloat(entity, "EndU"));
					decal.textureV        = new Vector2(GetFloat(entity, "StartV"), GetFloat(entity, "EndV"));
					decal.renderOrder     = GetInteger(entity, "RenderOrder");
					decal.fadeMinDistance = GetFloat(entity, "fademindist");
					decal.fadeMaxDistance = GetFloat(entity, "fademaxdist");

					var sideIds = GetSideIds(entity);
					decal.sides = new VmfDecal.Side[sideIds.Count];
					for (int i = 0; i < sideIds.Count; i++)
						decal.sides[i] = (sideIndex != null) ? sideIndex.Resolve(sideIds[i]) : Unresolved(sideIds[i]);

					// Placed where vbsp places it, looking into the surface, with the texture's v axis up.
					position = decal.basisOrigin;
					rotation = LookRotation(-ToUnityDirection(decal.basisNormal), ToUnityDirection(decal.basisV));
					break;
				}
			}

			gameObject.transform.localPosition = ToUnityPosition(position);
			gameObject.transform.localRotation = rotation;

			// A hidden entity is skipped by the map compiler as well, so it is not in the game either.
			if (entity.IsHidden)
				gameObject.SetActive(false);
			return decal;
		}

		static VmfDecal.Side Unresolved(int sideId)
		{
			return new VmfDecal.Side { sideId = sideId, solidId = -1, sideIndex = -1, entityId = -1 };
		}

		static Transform GetDecalsGroup(Transform parent)
		{
			var group = parent.Find(kGroupName);
			if (group == null)
			{
				group = new GameObject(kGroupName).transform;
				group.SetParent(parent, false);
			}
			return group;
		}

		// The same conversion every other entity position goes through (VmfEntityConverter.TryGetEntityOrigin).
		static Vector3 ToUnityPosition(Vector3 source)
		{
			return SourceEngineUnits.Swizzle(source) * SourceEngineUnits.VmfInvMeters;
		}

		static Vector3 ToUnityDirection(Vector3 source)
		{
			return SourceEngineUnits.Swizzle(source);
		}

		static Quaternion LookRotation(Vector3 forward, Vector3 up)
		{
			if (forward.sqrMagnitude < 1e-8f)
				return Quaternion.identity;
			if (Vector3.Cross(forward, up).sqrMagnitude < 1e-8f)
				return Quaternion.LookRotation(forward);
			return Quaternion.LookRotation(forward, up);
		}

		static string GetText(VmfEntity entity, string key)
		{
			return entity.Properties.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" : "";
		}

		static float GetFloat(VmfEntity entity, string key)
		{
			return entity.TryGetProperty(key, out float value) ? value : 0;
		}

		static int GetInteger(VmfEntity entity, string key)
		{
			return (int)GetFloat(entity, key);
		}

		static Vector3 GetVector(VmfEntity entity, string key)
		{
			return entity.TryGetProperty(key, out VmfVector3 value) ? new Vector3(value.X, value.Y, value.Z) : Vector3.zero;
		}

		/// <summary>
		/// The <c>sides</c> key: side ids separated by spaces, which vbsp reads one token at a time. The map
		/// parser types a value by its shape, so the same key arrives as an int for one id, a VmfVector3 for three,
		/// a VmfVector4 for four and a string for any other count. Ids stay exact through the vectors' floats up
		/// to 2^24.
		/// </summary>
		static List<int> GetSideIds(VmfEntity entity)
		{
			var result = new List<int>();
			if (!entity.Properties.TryGetValue("sides", out var value))
				return result;

			switch (value)
			{
				case int id:
					result.Add(id);
					break;
				case VmfVector3 ids:
					result.Add((int)ids.X);
					result.Add((int)ids.Y);
					result.Add((int)ids.Z);
					break;
				case VmfVector4 ids:
					result.Add((int)ids.X);
					result.Add((int)ids.Y);
					result.Add((int)ids.Z);
					result.Add((int)ids.W);
					break;
				case string text:
					foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
					{
						if (int.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id))
							result.Add(id);
					}
					break;
			}
			return result;
		}
	}
}
