# KbmOverlay

**English** · [한국어](README.ko.md)

KbmOverlay shows your keyboard and mouse input in OBS as browser sources. It works with one PC, and with two PCs
(a game PC and a streaming PC).

- **A single exe.** It uses .NET Framework 4.8, which comes with Windows 10 and 11, so there is nothing to install.
- **One PC needs no firewall changes.** The overlays are served only inside the PC (`localhost`).
- **No OBS refresh needed.** Restart KbmOverlay and the overlays reconnect on their own within a few seconds.
- **Same OBS URLs with two PCs.** The streaming PC also uses `http://localhost:5520/...`. You never type an IP into OBS.
- **Key input is never saved or sent to the internet.** See [Privacy](#privacy).
- **English, Korean, Japanese and Chinese.** The language follows Windows; see [Languages](#languages).

> KbmOverlay is a personal project. It is not an official program of OBS Studio (OBS Project) or Zergatul Input
> Overlay and is not affiliated with them. Parts of its design come from Zergatul Input Overlay under the MIT license.

## Getting started

1. Download `KbmOverlay.exe` from [Releases](https://github.com/xyzw0128-debug/KbmOverlay/releases/latest) and double-click it.
   The first time, Windows may say "Windows protected your PC" because the exe is not code-signed.
   Choose **More info → Run anyway**.
2. The settings page (`http://localhost:5520/`) opens in your browser. If you closed it, press **Enter** in
   KbmOverlay's window. It has the URLs to copy, the sizes to use in OBS, and live previews.
3. In OBS, under **Sources**, press **+** → **Browser**, and paste a copied URL into **URL**.
   Set **Width** and **Height** to the numbers on the settings page. Leave "Local file" unchecked.

If a game runs as administrator, start KbmOverlay as administrator too (**right-click → Run as administrator**),
or its input will not show.

## Two PCs (game PC + streaming PC)

Run `KbmOverlay.exe` on both PCs. They must be on the same network (same router).

**Streaming PC (the one with OBS), first time only**
1. On the settings page, choose **Two PCs** in the menu on the left and turn on the **Receive from game PCs** switch.
2. When Windows asks for administrator permission, choose **Yes**. This allows KbmOverlay in the firewall.
   - If the network is set to **Public**, the page tells you. Either turn on the **Also receive on Public networks**
     switch (allows KbmOverlay only), or set the network to **Private** in Windows Settings. See [Public networks](#public-networks).
3. In OBS, use the same `http://localhost:5520/...` URLs as with one PC.

**Game PC, first time only**
1. Start KbmOverlay. Within a few seconds it shows "Found streaming PC '…'".
2. Press **S** in its window, or press **Send** on the settings page.
3. Both PCs show the same **6-digit code**. On the streaming PC, check that the codes match and press **Allow**
   (in its console window: **Y** to allow, **N** to deny).

From then on, starting KbmOverlay on both PCs is enough; they connect by themselves. While the game PC is connected,
the streaming PC's overlays show the game PC's input. If the connection drops, held keys are released and the
overlays show the streaming PC's own input again.

- If the game PC cannot find the streaming PC (some routers block devices from seeing each other), enter the streaming
  PC's IP on the game PC's settings page. The streaming PC's settings page shows its IP under Two PCs.
- To stop, press **Stop sending** on the game PC. To remove an allowed game PC, press **Remove** on the streaming PC.
- Two PCs use port **5521** (TCP and UDP). The firewall rules ("KbmOverlay two-PC relay") are added only on the
  streaming PC, for KbmOverlay.exe only, from the local subnet only, on Private/Domain networks only.
  If you move the exe to another folder, press **Allow in the firewall** under Two PCs to update the rules.

### Public networks

Windows 11 often marks new networks as Public, even a home router. If the streaming PC is on a Public network:

| Option | What it opens |
|---|---|
| **Also receive on Public networks** (switch on the settings page) | KbmOverlay.exe only, port 5521 only, connections from the same network only |
| Set the network to **Private** | Windows features such as network discovery and file and printer sharing also trust that network |

**Also receive on Public networks** opens less. It also applies on real public networks such as café Wi-Fi, but the
streaming PC only *receives* input, and nobody can connect without you allowing them. To stop it, turn the
**Also receive on Public networks** switch off. The game PC needs no changes on a Public network.

## Overlay URLs

| URL | Shows | Options |
|---|---|---|
| `http://localhost:5520/keyboard.html` | Keyboard | `layout=wasd` (default), `tkl`, `full` or [your own](#editing-the-files); `keys=KeyW,KeyA,…` (only these keys); `scale=1.5` |
| `http://localhost:5520/mouse.html` | Mouse buttons and wheel | `scale=1.5` |
| `http://localhost:5520/mouse-move.html` | Ring showing the mouse's direction | `size=400` (px), `sens=1` (sensitivity) |

Add options after `?` and join them with `&`, e.g. `keyboard.html?layout=full&scale=0.8`.
Add `bg=1` to any page to give it a background, which makes it easier to check in a normal browser.

Every page also takes colors, as 6 hex digits without `#`: `pressed` (held), `released` (start of the fade),
`glow` (keyboard only) and `line` (outline), e.g. `keyboard.html?pressed=1F6FEB&line=FFCC00`.
The keyboard and mouse also take `fade` (seconds the fade after release lasts, `0` = none), `glowsize` (`1` = normal,
`0` = no glow), `idle` (how much shows while not pressed, `1` = normal, `0` = only while pressed), `press` (press
effect: `sink`, `pop` or `ripple`) and, keyboard only, `round` (key corner in px).

You don't need to type any of this: everything in [Customizing](#customizing) is set on the settings page, which
builds the URL. Changes there do not change sources already in OBS; copy the URL again and replace it in the source.

## Languages

The console window and the settings page are available in **English, Korean (한국어), Japanese (日本語) and
Simplified Chinese (简体中文)**. Overlays have no text, so they look the same in every language.

KbmOverlay picks the language in this order:
1. `--lang` when starting it, e.g. `KbmOverlay.exe --lang ja`
2. the language chosen at the bottom left of the settings page (remembered)
3. the Windows display language
4. English

Translations live in `pages/i18n/<code>.json`, shared by the console and the pages. To add or fix a language, press
**Open the pages folder** under **About** on the settings page (see [Editing the files](#editing-the-files)), then add
`i18n/<code>.json` to that folder, starting from a copy of
[en.json](https://github.com/xyzw0128-debug/KbmOverlay/blob/main/pages/i18n/en.json). It is picked up without rebuilding. Use a two-letter code (`fr`) or a region (`zh-TW`, used automatically on
Traditional Chinese Windows). Keys missing from a file fall back to English.

The Japanese and Chinese translations were not written by native speakers; corrections are welcome.

## Customizing

Everything is on the settings page. Your choices go into the overlay URL, so each OBS source can look different, and
the preview shows the result right away.

- **Colors:** the **Colors** box (presets, or a picker per color). The same colors go into every overlay's URL.
- **Fade after release, glow size, rounded corners, shown while not pressed:** the sliders under **Options** for the
  keyboard and the mouse. **Default** next to a slider puts it back. Set **Shown while not pressed** to **Hidden** (0%)
  to show only the keys being pressed.
- **Press effect:** **Sink in**, **Pop up** or **Ripple**, under **Press effect** in **Options**.
- **Preview background:** the circles at the top right of each preview: see-through like OBS, black, gray, white or
  any color. It only changes the preview, to check effects more easily; it is not part of the URL.
- **Which keys the keyboard shows:** set **Layout** to **Pick keys**. A full keyboard appears; click a key to turn it
  on or off. **Add by pressing** adds every key you press on your real keyboard until you press the button again or
  switch to another window. **Around WASD**, **All** and **Clear** are quick starts.
  The keys keep their places from a full-size keyboard, and empty rows between them are left out.

After changing something, copy the URL again and replace it in the OBS source.

### Editing the files

Only needed to change the look with CSS yourself, or to add keyboard layouts or a translation. On the settings page,
press **Open the pages folder** under **About** (or run `KbmOverlay.exe --export-pages`). This copies `custom.css` and
the keyboard layouts (`layouts/*.json`) to a `pages` folder next to the exe, keeping files already there, and opens it.
Edit them and refresh the source in OBS to see the change. If the folder cannot be made (for example when the exe is in
`Program Files`), move the exe somewhere you can write to, such as the desktop.

- `custom.css`: every overlay loads it after the built-in `theme.css`, so what is in it wins. The colors and the look
  (`--pressed`, `--glow-size`, `--key-release-time`, …) are in it as comments; take out the comment marks around a line
  and change the value. Other CSS rules work too. Values in an overlay URL win over these.
- Add `layouts/name.json` and use it as `keyboard.html?layout=name`. After a refresh, the settings page lists it under
  **Layout** too.

These files stay yours when KbmOverlay is updated. The other pages (`theme.css`, `keyboard.html`, `overlay.js`, …)
stay inside the exe, so updates reach them. Older versions copied those too, and an old copy in the `pages` folder
hides the new features and fixes. When a copy differs from the built-in file, KbmOverlay's window and the top of the
settings page name it; unless you changed it on purpose, delete it. If you had changed `theme.css`, move just your
changed lines into `custom.css` and delete `theme.css`.

### Keyboard layout files

List the keys row by row, from left to right. Sizes are in key units.

```json
{
  "rows": [
    { "h": 0.75, "keys": ["Esc", { "k": "F1", "x": 1 }] },
    { "y": 0.25, "keys": ["Tilde", "Key1", "Key2"] },
    { "keys": [{ "k": "Tab", "w": 1.5 }, "KeyQ", { "k": "KeyW", "l": "Up" }] }
  ]
}
```

- A plain key name gets the default size (1×1) and the default label.
- `w` / `h`: width / height, `x`: space before this key, `l`: label, `f`: font size in px.
- On a row, `y`: space above the row, `h`: default height of its keys.

### Key names

| Group | Names |
|---|---|
| Function | `Esc` `F1`–`F24` `PrintScreen` `ScrollLock` `Pause` |
| Number row | `Tilde` `Key1`–`Key0` `Minus` `Plus` `Backspace` |
| Letters | `KeyA`–`KeyZ` `Tab` `CapsLock` `Enter` `Space` |
| Symbols | `LeftBracket` `RightBracket` `Backslash` `Semicolon` `Quote` `Comma` `Period` `Slash` `IntlBackslash` |
| Modifiers | `LeftShift` `RightShift` `LeftCtrl` `RightCtrl` `LeftAlt` `RightAlt` `LeftWindows` `RightWindows` `App` |
| Korean | `Hangul` (Han/Yeong) `Hanja` |
| Editing and arrows | `Insert` `Home` `PageUp` `Delete` `End` `PageDown` `Up` `Down` `Left` `Right` |
| Numpad | `NumLock` `NumSlash` `NumAsterisk` `NumMinus` `NumPlus` `NumEnter` `NumPeriod` `Num0`–`Num9` |
| Mouse | `Mouse1` (left) `Mouse2` (right) `Mouse3` (wheel) `Mouse4` (back) `Mouse5` (forward) |

The names match Zergatul Input Overlay, so its pages are easy to port.

## Command-line options

| Option | Description |
|---|---|
| `--port 5530` | Overlay port (default 5520). Change the port in your OBS URLs too. Avoid 5521, which two PCs use. |
| `--lang ja` | Language of the console and the settings page (`en`, `ko`, `ja`, `zh`, …). |
| `--export-pages` | Copy `custom.css` and the keyboard layouts to a `pages` folder. Existing files are not overwritten. Same as **Open the pages folder** on the settings page. |
| `--no-browser` | Do not open the settings page when starting, e.g. when KbmOverlay starts with Windows. **Enter** in its window still opens it. |
| `--firewall-check` | Show the streaming PC's firewall status for Private, Domain and Public networks (read only). For troubleshooting two PCs. |

## How it works

- **Input:** Raw Input. It receives input while a game has focus, and gets the mouse's relative movement even when a
  game locks the cursor (FPS games). It does not hook into system input, so it cannot slow input down.
- **Sending:** keys and buttons go out immediately. Mouse movement and wheel are summed and sent at most 125 times a
  second (the totals are kept). With 1000 Hz of simulated mouse movement this cut the number of messages to about a tenth.
- **No stuck keys:** if a release is missed (for example while a UAC prompt is open), KbmOverlay checks Windows' key
  state every 0.5 s and releases the key within about a second.
- **Korean Han/Yeong and Hanja keys:** some keyboards send no release for these, so each press lights up briefly.
- **Reconnecting:** when the connection drops, pages clear all pressed keys; when it comes back they receive the
  current pressed state.
- **Two PCs:** the game PC connects to the streaming PC and sends the same events, so only the streaming PC needs a
  firewall rule. The game PC finds it with a UDP broadcast and only receives its answer. The game PC sends a keep-alive
  every 5 s; if nothing arrives for 15 s, the streaming PC drops the connection and releases held keys.

## Privacy

KbmOverlay handles key input, so here is exactly what it does and does not do.

- **It does not save key input.** Pressed keys go from memory straight to the overlays and are never written to a
  file or log. "Last key" on the settings page is shown only in that browser page.
- **It sends nothing to the internet.** No update checks, usage statistics or error reports.
- **It uses only these network connections:**
  - port 5520: showing the overlays and the settings page inside this PC (localhost)
  - port 5521: only with two PCs turned on, between paired PCs on the same network (encrypted)
- **It stores one file**, `%APPDATA%\KbmOverlay\settings.json`, with:
  - a random ID for this PC
  - whether receiving is on, and the chosen language
  - the names and addresses of paired PCs
  - pairing secrets (encrypted with your Windows account)

  Delete that folder after removing KbmOverlay and nothing is left.
- The settings page remembers the menu, options, colors and keyboard keys you picked and the URLs you last copied in
  that browser (local storage for `localhost:5520`). No key input goes there; picked keys are only a list of key names.
- With two PCs, other KbmOverlay instances on the same network can see this PC's **computer name** (e.g.
  `DESKTOP-ABC123`) and its random ID. While receiving is on, so can any KbmOverlay on the network that is looking
  for a streaming PC.

## Security

A program that shows key input works like a keylogger, so it is locked down so that nothing else can read the input.
If you find a security problem, report it privately as described in [SECURITY.md](SECURITY.md), not in a public issue.

**Overlays and settings page (port 5520)**
- Listens only on `127.0.0.1` and `::1`. Other PCs cannot connect.
- Rejects requests whose `Host` header is not `localhost`, `127.0.0.1` or `[::1]` (blocks DNS rebinding).
- The input stream (`/ws`) only accepts pages served by KbmOverlay itself. If another website open in your browser
  tries to connect, it is refused and a warning appears in the console. Two-PC settings (turning receiving on,
  allowing a PC, …) can only be changed through this stream, so other websites cannot press them for you.

**Two PCs (port 5521)**
- Only a streaming PC with receiving turned on opens this port, and it accepts connections only from private
  (local network) addresses. It cannot be reached from the internet: KbmOverlay never opens ports on your router
  (no UPnP, no port forwarding).
- Even if the router is compromised, the encrypted input cannot be read and paired PCs cannot be impersonated.
  An attacker can only drop the connection or show pairing requests.
- Input is sent only after you **pick the streaming PC on the game PC and allow it on the streaming PC**. If another
  PC on the network pretends to be a streaming PC, nothing is sent unless you pick it on the game PC.
- Every connection makes new keys (P-256 ECDH) and is **encrypted (AES-256) and authenticated (HMAC-SHA256)**.
  Others on the same network cannot read the input.
- When pairing, matching **codes** on both screens mean no other PC is in between.
- After pairing, both PCs remember a pairing secret, so they do not ask again. It is stored in
  `%APPDATA%\KbmOverlay\settings.json`, encrypted with your Windows account (DPAPI); copying the file to another PC
  does not make it usable.
- After you deny a game PC, its new requests are ignored for one minute.

## Troubleshooting

- **Nothing shows:** on the settings page, check that the top left says "connected" and that "Last key" changes when
  you press a key. If it does, check the OBS source (URL, size, "Local file" unchecked).
- **Only games do not show:** the game probably runs as administrator. Run KbmOverlay as administrator too.
- **"Port … is used" or "reserved":** start with another number, e.g. `KbmOverlay.exe --port 5530`, and change the
  port in your OBS URLs.
- **Two PCs: the game PC does not find the streaming PC.**
  - Check that Two PCs on the streaming PC's settings page says it is waiting for a game PC.
  - If it says the network is Public, turn on **Also receive on Public networks**, or set the network to **Private**
    in Windows Settings > Network & internet.
  - If it still fails, enter the streaming PC's IP on the game PC's settings page.
  - Run `KbmOverlay.exe --firewall-check` on the streaming PC to see whether the firewall rules exist.
- **Two PCs: the game PC's input does not show on the streaming PC.** Check that the top left of the streaming PC's
  settings page says it is showing the game PC. If it does and input still does not show, run KbmOverlay on the game PC as
  administrator (when the game runs as administrator).

## Building

With the .NET SDK (8 or later):

```bash
dotnet build -c Release
```

The result is a single `bin\Release\net48\KbmOverlay.exe`; the pages (`pages\`) are built into it.

## Making a release

GitHub Actions builds the exe and attaches it to the release (`.github/workflows/release.yml`).

1. Raise `<Version>` in `KbmOverlay.csproj` (e.g. `0.4.1`), commit and push.
2. Tag the same version and push the tag:
   ```bash
   git tag v0.4.1
   ```
   ```bash
   git push origin v0.4.1
   ```
3. A few minutes later, Releases has `KbmOverlay 0.4.1` with `KbmOverlay.exe`, `LICENSE` and
   `THIRD-PARTY-NOTICES.txt`. The notes include the exe's SHA-256.

The build stops if the tag and `<Version>` differ. If a release with that tag already exists, only its files are replaced.

## License and credits

KbmOverlay is released under the MIT license; see `LICENSE`.
Both notices are also built into the exe and shown at **Licenses and credits** under **About** on the settings page
(`http://localhost:5520/licenses.html`).

The mouse picture (`mouse.svg`), the key and mouse colors and animations, the mouse movement ring and the key name
table come from [Zergatul.Obs.InputOverlay](https://github.com/Zergatul/Zergatul.Obs.InputOverlay) (MIT).
Its copyright notice is in `THIRD-PARTY-NOTICES.txt`.
