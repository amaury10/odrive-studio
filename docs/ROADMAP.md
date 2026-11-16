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

## Phase 1 — Protocole natif et connexion (code terminé, acceptation matérielle à faire)
- [x] PacketCodec + CRC + tests (vecteurs déduits de `crc.hpp` et `legacy_protocol.cpp` : il n'existe pas
      d'implémentation Python pure en 0.5.6).
- [x] WinUsbTransport (énumération, ouverture, bulk in/out, délais d'attente, déconnexion) — écrit, **pas
      encore essayé sur une carte** (les tests unitaires ne touchent pas l'USB).
- [x] Lecture du JSON, arbre dynamique (`EndpointSchema`), lecture / écriture / appel de fonction
      (`ODriveDevice`), cache du JSON par `json_version_id` (en mémoire ; cache disque en phase 2).
- [x] `SimulatedODrive` + `SimulatedTransport` basés sur le JSON de référence : mêmes règles de trailer, de
      bit d'ACK et de MTU que la carte ; reboot, save et erase simulés. Le modèle moteur viendra avec les
      phases qui en ont besoin (5 à 7).
- [x] Wrappers typés générés par `ODrive.CodeGen` (`Generated/Wrappers.g.cs`) au-dessus de `RemoteProperty<T>`.
- [x] Commande console `tools/ODrive.Cli` : `list`, `dump` (JSON + toutes les valeurs, rechargeable par
      `SimulatedODrive.FromDump`) et `check` (déroule l'acceptation ci-dessous) ; `--sim` pour la carte simulée.
**Acceptation (matériel) :** lire `vbus_voltage`, écrire `axis0.controller.config.vel_limit`, appeler
`clear_errors()`, survivre à un `reboot()`.
- [x] sur le simulateur : `dotnet run --project tools/ODrive.Cli -- check --sim` ;
- [ ] sur la carte : `dotnet run --project tools/ODrive.Cli -- check`, puis `dump` pour confirmer le JSON de
      référence (`json_version_id` attendu : `0xC1A0AD28`) et archiver les valeurs réelles de la carte.

## Phase 2 — Application : connexion, tableau de bord, explorateur ✅ (sur le simulateur)
- [x] Shell WPF, `DeviceManager` (multi-cartes, reconnexion automatique après reboot / save), bandeau
      d'arrêt d'urgence (bouton permanent + `Échap`, en tête de la file de requêtes de chaque carte).
- [x] Tableau de bord (bus, deux axes, commandes) + décodage des erreurs (`ErrorDecoder`, descriptions du YAML).
- [x] Explorateur générique (arbre lu sur la carte, recherche chemins + docs, édition typée, enums, fonctions).
- [x] Journal des écritures (ancienne → nouvelle valeur, appels de fonction, export CSV).
- [x] `SafetyGuard` : contrôle erreurs + Vbus et confirmation unique par session avant toute demande d'état
      actif ; Save / Erase / Reboot refusés tant qu'un axe n'est pas en IDLE.
- [x] Carte non officielle signalée (`otp_valid` faux, ce que `odrivetool` appelle `dev0`).

Lancer sans matériel : `dotnet run --project src/ODriveStudio.App -- --sim`.

Reporté, avec la phase qui le portera :
- « cause probable et action conseillée » par erreur (SPEC §2) : seule la description du firmware est
  affichée ; un tableau de conseils en français reste à écrire et à vérifier sur le matériel (phase 3) ;
- différences avec la valeur par défaut et avec le profil chargé (SPEC §3) : phase 3, avec les profils ;
- épingler un attribut dans les courbes (SPEC §3) : phase 4 ;
- limites temporaires et surveillance pendant l'exécution dans `SafetyGuard` : phase 5, avec les tests ;
- cache disque du JSON et rapport de diagnostic en un clic (SPEC §11).

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
