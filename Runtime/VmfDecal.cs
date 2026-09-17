using System;
using UnityEngine;

namespace AeternumGames.Chisel.Import.Source.ValveMapFormat2006
{
	/// <summary>
	/// A Source decal entity - <c>infodecal</c>, <c>info_overlay</c> or <c>info_projecteddecal</c> - imported
	/// as data. The importer draws it with Chisel decals in the "Decals" group of the model it lands on
	/// (<c>VmfChiselDecals</c>).
	/// <para>
	/// Source places each kind by different rules, so the values it places them by are kept as the map wrote
	/// them, in Source's own units and axes. Whatever draws these later can then start from the real rules
	/// instead of from a choice made at import time:
	/// </para>
	/// <list type="bullet">
	/// <item><c>infodecal</c> is a point and a material. When the map spawns, the game traces a line from
	/// origin-(5,5,5) to origin+(5,5,5) only to choose ONE entity: the first solid one the line hits, skipping
	/// weapon_*, item_*, prop_static, prop_dynamic, prop_physics, prop_ragdoll, npc_bullseye and anything that
	/// is not drawn, or the world when it hits nothing else. The engine is then given that entity and the point,
	/// and places the decal itself (<c>CDecal::StaticDecal</c>). A named infodecal is applied when it is fired
	/// instead, and then the line only hits brushes (<c>CDecal::TriggerDecal</c>).</item>
	/// <item><c>info_overlay</c> is a quad in its own basis. The map compiler clips it onto the brush faces its
	/// <c>sides</c> key lists, and onto nothing else (vbsp <c>Overlay_GetFromEntity</c>).</item>
	/// <item><c>info_projecteddecal</c> is sent to the client with its angles and <c>Distance</c>, and the client
	/// projects it (<c>CProjectedDecal::ProjectDecal</c>).</item>
	/// </list>
	/// <para>
	/// It lives in a runtime assembly, the importer's own being editor-only, because Unity only attaches
	/// components whose scripts are in one. Its GameObject is tagged EditorOnly, so this data doesn't reach a
	/// player build; the Chisel decals that draw it are not tagged.
	/// </para>
	/// </summary>
	[DisallowMultipleComponent]
	public sealed class VmfDecal : MonoBehaviour
	{
		public enum DecalType
		{
			InfoDecal,
			InfoOverlay,
			InfoProjectedDecal
		}

		/// <summary>A brush face an info_overlay lists, looked up in the map it came from.</summary>
		[Serializable]
		public struct Side
		{
			/// <summary>The Hammer side id, as the overlay lists it.</summary>
			public int sideId;

			/// <summary>The Hammer id of the solid the side belongs to, or -1 when no solid in the map has that side.</summary>
			public int solidId;

			/// <summary>The side's position among its solid's sides, or -1 when unresolved.</summary>
			public int sideIndex;

			/// <summary>The Hammer id of the brush entity that owns the solid, or -1 for world geometry.</summary>
			public int entityId;
		}

		public DecalType type;

		/// <summary>The Hammer id of the entity.</summary>
		public int entityId;

		/// <summary><c>targetname</c>. A named infodecal is applied when it is fired rather than when the map spawns.</summary>
		public string targetName = "";

		/// <summary>The material: <c>texture</c> for infodecal and info_projecteddecal, <c>material</c> for
		/// info_overlay.</summary>
		public string material = "";

		/// <summary><c>origin</c>, in Source units. vbsp does not read it for an info_overlay, which it places by
		/// <see cref="basisOrigin"/>.</summary>
		public Vector3 origin;

		/// <summary><c>spawnflags</c>. infodecal and info_projecteddecal both remove themselves in deathmatch when
		/// their SF_DECAL_NOTINDEATHMATCH flag is set.</summary>
		public int spawnFlags;

		/// <summary>infodecal <c>LowPriority</c>, which the game hands to the engine along with the decal.</summary>
		[Header("infodecal")]
		public bool lowPriority;

		/// <summary>info_projecteddecal <c>angles</c>: Source pitch, yaw and roll, in degrees.</summary>
		[Header("info_projecteddecal")]
		public Vector3 angles;

		/// <summary>info_projecteddecal <c>Distance</c>, in Source units: how far the client projects it.</summary>
		public float distance;

		/// <summary>info_overlay <c>BasisOrigin</c>, in Source units.</summary>
		[Header("info_overlay")]
		public Vector3 basisOrigin;

		/// <summary>info_overlay <c>BasisU</c>.</summary>
		public Vector3 basisU;

		/// <summary>info_overlay <c>BasisV</c>.</summary>
		public Vector3 basisV;

		/// <summary>info_overlay <c>BasisNormal</c>.</summary>
		public Vector3 basisNormal;

		/// <summary>info_overlay <c>uv0</c> to <c>uv3</c>: the quad's corners, as distances along <see cref="basisU"/>
		/// and <see cref="basisV"/> from <see cref="basisOrigin"/>, in Source units.</summary>
		public Vector3[] corners = new Vector3[4];

		/// <summary>info_overlay <c>StartU</c> and <c>EndU</c>.</summary>
		public Vector2 textureU;

		/// <summary>info_overlay <c>StartV</c> and <c>EndV</c>.</summary>
		public Vector2 textureV;

		/// <summary>info_overlay <c>RenderOrder</c>. vbsp refuses to compile a map with a value outside 0 to 3.</summary>
		public int renderOrder;

		/// <summary>info_overlay <c>fademindist</c>, in Source units as written. vbsp squares it when it is positive
		/// and stores it for the engine.</summary>
		public float fadeMinDistance;

		/// <summary>info_overlay <c>fademaxdist</c>, in Source units as written. vbsp squares it when it is positive
		/// and stores it for the engine.</summary>
		public float fadeMaxDistance;

		/// <summary>info_overlay <c>sides</c>: the faces it covers, and the only ones.</summary>
		public Side[] sides = Array.Empty<Side>();
	}
}
