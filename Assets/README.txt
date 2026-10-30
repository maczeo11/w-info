W-INFO Visual Assets
======================

Minimalist Vector Assets (SVG):
- logo.svg: Flat minimalist icon on dark squircle canvas (#09090b).
- logo-mark.svg: Standalone transparent vector mark with currentColor fill (pure 2D geometry, zero dependencies, ready for web/app/docs).

MSIX Manifest Tile Assets (PNG):
- StoreLogo.png (50x50)
- Square44x44Logo.png (44x44)
- Square150x150Logo.png (150x150)
- logo.png (512x512) - the brand master. The three tiles above are scaled
  from it by scripts/make-icons.ps1 (package.ps1 runs that on every build).
  To re-brand, replace logo.png (and logo.svg) with a square PNG, 512 px or
  larger, with transparent corners; nothing else needs to change. The same
  image also appears in the Medium and Large card headers: the provider
  inlines the packaged Square150x150Logo.png into the card as a data URI.

Design Philosophy:
- Pure flat minimalism: Zero 3D effects, zero gradients, zero shadows, zero blur filters.
- Gemini 4-Pointed Precision Spark: Crisp astroid geometry with an inner diamond aperture.
- Central Telemetry Node: High-contrast electric cyan sensor core (#00F0FF).
- 100% unencumbered, non-copyrighted, and reusable across web, mobile, CLI, or desktop.
