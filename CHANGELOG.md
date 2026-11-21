# Changelog

Newest first. Every release also gets a page in `Mochi/Assets/Guide/whatsnew/<version>.json`, so people see it on screen: see
[docs/ADDING-A-FEATURE.md](docs/ADDING-A-FEATURE.md).

## [Unreleased]

<!-- New things go here while you work, then move under a version heading when you release. -->

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
