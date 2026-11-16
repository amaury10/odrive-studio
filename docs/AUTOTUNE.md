# Autotune

Le firmware 0.5.6 **n'a pas d'autotune intégré** : tout se fait dans l'outil, à partir des consignes et
mesures disponibles en USB. Principe : identifier le système mécanique, calculer des gains pour une bande
passante choisie, puis valider. L'outil **propose**, l'utilisateur **applique**.

## Prérequis vérifiés automatiquement
- Axe calibré, sans erreur, en `CLOSED_LOOP_CONTROL`.
- Case cochée « roue soulevée / libre de tourner » (obligatoire pour la mini-voiture).
- Limites de sécurité temporaires : `vel_limit` réduit, `current_lim` réduit, restaurés à la fin ou à l'abandon.

## Étape 0 — boucle de courant
- `current_control_bandwidth` est réglé à partir de R et L (calibration). Afficher la bande passante
  résultante et proposer de la réduire si L est élevée (hoverboard : environ 100 rad/s).

## Étape 1 — identification inertie et frottement (mode couple)
1. `control_mode = TORQUE_CONTROL`, `input_mode = PASSTHROUGH`.
2. Paliers de couple ±τ1, ±τ2 (fractions de `current_lim × torque_constant`) jusqu'à une vitesse plafond,
   coupure (`input_torque = 0`) dès que |vel| dépasse le plafond.
3. Ajuster `τ = J·a + b·ω + τc·sign(ω)` par moindres carrés sur `vel_estimate` filtrée
   (accélération par dérivée lissée Savitzky–Golay).
4. Résultat : J (Nm/(tour/s²)), b, τc, avec un indicateur de qualité de l'ajustement (R²).

## Étape 2 — calcul des gains
Avec ωc la bande passante vitesse visée (rad/s), choisie par l'utilisateur et plafonnée par la bande
passante encodeur (Hall : environ ωc ≤ encoder.bandwidth / 4) :
- `vel_gain = J · ωc`, **sans facteur 2π** : le firmware calcule `couple = vel_gain × erreur de vitesse` avec
  la vitesse en tours/s (`ref/ODrive/Firmware/MotorControl/controller.cpp:381`, unités dans
  `controller.hpp:27-30`). Avec J en Nm/(tour/s²) et `vel_gain` en Nm/(tour/s), la boucle fermée
  `J·dω/dt = vel_gain·(ω_ref − ω)` a pour pulsation de coupure `vel_gain / J` en rad/s : l'unité d'angle
  se simplifie tant que J et les gains sont tous exprimés en tours.
- `vel_integrator_gain = vel_gain · ωc / 4` (marge de phase d'environ 60°, ajustable).
- `pos_gain = ωc / 4` si le mode position est utilisé.
Préréglages de raideur : souple / normal / raide.

## Étape 3 — validation
- Échelon de vitesse (petite amplitude) : mesure du dépassement, du temps de réponse et de l'oscillation.
- Si dépassement > 20 % ou oscillation détectée : réduire ωc de 30 % et recommencer (3 itérations max).
- Affichage avant/après sur le même graphique, puis bouton « Appliquer » et option « Enregistrer dans la carte ».

## Variante : relais (Åström–Hägglund)
Option avancée pour les systèmes mal modélisés : oscillation entretenue par relais de couple autour d'une
vitesse moyenne, puis règles de Ziegler-Nichols modifiées. Mêmes protections et même validation.

## Implémentation
`ODrive.Tuning.Autotune` : machine d'états asynchrone annulable (`CancellationToken`) ; chaque sortie
d'état restaure la configuration de départ. Les algorithmes d'identification sont des fonctions pures,
testées sur des signaux synthétiques générés par le simulateur.
