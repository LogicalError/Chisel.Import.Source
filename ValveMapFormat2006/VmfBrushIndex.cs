using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Chisel.Components;
using Chisel.Core;

namespace AeternumGames.Chisel.Import.Source.ValveMapFormat2006
{
	/// <summary>
	/// The brush the world import built for each solid, and which of the brush's surfaces shows each of its sides,
	/// so that the entity import can put decals on the faces Source puts them on.
	/// <para>
	/// A side's surface is not simply its index: <c>BrushMeshFactory.CreateFromPlanes</c> makes one polygon per side
	/// plane that is part of the brush, with the side's index in its <c>descriptionIndex</c>, and a
	/// <see cref="ChiselBrushComponent"/> numbers its surfaces by polygon (<c>ChiselBrushDefinition.UpdateSurfaces</c>).
	/// So side k shows as the surface whose polygon had <c>descriptionIndex</c> k, and a side whose plane made no
	/// polygon shows nowhere.
	/// </para>
	/// </summary>
	public sealed class VmfBrushIndex
	{
		public const int kNoSurface = -1;

		/// <summary>A solid's brush, and per side the brush surface that shows it and whether it is drawn.</summary>
		public readonly struct Entry
		{
			public readonly ChiselBrushComponent brush;
			public readonly int[] sideSurfaces;
			public readonly bool[] sideDrawn;

			public Entry(ChiselBrushComponent brush, int[] sideSurfaces, bool[] sideDrawn)
			{
				this.brush        = brush;
				this.sideSurfaces = sideSurfaces;
				this.sideDrawn    = sideDrawn;
			}
		}

		// The index of the last world import of each map, for the entity import of the same map
		static readonly ConditionalWeakTable<VmfWorld, VmfBrushIndex> s_Indices = new();

		readonly Dictionary<int, Entry> solids = new();

		VmfBrushIndex() { }

		/// <summary>Starts a new index for a world import.</summary>
		public static VmfBrushIndex Begin(VmfWorld world)
		{
			var index = new VmfBrushIndex();
			if (world != null)
			{
				s_Indices.Remove(world);
				s_Indices.Add(world, index);
			}
			return index;
		}

		/// <summary>The index the last world import of this map built, or null.</summary>
		public static VmfBrushIndex Of(VmfWorld world)
		{
			return (world != null && s_Indices.TryGetValue(world, out var index)) ? index : null;
		}

		public int Count => solids.Count;

		public IEnumerable<Entry> Entries => solids.Values;

		/// <summary>
		/// Records the brush built for a solid. brushMesh is what <c>CreateFromPlanes</c> made of the solid's side
		/// planes, in side order; sideDrawn says per side whether Source draws it.
		/// </summary>
		// outline: the one the CSG derived from the brush's planes (ChiselBrushDefinition.SetPlanes), null when they make none
		public void Add(VmfSolid solid, ChiselBrushComponent brush, BrushMesh outline, bool[] sideDrawn)
		{
			if (solid == null || brush == null)
				return;
			solids[solid.Id] = new Entry(brush, SideSurfaces(solid.Sides.Count, outline), sideDrawn);
		}

		/// <summary>
		/// Per side of a brush given as its sides' planes, the surface that shows it, or <see cref="kNoSurface"/> when the side
		/// makes no face. Such a brush has one surface per plane, so a side's surface is its own index, and the derived outline
		/// has a face for it (descriptionIndex = the plane) only when the side bounds the brush.
		/// </summary>
		public static int[] SideSurfaces(int sideCount, BrushMesh outline)
		{
			var result = new int[sideCount];
			for (int i = 0; i < result.Length; i++)
				result[i] = kNoSurface;
			if (outline?.polygons == null)
				return result;
			foreach (var polygon in outline.polygons)
			{
				var side = polygon.descriptionIndex;
				if (side >= 0 && side < result.Length)
					result[side] = side;
			}
			return result;
		}

		public bool TryGet(int solidId, out Entry entry)
		{
			return solids.TryGetValue(solidId, out entry);
		}

		/// <summary>The brush and surface that show a side, when the side is drawn.</summary>
		public bool TryGetDrawnSurface(int solidId, int sideIndex, out ChiselBrushComponent brush, out int surfaceIndex)
		{
			brush = null;
			surfaceIndex = kNoSurface;
			if (!solids.TryGetValue(solidId, out var entry) || entry.brush == null ||
				sideIndex < 0 || sideIndex >= entry.sideSurfaces.Length ||
				entry.sideSurfaces[sideIndex] == kNoSurface ||
				(entry.sideDrawn != null && sideIndex < entry.sideDrawn.Length && !entry.sideDrawn[sideIndex]))
				return false;
			brush = entry.brush;
			surfaceIndex = entry.sideSurfaces[sideIndex];
			return true;
		}
	}
}
