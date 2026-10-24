# Changelog

## 1.0.4 - 2026-10-24

- Controller support in Desktop Mode. Start+Back (or Guide, if chosen in settings) now shows the overlay in Desktop Mode too, not only in Fullscreen Mode.
- XInput controllers (Xbox and compatible pads) are read directly by the extension, so the shortcut and overlay navigation work while a game has the focus and Playnite is in the background.
- Custom controller combination in settings: pick 2 or 3 buttons (Start, Back, Guide, LB, RB, LT, RT, LS, RS, A, B, X, Y, D-pad) to show and hide the overlay, so it does not clash with Steam, Moonlight or other apps. The default stays Start+Back, and the Start+Back, Guide and Off presets from 1.0.0 are kept as they are.
- The controller shortcut now also closes the overlay while it is open.
- Holding the shortcut toggles the overlay only once, and a pad never triggers twice in Fullscreen Mode.
- With no game running, the shortcut brings Playnite to the front in the mode it is already in (Desktop Mode is no longer switched to Fullscreen).
- The version number is shown in small text in a corner of the overlay, and at the top of the settings page with a Check for updates link to the GitHub releases.
- Moonlight/Sunshine streaming support (experimental), feedback welcome: Sunshine's virtual Xbox pad is an XInput controller, so the shortcut works over a stream too.

## 1.0.0 - 2026-10-09

First public release of Playnite Next Overlay, a fork of Playnite Game Overlay 0.4.5.

- New extension id, name, and assembly, so it can be installed beside the original.
- Screenshot, record, recent-clip, instant-replay, and performance buttons. A fresh install sends the NVIDIA App's standard keys (Alt+F1, Alt+F9, Alt+F10, Alt+Shift+F10, Alt+R).
- Stream button, shown only after you set a shortcut. Clearing any shortcut hides its button.
- Captures button. An empty folder opens your Videos folder, where the NVIDIA App saves its captures.
- Browser button. An empty path opens the default browser, and a browser with only background processes is started instead of being treated as already open.
- Achievements from Playnite Achievements (SQLite cache, read only), with SuccessStory as a fallback.
- Clickable recent-achievement panel and a full list: mouse and gamepad, filters, rarity, secret descriptions hidden until you reveal them.
- Optional pause of the game while the list is open, with several ways back to a running game.
- Discord button removed.
- No personal paths are shipped.
