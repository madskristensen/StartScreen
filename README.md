[marketplace]: <https://marketplace.visualstudio.com/items?itemName=MadsKristensen.StartScreen>
[vsixgallery]: <https://www.vsixgallery.com/extension/StartScreen.2d76ac3d-7ff2-47e1-82d8-e507cf765bbe>
[repo]: <https://github.com/madskristensen/StartScreen>

# Start Screen for Visual Studio

[![Build](https://github.com/madskristensen/StartScreen/actions/workflows/build.yaml/badge.svg)](https://github.com/madskristensen/StartScreen/actions/workflows/build.yaml)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/madskristensen)](https://github.com/sponsors/madskristensen)

**Less hunting. More coding.**

Your projects, pull requests, Copilot conversations, and developer news - together in a start screen that feels right at home in Visual Studio.

Pick up where you left off, see what needs your attention, and get back to building.

Download from the [Visual Studio Marketplace][marketplace] or get the latest [CI build][vsixgallery].

> **Requires Visual Studio 2022** or later. Free and open source.

![Start Screen brings recent projects, developer activity, and news into one Visual Studio dashboard](art/startscreen.png)

## Start your next coding session here

| Instead of... | Start Screen lets you... |
| --- | --- |
| Searching for yesterday's solution | Find it as you type, or keep it pinned at the top |
| Opening repositories just to check their state | See branches, local changes, and ahead/behind counts before opening |
| Switching between browser tabs to find your next task | Bring pull requests, issues, and builds into your IDE |
| Hunting for the Copilot chat you were using | Return to recent conversations from your workspace dashboard |
| Missing the latest Visual Studio updates | Browse blogs, videos, and What's New alongside your work |

## Your projects, ready when you are

Make your recent-project list a launchpad, not a filing cabinet.

- **Find and pin your favorites.** Search your saved history, pin frequently used workspaces, and drag pinned items into your preferred order.
- **Know before you open.** Check the current Git branch, uncommitted changes, ahead/behind counts, and last commit time at a glance.
- **Preview the work.** Select a workspace to see its repository activity. Double-click or press Enter when you're ready to open it.
- **Take the shortcut.** Open a terminal or containing folder, pull Git changes, copy a path, or launch a new Visual Studio instance from the context menu.
- **Drop it and go.** Drag a solution, project, or folder from File Explorer onto Start Screen to open it.

Need something new? The action bar puts **Create a new project**, **Open**, **Clone**, and **Attach to process** within reach.

## See what needs you - before diving into code

**Dev Hub** brings activity from **GitHub, Azure DevOps, and Bitbucket** next to your projects. Spend less time finding the work and more time moving it forward.

**My work** gathers your pull requests, created or assigned issues and tasks, builds, and Copilot sessions. The **Attention** feed highlights known actions such as requested reviews, failing checks on your PRs, and assigned work.

**Team activity** keeps your teammates' pull requests, issues, and repository builds in view. Select a recent workspace to focus on one repository, or choose **All repositories** for the bigger picture.

Review pull requests in Visual Studio where supported, or open them on the web. Review and check status help you decide where to start; **Ready to merge** appears only when the provider supplies verified readiness.

![Dev Hub showing personal work and repository activity inside Visual Studio](art/devhub.png)

Repository activity requires a connected provider account. Available activity and actions vary by provider and permissions; these are recent activity feeds, not exhaustive repository inventories.

## Keep Copilot in the loop

Your next step can start right from the work item.

- **Fix with Copilot** starts a local Visual Studio chat for an issue or task.
- **Investigate with Copilot** brings a failed build's context into a new chat.
- **Continue in Copilot** returns to a conversation started for that item, without sending the same prompt again.
- **Request Copilot Review** requests a review on supported GitHub.com pull requests, with confirmation.

The Copilot tab also brings recent Visual Studio and CLI conversations, plus GitHub cloud-agent tasks, into view. Open a conversation in its originating app; supported live Visual Studio sessions can surface questions or permission requests in Attention.

![Copilot](art/copilot.png)

Copilot integration is optional and experimental, and depends on compatible installed Visual Studio Copilot components. CLI resume requires the Copilot CLI on PATH. Cloud tasks and review requests require the relevant GitHub permissions and Copilot access; review requests may consume Copilot usage. Start Screen never answers permission requests automatically.

## Stay current without leaving your IDE

The **News & updates** sidebar gives you more than headlines:

- **Blogs** - curated developer news from sources including the Visual Studio and .NET blogs, with support for your own RSS or Atom feeds.
- **Videos** - recent videos from the Visual Studio YouTube channel.
- **What's New** - features shipped with your installed Visual Studio version.
- **Feedback** - Developer Community tickets you created or upvoted, with their latest status, using your signed-in Visual Studio account.

Browse when you want to. Background loading and local caches keep available content close at hand; Feedback and Videos load on demand.

![News](art/news.png)

## Make it feel like your Visual Studio

Start Screen follows your IDE theme and font settings, including High Contrast. Native context menus and familiar controls keep it feeling like part of Visual Studio, not a separate app.

Prefer a simpler dashboard? **Hide Dev Hub** and give news more space. Customize provider connections, activity sorting, and GitHub team queries. Your pinned projects and feed preferences are saved automatically.

**Keyboard-friendly from the first click:**

| Shortcut | Action |
| --- | --- |
| Ctrl+Shift+Alt+Backspace | Open Start Screen |
| Alt+` | Focus recent-project search |
| Arrow keys | Navigate projects, activity, and news |
| Enter | Open the selected item |
| Ctrl+Enter | Open a selected recent workspace in a new VS instance |
| P | Pin or unpin a selected recent workspace |
| Shift+F10 | Open the focused item's context menu |

## Get started in three steps

1. **[Install from the Visual Studio Marketplace][marketplace].** You can also search for **Start Screen** in Visual Studio's Extension Manager.
2. **Restart Visual Studio** to finish installation.
3. **Open File > Start Screen**, use the **Home** button on the Standard toolbar, or press **Ctrl+Shift+Alt+Backspace**. Pin your favorite workspace and connect the providers you use in Dev Hub settings.

Start Screen appears when no solution or folder is open and returns after you close a solution. On first use, it disables the built-in Start Window so you don't have two competing launch experiences.

**Your recent projects and news work without connecting a repository provider.** Add Dev Hub and Copilot when you're ready.

## Contributing

This is a passion project, and contributions are welcome!

- **Found a bug?** [Open an issue](https://github.com/madskristensen/StartScreen/issues/new)
- **Have an idea?** [Suggest a feature](https://github.com/madskristensen/StartScreen/issues/new)
- **Want to contribute?** Pull requests are always welcome. [Explore the source code][repo] to get started.
