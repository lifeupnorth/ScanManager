# Changelog

What's new in each release. Add a section for the next version before pushing its tag: the release workflow copies it to the top of the GitHub release notes, as "What's new". Preview them with `.\.github\release-notes.ps1 -Version <version>`.

## 0.2.4

- Canceling a feeder scan keeps the page that was already on its way, then stops. The status says "Finishing the current page…" and then "Stopping the scanner…" while it does. Some scanners still feed the remaining sheets through.
- Opening a second ScanManager window no longer deletes the pages of one left open for more than two days.
- Saving several pages as PNG or JPEG asks before replacing existing files with the same numbered names (name_001.png, …).
- Saving as TIFF and printing use far less memory: a 30-page color document went from over 800 MB to under 250 MB.
- A network scanner that stops answering is reported as unreachable, instead of looking like a canceled scan. Errors from a busy scanner also appear within seconds.
- If a page can't be processed (a damaged image, or a full disk), the scan stops with a clear message, and the pages scanned so far can still be undone. Damaged image files are named when you add images.
- Screen readers now announce the name of every setting and read pages in the page list as "Page 1", "Page 2", ….
- Dragging inside a scan frame that covers the whole glass draws a new frame.
- Blank-page skipping no longer keeps an empty page because of a speck of dust near its right or bottom edge.

## 0.2.3

- ScanManager is now open source, under the MIT License. The installer shows the license before installing and puts a copy, `LICENSE.txt`, next to the app.

## 0.2.2

- The app icon is now the same size as other apps' icons: its tile fills the whole frame.

## 0.2.1

- The app icon now fills its frame, so it's no longer smaller than other apps' icons in the taskbar, Start menu and on the desktop.

## 0.2.0

- No changes to the app itself. The release build can now code-sign the installers, which stays off until a certificate is configured.

## 0.1.0

- First release, with installers for x64 and ARM64 Windows PCs. Scan from the flatbed or the document feeder, single- or double-sided, including network scanners over eSCL; choose the scan area on a preview; rotate, crop, delete and reorder pages with undo; save as PDF, TIFF, PNG or JPEG, or print. A default scan source can be set in Settings.
