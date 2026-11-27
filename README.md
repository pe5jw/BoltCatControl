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

## MIDI

In *Instellingen → MIDI*:

1. Kies je controller bij **Apparaat**.
2. **Leer VFO-knop**: draai een paar klikjes rechtsom, laat even los, dan een paar klikjes linksom.
   BoltCatControl herkent zelf welk soort encoder het is:
   - rond 64 of 65 (63/65, 64/66, of grotere sprongen bij snel draaien)
   - 1 / 127 (two's complement)
   - 1 / 65 (signed bit)
3. **Leer PTT-knop**: druk één keer op de knop. Kies *vasthouden = zenden* of *druk aan / druk uit*.

Elke klik van de VFO-knop = de stap uit het hoofdvenster (10 Hz / 100 Hz / 1 kHz).

**MIDI-knoppen** (tab *MIDI-knoppen*): VFO 1 stap omhoog/omlaag, stapgrootte wisselen,
volgende mode of direct USB/LSB/CW/AM/FM/DATA, split, A↔B, A→B, band omhoog/omlaag en
elke band direct (160m–70cm). Klik op *Leer* en druk op de knop; een knop doet maar één ding.
De **monitor** toont live wat de controller stuurt. Windows: elk MIDI apparaat;
Linux: ALSA raw MIDI (`/dev/snd/midiC*D*`, gebruiker in de groep `audio`).

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

## Downloaden

De nieuwste build van `main` staat altijd in de pre-release
[latest](https://github.com/pe5jw/BoltCatControl/releases/tag/latest), zonder inloggen:

```
wget https://github.com/pe5jw/BoltCatControl/releases/download/latest/BoltCatControl-linux-x64.zip
wget https://github.com/pe5jw/BoltCatControl/releases/download/latest/BoltCatControl-win-x64.zip
```

Linux: ontbreekt rigctld, dan biedt BoltCatControl zelf aan om Hamlib te installeren
(apt, dnf, pacman, zypper; wachtwoord via het systeemvenster of een terminal), en ook om je
gebruiker toegang te geven tot de seriële poort (groep `dialout`). Windows: Hamlib zit erbij.

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
- [x] MIDI fase 1 — VFO encoder + PTT (leren, live monitor)

73 de PE5JW
