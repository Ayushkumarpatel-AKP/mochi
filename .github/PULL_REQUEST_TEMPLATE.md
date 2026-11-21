## What this changes

<!-- One or two sentences, in plain words. What can a person do now that they could not before? -->

## Type

- [ ] New feature
- [ ] Fix
- [ ] Tidy-up (no change for users)

## Checklist for a new feature

See [docs/ADDING-A-FEATURE.md](../docs/ADDING-A-FEATURE.md).

- [ ] It works: I ran the app and used it
- [ ] A **What's new** page: `Mochi/Assets/Guide/whatsnew/<version>.json` (copied from `_template.json`)
- [ ] The first-run tour mentions it, if it adds a page or a button (`Assets/Guide/welcome.json`)
- [ ] `<Version>` bumped in `Mochi.csproj` and `AppVersion` in `installer/mochi.iss`
- [ ] `CHANGELOG.md` has a line for it
- [ ] `README.md` and `docs/screenshots` updated, if people should see it
- [ ] Old `settings.json` files still load (new settings have a safe default)

## Screenshots

<!-- A picture or a short recording helps. -->
