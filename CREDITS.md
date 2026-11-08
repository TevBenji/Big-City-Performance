# Credits

Big City Performance is by TevBenji, GPL-3.0.

- **Placemaker** by jackwrichards (GPL-3.0): the project file layout is adapted from it, by way of Stepped Streets and
  Footbridge Builder.
- The UI bundle's setup (webpack configuration, the game's UI type declarations, the CSS checks, the error boundary and the
  way the count is placed into the population box) is adapted from **Placemaker** (GPL-3.0) by way of Footbridge Builder; Placemaker's
  error boundary in turn comes from Grid Road Generator by Ti4goc.
- **Harmony 2.2.2** by Andreas Pardeike, Copyright (c) 2017, MIT: runtime patching of the game's managed code. It ships
  with the mod as `0Harmony.dll`; its licence is in [0Harmony-LICENSE.txt](0Harmony-LICENSE.txt).
- The approach draws on published research and other games:
  - population sampling in transport simulation (MATSim);
  - path sharing and caching (Factorio, Supreme Commander 2);
  - Paradox's pop grouping (Stellaris, Victoria 3).
