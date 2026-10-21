[← Back to the index](README.md)

# Installation

Trading Hub is built on your own Mac from the repository source. A script assembles the «Trading Hub.app» bundle, signs
it and copies it to Applications. The first run takes a few minutes because it downloads the packages it needs.

## Requirements

| What | Detail |
|---|---|
| Mac | Apple Silicon (M1 or later). For now the app is built only for `arm64`. |
| macOS | 12 or newer. |
| .NET 10 SDK | Only needed to build. It is installed in `~/.dotnet`, with no administrator password. |
| Internet | The first build downloads the NuGet packages. |
| Claude Code | Optional. Only for the AI features (see [Claude Code](#claude-code-optional)). |

The installed app is self-contained: you don't need .NET installed to use it.

To check your Mac, open Terminal and run:

```bash
uname -m                 # has to say arm64
sw_vers -productVersion  # has to be 12 or higher
```

If `uname -m` says `x86_64`, your Mac is Intel and is not supported for now.

## Option 1: ask Claude Code

If you use Claude Code, it can do the install for you. The repository ships a `CLAUDE.md` file with the steps it has to
follow.

1. Install Claude Code and sign in with your Claude account.
2. Open a Terminal in the repository folder and run `claude`.
3. Write to it: «Install Trading Hub».

Claude Code will check that your Mac is compatible, install the .NET 10 SDK in `~/.dotnet` if it is missing, run
`./build-app.sh --install`, verify that the app finds your Claude Code and open it. It will not ask you for personal
data or keys: they are not needed to install. If Applications already holds another app called «Trading Hub», it has to
ask you before replacing it.

## Option 2: manual install

1. Check whether you already have the SDK:
   ```bash
   ~/.dotnet/dotnet --list-sdks
   ```
   If a line starting with `10.` appears, jump to step 3.
2. Install the .NET 10 SDK in `~/.dotnet`:
   ```bash
   curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
   bash /tmp/dotnet-install.sh --channel 10.0
   ```
   Then run `~/.dotnet/dotnet --list-sdks` again to confirm.
3. From the repository folder, build and install:
   ```bash
   ./build-app.sh --install
   ```
4. Open the app from Applications or with:
   ```bash
   open "/Applications/Trading Hub.app"
   ```

You don't need to add `~/.dotnet` to the `PATH`: the script adds it itself while it runs.

### What `build-app.sh` does

- Publishes the app in Release mode for `osx-arm64`, self-contained.
- Assembles the bundle in a temporary folder, away from the Desktop: iCloud adds attributes to files that break the
  signature.
- Signs the app and leaves a copy in `dist/Trading Hub.app`, inside the repository.
- With `--install`, it deletes `/Applications/Trading Hub.app` if it exists, copies the new one and verifies the
  signature. If all went well, it finishes with «Installed in /Applications/Trading Hub.app».

Without `--install` it only builds `dist/Trading Hub.app` and does not touch Applications.

## Ad hoc signing

If you don't have a developer certificate, the script signs the app «ad hoc» (you will see it as «Signed ad hoc»).
On the Mac where you built it, it opens without warnings.

- If you copy the app to another Mac, open it the first time with right click → **Open**.
- If you have a signing certificate, create a `build.local.env` file in the repository folder. The script reads it on
  its own and git ignores it:
  ```bash
  SIGN_IDENTITY="certificate name"
  BUNDLE_ID="com.your-domain.tradinghub"
  ```
  `SIGN_IDENTITY` has to match a name from `security find-identity -v -p codesigning`. If it is not found, it signs ad
  hoc anyway. With a fixed certificate, macOS remembers the permissions you gave the app across updates.

## Claude Code (optional)

Trading Hub does not use Claude API keys: it runs the Claude Code CLI you already have installed, with your session.
What Claude does counts against your normal usage limit. It is used in two places:

- **Backtesting → Claude**: a chat that writes and fixes strategies.
- **Live → Portfolios**: the «Ask Claude for analysis» button, which proposes size changes that you approve.

Without Claude Code the rest of the platform works just the same (charts, trading, data, backtesting and live
strategies).

The app looks for the CLI in these places and uses the newest version it finds:

- `~/.local/bin/claude`, `~/.claude/local/claude`, `/opt/homebrew/bin/claude`, `/usr/local/bin/claude`,
  `~/.npm-global/bin/claude`;
- the folders in the `PATH`;
- the copy that ships with the Claude desktop app.

If `command -v claude` points somewhere else, create a link:

```bash
mkdir -p ~/.local/bin && ln -sf "$(command -v claude)" ~/.local/bin/claude
```

You can also point at a specific CLI with the `TRADINGHUB_CLAUDE_CLI` environment variable. To sign in, run `claude` in
Terminal and use `/login`. If Claude Code asks you to sign in while you are using the Backtesting chat, the app shows
the «Sign in to Claude Code» button.

## Where your data is stored

| Place | What is there |
|---|---|
| `~/Library/Application Support/Trading Hub` | Settings, connections (without secrets), workspace, trade history, drawings, templates, account history, backtests and live strategies. |
| `~/Documents/Trading Hub/Strategies` | Your C# strategies. Inside, `Reports` holds the Markdown reports. |
| `~/Documents/Trading Hub/Indicators` | Your C# indicators. |
| macOS Keychain | Passwords, secrets and tokens for the connections, under the name «Trading Hub». |

Inside the data folder:

| File or folder | Contents |
|---|---|
| `settings.json` | General settings, shortcuts, DOM and stop/target templates. |
| `connections.json` | The configured connections, without passwords or secrets. |
| `workspace.json` | Open tabs, charts, layouts and panels. |
| `Ticks/` | Trade history: one file per connection, instrument and day. |
| `Klines/` | Candles downloaded from the connections (stored by month), to load long history quickly. |
| `Drawings/` | Drawings for each instrument. |
| `ChartTemplates/`, `IndicatorTemplates/`, `FootprintTemplates/` | Chart, indicator and footprint templates. |
| `Accounts/` | Executions for each account (the history in the Account section). |
| `Backtests/` and `Live/` | Backtesting runs and the state of live strategies. |
| `crash.log` | Unexpected errors, in case you need to report a problem. |

The first time you open the app the `Documents` folders are created. `Indicators` gets an example
(`DeltaPercentExample.cs`) and example strategies are written into `Strategies` when the folder is empty.

To open the folders from the app: **File → Open data folder**, **Code → Open indicators folder**,
**Code → Open strategies folder**, or the «Open» buttons in **Settings → Data**.

## Updating

1. Quit Trading Hub completely with ⌘Q or with «Quit Trading Hub» from its menu-bar icon. Closing the window is not
   enough: out of the box, the app keeps running in the top right.
2. Pull the new version:
   ```bash
   git pull
   ```
   If you downloaded the code as a ZIP, download the new one and replace the folder (keep your `build.local.env` if you
   had one).
3. Build and install:
   ```bash
   ./build-app.sh --install
   ```

You can also open the folder in Claude Code and ask it to «update Trading Hub». Your data, connections, keys and
strategies are left untouched because they live outside the repository folder.

## Uninstalling

1. Quit Trading Hub completely (⌘Q or «Quit Trading Hub»).
2. Before deleting anything, if you want to remove the keys from the Keychain from inside the app: in
   **Connections**, each connection's «Delete» button removes its secrets. The built-in connections cannot always be
   deleted; their secrets are removed in step 4.
3. Delete the app:
   ```bash
   rm -rf "/Applications/Trading Hub.app"
   ```
4. Delete any secrets left in the Keychain: open **Keychain Access**, search for «Trading Hub» and delete those
   items. From Terminal, this command deletes them one by one until none are left:
   ```bash
   while security delete-generic-password -s "Trading Hub" >/dev/null 2>&1; do :; done
   ```
5. If you are not going to use it again, delete the data folder (history, accounts, settings). This cannot be undone:
   ```bash
   rm -rf ~/Library/Application\ Support/Trading\ Hub
   ```
6. Your strategies and indicators are in `~/Documents/Trading Hub`. Delete them only if you don't want to keep them.
7. Optional: the repository folder (with `dist/`), the SDK in `~/.dotnet` if no other program uses it, and the
   `~/.local/bin/claude` link if you created it just for Trading Hub.

## If something fails

- **`dotnet` is not found**: the script uses `~/.dotnet` only. Check that `~/.dotnet/dotnet --list-sdks` shows a
  version 10.
- **The build stops with an error**: read the first error Terminal shows. Check that you have internet (the first run
  downloads packages) and that the installed SDK is 10.
- **The app closes or something stops responding**: look at `crash.log` in the data folder.
