<p align="center">
  <img src="ShadowDuskBanner.png" alt="ShadowDusk" />
</p>

# ShadowDusk — Brand Guide

This folder holds ShadowDusk's brand assets and the guidance for using them. It is the
single source of truth for the logo, icon, favicon, colors, and naming. If you add or
update a brand asset, update this file too.

> **One-liner:** ShadowDusk is a cross-platform HLSL → `.mgfx` shader compiler for
> MonoGame and KNI — compile `.fx` on Linux, macOS, or Windows (and in the browser),
> with output that loads and renders like `mgfxc`'s.

---

## The name

**ShadowDusk** — always **one word**, **PascalCase** with a capital `S` and capital `D`.

| Write it | Don't write it |
|---|---|
| ShadowDusk | Shadow Dusk · Shadowdusk · shadowDusk · SHADOWDUSK · Shadow-Dusk |

The mark is a sphere split in two: a dark, low-poly **shadow** half and a glowing,
triangulated-wireframe **dusk** half whose gradient runs from violet through magenta to
amber — a twilight horizon rendered as a 3D mesh. The "shadow + dusk" metaphor reads as
*lighting and shading* (what shaders do) at the *transition between dark and light* (dusk).

### Naming conventions used across the project

| Thing | Name | Notes |
|---|---|---|
| The project / product | **ShadowDusk** | The in-memory compiler library is the product. |
| The CLI command / tool | **ShadowDuskCLI** | The `dotnet tool` command and the self-contained binary. PascalCase, no space. |
| NuGet packages | **ShadowDusk.Core / .HLSL / .GLSL / .Compiler / .Cli / .Wasm** | Six packages, one shared version. |
| Reference tool we emulate | `mgfxc` | MonoGame's compiler. We are a *drop-in replacement* for it — so `mgfxc` appears in prose about compatibility/fidelity, but it is **not** our command name. |

---

## Tagline & positioning

- **Tagline (on the banner):** *Cross-platform shader compiler for game engines.*
- **Short positioning:** A self-contained, in-memory, cross-platform drop-in `mgfxc`
  replacement — add the package, call the API, get `.mgfx` bytes; nothing else to install.
- **What makes it matter:** it reaches where `mgfxc` can't (Linux/macOS, and in-browser via
  WASM) while producing output that renders the same in the real MonoGame/KNI runtime.

Voice: precise, technical, understated. Claims are backed by validation, not adjectives.

---

## Assets

All brand images live in this `Brand/` folder and are the only copies — references elsewhere
point here (or, for the docs site, are copied from here at build time).

| File | Size | Format | Role |
|---|---|---|---|
| [`ShadowDuskBanner.png`](ShadowDuskBanner.png) | 2508 × 627 | PNG (RGB) | Wide banner with wordmark + tagline. Used at the top of the repo `README.md`. |
| [`ShadowDuskIcon.png`](ShadowDuskIcon.png) | 1254 × 1254 | PNG (RGB) | High-resolution app icon / logo. Used as the docs-site navbar logo. Use this wherever a **large, high-quality** image is allowed. |
| [`ShadowDuskFavIcon.png`](ShadowDuskFavIcon.png) | 64 × 64 | PNG (RGBA) | Small icon for size-constrained slots: the docs-site **favicon** and the **`ShadowDusk.Cli` NuGet package icon**. |

### Which icon to use where — the size rule

- Use **`ShadowDuskFavIcon.png`** only where dimensions or file size are capped — e.g.
  browser favicons and the NuGet package icon (nuget.org limits the package icon to **1 MB**,
  and the full-resolution `ShadowDuskIcon.png` is ~1.5 MB, so it cannot go there).
- Use **`ShadowDuskIcon.png`** everywhere a larger, better-quality image is allowed (docs
  logo, READMEs, slides, store listings, social avatars).

### Where the assets are wired in the repo

| Slot | Asset | Wired in |
|---|---|---|
| Repo README banner | `ShadowDuskBanner.png` | [`README.md`](../README.md) |
| Docs-site navbar logo | `ShadowDuskIcon.png` | [`docfx.json`](../docfx.json) `_appLogoPath` |
| Docs-site favicon | `ShadowDuskFavIcon.png` | [`docfx.json`](../docfx.json) `_appFaviconPath` |
| `ShadowDusk.Cli` NuGet icon | `ShadowDuskFavIcon.png` | [`src/ShadowDusk.Cli/ShadowDusk.Cli.csproj`](../src/ShadowDusk.Cli/ShadowDusk.Cli.csproj) `<PackageIcon>` |

### Usage do's and don'ts

- **Do** place the logo/icon on a dark background — the mark is designed for deep navy and
  reads best there.
- **Do** keep clear space around the logo (at least the height of the sphere's radius).
- **Don't** stretch, squash, rotate, or recolor the mark; don't add drop shadows or outlines.
- **Don't** put the mark on a busy or light background that kills the glow gradient.
- **Don't** rebuild the wordmark in a different font — use `ShadowDuskBanner.png`.

---

## Color palette

Representative values **sampled from the current assets** (not a hand-authored spec — treat
as approximate and confirm against the source files before using in print).

| Swatch | Name | Hex (approx.) | Role |
|---|---|---|---|
| ⬛ | Twilight Navy | `#05071A` | Primary background / canvas (near-black blue). |
| 🟦 | Shadow Indigo | `#0A0B2A` | The dark "shadow" hemisphere; secondary surfaces. |
| 🔵 | Nebula Blue | `#3E3B92` | Cooler edge of the wireframe gradient. |
| 🟣 | Dusk Violet | `#6E2A8C` | Mid gradient; primary accent. |
| 🟥 | Magenta Rose | `#C84A6E` | Warm-mid gradient; the "Dusk" wordmark tint. |
| 🟧 | Ember / Amber | `#F0875F` | Hottest edge of the gradient; sunset highlight / call-to-action warmth. |

The signature look is the **violet → magenta → amber** gradient (the "dusk" sweep) glowing
against **Twilight Navy**.

---

## Typography

The wordmark pairs **"Shadow"** in a light/near-white weight with **"Dusk"** in the warm
violet-to-amber gradient, set in a rounded geometric sans-serif. The wordmark is delivered
pre-rendered in `ShadowDuskBanner.png`; there is no separate licensed font shipped here, so
reproduce the wordmark by using the banner asset rather than re-typesetting it. For body and
documentation text, the project uses each surface's default sans-serif (the DocFX site uses
its template's system font stack).

---

## Links

- **Repository:** https://github.com/kaltinril/ShadowDusk
- **Documentation site:** https://kaltinril.github.io/ShadowDusk/
- **NuGet:** the six `ShadowDusk.*` packages (search `ShadowDusk` on nuget.org)
