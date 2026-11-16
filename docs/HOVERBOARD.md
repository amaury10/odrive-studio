# Préréglage « moteur roue d'hoverboard »

> Valeurs et ordre des opérations repris du guide hoverboard du firmware 0.5.6
> (`ref/ODrive/docs/hoverboard.rst`). À afficher dans l'assistant, toutes modifiables.
> Les lignes marquées « outil » ne viennent pas du guide : ce sont des valeurs de départ prudentes.

## Carte
| Paramètre | Valeur | Remarque |
|---|---|---|
| `config.enable_brake_resistor` | true si résistance branchée | sauvegarde + reboot nécessaires |
| `config.brake_resistance` | valeur de la résistance (Ω) | ajouter ~0,05 Ω en cas d'erreur au freinage |
| `config.dc_max_negative_current` | −0,01 A par défaut | sans résistance : courant de recharge accepté par la batterie (négatif) |
| `config.gpio9_mode`, `gpio10_mode`, `gpio11_mode` | `GPIO_MODE_DIGITAL` | **entrées Hall de l'axe 0** (A, B, Z de M0) ; reboot nécessaire |
| `config.gpio12_mode`, `gpio13_mode`, `gpio14_mode` | `GPIO_MODE_DIGITAL` | **entrées Hall de l'axe 1** (A, B, Z de M1) ; reboot nécessaire |

Correspondance sur v3.5 / v3.6 (`ref/ODrive/Firmware/Board/v3/board.cpp:204-224`) : GPIO9-11 = ENC0 A/B/Z,
GPIO12-14 = ENC1 A/B/Z, GPIO15-16 = CAN. Le guide ne traite que l'axe 0 ; l'assistant règle les GPIO de
l'axe choisi. Hall A/B/C se branchent sur A/B/Z (`ref/ODrive/docs/encoders.rst:96-110`).

## Moteur
| Paramètre | Valeur | Remarque |
|---|---|---|
| `motor.config.pole_pairs` | 15 | 30 aimants ; à vérifier : compter les aimants / 2 |
| `motor.config.motor_type` | `HIGH_CURRENT` | valeur par défaut |
| `motor.config.resistance_calib_max_voltage` | 4 V | R de phase élevée |
| `motor.config.requested_current_range` | 25 A | sauvegarde + reboot nécessaires |
| `motor.config.current_control_bandwidth` | 100 rad/s | inductance élevée ; le défaut de 1000 est trop haut |
| `motor.config.torque_constant` | 8,27 / Kv | Kv typique 16 → 0,517 Nm/A ; à mesurer si possible |
| `motor.config.current_lim` | 10–20 A | outil ; selon l'échauffement |
| `motor.config.calibration_current` | 5–10 A | outil |

Contrôle de plausibilité après calibration (valeurs du guide) : `phase_resistance` ≈ 0,18 Ω,
`phase_inductance` ≈ 0,34 mH.

## Encodeur (Hall)
| Paramètre | Valeur |
|---|---|
| `encoder.config.mode` | `ENCODER_MODE_HALL` |
| `encoder.config.cpr` | 6 × pole_pairs = 90 |
| `encoder.config.calib_scan_distance` | 150 |
| `encoder.config.bandwidth` | 100 |

Matériel : condensateurs de 22 nF entre chaque ligne Hall et GND sur l'ODrive (le bruit Hall est la
première cause d'`ENCODER_ERROR_ILLEGAL_HALL_STATE`).

## Contrôleur
| Paramètre | Valeur de départ |
|---|---|
| `controller.config.pos_gain` | 1 |
| `controller.config.vel_gain` | 0,02 × torque_constant × cpr (≈ 0,93 avec Kv = 16) |
| `controller.config.vel_integrator_gain` | 0,1 × torque_constant × cpr (≈ 4,65 avec Kv = 16) |
| `controller.config.vel_limit` | 10 tours/s |
| `controller.config.control_mode` | `CONTROL_MODE_VELOCITY_CONTROL` |
| `controller.config.input_mode` | `INPUT_MODE_VEL_RAMP` (outil ; le guide laisse `PASSTHROUGH`) |
| `controller.config.vel_ramp_rate` | à régler (tours/s²) (outil) |

Les deux formules de gains sont celles du guide, à l'identique ; elles dépendent de `torque_constant` et de
`cpr`, donc les recalculer si l'un des deux change.

## Séquence de calibration (ordre du guide)
1. Écrire la configuration ci-dessus, `save_configuration()` puis reboot (les modes GPIO et
   `requested_current_range` ne prennent effet qu'après).
2. `AXIS_STATE_MOTOR_CALIBRATION` → vérifier `motor.error == 0`, R et L plausibles →
   `motor.config.pre_calibrated = true`.
3. `AXIS_STATE_ENCODER_HALL_POLARITY_CALIBRATION` → vérifier `encoder.error == 0`.
4. `AXIS_STATE_ENCODER_OFFSET_CALIBRATION` → vérifier `encoder.error == 0` et
   `encoder.config.phase_offset_float` ≈ 0,5 modulo 1 (−1,5 ; −0,5 ; 0,5 ; 1,5…) →
   `encoder.config.pre_calibrated = true`.
5. `save_configuration()`, reboot, puis essai : `CLOSED_LOOP_CONTROL`, `input_vel = 2`, retour à 0, `IDLE`.
6. Option : `axis.config.startup_closed_loop_control = true`.

Les phases moteur et les fils Hall peuvent être branchés dans n'importe quel ordre, à condition de ne plus
y toucher après la calibration.

## Mini-voiture
- `axis.config.can.node_id` : 0 (gauche), 1 (droite) — ce sont les valeurs par défaut.
- Watchdog activé, 0,2 s.
- Le moteur droit est monté en miroir : inverser le sens côté contrôleur (pas dans l'ODrive) et le
  documenter dans le profil.
- Freinage récupératif : résistance de freinage branchée et `enable_brake_resistor = true`, ou batterie
  acceptant la recharge avec `dc_max_negative_current` réglé.
