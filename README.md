# ComfyUI Model Manager

A small Windows program that keeps track of installed AI models:

1. Scans a **models folder** (recursively) for `*.safetensors` and `*.gguf`.
2. Scans a **workflows folder** (recursively) for `*.json`.
3. Searches every model file name in all workflow files and flags the model as **Used** or **Unused**.
4. Also scans the **archive folder**: archived files that are used are flagged **Archived**, unused archive files are not listed.
5. You can **archive** unused files with their folder structure preserved and **restore** archived files that are used – per file, either with its folder structure or into a chosen folder.
6. Flags **duplicates** in the archive (same relative path and size as a model) and can delete the archive copy.

## Download

Grab the latest build from the [Releases page](../../releases/latest), unzip it and run `ComfyUI Model Manager.exe`.

Keep the `lang` folder and `ComfyUI Model Manager.exe.config` next to the exe – the config file enables long file paths, and the `lang` folder provides the translations. Nothing else needs to be installed: .NET Framework 4.8 is part of Windows 10 and 11.

## Requirements

- Windows 10 or 11 (the program uses .NET Framework 4.8, which is already part of Windows).
- Nothing to install, no Python, no Node.

## Building

Run `build.cmd`. It uses the `csc.exe` that ships with Windows and creates:

```
ComfyUI Model Manager.exe
ComfyUI Model Manager.exe.config
```

## Usage

1. **Browse** to the models folder, workflows folder and archive folder. The choices are saved immediately in `ComfyUI Model Manager.ini` next to the exe and reused next time.
2. Click **Scan**.
3. The list shows file name, relative path, size, status (**Used**, **Unused**, **Archived** or **Duplicate**) and number of matches.
   - The **Show** filter and **Search** narrow the list.
   - Select a row to see under **Used by** which workflow files reference the model. Double-click opens the workflow file.
   - Double-click a row (or right-click → *Open model folder*) to show the file in Explorer.
4. Check the rows you want to archive, restore or delete – or use the **checkbox in the column header** to select all **visible** rows in the filtered list. Uncheck a row to keep it.
5. Click **Archive (n)** to move the selected unused models. The folders are rescanned automatically afterwards.
6. Click **Restore (m)** to move selected archived files back. You then choose the target for each file.
7. Click **Delete (n)** to permanently remove selected duplicates from the archive. The model copy stays.

The status bar at the bottom shows the count and size of **used**, **unused** and **duplicates**.

## Languages

- **English is built into the program.** On first start a language dialog appears where you pick a language; the choice is saved as `Language=` under `[General]` in `ComfyUI Model Manager.ini`.
- The bundled languages are external files in `lang\`: **Svenska**, **Deutsch**, **Français**, **Español**, **Italiano**.
- Change language at any time via **?** → select a language → **OK**. The program asks whether it should restart; if you choose no, the new language is used the next time you start.
- **Your own translation:** copy `lang\_template.ini` to `lang\xx.ini`, set `name=` and `culture=`, and translate the values. Keep placeholders such as `{0}` and `{1}`, use `\n` for line breaks and save the file as UTF-8. Files starting with `_` and `en.ini` are ignored.
- Missing keys fall back to English, so an incomplete translation still works.
- **Open language folder** in the dialog opens `lang` so you can add or edit files. New files appear the next time the dialog is opened.
- If the selected language is an external file that has been removed, the language dialog appears again on start.

**The "?" button** at the far right of the toolbar (or **F1**) shows the program icon, name, version, author, the license (click **License: MIT** to read the full text) and the language selection.

## How matching works

- The workflow is parsed as JSON when possible. Only **active** nodes count: nodes with `mode` 2 (muted) or 4 (bypassed) are ignored, as are `Note`/`MarkdownNote`.
- **Subgraphs** are followed only through active instances. A promoted widget value on the instance counts; a node whose widget is linked to an input counts only if the link actually supplies it with a value (otherwise the node's stored default value is kept).
- If the file cannot be parsed – API format, older group nodes or broken JSON – the whole text is used as before. There, a bypassed reference can still count as used.
- The same rules apply to files in the archive folder. Only archived files that are used or are duplicates are shown.
- Case-insensitive (`A.SafeTensors` matches `a.safetensors`).
- The file name must appear as a name in its own right: `dup.safetensors` does not match `useddup.safetensors`.
- Works with `\` and `/` in paths, URL-encoded names (`my%20model.safetensors`), JSON escapes (`r\u00e4v.safetensors`) and different Unicode normalization.
- Files that cannot be read are skipped and reported in a warning dialog.
- Shortcuts/junction folders are not followed (avoids infinite loops).

## Archiving

- Files are moved to `<archive>\<relative path from the models folder>`.
  Example: `models\flux\lora\x.safetensors` → `archive\flux\lora\x.safetensors`.
- Folders are created automatically.
- If the target already exists, the file is renamed to `name (1).safetensors` – nothing is overwritten.
- If the archive is on another drive, the files are copied first (this can take time for large models).
- Locked files (in use by another program) are skipped and listed in the result.
- The archive folder must not be inside the models folder (or vice versa), because archived files would then be scanned again.

## Restoring archived files

- Only archived files that are **used** by a workflow are shown (status **Archived**), so you can see right away if a model is missing.
- Select one or more and click **Restore (m)**. For **each file** you choose:
  - **Same folder structure as in the archive** – `archive\flux\lora\x.safetensors` → `models\flux\lora\x.safetensors`.
  - **To selected folder** – the file is placed directly in the chosen folder (the file name decides). The folder must be under the models folder.
- The dialog preview shows the exact target path. If the target already exists, **Restore** is disabled – choose another folder or **Skip**.
- **Cancel** cancels the whole restore; no files are moved.
- Nothing is ever overwritten. Afterwards the folders are rescanned automatically.

## Duplicates

- An archive file counts as a **duplicate** if it has the same relative path **and** exactly the same size as a file in the models folder. The same file name in a different folder does not count.
- Only the **archive copy** is flagged (status **Duplicate**) and can be deleted. The model copy is never touched and is the one the workflows use.
- Archived duplicates are shown even if they are not used, so they can be cleaned up.
- Cleaning up: filter on **Duplicates**, tick the **header checkbox** (selects all visible duplicates), untick the ones you want to keep and click **Delete (n)**. Path and size are verified again immediately before each deletion; if they do not match, the file is skipped.
- The deletion is **permanent** (no Recycle Bin) and cannot be undone. The program compares name and size, not content – the same size is no guarantee of identical content.
- Afterwards the folders are rescanned automatically.

## Files

| File | Description |
| --- | --- |
| `ComfyUI Model Manager.ini` | Your folder choices. Created automatically. |
| `selftest.log` | Result of the self test. |
| `testdata\` | Created and deleted by the self test. |
| `src\app.ico` | The program icon embedded in the exe. |
| `lang\_template.ini` | English source/translation template (embedded in the exe). |
| `lang\sv.ini` | Svenska. |
| `lang\de.ini` | Deutsch. |
| `lang\fr.ini` | Français. |
| `lang\es.ini` | Español. |
| `lang\it.ini` | Italiano. |
| `LICENSE` | The MIT license text (also embedded in the exe). |

## Self test

```
"ComfyUI Model Manager.exe" --selftest
```

The test builds a fictional structure under `testdata\` (models, archive, workflows, collisions, duplicates, Swedish characters), runs scanning, archiving, restoring and duplicate deletion and verifies the result. It also checks the language loader, the license resource and that the bundled language files load. Your real models are never touched. The result is written to `selftest.log`; the exit code is `0` if everything passed.

## Known limitations

- Two models with the **same file name** in different subfolders cannot be told apart by the workflow text – both are flagged as used. The same applies if the name exists both in the models folder and in the archive: both are shown as used. This is the safe side: an unused file may be left behind, but a used file is never archived by mistake.
- **API-exported** workflows and older group nodes contain no `mode` information. There the program falls back to text matching, so a bypassed reference can still count as used (the safe side).
- When restoring, files are skipped if the target already exists. The existing file is never overwritten, and an archived file only gets a new name when it is archived.
- Duplicates are identified by relative path + size, not by content. The deletion is permanent, but the model copy in the models folder always remains.
- There is no general undo function. Archived files that are **used** or are **duplicates** are shown; an unused archived file that is neither used nor a duplicate is hidden and must be handled manually.
- Only the extensions `.safetensors`, `.gguf` and `.json` are handled.
- Matching is based on file names, not on the content of the model files.

## License

Released under the **MIT License**. You may use, modify and redistribute the code freely – including commercially – as long as the copyright notice and the license text are kept. The software is provided "as is", without any warranty of any kind, and the author takes no responsibility for any damage it might cause.

The full text is in [LICENSE](LICENSE) and is also available inside the program: **?** → **License: MIT**.
