# Spécification fonctionnelle — ODrive Studio

Cible : ODrive v3.6 (56 V ou 24 V), firmware 0.5.6, Windows 10/11 x64.

## 1. Connexion et gestion des cartes
- Détection automatique USB : VID `0x1209`, PID `0x0D32`, interface native n° 2 (bulk `0x03` OUT / `0x83` IN), énumérée par les GUID d'interface enregistrés dans Windows pour `MI_02` (`{EA0BD5C3-50F3-4888-84B4-74E50E1649DB}` après `odrive-cli fix-usb`) — voir `docs/PROTOCOL_NATIVE.md` §Transport USB.
- Plusieurs ODrive en parallèle, identifiés par numéro de série (affiché en hexa comme `odrivetool`).
- Cartes non officielles (clones) : la carte expose `otp_valid`, faux quand la zone OTP du STM32 n'a pas été
  programmée par ODrive Robotics (`ref/ODrive/Firmware/MotorControl/odrive_main.h:246`). `odrivetool` les
  nomme alors `dev0` au lieu de `odrv0` (`ref/ODrive/tools/odrive/shell.py:83-89`) ; le protocole est le
  même. L'outil affiche « carte non officielle » dans le titre, sans rien restreindre. Attention : la
  version matérielle (`hw_version_*`) est fixée à la compilation du firmware (`odrive_main.h:210-212`) et non
  lue sur la carte : elle décrit le firmware flashé, pas forcément la carte réelle.
- Lecture et mise en cache de l'arbre JSON (endpoint 0) ; cache indexé par `json_version_id` (u32 lu en une requête, offset `0xFFFFFFFF`).
- Infos carte : version hardware, version firmware, numéro de série, `vbus_voltage`, `ibus`.
- Reconnexion automatique après un reboot ou un `save_configuration`.
- Indicateur de pilote : si la carte est visible par son VID/PID mais ne peut pas être ouverte, dire pourquoi (`UsbDriverDiagnosis`) : GUID d'interface absent — cas normal en 0.5.6, corrigé par `odrive-cli fix-usb` —, autre pilote que WinUSB (procédure Zadig), ou GUID pas encore publié (rebrancher).

## 2. Tableau de bord
- Par axe : état courant / demandé, mode de contrôle, mode d'entrée, consigne, vitesse et position estimées, Iq mesuré / consigne, température FET (et moteur si thermistance configurée).
- Bus : tension, courant, état de la résistance de freinage.
- Bandeau d'erreurs décodées en clair (axe, moteur, encodeur, contrôleur, sensorless), avec description issue de `odrive-interface.yaml`, cause probable et action conseillée.
- Boutons : Clear errors, IDLE, CLOSED_LOOP, Reboot, Save, Erase (avec confirmation).

## 3. Explorateur de paramètres (générique)
- Arbre complet de l'objet ODrive, comme dans `odrivetool`, avec recherche plein texte.
- Édition typée (bool, float, int, enums sous forme de listes déroulantes avec description).
- Différences avec la valeur par défaut et avec le profil chargé, mises en surbrillance.
- Appel de fonctions avec saisie des arguments (ex. `start_anticogging_calibration`, `get_adc_voltage`).
- Surveillance : épingler n'importe quel attribut numérique dans la vue « Courbes ».

## 4. Assistant de mise en service
Déroulé guidé, par axe, avec état visible de chaque étape et retour arrière possible :
1. Alimentation : tension, `dc_bus_undervoltage/overvoltage_trip_level`, résistance de freinage (`brake_resistance`, `enable_brake_resistor`), `dc_max_negative_current`.
2. Moteur : choix d'un préréglage (**hoverboard**, gimbal, D5065, personnalisé) ou saisie libre.
3. Encodeur : Hall / incrémental ABI / SPI absolu ; CPR ; filtrage Hall conseillé (22 nF).
4. Calibration : polarité Hall → résistance/inductance moteur → offset encodeur ; affichage des valeurs mesurées et contrôle de plausibilité (R, L, phase).
5. Marquage `pre_calibrated` + démarrage automatique optionnel (`startup_closed_loop_control`).
6. Limites : `vel_limit`, `current_lim`, `requested_current_range`, rampes.
7. Communication : `can.node_id`, débit CAN, watchdog.
8. Sauvegarde dans la carte + export du profil JSON.

Préréglage hoverboard : voir `docs/HOVERBOARD.md`.

## 5. Courbes temps réel
- Acquisition par lecture cyclique USB (objectif ≥ 200 Hz agrégé ; mesurer et afficher la cadence réelle).
- Jeux prédéfinis : vitesse (consigne / estimée), position, Iq (consigne / mesuré), Vbus / Ibus, températures.
- Pause, curseurs, zoom, export CSV, déclenchement sur seuil.
- Pas de « trace firmware » : l'oscilloscope interne de 0.5.6 (`oscilloscope.size`, `oscilloscope.get_val(index)`, 4096 points) existe, mais sa source et son déclenchement sont fixés à la compilation (`ref/ODrive/Firmware/MotorControl/oscilloscope.cpp:7-27`, `odrive_main.h:227`) et ne sont pas réglables par le protocole. Inutilisable avec le firmware officiel ; l'acquisition se fait donc uniquement par lecture cyclique.

## 6. Réglage manuel des boucles
- Curseurs et saisie directe : `pos_gain`, `vel_gain`, `vel_integrator_gain`, `current_control_bandwidth`, `encoder.config.bandwidth`, `vel_ramp_rate`, `torque_ramp_rate`.
- Générateur de consignes : échelon, rampe, sinus, carré, avec amplitude, offset et durée ; mode vitesse, position ou couple.
- Mesures automatiques sur l'échelon : temps de montée, dépassement, temps d'établissement, erreur statique.
- Annuler / rétablir les changements de gains (historique de session).

## 7. Autotune
Voir `docs/AUTOTUNE.md`. Identification inertie + frottement, calcul des gains vitesse et position pour une
bande passante choisie, validation par échelon, et proposition (jamais d'application silencieuse).

## 8. Anticogging
Voir `docs/ANTICOGGING.md`. Workflow firmware complet, avec un avertissement explicite quand l'encodeur
n'a pas une résolution suffisante (cas des capteurs Hall).

## 9. Profils
- Export / import JSON de toute la configuration persistante (`*.config.*`) des deux axes et de la carte.
- Comparaison entre deux profils ou entre un profil et la carte, puis application sélective.
- Versionnage : le profil enregistre la version firmware et le CRC du JSON ; avertissement en cas d'écart.

## 10. CAN
Voir `docs/CAN_SIMPLE.md`.
- Moniteur de trames avec décodage CAN Simple 0.5.6 (nœud, commande, valeurs).
- Émetteur : formulaires par commande (Set Input Vel, Set Axis State…).
- Simulation de conduite : deux curseurs gauche/droite ou « accélérateur / frein / sens + braquage », avec watchdog et arrêt sur relâchement — pour valider la configuration avant d'écrire le firmware du contrôleur.
- Vérification de la configuration CAN de la carte en USB et en CAN en même temps.

## 11. Diagnostics
- Test de phases (résistance des trois phases via la calibration), test Hall (rotation lente à la main et affichage de la séquence d'états Hall, détection des états invalides 0 et 7).
- Journal horodaté de toutes les écritures faites par l'outil (exportable).
- Rapport de diagnostic en un clic (config + erreurs + versions) au format texte.

## 12. Hors périmètre (phase optionnelle)
- Mise à jour firmware (DFU STM32) : phase 9, optionnelle, derrière un avertissement fort.
