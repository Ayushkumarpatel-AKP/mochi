# Changelog

Newest first. Every release also gets a page in `Mochi/Assets/Guide/whatsnew/<version>.json`, so people see it on screen: see
[docs/ADDING-A-FEATURE.md](docs/ADDING-A-FEATURE.md).

## [Unreleased]

<!-- New things go here while you work, then move under a version heading when you release. -->

## 1.1.4

### Fixed
- **Drag and drop in the panel, dock, command bar and tiles.** The bar no longer rebuilds under the pointer while you drag across it, tools are filtered for your files before the pointer arrives, the tool under the pointer lights up, and a drop anywhere on the bar keeps the files so you can search for the tool.
- A drag that carries no files now says so, instead of doing nothing.

### Added
- **Pin from anywhere.** Right-click a tool in any style, or tap the star in the command bar, to pin it to the front.
- **A more useful "Done" card.** It shows how much smaller the result is (for example 2.4 MB to 186 KB, 92% smaller) with Open and Show in folder buttons, and the bouncing Mochi is calm. A full disk is explained in plain words.

## 1.1.3

### Fixed
- **The donut ignored your own slices when you opened it with a file.** It showed only Mochi's suggestions for that kind of file. Now the slices you chose come first (when they fit the file), Mochi's suggestions fill the empty spots, and whatever does not fit the front goes to the back face. A donut you made for a kind of file still wins.

## 1.1.2

### Added
- **An on-screen guide.** The first time Mochi opens, a Mochi walks you through the real screen: it dims the app, lights up the button or menu entry it is talking about and explains it. Open it again from **About** > **Take the tour**.
- **"What's new" pages** that show once after an update, driven by JSON in `Assets/Guide/whatsnew`, with a template for new releases.
- **Mochi mascots** that pop up in the app: a different happy Mochi on every finished job (a puzzled one on a failure), a sticker next to each page title, and a sleeping Mochi when there is no history yet. Click one and it squishes.
- Pull request and feature-idea templates, and a guide for adding a feature.

### Changed
- **Sakura is the default theme** for new users (soft cherry-blossom pink with a pink accent). Everyone can still pick any of the 12 looks on the Theme page.
- A **cosy default look**: strawberry-milk pink in light mode, warm cocoa in dark mode, berry accent, and Fredoka titles.
- A bigger Mochi logo in the sidebar and a bigger app icon.
- The donut's labels wrap, shrink and trim so long names stay inside their slice.
- The Presets live preview has room for the group ring, so it is no longer cut off.

### Fixed
- Boxes and chips (preset chips, the live preview, tool ticks) were nearly invisible in light themes.
- The About page showed the old "Version 2.3.0".

## 1.1.1 and earlier

Early versions: the donut, presets, the engines, themes, the AI tools and smart files.
