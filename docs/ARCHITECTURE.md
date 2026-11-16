# Architecture

```
ODriveStudio.App (WPF, MVVM)
  ├── Views / ViewModels : Dashboard, Explorer, Wizard, Plots, Tuning, Autotune, Anticogging, Can, Profiles
  └── Services : DeviceManager, PlotAcquisition, ProfileService, SafetyGuard, WriteJournal
        │
ODrive.Tuning ─── Calibration, Autotune (Identification, GainDesign, StepAnalysis), Anticogging
        │
ODrive.Model (généré) ─── enums, flags d'erreur + descriptions, wrappers typés (Axis, Motor, Encoder…)
        │
ODrive.Protocol
  ├── Transport : ITransport ← WinUsbTransport, SerialFramedTransport, SimulatedTransport
  ├── Fibre : PacketCodec, Crc, JsonDescriptor, RequestQueue (une requête en vol, priorités)
  └── Objects : RemoteObject / RemoteProperty<T> / RemoteFunction (arbre dynamique depuis le JSON)

ODrive.Can ─── CanSimpleCodec, ICanBus ← PcanBus, GsUsbBus, SlcanBus, SimulatedCanBus
```

## Principes
- `ODrive.Protocol` ne connaît pas l'ODrive « métier » : il expose un arbre d'objets distants générique.
- `ODrive.Model` est un fin habillage typé, entièrement généré : `odrv0.axis0.motor.config.pole_pairs`
  devient `odrv.Axis0.Motor.Config.PolePairs.ReadAsync()`. Le générateur vérifie à l'exécution que
  chaque chemin existe dans le JSON de la carte connectée (sinon `NotSupported`).
- File de requêtes par carte avec trois priorités : **urgence** (IDLE) > **commandes utilisateur** >
  **acquisition**.
- L'acquisition des courbes produit des `Sample` horodatés (Stopwatch) dans un tampon circulaire ;
  l'interface les lit à 30 Hz.
- `SimulatedTransport` : sert un JSON réel capturé et simule un axe (moteur DC + inertie + frottement +
  couple de détente) pour développer l'assistant, les courbes, l'autotune et l'anticogging sans carte.
- Injection de dépendances : `Microsoft.Extensions.Hosting` dans l'application WPF.
- Journalisation : `Microsoft.Extensions.Logging` + fichier.
