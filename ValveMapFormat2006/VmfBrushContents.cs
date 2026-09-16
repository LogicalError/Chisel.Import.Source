using System;
using System.Collections.Generic;
using Chisel.Core;
using Chisel.Import.Source.VPKTools;

namespace AeternumGames.Chisel.Import.Source.ValveMapFormat2006
{
    /// <summary>
    /// What a Hammer brush is made of, as a Chisel contents type. vbsp decides it from the brush's sides: any side
    /// that is glass (a window), a grate, slime or water makes the whole brush that, and a brush with several of
    /// those gets the first of window, grate, slime, water. Such a brush is named after its type ("Glass 247412")
    /// and gets that entry of the project's contents list, which is added to the list when it isn't there yet.
    /// </summary>
    public static class VmfBrushContents
    {
        /// <summary>The contents index and the name of a brush with these sides</summary>
        public static (int contents, string name) Of(VmfSolid solid, GameResources gameResources)
        {
            var materials = new List<string>(solid.Sides.Count);
            foreach (var side in solid.Sides)
                materials.Add(side.Material);
            var typeName = TypeNameOf(VMTContents.VisibleType(BrushContentsOf(materials, gameResources.GetMaterialContents)));
            if (typeName == null)
                return (ChiselContentsList.kSolidIndex, ChiselContentsList.kSolidName);

            var index = ChiselContentsList.FindOrAppendInProject(typeName);
            if (index < 0)
                return (ChiselContentsList.kSolidIndex, ChiselContentsList.kSolidName);
            // The list's own spelling, so the object reads like the dropdown
            return (index, ChiselContentsList.Instance.NameOf(index));
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
    }
}
