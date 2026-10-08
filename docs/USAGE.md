# Playnite Next Overlay: full guide

[Back to the README](../README.md)

## Opening the overlay

| Action | Default |
| --- | --- |
| Show or hide the overlay | Alt+` (backtick, usually the key just left of 1) |
| Show or hide the overlay | Start + Back on a controller (View + Menu) |
| While no game is running | The same shortcuts open Playnite in full screen |
| Move around the overlay | D-pad or left stick, or the arrow keys |
| Activate the focused item | A, or Enter |
| Close the overlay | B, or Esc |

The controller shortcut can be changed to the Guide (Xbox) button, or turned off, in settings.

## Achievement list

Open it by clicking the recent-achievement panel, or by selecting that panel and pressing A.

| Action | Controller | Keyboard / mouse |
| --- | --- | --- |
| Move | D-pad or left stick. Hold to repeat. | Up and Down |
| Page | Left and Right | Page Up and Page Down, or Left and Right |
| Jump to the top or bottom | LT and RT | Home and End |
| Change filter (All, Unlocked, Locked) | LB and RB, or X | Left, Right, or Tab, or click the tabs |
| Reveal or hide secret descriptions | Y | H |
| Back to the main overlay | B | Esc or Backspace |
| Scroll | Left stick | Mouse wheel or touchpad |

Secret descriptions stay hidden until the achievement is unlocked, or until you reveal them.

If Playnite Achievements has no data for a game, the overlay falls back to SuccessStory when that extension is installed.

## Settings

Open Playnite, then Add-ons, then Extensions settings, then Playnite Next Overlay.

### Shortcut format

Shortcuts use [SendKeys](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.sendkeys.send) syntax:

| You want | Type this |
| --- | --- |
| Ctrl | `^` |
| Shift | `+` |
| Alt | `%` |
| Windows key | `{WIN}` in front of the rest |
| A function key | `{F1}` to `{F12}` |

A button is shown only when its shortcut box is not empty. When you press it, the overlay hides itself, gives the game a moment to take focus, then sends the keys.

### NVIDIA App

| Button | NVIDIA App keys | Value in settings |
| --- | --- | --- |
| Screenshot | Alt+F1 | `%{F1}` |
| Record | Alt+F9 | `%{F9}` |
| Recent clip (save Instant Replay) | Alt+F10 | `%{F10}` |
| Instant replay on or off | Alt+Shift+F10 | `+%{F10}` |
| Performance overlay | Alt+R | `%r` |

These are NVIDIA's standard keys. If you changed them in the NVIDIA overlay's keyboard settings, copy yours instead. Instant Replay has to be turned on before the recent-clip button can save anything.

### Steam

Steam's default screenshot key is F12, which is `{F12}` in the shortcut box. Steam's other recording keys depend on your Steam settings, so copy them from there.

### Out-of-the-box shortcuts

A fresh install uses the NVIDIA App keys above for all five buttons: Screenshot, Record, Recent clip, Instant replay and Performance. If you use the NVIDIA App, there is nothing to set up. With another recorder, replace them with its keys.

Stream starts empty, so its button stays hidden until you give it a shortcut. To hide any other button, clear its box.

### Captures folder and browser

- **Captures folder.** Leave it empty to open your Videos folder, where the NVIDIA App saves its captures (one folder per game). If your recorder saves somewhere else, type that folder instead.
- **Browser.** Leave it empty to use your default browser, or type the path to a browser's `.exe`.

## Pausing the game

"Pause the game while the achievement list is open" is on by default.

While the list is open, the extension suspends the game's process. It resumes the game when you go back, hide the overlay, the game exits, Playnite closes, or the extension is unloaded.

This is a hard pause of the process, not the game's own pause menu, so:

- Online games, anti-cheat and some launchers do not like being frozen. Turn the option off for those.
- Sound can glitch, or a cutscene can desync, after the game resumes. Turn the option off if that happens.
- The extension never suspends Windows itself, Explorer, the desktop compositor or common tools such as recorders, overlays, launchers and Microsoft Edge. If it cannot find a safe game process, it leaves the game running.
- A game made of several processes might only pause the one Playnite reports. If the picture freezes but the music keeps going, turn the option off for that game.
