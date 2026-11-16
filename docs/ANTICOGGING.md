# Anticogging

## Ce que fait le firmware 0.5.6
Vérifié dans `ref/ODrive/Firmware/MotorControl/controller.cpp:50-103` et `:364-371`, et dans
`ref/ODrive/Firmware/odrive-interface.yaml:1146-1155,1198-1200`.

- Une carte de **3600 points par tour mécanique** (pas de 1/3600 tour, `encoder.hpp:148`), indépendante du
  cpr de l'encodeur.
- `controller.start_anticogging_calibration()` met `calib_anticogging` à vrai (seulement si l'axe n'a pas
  d'erreur). Le firmware passe alors en `CONTROL_MODE_POSITION_CONTROL`, vise successivement chaque point
  (`input_pos = index / 3600`), attend que l'erreur de position et la vitesse passent sous les seuils, puis
  enregistre le couple de l'intégrateur de vitesse (`vel_integrator_torque`) comme valeur du point.
- À la fin : retour à la position 0, `anticogging_valid = true`, `calib_anticogging = false`.
- En service, si `anticogging_valid` et `anticogging_enabled`, la valeur du point correspondant à la
  position est ajoutée au couple en feed-forward.

Propriétés (`axisN.controller.config.anticogging.*`) :

| Nom | Accès | Rôle |
|---|---|---|
| `index` | lecture | point en cours (0 à 3599) : progression = index / 3600 |
| `pre_calibrated` | écriture | la carte sauvegardée redevient valide au démarrage, dès que l'encodeur est prêt (`encoder.cpp:63,94`) |
| `calib_anticogging` | lecture | calibration en cours |
| `calib_pos_threshold` | écriture | seuil d'erreur de position, **en coups d'encodeur** (divisé par cpr ; défaut 1) |
| `calib_vel_threshold` | écriture | seuil de vitesse, en coups/s (divisé par cpr ; défaut 1) |
| `cogging_ratio` | lecture | inutilisé par le firmware (vaut 1 ; le pas réel est fixé à 1/3600 tour) |
| `anticogging_enabled` | écriture | applique la carte |

Autres : `axisN.controller.anticogging_valid` (lecture, `odrive-interface.yaml:1059`) ;
fonctions `controller.get_anticogging_value(index) → float` (**lecture de la carte**, point par point),
`controller.remove_anticogging_bias() → float` (retire la moyenne de la carte et la renvoie).
Il n'existe pas de fonction d'écriture : la carte ne peut pas être réimportée depuis un profil.

Le firmware 0.5.6 ne documente pas de gains recommandés pour la calibration (rien dans `ref/ODrive/docs`).
L'intégrateur de vitesse doit être actif (`vel_integrator_gain > 0`), puisque c'est sa sortie qui est
enregistrée ; les gains temporaires proposés par l'outil sont donc à déterminer à l'essai (phase 7).

## Limite importante
La calibration demande de tenir chaque point à moins de `calib_pos_threshold` **coup d'encodeur** près.
Avec les capteurs Hall d'un hoverboard (cpr = 90), un coup vaut 40 points de la carte : les 3600 points ne
sont pas distinguables, et la tenue de position entre deux fronts Hall n'a pas de sens. L'anticogging est
donc inopérant. L'outil doit :
- afficher un avertissement bloquant (contournable par l'utilisateur) si `encoder.config.mode == HALL` ou
  si cpr < 3600 (moins d'un coup par point de la carte) ;
- suggérer un encodeur incrémental ou magnétique de plus haute résolution si l'anticogging est un vrai besoin.
Pour une mini-voiture, l'anticogging n'est en pratique pas utile : la fonction reste là pour d'autres moteurs.

## Workflow dans l'outil
1. Contrôles : encodeur calibré, `pre_calibrated`, axe en `CLOSED_LOOP_CONTROL`, aucune erreur.
2. Gains temporaires pour la calibration (boucle de position raide, intégrateur actif), mémorisation de
   `control_mode` et `input_pos` (le firmware les modifie).
3. Lancement de `start_anticogging_calibration()`, suivi de `calib_anticogging` et de `index` / 3600
   en direct, bouton d'abandon (→ IDLE + restauration des gains).
4. Fin : restauration des gains et du mode, `remove_anticogging_bias()` en option,
   `anticogging.pre_calibrated = true`, `anticogging_enabled = true`, proposition de sauvegarde dans la carte.
5. Vérification : test à vitesse lente constante, comparaison de l'ondulation de `vel_estimate` (écart-type)
   avec et sans anticogging ; graphique de la carte lue par `get_anticogging_value` (3600 appels).
6. Export de la carte dans le profil JSON, à titre d'archive (pas de réimport possible).
