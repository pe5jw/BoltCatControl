# BoltCatControl

**Moderne CAT control voor je transceiver**

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
tests/
  BoltCat.Core.Tests/  tests met een nep-rigctld (draaien in GitHub Actions)
  BoltCatControl.UiTests/  GUI headless renderen → screenshots (CI artifact)
tools/
  BoltCat.Test/        console testprogramma voor een echte radio
```

## Testen met een radio

`BoltCat.Test` zit in de build-zips (`BoltCat.Test-…-win-x64.zip`), met rigctld
(Hamlib) erbij in de map `hamlib`. Je hoeft niets apart te installeren:

```
BoltCat.Test --rig ft-897d --com COM3 --baud 4800
BoltCat.Test --list 897                   radio's opzoeken
```

`--baud` moet gelijk zijn aan de CAT RATE in het menu van de radio. Draait er al
een rigctld op poort 4532, dan wordt die gebruikt. Je ziet live frequentie, mode,
PTT en S-meter; typ `help` voor de commando's (`f 7.074`, `m LSB`, `tx`, `rx`,
`toggle`, `split on`, `bench`, `raw …`).

Linux: installeer rigctld via `sudo apt install libhamlib-utils`.

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
- [x] `BoltCat.Core` — `RigctldClient`, `RigState`, `RigPoller`
- [x] rigctld auto-start (meegeleverd voor Windows, systeem-rigctld op Linux)
- [x] MVP GUI — VFO A/B, mode, PTT, A↔B / A→B / Split, band, S-meter, status
- [x] JSON configuratie — radio, COM poort, baud, cache, band-geheugen, thema
- [ ] MIDI configuratie
- [ ] MIDI fase 1 — VFO encoder + PTT

73 de PE5JW
