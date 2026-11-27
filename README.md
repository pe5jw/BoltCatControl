# BoltCatControl

**Moderne CAT control voor je transceiver**

BoltCatControl draait op de pc die direct aan de radio hangt. De app start zelf een lokale
[Hamlib](https://hamlib.github.io/) `rigctld` op de COM/USB-poort en praat daar via TCP mee.
Puur lokaal, geen remote. Komt de radio via een virtuele COM-poort binnen (com0com),
dan werkt dat ook: zie [Windows: virtuele COM-poort (com0com)](#windows-virtuele-com-poort-com0com).

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
| **Raspberry Pi** | Pi 3/4/5 met Pi OS 64-bit (`linux-arm64`), of 32-bit (`linux-arm`) |
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

Raspberry Pi (Pi OS): `uname -m` geeft `aarch64` → `BoltCatControl-linux-arm64.zip`,
`armv7l` → `BoltCatControl-linux-arm.zip`.

```
wget https://github.com/pe5jw/BoltCatControl/releases/download/latest/BoltCatControl-linux-arm64.zip
unzip BoltCatControl-linux-arm64.zip && cd BoltCatControl && ./BoltCatControl
```

Linux: ontbreekt rigctld, dan biedt BoltCatControl zelf aan om Hamlib te installeren
(apt, dnf, pacman, zypper; wachtwoord via het systeemvenster of een terminal), en ook om je
gebruiker toegang te geven tot de seriële poort (groep `dialout`). Windows: Hamlib zit erbij.

## Windows: virtuele COM-poort (com0com)

Hangt de radio niet aan deze pc, maar komt CAT binnen via een programma dat een seriële
poort over het netwerk doorgeeft (bijvoorbeeld RadioBridge), dan heb je op Windows een
virtueel COM-poortpaar nodig. [com0com](https://sourceforge.net/projects/com0com/) maakt twee
poorten die met elkaar verbonden zijn, net als een null-modemkabel: wat op COM10 wordt
geschreven komt op COM11 binnen, en andersom. BoltCatControl gebruikt COM11 als een gewone
COM-poort.

```
[netwerk-programma] ── COM10 ══ com0com ══ COM11 ── [rigctld] ── TCP :4532 ── [BoltCatControl]
```

### Downloaden

Neem de **ondertekende** versie 2.2.2.0 voor 64-bit Windows:

- <https://sourceforge.net/projects/com0com/files/com0com/2.2.2.0/> → `com0com-2.2.2.0-x64-fre-signed.zip`

Windows 10/11 laadt geen niet-ondertekende drivers. Met de verkeerde versie staan de poorten
in Apparaatbeheer met een geel uitroepteken (Code 52) en werken ze niet.

### Installeren

1. Pak de zip uit en start de setup met rechtsklik → **Als administrator uitvoeren**.
2. Open daarna, ook als administrator, **Start → com0com → Setup Command Prompt**.
3. Maak een paar met gewone COM-namen. Kies nummers die nog vrij zijn
   (kijk in Apparaatbeheer → *Poorten (COM & LPT)*):
   ```
   install PortName=COM10 PortName=COM11
   ```
   Heeft de setup al een paar `CNCA0`/`CNCB0` gemaakt, hernoem dat dan:
   ```
   change CNCA0 PortName=COM10
   change CNCB0 PortName=COM11
   ```
   Met `list` zie je welke paren er zijn. Het grafische **Setup**-venster van com0com kan
   hetzelfde: vul bij beide poorten de naam in en klik op *Apply*.
4. Controleer in Apparaatbeheer dat COM10 en COM11 zonder uitroepteken onder
   *com0com - serial port emulators* staan.

### Instellen

| Waar | Instelling |
|---|---|
| Netwerk-programma (bijv. RadioBridge client) | COM10 |
| BoltCatControl → *Instellingen → Radio* | COM-poort **COM11** (klik op vernieuwen als hij er niet bij staat), model = je echte radio |
| Baud | gelijk aan de CAT RATE van de radio; de echte snelheid wordt ingesteld waar de radio aan de USB-kabel hangt |

Start eerst het netwerk-programma en daarna BoltCatControl → **Verbinden**.
Testen kan ook met `BoltCat.Test --rig ft-897d --com COM11 --baud 9600`.

Over een tragere verbinding is de radio wat langzamer. Laat dan *Cache automatisch aanpassen*
(tab *Weergave*) aan, dan past BoltCatControl de Hamlib-cache zelf aan als de radio niet op
tijd antwoordt. PTT gaat via CAT; PTT via RTS/DTR hangt af van wat het netwerk-programma
doorgeeft.

### Andere programma's tegelijk

rigctld laat meerdere programma's tegelijk toe, daar is geen extra COM-poort voor nodig.
Kies in WSJT-X, JTDX, fldigi of Log4OM als radio **Hamlib NET rigctl** met adres
`127.0.0.1:4532`. Een programma dat alleen een COM-poort kent, kan via `rigctlcom`
(zit in de map `hamlib`) en een tweede com0com-paar, bijv. COM12/COM13. `rigctlcom` doet zich
dan voor als een Kenwood TS-2000:

```
hamlib\rigctlcom -m 2 -r 127.0.0.1:4532 -R COM12 -S 9600
```

Stel in dat programma een Kenwood TS-2000 in op COM13, 9600 baud.

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
