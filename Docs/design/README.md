# Knowledge Graph Builder — UI design

High-fidelity mockups for the Workspace, Settings and system states, built on Radzen Blazor 12 (Material theme, compact density). Every screen is shown in light and dark.

- Live canvas (pan, zoom, comments): https://claude.ai/artifact/HA3kBC1UkJPbUZEZbsKwxq
- Source brief: [../Design-Prompt.txt](../Design-Prompt.txt)

The `.dc.html` files are the canvas's artboards. They depend on the canvas runtime (`support.js`), so open them through the link above rather than straight from disk. `kgb.css` holds the shared mockup styles and the light/dark color tokens; it is the starting point for the app's `app.css` overrides.

## Files

| File | Screen |
|---|---|
| `Main.dc.html` | 1 · Design system: color tokens, typography, spacing, component styles |
| `Workspace.dc.html` | 2 · Desktop workspace: page open, chat streaming |
| `Explorer.dc.html` | 2b · Explorer: context menu, drag and drop (valid and invalid), search results |
| `Editor.dc.html` | 2c · Editor: Preview, Invalid YAML, empty state, page no longer exists |
| `Approvals.dc.html` | 3 · Workspace with two pending approval cards |
| `ApprovalStates.dc.html` | 3b · All approval card states, full-screen diff, Pages changed note |
| `Narrow.dc.html` | 4 · Narrow tabbed layout (about 400 px) |
| `Dialogs.dc.html` | 5 · New page, New folder, Rename validation, Move to |
| `Dialogs2.dc.html` | 5b · Delete, conflict, unsaved changes, rename with link updates |
| `Settings.dc.html` | 6 · Settings, desktop host |
| `SettingsBrowser.dc.html` | 6b · Settings, browser host (read-only) |
| `States.dc.html` | 7 · Toasts, chat errors, no API key, skeletons, field states, Not Found |
| `FirstRun.dc.html` | 7b · First run with the seeded Welcome page |
| `canvas.json` | Canvas layout and the per-screen annotation notes |
| `kgb.css` | Shared styles and tokens |

## Design system

- **Base:** Radzen Material / Material Dark, compact. Tokens override the `--rz-*` variables; `--kgb-*` tokens are app additions. Values are in `Main.dc.html` and in the `.light` / `.dark` blocks of `kgb.css`.
- **Fonts:** Roboto for the UI; JetBrains Mono for YAML, paths, diffs, tool rows and keyboard hints.
- **Custom CSS on top of Radzen:** wiki link, unresolved link (dashed underline, warning color, `link_off` icon), citation chip, tool row, unified diff with a +/− sign column, unsaved dot, and icon + word status badges.
- **Accessibility:** text at least 4.5:1 in both themes; 2 px primary focus ring with a 2 px offset; status never by color alone.

## Keyboard

Ctrl+S save · F2 rename · Delete delete (tree) · Enter send · Shift+Enter new line · Esc closes menus and dialogs, and stops a stream when focus is in chat.

## Notes per screen

### Workspace, desktop (900 px and wider)
- Shell: `RadzenLayout` > `RadzenHeader` with `RadzenIcon`, `RadzenText`, a root chip, a `RadzenLink` to /settings and `RadzenAppearanceToggle`. No sidebar.
- Panes: `RadzenSplitter` with three panes: Explorer 272 px (min 200), Editor flexible, Chat 420 px (min 320).
- Explorer: icon-only `RadzenButton`s, `RadzenTextBox` search, `RadzenTree` with lazy loading and an item template (icon, name, unsaved dot); `RadzenContextMenu` via `ContextMenuService`.
- Editor: title + `RadzenBreadCrumb`, Save / Revert, `RadzenSelectBar` Design | Source | Preview, Properties in a collapsible `RadzenFieldset` with a monospace `RadzenTextArea`, `RadzenMarkdownEditor`, Links panel with `RadzenBadge` counts.
- Chat: provider/model chip, New chat, message list, `RadzenTextArea` composer; Send becomes Stop while streaming.
- Focus order: header → Explorer toolbar → search → tree (one tab stop, arrow keys inside) → editor header → Properties → editor body → Links panel → chat header → messages → composer → Send/Stop.

### Explorer states
- Context menu: Open · New page here · New folder here · Rename (F2) · Move to… · Delete (Del). Opens on right-click, Shift+F10 or the Menu key; Esc returns focus to the node.
- Drag and drop: HTML5 drag events on the tree item template. Valid target: dashed primary outline + `drive_file_move` icon. Invalid (folder into itself or a descendant): dashed danger outline, `block` icon, tooltip with the reason. Keyboard alternative: Move to….
- Search: 200 ms debounce; results replace the tree with title, path and a ~160-character snippet with highlights. Esc clears and restores the tree.

### Editor states
- Preview: read-only rendered page; wiki links open pages, unresolved links open the New page dialog prefilled.
- Invalid YAML: warning badge + line message, the line is highlighted, Save stays enabled.
- Empty state: New page / New folder and a shortcut list.
- Page no longer exists: warning alert, the buffer stays editable, Save as new page and Close.

### Approval cards
- When the model calls a write tool, the stream pauses and one card per change appears (`RadzenCard`, outlined).
- Header: action badge (Create page / Update page / Create folder), monospace path, state badge, full-screen button (`DialogService`).
- Diff: CSS grid (old no. / new no. / sign / text). Unchanged runs over 3 lines fold. New pages are all + lines; long ones show 12 lines then "Show all".
- Footer: Reject (outlined), then Apply (primary). With two or more cards, a sticky bar shows "N of M decided", a progress bar, Reject all and Apply all.
- Stop turns pending cards into Cancelled; sending a message turns them into Superseded (the composer warns first).
- After apply: the tree refreshes without moving selection, and the chat adds a "Pages changed" note.

### Card states (icon + word)
Pending (schedule) · Approved (check, "waiting for 1 more decision", Undo) · Applied (check_circle, Open page) · Rejected (block) · Not applied, page changed (warning; Open current page, Ask again) · Cancelled (cancel) · Superseded (redo). Full-screen diff: `RadzenDialog` 90vw × 85vh with the same Reject / Apply footer.

### Narrow (under 900 px)
- `RadzenTabs` (client render mode) replaces the splitter: Explorer, Editor (unsaved dot), Chat (pending-approval badge).
- Opening a page or citation switches to Editor. Tabs and primary actions are 44 px tall. The sticky approval bar stacks its buttons.

### Dialogs
- Name dialogs: `RadzenTemplateForm` + autofocused `RadzenTextBox`; validates invalid characters, trailing dot or space, reserved names (CON, PRN, AUX, NUL, COM1–9, LPT1–9), duplicates (case-insensitive) and length. Primary is disabled while invalid. The length limit is still a placeholder (`[N]/[MAX]`).
- Move to: folder-only tree; the current folder is disabled, and so are a moved folder and its descendants.
- Delete: counts from a dry-run scan; the permanent variant needs the name typed before Delete permanently enables.
- Conflict: Reload (default), Overwrite, Cancel. Unsaved: Save (default), Discard, Cancel; the window-close variant lists every unsaved page.
- Rename/move with link updates: progress dialog, then success or a warning that lists the pages that failed.
- Initial focus is the default button, except on destructive dialogs, where Cancel gets focus.

### Settings
- Centered stack (max-width 760) of `RadzenCard`s: Knowledge root (validation: drive roots refused, warning above 10,000 pages), AI provider (Endpoint only for Azure OpenAI and Ollama; key shown only as Configured / Not set with Replace), Graph (stats and Rebuild with progress), Appearance.
- Browser host: knowledge root shows only the folder name; the AI provider section is read-only (ReadOnly, not Disabled, so it stays in the tab order).

### Notifications and system states
- Toasts via `NotificationService`: success and info close after 4 s; warnings and errors stay until dismissed and can carry Retry.
- Chat errors are transcript items; retryable ones get Retry.
- No API key: inline card linking to Settings; the composer is disabled.
- Skeletons are a light CSS shimmer. Not Found links back to the Workspace.
