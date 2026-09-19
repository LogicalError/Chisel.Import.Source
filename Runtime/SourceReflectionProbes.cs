using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace AeternumGames.Chisel.Import.Source.ValveMapFormat2006
{
	/// <summary>
	/// The reflection probes a Source level has at its env_cubemap entities, handed to the Source shaders
	/// (ChiselSourceProbes.hlsl in the VPK tools): the probes' baked cubemaps packed into one cubemap array, and a volume
	/// whose cells each hold the index of the probe nearest to them.
	/// <para>
	/// Source gives a surface the env_cubemap nearest to it - vbsp per brush side, the engine per model. Chisel's world
	/// renderers each span the whole map, and URP takes at most 64 visible probes where a map can have hundreds, so the
	/// shaders look the nearest probe up per pixel instead. The probes stay ordinary ReflectionProbe components, baked by
	/// Unity with the scene's lighting, which Unity's own shaders use as usual; the array is packed again whenever Unity
	/// has baked them.
	/// </para>
	/// <para>
	/// The shader data is global, so of several of these the one enabled last is the one in use.
	/// </para>
	/// </summary>
	[ExecuteAlways, DisallowMultipleComponent]
	public sealed class SourceReflectionProbes : MonoBehaviour
	{
		/// <summary>The probes, in the order the index volume numbers them</summary>
		public ReflectionProbe[] probes = new ReflectionProbe[0];

		/// <summary>For each cell, the index of the nearest probe (R16)</summary>
		public Texture3D index;

		/// <summary>The volume's corner, in world space</summary>
		public Vector3 volumeMin;

		/// <summary>The cells' size, in world units</summary>
		public float cellSize = 1;

		/// <summary>The most probes the shaders take</summary>
		public const int kMaxProbes = 256;

		static readonly int kProbesID      = Shader.PropertyToID("_SourceProbes");
		static readonly int kIndexID       = Shader.PropertyToID("_SourceProbeIndex");
		static readonly int kVolumeMinID   = Shader.PropertyToID("_SourceProbeVolumeMin");
		static readonly int kVolumeCellsID = Shader.PropertyToID("_SourceProbeVolumeCells");
		static readonly int kHDRID         = Shader.PropertyToID("_SourceProbes_HDR");
		static readonly int kIntensityID   = Shader.PropertyToID("_SourceProbeIntensity");

		static SourceReflectionProbes s_Current;
		static CubemapArray s_Black;
		static Texture3D s_EmptyIndex;

		CubemapArray packed;

		void OnEnable()
		{
			Apply();
#if UNITY_EDITOR
			// The probes' textures change when Unity bakes them (Generate Lighting, Bake Reflection Probes)
			UnityEditor.Lightmapping.bakeCompleted += Apply;
			UnityEditor.Lightmapping.lightingDataUpdated += Apply;
#endif
		}

		void OnDisable()
		{
#if UNITY_EDITOR
			UnityEditor.Lightmapping.bakeCompleted -= Apply;
			UnityEditor.Lightmapping.lightingDataUpdated -= Apply;
#endif
			if (s_Current == this)
			{
				// Without probes the shaders reflect Unity's own reflection, the sky; the black array only keeps a
				// texture of the right kind bound in place of the packed one, which goes
				s_Current = null;
				SetShaderData(BlackProbes(), null, Vector3.zero, 1, new Vector4(1, 1, 0, 0), null, available: false);
			}
			Release();
		}

		/// <summary>
		/// Packs the probes' textures and hands them to the shaders. It runs when the component is enabled and, in the editor,
		/// after every bake. Probes whose textures can't be packed together - not baked yet, or of another size or format -
		/// leave the shaders reflecting black.
		/// </summary>
		public void Apply()
		{
			Release();
			s_Current = this;

			if (probes == null || probes.Length == 0 || probes.Length > kMaxProbes || index == null)
			{
				Debug.LogWarning($"{name}: no reflection probes to hand to the Source shaders", this);
				Suspend();
				return;
			}

			// Not baked yet is a state of its own, until Unity bakes the scene's lighting
			if (System.Array.TrueForAll(probes, probe => probe == null || probe.texture == null))
			{
				Suspend();
				return;
			}

			var first = probes[0] != null ? probes[0].texture : null;
			foreach (var probe in probes)
			{
				var texture = probe != null ? probe.texture : null;
				if (texture == null || first == null || texture.width != first.width || texture.graphicsFormat != first.graphicsFormat ||
					texture.mipmapCount != first.mipmapCount || texture.dimension != UnityEngine.Rendering.TextureDimension.Cube)
				{
					Debug.LogWarning($"{name}: reflection probe {(probe != null ? probe.name : "(missing)")} has no baked cubemap like the others; " +
									 "bake the scene's lighting again", this);
					Suspend();
					return;
				}
			}

			packed = new CubemapArray(first.width, probes.Length, first.graphicsFormat, TextureCreationFlags.MipChain, first.mipmapCount)
			{
				name       = name + " (packed probes)",
				hideFlags  = HideFlags.HideAndDontSave,
				filterMode = FilterMode.Trilinear,
				wrapMode   = TextureWrapMode.Clamp
			};
			var intensities = new float[kMaxProbes];
			var decode = probes[0].textureHDRDecodeValues;
			for (int i = 0; i < probes.Length; i++)
			{
				for (int face = 0; face < 6; face++)
					Graphics.CopyTexture(probes[i].texture, face, packed, i * 6 + face);
				// Each probe's own decode scale, which carries its intensity, relative to the one shared by all
				intensities[i] = (decode.x != 0) ? probes[i].textureHDRDecodeValues.x / decode.x : 1;
			}
			SetShaderData(packed, index, volumeMin, cellSize, decode, intensities, available: true);
		}

		/// <summary>Hands the shaders black reflections, as while the probes themselves are being baked.</summary>
		public void Suspend()
		{
			SetShaderData(BlackProbes(), null, Vector3.zero, 1, new Vector4(1, 1, 0, 0), null, available: true);
		}

		static CubemapArray BlackProbes()
		{
			if (s_Black == null)
			{
				s_Black = new CubemapArray(1, 1, GraphicsFormat.R16G16B16A16_SFloat, TextureCreationFlags.None)
				{
					name = "Source Probes (black)", hideFlags = HideFlags.HideAndDontSave
				};
				var black = new Color[1];
				for (int face = 0; face < 6; face++)
					s_Black.SetPixels(black, (CubemapFace)face, 0);
				s_Black.Apply();
			}
			return s_Black;
		}

		static void SetShaderData(Texture probes, Texture3D index, Vector3 min, float cellSize, Vector4 decode, float[] intensities, bool available)
		{
			if (s_EmptyIndex == null)
			{
				s_EmptyIndex = new Texture3D(1, 1, 1, GraphicsFormat.R16_UNorm, TextureCreationFlags.None)
				{
					name = "Source Probe Index (empty)", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point
				};
				s_EmptyIndex.SetPixels(new[] { Color.clear });
				s_EmptyIndex.Apply();
			}
			if (index == null)
				index = s_EmptyIndex;
			Shader.SetGlobalTexture(kProbesID, probes);
			Shader.SetGlobalTexture(kIndexID, index);
			Shader.SetGlobalVector(kVolumeMinID, new Vector4(min.x, min.y, min.z, available ? 1 : 0));
			Shader.SetGlobalVector(kVolumeCellsID, new Vector4(index.width, index.height, index.depth, 1.0f / Mathf.Max(cellSize, 1e-4f)));
			Shader.SetGlobalVector(kHDRID, decode);
			Shader.SetGlobalFloatArray(kIntensityID, intensities ?? s_Ones);
		}

		static readonly float[] s_Ones = CreateOnes();

		static float[] CreateOnes()
		{
			var ones = new float[kMaxProbes];
			for (int i = 0; i < ones.Length; i++)
				ones[i] = 1;
			return ones;
		}

		void Release()
		{
			if (packed == null)
				return;
			if (Application.isPlaying)
				Destroy(packed);
			else
				DestroyImmediate(packed);
			packed = null;
		}
	}
}
