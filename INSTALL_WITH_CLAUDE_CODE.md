# Installing Trading Hub with Claude Code

The easiest way to install Trading Hub is to ask your own Claude Code: it builds the app on your Mac, installs it in
Applications and leaves it connected to your Claude subscription. You don't need to know how to program.

## Before you start

- An Apple Silicon Mac (M1 or newer) with macOS 12 or newer.
- [Claude Code](https://claude.com/claude-code) installed and signed in (Pro or Max plan).

## Steps

1. Download the project. With Git:
   ```bash
   git clone https://github.com/ralphtradesz/trading-hub.git
   ```
   or from GitHub: **Code → Download ZIP** and unzip it.
2. Open a Terminal in that folder and start Claude Code:
   ```bash
   cd trading-hub
   claude
   ```
3. Copy and paste this message:

---

```
Install Trading Hub on my Mac following the instructions in CLAUDE.md in this folder:
check that my Mac is compatible, install the .NET 10 SDK if it is missing (in ~/.dotnet, without sudo),
build and install the app with ./build-app.sh --install, check that the app finds my Claude Code
(create the link in ~/.local/bin if needed), open it and tell me in a few lines how to get started.
If Applications already holds another app called «Trading Hub», ask me before replacing it.
```

---

## What will happen

- Claude Code checks your Mac (Apple Silicon, macOS 12 or newer).
- If you don't have the .NET 10 SDK, it downloads it from Microsoft's official site into `~/.dotnet` (it does not touch
  the rest of the system and does not ask for your password).
- It builds Trading Hub (a few minutes the first time) and installs it in `/Applications/Trading Hub.app`.
- It checks that the app finds your Claude Code: since it uses your own installation and your own session, the AI
  features (the Backtesting chat, the lab and the Live risk agent) end up connected to **your** subscription, with no
  API keys.
- It opens the app. It starts with a simulated account: nothing trades real money until you enable it yourself in
  Connections.

## Updating

Download the new version (`git pull` or the new ZIP) and ask Claude Code: «update Trading Hub». Your data, your
connections and your strategies are left untouched (they live outside this folder).

## If something goes wrong

Ask Claude Code to show you the error and fix it. The most common problems:

- **The app can't find Claude Code**: ask it to create the link in `~/.local/bin/claude`.
- **macOS says it can't verify the app**: the app is signed "ad hoc" on your Mac when you build it; if you copied it
  from another computer, build it again on yours.
- **The build fails**: confirm that `~/.dotnet/dotnet --list-sdks` shows a version 10.

More details in [docs/installation.md](docs/installation.md).
