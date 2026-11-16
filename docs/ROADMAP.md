# Feuille de route

Chaque phase se termine par : build sans warning, tests verts, critères d'acceptation cochés, docs à jour
(retirer les ⚠ confirmés), commit.

## Phase 0 — Références et outillage ✅
- [x] Cloner `ref/ODrive` au tag `fw-v0.5.6`.
- [x] Confirmer tous les ⚠ de `PROTOCOL_NATIVE.md`, `CAN_SIMPLE.md` et `HOVERBOARD.md` ; corriger les docs
      (fait aussi pour `SPEC.md`, `SAFETY.md`, `AUTOTUNE.md` et `ANTICOGGING.md`).
- [x] Créer `ODriveStudio.slnx` et les projets (squelettes fournis dans `src/`).
- [x] `tools/ODrive.CodeGen` : génère `src/ODrive.Model/Generated/*.cs` depuis `odrive-interface.yaml` —
      enums, flags d'erreur et métadonnées (chemin, type, accès, unité, doc) de toutes les propriétés et
      fonctions. Les wrappers typés (`odrv.Axis0.Motor.Config.PolePairs`) s'appuient sur `RemoteProperty<T>`
      et seront générés en phase 1.
- [x] `tools/gen_reference_json.py` : JSON d'endpoints de référence régénéré depuis les sources du firmware
      (`tests/ODrive.Protocol.Tests/data/odrive-v3.6-0.5.6.json`) ; `ODrive.CodeGen --check-json` prouve que
      le portage C# le reproduit à l'octet près.
**Acceptation :** solution qui compile ; enums et flags d'erreur générés ; docs sans ⚠ sur le protocole.

## Phase 1 — Protocole natif et connexion
- [ ] PacketCodec + CRC + tests (vecteurs déduits de `crc.hpp` et `legacy_protocol.cpp` : il n'existe pas
      d'implémentation Python pure en 0.5.6).
- [ ] WinUsbTransport (énumération, ouverture, bulk in/out, délais d'attente, déconnexion).
- [ ] Lecture du JSON, arbre dynamique, lecture / écriture / appel de fonction.
- [ ] SimulatedTransport basé sur le JSON de référence (déjà disponible, phase 0).
- [ ] Wrappers typés générés par `ODrive.CodeGen` au-dessus de `RemoteProperty<T>`.
- [ ] Commande console `ODrive.Cli dump` : écrit le JSON et toutes les valeurs de la carte dans un fichier →
      confirme le JSON de référence (`json_version_id` attendu : `0xC1A0xxxx`) et fournit les valeurs
      par défaut réelles au simulateur.
**Acceptation (matériel) :** lire `vbus_voltage`, écrire `axis0.controller.config.vel_limit`, appeler
`clear_errors()`, survivre à un `reboot()`.

## Phase 2 — Application : connexion, tableau de bord, explorateur
- [ ] Shell WPF, DeviceManager, multi-cartes, barre d'arrêt d'urgence.
- [ ] Tableau de bord + décodage des erreurs.
- [ ] Explorateur générique (édition, fonctions, recherche).
- [ ] Journal des écritures.

## Phase 3 — Assistant de mise en service + profils
- [ ] Assistant complet (SPEC §4) avec le préréglage hoverboard.
- [ ] Profils JSON : export, import, comparaison, application sélective.
**Acceptation (matériel) :** un moteur d'hoverboard neuf passe de la configuration usine à la boucle
fermée en vitesse uniquement avec l'assistant.

## Phase 4 — Courbes temps réel
- [ ] PlotAcquisition à priorités, mesure de la cadence réelle, export CSV, déclenchement.

## Phase 5 — Réglage manuel et tests d'échelon
- [ ] Générateur de consignes, StepAnalysis, historique annuler / rétablir.

## Phase 6 — Autotune
- [ ] Identification (fonctions pures testées sur le simulateur), calcul des gains, validation, interface.
**Acceptation :** sur le simulateur, gains retrouvés à ±15 % des valeurs théoriques ; sur le matériel,
réponse à l'échelon sans oscillation, roue soulevée.

## Phase 7 — Anticogging
- [ ] Workflow complet, avertissement Hall, mesure avant/après (validé sur le simulateur avec couple de
      détente simulé + encodeur haute résolution simulé).

## Phase 8 — CAN
- [ ] CanSimpleCodec + tests, backends PCAN / gs_usb / slcan, moniteur, émetteur, simulateur de conduite,
      test du watchdog.
**Acceptation (matériel) :** la mini-voiture roue soulevée répond à accélérer / freiner / marche arrière
depuis l'onglet CAN ; coupure de l'envoi → arrêt par watchdog.

## Phase 9 (optionnelle) — Mise à jour firmware DFU
