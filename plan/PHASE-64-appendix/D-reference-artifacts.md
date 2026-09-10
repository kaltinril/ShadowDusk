# D. Reference artifacts: how the inputs were made, and where each runtime's rules were read

All on 2026-09-09, from the worktree at commit 445fddb (`origin/main` after PR #201), Release build
of `src/ShadowDusk.Cli`, natives restored by `tools/restore.ps1`, `dotnet tool restore` for the
pinned `dotnet-mgcb` 3.8.4.1.

## 1. ShadowDusk `.xnb` per CLI selector

```text
ShadowDuskCLI tests/fixtures/shaders/Grayscale.fx Grayscale_<sel>.xnb /Profile:<sel>      (OpenGL, DirectX_11, DirectX_12, Vulkan, FNA)
ShadowDuskCLI tests/fixtures/shaders/Grayscale.fx Grayscale_tr_<name>.xnb --target-runtime <name>   (monogame-gl, monogame-dx, monogame-gl-v11, kni-knifx, fna)
```

All ten exit 0. First 16 bytes and the start of the payload (offset 0x85 is the int32 payload
length, 0x89 the payload):

```text
Grayscale_OpenGL.xnb            size=682   584e 4264 0500 aa02 0000 0177 4d69 6372  XNBd  ... payload MGFX v10 (0x0a), profile 0x00 (GL)
Grayscale_DirectX_11.xnb        size=1272  584e 4277 0500 f804 0000 0177 4d69 6372  XNBw  ... payload MGFX v10 (0x0a), profile 0x01 (DX11)
Grayscale_DirectX_12.xnb        size=4426  584e 4247 0500 4a11 0000 0177 4d69 6372  XNBG  ... payload MGFX v11 (0x0b), profile 0x02 (DX12/DXIL)
Grayscale_Vulkan.xnb            size=1608  584e 4256 0500 4806 0000 0177 4d69 6372  XNBV  ... payload MGFX v11 (0x0b), profile 0x50 (Vulkan/SPIR-V)
Grayscale_FNA.xnb               size=909   584e 4277 0500 8d03 0000 0177 4d69 6372  XNBw  ... payload fx_2_0 (0x01 0x09 0xff 0xfe ... 0xc4 ...)
Grayscale_tr_monogame-gl.xnb    size=682   (identical to /Profile:OpenGL)
Grayscale_tr_monogame-dx.xnb    size=1272  (identical to /Profile:DirectX_11)
Grayscale_tr_monogame-gl-v11.xnb size=775  584e 4264 ...                              XNBd  ... payload MGFX v11 (0x0b), profile 0x00
Grayscale_tr_kni-knifx.xnb      size=1180  584e 4264 ...                              XNBd  ... payload "KNIF" 0x0b
Grayscale_tr_fna.xnb            size=909   (identical to /Profile:FNA)
```

Bytes 10..140 are identical in every file:
`01 77` (one reader, name length 119) +
`Microsoft.Xna.Framework.Content.EffectReader, MonoGame.Framework, Version=3.8.4.1, Culture=neutral, PublicKeyToken=null`
+ `00 00 00 00` (reader version) + `00` (shared resources) + `01` (type id).

## 2. Stock `dotnet mgcb` `.xnb` per platform

Per platform, a `build.mgcb` of the form

```text
/outputDir:bin
/intermediateDir:obj
/platform:<P>
/config:
/profile:Reach
/compress:False

#begin <abs path>/tests/fixtures/shaders/Grayscale.fx
/importer:EffectImporter
/processor:EffectProcessor
/build:<abs path>/tests/fixtures/shaders/Grayscale.fx;Grayscale
```

run as `dotnet mgcb /@:build.mgcb` (3.8.4.1, from the repo's tool manifest) and, for the two
platforms 3.8.4.1 does not know, `mgcb.exe /@:build.mgcb` from a scratch
`dotnet tool install dotnet-mgcb --version 3.8.5 --tool-path <scratch>`.

```text
mgcb 3.8.4.1  /platform:DesktopGL    size=796  XNBd 05 00   manifest ..., Version=3.8.4.1, ...   payload MGFX v10 profile 0
mgcb 3.8.4.1  /platform:Windows      size=968  XNBw 05 00   same manifest                        payload MGFX v10 profile 1
mgcb 3.8.4.1  /platform:Android      size=796  XNBa 05 00   same                                 payload identical to DesktopGL's
mgcb 3.8.4.1  /platform:iOS          size=796  XNBi 05 00   same                                 identical
mgcb 3.8.4.1  /platform:MacOSX       size=796  XNBX 05 00   same                                 identical
mgcb 3.8.4.1  /platform:Web          size=796  XNBb 05 00   same                                 identical
mgcb 3.8.5    /platform:WindowsDX12             XNBG 05 00   manifest ..., Version=3.8.5.0, ...   payload MGFX v11 profile 2
mgcb 3.8.5    /platform:DesktopVK               XNBV 05 00   same                                 payload MGFX v11 profile 0x50
mgcb 3.8.5    /platform:DesktopGL               XNBd 05 00   same                                 payload MGFX v11 profile 0
mgcb 3.8.5    /platform:Windows                 XNBw 05 00   same                                 payload MGFX v11 profile 1
```

Note the GL-family platforms differ from each other ONLY in byte 3: mgcb writes the same
payload and the same manifest for DesktopGL / Android / iOS / MacOSX / Web. That is why
ShadowDusk emitting `'d'` for all of them is sufficient: every runtime accepts `'d'`, and no runtime
compares the byte to itself.

## 3. Where each runtime's rules were read

| Runtime | Source used | Where |
|---|---|---|
| MonoGame 3.8.5 | git tag `v3.8.5` (4f9e372), sparse clone of `MonoGame.Framework/Content` | `ContentManager.cs:37` (whitelist), `:491-512` (header check, version 4/5, LZX/LZ4 flags), `ContentTypeReaderManager.cs:253-278` (`PrepareType`) |
| MonoGame 3.8.2.1105 and 3.8.1.263 | `ilspycmd` over `MonoGame.Framework.dll` from `monogame.framework.desktopgl/<v>/lib/` in the NuGet cache | `PrepareType` textually identical to 3.8.5; whitelists: 3.8.1.263 = `w x i a d X W n M r P 5 O S G b m p v g l` (no `V`), 3.8.2.1105 = `w x i a d X n r P 5 O S G b W M m p v g l` (no `V`) |
| KNI 4.2.9001 | git tag `v4.2.9001` (f69c146), sparse clone; cross-checked against `ilspycmd` over `nkast.xna.framework.content/4.2.9001/lib/net8.0/Xna.Framework.Content.dll` (identical resolver) | `src/Xna.Framework.Content/Content/ContentManager.cs:30-65` (whitelist), `:215-235` (platform text, version, `ContentFlagHiDef`, `ContentFlagCompressedExt`), `ContentTypeReaderManager.cs:195-286` (`ResolveReaderType`), `src/Xna.Framework.Graphics/Content/EffectReader.cs` (reader), `src/Xna.Framework.Content.Pipeline.Graphics/Serialization/Compiler/CompiledEffectWriter.cs:22-30` (what KNI's pipeline writes) |
| KNI 4.3.9001 and `main` | raw `ContentTypeReaderManager.cs` at tag `v4.3.9001` and at `main` (both 293 lines) | lines 249-261: the three appended-assembly `Type.GetType` calls each wrapped in `catch (FileLoadException) { /* ignore */ }`; the rest of the resolver unchanged |
| FNA 26.06 | `validation/FnaValidation/external/FNA` (tag `26.06`, e1520a5) | `src/Content/ContentManager.cs:77-93` (whitelist), `:300-316` (an unlisted byte falls to the raw-asset branch), `:462-472` (version 4/5, flag 0x80), `ContentTypeReaderManager.cs:59` (regex), `:322-325` (`PrepareType` = one regex replace), `ContentReaders/EffectReader.cs` |
| FNA `master` (76b1aef) | sparse clone of `src/Content` | whitelist and regex identical to 26.06 |

## 4. The KNI resolver, step by step, for the mgcb-shaped name (why 4.2.9001 throws)

Input: `Microsoft.Xna.Framework.Content.EffectReader, MonoGame.Framework, Version=3.8.4.1, Culture=neutral, PublicKeyToken=null`

1. Contains `PublicKeyToken` → `Regex.Replace(@"(.+?), Version=.+?$", "$1")` →
   `Microsoft.Xna.Framework.Content.EffectReader, MonoGame.Framework`.
2. `.Replace(", Microsoft.Xna.Framework.Graphics", ", Xna.Framework.Graphics")` etc.: no match
   (the only comma-prefixed assembly part is `, MonoGame.Framework`). `Type.GetType` → `null`.
3. `.Replace(", Microsoft.Xna.Framework", ", Xna.Framework")`: no match. `Type.GetType` → `null`.
4. `readerTypeName + ", Xna.Framework.Graphics"` →
   `Microsoft.Xna.Framework.Content.EffectReader, MonoGame.Framework, Xna.Framework.Graphics`.
   `Type.GetType` parses `MonoGame.Framework, Xna.Framework.Graphics` as one assembly name whose
   second component is not `Version=`/`Culture=`/`PublicKeyToken=` →
   **`FileLoadException: The given assembly name was invalid.`** Uncaught in 4.2.9001; caught and
   ignored in 4.3.9001, which then reaches step 5.
5. (4.3 only) `.Replace(", MonoGame.Framework", ", Xna.Framework")` → `null`; `", Xna.Framework.Content, …"` → `null`;
   `", Xna.Framework.Graphics"` → **resolves** (`EffectReader` is an internal type of `Xna.Framework.Graphics`).

Same input through MonoGame: step 1, then no replacement matches, `Type.GetType` resolves because
`MonoGame.Framework` is the executing assembly. Through FNA: the regex alternative
`MonoGame.Framework` with the full triple matches; replaced by `FNA, Version=26.6.0.0, …`; resolves.

The XNA-4.0 name (`…, Microsoft.Xna.Framework.Graphics, Version=4.0.0.0, Culture=neutral, PublicKeyToken=842cf8be1de50553`)
resolves at KNI step 2, at MonoGame's `Graphics` replacement, and at FNA's first regex alternative,
which is why it is the intersection.
