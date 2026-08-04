using System;
using System.Collections.Generic;
using System.Globalization;
using Chicken.UI;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace BiggerUI
{
    /// <summary>
    /// Multiplies the font size of the game's UI text, and nothing else.
    ///
    /// <para><b>Why this is the second attempt.</b> v0.1.0 scaled canvases instead, which grows
    /// boxes and text together and cannot clip. It also broke the game: sweeping every
    /// <c>CanvasScaler</c> caught the mouse cursor, a canvas named <c>UnscaledCanvas</c>, two
    /// other mods' canvases, and <c>SafezoneCanvas</c> - whose 800x600 reference became 640x480,
    /// moving the safe-area boundary that everything else positions against. Black band at the
    /// bottom of the screen, layout not fitting, resolution behaving strangely on a window
    /// resize.</para>
    ///
    /// <para><b>What changed here.</b> Nothing in this file touches canvas geometry, reference
    /// resolutions, scale factors or RectTransforms - only <c>fontSize</c> on text components.
    /// The entire class of failure above is unreachable. The trade is the one canvas scaling
    /// avoided: text grows inside boxes that do not, so it can clip. See
    /// <see cref="ScaleAutoSized"/> for where that bites first.</para>
    ///
    /// <para><b>And it is allowlisted.</b> Text is scaled only under canvases named in config,
    /// defaulting to the two that are the game's own readable UI. Reaching into another mod's
    /// interface was the worst part of v0.1.0 and is not repeated: a canvas nobody named is a
    /// canvas this mod leaves alone.</para>
    /// </summary>
    internal sealed class FontScaler : MonoBehaviour
    {
        internal const float MinScale = 1.0f;
        internal const float MaxScale = 2.0f;

        /// <summary>
        /// How often to sweep for text that did not exist yet. Slower than v0.1.0's canvas
        /// pass: there are far more text components than canvases, and none of this is urgent.
        /// </summary>
        private const float RescanSeconds = 3f;

        /// <summary>What a text component looked like before this mod touched it.</summary>
        private sealed class Baseline
        {
            internal float FontSize;
            internal float FontSizeMin;
            internal float FontSizeMax;
            internal bool WasAutoSized;

            /// <summary>What was last written, so an outside change is recognisable.</summary>
            internal float Applied;

            /// <summary>
            /// Largest size this text's box was measured to hold, and the content length it was
            /// measured against. Cached because measuring runs a layout pass.
            /// </summary>
            internal float FittingSize = float.MaxValue;
            internal int MeasuredLength = -1;

            /// <summary>
            /// True when auto-sizing was switched on by this mod to fit fixed-size text. Restore
            /// has to switch it back off, or the text would keep resizing itself afterwards.
            /// </summary>
            internal bool FitToBoxApplied;
        }

        internal static FontScaler Instance { get; private set; }

        private readonly Dictionary<int, Baseline> baselines = new Dictionary<int, Baseline>();
        private readonly HashSet<string> canvasesReported = new HashSet<string>();

        /// <summary>Root canvas name to how many UI text elements hang off it.</summary>
        private readonly Dictionary<string, int> census = new Dictionary<string, int>();

        /// <summary>Screen name to how many UI text elements it holds.</summary>
        private readonly Dictionary<string, int> areaCensus = new Dictionary<string, int>();

        private readonly HashSet<string> areasReported = new HashSet<string>();

        /// <summary>Screen name to the largest resulting font size on it.</summary>
        private readonly Dictionary<string, float> areaLargest = new Dictionary<string, float>();

        private float sinceRescan;

        /// <summary>
        /// False while the toggle key is holding the game at its own sizes, for A/B comparison.
        /// </summary>
        private bool scalingOn = true;

        private void OnEnable()
        {
            Instance = this;
            SceneManager.sceneLoaded += HandleSceneLoaded;
            BiggerUiPlugin.Scale.SettingChanged += HandleSettingChanged;
            BiggerUiPlugin.AreaScales.SettingChanged += HandleSettingChanged;
            BiggerUiPlugin.AreaMaxFontSize.SettingChanged += HandleSettingChanged;
            BiggerUiPlugin.MaxFontSize.SettingChanged += HandleSettingChanged;
            BiggerUiPlugin.Canvases.SettingChanged += HandleSettingChanged;
            BiggerUiPlugin.AutoSize.SettingChanged += HandleSettingChanged;
            BiggerUiPlugin.FitToBox.SettingChanged += HandleSettingChanged;
            Apply();
        }

        private void OnDisable()
        {
            if (Instance == this)
            {
                Instance = null;
            }

            SceneManager.sceneLoaded -= HandleSceneLoaded;
            BiggerUiPlugin.Scale.SettingChanged -= HandleSettingChanged;
            BiggerUiPlugin.AreaScales.SettingChanged -= HandleSettingChanged;
            BiggerUiPlugin.AreaMaxFontSize.SettingChanged -= HandleSettingChanged;
            BiggerUiPlugin.MaxFontSize.SettingChanged -= HandleSettingChanged;
            BiggerUiPlugin.Canvases.SettingChanged -= HandleSettingChanged;
            BiggerUiPlugin.AutoSize.SettingChanged -= HandleSettingChanged;
            BiggerUiPlugin.FitToBox.SettingChanged -= HandleSettingChanged;
        }

        /// <summary>
        /// Puts every text back the size it was found. The mod writes nothing to disk, so
        /// deleting the DLL is equally complete - this just makes disabling it mid-session
        /// tidy.
        /// </summary>
        private void OnDestroy()
        {
            RestoreAll();
        }

        private void RestoreAll()
        {
            foreach (var text in FindTexts())
            {
                if (text == null || !baselines.TryGetValue(text.GetInstanceID(), out var baseline))
                {
                    continue;
                }

                Restore(text, baseline);
            }
        }

        private static void Restore(TextMeshProUGUI text, Baseline baseline)
        {
            if (baseline.WasAutoSized)
            {
                text.fontSizeMin = baseline.FontSizeMin;
                text.fontSizeMax = baseline.FontSizeMax;
                baseline.Applied = baseline.FontSizeMin;
            }
            else
            {
                // Undo the auto-sizing this mod switched on, or the text would keep fitting
                // itself to its box after the mod has supposedly let go of it.
                if (baseline.FitToBoxApplied)
                {
                    text.enableAutoSizing = false;
                    text.fontSizeMin = baseline.FontSizeMin;
                    text.fontSizeMax = baseline.FontSizeMax;
                    baseline.FitToBoxApplied = false;
                }

                text.fontSize = baseline.FontSize;
                baseline.Applied = baseline.FontSize;
            }
        }

        private void Update()
        {
            if (Hotkey.WasPressed(BiggerUiPlugin.ToggleKey.Value))
            {
                scalingOn = !scalingOn;
                BiggerUiPlugin.Log.LogInfo(
                    scalingOn
                        ? $"Toggled ON - text back to {CurrentScale():0.00}x."
                        : "Toggled OFF - text at the game's own size.");

                if (scalingOn)
                {
                    Apply();
                }
                else
                {
                    RestoreAll();
                }

                sinceRescan = 0f;
                return;
            }

            sinceRescan += Time.unscaledDeltaTime;
            if (sinceRescan < RescanSeconds)
            {
                return;
            }

            sinceRescan = 0f;

            // While toggled off, stop sweeping entirely rather than re-scaling behind the
            // player's back - the comparison is only honest if nothing moves.
            if (scalingOn)
            {
                Apply();
            }
        }

        private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Apply();
        }

        private void HandleSettingChanged(object sender, EventArgs e)
        {
            BiggerUiPlugin.Log.LogInfo($"Settings changed; text now {CurrentScale():0.00}x.");
            Apply();
        }

        private static float CurrentScale()
        {
            return Mathf.Clamp(BiggerUiPlugin.Scale.Value, MinScale, MaxScale);
        }

        private static TextMeshProUGUI[] FindTexts()
        {
            // FindObjectsOfTypeAll rather than FindObjectsOfType: screens spend most of their
            // life inactive, and an inactive screen still has to come back bigger.
            return Resources.FindObjectsOfTypeAll<TextMeshProUGUI>();
        }

        private void Apply()
        {
            var scale = CurrentScale();
            var allowed = Split(BiggerUiPlugin.Canvases.Value);
            var areaScales = ParseAreaScales(BiggerUiPlugin.AreaScales.Value);
            var areaCaps = ParseAreaScales(BiggerUiPlugin.AreaMaxFontSize.Value);
            var autoSizeMode = BiggerUiPlugin.AutoSize.Value;

            var scaled = 0;
            var autoSized = 0;
            var skippedAutoSized = 0;

            census.Clear();
            areaCensus.Clear();
            areaLargest.Clear();

            foreach (var text in FindTexts())
            {
                if (text == null)
                {
                    continue;
                }

                // FindObjectsOfTypeAll also returns prefabs sitting in memory, which have no
                // scene. Editing those would change screens that have not been built yet, and
                // then double up with the instance pass once they are.
                if (!text.gameObject.scene.IsValid())
                {
                    continue;
                }

                var root = FindRootCanvas(text.transform);
                if (root == null)
                {
                    continue;
                }

                // Counted whether or not it is scaled: "where is the text I am not reaching?"
                // is the question that took two versions to answer, and a census answers it
                // directly instead of by guesswork.
                census.TryGetValue(root.name, out var seen);
                census[root.name] = seen + 1;

                // Exact name match on the root canvas, not a substring: 'Canvas' as a substring
                // would match ':: CANVAS' and 'MinimapCanvas', which is how v0.1.0 ended up
                // resizing other people's mods.
                if (!allowed.Contains(root.name))
                {
                    continue;
                }

                // The area is the screen this text belongs to - QuestScreen, InventoryScreen -
                // so a single screen that reacts badly can be dialled back on its own instead
                // of dragging the whole game's scale down with it.
                var area = FindAreaName(text.transform, root);

                areaCensus.TryGetValue(area, out var areaSeen);
                areaCensus[area] = areaSeen + 1;

                var effectiveScale = scale;
                if (areaScales.TryGetValue(area, out var overrideScale))
                {
                    effectiveScale = Mathf.Clamp(overrideScale, MinScale, MaxScale);
                }

                if (text.enableAutoSizing)
                {
                    autoSized++;
                    if (autoSizeMode == BiggerUiPlugin.AutoSizeMode.Off)
                    {
                        skippedAutoSized++;
                        continue;
                    }
                }

                var cap = CeilingFor(area, areaCaps);

                if (ScaleText(text, effectiveScale, autoSizeMode, cap))
                {
                    scaled++;
                    RecordAreaSize(area, text);
                }
            }

            LogCensus(allowed);
            LogAreaCensus(areaScales, scale);
            LogPass(scaled, autoSized, skippedAutoSized);
        }

        /// <summary>
        /// The name of the screen a text belongs to, used as the per-area config key.
        ///
        /// <para>Every screen in the game derives from <c>Chicken.UI.UIScreen</c>, so the
        /// nearest such ancestor is the natural unit - and its <b>type</b> name is used rather
        /// than the GameObject name, because the type names match what the decompiled game
        /// calls them (<c>QuestScreen</c>, <c>InventoryScreen</c>) and do not drift with
        /// whatever an artist named the object.</para>
        ///
        /// <para>Text that belongs to no screen falls back to the canvas name, so it is still
        /// addressable.</para>
        /// </summary>
        private static string FindAreaName(Transform start, Canvas root)
        {
            for (var current = start; current != null; current = current.parent)
            {
                var screen = current.GetComponent<UIScreen>();
                if (screen != null)
                {
                    return screen.GetType().Name;
                }
            }

            return root.name;
        }

        /// <summary>
        /// Names every canvas that holds UI text and says whether this mod is allowed to touch
        /// it. Reported once per canvas, as each one first appears.
        ///
        /// <para>This is the diagnostic that should have existed in v0.2.0. "Some text scaled
        /// and some did not" was answered twice by inference - first the inactive-screen bug,
        /// then the allowlist being too narrow - when the mod could simply have said which
        /// canvases the text it was not touching lived on.</para>
        /// </summary>
        private void LogCensus(HashSet<string> allowed)
        {
            if (!BiggerUiPlugin.LogReport.Value)
            {
                return;
            }

            foreach (var entry in census)
            {
                if (!canvasesReported.Add(entry.Key))
                {
                    continue;
                }

                var verdict = allowed.Contains(entry.Key)
                    ? "scaling"
                    : "NOT in Scale/Canvases - add it if this text should grow";

                BiggerUiPlugin.Log.LogInfo(
                    $"Canvas '{entry.Key}' holds {entry.Value} text element(s) - {verdict}.");
            }
        }

        /// <summary>
        /// Walks to the topmost Canvas by hand rather than reading <c>Graphic.canvas</c>.
        ///
        /// <para><b>This is not a stylistic choice.</b> <c>Graphic.canvas</c> resolves its
        /// canvas with <c>GetComponentsInParent</c> excluding inactive objects, so for a text
        /// on a screen that is currently closed it returns null. Screens spend most of their
        /// life closed, which is the whole reason this mod uses
        /// <c>FindObjectsOfTypeAll</c> - and v0.2.0 then threw all of them away again on that
        /// null check. It scaled the 61 elements that happened to be on screen and silently
        /// skipped every closed one, which is exactly the "some text got bigger, some did not"
        /// that showed up in play.</para>
        /// </summary>
        private static Canvas FindRootCanvas(Transform start)
        {
            Canvas highest = null;
            for (var current = start; current != null; current = current.parent)
            {
                var canvas = current.GetComponent<Canvas>();
                if (canvas != null)
                {
                    highest = canvas;
                }
            }

            return highest;
        }

        /// <summary>
        /// The point ceiling for a screen: its own AreaMaxFontSize entry if it has one, otherwise
        /// the global MaxFontSize, otherwise none.
        ///
        /// <para>The global ceiling exists because some text is large before anything scales it -
        /// a speaker's name during dialogue, most visibly - and a percentage increase on already
        /// large text is what pushes a nameplate over its neighbours. A ceiling in points bounds
        /// the result rather than the multiplier.</para>
        /// </summary>
        private static float CeilingFor(string area, Dictionary<string, float> areaCaps)
        {
            if (areaCaps.TryGetValue(area, out var areaCap))
            {
                return areaCap;
            }

            var global = BiggerUiPlugin.MaxFontSize.Value;
            return global > 0f ? global : float.MaxValue;
        }

        /// <summary>
        /// True when something is already sizing this text's box to its content, so the box will
        /// grow along with the text and needs no fitting.
        ///
        /// <para>Checked on the text and a few ancestors, because the fitter usually sits on the
        /// row rather than the label - an interaction prompt is a horizontal row of icon plus
        /// text with the fitter on the row itself.</para>
        /// </summary>
        private static bool HasSelfSizingBox(TextMeshProUGUI text)
        {
            const int levels = 4;

            var current = text.transform;
            for (var i = 0; i < levels && current != null; i++, current = current.parent)
            {
                var fitter = current.GetComponent<ContentSizeFitter>();
                if (fitter != null &&
                    (fitter.horizontalFit != ContentSizeFitter.FitMode.Unconstrained ||
                     fitter.verticalFit != ContentSizeFitter.FitMode.Unconstrained))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Applies a configured ceiling, never dropping below the size the game itself used.
        ///
        /// <para>A ceiling is the player limiting how far this mod grows text, not asking for the
        /// game's own text to be made smaller - so a ceiling set below the original leaves the
        /// original alone. This mod only ever grows text.</para>
        /// </summary>
        private static float Ceiling(float wanted, float cap, float original)
        {
            if (cap >= float.MaxValue)
            {
                return wanted;
            }

            return Mathf.Max(original, Mathf.Min(wanted, cap));
        }

        /// <summary>
        /// The largest font size this text's own box can hold, or <c>float.MaxValue</c> when that
        /// cannot be established.
        ///
        /// <para>TextMeshPro's preferred height scales with font size, so measuring the content
        /// once at the size it is currently drawn at gives the ratio: if the text wants 40 units
        /// of height at size 20 and the box is 30 tall, the box holds size 15.</para>
        ///
        /// <para>Cached per text and re-measured only when the content length changes, because
        /// <c>GetPreferredValues</c> runs a real layout pass and this is called for every
        /// auto-sized element on every sweep.</para>
        ///
        /// <para>Returns <c>MaxValue</c> - meaning "do not clamp" - when the rect has no size
        /// yet. That happens on the frame a screen is built, before layout has run; the next
        /// sweep measures it properly.</para>
        /// </summary>
        private static float FittingSize(TextMeshProUGUI text, Baseline baseline)
        {
            var content = text.text;
            var length = content == null ? 0 : content.Length;

            if (baseline.MeasuredLength == length && baseline.FittingSize < float.MaxValue)
            {
                return baseline.FittingSize;
            }

            if (length == 0)
            {
                return float.MaxValue;
            }

            var rect = text.rectTransform.rect;
            if (rect.height <= 1f || rect.width <= 1f)
            {
                return float.MaxValue;
            }

            var drawnAt = text.fontSize;
            if (drawnAt <= 0.01f)
            {
                return float.MaxValue;
            }

            float preferredHeight;
            try
            {
                preferredHeight = text.GetPreferredValues(content, rect.width, 0f).y;
            }
            catch (Exception)
            {
                // Measuring is a nicety; never let it stop the mod scaling text.
                return float.MaxValue;
            }

            if (preferredHeight <= 0.01f)
            {
                return float.MaxValue;
            }

            var fitting = drawnAt * (rect.height / preferredHeight);

            baseline.FittingSize = fitting;
            baseline.MeasuredLength = length;
            return fitting;
        }

        /// <summary>
        /// Scales one text the moment it is enabled, so it is never drawn at the wrong size.
        /// Called from <see cref="TextEnablePatch"/>; see that class for why.
        /// </summary>
        internal void ScaleOnEnable(TextMeshProUGUI text)
        {
            if (!scalingOn || text == null || !text.gameObject.scene.IsValid())
            {
                return;
            }

            var root = FindRootCanvas(text.transform);
            if (root == null || !Split(BiggerUiPlugin.Canvases.Value).Contains(root.name))
            {
                return;
            }

            var mode = BiggerUiPlugin.AutoSize.Value;
            if (text.enableAutoSizing && mode == BiggerUiPlugin.AutoSizeMode.Off)
            {
                return;
            }

            var scale = CurrentScale();
            var area = FindAreaName(text.transform, root);
            var areaScales = ParseAreaScales(BiggerUiPlugin.AreaScales.Value);
            if (areaScales.TryGetValue(area, out var overrideScale))
            {
                scale = Mathf.Clamp(overrideScale, MinScale, MaxScale);
            }

            var areaCaps = ParseAreaScales(BiggerUiPlugin.AreaMaxFontSize.Value);
            var cap = areaCaps.TryGetValue(area, out var capValue) ? capValue : float.MaxValue;

            ScaleText(text, scale, mode, cap);
        }

        /// <summary>
        /// Returns true when this text is now at the requested size.
        ///
        /// <para>Auto-sized text needs both ends moved. TextMeshPro treats <c>fontSize</c> as an
        /// output when auto-sizing is on - the rect caps it, which is the "there is an upper
        /// limit on font size" that Chest Labels ran into. Raising only <c>fontSizeMax</c>
        /// therefore does nothing unless the box already had spare room, so
        /// <c>fontSizeMin</c> moves too. That is also precisely where clipping will show up
        /// first, which is why it is a separate config switch.</para>
        /// </summary>
        /// <summary>
        /// Remembers the largest size any text on a screen ended up at, so the log can report a
        /// real number to set AreaMaxFontSize below instead of leaving it to guesswork.
        /// </summary>
        private void RecordAreaSize(string area, TextMeshProUGUI text)
        {
            var size = text.enableAutoSizing ? text.fontSizeMax : text.fontSize;
            areaLargest.TryGetValue(area, out var largest);
            if (size > largest)
            {
                areaLargest[area] = size;
            }
        }

        private bool ScaleText(
            TextMeshProUGUI text, float scale, BiggerUiPlugin.AutoSizeMode autoSizeMode, float cap)
        {
            var id = text.GetInstanceID();
            if (!baselines.TryGetValue(id, out var baseline))
            {
                baseline = new Baseline
                {
                    FontSize = text.fontSize,
                    FontSizeMin = text.fontSizeMin,
                    FontSizeMax = text.fontSizeMax,
                    WasAutoSized = text.enableAutoSizing,
                    Applied = text.enableAutoSizing ? text.fontSizeMin : text.fontSize,
                };
                baselines[id] = baseline;
            }

            if (baseline.WasAutoSized)
            {
                // GrowWithinBox raises only the ceiling: the text may grow if its box has room
                // and stays put if it does not. Force raises the floor too, so it grows whether
                // or not it fits - which is what made the quest names vanish, since TextMeshPro
                // drops a line entirely rather than spill it when the rect cannot hold it.
                //
                // So Force now measures the box and refuses to push the floor past what that box
                // can actually hold. It still grows text as far as there is room for, and can no
                // longer delete a line to do it.
                var targetMax = Ceiling(baseline.FontSizeMax * scale, cap, baseline.FontSizeMax);
                var targetMin = baseline.FontSizeMin;

                if (autoSizeMode == BiggerUiPlugin.AutoSizeMode.Force)
                {
                    var wanted = baseline.FontSizeMin * scale;
                    var fits = FittingSize(text, baseline);

                    // Never below the game's own minimum: clamping is there to stop text
                    // disappearing, not to shrink anything.
                    targetMin = Mathf.Max(baseline.FontSizeMin, Mathf.Min(wanted, fits));
                    targetMin = Ceiling(targetMin, cap, baseline.FontSizeMin);
                }

                if (!Mathf.Approximately(text.fontSizeMin, targetMin))
                {
                    text.fontSizeMin = targetMin;
                }

                if (!Mathf.Approximately(text.fontSizeMax, targetMax))
                {
                    text.fontSizeMax = targetMax;
                }

                baseline.Applied = targetMin;
                return true;
            }

            // Fixed-size text. This is the branch the quest log's names go through, and the one
            // that was growing them blind: every fit check this mod had applied only to
            // auto-sized text, so AutoSize=GrowWithinBox did nothing for them and they kept
            // being pushed past what their box holds until TextMeshPro dropped the line.
            //
            // The fix is to stop estimating and let TextMeshPro fit the text itself. Turning its
            // auto-sizing on between the original size and the scaled size means it picks the
            // largest size that actually fits: bigger where there is room, unchanged where there
            // is none, and never smaller than the game's own size - so it cannot vanish.
            // A box that resizes itself to its content does not need protecting - it grows with
            // the text. Worse, fitting the text to it does not work: the fitter widens the box,
            // auto-sizing then sees text that already fits, and nothing grows at all. That is
            // the bottom-left interaction prompts, which expanded sideways without the letters
            // getting any bigger.
            if (BiggerUiPlugin.FitToBox.Value && !HasSelfSizingBox(text))
            {
                var ceiling = Ceiling(baseline.FontSize * scale, cap, baseline.FontSize);

                if (!text.enableAutoSizing)
                {
                    text.enableAutoSizing = true;
                }

                if (!Mathf.Approximately(text.fontSizeMin, baseline.FontSize))
                {
                    text.fontSizeMin = baseline.FontSize;
                }

                if (!Mathf.Approximately(text.fontSizeMax, ceiling))
                {
                    text.fontSizeMax = ceiling;
                }

                baseline.FitToBoxApplied = true;
                baseline.Applied = ceiling;
                return true;
            }

            // Someone else - the game rebuilding a label, or another mod - wrote a different
            // size. Take theirs as the new truth rather than fighting over it, which also stops
            // the multiplier compounding on every sweep.
            if (!Mathf.Approximately(text.fontSize, baseline.Applied))
            {
                baseline.FontSize = text.fontSize;
            }

            var target = Ceiling(baseline.FontSize * scale, cap, baseline.FontSize);
            if (!Mathf.Approximately(text.fontSize, target))
            {
                text.fontSize = target;
            }

            baseline.Applied = target;
            return true;
        }

        /// <summary>
        /// Names each screen the first time text on it is scaled, with the scale it is getting.
        /// This is the list to pick names from when writing AreaScales - the same trick that
        /// found GameCanvas, applied one level down.
        /// </summary>
        private void LogAreaCensus(Dictionary<string, float> areaScales, float globalScale)
        {
            if (!BiggerUiPlugin.LogReport.Value)
            {
                return;
            }

            foreach (var entry in areaCensus)
            {
                if (!areasReported.Add(entry.Key))
                {
                    continue;
                }

                var note = areaScales.TryGetValue(entry.Key, out var overrideScale)
                    ? $"{Mathf.Clamp(overrideScale, MinScale, MaxScale):0.00}x from AreaScales"
                    : $"{globalScale:0.00}x";

                // The largest resulting size is the number to set AreaMaxFontSize below when a
                // screen's boxes cannot take the full scale.
                var largest = areaLargest.TryGetValue(entry.Key, out var size)
                    ? $", largest resulting size {size:0.#}pt"
                    : string.Empty;

                BiggerUiPlugin.Log.LogInfo(
                    $"Area '{entry.Key}': {entry.Value} text element(s) at {note}{largest}.");
            }
        }

        /// <summary>
        /// Parses "QuestScreen=1.0, InventoryScreen=1.4" into a lookup. Malformed entries are
        /// reported and skipped rather than silently dropped - a typo'd screen name looks
        /// exactly like a mod that ignored you.
        /// </summary>
        private static Dictionary<string, float> ParseAreaScales(string raw)
        {
            var result = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(raw))
            {
                return result;
            }

            foreach (var part in raw.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                var split = trimmed.IndexOf('=');
                if (split <= 0 || split == trimmed.Length - 1)
                {
                    BiggerUiPlugin.Log.LogWarning(
                        $"AreaScales entry '{trimmed}' is not Name=Number and was ignored.");
                    continue;
                }

                var name = trimmed.Substring(0, split).Trim();
                var value = trimmed.Substring(split + 1).Trim();

                if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    BiggerUiPlugin.Log.LogWarning(
                        $"AreaScales entry '{trimmed}' has no readable number and was ignored.");
                    continue;
                }

                result[name] = parsed;
            }

            return result;
        }

        private static HashSet<string> Split(string raw)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(raw))
            {
                return result;
            }

            foreach (var part in raw.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0)
                {
                    result.Add(trimmed);
                }
            }

            return result;
        }

        /// <summary>
        /// Logs a pass whenever its numbers change. If text stayed small, this says whether the
        /// mod never found it, skipped it as auto-sized, or scaled it - in which case the box is
        /// what is holding it back.
        /// </summary>
        private void LogPass(int scaled, int autoSized, int skippedAutoSized)
        {
            if (!BiggerUiPlugin.LogReport.Value || scaled == lastScaledCount)
            {
                return;
            }

            lastScaledCount = scaled;

            if (scaled == 0)
            {
                BiggerUiPlugin.Log.LogWarning(
                    $"No text scaled. Nothing matched Scale/Canvases [{BiggerUiPlugin.Canvases.Value}]. " +
                    "Canvas names seen on this machine: SharedCanvas, MenuCanvas, Canvas.");
                return;
            }

            BiggerUiPlugin.Log.LogInfo(
                $"{scaled} text element(s) at {CurrentScale():0.00}x; {autoSized} auto-sized" +
                (skippedAutoSized > 0 ? $", {skippedAutoSized} of those left alone." : "."));
        }

        private int lastScaledCount = -1;
    }
}
