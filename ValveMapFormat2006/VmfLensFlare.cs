using System;
using System.Collections.Generic;
using UnityEngine;
using Chisel.Import.Source.VPKTools;

namespace AeternumGames.Chisel.Import.Source.ValveMapFormat2006
{
	/// <summary>
	/// Black Mesa's env_lensflare: up to twenty screen-space elements around a light, which its client draws
	/// (C_Lensflare in bms/bin/client.dll; the keys are in bin/bms.fgd). The public SDK has none of it, so these rules are
	/// read off the client's code: the parameter parser (client.dll 0x1022f8d0), DrawModel (0x1022dbe0) and the element
	/// draw (0x1022e080). The drawing itself is the "Chisel/Import/LensFlare" shader. This builds its mesh: one quad per
	/// element, carrying the element's parameters in its vertex data.
	/// </summary>
	public static class VmfLensFlare
	{
		public enum ElementType
		{
			None  = 0,
			Disk  = 1,  // "DISK position width height [1]", and "CLA" parsed the same way
			Bow   = 2,  // "BOW size"
			Ray   = 3,  // "RAY width height"
			DiskH = 4   // "DISKH position width height offset"
		}

		public struct Element
		{
			public string      Texture;
			public ElementType Type;
			/// <summary>The numbers after the type, in order.</summary>
			public Vector4     Parameters;
			/// <summary>DISK's fifth word "1": the disk stands on its point, turned along the line.</summary>
			public bool        Turned;
			/// <summary>flareNN_intensity: with the light at the centre of the screen, a quarter of its height away, half.</summary>
			public Vector3     Intensity;
			/// <summary>flareNN_sizes, at the same three distances.</summary>
			public Vector3     Sizes;
			public Color32     Color;
		}

		public const int kMaxElements = 20;

		/// <summary>
		/// flareNN_params as the client parses it. It splits at spaces into at most five words of at most fifteen characters,
		/// in a string shorter than 64, and the first word names the type:
		/// <list type="bullet">
		/// <item>CLA or DISK: position width height [1]</item>
		/// <item>DISKH: position width height offset</item>
		/// <item>BOW: size</item>
		/// <item>RAY: width height</item>
		/// </list>
		/// Anything else, or the wrong number of words, leaves the element out. The numbers are read with atof.
		/// </summary>
		public static bool TryParseParameters(string text, out ElementType type, out Vector4 parameters, out bool turned)
		{
			type = ElementType.None;
			parameters = Vector4.zero;
			turned = false;
			if (string.IsNullOrEmpty(text) || text.Length >= 64)
				return false;

			var words = new List<string>();
			var word = new System.Text.StringBuilder();
			foreach (var character in text)
			{
				if (character == ' ')
				{
					if (word.Length > 0)
					{
						words.Add(word.ToString());
						word.Clear();
					}
					continue;
				}
				if (words.Count >= 5 || word.Length >= 15)
					return false;
				word.Append(character);
			}
			if (word.Length > 0)
				words.Add(word.ToString());
			if (words.Count == 0)
				return false;

			float Number(int index) => VmfEntity.ParseLeadingFloat(words[index]);
			switch (words[0])
			{
				case "CLA":
				case "DISK":
					if (words.Count != 4 && words.Count != 5)
						return false;
					type = ElementType.Disk;
					parameters = new Vector4(Number(1), Number(2), Number(3), 0);
					turned = words.Count == 5 && words[4] == "1";
					return true;
				case "DISKH":
					if (words.Count != 5)
						return false;
					type = ElementType.DiskH;
					parameters = new Vector4(Number(1), Number(2), Number(3), Number(4));
					return true;
				case "BOW":
					if (words.Count != 2)
						return false;
					type = ElementType.Bow;
					parameters = new Vector4(Number(1), 0, 0, 0);
					return true;
				case "RAY":
					if (words.Count != 3)
						return false;
					type = ElementType.Ray;
					parameters = new Vector4(Number(1), Number(2), 0, 0);
					return true;
				default:
					return false;
			}
		}

		/// <summary>
		/// A vector key the way the game's keyfields read one (UTIL_StringToFloatArray): atof on each word in turn, and 0
		/// for whatever the text runs out before. "1.0" is (1, 0, 0), and the "CLA 1.0 10 10" some maps wrote into a colour
		/// is (0, 1, 10).
		/// </summary>
		public static Vector3 ParseVector(string text)
		{
			var result = Vector3.zero;
			if (string.IsNullOrEmpty(text))
				return result;

			var front = 0;
			var position = 0;
			for (int j = 0; j < 3; j++)
			{
				result[j] = VmfEntity.ParseLeadingFloat(text.Substring(front));
				while (position < text.Length && text[position] <= ' ')
					position++;
				while (position < text.Length && text[position] > ' ')
					position++;
				if (position >= text.Length)
					break;
				position++;
				front = position;
			}
			return result;
		}

		/// <summary>The elements the entity draws: flare01..flare20 with a texture and parameters the client takes.</summary>
		public static List<Element> ReadElements(VmfEntity entity)
		{
			var elements = new List<Element>();
			for (int i = 1; i <= kMaxElements; i++)
			{
				var prefix = $"flare{i:00}_";
				var texture = entity.GetText(prefix + "texture");
				if (string.IsNullOrEmpty(texture))
					continue;
				if (!TryParseParameters(entity.GetText(prefix + "params"), out var type, out var parameters, out var turned))
					continue;

				var color = ParseVector(entity.GetText(prefix + "color"));
				elements.Add(new Element
				{
					Texture    = texture,
					Type       = type,
					Parameters = parameters,
					Turned     = turned,
					Intensity  = ParseVector(entity.GetText(prefix + "intensity")),
					Sizes      = ParseVector(entity.GetText(prefix + "sizes")),
					Color      = new Color32(ColorByte(color.x), ColorByte(color.y), ColorByte(color.z), 255)
				});
			}
			return elements;
		}

		static byte ColorByte(float value) => (byte)Mathf.Clamp((int)value, 0, 255);

		/// <summary>The textures the elements use, one per submesh, in the order they first appear.</summary>
		public static List<string> TexturesOf(IList<Element> elements)
		{
			var textures = new List<string>();
			foreach (var element in elements)
			{
				if (!textures.Contains(element.Texture))
					textures.Add(element.Texture);
			}
			return textures;
		}

		/// <summary>
		/// The flare's mesh, an asset shared by every flare with the same elements and settings (effects/lens
		/// flares/&lt;hash&gt;.asset). Its submeshes follow <see cref="TexturesOf"/>.
		/// </summary>
		public static Mesh GetMesh(IList<Element> elements, float attenuation, float proxyRadius, int style, bool sunFlare)
		{
			var key = new System.Text.StringBuilder();
			key.Append(FormattableString.Invariant($"{attenuation:R} {proxyRadius:R} {style} {sunFlare}"));
			foreach (var element in elements)
			{
				key.Append(FormattableString.Invariant(
					$"|{element.Texture} {(int)element.Type} {element.Parameters.x:R} {element.Parameters.y:R} {element.Parameters.z:R} {element.Parameters.w:R} {element.Turned} ")
				);
				key.Append(FormattableString.Invariant(
					$"{element.Intensity.x:R} {element.Intensity.y:R} {element.Intensity.z:R} {element.Sizes.x:R} {element.Sizes.y:R} {element.Sizes.z:R} {element.Color.r} {element.Color.g} {element.Color.b}"));
			}
			using var sha = System.Security.Cryptography.SHA1.Create();
			var hash = System.BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(key.ToString())))
								.Replace("-", "").Substring(0, 16).ToLowerInvariant();
			return VmfEffectAssets.GetMesh($"lens flares/{hash}", () => BuildMesh(elements, attenuation, proxyRadius, style, sunFlare));
		}

		/// <summary>
		/// One quad per element, a submesh per texture as <see cref="TexturesOf"/> orders them. The vertex data is what the
		/// LensFlare shader reads (see its Attributes): the quad's corner, the element's type and parameters, its intensity
		/// and sizes, and the flare's own FlareAttenuation, GlowProxySize (both in meters), FlareStyle and FlareType.
		/// </summary>
		public static Mesh BuildMesh(IList<Element> elements, float attenuation, float proxyRadius, int style, bool sunFlare)
		{
			var textures = new List<string>();
			var groups = new List<List<int>>();
			var vertices = new List<Vector3>();
			var uvs = new List<Vector2>();
			var colors = new List<Color32>();
			var shape = new List<Vector4>();
			var ramp = new List<Vector4>();
			var sizes = new List<Vector4>();
			var flare = new List<Vector4>();
			var flareData = new Vector4(attenuation, proxyRadius, style, sunFlare ? 1 : 0);
			// How far short of the far plane a sun flare's sun stands: 32 units (DrawModel)
			var sunOffset = 32 * SourceEngineUnits.VmfInvMeters;

			foreach (var element in elements)
			{
				var group = textures.IndexOf(element.Texture);
				if (group < 0)
				{
					group = textures.Count;
					textures.Add(element.Texture);
					groups.Add(new List<int>());
				}

				var first = vertices.Count;
				var fourth = (element.Type == ElementType.DiskH) ? element.Parameters.w
						   : (element.Type == ElementType.Disk && element.Turned) ? 1 : 0;
				for (int corner = 0; corner < 4; corner++)
				{
					// x: 0 left, 1 right. y: 0 top, 1 bottom, which is the top of the image at the top, as the client
					// maps it: the importer turns images upside down into Unity's texture space.
					var x = corner & 1;
					var y = corner >> 1;
					vertices.Add(new Vector3(x, y, 0));
					uvs.Add(new Vector2(x, 1 - y));
					colors.Add(element.Color);
					shape.Add(new Vector4((int)element.Type, element.Parameters.x, element.Parameters.y, element.Parameters.z));
					ramp.Add(new Vector4(fourth, element.Intensity.x, element.Intensity.y, element.Intensity.z));
					sizes.Add(new Vector4(element.Sizes.x, element.Sizes.y, element.Sizes.z, sunOffset));
					flare.Add(flareData);
				}
				groups[group].AddRange(new[] { first, first + 2, first + 1, first + 2, first + 3, first + 1 });
			}

			var mesh = new Mesh { name = "Lens Flare" };
			mesh.SetVertices(vertices);
			mesh.SetUVs(0, uvs);
			mesh.SetColors(colors);
			mesh.SetUVs(1, shape);
			mesh.SetUVs(2, ramp);
			mesh.SetUVs(3, sizes);
			mesh.SetUVs(4, flare);
			mesh.subMeshCount = groups.Count;
			for (int i = 0; i < groups.Count; i++)
				mesh.SetTriangles(groups[i], i, calculateBounds: false);

			// The shader places the quads in screen space, around the light at the object's origin. The bounds are the
			// proxy sphere's, so the flare is culled with its light, as the client's visibility query would have it. A
			// sun flare's light is wherever the camera looks toward the sun from, so nothing may cull it.
			mesh.bounds = sunFlare ? new Bounds(Vector3.zero, Vector3.one * 1.0e6f)
								   : new Bounds(Vector3.zero, Vector3.one * (2 * Mathf.Max(proxyRadius, 0.01f)));
			return mesh;
		}
	}
}
