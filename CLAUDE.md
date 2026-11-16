# ODrive Studio — consignes pour Claude Code

Outil Windows de mise en service, de réglage et de diagnostic pour **ODrive v3.6** en **firmware 0.5.6**
(le dernier pour la gamme v3). Il remplace `odrivetool` pour un usage atelier. Application de référence :
mini-voiture avec deux moteurs roues d'hoverboard (capteurs Hall, 15 paires de pôles), pilotée en CAN par
un contrôleur externe.

## Règles d'or

1. **Le firmware 0.5.6 est la seule vérité.** Toute affirmation sur le protocole, les endpoints, les enums,
   les unités ou les identifiants CAN doit être vérifiée dans le code source au tag `fw-v0.5.6`, pas dans la
   documentation en ligne actuelle (qui décrit 0.6.x / ODrive Pro / S1, incompatibles).
   ```
   git clone --depth 1 --branch fw-v0.5.6 https://github.com/odriverobotics/ODrive.git ref/ODrive
   ```
   `ref/` est dans `.gitignore`. Fichiers clés : voir `docs/PROTOCOL_NATIVE.md` §Références.
2. **Pas de code d'énumérations écrit à la main.** Les enums, flags d'erreur, propriétés et docstrings sont
   **générés** depuis `ref/ODrive/Firmware/odrive-interface.yaml` (outil `tools/ODrive.CodeGen`).
3. **L'interface s'appuie sur l'arbre JSON lu à la connexion** (endpoint 0). Les vues typées (assistant,
   tuning…) passent par la couche générée ; l'explorateur de paramètres reste générique.
4. **Sécurité avant tout** : chaque action qui fait tourner un moteur passe par `SafetyGuard`
   (voir `docs/SAFETY.md`). Arrêt d'urgence global : touche `Échap` + bouton toujours visible → `IDLE` sur
   les deux axes.
5. **Développable sans matériel** : tout doit tourner contre `SimulatedTransport` (rejeu d'un JSON réel +
   modèle moteur simple). Les tests unitaires ne touchent jamais l'USB.

## Stack

- .NET 10 (LTS), C# 14, `Nullable` activé, warnings = erreurs dans les bibliothèques.
- UI : WPF + CommunityToolkit.Mvvm, courbes ScottPlot 5 (WPF).
- USB : P/Invoke WinUSB direct (`SetupAPI` + `winusb.dll`) dans `ODrive.Protocol/Transport/WinUsb`.
  Pas de libusb. Repli : port série CDC (protocole natif tramé, ou ASCII pour le diagnostic minimal).
- CAN : abstraction `ICanBus` ; implémentations PCAN-Basic, gs_usb/candleLight (CANable) et slcan.
- Tests : xUnit + FluentAssertions. Les tests protocole utilisent des captures binaires dans `tests/data/`.

## Organisation

```
src/ODrive.Protocol    transport USB/série, protocole natif fibre (legacy), arbre d'objets dynamique
src/ODrive.Model       code généré (enums, erreurs, wrappers typés Axis/Motor/Encoder/Controller…)
src/ODrive.Can         CAN Simple 0.5.6 (encodage/décodage), backends adaptateurs
src/ODrive.Tuning      calibration, autotune, anticogging, identification, tests d'échelon
src/ODriveStudio.App   application WPF (MVVM)
tools/ODrive.CodeGen   génère src/ODrive.Model depuis odrive-interface.yaml
tests/*                tests unitaires par projet
docs/                  spécifications : à lire avant de coder une fonctionnalité
```

## Méthode de travail

- Suivre `docs/ROADMAP.md` phase par phase. Une phase n'est terminée que si ses critères d'acceptation sont
  vérifiés (tests verts + démonstration sur le simulateur ; sur le matériel quand c'est indiqué).
- Avant d'implémenter un point de protocole incertain, citer le fichier et la ligne de `ref/ODrive` qui le
  justifie dans le commentaire du code.
- Les passages marqués **⚠ À VÉRIFIER** dans `docs/` sont des hypothèses : les confirmer dans les sources,
  puis corriger la doc.
- Commandes : `dotnet build ODriveStudio.slnx`, `dotnet test ODriveStudio.slnx`.
- Langue : code et identifiants en anglais ; interface, messages utilisateur et docs en français.
- Unités firmware 0.5.x : positions en **tours**, vitesses en **tours/s**, couple en **Nm**,
  `vel_gain` en Nm/(tour/s), `pos_gain` en (tour/s)/tour. Toujours afficher l'unité dans l'interface.
