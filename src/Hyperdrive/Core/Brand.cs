using System.Text;

namespace Hyperdrive.Core
{
    /// <summary>
    /// The mod's name as players see it (loading screens, intro, panel, icon). Developer setting devBrand in
    /// Hyperdrive.cfg's global values tries another name on screen without renaming anything (mockups).
    /// </summary>
    internal static class Brand
    {
        internal static string Name { get; private set; } = "Hyperdrive";

        /// <summary>The name in spaced capitals, as the loading screens write it (thin spaces).</summary>
        internal static string Spaced { get; private set; } = "H Y P E R D R I V E";

        internal static bool IsApoapsis => Name == "Apoapsis";

        internal static void Load(SettingsSection global)
        {
            string name = global.GetString("devBrand", null);
            if (string.IsNullOrEmpty(name))
                return;
            Name = name.Trim();
            var sb = new StringBuilder(Name.Length * 2);
            foreach (char c in Name.ToUpperInvariant())
            {
                if (sb.Length > 0)
                    sb.Append(' ');   // thin spaces, as the loading screens' mark
                sb.Append(c);
            }
            Spaced = sb.ToString();
            Log.Info("Brand: shown as '" + Name + "' (developer setting devBrand)");
        }
    }
}
