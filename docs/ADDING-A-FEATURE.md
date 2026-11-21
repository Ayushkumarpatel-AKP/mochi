# Adding a feature to Mochi

A short recipe, so every new feature arrives the same way: it works, it is easy to find, and people hear about it.

## 1. Build it

| You are adding | Where it goes |
| --- | --- |
| A new **conversion** (a target format, a size limit, a mode) | `Mochi/Services/PresetCatalog.cs`: one line per preset. Nothing else is needed if an engine already handles it. |
| A new **engine** (a new kind of file) | A class in `Mochi/Services/Engines/` that implements `EngineBase`, then list it in `Converter.All`. |
| A new **tool window** (like Annotate or Erase) | A window in `Mochi/Views/`, a hook in `ToolHooks`, and a preset whose mode opens it. |
| A new **page** | A page in `MainWindow.axaml`, a key in `MainViewModel.Keys`, and a sticker for it in `PageMascots` (`MainWindow.axaml.cs`). |
| A new **setting** | A property in `Services/Settings.cs` with a safe default. Old settings files keep working. |

Keep the code readable and match the file you are in. Run the app and use the feature before you call it done.

## 2. Tell people: the "What's new" pages

Every release announces its features on screen, with Mochi pointing at the real thing. It is data, not code.

1. Copy `Mochi/Assets/Guide/whatsnew/_template.json` to `Mochi/Assets/Guide/whatsnew/<version>.json`. The file name is the version from `Mochi.csproj` and nothing else, for example `1.2.0.json`.
2. Write **one page per feature**, one to three pages in total:
   - `mascot`: a sticker from `Mochi/Assets/Mascot` (the file name without `.png`). The template lists them.
   - `title`: the feature in a few words.
   - `body`: one friendly sentence about what it does for the person using Mochi.
   - `bullets`: one to four short points, and where to find it.
   - `tip`: optional, one small extra.
   - `target`: optional, what Mochi lights up on screen: `dropzone`, `preset`, `donutbtn` or `nav:<page>` (`home`, `presets`, `smart`, `ai`, `engines`, `watch`, `triggers`, `toolbar`, `fields`, `general`, `theme`, `about`). Leave it out and the card sits in the middle.
3. That is all. The next time someone opens Mochi after updating, the pages show once. Someone who skipped a few versions sees every page they missed, oldest first. The **What's new** button on the About page shows the current version's pages again.

A new feature that adds a **page or a button** should also get a line in the first-run tour: `Mochi/Assets/Guide/welcome.json` (same fields, and it can point at the new page with `"target": "nav:<page>"`).

## 3. Ship it

1. Bump `<Version>` in `Mochi/Mochi.csproj` and `AppVersion` in `installer/mochi.iss`.
2. Add the changes to `CHANGELOG.md`, newest on top.
3. Update `README.md` if the feature is big enough to be listed there, and add a screenshot to `docs/screenshots` if it is something people should *see*.
4. Run `installer/build.ps1`, install the result, and try the feature and the What's new pages on a clean profile. Delete `%APPDATA%\Mochi\settings.json` (or set `GuideSeen` to `false`) to see the first-run tour again.
5. Publish a GitHub release with `Mochi-Setup-<version>.exe` and the same notes as the changelog.

The pull request template has the same list as checkboxes.
