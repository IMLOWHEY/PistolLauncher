# PistolLauncher

PistolLauncher is a Windows launcher for managing separate Minecraft instances, loaders, mods, resource packs, and shaders.

## Features

- Create isolated Vanilla, Fabric, Quilt, Forge, and NeoForge instances. Loader installers are downloaded from their official services; availability depends on the selected Minecraft version.
- Browse and install mods, resource packs, shaders, and modpacks from Modrinth and CurseForge. CurseForge requires your own API key.
- Sign in with an offline profile or Ely.by. Microsoft sign-in is not available yet.
- Ely.by profiles use authlib-injector for Minecraft authentication. The launcher downloads the injector automatically when needed.
- Skin support depends on the account type and instance:
  - Ely.by skins work in Vanilla and modded instances through Ely.by authentication.
  - Microsoft skins will work with Microsoft authentication once Microsoft sign-in is implemented.
  - Offline profile skins need CustomSkinLoader, which the launcher installs into Fabric, Quilt, Forge, or NeoForge instances. The local PNG is stored in that instance.
  - Offline profile skins do not work in an unmodified Vanilla instance. The launcher starts Vanilla without the local PNG; supporting this would require a custom Minecraft client patch or a compatible client-side mod.
- Browse Minecraft files, manage instance folders, and choose Java memory and storage paths.
- Automatically find the Java version required by Minecraft or download Eclipse Temurin JRE when it is missing.

## Requirements

- Windows 10/11, x64.
- .NET 9 Desktop Runtime.
- Internet access for downloading Minecraft, Java, loaders, and catalog content.

## Build from source

Open `PistolLauncher/PistolLauncher.csproj` in Visual Studio with the .NET desktop development workload, or build it with the .NET 9 SDK:

```powershell
dotnet publish PistolLauncher/PistolLauncher.csproj -c Release -r win-x64 --self-contained false
```

The published application is created under `PistolLauncher/bin/Release/net9.0-windows/win-x64/publish/`.

## Notes

- Ely.by skin appearance in multiplayer depends on the server accepting Ely.by authentication and skins. Other players may also need a compatible skin setup to see them.
- Offline local skins are client-side; other players will not see them automatically.
- Some Minecraft versions may not have a release of every loader. The launcher reports when the official service has no compatible installer.
- Java logs are saved inside each instance under `logs/pistol-launcher.log`.
