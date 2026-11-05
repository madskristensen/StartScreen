[marketplace]: <https://marketplace.visualstudio.com/items?itemName=MadsKristensen.StartScreen>
[vsixgallery]: <https://www.vsixgallery.com/extension/StartScreen.2d76ac3d-7ff2-47e1-82d8-e507cf765bbe>
[repo]: <https://github.com/madskristensen/StartScreen>

# Start Screen for Visual Studio

[![Build](https://github.com/madskristensen/StartScreen/actions/workflows/build.yaml/badge.svg)](https://github.com/madskristensen/StartScreen/actions/workflows/build.yaml)
[![Install from VSIX Gallery](https://www.vsixgallery.com/badge/StartScreen.2d76ac3d-7ff2-47e1-82d8-e507cf765bbe.png)][vsixgallery]
[![GitHub Sponsors](https://img.shields.io/github/sponsors/madskristensen)](https://github.com/sponsors/madskristensen)

Download this extension from the [Visual Studio Marketplace][marketplace]
or get the latest CI build from [Open VSIX Gallery][vsixgallery].

----------------------------------------------

**Your first five seconds in Visual Studio should feel *fast*.** Start Screen
replaces the default launch experience with a clean, modern dashboard that
gets you into code immediately - no clicking through menus, no waiting.

### Startup and background work

The action bar and search are available before secondary content loads. Recent
projects load first; Git metadata, repository summaries, Dev Hub, and news start after the UI has
yielded to rendering and user input. Cache reads, feed parsing, and thumbnail
decoding run on background threads with bounded disk and network concurrency.
Dev Hub does not read its cache or authenticate providers when disabled.

Hiding the Start Screen cancels refreshes and pauses its automatic refresh timer.
Visual Studio shutdown also cancels background work and pending dispatcher waits,
without waiting for the Start Screen to unload or hiding it synchronously.
Background Git fetches suppress credential prompts, honor cancellation, and time
out after five seconds. Reopening the screen does not retry the same repository
more than once every fifteen minutes, including failed fetch attempts.

For startup profiling, Debug builds emit `[StartScreen startup]` trace milestones
for control creation, loading, MRU display, and secondary content. These are
diagnostics, not a startup-time guarantee; compare cold and warm VS launches,
including offline and large-repository scenarios, in the Performance Profiler.

![Start Screen screenshot](art/startscreen.png)

## Get to work

Open a recent solution, create a new project, open a project or folder,
clone a repo, or attach and reattach to a process - all from a single,
focused screen. The action bar puts every common workflow front and center
so you never have to dig through File or Debug menus again.

## Your recent projects, supercharged

Start Screen doesn't just list your recent files. It understands how you
work:

**Find anything instantly.** Type a few characters and the search filter
narrows your list in real time. Pin the projects you care about most so
they always float to the top.

**Preview before opening.** Select a recent workspace to show its repository
activity in the center pane. Double-click or press Enter to open it. Choose
**All repositories** to return to your combined activity. **Solutions only**
filters unpinned entries; pinned folders and projects remain visible, and
search still applies to them.

**See real repository activity.** Compact second-line summaries show repository-wide
open PR counts and direct personal review requests for supported GitHub and
Azure DevOps repositories. Counts are fetched independently of the limited
dashboard lists. Unsupported, signed-out, and unavailable states are shown
explicitly rather than as zero. Select a summary to open the scoped PR list.

**See your Git status at a glance.** Every repo shows its current branch,
ahead/behind counts, uncommitted changes, and the last commit time - all
loaded in the background so the UI stays snappy. Hover for the full
picture, or spot a detached HEAD before you accidentally commit to nowhere.

**Stay organized.** Projects are grouped by when you last touched them -
*Today*, *This week*, *This month* - so yesterday's prototype doesn't bury
this morning's deadline.

**Right-click for power moves.** Open the containing folder, launch a
terminal, pull the latest Git changes, copy the path, pin, unpin, or
remove - all from a context menu with keyboard shortcuts.

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

## YouTube video feed

The latest videos from the Visual Studio YouTube channel are shown in
the Videos tab of the News panel, right next to the Blogs tab. Each
video displays a thumbnail, title, and publish date - videos published
in the last three days get a "NEW" badge so you never miss fresh
content. A green dot appears on the Videos tab whenever new videos
arrive in a refresh, and clears once you view the tab.

The feed is cached locally and refreshes automatically every four hours.
Click the Refresh button at the top of the screen to force an update at
any time.

## Drag and drop

Drop a solution, project, or folder from File Explorer directly onto the
Start Screen to open it. You can also drag pinned items to reorder them -
a visual indicator shows exactly where the item will land.

## Dev Hub

The Dev Hub panel shows your open pull requests, assigned issues, and
recent builds from GitHub and Azure DevOps - right next to your project
list. Data loads in the background and is cached locally so the UI stays
responsive.

**Overview** opens by default and shows Copilot activity and other recent
changes, without a separate attention banner. Existing saved defaults are switched
to Overview once; the default-tab setting remains configurable afterward.
**Needs you** contains the full list of requested
reviews and authored PRs with failing checks or conflicts. Those actionable
PRs are not repeated in Overview's change list. Repository changes use provider timestamps and the last
Overview visit, not guesses about agent progress. Lists are bounded and are
not complete repository inventories.

PRs open in Visual Studio's native review experience when the installed Git
tooling supports it. **Open in Browser** remains available from the PR context
menu; unsupported providers use the browser. Native review failures are reported
instead of silently opening a browser. Git tooling is loaded on demand, so review
can be opened from the Start Screen before a repository is active.
Opening a review does not silently check out a branch.

An experimental, optional Copilot integration observes the installed VS harness
through its in-process MEF contracts. Overview includes running and recent local
conversations; Needs you includes sessions with unresolved permission requests or
questions, including requests from subagents. Clicking a conversation, or pressing
Enter on it, opens it in Copilot so requests are answered in Copilot's own UI. It does not
send prompts, resume sessions in another process, or answer requests automatically.
Conversation titles occupy a single line with an ellipsis; hover to see the full
title. The native Copilot icon identifies conversation cards. A compact,
theme-aware status badge and news-style date appear below the
title. Running uses the accent color, Waiting uses the warning color, and Idle
is muted. Conversations without a known live state show no state badge rather
than claiming to be idle. These signals do not describe work in
another Visual Studio instance or a separate CLI process.
Loading and error status appears beside the work-section heading.
Sessions appear as they are discovered, without waiting for the entire catalog.
Reopening the page reuses up to 100 conversation metadata entries while reconnecting.
Metadata is also saved locally under `%LOCALAPPDATA%\StartScreen\CopilotCache` after
discovery and when leaving the page. It contains conversation IDs, titles, working
directories, timestamps, and saved-conversation markers, not messages or live state.
On a new VS launch, cached metadata is loaded off the UI thread and bound before
Copilot service initialization, with a dispatcher yield to allow rendering first.
The first launch without a cache still depends on VS discovery. Cached entries show
no state badge and never retain old running or permission state. Completed discovery
replaces stale entries, including deleting history when the catalog is empty.
Cache errors are logged and surfaced beside the work heading without disabling
live discovery. Opening a cached entry requires the current backend to confirm
that the conversation still exists.
Workspace selection filters sessions by their reported working directory; sessions
whose directory is not yet known are not attributed to a selected workspace.

These are internal, version-sensitive contracts, not a supported Copilot SDK API.
The harness does not expose a force-relist operation. Refresh reconnects to its
current in-memory catalog; it cannot force saved history from other processes to
be re-listed, or obtain their live activity. Missing or incompatible contracts
produce an explicit unavailable status; discovery
in progress is not reported as an empty successful result. Refresh retries the
connection. Subscriptions are removed while the page is hidden or closed. Copilot
activity remains accessible without a GitHub/Azure DevOps dashboard connection.
Only sessions exposed by this VS instance's current backend are shown, not cloud
agents or agents in other processes. Chat files and open-session counts are never
treated as proof that an agent is running. No Copilot implementation assemblies are
shipped or required to build the extension.

Closing the Start Screen cancels pending Dev Hub requests without logging
normal cancellation as an error or caching canceled requests as empty data.
HTTP timeouts and other request failures are still logged for troubleshooting.

![Dev Hub](dev-hub.png)

### Scope to a repository

Right-click any pull request, issue, or build and choose **Scope to
&lt;repo&gt;** to filter all three Dev Hub tabs down to that single
repository. Selecting a recent workspace scopes the same view. While scoped,
choose **All repositories**, or right-click an item and choose **Show all
repositories** to clear the scope and see everything again.

The scope is not saved between sessions, so reopening the Start Screen
always starts with the full view.

### Custom search query (GitHub)

By default the Dev Hub shows issues and pull requests that "involve" your GitHub
account. Click the gear icon next to the Dev Hub header to enter a custom
GitHub search query. The extension prepends `is:issue` or `is:pr`
automatically, so you only need to provide the filtering part.

Use the `{login}` placeholder to reference the authenticated username.

**Example - show open issues across multiple orgs:**

```text
state:open archived:false sort:updated-desc user:madskristensen org:VsixCommunity org:ligershark
```

**Example - only issues assigned to you:**

```text
state:open assignee:{login}
```

**Example - issues in a single org:**

```text
state:open org:dotnet
```

Leave the field empty to restore the default behavior (`involves:{login}`).

The custom query applies to the Issues and Pull requests tabs. The
Builds tab always fetches from your recently pushed repositories and is
not affected by the query.

> The custom search query setting only applies to GitHub. Azure DevOps
> uses dedicated REST APIs for each repository and is not affected.

### Display options

Click the gear icon next to the Dev Hub header to open its settings, where
the **Display** section lets you tailor the layout:

- **Default tab** - choose whether Overview, Needs you, Issues, Pull requests, or Builds is
  selected when the Start Screen opens.
- **Sort issues and pull requests by** - order the lists by **Most recent
  first** (the default), **GitHub first**, or **Azure DevOps first**. The
  provider options group items by source, then fall back to most recent
  within each group. The Builds tab is always ordered most recent first.

### Hiding the Dev Hub

If you don't use the Dev Hub, you can hide it. Click the settings (gear)
button in the top-right of the Start Screen and uncheck **Show Dev Hub**.
The News section expands to use the freed-up space. Re-check the box at
any time to bring it back.

## Refresh, service health, and VS updates

The header groups Refresh with its update timestamp. This single Refresh button
updates repository activity, blog feeds, and videos; news tabs have no separate
refresh link or timestamp. Visual Studio service
health is shown below the action buttons and links to the public status page.
Unknown, stale, and failed checks are not displayed as healthy. Public service
health does not describe your account sign-in or repository CI status.

The work pane is capped at 560 pixels including its gutter. Extra screen width
goes to news and videos, which expand into multiple columns. At narrow widths,
news moves below the work pane.

A discreet update link uses Visual Studio's setup API and shows the available
version when supplied by VS. It opens the standard Visual Studio update flow;
there is no separate release-notes link. Tips and suggested-extension panels
have been removed to give workspace activity and news more room.

## Keyboard-first design

Every part of Start Screen is navigable without a mouse.

Open the Start Screen any time from **File > Start Screen** or press
**Ctrl+Shift+Alt+Backspace**.

### Recent solutions / folders

| Shortcut   | Action                 |
| ---------- | ---------------------- |
| Up / Down  | Select and preview     |
| Enter      | Open                   |
| Ctrl+Enter | Open in new instance   |
| O          | Open containing folder |
| T          | Open in terminal       |
| G          | Git pull               |
| Ctrl+C     | Copy path              |
| P          | Pin / Unpin            |
| Del        | Remove from list       |
| Alt+`      | Focus search box       |
| Right      | Jump to Dev Hub        |

### Dev Hub

| Shortcut  | Action               |
| --------- | -------------------- |
| Up / Down | Move between items   |
| Enter     | Review PR in VS; other items in browser |
| Ctrl+C    | Copy URL             |
| Left      | Jump to recent files |
| Right     | Jump to news feed    |

### News feed

| Shortcut            | Action                 |
| ------------------- | ---------------------- |
| Up / Down           | Move between rows      |
| Left / Right        | Move between columns   |
| Enter               | Open in browser        |
| Ctrl+C              | Copy URL               |
| Left (first column) | Jump to Dev Hub        |
| Right (last column) | Jump to YouTube videos |

### YouTube videos

| Shortcut  | Action             |
| --------- | ------------------ |
| Up / Down | Move between items |
| Enter     | Open in browser    |
| Ctrl+C    | Copy URL           |
| Left      | Jump to news feed  |

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
