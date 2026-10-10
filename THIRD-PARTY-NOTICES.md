# Third-party notices

## Facepunch.Steamworks 2.5.2

`Plugins/Facepunch.Steamworks/Facepunch.Steamworks.Win64.dll` and
`Plugins/Facepunch.Steamworks/Facepunch.Steamworks.Posix.dll` are the unmodified Release builds
from the official 2.5.2 release (https://github.com/Facepunch/Facepunch.Steamworks/releases/tag/2.5.2).
`Server~/Plugins/Facepunch.Steamworks.Win64.dll` and `Server~/Plugins/Facepunch.Steamworks.Posix.dll`
are the unmodified .NET Standard 2.1 Release builds from the same release, used by the Basis Server.
MIT licensed, Copyright (c) 2016 Facepunch Studios LTD. The full license text is in
`Plugins/Facepunch.Steamworks/LICENSE.txt`.

## Steamworks API redistributables (Steamworks SDK 1.61)

`Plugins/Facepunch.Steamworks/redistributable_bin/` holds `steam_api64.dll` (Windows),
`libsteam_api.so` (Linux) and `libsteam_api.dylib` (macOS), as shipped in the same Facepunch
release. They are Valve's Steamworks SDK redistributables, distributed under the Steamworks SDK
Access Agreement (https://partner.steamgames.com/documentation/sdk_access_agreement), not under
MIT. They may be shipped with an application that uses the Steamworks API.

This is why the Steam integration is a separate package: Basis itself stays free of proprietary
binaries, and only projects that install this package carry them.
