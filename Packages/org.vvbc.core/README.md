# Virtual Venue Baseball Components

Virtual Venue Baseball Components (VVBC) is a modular, open-source baseball system for virtual reality spaces. The project is currently in early development.

## Requirements

- Unity 2022.3.22f1
- VRChat SDK - Worlds 3.10.6
- TsukimiSharp (`com.tsukimicode.udon-tools`) 0.10.2

Runtime sources are T# in `Runtime/`; Program Assets belong in
`Runtime/TsukimiProgramAssets/`. No generated UdonSharp implementation is distributed.
See the repository `docs/development/tsharp-toolchain.md` for validation.

## Back screen displays

Place `InningScoreboard`, `CountDisplay`, `BaseRunnerDisplay` and two instances
of `BattingOrder` from `Runtime/Prefabs/` under a shared Canvas. Use the root
RectTransforms to position and scale each panel. The package's Match sample
shows one arrangement.

Assign the scene's MatchController to **Match** on each panel. On both batting
orders, assign the RosterManager to **Roster** and select **Away** or **Home**
in **Team**. **Team Name** is an optional local label, with AWAY and HOME as
the defaults. The batting orders remain visible independently of match readiness
when the roster is ready.

In Edit Mode, select a panel's UdonBehaviour and open **Back Screen Preview**.
Set balls/strikes/outs, select the occupied bases, or enable **Sample Lineup**
to edit names, positions and the current batter. Empty a sample name to preview
a vacant row. **Reset Preview** clears the sample output. The preview updates
the selected panel's graphics with Undo support. The appearance fields also
apply at runtime, where the game and roster supply the data.

All text uses the shared Barlow Semi Condensed Bold TMP asset. Keep the global
TextMeshPro **Fallback Font Assets** list empty to use VRChat's built-in fallback.
Multilingual sample names can show missing glyphs in Unity's Editor even with
the correct runtime configuration. See [VRChat's TextMesh Pro documentation](https://creators.vrchat.com/worlds/components/textmeshpro/).

## License

MIT License

### Third-party assets

- `Runtime/Fonts/BarlowSemiCondensed/`: Barlow Semi Condensed Bold, Copyright 2017 The Barlow
  Project Authors (https://github.com/jpt/barlow), licensed under the SIL Open Font License 1.1
  (`OFL.txt` beside the font). The TMP SDF Font Asset in that folder is generated from it and is
  distributed under the same license. The MIT License does not cover these files.
