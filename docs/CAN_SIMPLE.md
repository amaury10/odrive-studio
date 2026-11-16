# CAN Simple — firmware 0.5.6

> Vérifié dans `ref/ODrive/Firmware/communication/can/can_simple.hpp` (identifiants, l.9-41) et
> `can_simple.cpp` (contenu des trames ; lignes citées ci-dessous).
> Attention : en 0.6.x, plusieurs commandes ont changé — ne pas copier une bibliothèque 0.6.

- Trames standard 11 bits : `arbitration_id = (node_id << 5) | cmd_id` — 6 bits de nœud, 5 bits de commande
  (`can_simple.hpp:92-102`). Avec `axisN.config.can.is_extended = true`, l'identifiant est étendu (29 bits) et
  le nœud occupe tous les bits au-dessus des 5 bits de commande.
- `node_id` par axe : `axisN.config.can.node_id` (par défaut = numéro de l'axe, `MotorControl/axis.cpp:88`).
  Débit : `can.config.baud_rate` (250 kbit/s par défaut, `can/odrive_can.hpp:25`).
- Données little-endian ; floats en IEEE-754 32 bits. Toutes les réponses font 8 octets (`txmsg.len = 8`),
  les octets non utilisés valent 0.
- Une commande « Get » répond si la trame reçue est **RTR ou de longueur 0** (`can_simple.cpp:73`).

| cmd | Nom (firmware) | Sens | Contenu (octets) |
|---|---|---|---|
| 0x000 | CANopen NMT | — | réservé, ignoré |
| 0x001 | Heartbeat | ODrive → | axis_error u32, axis_state u8, motor_flags u8, encoder_flags u8, controller_flags u8 (l.441-467) |
| 0x002 | Estop | → ODrive | — ; lève `AXIS_ERROR_ESTOP_REQUESTED` (l.170) |
| 0x003 | Get Motor Error | RTR | motor_error **u64** (l.181) |
| 0x004 | Get Encoder Error | RTR | encoder_error u32 |
| 0x005 | Get Sensorless Error | RTR | sensorless_error u32 |
| 0x006 | Set Axis Node ID | → | node_id u32 |
| 0x007 | Set Axis Requested State | → | state i32 (`AxisState`) |
| 0x008 | Set Axis Startup Config | → | **non implémenté** dans le firmware (l.230) |
| 0x009 | Get Encoder Estimates | ODrive → / RTR | pos f32 (tours), vel f32 (tours/s) |
| 0x00A | Get Encoder Count | RTR | shadow_count i32, count_in_cpr i32 |
| 0x00B | Set Controller Modes | → | control_mode i32, input_mode i32 |
| 0x00C | Set Input Pos | → | pos f32 (tours), vel_ff i16 × 0,001 tour/s, torque_ff i16 × 0,001 Nm (l.274-279) |
| 0x00D | Set Input Vel | → | vel f32 (tours/s), torque_ff f32 (Nm) |
| 0x00E | Set Input Torque | → | torque f32 (Nm) |
| 0x00F | Set Limits | → | vel_limit f32 (tours/s), current_limit f32 (A) |
| 0x010 | Start Anticogging | → | — |
| 0x011 | Set Traj Vel Limit | → | f32 |
| 0x012 | Set Traj Accel Limits | → | accel f32, decel f32 |
| 0x013 | Set Traj Inertia | → | f32 |
| 0x014 | Get Iq | RTR | iq_setpoint f32, iq_measured f32 |
| 0x015 | Get Sensorless Estimates | RTR | pos f32, vel f32 |
| 0x016 | Reboot | → | — (redémarre toute la carte) |
| 0x017 | Get Bus Voltage and Current | RTR | vbus f32 (V), **ibus f32 (A)** (l.352-366) |
| 0x018 | Clear Errors | → | — (efface les erreurs de **toute** la carte, l.387) |
| 0x019 | Set Linear Count | → | i32 |
| 0x01A | Set Pos Gain | → | f32 |
| 0x01B | Set Vel Gains | → | vel_gain f32, vel_integrator_gain f32 |
| 0x01C | Get ADC Voltage | → (**pas** RTR) | requête : gpio u8 ; réponse : tension f32 (l.368-384) |
| 0x01D | Get Controller Error | RTR | controller_error u32 |

Heartbeat, octets 5 à 7 : `motor_flags` = 1 si `motor.error != 0` ; `encoder_flags` = 1 si
`encoder.error != 0` ; `controller_flags` : bit 0 = `controller.error != 0`, bit 7 = `trajectory_done`.

## Messages cycliques
Périodes en ms dans `axisN.config.can.*` (0 = désactivé ; `Firmware/odrive-interface.yaml:596-610`,
`can_simple.cpp:409-420`) : `heartbeat_rate_ms` (100 par défaut), `encoder_rate_ms`,
`motor_error_rate_ms`, `encoder_error_rate_ms`, `controller_error_rate_ms`, `sensorless_error_rate_ms`,
`encoder_count_rate_ms`, `iq_rate_ms`, `sensorless_rate_ms`, `bus_vi_rate_ms`.

## Watchdog
`axisN.config.enable_watchdog = true`, `axisN.config.watchdog_timeout` (s). Il est nourri par :
- **toute trame CAN adressée au nœud de l'axe**, quelle que soit la commande, y compris les RTR et les
  commandes inconnues (`can_simple.cpp:60`, première ligne de `do_command`) ;
- les commandes moteur du protocole ASCII (`communication/ascii_protocol.cpp:151…`) ;
- la fonction `axisN.watchdog_feed()` du protocole natif ;
- l'entrée en `CLOSED_LOOP_CONTROL` (`MotorControl/axis.cpp:577`) et l'application de la configuration.

Une simple **lecture ou écriture USB native ne le nourrit pas** (voir `docs/SAFETY.md`).
À l'expiration : `AXIS_ERROR_WATCHDOG_TIMER_EXPIRED` (`axis.cpp:171-181`). L'outil doit le configurer,
l'expliquer et le tester (arrêt d'envoi → contrôle de l'erreur).

## Adaptateurs supportés (`ICanBus`)
1. PCAN-USB (PCAN-Basic, `PCANBasic.dll`).
2. CANable / candleLight (gs_usb via WinUSB).
3. slcan (port série, firmware CANable d'origine).
Interface : `Open(bitrate)`, `Send(frame)`, `IAsyncEnumerable<CanFrame> ReadAll()`, horodatage.

## Codec
`ODrive.Can.CanSimpleCodec` : une méthode `Encode` / `TryDecode` par commande, testée avec des vecteurs
écrits à partir du code source 0.5.6 (pas à partir de la doc 0.6).
