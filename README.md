[marketplace]: <https://marketplace.visualstudio.com/items?itemName=MadsKristensen.StartScreen>
[vsixgallery]: <https://www.vsixgallery.com/extension/StartScreen.2d76ac3d-7ff2-47e1-82d8-e507cf765bbe>
[repo]: <https://github.com/madskristensen/StartScreen>

# Start Screen for Visual Studio

[![Build](https://github.com/madskristensen/StartScreen/actions/workflows/build.yaml/badge.svg)](https://github.com/madskristensen/StartScreen/actions/workflows/build.yaml)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/madskristensen)](https://github.com/sponsors/madskristensen)

Download this extension from the [Visual Studio Marketplace][marketplace]
or get the latest CI build from [Open VSIX Gallery][vsixgallery].

Start Screen brings a fast, modern, and developer-focused launch experience to Visual Studio.

![Start Screen screenshot placeholder](art/startscreen.png)

It replaces the default start page with a practical workspace hub that helps you get productive right away.

## Why you will love it

- Clean dashboard for opening, creating, and cloning projects
- Smart recent list with search, pinning, and time-based grouping
- Developer news feed with curated sources and background refresh
- Native Visual Studio look and feel with full theme integration
- Lightweight and designed for speed

## Features

### Quick actions

Launch common workflows from one place:

- New Project
- Open Project
- Open Folder
- Clone Repository

### Intelligent recent files

- Instant filtering with search
- Pin important solutions and projects
- Organize recents by time groups like Today and This week
- Git branch name displayed for each repository
- Ahead/behind commit counts relative to the upstream tracking branch
- Uncommitted changes indicator for dirty working directories
- Last commit timestamp shown as a relative time (e.g., 2h ago)
- Rich tooltip with branch, ahead/behind, and last commit details
- Detached HEAD state shown as an abbreviated commit SHA
- Git status loaded in the background without blocking the UI
- Right-click context menu with keyboard shortcuts

### Keyboard navigation

Full keyboard support across the entire Start Screen:

**Recent solutions/folders list**

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
| Right      | Move focus to news     |

**News feed**

| Shortcut            | Action                     |
| ------------------- | -------------------------- |
| Up / Down           | Move between rows          |
| Left / Right        | Move between columns       |
| Enter               | Open in browser            |
| Ctrl+C              | Copy URL                   |
| Left (first column) | Move focus to recent files |

**Action bar**

| Shortcut     | Action                     |
| ------------ | -------------------------- |
| Left / Right | Move between buttons       |
| Down         | Move focus to recent files |

### Built-in developer news

- Read fresh engineering news directly inside Visual Studio
- Combines multiple RSS and Atom sources into one view
- Uses local cache for fast startup and background refresh for freshness

### Custom feed URLs

Add your own RSS or Atom feeds by editing the `newsfeeds.json` file:

- Location: `%USERPROFILE%\.vs\StartScreen\newsfeeds.json`
- Add entries with `name`, `url`, and `enabled` properties
- Changes are detected automatically - no restart required
- JSON schema validation is available for IntelliSense support

Example entry:

```json
{
  "name": "My Custom Feed",
  "url": "https://example.com/feed.xml",
  "enabled": true
}
```

## Installation

1. Install from the [Visual Studio Marketplace][marketplace].
2. Restart Visual Studio.
3. Open Start Screen and start coding.

## Configuration

Start Screen is designed to work out of the box. Feed preferences and pinned items are persisted automatically.

## Feedback and support

- Report bugs and request features in the [GitHub repository][repo]
- Share ideas to help shape future improvements

## Contribute

Check out the [issue tracker](https://github.com/madskristensen/StartScreen/issues) for ideas, bugs, and feature requests. Pull requests are welcome.
