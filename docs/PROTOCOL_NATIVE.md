# Protocole natif (fibre « legacy ») — ODrive firmware 0.5.6

> Tous les points de ce document ont été vérifiés dans `ref/ODrive` (tag `fw-v0.5.6`) ; la référence
> (fichier:ligne, relative à `ref/ODrive/`) est donnée entre parenthèses. Reprendre ces références dans les
> commentaires du code qui les implémente.

## Transport USB
- VID `0x1209`, PID `0x0D32` (`Firmware/Board/v3/Src/usbd_desc.c:94,97`).
- Périphérique composite à 3 interfaces (`Firmware/ThirdParty/STM32_USB_Device_Library/Class/CDC/Src/usbd_cdc.c:171`) :
  | Interface | Classe | Endpoints | Rôle |
  |---|---|---|---|
  | 0 | CDC contrôle (0x02) | `0x82` IN (interruption) | port COM |
  | 1 | CDC données (0x0A) | `0x01` OUT, `0x81` IN | port COM (ASCII par défaut) |
  | 2 | vendor (0x00) | `0x03` OUT, `0x83` IN (bulk) | **interface native** |

  (numéros d'interface : `usbd_cdc.c:195,243,286` ; endpoints : `.../CDC/Inc/usbd_cdc.h:44-48`.)
- Nom de l'interface native : « ODrive 3.6 Native Interface » (`usbd_desc.c:101`).
- **Pilote Windows : WinUSB s'installe tout seul.** Le firmware fournit les descripteurs MS OS 1.0 : chaîne
  `MSFT100` à l'index 0xEE (`usbd_desc.c:122`), compat ID `WINUSB` sur l'interface 2 (`usbd_cdc.c:823-845`)
  et la propriété `DeviceInterfaceGUID = {EA0BD5C3-50F3-4888-84B4-74E50E1649DB}` (`usbd_cdc.c:865-895`).
  → L'énumération se fait avec SetupAPI sur ce GUID d'interface. Zadig n'est nécessaire que si un autre
  pilote (libusb-win32, libusbK…) a été forcé auparavant sur l'interface 2 : dans ce cas le GUID
  n'apparaît pas alors que le VID/PID est présent, et l'application explique la marche à suivre.
  Windows ne lit ces descripteurs qu'une fois par périphérique (cache dans
  `HKLM\SYSTEM\CurrentControlSet\Control\usbflags\12090D32xxxx`).
- Sur USB bulk, **pas de tramage** : un transfert = un paquet.
- **Taille maximale d'un paquet : 63 octets dans chaque sens.** Le firmware ne segmente pas les transferts
  et refuse d'émettre 64 octets ou plus (`Firmware/communication/interface_usb.cpp:65-74`,
  `USB_TX_DATA_SIZE = 64` dans `Firmware/Board/v3/Inc/usbd_cdc_if.h:82`) ; le protocole est instancié avec
  un MTU de 63 (`interface_usb.cpp:149`). Une réponse transporte donc au plus **61 octets** de données
  (`Firmware/fibre-cpp/legacy_protocol.cpp:487`), et une requête au plus 55 (63 − 8 d'en-tête et de trailer).

## Transport série (CDC ou UART)
Le paquet est encapsulé (`Firmware/fibre-cpp/legacy_protocol.cpp:22-51`, constantes dans
`legacy_protocol.hpp:18-28`) :

| Champ | Taille | Valeur |
|---|---|---|
| préfixe | 1 | `0xAA` |
| longueur | 1 | longueur du paquet, < 128 (bit 7 à 0) |
| CRC8 | 1 | CRC8 des 2 octets précédents — polynôme `0x37`, init `0x42` |
| paquet | n | |
| CRC16 | 2 | CRC16 du paquet — polynôme `0x3D65`, init `0x1337`, **octet de poids fort en premier** |

- Les deux CRC sont calculés MSB en premier, sans réflexion ni XOR final (`Firmware/fibre-cpp/crc.hpp:10-27`).
- À la réception, le CRC8 calculé sur les 3 octets d'en-tête et le CRC16 calculé sur paquet + trailer
  valent 0 (`legacy_protocol.cpp:147,167-170`).
- Par défaut, le CDC parle ASCII (`config.usb_cdc_protocol`, type `StreamProtocolType`) : le transport série
  natif sert surtout à l'UART.

## Format des paquets (little-endian)
Requête (`legacy_protocol.cpp:230-248` côté client, `:411-501` côté carte) :

| Champ | Type | Remarque |
|---|---|---|
| seq_no | u16 | **bit 15 = 0** (sinon la carte prend le paquet pour un ACK, l.419). libfibre force aussi le bit 7 à 1 pour ne pas être confondu avec de l'ASCII (l.208) : on fait pareil. |
| endpoint_id | u16 | bit 15 = 1 → une réponse est attendue (l.459) |
| expected_response_size | u16 | plafonné à 61 par la carte |
| payload | octets | valeur écrite ou argument |
| trailer | u16 | **endpoint 0** : version du protocole = 1 ; **autres** : CRC16 du JSON (l.473) |

Réponse : `seq_no | 0x8000` (u16) suivi du payload (l.497). La carte ne répond **que** si le bit 15 de
`endpoint_id` est à 1. Si le trailer est faux, la requête est ignorée sans réponse (l.475-478).

L'outil demande toujours une réponse (comme libfibre, l.232), y compris pour les écritures : l'ACK vide
sert d'accusé de réception.

## Lecture du JSON (endpoint 0)
- Payload de requête = offset u32 ; la réponse renvoie le fragment qui suit (61 octets au plus en USB).
  Répéter jusqu'à recevoir une réponse vide, puis concaténer (`legacy_protocol.cpp:298-318`,
  `legacy_object_client.cpp:347-374`). JSON 0.5.6 pour v3.x : 55 313 octets, soit environ 900 requêtes.
- **CRC16 du JSON** : polynôme `0x3D65`, **init = version du protocole = 1** (et non `0x1337`, qui ne sert
  qu'au tramage série) — `Firmware/fibre-cpp/endpoints_template.j2:33`, `legacy_object_client.cpp:393`.
  C'est le trailer de tous les autres endpoints. Pour le firmware 0.5.6 v3.x : `0xC1A0`.
- **Identifiant de version** : avec l'offset spécial `0xFFFFFFFF`, l'endpoint 0 renvoie un u32
  `json_version_id = (json_crc << 16) | crc16(init = json_crc, JSON)` (`legacy_protocol.cpp:305-307`,
  `endpoints_template.j2:34`). Une seule requête suffit donc pour savoir si le JSON en cache est le bon :
  c'est la clé du cache (SPEC §1).

## Structure du JSON
Généré par `tools/fibre-tools/interface_generator.py:462-541`. Une liste de membres, chacun étant :

| `type` | Champs | Remarque |
|---|---|---|
| `json` | `name: ""`, `id: 0`, `access: "r"` | premier élément, à ignorer |
| `object` | `name`, `members` | pas d'`id` |
| `function` | `name`, `id`, `inputs`, `outputs` | chaque argument est une propriété avec son propre `id` |
| propriété | `name`, `id`, `type`, `access` (`r` ou `rw`) | |

- Types de propriété (`legacy_object_client.cpp:182-194`) : `bool` (1), `int8`/`uint8` (1),
  `int16`/`uint16` (2), `int32`/`uint32` (4), `int64`/`uint64` (8), `float` (4), `endpoint_ref` (4).
- Les enums et flags apparaissent sous forme d'entier non signé de la plus petite taille qui contient leur
  valeur maximale (`interface_generator.py:443-458`) : `axis0.error` est un `uint32`, `motor.error` un
  `uint64`, `requested_state` un `uint8`. Le type enum lui-même n'est pas dans le JSON → il vient du code
  généré (`ODriveInterface.FindProperty(path).EnumType`).
- `endpoint_ref` = `endpoint_id` u16 + `json_crc` u16 (`legacy_object_client.cpp:512-513`).
- Le JSON n'utilise jamais d'échappement dans les chaînes.

## Opérations
- **Lecture** : requête sur l'id, payload vide, `expected_response_size` = taille du type.
- **Écriture** : payload = valeur, `expected_response_size` = 0, réponse (ACK vide) demandée.
- **Appel de fonction** (`legacy_object_client.cpp:673-684`) :
  1. écrire chaque entrée sur l'id de son argument (dans l'ordre de `inputs`) ;
  2. déclencher : requête sur l'id de la fonction, payload vide, réponse vide ;
  3. lire chaque sortie sur l'id de son argument (dans l'ordre de `outputs`).

  Les ids se suivent : fonction = `id`, entrées = `id+1…`, sorties ensuite
  (`interface_generator.py:508-539`) — mais on utilise toujours les ids lus dans le JSON.
  Ces trois étapes ne sont pas atomiques côté carte : la file de requêtes ne doit rien intercaler entre
  elles pour une même fonction.

## Robustesse
- Délai d'attente par requête (100 ms par défaut), 3 essais, puis la connexion est marquée « perdue ».
  Une réponse dont le `seq_no` ne correspond pas à la requête en cours est jetée.
- Accès sérialisé par carte (une seule requête en vol) via un `Channel<T>` ; l'acquisition des courbes
  regroupe ses lectures et passe dans la même file.
- Après `reboot()`, `save_configuration()` ou `erase_configuration()`, la carte se déconnecte : attendre
  la ré-énumération et relire le JSON (ou seulement `json_version_id` si le cache est valide).

## Références dans `ref/ODrive` (tag fw-v0.5.6)
- `Firmware/odrive-interface.yaml` : interfaces, enums, flags d'erreur, docs → `tools/ODrive.CodeGen`.
- `Firmware/fibre-cpp/` : `legacy_protocol.cpp/.hpp` (paquets, tramage), `legacy_object_client.cpp`
  (client de référence : lecture du JSON, appels de fonction), `crc.hpp`, `endpoints_template.j2`.
- `tools/fibre-tools/interface_generator.py` : génération du JSON et des endpoints.
- `Firmware/communication/` : USB (`interface_usb.cpp`), UART, CAN.
- `tools/odrive/utils.py` : `dump_errors`, référence pour le décodage des erreurs.
- **Il n'y a pas d'implémentation Python pure du protocole en 0.5.6** : `tools/odrive/pyfibre/fibre/libfibre.py`
  est une liaison ctypes vers libfibre (le C++ ci-dessus). Les vecteurs de test se déduisent donc de
  `crc.hpp` et de `legacy_protocol.cpp`.

## Données de test
- `tests/ODrive.Protocol.Tests/data/odrive-v3.6-0.5.6.json` : JSON d'endpoints **régénéré depuis les
  sources** par `tools/gen_reference_json.py` (le générateur Python du firmware, avec
  `--generate-endpoints ODrive3` comme `Firmware/Tupfile.lua:235,425`). À confirmer en phase 1 sur la
  carte : `json_version_id` doit valoir `0xC1A0xxxx`. Un écart signifierait que la carte n'exécute pas le
  firmware officiel 0.5.6.
- `tools/ODrive.CodeGen --check-json` vérifie que le portage C# du générateur reproduit ce JSON à l'octet près.

## Tests attendus (phase 1)
- Encodage / décodage des paquets, CRC8 et CRC16 (vecteurs calculés à la main depuis `crc.hpp`).
- Parsing du JSON de référence, calcul de son CRC (`0xC1A0`).
- Transport simulé : un serveur d'objets en mémoire qui répond comme la carte (mêmes règles de trailer,
  de bit d'ACK et de MTU).
