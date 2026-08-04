using HarmonyLib;
using TMPro;

namespace BiggerUI
{
    /// <summary>
    /// Scales a text the instant it is switched on, rather than waiting for the next sweep.
    ///
    /// <para><b>Why this exists.</b> The periodic sweep runs every few seconds, so a screen that
    /// appears just after one lands is drawn at the game's own size and then visibly jumps when
    /// the next sweep catches it. That pop was plainly visible on the load-save menu.</para>
    ///
    /// <para><c>OnEnable</c> runs before the object's first frame is drawn and after its
    /// hierarchy is in place, so the canvas and screen lookups both work and the text is only
    /// ever rendered at its final size. This is the earliest a mod can act without pre-scaling
    /// the prefabs themselves - which would look seamless and then silently compound, because an
    /// instance born from an already-scaled prefab is indistinguishable from one the sweep has
    /// not reached yet.</para>
    ///
    /// <para>The sweep stays as the safety net, for text whose size the game rewrites after
    /// enabling it.</para>
    /// </summary>
    [HarmonyPatch(typeof(TextMeshProUGUI), "OnEnable")]
    internal static class TextEnablePatch
    {
        private static void Postfix(TextMeshProUGUI __instance)
        {
            FontScaler.Instance?.ScaleOnEnable(__instance);
        }
    }
}
