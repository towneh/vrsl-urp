using System;
using System.Reflection;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Silences the host project's own logger for the life of a fixture.
    ///
    /// The project these rows run in is a Basis client, and with no Basis scene
    /// loaded its systems log errors of their own: once when the avatar load fails
    /// over, and then on every respawn check for the rest of the run, because the
    /// player falls below the respawn height for ever. The framework fails any test
    /// that sees an unhandled error, and which row sees one is alignment. Basis
    /// routes every message through <c>BasisDebug</c>, which carries a public
    /// <c>LoggingDisabled</c> switch, so the noise is turned off at source and the
    /// package's own errors keep failing rows the ordinary way.
    ///
    /// Found by reflection rather than referenced, so the test assembly compiles
    /// unchanged in a project without Basis, where there is nothing to silence.
    /// </summary>
    static class VRSLHostQuiet
    {
        static FieldInfo s_switch;
        static bool      s_was;
        static int       s_holders;

        static FieldInfo Switch()
        {
            if (s_switch != null) return s_switch;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type;
                try { type = assembly.GetType("BasisDebug", false); }
                catch (Exception) { continue; }
                if (type == null) continue;
                s_switch = type.GetField("LoggingDisabled", BindingFlags.Public | BindingFlags.Static);
                if (s_switch != null && s_switch.FieldType == typeof(bool)) return s_switch;
                s_switch = null;
            }
            return null;
        }

        /// <summary>Turn the host's logging off. Counted, so nested or overlapping
        /// fixtures restore it only when the last one lets go.</summary>
        public static void Silence()
        {
            var field = Switch();
            if (field == null) return;
            if (s_holders++ == 0)
            {
                s_was = (bool)field.GetValue(null);
                field.SetValue(null, true);
            }
        }

        /// <summary>Put the host's logging back the way it was. Left silenced, a
        /// Test Runner session in the editor would mute the client for the rest of
        /// it.</summary>
        public static void Restore()
        {
            var field = Switch();
            if (field == null || s_holders == 0) return;
            if (--s_holders == 0) field.SetValue(null, s_was);
        }
    }
}
