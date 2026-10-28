# Authorship and acknowledgements

## Authorship

SkyRender was created by **Piranha91** together with **Claude**, Anthropic's AI model.

- **Piranha91** set the goals and requirements, directed the design, chose every trade-off,
  validated the output against the game by eye, and reviewed and accepted each change.
- **Claude** wrote the code. No code in the initial release (3.0.0) was written by hand: the
  libraries, shaders, tests, tools, sample app and documentation were all written by Claude, under
  Piranha91's direction.

## Acknowledgements

SkyRender stands on the work of these projects and their creators.

### Nifly

**ousnius** — [github.com/ousnius/nifly](https://github.com/ousnius/nifly) (GPL-3.0)

The C++ library that reads and writes the Gamebryo/NetImmerse NIF format. Every mesh SkyRender
renders is parsed by nifly.

### NiflySharp

**SteveTownsend** — [github.com/SteveTownsend/niflyswig](https://github.com/SteveTownsend/niflyswig)
(GPL-3.0), packaged as `niflysharp` together with ousnius

The SWIG-generated C# bindings that make nifly usable from .NET. SkyRender depends on the
`niflysharp` package.

### Mutagen

**Noggog** and contributors — [github.com/Mutagen-Modding/Mutagen](https://github.com/Mutagen-Modding/Mutagen)
(GPL-3.0)

The .NET library for reading and writing Bethesda plugins and archives. SkyRender itself takes no
dependency on it, by design, but it was developed inside Mutagen-based applications that resolve
NPC records, load orders and archives through Mutagen, and the sample app reads the game's BSA
archives with it.

### Community Shaders

**doodlum** and contributors — [github.com/community-shaders/skyrim-community-shaders](https://github.com/community-shaders/skyrim-community-shaders)
(GPL-3.0)

Not used as a library. Parts of Community Shaders' DirectX (HLSL) lighting shader were ported to
GLSL in `src/SkyRender/Shaders/basic.frag`: the FaceGen tint composition (Pegtop soft-light
FaceTint blend), the SkinTint / FaceGen RGB tint base colour and its colour-shift constant, and the
engine-style detail-map multiply. Reading that code is what made character skin tint match the
game.

### Also used

- [OpenTK](https://github.com/opentk/opentk) (Team OpenTK, MIT): OpenGL bindings, maths and
  windowing; OpenTK.GLWpfControl hosts the renderer in WPF.
- [Pfim](https://github.com/nickbabcock/Pfim) (Nick Babcock, MIT): DDS texture decoding.
- [ReactiveUI](https://github.com/reactiveui/ReactiveUI), [Fody](https://github.com/Fody/Fody) /
  PropertyChanged.Fody, and [Extended WPF Toolkit](https://github.com/xceedsoftware/wpftoolkit):
  the character viewer's view model and WPF panels.
- [NifSkope](https://github.com/niftools/nifskope) (the NifTools team): its shaders and NIF
  documentation were the reference for many material rules; no NifSkope code is included.

## License

SkyRender is licensed under GPL-3.0-only (see [LICENSE](LICENSE)), which is compatible with the
GPL-3.0 licences of nifly, NiflySharp and the ported Community Shaders code.
