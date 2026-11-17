# ODrive Studio

Outil Windows de mise en service et de réglage pour ODrive v3.6 (firmware 0.5.6) : assistant de
configuration, courbes temps réel, réglage des boucles, autotune, anticogging, profils et outils CAN Simple.

## Démarrer avec Claude Code
1. Ouvrir ce dossier dans Claude Code.
2. Premier message conseillé :
   > Lis CLAUDE.md et docs/, puis attaque la phase 0 de docs/ROADMAP.md.
3. Avancer phase par phase ; brancher la carte à partir de la phase 1 pour la capture du JSON.

## Prérequis
- Windows 10/11 x64, SDK .NET 10.
- ODrive v3.6 en firmware 0.5.6. Windows charge WinUSB tout seul sur l'interface native, mais sans GUID
  d'interface (défaut du firmware) : à la première connexion d'une carte sur un port USB, lancer une fois
  `dotnet run --project tools/ODrive.Cli -- fix-usb` dans un terminal administrateur
  (voir `docs/PROTOCOL_NATIVE.md` §Transport USB).
- Pour régénérer le code ou les données de référence : `ref/ODrive` (voir `CLAUDE.md`), puis
  `dotnet run --project tools/ODrive.CodeGen` et `python tools/gen_reference_json.py`
  (`pip install pyyaml jinja2 jsonschema`).
- Optionnel : adaptateur USB-CAN (PCAN-USB, CANable).

## Outil console (phase 1)
```
dotnet run --project tools/ODrive.Cli -- list           # cartes branchées, diagnostic du pilote
dotnet run --project tools/ODrive.Cli -- check          # lecture, écriture, clear_errors, reboot, reconnexion
dotnet run --project tools/ODrive.Cli -- dump           # JSON d'endpoints + toutes les valeurs → fichier
```
Ajouter `--sim` pour travailler sur la carte simulée, sans matériel. Fermer `odrivetool` avant : une seule
application à la fois peut ouvrir l'interface native.
