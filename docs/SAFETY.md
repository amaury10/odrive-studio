# Sécurité

- **Arrêt d'urgence** : touche `Échap` globale + bouton rouge toujours visible → `requested_state = IDLE`
  sur tous les axes de toutes les cartes, en priorité dans la file de requêtes (le plus haut niveau).
- État du code (phase 2) : arrêt d'urgence, points 1 et 2 de SafetyGuard, journal et règle Save / Erase /
  Reboot sont en place ; les points 3 et 4 (limites temporaires, surveillance) arrivent avec les tests de
  la phase 5, et le watchdog pendant les tests avec eux.
- **SafetyGuard** : toute opération qui peut faire tourner un moteur (calibration, test, autotune,
  anticogging, générateur de consignes, simulation CAN) doit :
  1. vérifier l'état de la carte (pas d'erreur, Vbus dans la plage) ;
  2. demander une confirmation explicite la première fois dans la session (« roues soulevées ? ») ;
  3. appliquer des limites temporaires et les restaurer dans un `finally` ;
  4. surveiller pendant l'exécution : vitesse, courant, Vbus, température FET → abandon si dépassement.
- **Perte de liaison USB** pendant un test : l'outil ne peut plus agir, donc proposer d'activer le watchdog
  de l'ODrive pendant les tests. **Les lectures et écritures USB natives ne nourrissent pas le watchdog** :
  seuls le font les trames CAN adressées à l'axe, les commandes moteur ASCII et la fonction
  `axisN.watchdog_feed()` (`ref/ODrive/Firmware/MotorControl/axis.cpp:166`, appels listés dans
  `docs/CAN_SIMPLE.md` §Watchdog). L'outil appelle donc `axisN.watchdog_feed()` périodiquement (période
  ≤ `watchdog_timeout` / 4) tant qu'un test est en cours, et restaure `enable_watchdog` / `watchdog_timeout`
  à la fin. Le compteur est rechargé à l'entrée en `CLOSED_LOOP_CONTROL` (`axis.cpp:577`).
- **Écritures traçées** : chaque écriture est journalisée (horodatage, chemin, ancienne → nouvelle valeur).
- **Save / Erase / Reboot** : confirmation, et jamais pendant que l'axe est en boucle fermée.
