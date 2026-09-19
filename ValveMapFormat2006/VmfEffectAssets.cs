using System;
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
		public static readonly (string generator, int version) GeneratedVersion = ("effect-asset", 1);

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
							  PackagePath.GetOutputPath(fullName));
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
			}, existing => existing.parent == parent);
		}

		/// <summary>A mesh at effects/&lt;name&gt;.asset in the import folder.</summary>
		public static Mesh GetMesh(string name, Func<Mesh> build)
		{
			var fullName = $"effects/{name}.asset";
			return GetOrBuild(PackagePath.GetAssetPath(fullName).Replace('\\', '/'), build, existing => true, PackagePath.GetOutputPath(fullName));
		}

		static string SiblingPath(string assetPath, string name, string extension)
		{
			foreach (var invalid in Path.GetInvalidFileNameChars())
				name = name.Replace(invalid, '_');
			return Path.Combine(Path.GetDirectoryName(assetPath), name + extension).Replace('\\', '/');
		}

		static T GetOrBuild<T>(string assetPath, Func<T> build, Func<T, bool> matches, string destinationPath = null) where T : UnityEngine.Object
		{
			var existing = AssetDatabase.LoadAssetAtPath<T>(assetPath);
			if (existing != null && matches(existing) && GeneratedAssetVersion.IsCurrent(assetPath, GeneratedVersion))
				return existing;

			var built = build();
			built.name = Path.GetFileNameWithoutExtension(assetPath);
			if (existing != null)
			{
				// In place, so the GUID survives
				EditorUtility.CopySerialized(built, existing);
				existing.name = built.name;
				EditorUtility.SetDirty(existing);
				AssetDatabase.SaveAssetIfDirty(existing);
				UnityEngine.Object.DestroyImmediate(built);
			}
			else
			{
				PackagePath.EnsureDirectoriesExist(destinationPath ?? Path.GetFullPath(assetPath));
				AssetDatabase.CreateAsset(built, assetPath);
				existing = built;
			}

			var importer = AssetImporter.GetAtPath(assetPath);
			if (importer != null)
			{
				importer.userData = GeneratedAssetVersion.Stamp(importer.userData, GeneratedVersion);
				importer.SaveAndReimport();
			}
			return existing;
		}
	}
}
