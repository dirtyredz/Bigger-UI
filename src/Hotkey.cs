using BepInEx.Configuration;
using UnityEngine;

namespace BiggerUI
{
    /// <summary>
    /// Key checks that fire when the bound key is pressed, regardless of what else is held.
    ///
    /// Copied from PlantPeek, which learned it the hard way. Deliberately not
    /// KeyboardShortcut.IsDown()/IsPressed(). Those end in:
    ///
    ///     _modifierBlockKeyCodes.All(c =&gt; !Input.GetKey(c) || allKeys.Contains(c))
    ///
    /// where _modifierBlockKeyCodes is every supported key except the mouse buttons - so they
    /// report false whenever *any* other key is held. Sensible for a config-menu shortcut that
    /// must not fire mid-combo; wrong for a key pressed during play, where the player may be
    /// holding a movement key.
    ///
    /// <para>Right Alt makes it worse: on many Windows layouts it is AltGr, which Unity reports
    /// as LeftControl *and* RightAlt together. IsDown() then sees a foreign modifier held and
    /// refuses, so the toggle never fired even standing still.</para>
    ///
    /// These check the bound key and its declared modifiers, and ignore everything else.
    /// </summary>
    internal static class Hotkey
    {
        internal static bool WasPressed(KeyboardShortcut shortcut)
        {
            var main = shortcut.MainKey;
            if (main == KeyCode.None || !Input.GetKeyDown(main))
            {
                return false;
            }

            foreach (var modifier in shortcut.Modifiers)
            {
                if (!Input.GetKey(modifier))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
