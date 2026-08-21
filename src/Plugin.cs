using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace BiggerUI
{
    /// <summary>
    /// Makes the game's UI text bigger, for people who find the default too small to read.
    ///
    /// The game has no text size or UI scale setting of its own - Settings offers clock
    /// format, prompts, speech bubbles, vibration, text animation, language, day length and
    /// portrait style, and nothing about size. This fills that hole.
    ///
    /// <para>
    /// v0.2.0 scales font sizes only. v0.1.0 scaled canvases and broke the display; see
    /// <see cref="FontScaler"/> and the mod README for what happened and why this cannot
    /// repeat it.
    /// </para>
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInProcess("Moonlight Peaks.exe")]
    public sealed class BiggerUiPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.dirtyredz.moonlightpeaks.biggerui";
        public const string PluginName = "Bigger UI";
        public const string PluginVersion = ModBuildInfo.Version;

        /// <summary>What to do about text that TextMeshPro fits to its box.</summary>
        public enum AutoSizeMode
        {
            /// <summary>Leave auto-sized text at the game's own size.</summary>
            Off,

            /// <summary>
            /// Raise the ceiling only: the text grows if its box has room, and stays put if it
            /// does not. Cannot make text vanish.
            /// </summary>
            GrowWithinBox,

            /// <summary>
            /// Raise the floor as well, so auto-sized text grows whether or not it fits. Bigger
            /// text, at the risk of a line being dropped where the box is too tight.
            /// </summary>
            Force,
        }

        internal static ManualLogSource Log;

        internal static ConfigEntry<float> Scale;
        internal static ConfigEntry<string> AreaScales;
        internal static ConfigEntry<float> MaxFontSize;
        internal static ConfigEntry<string> AreaMaxFontSize;
        internal static ConfigEntry<string> Canvases;
        internal static ConfigEntry<bool> FitToBox;
        internal static ConfigEntry<AutoSizeMode> AutoSize;
        internal static ConfigEntry<KeyboardShortcut> ToggleKey;
        internal static ConfigEntry<bool> LogReport;

        // Mod Menu reads these out of ConfigDescription.Tags to title its sections. Display
        // names only - the .cfg section keys are untouched, since renaming one there would
        // orphan every saved value.
        private const string ScaleSection = "ModMenu.Section=Text size";
        private const string DiagnosticsSection = "ModMenu.Section=Diagnostics";

        private void Awake()
        {
            Log = Logger;

            Scale = Config.Bind(
                "Scale", "Scale", 1.25f,
                new ConfigDescription(
                    "How much bigger the text is. 1.0 is the game's own size, 1.25 is a " +
                    "quarter bigger, 2.0 is double.\n" +
                    "Only font sizes change - no panel, box or canvas is resized - so text " +
                    "that outgrows its box will clip or truncate rather than push the layout " +
                    "around. If something reads as cut off, come down a step.\n" +
                    "Takes effect immediately when changed in an in-game config editor; " +
                    "editing the .cfg by hand needs a restart.",
                    new AcceptableValueRange<float>(FontScaler.MinScale, FontScaler.MaxScale),
                    ScaleSection, "ModMenu.Label=Text size"));

            Canvases = Config.Bind(
                "Scale", "Canvases", "GameCanvas,SharedCanvas,MenuCanvas",
                new ConfigDescription(
                "Which canvases' text to scale, by exact root canvas name, comma-separated.\n" +
                "GameCanvas is the important one - it carries roughly 600 text elements, " +
                "including dialogue and the book. SharedCanvas holds about 65 and survives a " +
                "save load; MenuCanvas holds the main menu and unloads once you are in game.\n" +
                "This is an allowlist on purpose. v0.1.0 swept everything on screen and ended " +
                "up resizing the mouse cursor, the safe-area boundary, and two other mods' " +
                "interfaces. A canvas that is not named here is left alone.\n" +
                "Left out deliberately: CursorCanvas (the mouse pointer); Canvas, " +
                "SafezoneCanvas and UnscaledCanvas (they hold no text at all); and the " +
                "per-mod canvases - MinimapCanvas, ':: CANVAS', PlantPeek_HoverCanvas, " +
                "ChestLabels_HoverCanvas, CoffinBreak_BadgeCanvas - which have their own size " +
                "settings and are not this mod's business.\n" +
                "The log names every canvas holding text and whether it is in this list, so " +
                "anything still small can be added.",
                null,
                ScaleSection, "ModMenu.Label=Canvases to scale"));

            AreaScales = Config.Bind(
                "Scale", "AreaScales", string.Empty,
                new ConfigDescription(
                "Per-screen overrides of Scale, as Name=Number pairs separated by commas.\n" +
                "Example: QuestScreen=1.0, InventoryScreen=1.4\n" +
                "The names are the game's own screen classes. Every screen that holds text is " +
                "listed in the log with its element count and the scale it is getting, so " +
                "there is no need to guess - play to the screen you want to change, then read " +
                "off its name.\n" +
                "A screen not named here uses Scale. Values are clamped to the same 1.0-2.0 " +
                "range.",
                null,
                ScaleSection, "ModMenu.Label=Per-screen scale"));

            MaxFontSize = Config.Bind(
                "Scale", "MaxFontSize", 36f,
                new ConfigDescription(
                    "The largest any text is allowed to become, in points. 0 turns the ceiling " +
                    "off.\n" +
                    "A percentage increase hurts most where text is already large - a speaker's " +
                    "name during dialogue grows enough to push its nameplate over whatever sits " +
                    "beside it. Bounding the result in points rather than the multiplier fixes " +
                    "that without holding back ordinary body text, which is nowhere near this " +
                    "size.\n" +
                    "AreaMaxFontSize overrides this for a named screen. Like every ceiling here, " +
                    "it only limits growth: text the game already draws larger than this is left " +
                    "alone rather than shrunk.",
                    new AcceptableValueRange<float>(0f, 96f),
                    ScaleSection, "ModMenu.Label=Maximum size"));

            AreaMaxFontSize = Config.Bind(
                "Scale", "AreaMaxFontSize", string.Empty,
                new ConfigDescription(
                "Per-screen ceilings in points, as Name=Number pairs separated by commas.\n" +
                "Example: QuestScreen=26\n" +
                "Text on that screen is scaled as normal and then capped, so a screen whose " +
                "boxes cannot take the full scale stops growing at a size that still fits " +
                "instead of dragging Scale down for the whole game.\n" +
                "The log reports the largest resulting size on each screen, so there is a real " +
                "number to aim below rather than a guess.\n" +
                "This mod only ever grows text: a ceiling below the game's own size leaves that " +
                "text at the game's size rather than shrinking it.",
                null,
                ScaleSection, "ModMenu.Label=Per-screen maximum size"));

            FitToBox = Config.Bind(
                "Scale", "FitToBox", true,
                new ConfigDescription(
                "Grow ordinary, fixed-size text by letting TextMeshPro fit it, instead of just " +
                "multiplying its size.\n" +
                "On, the text is given a size range from its original size up to the scaled " +
                "size, and TextMeshPro picks the largest that actually fits its box: bigger " +
                "where there is room, unchanged where there is none. It cannot overflow and it " +
                "cannot disappear.\n" +
                "Off multiplies the size directly, which is what earlier versions did. Text " +
                "then grows past what its box holds, and where a box is too tight TextMeshPro " +
                "drops the line rather than spilling it - which is why the quest log's names " +
                "vanished. Only turn this off if fitted text looks inconsistent between rows " +
                "and you would rather have uniform sizes with the clipping that brings.",
                null,
                ScaleSection, "ModMenu.Label=Fit text to its box"));

            AutoSize = Config.Bind(
                "Scale", "AutoSize", AutoSizeMode.Force,
                new ConfigDescription(
                "What to do about text that TextMeshPro fits to its box.\n" +
                "Force raises both ends of the size range, so auto-sized text grows whether or " +
                "not it fits. This is what makes most of the game respond - but where a box is " +
                "too tight, TextMeshPro drops the line rather than spilling it, and the text " +
                "disappears. The quest log's quest names do exactly that.\n" +
                "GrowWithinBox raises only the ceiling: text grows where its box has room and " +
                "stays put where it does not. It cannot make anything vanish, and it cannot " +
                "make tight text bigger either.\n" +
                "Off leaves auto-sized text at the game's own size.\n" +
                "Prefer an AreaScales entry for a single misbehaving screen; change this only " +
                "if text is vanishing in several places at once.",
                null,
                ScaleSection, "ModMenu.Label=Auto-sized text"));

            ToggleKey = Config.Bind(
                "Scale", "ToggleKey", new KeyboardShortcut(KeyCode.RightAlt),
                new ConfigDescription(
                "Flips the scaling on and off, for comparing against the game's own sizes " +
                "without restarting.\n" +
                "This is a testing aid. The scale setting is the real interface; if this " +
                "reaches a release it should probably default to none, since a hotkey nobody " +
                "asked for is a hotkey that collides with something. Set to None to disable.",
                null,
                ScaleSection, "ModMenu.Label=Compare on/off key"));

            LogReport = Config.Bind(
                "Diagnostics", "LogReport", true,
                new ConfigDescription(
                    "Log every canvas and screen holding text, with element counts and the " +
                    "largest resulting size. This is how 'that text stayed small' gets " +
                    "diagnosed, and where the names for AreaScales and AreaMaxFontSize come " +
                    "from.",
                    null,
                    DiagnosticsSection, "ModMenu.Label=Log what was scaled"));

            gameObject.AddComponent<FontScaler>();

            // Patched after the scaler exists, so the first OnEnable already has somewhere to
            // go. Without this, text is scaled by the next sweep instead - which is up to three
            // seconds of the screen sitting at the wrong size, visible as a jump when it lands.
            try
            {
                new Harmony(PluginGuid).PatchAll(typeof(TextEnablePatch));
                Log.LogInfo("Text is scaled as each element is enabled, before it is drawn.");
            }
            catch (Exception e)
            {
                Log.LogWarning(
                    $"Could not patch TextMeshProUGUI.OnEnable ({e.Message}). Falling back to " +
                    "the periodic sweep, which is a few seconds slower to catch a new screen.");
            }

            Log.LogInfo(
                $"{PluginName} {PluginVersion} loaded. Text at {Scale.Value:0.00}x on " +
                $"[{Canvases.Value}]. Font sizes only - no canvas, panel or layout is resized. " +
                "Nothing is written to your save.");
            Log.LogInfo($"Press {ToggleKey.Value} to compare against the game's own sizes.");
        }
    }
}
