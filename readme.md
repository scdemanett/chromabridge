# ChromaBridge

ChromaBridge makes RGB hardware from other brands follow Razer Chroma. Lighting effects
from Razer Synapse, and from games and apps that support Chroma, show up on your
motherboard, memory, sound card and other devices.

It is an early release. Expect rough edges.

## What it drives

| Provider | Devices | Needs |
|---|---|---|
| ASUS Aura | Motherboard lighting and addressable headers | Armoury Crate |
| Corsair iCUE | Whatever iCUE lists, for example memory | iCUE with its SDK turned on |
| OpenRGB | Devices [OpenRGB](https://openrgb.org) supports that have no provider of their own | Nothing; ChromaBridge downloads and runs OpenRGB for you |

## Requirements

- Windows 10 or 11, 64-bit
- Razer Synapse with Chroma Connect turned on
- Administrator rights. ASUS only reports its devices to an administrator, so
  ChromaBridge's background helper runs elevated.

## Install

Download the latest `ChromaBridge-x.y.z-setup.exe` from
[Releases](https://github.com/scdemanett/chromabridge/releases) and run it. ChromaBridge
starts with Windows and lives in the system tray.

After installing:

1. **Synapse.** Open Chroma Connect. ChromaBridge appears as "Chroma Control - ASUS",
   "Chroma Control - Corsair" and, for OpenRGB, "Chroma Control - Gigabyte". Those names
   belong to the Razer app IDs ChromaBridge uses and cannot be changed by ChromaBridge.
2. **Chroma Studio.** A new entry stays dark until its tile is part of an effect layer.
   Select the tile's lights in Chroma Studio and apply your layer to them.
3. **Armoury Crate.** In Aura Sync's in-game lighting order, move `ChromaBridge.Host.exe`
   to the top and confirm.

## Using it

Open ChromaBridge from the tray icon. For each provider you can:

- turn the provider on or off
- leave individual devices alone
- choose how the five Chroma Link colors are laid out on each device
- adjust gamma, saturation and brightness

Lighting keeps working when the window is closed.

## Updates

ChromaBridge checks GitHub for a new release once a day and tells you when there is one.
Nothing is downloaded or installed until you click Install. Before running an installer,
ChromaBridge verifies that it is signed by the ChromaBridge publisher.

## Good to know

- OpenRGB is started without administrator rights and without its low-level bus driver,
  and it is set to leave ASUS, Corsair and Razer devices alone so it does not compete with
  their own software.
- Some lights can only be one color. The GeForce RTX 5090 Founders Edition, for example,
  can only be switched on or off.

## Building

Needs the .NET 10 SDK on Windows.

```
tools\fetch-icue-sdk.ps1
dotnet build src\ChromaBridge.slnx
```

`tools\build-installer.ps1` builds the installer and needs Inno Setup 6.6 or later.

## Credits and license

ChromaBridge began as a fork of [Chroma Control](https://github.com/ChromaControl/ChromaControl)
by the Chroma Control Contributors and has since been rewritten. It is released under the
[MIT license](LICENSE.txt).

ChromaBridge is not affiliated with or endorsed by Razer, ASUS, Corsair, Creative, NVIDIA
or the OpenRGB project. All product names are trademarks of their owners.
