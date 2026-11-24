# Querithm brand assets

The mark: the database is the Q, and its middle layer runs out as a violet pulse (the "rhythm" in the name).
It has no background in any theme. Every file below is drawn from the same geometry.

| File | Use |
|---|---|
| `querithm-logo.svg` | Master app icon, square with padding. Source of every generated icon. |
| `querithm-mark.svg` | The mark cropped tight, for small UI sizes. Same as `public/favicon.svg`. |
| `querithm-wordmark.svg` | Mark + name, for light backgrounds. |
| `querithm-wordmark-dark.svg` | Mark + name, for dark backgrounds. |

Colours: body `#38BDF8 → #2F6FED → #4F46E5`, pulse `#D8B4FE → #A855F7`.

## Where the mark is used

| Place | File |
|---|---|
| App icons (window, taskbar, Dock, Start menu, Linux desktop entry, Windows Store tiles) | `src-tauri/icons/*` |
| Windows installer (.exe) icon, uninstaller icon, header and welcome/finish sidebar | `src-tauri/tauri.conf.json` → `bundle.windows.nsis`, images in `src-tauri/installer/` |
| Windows MSI banner and welcome dialog | `bundle.windows.wix`, images in `src-tauri/installer/` |
| macOS disk image background | `bundle.macOS.dmg`, `src-tauri/installer/dmg-background.png` |
| Start screen and its animation | `index.html` (`#splash`), `public/boot-splash.css` |
| Top bar, welcome screen, About | `src/app/Logo.tsx` |
| Browser tab (dev web mode) | `public/favicon.svg` |
| README | the two wordmarks |

Keep `icons/icon.png` (512 px) first in `bundle.icon`: on Linux and macOS the app window takes the first PNG of that
list as its icon, and Linux installs every listed PNG into the icon theme at its size.

## Regenerating

App icons, after changing `querithm-logo.svg`:

```bash
pnpm tauri icon docs/brand/querithm-logo.svg
rm -rf src-tauri/icons/android src-tauri/icons/ios   # mobile targets are not built
cp docs/brand/querithm-logo.svg src-tauri/icons/icon.svg
```

The installer images are fixed sizes and must be 24-bit BMP for NSIS and WiX:

| File | Size |
|---|---|
| `nsis-header.bmp` | 150 × 57 |
| `nsis-sidebar.bmp` | 164 × 314 |
| `wix-banner.bmp` | 493 × 58 (the installer writes its title on the left, so keep the left side white) |
| `wix-dialog.bmp` | 493 × 312 (the picture fills the left 164 px; the installer writes on the white right side) |
| `dmg-background.png` | 660 × 400 (app icon at 180, 170; Applications at 480, 170) |
