# Bigger UI

Makes Moonlight Peaks' text bigger, for people who find the default too small to read
comfortably.

**Status:** 🪦 **Retired 2026-08-22** — v0.10.0, built and deployed but never published; no longer
maintained. The post-mortems below are kept as a record of what was learned. Getting the basics right
took three tries early on, "some text got bigger and some did
not", each a different cause: v0.2.0 threw away every closed screen
([the inactive-screen bug](#the-inactive-screen-bug)), v0.3.0 fixed that but had an allowlist
covering only two of the game's canvases ([the allowlist was too narrow](#the-allowlist-was-too-narrow)),
and v0.1.0 scaled canvases instead of fonts and broke the display outright
([what v0.1.0 broke](#what-v010-broke)).

## Why it exists

The game has no text size or UI scale setting. `SettingsGameplayScreen` offers exactly eight
options — clock format, interaction prompts, speech bubbles, controller vibration, text
animation, language, day duration, portrait style — and `SettingsVideoScreen` offers
resolution, screen mode, VSync, render scale and framerate. `AppSettingsLibrary` confirms that
is the whole list. Nothing about size.

Nor does a mod exist. All 88 Nexus mods were read sorted by date on 2026-08-04, plus keyword
searches for `font` (0 results), `scale` (0), `text` (2, both *texture* frameworks) and `UI`
(4, none relevant). **No accessibility mod of any kind exists for this game.**

## What it does

Multiplies `fontSize` on the game's UI text. That is the whole mod.

- **No canvas, panel, box or RectTransform is resized.** Nothing touches `CanvasScaler`,
  reference resolutions, scale factors or safe areas. The failure mode that killed v0.1.0 is
  not reachable from here.
- **Allowlisted by root canvas name**, defaulting to `SharedCanvas,MenuCanvas` — the two
  canvases identified as the game's own readable interface. A canvas nobody named is left
  alone, which keeps the mod out of other people's mods.
- Matching is **exact**, not substring: `Canvas` as a substring would also match `MinimapCanvas`
  and `:: CANVAS`, which belong to other plugins. That is precisely how v0.1.0 went wrong.
- Originals are remembered per text component and every size is computed from the original, so
  repeated sweeps can never compound. If the game or another mod rewrites a size, that is taken
  as the new baseline rather than fought over.
- **Scales each text as it is enabled**, via a Harmony postfix on `TextMeshProUGUI.OnEnable`, so
  a screen is never drawn at the wrong size. This is the earliest a mod can act without
  pre-scaling the prefabs themselves — which would look seamless and then silently compound,
  because an instance born from an already-scaled prefab is indistinguishable from one the sweep
  has not reached yet.
- Sweeps every 3 seconds and on scene load as a safety net, for text whose size the game rewrites
  after enabling it. Prefab assets in memory are skipped (`scene.IsValid()`) so nothing
  double-applies.

**Nothing is written to your save.** Deleting the DLL restores the game's own sizes.

### The known cost of doing it this way

Text grows inside boxes that do not grow. Long labels in fixed-width buttons and dense list
rows are where clipping or truncation will show up first — that is inherent to font-only
scaling, not a bug to be fixed later. If something reads as cut off, the answer is a lower
`Scale`, or `ScaleAutoSized = false`.

### Auto-sized text, and the "upper limit on font size"

Where TextMeshPro auto-sizing is on, `fontSize` is an *output* — the rect caps it, so writing a
bigger number changes nothing. Chest Labels hit this and has to grow the rect alongside
`fontSizeMax` (seen while building Chest Labels). It is
a per-box limit, not a global font cap.

So for auto-sized text this mod scales the **range** (`fontSizeMin` and `fontSizeMax`) rather
than the size. Raising only the max does nothing unless the box already had slack, which is why
the min moves too — and which is why this is exactly where clipping appears first. Hence the
separate `ScaleAutoSized` switch: off grows only the text that is safe to grow, at the cost of
leaving some of it small.

## The inactive-screen bug

v0.2.0 ran clean and reported `61 text element(s) scaled 1.25x across canvas(es)
[SharedCanvas]`. In play, some text was bigger and some was not.

The cause is one line. It filtered on `Graphic.canvas` — and **`Graphic.canvas` resolves its
canvas with `GetComponentsInParent` excluding inactive objects, so it returns null for a text
on a screen that is currently closed.** Screens spend nearly all their life closed, which is the
entire reason the mod sweeps with `FindObjectsOfTypeAll` rather than `FindObjectsOfType`.
v0.2.0 gathered every inactive text and then discarded all of them again on that null check.
It scaled exactly the elements that happened to be on screen at the time.

v0.3.0 walks the transform parents by hand to the topmost `Canvas`, which works regardless of
active state. Same allowlist, same exact-name matching — it just no longer throws away the
screens that are the point.

## The allowlist was too narrow

v0.3.0 fixed the inactive-screen bug and the numbers moved: **420** elements at the main menu,
then **~65** once a save loaded. That drop is `MenuCanvas` unloading — and it means during
actual play only `SharedCanvas` was in scope, which is a small slice of the interface.

Two lessons, and the second is the one that cost the time:

1. The allowlist named two canvases; the game has more than two. `Canvas` (screen-space camera),
   `SafezoneCanvas` and `UnscaledCanvas` were all excluded, carried over from v0.1.0 where
   scaling them was genuinely dangerous. **Scaling their *text* is not** — a font size cannot
   move a safe-area boundary. The exclusions were right for canvas scaling and wrong here.
2. **The mod could not say where the text it was missing lived.** "Some text scaled" was
   diagnosed twice by inference instead of by asking the game. v0.4.0 logs a census: every
   canvas holding UI text, how many elements, and whether it is in the allowlist. Anything
   still small now names itself.

## Config

`BepInEx\config\com.dirtyredz.moonlightpeaks.biggerui.cfg`

| Key | Default | |
|---|---|---|
| `Scale / Scale` | `1.25` | How much bigger the text is, 1.0–2.0 |
| `Scale / AreaScales` | *(empty)* | Per-screen scale overrides, `QuestScreen=1.0, InventoryScreen=1.4` |
| `Scale / AreaMaxFontSize` | *(empty)* | Per-screen ceilings in points, `QuestScreen=26` |
| `Scale / Canvases` | `GameCanvas,SharedCanvas,MenuCanvas` | Exact root canvas names to scale text on. An allowlist on purpose |
| `Scale / MaxFontSize` | `36` | Largest any text may become, in points. `0` turns it off |
| `Scale / FitToBox` | `true` | Grow ordinary text by letting TextMeshPro fit it, rather than multiplying blind |
| `Scale / AutoSize` | `Force` | `Force` / `GrowWithinBox` / `Off` — how to treat text already fitted to its box |
| `Scale / ToggleKey` | `RightAlt` | Flips scaling on and off for A/B comparison. **A testing aid** |
| `Diagnostics / LogReport` | `true` | Names each canvas and each screen as it is first scaled, and logs each pass whose element count changed |

`ScaleAutoSized` was replaced by `AutoSize` in v0.6.0; the old key is left orphaned in existing
config files and is ignored.

## Per-area scale

`AreaScales` overrides `Scale` for named screens:

```
AreaScales = QuestScreen=1.0, InventoryScreen=1.4
```

An **area** is the screen a text belongs to — the nearest ancestor with a `Chicken.UI.UIScreen`
component, which every screen in the game derives from. The **type** name is used rather than
the GameObject name, so the keys match what the decompiled game calls its screens and do not
drift with whatever an artist named an object.

There is no guessing involved: every screen holding text is logged with its element count, the
scale it is receiving, and **the largest resulting font size on it**:

```
Area 'QuestScreen': 25 text element(s) at 1.25x, largest resulting size 31.2pt.
```

## Boxes that size themselves

Fitting text to its box is wrong when something is already sizing that box to the text. A
`ContentSizeFitter` and TextMeshPro's auto-sizing actively fight: the fitter widens the box,
auto-sizing then sees text that already fits, and nothing grows. The interaction prompts in the
bottom-left corner did exactly this in v0.9.0 — the row expanded sideways while the letters
stayed the same size.

So the mod looks for a `ContentSizeFitter` on the text and up to four ancestors — the fitter
normally sits on the row, not the label — and where it finds one, multiplies the size directly.
That is safe precisely because the box grows with the text.

## Growing what is already large

A percentage increase does the most damage where text starts big. A speaker's name during
dialogue is the clearest case: scaled up, the nameplate grows enough to cover what sits beside
it.

`MaxFontSize` (default **36pt**) bounds the *result* rather than the multiplier, which is the
right shape for this — ordinary body text is nowhere near 36pt and is unaffected, while
already-large text stops before it becomes a problem. `0` disables it.

Like every ceiling here, it only limits growth: text the game already draws larger than the
ceiling is left alone rather than shrunk.

## Per-area ceiling

`AreaMaxFontSize` caps the result in points, per screen:

```
AreaMaxFontSize = QuestScreen=26
```

This is the answer to "one screen cannot take the full scale". Without it the only lever is
`Scale`, and dropping that to 1.15 for the whole game to keep one screen intact is paying
everywhere for a problem in one place. With a ceiling, that screen stops growing where its boxes
run out and everything else keeps the size you asked for.

The `largest resulting size` in the log is the number to aim below — if the quest log reports
31.2pt and breaks, try 26.

A ceiling only limits how far this mod grows text. Setting one **below** the game's own size
leaves that text at the game's size rather than shrinking it; this mod never makes text smaller
than it found it.

## The vanishing quest names — the actual cause

Three versions treated this as an auto-sizing problem. It was not.

**Every fit check applied only to auto-sized text.** Fixed-size text took a different branch —
`fontSize = original × scale`, no measurement, no cap. So all three `AutoSize` settings were
irrelevant to it. The proof arrived when `AutoSize = GrowWithinBox`, which provably cannot
shrink or delete auto-sized text, left the quest names vanishing exactly as before. That ruled
out the diagnosis rather than confirming it, which is what it should have done two versions
earlier.

**And the fitting was estimated rather than measured.** `FittingSize` scales a preferred-height
ratio, which assumes the line count does not change — false the moment bigger text wraps.
TextMeshPro computes this exactly; that is what auto-sizing is for.

So v0.9.0 stops estimating. `FitToBox` gives fixed-size text a range from its original size up
to the scaled size and lets TextMeshPro choose: bigger where there is room, unchanged where
there is none, never below the game's own size. It cannot overflow and it cannot disappear.

The honest cost: text in a tight box no longer grows, and rows in the same list can end up at
slightly different sizes. That is the price of never deleting anything.

## Historical: why `Force` caused vanishing in auto-sized text

Reported in the v0.5.0 run: at 1.25x the quest log's **quest names disappeared entirely** while
everything else worked.

That is auto-sizing plus a tight box. Where TextMeshPro cannot fit a line in the rect it has,
it **drops the line rather than spilling it** — so forcing the size up past what the box allows
does not clip the text, it deletes it. `AutoSize = Force` raises the floor (`fontSizeMin`),
which is what makes most of the game respond at all, and is also exactly what triggers this.

**v0.7.0 fixes this properly rather than by configuration.** `Force` now measures the box before
raising the floor, and refuses to push past what that box can hold:

```
targetMin = clamp(originalMin, originalMin × scale, largest size the rect can hold)
```

The measurement uses TextMeshPro's own layout: preferred height scales with font size, so
measuring the content once at the size it is currently drawn at gives the ratio — text wanting
40 units of height at size 20 in a box 30 tall means the box holds size 15. It is cached per
element and re-measured only when the content length changes, since `GetPreferredValues` runs a
real layout pass and this runs over every auto-sized element on every sweep.

Text still grows as far as there is room for. It can no longer delete a line to do it.

The manual escapes remain if a screen still misbehaves: `AreaScales = QuestScreen=1.0` for one
screen, `AutoSize = GrowWithinBox` globally, or a lower `Scale`.

Changing `Scale` in an in-game config editor applies immediately.

`ToggleKey` exists to compare against the game's own sizes without restarting — press it and
every scaled element snaps back to its original size; press again to restore. While toggled off
the sweep stops entirely, so nothing creeps back up behind you mid-comparison. It is a
**testing aid, not the interface**: a shipped version should probably default it to none, since
a hotkey nobody asked for is a hotkey that collides with something.

### The toggle did not fire (v0.4.0)

`KeyboardShortcut.IsDown()` ends in
`_modifierBlockKeyCodes.All(c => !Input.GetKey(c) || allKeys.Contains(c))` — it returns false
whenever **any** other key is held. Right Alt makes it worse: on many Windows layouts it is
AltGr, which Unity reports as LeftControl *and* RightAlt together, so the check saw a foreign
modifier and refused even with nothing else pressed.

Plant Peek had already hit this and written it down — its binding "simply never fired while
moving". [`Hotkey.cs`](src/Hotkey.cs) here is that same helper, copied:
check the bound key and its declared modifiers, ignore everything else. **The answer was in the
repo before the bug was written.**

Canvas names observed on this machine, for filling in `Canvases`:

| Name | What it is |
|---|---|
| `GameCanvas` | **~597 elements — the bulk of the game.** Dialogue, the book, in-play screens |
| `SharedCanvas` | ~65 elements, survives a save load. HUD-level text |
| `MenuCanvas` | The main menu, ~360 elements. **Unloads once a save is loaded** |
| `Canvas`, `SafezoneCanvas`, `UnscaledCanvas` | The game's, but hold **no text at all**. Nothing to scale |
| `CursorCanvas` | The mouse pointer. Left out on purpose |
| `MinimapCanvas`, `:: CANVAS` | **Other mods'.** Never add these |
| `PlantPeek_HoverCanvas`, `ChestLabels_HoverCanvas`, `CoffinBreak_BadgeCanvas` | **Our own mods'.** They have their own font size settings; not this mod's business |

## What v0.1.0 broke

v0.1.0 scaled `CanvasScaler` rather than fonts — which grows boxes and text together and cannot
clip, and is still the better idea *in principle*. It was run once at 1.25x on 2026-08-04.

**Observed:** the screen did not fit, with a black band across the bottom. Resizing or
maximising the window drove the game to a tiny resolution — "640 x something". Uninstalled the
same evening; everything returned to normal immediately.

The canvas report explains all of it:

```
Canvas 'Canvas'          [ScreenSpaceCamera]  ScaleWithScreenSize ref=1536x864  match=0     -> scaled
Canvas 'CursorCanvas'    [ScreenSpaceOverlay] ConstantPixelSize   factor=1.25            -> scaled
Canvas 'SharedCanvas'    [ScreenSpaceOverlay] ScaleWithScreenSize ref=1536x864  match=0     -> scaled
Canvas 'SafezoneCanvas'  [ScreenSpaceOverlay] ConstantPixelSize   ref=800x600            -> scaled
Canvas 'UnscaledCanvas'  [ScreenSpaceOverlay] ConstantPixelSize   ref=800x600            -> scaled
Canvas 'MenuCanvas'      [ScreenSpaceOverlay] ScaleWithScreenSize ref=1536x864  match=0     -> scaled
Canvas 'MinimapCanvas'   [ScreenSpaceOverlay] ScaleWithScreenSize ref=1536x864  match=0.5   -> scaled
Canvas ':: CANVAS'       [ScreenSpaceOverlay] ScaleWithScreenSize ref=1536x864  match=0.31  -> scaled
```

**800 ÷ 1.25 = 640.** `SafezoneCanvas` is a safe-area boundary and `UnscaledCanvas` is the game
saying in the object's own name that it must not be scaled. Moving the safe area moves what
every other element positions itself against — the black band, the layout not fitting, and the
resize misbehaviour all follow from that one write. `CursorCanvas` made the mouse pointer 25%
bigger. `MinimapCanvas` and `:: CANVAS` belong to Detailed Minimap and another installed plugin.

**The design was right and the targeting was wrong.** "Every canvas" is not "the UI you read".
Three rules came out of it, and all three are in v0.2.0:

1. **Allowlist, never sweep.** Opt-out was the wrong polarity — it silently includes canvases
   that do not exist yet, and canvases this mod does not own.
2. **Never touch another mod's UI**, under any default.
3. **Match names exactly.** Substring matching is what reached `:: CANVAS` and `MinimapCanvas`.

If font-only scaling turns out to clip too much to live with, the way back is not the v0.1.0
sweep — it is canvas scaling restricted to `SharedCanvas` and `MenuCanvas` by exact name, which
would have been safe all along.

## Testing

**v0.2.0, 2026-08-04.** Loaded clean, no errors. `61 text element(s) scaled 1.25x across
canvas(es) [SharedCanvas]`; `MenuCanvas` contributed nothing. In play, some text grew and some
did not — diagnosed as the inactive-screen bug above.

**v0.3.0, 2026-08-04.** Inactive-screen fix confirmed working: 420 elements at the main menu vs
61 before. But in play, most text was still untouched — `MenuCanvas` unloads on save load,
leaving only `SharedCanvas`'s ~65 elements in scope. Allowlist too narrow.

**v0.4.0** — never ran. The `RightAlt` toggle was tested against the v0.3.0 build still in
memory and did not fire; cause was `KeyboardShortcut.IsDown()`, fixed in v0.4.1.

**v0.4.1, 2026-08-04.** Toggle confirmed working. Some UI scaled — shortcut options — but not
chat and nothing in the book. **The census immediately named the cause:**

```
Canvas 'SharedCanvas' holds 61 text element(s) - scaling.
Canvas 'MenuCanvas'   holds 359 text element(s) - scaling.
Canvas 'GameCanvas'   holds 597 text element(s) - NOT in Scale/Canvases - add it if this text should grow.
```

`GameCanvas` — the game's own UI singleton, ~600 elements, carrying dialogue and the book — had
never been in the allowlist. It was not among the eight canvases v0.1.0 reported, because that
version enumerated `CanvasScaler`s and `GameCanvas` has none. Every allowlist since was built
from that list, so the largest text surface in the game was invisible to three versions running.

The same run also confirmed `Canvas`, `SafezoneCanvas` and `UnscaledCanvas` hold **no text**,
and correctly identified our own three mods' canvases as out of scope.

**Lesson: enumerate the thing you actually care about.** Text was the target throughout, and
the canvas list came from a scaler census inherited from a different design.

**v0.5.0, 2026-08-04.** `GameCanvas` added — **"a lot better of an improvement."** Chat, the
book and the in-play screens all responded. One defect: in the quest log, quest names vanished
at 1.25x. Diagnosed as auto-sizing in a tight box, where TextMeshPro drops a line rather than
spilling it.

**v0.6.0, 2026-08-04.** Area census works — 110 screens named, `QuestScreen` among them with 25
elements. Quest names still vanished, correctly: `AreaScales` was empty and `AutoSize` was
`Force`, so nothing had changed for that screen. A fix that requires the player to read a log
and edit a config file is not a fix, which is what prompted v0.7.0's measured clamp.

Also reported: a visible size jump on the load-save menu — the sweep landing after the screen
had already drawn.

**v0.7.0, 2026-08-04.** Loaded clean, on-enable patch applied. The measured clamp helped but did
not fully hold: quest names still vanish once the font gets large enough, and `Scale` had been
dropped to 1.15 to keep that one screen intact — the whole game paying for one screen.

The clamp is an estimate. It measures preferred height at the currently drawn size and scales
the ratio, which assumes line count does not change; when bigger text wraps onto an extra line
the real requirement jumps past the estimate. Good enough to help, not good enough to promise.

**v0.8.0, 2026-08-04.** Added `AreaMaxFontSize` and per-screen size reporting. Quest names still
vanished — the per-screen ceiling was never set, and more importantly the diagnosis was still
wrong.

**v0.9.0, 2026-08-04.** **Quest log fixed**, by treating fixed-size text as its own case and
letting TextMeshPro fit it. Two consequences surfaced in play:

- The bottom-left interaction prompts expanded sideways without the letters growing — a
  `ContentSizeFitter` sizing the box against the fitting.
- Dialogue speaker names grew enough to push their nameplates over neighbouring text.

**v0.10.0** — to run. Skips fitting where the box sizes itself, and adds a 36pt global ceiling.

Two things to watch specifically, because they are the predicted failure points:

- **Text that stayed small.** The log says whether the mod never found it (wrong canvas — try
  adding `Canvas`), skipped it, or scaled it and the box is holding it back.
- **Text that got cut off.** Drop `Scale`, or set `ScaleAutoSized = false`.

## Related

- This mod draws nothing of its own, so it has no font or palette of its own to get wrong. It
  changes the size of the game's own text and nothing else.
- Built with [BepInEx 5](https://github.com/BepInEx/BepInEx) (win_x64) and HarmonyX, against
  the game's own `Vampire.Runtime.dll`. Moonlight Peaks is Unity Mono, so no IL2CPP tooling is
  needed.
- Sibling mods by the same author: [Chest Labels](https://www.nexusmods.com/moonlightpeaks/mods/119).
