Vendored third-party code (MIT). Downloaded from registry.npmjs.org; sha512 verified against dist.integrity.
@xterm/xterm 6.0.0       sha512-TQwDdQGtwwDt+2cgKDLn0IRaSxYu1tSUjgKarSDkUM0ZNiSRXFpjxEsvc/Zgc5kq5omJ+V0a8/kIM2WD3sMOYg==  (lib/xterm.mjs, css/xterm.css)
@xterm/addon-fit 0.11.0  sha512-jYcgT6xtVYhnhgxh3QgYDnnNMYTcf8ElbxxFzX0IZo+vabQqSPAjC3c1wJrKB5E19VwQei89QCiZZP86DCPF7g==  (lib/addon-fit.mjs)
Source map comments removed. Licenses alongside.
three 0.186.1            sha512-blFeqb49wRCSGUGj7gtpfnSGHy2lwDk94RhUmS1c/hTby70kvChbWpkJ4Pm1390LqzzvTmzgXKHPEafJwCb8jA==  (three/)
  build/three.core.js, build/three.module.js and the addons under three/addons/ (OrbitControls, CSS2DRenderer,
  RoomEnvironment, EffectComposer, RenderPass, UnrealBloomPass, OutputPass, ShaderPass, MaskPass, Pass,
  CopyShader, LuminosityHighPassShader, OutputShader), each minified on its own with esbuild 0.25
  (--minify --format=esm --legal-comments=inline; no bundling). Import specifiers rewritten because the
  dashboard has no import map (its CSP forbids inline scripts): "./three.core.js" -> "./three.core.min.js"
  in three.module.min.js, and "three" -> "../../three.module.min.js" in the addons. Nothing else changed.
