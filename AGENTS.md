# AGENTS.md

Small WinForms app (English UI by default, with external translations; Swedish, German, French, Spanish and Italian included) that finds unused ComfyUI model files by searching workflow JSONs, archives them, restores them and removes duplicate archive copies.
Not a git repository (no history/CI to consult), no package manager and no .NET SDK on the machine. All code is in `src\`; the UI is built in code (no designer files or `.resx`), and translations live in `lang\`.

## Build

- `.\build.cmd` (or `cmd /c build.cmd`) from the repo root. It uses the built-in .NET Framework 4.8 `csc.exe` — do not try `dotnet build` and do not install an SDK.
- That compiler is **C# 5 only**: no string interpolation, `?.`, `nameof`, expression-bodied members, auto-property initializers, tuples, `out var`, pattern matching or `using static`. Otherwise the build fails.
- Source files are UTF-8 and contain Swedish characters, so the build passes `/codepage:65001`. Keep it if you invoke `csc` manually.
- New `.cs` files must be added explicitly to `build.cmd` (no globbing).
- `src\app.config` is copied to `ComfyUI Model Manager.exe.config` by `build.cmd` (needed for long-path switches) — keep the exe/config names in sync.
- The icon is embedded twice in `build.cmd`: `/win32icon` and `/resource:...,ComfyUI.ModelManager.AppIcon.ico`. `MainForm.LoadAppIcon` looks up that exact resource name; renaming breaks the window icon silently (falls back).
- English is embedded from `lang\_template.ini` as `ComfyUI.ModelManager.English.ini`; that file is both the built-in language and the translation template. External languages live in `lang\*.ini` and are not compiled. If you add a new `.cs` file, add it to `build.cmd`.
- `LICENSE` is embedded as `ComfyUI.ModelManager.License.txt` (third resource in `build.cmd`) and shown by `MainForm.LoadLicenseText` in the About dialog's license link. Keep the resource name in sync; the file is only a fallback, so the license works even if `LICENSE` is not shipped next to the exe.

## Test / verify

- Headless self test: `Start-Process '.\ComfyUI Model Manager.exe' -ArgumentList '--selftest' -Wait -PassThru` and check `ExitCode` (0 = all passed). A plain `&` returns immediately because the target is a winexe.
- Results are also written to `selftest.log` (UTF-8, may show mojibake in the console). The test **deletes and recreates `testdata\`** at start — never store real data there.
- No unit-test framework; add checks as `Expect(...)` in `src\SelfTest.cs`. Fixture changes require updating the expected counts/relative paths there.
- The self test covers the matching, archiving, restore and duplicate rules end-to-end (case, `%20`, `\uXXXX`, subfolders, duplicate names, archive collisions, unused archived files hidden, bypassed/muted/notes, subgraph promotions and linked widgets, duplicate detection + re-checked deletion, both restore modes, re-scan). It also checks the language loader, cultures, placeholders, the shipped language files and the embedded MIT license resource. Keep it green.

## Architecture notes

- `Scanner.cs` is the engine: `FileScan.Scan` runs two passes (fast filename-candidate extraction, then a literal text fallback for models still unmatched), also walks the archive root, drops archived files that are neither used nor duplicates, and marks duplicated archive copies (same relative path + size). `FileScan.Restore` moves archived files back per file — keeping the archived structure or into a chosen folder under the models root — `FileScan.Archive` moves files preserving their structure, and `FileScan.DeleteDuplicates` permanently deletes archive copies after re-checking path and size.
- `WorkflowFilter.cs` parses UI-format JSON with `JavaScriptSerializer` from `System.Web.Extensions.dll` (referenced in `build.cmd`); keep that reference. It is the only place that decides which node/widget values count.
- `Lang.cs` is the localization service. English comes from the embedded `lang\_template.ini`; `lang\sv.ini`, `de.ini`, `fr.ini`, `es.ini`, `it.ini` override it. Missing keys fall back to English; malformed format strings also fall back. The language choice is `[General] Language` in `ComfyUI Model Manager.ini`. First run (or a missing language file) shows `LanguageDialog`; the About dialog (`?`/F1) has the same language picker, saves the choice and prompts for a restart, and shows a license link (`app.license`) that opens the embedded MIT text in a modal (`MainForm.ShowLicense`). Language files are UTF-8 `key=value` with `name=`, `culture=`, `\n` escapes and `{0}` placeholders; files starting with `_` and `en.ini` are ignored.
- Matching is by filename in the values collected by `WorkflowFilter` from UI-format JSON: it skips `mode` 2 (muted) and `mode` 4 (bypassed) nodes plus `Note`/`MarkdownNote`, follows subgraphs only through active instances, counts promoted instance widget values, and ignores a linked widget's stored default only when the link has an active source. Unknown/API/broken JSON falls back to raw text (no bypass detection), so false positives are preferred over missing a used model. The fallback match is filename-boundary aware (`dup.safetensors` must not match `useddup.safetensors`). Identical basenames in different folders (including models vs archive) deliberately flag **both** as used.
- Use `Paths.Long` (adds `\\?\`) and `ReadAllTextSmart` (BOM/UTF-8/ANSI fallback) instead of raw `File.*` when paths can be long or on a network share.
- Archive keeps the path relative to the models root; an existing target becomes `name (1).ext`; cross-volume moves are streamed copy+delete; an archive folder inside the models folder is rejected. Restore never overwrites: an existing target is skipped and reported, and its target must stay under the models root (never inside the archive).
- Settings live in `ComfyUI Model Manager.ini` next to the exe (legacy `settings.ini` is migrated once). It holds the user's live folder paths and `[General] Language`. Do not reset or overwrite it when testing; use a temp folder or the self test.
- `MainForm` builds all controls in code with `TableLayoutPanel`. Always add explicit `RowStyles`/`ColumnStyles`: a missing `RowStyle` produced a default ~100 px row and controls bleeding into the next row. Layout must remain usable at `MinimumSize` 840×580 in every language — action buttons auto-size, the hint label ellipsizes, and column headers wrap. Statuses are Used/Unused/Archived/Duplicate; selection is done with a real `CheckBox` positioned over the checkbox column header (selects only currently visible markable rows), and the three action buttons (Archive/Restore/Delete) only touch their own kind. Restore asks per file (structure or chosen folder) in a modal dialog before anything is moved. The About dialog derives its height from `layout.GetPreferredSize` after `PerformLayout`, using the description label's `MaximumSize` as the width constraint — do not hardcode a `ClientSize` height there, or the description leaves a large gap above the language picker again.
- Version/product metadata is in `src\AssemblyInfo.cs` and is read by the About dialog (`Assembly.GetName().Version`); the author/copyright/license values are `app.author`/`app.copyright`/`app.license` in the language files (the license keys are localized, the MIT text itself stays English).
- UI verification on this machine: capture with `PrintWindow(hwnd, hdc, 2)` — `CopyFromScreen` catches whatever window is on top. Click via `PostMessage(hwnd, 0x00F5 /* BM_CLICK */)`; a synchronous `SendMessage` blocks on buttons that open a modal dialog.

## Conventions

- User-facing strings go through `Lang.T`/`Lang.F` keys (English built-in, external files override); code comments are Swedish and identifiers English. Keep it that way.
- `README.md` is the English user manual — update it when behavior, UI or licensing changes. The project is released under the **MIT License** (`LICENSE`).
- Generated, do not edit by hand: `ComfyUI Model Manager.exe`, `ComfyUI Model Manager.exe.config`, `selftest.log`, `testdata\`.
