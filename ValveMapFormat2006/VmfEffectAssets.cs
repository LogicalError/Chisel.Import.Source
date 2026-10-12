using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Chisel.Import.Source.VPKTools;

namespace AeternumGames.Chisel.Import.Source.ValveMapFormat2006
{
	/// <summary>
	/// The materials and meshes the map importer makes for effects (glows, spotlight beams, tinted sprites, lens flares),
	/// kept as assets rather than in the scene, and shared by every map that uses them:
	/// <list type="bullet">
	/// <item>a base material per effect texture and shader, in the materials tree beside the game's material for it;</item>
	/// <item>a Material Variant of a material per setting that differs (a tint, a fade distance), beside that material;</item>
	/// <item>meshes under effects/ in the import folder.</item>
	/// </list>
	/// </summary>
	/// <remarks>
	/// Like the importer's other generated assets they carry a version stamp (<see cref="GeneratedAssetVersion"/>), and
	/// one written by older code is rebuilt in place, so its GUID and every scene pointing at it survive.
	/// </remarks>
	internal static class VmfEffectAssets
	{
		/// <summary>Bump when what these assets are built with changes.</summary>
		/// <remarks>2: the dust spawn meshes are triangles (a ParticleSystem's shape module takes no points).</remarks>
		public static readonly (string generator, int version) GeneratedVersion = ("effect-asset", 2);

		/// <summary>
		/// The base material that draws an effect's texture with the shader: "materials/&lt;material&gt; (&lt;purpose&gt;).mat"
		/// in the import folder, beside the material the game names for the effect. It is kept in the materials tree, apart
		/// from the textures, so deleting the materials only makes them build again.
		/// </summary>
		public static Material GetBaseMaterial(string materialName, Texture2D texture, Shader shader, string purpose)
		{
			if (string.IsNullOrEmpty(materialName) || texture == null || shader == null)
				return null;

			// The name as the game's file system spells it, which is how the imported materials are laid out
			var name = materialName.Replace('\\', '/').ToLowerInvariant();
			if (name.StartsWith("materials/"))
				name = name.Substring("materials/".Length);
			if (name.EndsWith(".vmt"))
				name = name.Substring(0, name.Length - ".vmt".Length);

			var fullName = $"materials/{name} ({purpose}).mat";
			return GetOrBuild(PackagePath.GetAssetPath(fullName).Replace('\\', '/'),
							  () => new Material(shader) { mainTexture = texture },
							  existing => existing.shader == shader && existing.mainTexture == texture,
							  destinationPath: PackagePath.GetOutputPath(fullName));
		}

		/// <summary>
		/// A Material Variant of the parent: "&lt;parent&gt; &lt;key&gt;.mat" beside the parent. The key names the settings
		/// and <paramref name="configure"/> writes them. What it sets becomes the variant's overrides, and everything else
		/// follows the parent.
		/// </summary>
		public static Material GetMaterialVariant(Material parent, string key, Action<Material> configure)
		{
			if (parent == null)
				return null;

			var parentPath = AssetDatabase.GetAssetPath(parent);
			if (string.IsNullOrEmpty(parentPath))
			{
				Debug.LogError($"The material {parent.name} is not an asset, so it can't have variants");
				return null;
			}

			return GetOrBuild(SiblingPath(parentPath, $"{parent.name} {key}", ".mat"), () =>
			{
				var variant = new Material(parent) { parent = parent };
				configure(variant);
				return variant;
			}, existing => existing.parent == parent && CarriesSettings(existing, parent, configure),
			// Copied over an older asset, a variant doesn't bring its overrides along, so they are set on the asset again
			configure, together: true);
		}

		/// <summary>
		/// Whether the variant already has what <paramref name="configure"/> gives a copy of its parent: all of the shader's
		/// values match. A variant made while its parent's shader lacked a property it sets has no override for it, and is
		/// made again once the parent's shader has the property. The variant is only read: a copy of one whose saved values
		/// belong to another shader makes Unity complain about every one of them.
		/// </summary>
		static bool CarriesSettings(Material variant, Material parent, Action<Material> configure)
		{
			if (variant.shader != parent.shader)
				return false;
			var probe = new Material(parent);
			try
			{
				configure(probe);
				var shader = parent.shader;
				for (int i = 0; i < shader.GetPropertyCount(); i++)
				{
					var id = shader.GetPropertyNameId(i);
					var same = shader.GetPropertyType(i) switch
					{
						UnityEngine.Rendering.ShaderPropertyType.Color   => probe.GetColor(id) == variant.GetColor(id),
						UnityEngine.Rendering.ShaderPropertyType.Vector  => probe.GetVector(id) == variant.GetVector(id),
						UnityEngine.Rendering.ShaderPropertyType.Texture => probe.GetTexture(id) == variant.GetTexture(id),
						UnityEngine.Rendering.ShaderPropertyType.Int     => probe.GetInteger(id) == variant.GetInteger(id),
						_                                                => probe.GetFloat(id) == variant.GetFloat(id)
					};
					if (!same)
						return false;
				}
				return true;
			}
			finally
			{
				UnityEngine.Object.DestroyImmediate(probe);
			}
		}

		/// <summary>
		/// A name for an asset made from <paramref name="key"/>, which spells out everything it is made from: the same key
		/// always gives the same name, and a different one practically never does. The first 16 hex digits of its SHA-1.
		/// </summary>
		public static string ContentName(string key)
		{
			using var sha = System.Security.Cryptography.SHA1.Create();
			return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(key)))
							   .Replace("-", "").Substring(0, 16).ToLowerInvariant();
		}

		/// <summary>A mesh at effects/&lt;name&gt;.asset in the import folder.</summary>
		public static Mesh GetMesh(string name, Func<Mesh> build)
		{
			var fullName = $"effects/{name}.asset";
			return GetOrBuild(PackagePath.GetAssetPath(fullName).Replace('\\', '/'), build, existing => true, destinationPath: PackagePath.GetOutputPath(fullName),
							  together: true);
		}

		// The meshes and Material Variants made new while a batch is open (Together), by asset path, and in the order made
		static Dictionary<string, UnityEngine.Object> s_Unwritten;
		static List<(UnityEngine.Object asset, string assetPath)> s_UnwrittenInOrder;

		/// <summary>
		/// Opens a batch, which ends when it is disposed. Until then a mesh or a Material Variant that has to be written new
		/// is made and handed back at once, but written when the batch ends, with every other one, in ONE refresh
		/// (UnityAssets.CreateStampedTogether), where each would take one of its own.
		/// Until then it is no asset, and asking for it again hands back the same object. A base material is written at
		/// once: a variant's parent has to be an asset. VmfEntityConverter.Import opens one around its entities.
		/// </summary>
		public static Batch Together()
		{
			if (s_Unwritten != null)
				return new Batch(false);
			s_Unwritten = new Dictionary<string, UnityEngine.Object>();
			s_UnwrittenInOrder = new List<(UnityEngine.Object asset, string assetPath)>();
			return new Batch(true);
		}

		/// <summary>See <see cref="Together"/>. It ends once: ending it again, or one opened inside another, does nothing.</summary>
		public sealed class Batch : IDisposable
		{
			bool open;

			internal Batch(bool open) => this.open = open;

			public void Dispose()
			{
				if (!open)
					return;
				open = false;
				var unwritten = s_UnwrittenInOrder;
				s_Unwritten = null;
				s_UnwrittenInOrder = null;
				foreach (var (_, assetPath) in unwritten)
					PackagePath.EnsureDirectoriesExist(Path.GetFullPath(assetPath));
				UnityAssets.CreateStampedTogether(unwritten, GeneratedVersion);
			}
		}

		static string SiblingPath(string assetPath, string name, string extension)
		{
			foreach (var invalid in Path.GetInvalidFileNameChars())
				name = name.Replace(invalid, '_');
			return Path.Combine(Path.GetDirectoryName(assetPath), name + extension).Replace('\\', '/');
		}

		/// <summary>
		/// The asset at the path, built when it is missing, doesn't match or is older than <see cref="GeneratedVersion"/>.
		/// An old one is rebuilt in place, so its GUID survives; <paramref name="reapply"/> then runs on it, for what a copy
		/// doesn't carry. Either way it is written and stamped with one import (UnityAssets.CreateStamped, SaveStamped);
		/// writing it and then stamping it would take two, each a refresh of its own. <paramref name="together"/>: a new one
		/// is written when the batch open ends, if one is (<see cref="Together"/>).
		/// </summary>
		static T GetOrBuild<T>(string assetPath, Func<T> build, Func<T, bool> matches, Action<T> reapply = null, string destinationPath = null,
							   bool together = false)
			where T : UnityEngine.Object
		{
			if (s_Unwritten != null && s_Unwritten.TryGetValue(assetPath, out var unwritten))
				return (T)unwritten;

			var existing = AssetDatabase.LoadAssetAtPath<T>(assetPath);
			if (existing != null && matches(existing) && GeneratedAssetVersion.IsCurrent(assetPath, GeneratedVersion))
				return existing;

			var built = build();
			built.name = Path.GetFileNameWithoutExtension(assetPath);
			if (existing != null)
			{
				EditorUtility.CopySerialized(built, existing);
				existing.name = built.name;
				reapply?.Invoke(existing);
				UnityEngine.Object.DestroyImmediate(built);
				UnityAssets.SaveStamped(existing, GeneratedVersion);
			}
			else if (together && s_Unwritten != null)
			{
				s_Unwritten.Add(assetPath, built);
				s_UnwrittenInOrder.Add((built, assetPath));
				existing = built;
			}
			else
			{
				PackagePath.EnsureDirectoriesExist(destinationPath ?? Path.GetFullPath(assetPath));
				UnityAssets.CreateStamped(built, assetPath, GeneratedVersion);
				existing = built;
			}
			return existing;
		}
	}
}
