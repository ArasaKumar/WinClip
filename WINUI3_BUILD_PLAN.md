# Clippet — C# / WinUI 3 Build Plan

> **Audience:** an implementing agent (or developer) building Clippet as a brand-new
> **C# + WinUI 3 (Windows App SDK)** desktop app.
>
> This document is self-contained. It encodes *what* the app does and *how* to build
> each piece on a modern WinUI 3 stack. This is a **greenfield build** — every choice
> here is made because it's the best way to build the app today, not to mirror any prior
> implementation. A reference Rust + Win32 prototype exists in `src/*.rs` with per-feature
> notes in `docs/level-*.md`; treat it as *inspiration and a worked example* of the hard
> Win32 details (DIB handling, the suppress/paste handshake, capture priority), not as a
> spec you must reproduce byte-for-byte. Everything needed to implement is captured below.

---

## 0. Design principles — what we're building and why

Clippet is a fast, native, tray-resident clipboard-history manager for Windows 11. Build it
around four principles, in priority order:

- **Responsive by design.** The single most important architectural rule: **the UI thread
  does UI only.** Every clipboard capture decode, DIB↔PNG conversion, thumbnail encode, disk
  I/O, history serialization, and fuzzy filter runs off the UI thread via `Task.Run` /
  `async` I/O, with results marshaled back through `DispatcherQueue`. Search is debounced.
  Writes are serialized through a single async writer. Build this in from M0 — see §4. A
  naively synchronous clipboard manager stalls the moment someone copies a 20-megapixel
  screenshot; ours must not.
- **Native Win11 chrome, for free where possible.** WinUI 3 + Windows App SDK gives us
  acrylic backdrops, rounded corners, custom title bars, immersive dark mode, and DPI
  scaling natively — features a raw-Win32 app must hand-paint. Lean on the framework; drop
  to P/Invoke only where WinRT genuinely doesn't reach (hotkeys, low-level clipboard format
  enumeration, `SendInput`). See §5, §6.
- **Single artifact, drop-it-anywhere distribution.** Ship **unpackaged, self-contained** so
  the app stays a zip-and-run folder with no installer, no admin, no machine-wide runtime
  dependency. See §1. (MSIX/Store packaging can be layered on later without code changes.)
- **Idiomatic, maintainable C#.** Use modern C# 14 / .NET 10 idioms — `async`/`await`
  end-to-end, `System.Text.Json` source generators, `field`-backed properties, collection
  expressions, nullable reference types, records for immutable data. Favor clarity over
  cleverness.

**Accepted trade-off — footprint.** A self-contained WinUI 3 + .NET app is tens of MB on
disk with a larger resident set than a hand-rolled native binary. This is the deliberate
cost of getting native chrome, a managed concurrency model, and fast development. Mitigate
with `PublishReadyToRun` and (carefully) `PublishTrimmed` — see §1, §18 — but don't expect a
tiny footprint, and call the size out to users rather than hiding it.

**Non-goals:** minimizing binary/memory footprint to native levels; shipping a single tiny
`.exe`. **Goals:** all seven feature areas (§2) working; a responsive, non-blocking UI;
native Win11 chrome; a clean, forward-compatible on-disk format.

---

## 1. Target stack & project setup

| Concern | Choice | Notes |
|---|---|---|
| Language | **C# 14** | Ships with the .NET 10 SDK. Use `field` keyword, collection expressions, extension members, nullable reference types. |
| Runtime | **.NET 10 (LTS)** | Current LTS, supported to **Nov 10, 2028**. TFM: `net10.0-windows10.0.19041.0`, min `10.0.17763.0`. |
| UI | WinUI 3 via **Windows App SDK 2.1** | `Microsoft.WindowsAppSDK` NuGet, latest stable **2.1.3** (the 2.x line shipped at Build 2026). Decoupled from the OS, shipped via NuGet. |
| Packaging | **Unpackaged, self-contained** | Zip-and-run folder, no installer. `WindowsAppSDKSelfContained=true`, `SelfContained=true`, `WindowsPackageType=None`. |
| P/Invoke | **Hand-written `DllImport`** (`Services/Native.cs`) | Implemented directly rather than via CsWin32 — the generated wrapper types/namespaces are error-prone to target blind, and the documented struct layouts (INPUT, NOTIFYICONDATA, DROPFILES, BITMAPINFOHEADER) are stable to hand-write. CsWin32 remains a valid alternative. |
| Tray icon | **Hand-rolled `Shell_NotifyIcon`** (`Services/TrayService.cs`) | Avoids a third-party dependency against the brand-new 2.x line; menu via `TrackPopupMenu`. (H.NotifyIcon.WinUI is the library alternative.) |
| Imaging | `Windows.Graphics.Imaging` (`BitmapDecoder` / `BitmapEncoder`) | WinRT, in-box. For DIB↔PNG and thumbnails. |
| JSON | `System.Text.Json` | Source-generated `JsonSerializerContext` for speed and AOT/trim-friendliness. |
| Fuzzy match | **A skim-style matcher** (`FuzzyMatcher.cs`, ~150 lines) | See §11. Off-the-shelf NuGets (FuzzySharp) use different scoring and don't return the matched-index set we need for highlighting. |

> **Version pinning:** pin the exact `Microsoft.WindowsAppSDK` and `Microsoft.Windows.CsWin32`
> versions in `Directory.Packages.props` (Central Package Management) so CI and dev builds
> agree. Take the latest stable of each at project start and bump deliberately.

**Why unpackaged:** the product ethos is "single artifact, drop it anywhere, no installer, no
admin." Unpackaged self-contained deployment keeps that closest — the output is a folder you
can zip, needing no MSIX install and no machine-wide runtime. If MSIX/Store distribution is
later desired, the code does not change — only the packaging project is added.

**Project layout:**

```
Clippet.sln
Directory.Packages.props             # central NuGet version pins
Directory.Build.props                # shared TFM, nullable, langversion
src-cs/
  Clippet/
    Clippet.csproj
    App.xaml / App.xaml.cs            # app bootstrap, single-instance guard
    NativeMethods.txt                # CsWin32 symbol list
    Models/
      ClipItem.cs                    # data model
      ItemType.cs                    # enum + tag()/tag color
      Settings.cs                    # settings.json schema
      Palette.cs                     # light/dark color tables
    Services/
      ClipboardMonitor.cs            # AddClipboardFormatListener + capture pipeline
      ClipboardWriter.cs             # re-publish item + paste synthesis (SendInput)
      HotkeyService.cs               # RegisterHotKey + WM_HOTKEY routing
      Storage.cs                     # history.json / settings.json / media / autostart
      ImageCodec.cs                  # DIB <-> PNG, thumbnails
      FuzzyMatcher.cs                # skim-style matcher w/ match indices
      SearchEngine.cs                # filter + sort + FilteredRow mapping
      TrayService.cs                 # tray icon + menu
      ThemeService.cs                # system theme detect + override + backdrop
      Win32Interop.cs                # HWND helpers, message-window subclassing
    Views/
      PopupWindow.xaml / .cs         # the main popup
      Controls/ClipRow.xaml          # row template
      AboutDialog / SettingsDialog
    Assets/clippet.svg + .ico
```

---

## 2. Feature specification (the whole app in one page)

Clippet is a tray-resident clipboard-history manager. It:

1. **Listens** to every system clipboard change and captures the *most informative* format
   available, building a history list (newest first, 200-item cap, pins exempt).
2. **Summons** a borderless Win11-styled popup at the cursor on **Ctrl+Shift+V**.
3. **Searches** the history with live fuzzy matching, highlighting matched characters.
4. **Pastes** a chosen item back into whatever window was focused before the popup opened,
   by re-publishing it to the clipboard and synthesizing Ctrl+V.
5. **Persists** everything to `%APPDATA%\Clippet\` (JSON + on-disk PNGs for images).
6. **Pins** items (sort to top, survive the cap), with a per-row context menu.
7. Lives in the **system tray** with optional autostart.

**Capture priority (highest wins):** Files → Spreadsheet → RTF → HTML → Image →
UnicodeText → AnsiText.

**Item types & tags:** `[T]` Text, `[R]` RichText, `[I]` Image, `[F]` File, `[H]` HTML,
`[X]` Spreadsheet, `[C:lang]` Code.

**Hotkey:** Ctrl+Shift+V (Win+V is unavailable — the Win11 shell holds it even when
clipboard history is disabled).

---

## 3. Data model & on-disk format

Design a clean, **versioned** JSON format. Start at **version 1** — this is a new app with
no legacy data to honor, so there is no migration burden. The schema below is deliberately
simple, forward-compatible (unknown fields ignored, absent fields default), and stores image
bytes as on-disk PNGs rather than inline base64 to keep the JSON small.

> **Isolation:** the app stores under **`%APPDATA%\ClippetNext\`** (not `…\Clippet\`) and uses
> the autostart Run value **`ClippetNext`**, so it runs fully independent of any original
> Clippet install and never reads or overwrites that app's data. The loader also backs up any
> unrecognized-version `history.json` to `history.unsupported.bak` before doing anything, as a
> safety net.

### `%APPDATA%\ClippetNext\` layout

| Path | Purpose |
|---|---|
| `history.json` | History metadata (200-item cap; pins exempt) |
| `settings.json` | Popup size, autostart state, theme override |
| `media/{id}.png` | Full-resolution image bytes |
| `media/{id}_thumb.png` | Listbox thumbnail (longest edge ≤ 96 px) |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` → value `Clippet` | Autostart (opt-in only) |

`%APPDATA%` = `Environment.GetFolderPath(SpecialFolder.ApplicationData)` (roaming).

### `history.json` schema (version 1)

```jsonc
{
  "version": 1,
  "items": [
    {
      "id": 123,                    // ulong, globally unique, monotonic
      "type": "text",              // text|richtext|image|file|html|spreadsheet|code
      "content_b64": "…",          // base64 of raw payload; OMITTED/empty for images
      "preview": "…",              // display string (cleaned, ~80 chars)
      "ts": 1717000000,             // ulong unix seconds
      "pinned": false,              // default false
      "lang": "rs",                // optional; Code items only (file ext, lowercase)
      "content_hash": 12345678901,  // ulong FNV-1a/64 of source bytes; default 0
      "media_file": "123.png",     // optional; images only
      "thumb_file": "123_thumb.png",// optional; images only
      "media_w": 1280,              // optional uint; images only
      "media_h": 720                // optional uint; images only
    }
  ]
}
```

**JSON conventions:**
- The C# `Kind` property serializes as `"type"` (`[JsonPropertyName("type")]`).
- `type` enum values are **lowercase** strings: `text, richtext, image, file, html,
  spreadsheet, code` (use a `JsonStringEnumConverter` with a lowercase naming policy).
- `pinned` and `content_hash` default when absent.
- `content_b64` omitted when empty; optional fields omitted when null
  (`JsonIgnoreCondition.WhenWritingNull` / custom for empty arrays).
- Serialize **indented** for readability/diff-ability.
- Use a **source-generated** `JsonSerializerContext` for all (de)serialization.

### `ClipItem` (C#)

```csharp
public sealed class ClipItem {
    public ulong Id;
    public ItemType Kind;            // -> "type"
    public byte[] Raw = [];          // -> content_b64 (empty for images)
    public string Preview = "";
    public ulong Timestamp;          // -> "ts"
    public bool Pinned;
    public string? Lang;
    public ulong ContentHash;        // FNV-1a/64
    public string? MediaFile;
    public string? ThumbFile;
    public uint? MediaW;
    public uint? MediaH;
}
```

### `settings.json` schema

```jsonc
{
  "autostart_prompted": false,   // default false
  "autostart_enabled": false,    // default false
  "popup_w": 400,                // optional int
  "popup_h": 500,                // optional int
  "theme_override": null         // null=follow system, true=force light, false=force dark
}
```

### FNV-1a/64 (content hash — drives dedup; persisted so it's stable across restarts)

```csharp
public static ulong Fnv1a64(ReadOnlySpan<byte> data) {
    ulong h = 0xcbf29ce484222325UL;
    foreach (byte b in data) { h ^= b; h *= 0x100000001b3UL; }
    return h;
}
```

Hash source bytes = PNG bytes for images, raw payload bytes otherwise. FNV-1a/64 is chosen
because it's tiny, fast, and dependency-free; collision risk over a 200-item cap is
negligible and dedup only compares against the most-recent item (§9).

---

## 4. Concurrency model (the responsiveness foundation)

This is the architectural backbone — design it first, not last. Rules:

- **UI thread does UI only.** All capture decoding, DIB↔PNG, thumbnail encode, disk read/
  write, and history serialization run on background threads via `Task.Run` / async I/O.
- Marshal results back with `DispatcherQueue.TryEnqueue`. Provide a small `UiDispatch` helper
  so services never touch UI types directly.
- **Debounce search** (~80–120 ms) so keystrokes don't trigger a full re-filter+re-render
  storm. Fuzzy filtering of ≤200 short strings is cheap, but rendering churn is not.
- **Serialize writes.** Funnel `history.json` persistence through a single async writer (a
  `Channel<SaveRequest>` consumed by one background loop) so concurrent captures don't race
  or write-storm. Writes stay atomic (`.tmp` + rename).
- **Image decode for thumbnails is async + cached.** Decode each `media/{id}_thumb.png` to a
  `BitmapImage` once, cache by item id, evict on delete/clear.
- The clipboard *capture* itself (`OpenClipboard` / `GetClipboardData`) must happen promptly
  on the listener callback while the clipboard is available — read the raw bytes on the
  message thread, then hand the bytes to a background task for decode/encode/persist.

---

## 5. Win32 interop you can't avoid

WinUI 3 / WinRT covers a lot, but these require P/Invoke (generate via CsWin32). List them
in `NativeMethods.txt`:

```
# Hotkey
RegisterHotKey
UnregisterHotKey
# Clipboard listener + raw access
AddClipboardFormatListener
RemoveClipboardFormatListener
OpenClipboard
CloseClipboard
EmptyClipboard
GetClipboardData
SetClipboardData
EnumClipboardFormats
RegisterClipboardFormat
GlobalLock
GlobalUnlock
GlobalAlloc
GlobalSize
# Paste synthesis
SendInput
SetForegroundWindow
GetForegroundWindow
GetWindowText
# Files (CF_HDROP)
DragQueryFile
# Window subclassing (to catch WM_HOTKEY / WM_CLIPBOARDUPDATE on a WinUI window)
SetWindowSubclass
DefSubclassProc
RemoveWindowSubclass
# DWM chrome
DwmSetWindowAttribute
# Tray (if not using H.NotifyIcon)
Shell_NotifyIcon
# Misc
GetCursorPos
MonitorFromPoint
GetMonitorInfo
```

**Getting an HWND from a WinUI 3 window:**
```csharp
var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(myWindow);
```

**Receiving `WM_HOTKEY` / `WM_CLIPBOARDUPDATE`:** WinUI 3 windows don't expose a WndProc.
Two options:
1. **Subclass the popup's HWND** with `SetWindowSubclass` and handle the messages in the
   subclass proc. Simplest; the popup HWND is stable for the app lifetime (it's hidden,
   not destroyed).
2. **Create a dedicated message-only window** (`HWND_MESSAGE` parent) on a helper thread to
   own the clipboard listener + hotkey, posting events to the UI via `DispatcherQueue`.
   Cleaner separation; more code.

Recommended: **option 1** (subclass the popup window) for a single-window design. Register
the hotkey and `AddClipboardFormatListener` against that HWND in `OnLoaded`.

---

## 6. Window chrome & theming

WinUI 3 gives natively what a raw-Win32 app must hand-paint:

| Need | WinUI 3 mechanism |
|---|---|
| Acrylic backdrop | `DesktopAcrylicBackdrop` (set `Window.SystemBackdrop`) |
| Rounded corners | Default on Win11; or `DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND)` |
| Custom title bar | `AppWindow.TitleBar.ExtendsContentIntoTitleBar = true` + a custom XAML title bar region; call `SetTitleBar()` for the drag region |
| No taskbar / Alt-Tab entry | `OverlappedPresenter` with `IsShownInSwitchers=false`; set ex-style `WS_EX_TOOLWINDOW` via P/Invoke if needed |
| Resizable window + native resize hit-test | `OverlappedPresenter` with `IsResizable=true` |
| Win11-style close button | XAML `Button` styled like the Win11 caption close (transparent → `#C42B1C` hover, white glyph), or the system caption buttons |
| Light/dark follow-system | `Application.RequestedTheme` + listen for system theme changes; read `AppsUseLightTheme` registry value for the "follow system" mode |

### Theme behavior

- **Three states**, persisted as `settings.theme_override`: `null` = follow system,
  `true` = force light, `false` = force dark.
- On startup, resolve effective theme: override if set, else system `AppsUseLightTheme`
  (`HKCU\…\Themes\Personalize\AppsUseLightTheme`, DWORD 0=dark/1=light).
- Apply via `FrameworkElement.RequestedTheme` on the root + matching `SystemBackdrop` and
  `DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE)`.
- The footer theme-toggle **flips** dark↔light, writes the override to settings, and
  re-themes live.

### Palette (use these exact colors)

Both palettes carry per-format tag colors. Colors below are standard `#RRGGBB`.

**Dark:** bg `#202020`, row_sel `#3C3C3C`, text `#F0F0F0`, subtext `#A0A0A0`,
accent(pin) gold `#FACC15`, pin_dim `#606060`, search_bg `#333333`.
Tags: text `#CBD5E1`, rich `#FCA5A5`, image `#86EFAC`, file `#FCD34D`, html `#C4B5FD`,
sheet `#5EEAD4`, code `#93C5FD`.

**Light:** bg `#FAFAFA`, row_sel `#E5E5E5`, text `#111111`, subtext `#606060`,
accent(pin) amber, pin_dim `#B0B0B0`, search_bg `#F1F1F1`.
Tags: text `#475569`, rich `#DC2626`, image `#16A34A`, file `#CA8A04`, html `#7C3AED`,
sheet `#0D9488`, code `#2563EB`.

Close-button hover: fill `#C42B1C`, glyph `#FFFFFF`.

> Express these as `ThemeResource` / `ResourceDictionary` brushes keyed by theme so the
> light/dark swap is automatic.

### Geometry constants

Popup default 400×500, min 280×280. Title bar 48px. Footer 44px. Search box 40px tall,
12px inset, 8px corner radius, magnifier icon 14px. Text row 34px; **image row 102px**
(3× text) with a **68px** thumbnail. Pin column 22px. Express these as XAML sizes /
`ListView` item-template dimensions rather than pixel math — but keep the visual proportions.

---

## 7. The popup UI

Build the popup as a XAML window. Use a virtualized **`ListView`** bound to the filtered
rows. Each row is a `DataTemplate`:

```
[Tag pill]  [thumbnail (image rows only)]  [Preview w/ bold match runs]   [relative time]  [★/☆]
```

Row template requirements:
- **Tag**: small colored label, text = `[T]/[R]/[I]/[F]/[H]/[X]` or `[C:lang]`. Color =
  per-type tag color from the active palette. Tag color stays constant even when the row is
  selected.
- **Preview with match highlighting**: render the preview as a `TextBlock` with multiple
  `Run`s — matched character ranges (from the fuzzy matcher's byte indices, converted to
  char indices) in **bold**. Build the `Run` list in a value converter / code-behind from
  `FilteredRow.Indices`.
- **Relative time**: right-aligned, subtext color. Format rules (§9).
- **Pin glyph**: ★ (U+2605, pinned, accent/gold) / ☆ (U+2606, unpinned, dim). Clicking the
  star toggles pin **without** selecting/pasting. Give it its own `Button` / hit area
  (~22px) so the click is unambiguous.
- **Image rows**: show the cached thumbnail `BitmapImage` (decoded from
  `media/{id}_thumb.png`), ~68px, with a placeholder rectangle (image tag color) if the file
  is missing/undecodable. Taller row (102px).
- **Selection**: subtle `row_sel` background, no focus dotted rectangle (Win11 style).

**Search box**: a `TextBox` (or `AutoSuggestBox` styled down) at the top with a magnifier
glyph and placeholder **"Search clipboard…"**. `TextChanged` → debounce → re-filter.

**Per-row context menu** (`MenuFlyout` on right-click / `ContextRequested`):
1. **Paste** → activate & paste (same as Enter)
2. **Pin / Unpin** → label reflects state
3. **Copy to clipboard** → re-publish to clipboard, no paste
4. — separator —
5. **Delete this item** → remove from history, delete media, re-save, drop thumb cache

**Footer**: a right-aligned row of 5 flat icon buttons with tooltips and per-action accent
colors, **in this order**:

| # | Button | Glyph (Segoe Fluent Icons) | Tooltip | Accent | Action |
|---|---|---|---|---|---|
| 0 | Theme toggle | sun/moon | "Switch to Light/Dark Mode" (dynamic) | accent | flip theme + persist |
| 1 | Clear history | trash | "Clear History" | rich/red | confirm → clear unpinned |
| 2 | Settings | gear | "Settings" | subtext | settings dialog |
| 3 | About | info | "About" | code/blue | about dialog |
| 4 | Quit | power | "Quit" | file/amber | exit app |

> Use **Segoe Fluent Icons** `FontIcon` glyphs. Keep the order, tooltips, accent colors, and
> the dynamic theme tooltip.

---

## 8. Keyboard & interaction model

Handle these on the popup (via `KeyboardAccelerator`s and key handlers). Focus may be in the
search box or the list — most actions work from either.

| Key | Action |
|---|---|
| **Ctrl+Shift+V** (global) | Show popup at cursor (capture PREV_FG first), clear search, focus search box, select first row |
| **Esc** | Hide popup (never destroy) |
| **Enter** | Paste selected item into PREV_FG |
| **Tab** | Toggle focus between search box and list |
| **↑ / ↓** | Move list selection (works while typing in search) |
| **Ctrl+P** | Toggle pin on selected row (bare `P` must still type into search) |
| **Right-click row** | Context menu |
| **Double-click / click row + Enter** | Paste |
| Click ★/☆ | Toggle pin (no paste) |

**Show/hide semantics:** the popup is **hidden, never destroyed** (process stays
tray-resident). Hiding flushes popup size to settings. Do **not** auto-hide on focus loss
(the user may reposition the window); but on deactivation, **remember the new foreground
window** as the paste target (skipping windows owned by the app itself). Left-clicking the
tray icon toggles show/hide.

---

## 9. Capture pipeline (`ClipboardMonitor.cs`)

On `WM_CLIPBOARDUPDATE` (subclass proc):

1. If `SuppressNextUpdate` flag is set → clear it and **ignore** (this was our own paste).
2. Otherwise `OpenClipboard`, enumerate formats, pick the **most informative** by priority,
   read its raw bytes **promptly**, `CloseClipboard`. Then hand off to a background task for
   decode/preview/persist.

**Registered formats** (resolve once at startup via `RegisterClipboardFormat`):
`"Rich Text Format"`, `"HTML Format"`, `"XML Spreadsheet"`.

**Priority & readers:**

| Priority | Condition | Read as | Preview |
|---|---|---|---|
| 1 | `CF_HDROP` present | File list (UTF-16 paths) | `name.ext` or `name.ext + N more` |
| 2 | `XML Spreadsheet` present | Prefer parallel `CF_TEXT` (TSV); else raw XML | `R rows x C cols` |
| 3 | `Rich Text Format` present | raw RTF bytes | RTF→plain, 80 chars |
| 4 | `HTML Format` present | raw CF_HTML bytes | CF_HTML→plain, 80 chars |
| 5 | `CF_DIB` / `CF_BITMAP` present | DIB bytes → PNG on disk | `[Image WxH]` + thumbnail |
| 6 | `CF_UNICODETEXT` | UTF-16 text | classify text (§10), 80 chars |
| 7 | `CF_TEXT` | ANSI text | classify text, 80 chars |

**WinRT shortcut:** for *reading*, you may use `Clipboard.GetContent()` / `DataPackageView`,
which exposes `Text`, `Html`, `Rtf`, `Bitmap`, `StorageItems` — simpler than raw Win32 for
most formats. **But** the "most-informative-wins" priority and the spreadsheet TSV-vs-XML
nuance need the format *enumeration*, so keep raw `EnumClipboardFormats` to decide which
format to take, then read either via WinRT or raw. Validate that `DataPackageView` reliably
surfaces the registered `XML Spreadsheet` format; if not, read it raw.

**Dedup:** compare `(Kind, ContentHash)` against the **last** history item. If both equal,
discard (consecutive-duplicate suppression). Hash is over the source bytes (PNG bytes for
images) so it works even though images keep `Raw` empty.

**Then:** prepend to history, **prune** (§12), persist (async, atomic), refresh the list and
tray tooltip.

---

## 10. Code detection & text classification

When capturing plain text (Unicode or ANSI), classify as **Code** vs **Text** by the
**foreground window title** (`GetForegroundWindow` + `GetWindowText`, captured at copy time):

- Title contains any of (case-sensitive substring): `"Visual Studio Code"`, `"JetBrains"`,
  `"IntelliJ"`, `"PyCharm"`, `"Rider"`, `"GoLand"`, `"WebStorm"`, `"Notepad++"`,
  `"Microsoft Visual Studio"` → **Code**.
- For Code, extract a **language hint**: the **last** `.<ext>` in the title where ext is
  1–6 alphanumeric/underscore chars, lowercased (e.g. `main.rs - … VS Code` → `rs`,
  `foo.spec.ts - …` → `ts`). Preview = first non-empty line (≤80 chars).
- Otherwise **Text**, preview = cleaned first 80 chars.

**Preview helpers:**
- `TruncatePreview(s, max)`: replace `\r \n \t` with space, trim, cut to `max` chars,
  append `…` if cut.
- `FirstNonemptyLine(s, max)`: first non-blank line, truncated.

**HTML→plain (`CfHtmlToPlain`):** parse the CF_HTML header; use `StartFragment:` /
`EndFragment:` byte offsets if present (else skip to first `<`); strip tags (`<…>`); decode
entities `&amp; &lt; &gt; &quot; &apos; &nbsp;` and numeric `&#123;` / hex `&#x7B;`.

**RTF→plain (`RtfToPlain`):** strip control words; map `\par`→newline, `\tab`→tab; skip
non-text destination groups by brace-depth tracking (`\*`, `\fonttbl`, `\colortbl`,
`\stylesheet`, `\info`, `\pict`, `\object`, `\listtable`, `\listoverridetable`, `\rsidtbl`,
`\generator`, `\themedata`, `\datastore`, `\latentstyles`, `\xmlnstbl`, `\revtbl`); handle
`\uN` unicode escapes with `\ucN` skip count; keep literal text.

**Relative time:**
```
< 5s   -> "just now"
< 60s  -> "{n}s ago"
< 1h   -> "{n}m ago"
< 24h  -> "{n}h ago"
else   -> "{n}d ago"
```

---

## 11. Fuzzy search (`FuzzyMatcher.cs` + `SearchEngine.cs`)

Implement a **skim-style fuzzy matcher** returning `(int score, List<int> matchedIndices)?`.
The matched indices drive bold highlighting, so off-the-shelf token/Levenshtein matchers
won't do — a faithful skim-style implementation is ~150 lines. Scoring need not match any
reference byte-for-byte, but ranking and the *set of matched indices* should feel natural
(prefer consecutive matches, word-boundary bonuses, camelCase bonuses).

- Match against the item's **`preview`** only (not raw bytes).

**Filter/sort (`UpdateFilter`) → builds `List<FilteredRow>`** where
`FilteredRow { int HistIndex; List<int> Indices; }`:

- **Empty/whitespace query:** include all items; `Indices` empty. Sort by: **pinned first**,
  then **newest first** (higher id/index).
- **Non-empty query:** keep only items that match; sort by **pinned first**, then **highest
  score**, then **newest first**.
- The list never iterates history directly — the ListView is bound to `FilteredRow`s, and
  row→item is always `History[FilteredRow.HistIndex]`.
- After rebuild, **select row 0** if any rows exist.

Debounce the recompute (§4).

---

## 12. Storage, pruning, eviction (`Storage.cs`)

- **Atomic writes:** write to `path.tmp`, flush, then `File.Move(tmp, path, overwrite:true)`.
  Applies to `history.json`, `settings.json`, and every media file.
- **Load:**
  - Parse `history.json`; on parse error or unknown `version` → return empty history,
    next_id = 1.
  - **Drop image items whose `media_file` is missing on disk.**
  - **Sweep `media/`** of files not referenced by any surviving item (orphans from
    interrupted writes).
  - `next_id = max(loaded ids) + 1` (monotonic; never reuse).
- **Prune (cap = 200):** while `count > 200`, remove the **first unpinned** item and delete
  its media files. **Pinned items are never evicted**, even past the cap.
- **Media filenames:** `{id}.png` and `{id}_thumb.png`. Thumbnail longest edge ≤ **96 px**.
- **Delete item:** remove from history, delete its `media_file` + `thumb_file` (treat
  not-found as success), drop the cached thumbnail bitmap, re-save.
- **Clear history:** keep pinned, drop unpinned (delete their media), re-save, refresh tray
  tooltip. Confirm first.
- **Final flush** on app exit (so in-session pins/edits survive force-close).

---

## 13. Paste-back (`ClipboardWriter.cs`)

**Re-publish item to clipboard** (`SetClipboardFromItem`), format per type:

| Type | Clipboard format written |
|---|---|
| Text, Code | `CF_UNICODETEXT` (null-terminated UTF-16) |
| RichText | registered `Rich Text Format` (raw bytes) |
| Html | registered `HTML Format` (raw CF_HTML bytes) |
| Spreadsheet | `CF_TEXT` (TSV) — universal so Notepad/Excel/terminals accept |
| Image | `CF_DIB` — read `media/{id}.png` from disk; if PNG (signature check) convert PNG→DIB; else use raw DIB bytes |
| File | `CF_HDROP` — build `DROPFILES` struct + double-null-terminated UTF-16 path list, `fWide=1` |

**Sequence (`ActivateSelected`):**
1. Map selected ListView row → `FilteredRow.HistIndex` → history item.
2. Set `SuppressNextUpdate = true` **before** `SetClipboardData` (so our own
   `WM_CLIPBOARDUPDATE` is ignored).
3. `OpenClipboard` → `EmptyClipboard` → `SetClipboardData(format, handle)` → `CloseClipboard`.
4. `ShowWindow(SW_HIDE)` the popup.
5. `SetForegroundWindow(PREV_FG)` — the window that was focused before the popup opened.
6. **Synthesize Ctrl+V** via `SendInput`: 4 inputs — Ctrl down, V down, V up, Ctrl up.

**Copy-to-clipboard** (context menu): steps 2–3 only (publish, no hide/paste/SendInput).

**WinRT alternative for publishing:** `DataPackage` + `Clipboard.SetContent` can set Text,
Html, Rtf, Bitmap, StorageItems cleanly and may be simpler/more robust than raw
`SetClipboardData` for most types. If you use it, you must still set `SuppressNextUpdate`
first, and you still need raw Win32 for `CF_DIB` exactness and the spreadsheet-as-TSV
behavior if `DataPackage` doesn't reproduce them. **SendInput has no WinRT equivalent —
always P/Invoke.** Decide per-format; document the choice.

`PREV_FG` capture: on popup deactivation (`WM_ACTIVATE WA_INACTIVE` or WinUI `Deactivated`),
record `GetForegroundWindow()` if it isn't a window owned by the app. Also capture it at the
moment the hotkey fires, before showing the popup.

---

## 14. Tray icon & menu (`TrayService.cs`)

Use **H.NotifyIcon.WinUI** (or raw `Shell_NotifyIconW`):

- Icon from the embedded `clippet.ico` (rasterized from `assets/clippet.svg`). Embed the
  `.ico` as a resource / app icon; H.NotifyIcon takes an `ImageSource`.
- **Tooltip:** `"Clippet"`, or `"Clippet — N pinned"` when pinned count > 0. Update on every
  pin toggle / history mutation.
- **Left-click:** toggle popup (show at cursor / hide).
- **Right-click menu:**
  1. **Open Clippet** → show popup
  2. **Clear History** → confirm dialog → clear unpinned
  3. **Settings…** → settings dialog
  4. — separator —
  5. **Exit** → flush history, remove tray icon, quit
- No taskbar entry by default (tool-window style + presenter not in switchers).

**Popup positioning:** `GetCursorPos` → `MonitorFromPoint` → monitor work area → place popup
top-left at cursor, clamp so it never spills off the work area. Use persisted size (else
400×500). Set position via `AppWindow.Move` / `MoveAndResize` (note: AppWindow uses physical
pixels — handle DPI).

---

## 15. Autostart

- Registry: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value name **`Clippet`**,
  data = **quoted** absolute exe path (`"C:\path\to\Clippet.exe"`). Use the launcher exe path
  (`Environment.ProcessPath`).
- **First-launch prompt** (once): after the tray icon is visible, ask "Start Clippet
  automatically with Windows?" (Yes/No). On Yes, write the Run value. Persist
  `autostart_prompted = true` (and `autostart_enabled`) in `settings.json` so it never asks
  twice.
- The Settings dialog exposes a toggle that writes/removes the Run value (see §16).

---

## 16. Dialogs

- **About:** `ContentDialog` — app name + version, "Native Windows 11 clipboard manager",
  built-with line, and "Global hotkey: Ctrl+Shift+V".
- **Settings:** a real `ContentDialog` with at least: **Start with Windows** toggle (writes
  the Run key), **Theme** (Follow system / Light / Dark — writes `theme_override`), and
  optionally a **Clear history** button and the history cap. Keep it minimal.
- **Clear-history confirm:** "Clear unpinned clipboard history? Pinned items will be kept.
  This cannot be undone." Yes/No.

---

## 17. Single-instance

The app must be single-instance (a second launch should signal the running instance, not
start a second tray icon). Use the WinAppSDK `AppInstance.FindOrRegisterForKey` +
`RedirectActivationToAsync` (preferred), or a named `Mutex`. On redirect, the existing
instance shows the popup.

---

## 18. Build, packaging, CI

- **Local build:** `dotnet build -c Release`. Run the output exe. (Document a
  kill-then-rebuild one-liner — e.g. `Stop-Process -Name Clippet -ErrorAction
  SilentlyContinue; dotnet build …` — since Windows locks the running exe.)
- **Publish (unpackaged, self-contained):**
  `dotnet publish -c Release -r win-x64 --self-contained -p:WindowsAppSDKSelfContained=true`.
  Output is a folder; zip it for distribution. Add a `win-arm64` publish for Arm devices.
- **Trim/size:** consider `PublishReadyToRun` for startup, and `PublishTrimmed` for size
  (**test every dialog and template thoroughly** — WinUI 3 + XAML reflection is
  trim-sensitive; keep a trimming roots / `TrimmerRootAssembly` list).
- **Icon:** keep `assets/clippet.svg` as the design source; generate `clippet.ico` (build
  step or checked-in) and set as app icon + tray icon.
- **CI:** GitHub Actions on `windows-latest` (use the .NET 10 SDK via `actions/setup-dotnet`),
  publish the zipped self-contained folder + SHA-256, attach to a GitHub Release on `v*` tags.

---

## 19. Implementation milestones (suggested order)

Build in vertical slices that each compile and run, front-loading the async architecture.

1. **M0 — Skeleton.** WinUI 3 unpackaged app, hidden tool window, tray icon (H.NotifyIcon),
   single-instance, exit. Async/threading scaffolding + `DispatcherQueue` helpers.
2. **M1 — Capture + list.** Clipboard listener (subclass), capture priority pipeline (text/
   image/file/rtf/html/sheet/code), in-memory history, ListView with row template + tags +
   relative time. Dedup.
3. **M2 — Hotkey popup.** RegisterHotKey Ctrl+Shift+V, show-at-cursor positioning, custom
   title bar, acrylic backdrop, rounded corners, Esc/hide, PREV_FG capture, theming
   (light/dark + override).
4. **M3 — Paste-back.** Re-publish per format + SendInput Ctrl+V + SuppressNextUpdate +
   focus restore.
5. **M4 — Persistence.** Storage (history.json/settings.json, version 1, atomic), media
   PNGs + thumbnails (async/cached), load/orphan-sweep/prune, FNV dedup persisted.
6. **M5 — Search.** Search box, skim-style matcher, filter+sort+FilteredRow, match-run bold
   highlighting, debounce, keyboard nav (Tab/arrows/Enter/Esc).
7. **M6 — Pin + context menu.** Star glyph toggle, pin sort/eviction-exemption, per-row
   MenuFlyout (Paste/Pin/Copy/Delete), Ctrl+P, tray pinned-count tooltip.
8. **M7 — Footer + dialogs + autostart.** Footer icon row (theme/clear/settings/about/quit),
   tooltips, About + Settings dialogs, autostart prompt + registry, final-flush on exit.
9. **M8 — Polish & verify.** Run the acceptance checks (§20), profile the UI thread for
   stalls, DPI correctness on mixed-DPI, trim/size pass, CI/release.

---

## 20. Acceptance criteria (build is "done" when all pass)

**Capture & types**
- Copying text, RTF (Word/WordPad), HTML (browser), an image (Snipping Tool), files
  (Explorer), a spreadsheet range (Excel), and code (VS Code) each produce a correctly
  **tagged** row with the right preview (`[X] R rows x C cols`, `[Image WxH]`,
  `name.ext + N more`, `[C:rs]`, etc.).
- Consecutive identical copies do **not** create duplicate rows.

**Popup & paste**
- Ctrl+Shift+V opens the popup at the cursor on the correct monitor, never spilling off
  screen; Esc hides it; the process keeps running.
- Enter / double-click / context-Paste re-publishes and pastes into the previously focused
  window: RTF keeps formatting, image pastes into Paint, files paste into Explorer, TSV
  pastes into Notepad.
- The app never re-captures its own paste-back (no phantom duplicate after pasting).

**Search**
- Typing filters live with fuzzy matching; matched characters are **bold**; ranking is
  pinned-first → score → recency; clearing the box restores the full list.

**Pin**
- Clicking ★/☆ toggles pin without pasting; Ctrl+P toggles the selected row; pinned items
  sort to the top and **survive** exceeding 200 captures and **survive** Clear History;
  pin state persists across restart; tray tooltip shows `Clippet — N pinned`.

**Persistence**
- Capture items, kill the process, relaunch → history restored.
- Image rows leave both `{id}.png` and `{id}_thumb.png`; deleting/clearing removes them;
  orphaned media is swept on next launch; `.tmp` files never linger.

**Chrome & theme**
- Rounded corners + acrylic backdrop; custom title bar with a Win11-style red-hover close
  button; light/dark follows system and can be forced via the footer toggle (persisted);
  sharp on mixed-DPI.

**Tray & autostart**
- App shows only as a tray icon at launch; left-click toggles popup; right-click menu
  (Open/Clear/Settings/Exit) works; Exit removes the icon and stops the process.
- First launch prompts for autostart once; accepting writes the `Run` key and the app
  starts at next login; the prompt never reappears.

**Responsiveness (the reason this is built async-first)**
- Capturing a large (e.g. 20 MP) screenshot does **not** freeze the popup or the typing in
  the search box — encode/persist happen off the UI thread.
- Opening the popup with a full 200-item history (including images) renders without a
  visible stall.

---

## 21. Gotchas / implementation traps

- **SuppressNextUpdate ordering:** set the flag *before* `SetClipboardData`, clear it when
  the resulting `WM_CLIPBOARDUPDATE` arrives. Getting this wrong = every paste re-captures
  itself.
- **Row→item mapping always via FilteredRow.** Never assume ListView order == history order;
  it's filtered and sorted.
- **Don't auto-hide on focus loss**, but *do* update PREV_FG on deactivation (skip
  app-owned windows). This is what lets the user reposition the popup and still paste into
  the right place.
- **Pinned exemption** applies to both the 200-cap eviction *and* Clear History.
- **Image dedup uses the PNG-byte hash**, not `Raw` (which is empty for images).
- **Spreadsheet pastes as `CF_TEXT` (TSV)**, not Unicode — deliberate, for universal
  acceptance.
- **DIB exactness:** capture handles `BI_RGB` 24/32bpp *and* `BI_BITFIELDS` 32bpp (modern
  Snipping Tool uses BITFIELDS). On paste, emit a **top-down 32bpp BI_RGB** DIB. Reuse
  `Windows.Graphics.Imaging` for PNG; you still need manual BITMAPINFOHEADER construction for
  the `CF_DIB` paste path.
- **AppWindow uses physical pixels**; scale popup size/position by the monitor DPI.
- **WinUI 3 has no WndProc** — you must subclass the HWND to see `WM_HOTKEY` /
  `WM_CLIPBOARDUPDATE`.
- **Trimming** can break WinUI 3 (XAML reflection). If you trim, test every dialog and
  template, and maintain a trimmer-roots list.

---

## 22. Module responsibility map

| Module | Responsibility |
|---|---|
| `Models/*`, `Palette.cs` | types, constants, palette, app-state shapes |
| `ClipboardMonitor.cs`, `ImageCodec.cs` | capture priority, DIB↔PNG, format readers |
| `ClipboardWriter.cs` | re-publish + SendInput paste synthesis |
| `Storage.cs` | json/media/registry, atomic writes, prune, orphan sweep |
| `SearchEngine.cs`, `FuzzyMatcher.cs` | push/dedup, filter+sort, FilteredRow, fuzzy match |
| `Views/PopupWindow`, `Controls/ClipRow`, MenuFlyout | rows, pin hit area, context menu |
| XAML title bar + `AppWindow.TitleBar` | custom caption + close button |
| `ThemeService.cs` + ResourceDictionaries | theme detect, palette, DWM backdrop |
| `TrayService.cs` | tray icon/menu, popup positioning, autostart prompt |
| XAML footer + `FontIcon` | footer icon buttons + tooltips |
| `Util.cs` | time formatting, FNV hash, IDE detect, html/rtf strip |
| `App.xaml.cs`, `PopupWindow.xaml.cs`, `HotkeyService.cs` | app bootstrap, hotkey, message dispatch |

> The reference Rust prototype (`src/*.rs`, `docs/level-*.md`) is a useful worked example for
> the fiddly Win32 details — DIB byte layouts, the capture-priority ordering, the
> suppress/paste handshake, the RTF/HTML strippers. Consult it when a low-level detail is
> ambiguous, but the WinUI 3 app is its own thing: build each layer idiomatically in C#, and
> when in doubt, choose the approach that's clearest and most responsive rather than the one
> that most closely mimics the prototype.

---

*End of plan.*
