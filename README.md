[marketplace]: <https://marketplace.visualstudio.com/items?itemName=MadsKristensen.StartScreen>
[vsixgallery]: <https://www.vsixgallery.com/extension/StartScreen.2d76ac3d-7ff2-47e1-82d8-e507cf765bbe>
[repo]: <https://github.com/madskristensen/StartScreen>

# Start Screen for Visual Studio

[![Build](https://github.com/madskristensen/StartScreen/actions/workflows/build.yaml/badge.svg)](https://github.com/madskristensen/StartScreen/actions/workflows/build.yaml)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/madskristensen)](https://github.com/sponsors/madskristensen)

Download this extension from the [Visual Studio Marketplace][marketplace]
or get the latest CI build from [Open VSIX Gallery][vsixgallery].

----------------------------------------------

**Your first five seconds in Visual Studio should feel *fast*.** Start Screen
replaces the default launch experience with a clean, modern dashboard that
gets you into code immediately - no clicking through menus, no waiting.

![Start Screen screenshot](art/startscreen.png)

## Get to work in one click

Open a recent solution, create a new project, open a folder, or clone a
repo - all from a single, focused screen. The action bar puts every common
workflow front and center so you never have to dig through File menus again.

## Your recent projects, supercharged

Start Screen doesn't just list your recent files. It understands how you
work:

**Find anything instantly.** Type a few characters and the search filter
narrows your list in real time. Pin the projects you care about most so
they always float to the top.

**See your Git status at a glance.** Every repo shows its current branch,
ahead/behind counts, uncommitted changes, and the last commit time - all
loaded in the background so the UI stays snappy. Hover for the full
picture, or spot a detached HEAD before you accidentally commit to nowhere.

**Stay organized.** Projects are grouped by when you last touched them -
*Today*, *This week*, *This month* - so yesterday's prototype doesn't bury
this morning's deadline.

**Right-click for power moves.** Open the containing folder, launch a
terminal, copy the path, pin, unpin, or remove - all from a context menu
with keyboard shortcuts.

![Context menu](art/context-menu.png)

## Developer news, built right in

A curated feed of engineering news from the Visual Studio Blog, .NET Blog,
and more lives right next to your project list. It refreshes in the
background and caches locally, so it's always ready and never slows you
down.

Want different sources? Drop your own RSS or Atom feeds into a simple JSON
file and Start Screen picks them up automatically - no restart needed.

```txt
%USERPROFILE%\.vs\StartScreen\newsfeeds.json
```

```json
{
  "name": "My Custom Feed",
  "url": "https://example.com/feed.xml",
  "enabled": true
}
```

Full JSON schema validation is included so you get IntelliSense while
editing.

## Tip of the day

Every time Visual Studio starts, a short productivity tip appears at the
bottom of the Start Screen. The tips rotate daily and cover navigation
shortcuts, editing tricks, refactoring commands, and other features you
might not know about. No configuration required - just glance down and
learn something new.

![Tip of the day screenshot](art/tip.png)

Have a tip to share? [Open an issue][repo] with your suggestion or submit
a pull request directly to
[tips.txt](https://github.com/madskristensen/StartScreen/blob/master/src/Resources/tips.txt).

## Suggested extensions

Right next to the Tip of the Day, Start Screen showcases a curated
extension from the Visual Studio Marketplace. The suggestion rotates
daily and highlights free, open-source extensions that enhance your
development workflow.

Extensions you've already installed show an "Installed" badge.
Extensions you haven't tried yet display an "Install" link that takes
you straight to the Marketplace page.

Want to suggest an extension? Submit a pull request to
[extensions.json](https://github.com/madskristensen/StartScreen/blob/master/src/Resources/extensions.json).
We're looking for extensions that are:

- Free
- Open source (with a GitHub repository link)
- Support both ARM and x86 architectures
- Compatible with Visual Studio 2022+

## Keyboard-first design

Every part of Start Screen is navigable without a mouse.

Open the Start Screen any time from **File > Start Screen** or press
**Ctrl+Shift+Alt+Backspace**.

### Recent solutions / folders

| Shortcut   | Action                 |
| ---------- | ---------------------- |
| Up / Down  | Move between items     |
| Enter      | Open                   |
| Ctrl+Enter | Open in new instance   |
| O          | Open containing folder |
| T          | Open in terminal       |
| Ctrl+C     | Copy path              |
| P          | Pin / Unpin            |
| Del        | Remove from list       |
| Alt+`      | Focus search box       |
| Right      | Jump to news feed      |

### News feed

| Shortcut            | Action                     |
| ------------------- | -------------------------- |
| Up / Down           | Move between rows          |
| Left / Right        | Move between columns       |
| Enter               | Open in browser            |
| Ctrl+C              | Copy URL                   |
| Left (first column) | Jump back to recent files  |

### Action bar

| Shortcut     | Action                     |
| ------------ | -------------------------- |
| Left / Right | Move between buttons       |
| Down         | Move focus to recent files |

## Looks right at home

Start Screen inherits your Visual Studio theme - Light, Dark, Blue, or
whatever you're running. No jarring color mismatches, no extra
configuration. It just blends in.

## Install it

1. Grab it from the [Visual Studio Marketplace][marketplace].
2. Restart Visual Studio.
3. That's it. Start Screen is already waiting for you.

It works out of the box. Feed preferences and pinned items are persisted
automatically.

## Get involved

Found a bug? Have an idea? Head to the [issue tracker][repo] - pull
requests are always welcome.
