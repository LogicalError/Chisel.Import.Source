using System.Globalization;

namespace AeternumGames.Chisel.Import.Source.ValveMapFormat2006
{
    /// <summary>
    /// One of a Hammer entity's I/O connections: when the entity fires <see cref="Output"/>, every
    /// entity named <see cref="Target"/> receives <see cref="Input"/>, <see cref="Delay"/> seconds later.
    /// </summary>
    public class VmfConnection
    {
        /// <summary>The output that fires the connection, e.g. "OnMapSpawn".</summary>
        public string Output;

        /// <summary>The entity the input goes to: a name, or a class name when no entity has that name.</summary>
        public string Target = "";

        /// <summary>The input the target receives, e.g. "SetBloomScale".</summary>
        public string Input = "";

        /// <summary>The input's parameter, empty when it takes none.</summary>
        public string Parameter = "";

        /// <summary>Seconds between the output firing and the input arriving.</summary>
        public float Delay;

        /// <summary>How often the connection fires before it is removed, or -1 for no limit.</summary>
        public int TimesToFire = -1;

        /// <param name="output">The key the connection is written under.</param>
        /// <param name="value">The connection itself: target, input, parameter, delay and times to fire.</param>
        public VmfConnection(string output, string value)
        {
            Output = output;

            // Recent Hammer versions separate the fields with an escape character, older ones with
            // commas - which is why an old-style parameter can never contain a comma.
            const char kEscape = '\u001b';
            var fields = value.Split(value.IndexOf(kEscape) >= 0 ? kEscape : ',');
            if (fields.Length > 0) Target    = fields[0];
            if (fields.Length > 1) Input     = fields[1];
            if (fields.Length > 2) Parameter = fields[2];
            if (fields.Length > 3 && float.TryParse(fields[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var delay))
                Delay = delay;
            if (fields.Length > 4 && int.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var timesToFire))
                TimesToFire = timesToFire;
        }

        /// <summary>
        /// Returns a <see cref="System.String"/> that represents this instance.
        /// </summary>
        /// <returns>A <see cref="System.String"/> that represents this instance.</returns>
        public override string ToString()
        {
            return "VmfConnection " + Output + " -> " + Target + "." + Input + "(" + Parameter + ") after " + Delay + "s";
        }
    }
}
