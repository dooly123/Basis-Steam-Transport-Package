# Basis Steam Transport

Adds the `steam` network stack to Basis. Players connect to a Steam ID instead of an IP address,
and the traffic goes through Steam's relay network (Steam Datagram Relay), so nobody forwards ports
or shares their IP address. The Steam ID can be a player hosting from the client, or a dedicated
Basis Server logged in to Steam as a game server.

Basis does not include this package. It plugs into the transport hooks Basis already has
(`BasisNetworkStackRegistry`, `BasisTransportConfigStore`), so removing it removes Steam completely.
The **Basis Steam Lobby** package (`com.basis.steamlobby`) builds lobbies, invites and a menu panel
on top of it; this package works without it.

| Folder | Built into |
|---|---|
| `Runtime/Shared/` | Unity assembly `BasisSteamTransport`, and `BasisNetworkCore` on the server: the `steam` stack (a `NetManager` over Steam networking sockets), the address parser, the server-list probe and the Steam thread handoff |
| `Runtime/Unity/` | Unity assembly `BasisSteamTransport` only: Steam client start-up and the per-frame pump |
| `Server~/BasisNetworkCore/` | the server only: the package entry point and the Steam game server (its own thread, login, status) |
| `Server~/BasisServerTests/` | the server test project |
| `Plugins/Facepunch.Steamworks/` | Facepunch.Steamworks 2.5.2 for Unity and the Steamworks API binaries, used by Unity and the server |
| `Server~/Plugins/` | Facepunch.Steamworks 2.5.2 built for .NET Standard 2.1, used by the server (see `THIRD-PARTY-NOTICES.md`) |

## Installing in Unity

Add it to `Basis/Packages/manifest.json`. `file:` paths are relative to `Basis/Packages`, so with
this folder in `Documents` and Basis in `Documents/Github/Basis`:

```json
"com.basis.transport.steam": "file:../../../../Steam Transport Package"
```

Run **Basis > Build > Update Link XML** after installing so IL2CPP builds keep the package's
assemblies. Windows x64, Linux x64 and macOS only; the assembly and binaries are excluded from
Android, iOS and web builds.

## Installing on a Basis Server

The package is a Basis server package with prebuilt libraries, so it needs a Basis Package Manager
that supports `references` and `natives` in `basisServer`:

```
basispm --project <Basis repository> server-install <git URL, or the path to this folder>
```

A Docker image build only sees `Basis Server/`, so install it by git URL on servers built as images.
Then turn it on in `config/config.xml` (or the `NetworkStackId` environment variable):

```xml
<NetworkStackId>litenetlib,steam</NetworkStackId>
```

On start the server logs in to Steam as a game server and prints the address players use:

```
Steam game server logged in as 90250000000000001. Players join with steam:90250000000000001
```

`/health` lists the same Steam ID under `transports`, with whether it is logged in and listening.
Players add that address in the Servers panel together with the server's password.

### Steam ID: anonymous or with a token

By default the server logs in anonymously, which works but gives it a new Steam ID every time it
starts, so the address changes. For a fixed address, create a Game Server Login Token for your app
id at <https://steamcommunity.com/dev/managegameservers> and put it in
`config/transports/steam.xml`:

```xml
<GameServerToken>YOUR_TOKEN</GameServerToken>
```

The server and its players must use the same app id: `AppId` in `steam.xml`, else the `SteamAppId`
environment variable, else `steam_appid.txt` next to the server, else 3157090 (Basis Labs).

### Steam's game server files

The Steamworks API (`steam_api64.dll`, `libsteam_api.so`) is copied next to the server by the
package. Running as a game server also needs Steam's client library, which is not one of the
Steamworks SDK redistributables, so the package does not ship it. It comes from SteamCMD's
"Steamworks SDK Redist" (app 1007):

```
steamcmd +force_install_dir ./steamworks +login anonymous +app_update 1007 +quit
```

- **Linux and Docker:** copy `steamworks/linux64/steamclient.so` to `~/.steam/sdk64/steamclient.so`
  for the user the server runs as (in the official image, `/root/.steam/sdk64/`).
- **Windows:** nothing more when the Steam client is installed. Otherwise copy `steamclient64.dll`,
  `tier0_s64.dll` and `vstdlib_s64.dll` from `steamworks/` next to the server.

Without them the server logs that the Steam game server could not start, and the `steam` transport
does not listen.

### Building for another operating system

The Steamworks libraries are picked for the platform being built. When publishing for a different
operating system than the machine you build on, pass the target through:
`dotnet publish -r linux-x64 -p:BasisServerPackageRid=linux-x64`. Docker builds on Linux need
nothing extra.

## Using it from the client without the lobby package

- **Host:** in the Servers panel, host with **LiteNetLib + Steam**. Your own client stays on
  LiteNetLib locally and Steam players come in through the relay. Hosting with **Steam** alone also
  works, but then your own client reaches your server through the relay too. The log prints your
  SteamID64 when the Steam listener opens.
- **Join:** add a server whose address is `steam:<SteamID64>` (or `steam:<id>:<virtual port>`)
  with the server's password. Addresses that start with `steam:` pick this stack on their own.
- **Server list:** the Servers panel queries Steam servers through the relay, so they show their
  name, player count and ping like any other server.

## Steam App ID and start-up in Unity

The App ID is picked in this order: `BasisSteamClient.AppIdOverride` if code sets it, the
`SteamAppId` environment variable (Steam sets it when it launches the game), `steam_appid.txt` in
the working directory, then `3157090`, Basis Labs (the Basis app on Steam), which is also what the
editor and local builds use. To test with Valve's Spacewar app instead, put `480` in
`steam_appid.txt`.

Steam starts on its own when the game was launched by Steam (or with `+connect_lobby`). Otherwise it
starts the first time something needs it: a Steam connection, a Steam listener, or a probe.

## How it works

- Messages travel over Steam networking sockets, framed with a 3-byte header (packet type, delivery
  method, Basis channel). Unreliable and sequenced traffic (voice, avatar poses) goes out
  immediately on its own lane; reliable bulk channels (scene, resources, content share, avatar
  bundles) share a lower-weight lane, so a large transfer cannot starve chat, ownership and other
  control messages.
- The connect handshake carries Basis's normal connection request. Rejections and kicks carry the
  server's reason to the client, like LiteNetLib, and a kick raises the disconnect on the server
  so the player is cleaned up.
- Player ids come from the server's shared allocator, so a server running `litenetlib,steam` never
  hands out the same id twice.
- Steam peers report a pseudo IPv6 address derived from their SteamID64 (`fd53:7465:616d::/64`), so
  IP bans become per-account bans instead of banning every Steam player at once.
- Pings every second give `RoundTripTime`, `TimeSinceLastPacket` (milliseconds, which the connection
  watchdog relies on) and the server clock offset used for synchronised playback.
- `GetPacketsCountInQueue` converts Steam's pending bytes into MTU-sized packets, so the server's
  per-peer send budget keeps the same meaning as on LiteNetLib.
- A local disconnect raises `DisconnectPeerCalled`, which `BasisConnectionService` waits for when
  switching servers.
- All Steam calls run on one thread: Unity's main thread, driven by the per-frame pump, or the
  dedicated server's own "Basis Steam" thread, which starts only when a server lists the `steam`
  stack. Work from other threads (opening the relay listener, closing peers) is handed to it.
- The server-list probe opens a short relay connection and sends Basis's standard server info
  query; the server answers through its normal info query handler.
- Settings live in `config/transports/steam.xml` on a server (`BasisSteamTransportConfig`, stack
  id `steam`), written on first start with a comment on every field: app id, game server token,
  virtual port, MTU, timeouts, send buffer, ping interval and the receive budget per pass.

## Framework changes it relies on

- `--connection=` (relaunch, command line, editor play-mode restart) is written with the connected
  transport's parser and read back through the stack registry's address matchers, so relaunching
  during a Steam session (for example after switching graphics API) reconnects over Steam.
- `BasisNetworkServerRunner.Stop` clears the previous session's persistent resources and per-player
  saved state, so hosting again in the same run starts clean.

## Credits

The Steam relay transport was first written by
[saint4626 (Momonth)](https://github.com/saint4626) in BasisVR/Basis pull requests #827 and #818.
This package reworks it as a standalone package on top of the current Basis transport hooks.
