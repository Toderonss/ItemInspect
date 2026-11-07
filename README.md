# 🔍 Item Inspect for PEAK

[![Download on Thunderstore](https://img.shields.io/badge/Download-Thunderstore-purple?style=for-the-badge&logo=thunderstore)](https://thunderstore.io/)
[![BepInEx](https://img.shields.io/badge/BepInEx-5.4.24-yellow?style=for-the-badge)](https://github.com/BepInEx/BepInEx)

**Take a closer look at your gear!** Press **F** and your scout brings the item in hands up to the face and turns it around — just like the weapon inspect in CS2. Every kind of item gets its own animation.

---

## ✨ Features

| Feature | Description |
|---------|-------------|
| 🔍 **Inspect on a key** | Press **F** (configurable) to inspect the item you are holding |
| 🎞️ **Animation per item** | Berries, books, lanterns, idols, the Ritual Dagger and others each get a fitting animation |
| ✋ **One-hand inspect** | Small items are inspected with one hand — the left hand lets go and comes back smoothly |
| 🔁 **Loop & restart** | Hold the key to keep inspecting, press it again to start over |
| 🗡️ **Knife swing** | LMB with the Ritual Dagger makes a visual swing (only for blades without their own LMB action) |
| 💬 **In-game hints** | "inspect [F]" and "swing [LMB]" appear next to the game's own item hints |
| ⚙️ **Configurable** | Key, speed, angles, animation per item — all via the BepInEx config |
| 🎮 **Multiplayer safe** | Purely visual and local, no network traffic |

---

## 🎞️ Animations

| Style | What it does | Used for |
|-------|--------------|----------|
| **Classic** | One side, top, other side, bottom | Bugle, binoculars, flare, ice axe |
| **Flip** | Back side, edge, front from below | Guidebook, compass, flat items |
| **Toss** | Top towards you, then both sides and bottom | Berries, mushrooms, energy drink, small stuff |
| **Gentle** | Left side, top, then fully onto the right side | Lantern, rope, amulets, Rescue Hook |
| **Lift** | Up to the face, tilt left and right | Frog, Ancient Idol |
| **Heavy** | Small tilts only, so the arms don't twist | Antizooka and other long two-handed items |
| **Blade** | Like Gentle, ends with the flat of the blade facing you | Ritual Dagger |

With `Style = Auto` the mod picks the animation by item name and shape. You can override it per item with `ItemStyles`, e.g. `compass=Toss, rope=Gentle`.

---

## 📥 Installation

### Automatic (recommended)
1. Install [Thunderstore Mod Manager](https://www.overwolf.com/app/Thunderstore-Thunderstore_Mod_Manager) or [r2modman](https://thunderstore.io/package/ebkr/r2modman/).
2. Search for **"ItemInspect"** and click **Download**.
3. Launch the game.

### Manual
1. Install [BepInExPack PEAK](https://thunderstore.io/c/peak/p/BepInEx/BepInExPack_PEAK/).
2. Put `nixi.ItemInspect.dll` into `BepInEx/plugins` in your PEAK game folder.
3. Run the game once — a config file will be created.

---

## ⚙️ Configuration

After the first launch, edit `BepInEx/config/nixi.iteminspect.cfg` with any text editor (or use the in-game mod settings).

| Section | Setting | Default | Description |
|---------|---------|---------|-------------|
| General | `InspectKey` | `F` | Inspect key: a letter or digit, `Alt`, `Ctrl`, `Shift`, `Tab`, `Space`, `Backquote`, `Mouse3`, `Mouse4`, `Mouse5` |
| General | `LoopWhileHeld` | `true` | Keep inspecting while the key is held |
| General | `BringToCenter` | `true` | Bring the item towards the center of the screen |
| Animation | `Style` | `Auto` | `Auto`, `Random` or a fixed style: `Classic`, `Flip`, `Toss`, `Gentle`, `Lift`, `Heavy`, `Blade` |
| Animation | `ItemStyles` | *(empty)* | Your own rules for `Auto`: `name_part=Style`, comma-separated |
| Animation | `Speed` | `1` | Animation speed (2 = twice as fast) |
| Animation | `RotationIntensity` | `1` | Rotation angle multiplier |
| Animation | `Smoothing` | `0.3` | Motion smoothing (0 = exact curve, 2 = very soft) |
| Hands | `OneHandItems` | `berry, mushroom, …` | Items inspected with one hand (parts of names, comma-separated) |
| Hands | `OneHandSmallItems` | `true` | Inspect any small item (under 30 cm) with one hand |
| Swing | `EnableSwing` | `true` | Knife swing on LMB |
| Swing | `SwingItems` | `dagger, ritual, knife, sword, blade` | Which items can swing |
| Prompt | `ShowPrompt` | `true` | Show the "inspect" / "swing" hints |
| Prompt | `PromptText` | `осмотр` | Text of the inspect hint |
| Prompt | `SwingPromptText` | `взмах` | Text of the swing hint |

---

## ❓ Notes

- Inspecting stops smoothly when you use the item, switch it, start climbing or pass out.
- It doesn't start while a menu, the chat or the inventory wheel is open.
- The animation is local: other players may see the item turn, but your arms stay in the usual pose for them.
- Two-handed items like the Cure-All always stay in both hands.
