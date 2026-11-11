# 02 — Run the server yourself

The offline APK already runs its own copy of the server, so you do **not** need this to play. This is
for development, testing, or modding — running the server as a normal program on your PC (or any
machine) and watching its logs.

## What the server is
`server/` is a .NET server that speaks Girl Cafe Gun's protocol. It has four projects:
- `SssjServer.App` — the entry point / command-line host.
- `SssjServer.Core` — the network layer (HTTP bootstrap, the gate TCP server, session 3DES).
- `SssjServer.Game` — all the game logic and the config-table loaders.
- `SssjServer.Protocol` — the wire protocol types.

It reads the game config from a **setting directory** (`server/setting/`, the EN v1.0.9 tables) and
writes player saves to a **data directory** you choose.

## Prerequisites
- **.NET 10 SDK** — https://dotnet.microsoft.com/download (the `dotnet` command on your PATH).
- That's it for running on a PC. (No database; saves are JSON files on disk.)

## Build
```bash
cd server
dotnet build -c Release SssjServer.sln
```
To run it on a different machine as a self-contained binary (no .NET needed there), publish for that
machine's runtime, e.g. Windows:
```bash
dotnet publish src/SssjServer.App/SssjServer.App.csproj -c Release -r win-x64 --self-contained true
```
(For the Android build used inside the APK the runtime id is `linux-bionic-arm64`; see
[03-BUILD-APK.md](03-BUILD-APK.md).)

## Run
From `server/`:
```bash
dotnet run -c Release --project src/SssjServer.App -- \
  --setting-dir setting \
  --data-dir    data \
  --http-port   9080 \
  --gate-port   9600 \
  --gate-host   127.0.0.1 \
  --gm-accounts none \
  --skip-tutorial false \
  --grant-starter  true \
  --all-events-open true
```
You should see it load the config tables and then:
```
[http] bootstrap listening 0.0.0.0:9080
[gateway] listening 0.0.0.0:9600 (advertises client address 127.0.0.1:9600)
```

### What the flags do
| Flag | Meaning |
|------|---------|
| `--setting-dir` | folder with the game config tables (use `setting/`) |
| `--data-dir` | where player saves are written (created if missing) |
| `--http-port` | the bootstrap/serverlist HTTP port the client checks first (9080) |
| `--gate-port` | the gameplay gate TCP port (9600) |
| `--gate-host` | the address the gate advertises back to the client (`127.0.0.1` for same-machine) |
| `--gm-accounts` | `none` = no GM accounts; everyone is a normal player |
| `--skip-tutorial` | `false` = new accounts play the tutorial; `true` = skip straight to the city |
| `--grant-starter` | `true` = give new accounts the starter squad/resources |
| `--all-events-open` | `true` = all events/banners available |

Stop it with Ctrl-C. Saves persist in `--data-dir` between runs.

## Connecting a client to *your* server
The client normally talks to the official servers over HTTPS. To make a client use your local server
instead you have to point it at `http://127.0.0.1:9080` and take the offline-account path. Two ways:

1. **Easiest — use the offline APK.** It already does this, but it starts its *own* embedded server, so
   it will not use the one you launched here. Good for playing, not for watching your server's logs.

2. **Dev setup — stock client + a launcher.** Run a stock client on a rooted device, forward the ports
   to your PC server, and patch the client in memory to use `http://127.0.0.1` with the offline path:
   ```bash
   adb reverse tcp:9080 tcp:9080
   adb reverse tcp:9600 tcp:9600
   ```
   then run the memory-patch launcher (`client/launch_gcg.ps1` in the full project) which flips the
   client's `disablegm` flag, sets its platform to `selftest`, and rewrites the server URL to
   `http://127.0.0.1:09080`. That launcher is device-specific (it uses fixed il2cpp offsets for this
   build) — it is the same logic the APK's shim performs in-process; see
   [03-BUILD-APK.md](03-BUILD-APK.md) and the project's `client/` folder.

For most people: run the server as above to read logs / test changes, and use the APK to actually play.

## About the config in `setting/`
These are the EN v1.0.9 tables (items, cards, gacha pools, chapters, events, …). The server reads them
read-only. Replacing them with a different game version's tables will change what the server serves;
the included tables match the client version in the APK (10009 / v1.0.9).
