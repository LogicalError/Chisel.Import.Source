///////////////////////////////////////////////////////////////////////////////////////////////////
// MIT License
//
// Copyright(c) 2018-2020 Henry de Jongh
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
////////////////////// https://github.com/Henry00IS/ ////////////////// http://aeternumgames.com //

using System;
using System.Collections.Generic;

namespace AeternumGames.Chisel.Import.Source.ValveMapFormat2006
{
    /// <summary>
    /// Represents a Hammer Entity.
    /// </summary>
    public class VmfEntity
    {
        public int Id = -1;

        /// <summary>
        /// True when this was inside a Hammer `hidden` block. Those are hidden in the editor AND
        /// skipped by the map compiler, so they are imported but left inactive.
        /// </summary>
        public bool IsHidden;

        /// <summary>
        /// The class name of the entity. Empty, never null, for an entity block that declares no
        /// `classname` - the converters switch and string-compare on this on every entity.
        /// </summary>
        public string ClassName = "";

        /// <summary>
        /// The solids in the entity if available.
        /// </summary>
        public List<VmfSolid> Solids = new List<VmfSolid>();

        /// <summary>
        /// The properties in the entity whenever parsable. Keys match regardless of case, as they do in
        /// Source: vbsp's ValueForKey and the game's KeyValue both compare them case-insensitively, and
        /// Hammer does not always write a key in the same case ("BasisOrigin" and "basisorigin" both
        /// occur). A key written twice keeps its last value, as in vbsp.
        /// </summary>
        public Dictionary<string, object> Properties = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The properties as the map writes them, keys matched as in <see cref="Properties"/>. The game reads every key
        /// from its text, while <see cref="Properties"/> gives a value a type by its shape - which makes a name like "1"
        /// a number - and leaves out the keys written empty.
        /// </summary>
        public Dictionary<string, string> PropertyText = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A key's text as the map writes it, or null when the entity doesn't have the key.</summary>
        public string GetText(string name)
        {
            return PropertyText.TryGetValue(name, out var text) ? text : null;
        }

        /// <summary>
        /// The entity's I/O connections, in the order the map lists them. An output is written once for
        /// every connection it drives, so these cannot live in <see cref="Properties"/>.
        /// </summary>
        public List<VmfConnection> Connections = new List<VmfConnection>();

        /// <summary>
        /// Tries the get a property of a certain type.
        /// </summary>
        /// <typeparam name="T">The expected type of the property</typeparam>
        /// <param name="name">The name of the property to retrieve.</param>
        /// <param name="value">The value of the property if found.</param>
        /// <returns>True if the property could be found and is of the expected type else false.</returns>
        public bool TryGetProperty<T>(string name, out T value)
        {
            value = default;
            if (!Properties.ContainsKey(name)) return false;
            if (typeof(T) == typeof(float))
            {
                // integer can be casted to float.
                if (Properties[name].GetType() == typeof(int))
                {
                    value = (T)(object)Convert.ToSingle((int)Properties[name]);
                    return true;
                }
            }
            if (Properties[name].GetType() != typeof(T)) return false;
            value = (T)Properties[name];
            return true;
        }

        /// <summary>
        /// Reads a property the way vbsp's IntForKey and the game's integer keyfields read one, with atoi: the whole
        /// number the value starts with, and 0 when it starts with none or the key is missing.
        /// </summary>
        public int GetInteger(string name)
        {
            if (!Properties.TryGetValue(name, out var value))
                return 0;

            switch (value)
            {
                case int integer:
                    return integer;
                // The parser only types a value as a float or a vector when it is written in numbers, and atoi stops
                // at the decimal point or the space.
                case float number:
                    return (int)number;
                case VmfVector3 vector:
                    return (int)vector.X;
                case VmfVector4 vector:
                    return (int)vector.X;
                case string text:
                    return ParseLeadingInteger(text);
                default:
                    return 0;
            }
        }

        /// <summary>
        /// Reads a property the way the game's float keyfields read one, with atof: the number the value starts with, and
        /// 0 when it starts with none or the key is missing.
        /// </summary>
        public float GetFloat(string name)
        {
            if (!Properties.TryGetValue(name, out var value))
                return 0;

            switch (value)
            {
                case int integer:
                    return integer;
                case float number:
                    return number;
                // atof stops at the space.
                case VmfVector3 vector:
                    return vector.X;
                case VmfVector4 vector:
                    return vector.X;
                case string text:
                    return ParseLeadingFloat(text);
                default:
                    return 0;
            }
        }

        static float ParseLeadingFloat(string text)
        {
            var i = 0;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;

            var start = i;
            if (i < text.Length && (text[i] == '-' || text[i] == '+'))
                i++;
            var digits = 0;
            for (; i < text.Length && text[i] >= '0' && text[i] <= '9'; i++)
                digits++;
            if (i < text.Length && text[i] == '.')
            {
                i++;
                for (; i < text.Length && text[i] >= '0' && text[i] <= '9'; i++)
                    digits++;
            }
            if (digits == 0)
                return 0;

            // An exponent only counts when digits follow it.
            if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
            {
                var exponent = i + 1;
                if (exponent < text.Length && (text[exponent] == '-' || text[exponent] == '+'))
                    exponent++;
                if (exponent < text.Length && text[exponent] >= '0' && text[exponent] <= '9')
                {
                    i = exponent;
                    while (i < text.Length && text[i] >= '0' && text[i] <= '9')
                        i++;
                }
            }

            return (float)double.Parse(text.Substring(start, i - start), System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture);
        }

        static int ParseLeadingInteger(string text)
        {
            var i = 0;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;

            var negative = i < text.Length && text[i] == '-';
            if (i < text.Length && (text[i] == '-' || text[i] == '+'))
                i++;

            long value = 0;
            for (; i < text.Length && text[i] >= '0' && text[i] <= '9'; i++)
                value = Math.Min(value * 10 + (text[i] - '0'), (long)int.MaxValue + 1);
            return (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, negative ? -value : value));
        }

        /// <summary>
        /// Returns a <see cref="System.String"/> that represents this instance.
        /// </summary>
        /// <returns>A <see cref="System.String"/> that represents this instance.</returns>
        public override string ToString()
        {
            return "VmfEntity " + ClassName + " " + Id + " (" + Solids.Count + " Solids)";
        }
    }
}