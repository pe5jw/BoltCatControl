# BoltCatControl

**Moderne CAT control voor je transceiver — vervanger voor DXLab Commander**

BoltCatControl draait op de pc die direct aan de radio hangt. De app start zelf een lokale
[Hamlib](https://hamlib.github.io/) `rigctld` op de COM/USB-poort en praat daar via TCP mee.
Geen remote, geen com0com — puur lokaal.

```
[Transceiver]
      │
   COM/USB serieel
      │
  [rigctld lokaal]  ← gestart door BoltCatControl
      │ TCP :4532
  [BoltCatControl]
```

## Platform

| | |
|---|---|
| **Windows x64** | primair (desktop) |
| **Linux x64** | volledig ondersteund, zelfde codebase |
| **Stack** | .NET 10, Avalonia 11 (puur C#, geen AXAML) |
| **CAT** | Hamlib `rigctld` (meegeleverd, 600+ radio's) |

## Projectstructuur

```
BoltCatControl.slnx
src/
  BoltCat.Core/      CAT logica zonder UI (rigctld client, state, poller)
  BoltCatControl/    Avalonia desktop app
```

## Bouwen

```
dotnet build
dotnet run --project src/BoltCatControl
```

Release-build (self-contained):

```
dotnet publish src/BoltCatControl -r win-x64   --self-contained -c Release -o out/win-x64
dotnet publish src/BoltCatControl -r linux-x64 --self-contained -c Release -o out/linux-x64
```

## Releases

Elke push naar `main` wordt door GitHub Actions gebouwd voor Windows en Linux.
Een tag `v*` (bijv. `v0.1.0`) maakt een GitHub release met de zips:

```
git tag v0.1.0
git push origin v0.1.0
```

## Roadmap (MVP)

- [x] Project setup
- [ ] `BoltCat.Core` — `RigctldClient`, `RigState`, `RigPoller`
- [ ] rigctld auto-start (meegeleverde binary Windows + Linux)
- [ ] MVP GUI — VFO A/B, mode, PTT, A↔B / A→B / Split, band, S-meter, status
- [ ] JSON configuratie — COM poort, baud, Hamlib rig model, MIDI
- [ ] MIDI fase 1 — VFO encoder + PTT

73 de PE5JW
