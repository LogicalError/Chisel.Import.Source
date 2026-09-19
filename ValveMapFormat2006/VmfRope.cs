using System.Collections.Generic;
using UnityEngine;

namespace AeternumGames.Chisel.Import.Source.ValveMapFormat2006
{
    /// <summary>
    /// The ropes, cables and wires a map draws, built as Source builds them (SDK 2013: CRopeKeyframe in rope.cpp on the
    /// server, C_RopeKeyframe in c_rope.cpp on the client). Everything here is in Source units, z up.
    /// <para>
    /// move_rope and keyframe_rope are one class. Each such entity draws at most one rope, from itself to the entity its
    /// NextKey names, with its own settings - a "chain" is only ropes that happen to share end points. The client hangs
    /// the rope by simulating a few nodes on a string, then draws a camera-facing strip through a Catmull-Rom spline
    /// over those nodes, with the texture advancing a fixed step per point.
    /// </para>
    /// </summary>
    public static class VmfRope
    {
        /// <summary>The settings a rope entity gives its rope, as the game reads and networks them.</summary>
        public struct Settings
        {
            /// <summary>How many nodes the rope is simulated with, 2 to 10 (Type)</summary>
            public int Nodes;
            /// <summary>Spline points drawn between two nodes, 0 to 7 (Subdiv)</summary>
            public int Subdivisions;
            /// <summary>Drawn zig-zagging, with three spline points of its own between nodes (Barbed)</summary>
            public bool Barbed;
            /// <summary>The far end hangs free (Dangling)</summary>
            public bool Dangling;
            /// <summary>Length added to the distance between the end points, less 100 (Slack)</summary>
            public int Slack;
            /// <summary>Width of the strip, in units (Width)</summary>
            public float Width;
            /// <summary>Units of rope per texel along it, divided by 4 (TextureScale)</summary>
            public float TextureScale;
            /// <summary>The rope's material (RopeMaterial)</summary>
            public string Material;
        }

        /// <summary>One rope: the entity that draws it, and the entity it ends at - null for a dangling rope without one.</summary>
        public struct Link
        {
            public VmfEntity Rope;
            public VmfEntity End;
        }

        public const string kDefaultMaterial = "cable/cable";

        const int kMaxNodes      = 10;   // ROPE_MAX_SEGMENTS
        const int kType1Nodes    = 4;    // ROPE_TYPE1_NUMSEGMENTS
        const int kType2Nodes    = 2;    // ROPE_TYPE2_NUMSEGMENTS
        const int kDefaultNodes  = 5;    // CRopeKeyframe's constructor, for a rope with no Type
        const int kMaxSubdivisions = 7;  // MAX_ROPE_SUBDIVS - 1
        const int kSlackFudge    = -100; // ROPESLACK_FUDGEFACTOR

        /// <summary>
        /// How long the rope is simulated. The client lets a rope sag for 251 steps as it spawns and keeps simulating it
        /// while it moves - near the player, wind never lets it rest - so the shape shown is the one the simulation settles
        /// on. Every rope in Black Mesa's maps settles within 979 steps; this leaves room for longer, slacker ones.
        /// </summary>
        public const int kSettleSteps = 2000;

        /// <summary>The settings of a rope entity, read as CRopeKeyframe reads its keys, with its defaults for the missing
        /// ones, and cut to what the network sends the client.</summary>
        public static Settings SettingsOf(VmfEntity rope)
        {
            // A key written empty is still a key the game reads, as 0 (atoi and atof of nothing)
            var keys = rope.PropertyText;
            var settings = new Settings
            {
                Nodes        = kDefaultNodes,
                Slack        = SignExtend(keys.ContainsKey("Slack") ? rope.GetInteger("Slack") : 0, 12),
                Width        = keys.ContainsKey("Width") ? rope.GetFloat("Width") : 2,
                // Sent as a float within [0.1, 10]
                TextureScale = Mathf.Clamp(keys.ContainsKey("TextureScale") ? rope.GetFloat("TextureScale") : 4, 0.1f, 10.0f),
                Dangling     = rope.GetInteger("Dangling") == 1,
                Barbed       = rope.GetInteger("Barbed") == 1,
                Material     = kDefaultMaterial,
            };

            if (keys.ContainsKey("Type"))
            {
                var type = rope.GetInteger("Type");
                settings.Nodes = (type == 0) ? kMaxNodes : (type == 1) ? kType1Nodes : kType2Nodes;
            }

            // Sent in 4 bits, and the client draws at most 7
            var subdivisions = keys.ContainsKey("Subdiv") ? rope.GetInteger("Subdiv") : 2;
            settings.Subdivisions = Mathf.Min(subdivisions & 15, kMaxSubdivisions);

            // The legacy RopeShader picks one of three materials, and RopeMaterial names one. The game takes whichever it
            // reads last, which is the one the map lists first (vbsp writes an entity's keys back to front); the importer
            // doesn't keep that order, so RopeMaterial wins here.
            var material = rope.GetText("RopeMaterial");
            if (material != null)
                settings.Material = material;
            else if (keys.ContainsKey("RopeShader"))
            {
                var shader = rope.GetInteger("RopeShader");
                settings.Material = (shader == 0) ? "cable/cable" : (shader == 1) ? "cable/rope" : "cable/chain";
            }
            return settings;
        }

        /// <summary>Whether an entity is a rope: move_rope and keyframe_rope are the same class in the game</summary>
        public static bool IsRope(VmfEntity entity)
        {
            return entity.ClassName == "move_rope" || entity.ClassName == "keyframe_rope";
        }

        /// <summary>
        /// The ropes these entities draw, in map order. A rope entity draws one when its NextKey finds an entity, or when
        /// it dangles (CRopeKeyframe::Activate). The name is looked up as the game looks it up: the first entity in map
        /// order that answers to it (<see cref="NamesMatch"/>), among the entities the map compiler kept - it drops the
        /// ones hidden in Hammer. A hidden rope, which the importer brings in switched off, is resolved as it would be
        /// with every hidden entity shown.
        /// </summary>
        /// <remarks>The game also needs the entity it finds to be a networked one, which only its code knows class by
        /// class; a rope entity is.</remarks>
        public static List<Link> Links(IReadOnlyList<VmfEntity> entities)
        {
            var links = new List<Link>();
            NameIndex compiled = null, all = null;
            foreach (var entity in entities)
            {
                if (!IsRope(entity))
                    continue;
                VmfEntity end = null;
                var nextKey = entity.GetText("NextKey");
                if (!string.IsNullOrEmpty(nextKey))
                {
                    var names = entity.IsHidden ? (all ??= new NameIndex(entities, includeHidden: true))
                                                : (compiled ??= new NameIndex(entities, includeHidden: false));
                    end = names.Find(nextKey);
                }
                if (end == null && !SettingsOf(entity).Dangling)
                    continue;
                links.Add(new Link { Rope = entity, End = end });
            }
            return links;
        }

        /// <summary>
        /// CBaseEntity's NamesMatch (baseentity.cpp): the names are compared as far as they agree, ASCII letters
        /// regardless of case, and they match when both end there, or when the query has a '*' there.
        /// </summary>
        public static bool NamesMatch(string query, string name)
        {
            var i = 0;
            for (; i < query.Length && i < name.Length; i++)
            {
                if (!SameAsciiLetter(query[i], name[i]))
                    break;
            }
            if (i == query.Length && i == name.Length)
                return true;
            return i < query.Length && query[i] == '*';
        }

        static bool SameAsciiLetter(char a, char b)
        {
            if (a == b)
                return true;
            return IsAsciiLetter(a) && IsAsciiLetter(b) && (a | 0x20) == (b | 0x20);
        }

        static bool IsAsciiLetter(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        }

        /// <summary>The first entity answering to a name, as CGlobalEntityList::FindEntityByName finds it</summary>
        sealed class NameIndex
        {
            readonly IReadOnlyList<VmfEntity> entities;
            readonly bool includeHidden;
            // A query without a '*' only matches a name equal to it, which the dictionary answers at once
            readonly Dictionary<string, VmfEntity> firstByName = new(AsciiIgnoreCase.Instance);

            public NameIndex(IReadOnlyList<VmfEntity> entities, bool includeHidden)
            {
                this.entities = entities;
                this.includeHidden = includeHidden;
                foreach (var entity in entities)
                {
                    if (TryGetName(entity, out var name) && !firstByName.ContainsKey(name))
                        firstByName.Add(name, entity);
                }
            }

            bool TryGetName(VmfEntity entity, out string name)
            {
                name = null;
                if (entity.IsHidden && !includeHidden)
                    return false;
                // An entity without a name is never found
                name = entity.GetText("targetname");
                return !string.IsNullOrEmpty(name);
            }

            public VmfEntity Find(string query)
            {
                // "!player" and the like name entities that only exist in the running game
                if (string.IsNullOrEmpty(query) || query[0] == '!')
                    return null;
                if (query.IndexOf('*') < 0)
                    return firstByName.TryGetValue(query, out var found) && found != null ? found : null;
                foreach (var entity in entities)
                {
                    if (TryGetName(entity, out var name) && NamesMatch(query, name))
                        return entity;
                }
                return null;
            }
        }

        sealed class AsciiIgnoreCase : IEqualityComparer<string>
        {
            public static readonly AsciiIgnoreCase Instance = new();

            public bool Equals(string x, string y)
            {
                if (x == null || y == null)
                    return x == y;
                if (x.Length != y.Length)
                    return false;
                for (var i = 0; i < x.Length; i++)
                {
                    if (!SameAsciiLetter(x[i], y[i]))
                        return false;
                }
                return true;
            }

            public int GetHashCode(string text)
            {
                var hash = 17;
                foreach (var c in text)
                    hash = hash * 31 + (IsAsciiLetter(c) ? (c | 0x20) : c);
                return hash;
            }
        }

        /// <summary>
        /// Where a rope ends at an entity, in Source units: its origin, or the world origin, where the game spawns an
        /// entity that has none. (The client takes the end entity's WorldSpaceCenter, which is the origin for a rope
        /// entity; for an entity with a model it would be the centre of its bounds.)
        /// </summary>
        public static Vector3 OriginOf(VmfEntity entity)
        {
            return entity.TryGetProperty("origin", out VmfVector3 origin) ? new Vector3(origin.X, origin.Y, origin.Z) : Vector3.zero;
        }

        /// <summary>
        /// The rope length the server gives the client: the distance between the end points, truncated
        /// (CRopeKeyframe::RecalculateLength), and 0 without an end. Sent in 15 bits.
        /// </summary>
        public static int RopeLength(Vector3 start, Vector3 end, bool hasEnd)
        {
            return hasEnd ? SignExtend((int)(start - end).magnitude, 15) : 0;
        }

        /// <summary>
        /// The longest a link between two nodes may stretch: the rope length plus the slack less 100, shared out over the
        /// links in whole units (C_RopeKeyframe::RecomputeSprings), and never below 0 (ResetSpringLength).
        /// </summary>
        public static float SpringLength(int ropeLength, int slack, int nodes)
        {
            return Mathf.Max((ropeLength + slack + kSlackFudge) / (nodes - 1), 0);
        }

        /// <summary>
        /// The nodes of a rope once it has settled. They start on the line between the end points, and each step moves
        /// them on with 98% of their velocity plus gravity (CSimplePhysics::Simulate, 50 steps a second), then pulls the
        /// links that are longer than <paramref name="springLength"/> back to that length, three times over, locking the
        /// ends after each pass (CBaseRopePhysics::ApplyConstraints, C_RopeKeyframe::CPhysicsDelegate). The solver never
        /// quite satisfies the links, which is why this is simulated rather than solved as a hanging chain: taut ropes
        /// still sag by what it lets through.
        /// </summary>
        /// <param name="end">Where the far end starts; a dangling rope without an end starts toward the world origin.</param>
        /// <param name="lockEnd">False for a dangling rope, whose far end then falls.</param>
        public static Vector3[] Settle(Vector3 start, Vector3 end, float springLength, int nodes, bool lockEnd, int steps = kSettleSteps)
        {
            const float kTimeStep = 1.0f / 50;
            const float kDamping  = 0.98f;
            var gravityStep = new Vector3(0, 0, -1500) * (kTimeStep * kTimeStep * 0.5f);

            // Relative to the start: floats far from the origin would round every step
            var endPoint = end - start;
            var position = new Vector3[nodes];
            var previous = new Vector3[nodes];
            for (var i = 0; i < nodes; i++)
            {
                position[i] = endPoint * ((float)i / (nodes - 1));
                previous[i] = position[i];
            }

            var springLengthSquared = springLength * springLength;
            for (var step = 0; step < steps; step++)
            {
                for (var i = 0; i < nodes; i++)
                {
                    var current = position[i];
                    position[i] = current + (current - previous[i]) * kDamping + gravityStep;
                    previous[i] = current;
                }

                for (var iteration = 0; iteration < 3; iteration++)
                {
                    for (var i = 0; i < nodes - 1; i++)
                    {
                        var link = position[i] - position[i + 1];
                        var lengthSquared = link.sqrMagnitude;
                        if (lengthSquared <= springLengthSquared)
                            continue;
                        link *= 1 - springLength / Mathf.Sqrt(lengthSquared);
                        position[i]     -= link * 0.5f;
                        position[i + 1] += link * 0.5f;
                    }
                    position[0] = Vector3.zero;
                    if (lockEnd)
                        position[nodes - 1] = endPoint;
                }
            }

            for (var i = 0; i < nodes; i++)
                position[i] += start;
            return position;
        }

        // The spline positions between two nodes a barbed rope is drawn with: past the next node and back again
        static readonly float[] kBarbedSubdivisions = { 1.5f, -0.5f, 0.5f };

        /// <summary>How many spline points the client draws between two nodes</summary>
        public static int DrawnSubdivisions(Settings settings)
        {
            return settings.Barbed ? kBarbedSubdivisions.Length : settings.Subdivisions;
        }

        /// <summary>
        /// The points the strip is drawn through (C_RopeKeyframe::BuildRope): every node, and after each node but the last,
        /// points on the Catmull-Rom spline through the node before it, the node itself and the two after it - the first
        /// and last nodes standing in for the ones the rope doesn't have.
        /// </summary>
        public static Vector3[] RenderPoints(Vector3[] nodes, int subdivisions, bool barbed)
        {
            var count = barbed ? kBarbedSubdivisions.Length : subdivisions;
            var last = nodes.Length - 1;
            var points = new Vector3[nodes.Length + last * count];
            var written = 0;
            var previousNode = 0;
            for (var node = 0; node < nodes.Length; node++)
            {
                points[written++] = nodes[node];
                if (node == last)
                    break;

                var next = node + 1;
                var nextNext = Mathf.Min(node + 2, last);
                var p1 = nodes[previousNode];
                var p2 = nodes[node];
                var p3 = nodes[next];
                var p4 = nodes[nextNext];
                // Catmull_Rom_Spline_Matrix
                var cubic     = 0.5f * ((-1 * p1) + (3 * p2) + (-3 * p3) + p4);
                var quadratic = 0.5f * ((2 * p1) + (-5 * p2) + (4 * p3) - p4);
                var linear    = 0.5f * ((-1 * p1) + p3);
                for (var i = 0; i < count; i++)
                {
                    var t = barbed ? kBarbedSubdivisions[i] : (float)(i + 1) / (count + 1);
                    points[written++] = p2 + (t * linear) + ((t * t) * quadratic) + ((t * t * t) * cubic);
                }
                previousNode = node;
            }
            return points;
        }

        /// <summary>
        /// How far the texture advances from one drawn point to the next, in texture heights (BuildRope). The rope's
        /// length plus its slack less 100 is covered at 4 texels a unit over TextureScale, spread over one point per
        /// subdivision plus one - which is fewer points than the rope has, so the texture runs on past that length.
        /// </summary>
        /// <param name="textureHeight">The height of the material's base texture in texels (GetMappingHeight)</param>
        public static float TextureStep(int ropeLength, Settings settings, int textureHeight)
        {
            var texelsPerUnit = 4.0f / settings.TextureScale;
            var totalTexels = texelsPerUnit * ((float)ropeLength + settings.Slack + kSlackFudge);
            var points = (settings.Nodes - 1) * DrawnSubdivisions(settings) + 1;
            return (totalTexels / points) / textureHeight;
        }

        static int SignExtend(int value, int bits)
        {
            var shift = 32 - bits;
            return (value << shift) >> shift;
        }
    }
}
