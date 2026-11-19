using System.Diagnostics;
using System.Globalization;
using System.Text;
using ODrive.Model;
using ODrive.Protocol;
using ODrive.Protocol.Dfu;
using ODrive.Protocol.Simulation;
using ODrive.Protocol.Transport;
using ODrive.Protocol.Transport.WinUsb;
using ODrive.Tuning.Commissioning;
using ODrive.Tuning.Profiles;

// Outil console de la phase 1 : énumération, dump et contrôle d'acceptation du protocole natif.
//   odrive-cli list
//   odrive-cli fix-usb
//   odrive-cli dump  [--sim] [--out fichier.json]
//   odrive-cli check [--sim]

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = Encoding.UTF8;

string command = args.Length > 0 ? args[0] : "help";
bool simulated = args.Contains("--sim");
string? outPath = null;
for (int i = 1; i < args.Length - 1; i++)
{
    if (args[i] == "--out")
    {
        outPath = args[i + 1];
    }
}

// Une seule carte simulée pour toute la durée du programme : elle survit à ses propres reboots.
SimulatedODrive simulatedBoard = SimulatedODrive.CreateReference();
var schemaCache = new MemorySchemaCache();

try
{
    return command switch
    {
        "list" => List(),
        "fix-usb" => FixUsb(),
        "dump" => await DumpAsync(),
        "check" => await CheckAsync(),
        "commission" => await CommissionAsync(),
        "hall" => await HallAsync(),
        "get" => await GetAsync(),
        "calibrate" => await CalibrateAsync(),
        "set" => await SetAsync(),
        "enter-dfu" => await EnterDfuAsync(),
        "dfu" => DfuInfo(),
        "backup" => Backup(),
        "flash" => Flash(),
        "leave-dfu" => LeaveDfu(),
        "image" => Image(),
        _ => Help(),
    };
}
catch (Exception e) when (e is IOException or ODriveProtocolException or TimeoutException or KeyNotFoundException
    or InvalidDataException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("Erreur : " + e.Message);
    return 1;
}

int Help()
{
    Console.WriteLine(
        """
        odrive-cli list                      liste les cartes ODrive branchées en USB
        odrive-cli fix-usb                   installe le pilote WinUSB et le GUID d'interface manquants (administrateur)
        odrive-cli dump  [--sim] [--out f]   écrit le JSON d'endpoints et toutes les valeurs de la carte
        odrive-cli check [--sim]             contrôle d'acceptation de la phase 1 (lecture, écriture, fonction, reboot)
        odrive-cli commission [--axis n] [--brake] [--sim]
                                             écrit le préréglage hoverboard, sauvegarde et redémarre ; aucun
                                             courant dans le moteur (pas de calibration)
        odrive-cli hall [--axis n] [--seconds s] [--sim]
                                             affiche les capteurs Hall pendant qu'on tourne la roue à la main

        odrive-cli get <chemin> [<chemin>…]    lit des propriétés
        odrive-cli set <chemin> <valeur> [--save]
                                             écrit une propriété (refusé hors IDLE) ; --save sauvegarde et redémarre

        odrive-cli calibrate <motor|hall|offset|full> --yes [--axis n]
                                             lance une calibration : LE MOTEUR EST ALIMENTÉ ET PEUT TOURNER.
                                             Ctrl+C remet l'axe en IDLE.

        Firmware (bootloader DFU du STM32) :
        odrive-cli enter-dfu                 redémarre la carte dans son bootloader
        odrive-cli dfu                       décrit la carte en mode DFU (régions mémoire, version en OTP)
        odrive-cli backup [--out f.bin]      sauvegarde toute la flash (firmware + configuration)
        odrive-cli flash <f.elf|.hex|.bin> --yes efface la flash, écrit et vérifie ; --stay pour rester en DFU
        odrive-cli image <fichier>           décrit un fichier firmware (.elf, .hex) ou une sauvegarde (.bin)
        odrive-cli leave-dfu                 quitte le bootloader et démarre le firmware

        --sim : utilise la carte simulée au lieu de l'USB.
        """);
    return command == "help" ? 0 : 1;
}

int List()
{
    IReadOnlyList<WinUsbDeviceInfo> devices = WinUsbEnumerator.Enumerate();
    IReadOnlyList<NativeInterfaceStatus> present = WinUsbEnumerator.FindNativeInterfaces();

    Console.WriteLine($"{devices.Count} carte(s) ODrive accessible(s) par WinUSB :");
    foreach (WinUsbDeviceInfo device in devices)
    {
        Console.WriteLine("  " + device.DevicePath);
    }

    if (UsbDriverDiagnosis.Explain(present, devices.Count) is { } hint)
    {
        Console.WriteLine();
        Console.WriteLine($"{present.Count} interface(s) native(s) ODrive vue(s) sur le bus USB (VID 1209, PID 0D32, MI_02) :");
        foreach (NativeInterfaceStatus status in present)
        {
            string guids = status.InterfaceGuids.Count == 0 ? "aucun" : string.Join(", ", status.InterfaceGuids);
            Console.WriteLine($"  {status.InstanceId}  pilote = {status.Service ?? "aucun"}, GUID d'interface = {guids}");
        }
        Console.WriteLine(hint);
    }
    return devices.Count > 0 ? 0 : 1;
}

// Enregistre le GUID d'interface que le firmware 0.5.6 ne transmet pas à Windows (voir UsbDriverDiagnosis),
// puis redémarre le périphérique pour que WinUSB le publie.
int FixUsb()
{
    IReadOnlyList<string> report;
    try
    {
        report = WinUsbEnumerator.Repair();
    }
    catch (UnauthorizedAccessException e)
    {
        Console.Error.WriteLine(e.Message);
        Console.Error.WriteLine("Droits administrateur requis : relancer cette commande dans un terminal "
            + "ouvert « en tant qu'administrateur ».");
        return 1;
    }

    if (report.Count == 0)
    {
        Console.WriteLine("Rien à corriger : aucune interface sans pilote ni sans GUID d'interface.");
        return List();
    }
    foreach (string line in report)
    {
        Console.WriteLine(line);
    }

    // Laisse à Windows le temps de redémarrer le périphérique.
    var watch = Stopwatch.StartNew();
    while (WinUsbEnumerator.Enumerate().Count == 0 && watch.Elapsed < TimeSpan.FromSeconds(5))
    {
        Thread.Sleep(250);
    }
    if (WinUsbEnumerator.Enumerate().Count == 0)
    {
        Console.WriteLine("La carte n'est pas encore visible : débrancher puis rebrancher son câble USB, puis "
            + "« odrive-cli list ».");
        return 1;
    }
    return List();
}

async Task<int> EnterDfuAsync()
{
    await using ODriveDevice device = await ConnectAsync(TimeSpan.Zero);
    PrintIdentity(device);
    await device.EnterDfuModeAsync();
    Console.WriteLine("enter_dfu_mode() envoyé. Attente du bootloader…");

    var watch = Stopwatch.StartNew();
    while (WinUsbEnumerator.FindDfuDevices().Count == 0 && watch.Elapsed < TimeSpan.FromSeconds(10))
    {
        await Task.Delay(250);
    }
    await Task.Delay(500); // laisse Windows finir d'installer le périphérique
    return DfuInfo();
}

// Ouvre le bootloader, ou explique pourquoi c'est impossible.
DfuSeDevice? OpenDfu()
{
    IReadOnlyList<WinUsbDeviceInfo> usable = WinUsbEnumerator.EnumerateDfu();
    IReadOnlyList<NativeInterfaceStatus> present = WinUsbEnumerator.FindDfuDevices();
    if (usable.Count == 0)
    {
        if (present.Count == 0)
        {
            Console.WriteLine("Aucune carte en mode DFU (STM32 BOOTLOADER, VID 0483, PID DF11) sur le bus USB.");
            return null;
        }
        foreach (NativeInterfaceStatus status in present)
        {
            string guids = status.InterfaceGuids.Count == 0 ? "aucun" : string.Join(", ", status.InterfaceGuids);
            Console.WriteLine($"  {status.InstanceId}  pilote = {status.Service ?? "aucun"}, GUID d'interface = {guids}");
        }
        Console.WriteLine(UsbDriverDiagnosis.ExplainDfu(present, 0));
        return null;
    }
    return new DfuSeDevice(WinUsbDfuTransport.Open(usable[0]));
}

int DfuInfo()
{
    using DfuSeDevice? dfu = OpenDfu();
    if (dfu is null)
    {
        return 1;
    }

    Console.WriteLine($"Bootloader STM32, numéro de série {dfu.SerialNumber}");
    foreach (IGrouping<string, DfuSector> region in dfu.Sectors.GroupBy(sector => sector.Region))
    {
        Console.WriteLine($"  {region.Key} : 0x{region.First().Address:X8}, {region.Count()} secteur(s), "
            + $"{region.Sum(sector => sector.Length)} octets");
    }
    (int Major, int Minor, int Variant)? hardware = FirmwareUpdater.ReadHardwareVersion(dfu);
    Console.WriteLine(hardware is { } version
        ? $"  version matérielle en OTP : v{version.Major}.{version.Minor}-{version.Variant}V"
        : "  OTP vierge : carte non officielle, version matérielle inconnue du bootloader.");
    return 0;
}

int Backup()
{
    using DfuSeDevice? dfu = OpenDfu();
    if (dfu is null)
    {
        return 1;
    }

    var watch = Stopwatch.StartNew();
    byte[] flash = FirmwareUpdater.ReadFlash(dfu, new DfuConsoleProgress());
    Console.WriteLine();
    outPath ??= $"odrive-{dfu.SerialNumber}-flash-{DateTime.Now:yyyyMMdd-HHmmss}.bin";
    File.WriteAllBytes(outPath, flash);
    Console.WriteLine($"Sauvegarde écrite : {Path.GetFullPath(outPath)} ({flash.Length} octets, {watch.ElapsedMilliseconds} ms)");
    Console.WriteLine("  SHA-256 : " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(flash)));
    bool plausible = FirmwareImage.FromBinary(flash).LooksLikeFirmware(out string? reason);
    Console.WriteLine(plausible
        ? "  La sauvegarde commence par une table de vecteurs valide : elle est restaurable par « flash »."
        : "  ATTENTION : la sauvegarde ne ressemble pas à un firmware (" + reason + ")");
    return plausible ? 0 : 2;
}

int Flash()
{
    string? file = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && a != outPath);
    if (file is null || !args.Contains("--yes"))
    {
        Console.Error.WriteLine("Usage : odrive-cli flash <fichier.elf|.hex|.bin> --yes [--stay]");
        Console.Error.WriteLine("Efface TOUTE la flash de la carte (firmware et configuration). Faire « backup » avant.");
        return 1;
    }

    FirmwareImage image = FirmwareImage.Load(file);
    using DfuSeDevice? dfu = OpenDfu();
    if (dfu is null)
    {
        return 1;
    }

    Console.WriteLine($"Écriture de {Path.GetFileName(file)} ({image.Segments.Sum(s => s.Data.Length)} octets) "
        + $"sur le bootloader {dfu.SerialNumber}");
    FirmwareUpdater.Flash(dfu, image, new DfuConsoleProgress());
    Console.WriteLine();
    Console.WriteLine("Écriture vérifiée.");
    if (!args.Contains("--stay"))
    {
        dfu.Leave();
        Console.WriteLine("Bootloader quitté : la carte redémarre sur le nouveau firmware.");
    }
    return 0;
}

int Image()
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage : odrive-cli image <fichier.elf|.hex|.bin>");
        return 1;
    }

    FirmwareImage image = FirmwareImage.Load(args[1]);
    foreach (FirmwareSegment segment in image.Segments)
    {
        Console.WriteLine($"  0x{segment.Address:X8} - 0x{segment.End - 1:X8}  {segment.Data.Length} octets  "
            + "SHA-256 " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(segment.Data)));
    }
    bool plausible = image.LooksLikeFirmware(out string? reason);
    Console.WriteLine(plausible ? "  Table de vecteurs valide." : "  PAS un firmware : " + reason);
    Console.WriteLine(FirmwareImage.HardwareVersionFromFileName(args[1]) is { } version
        ? $"  Carte visée d'après le nom : v{version.Major}.{version.Minor}-{version.Variant}V"
        : "  Carte visée : le nom du fichier ne la donne pas.");
    return plausible ? 0 : 2;
}

int LeaveDfu()
{
    using DfuSeDevice? dfu = OpenDfu();
    if (dfu is null)
    {
        return 1;
    }
    dfu.Leave();
    Console.WriteLine("Bootloader quitté : la carte redémarre sur son firmware.");
    return 0;
}

async Task<int> DumpAsync()
{
    await using ODriveDevice device = await ConnectAsync(TimeSpan.Zero);
    PrintIdentity(device);

    var watch = Stopwatch.StartNew();
    int total = device.Schema.Properties.Count;
    var progress = new InlineProgress(done =>
    {
        if (done % 50 == 0 || done == total)
        {
            Console.Write($"\rLecture des propriétés : {done}/{total}");
        }
    });
    DeviceDump dump = await DeviceDump.CaptureAsync(device, progress);
    Console.WriteLine($"  ({watch.ElapsedMilliseconds} ms, {total * 1000.0 / Math.Max(1, watch.ElapsedMilliseconds):F0} lectures/s)");

    outPath ??= $"odrive-{device.SerialNumber:X}-{dump.JsonVersionId:X8}.dump.json";
    await using (FileStream file = File.Create(outPath))
    {
        dump.Save(file);
    }
    Console.WriteLine("Dump écrit : " + Path.GetFullPath(outPath));
    return ReportReferenceJson(device) ? 0 : 2;
}

async Task<int> CheckAsync()
{
    const string VelLimitPath = "axis0.controller.config.vel_limit";
    ODriveDevice device = await ConnectAsync(TimeSpan.Zero);
    ulong serial = device.SerialNumber;
    try
    {
        PrintIdentity(device);
        bool referenceOk = ReportReferenceJson(device);
        var odrv = new ODrive3(device);

        float vbus = await odrv.VbusVoltage.GetAsync();
        Step("lecture de vbus_voltage", vbus is > 5f and < 60f, $"{vbus:F2} V");

        float original = await odrv.Axis0.Controller.Config.VelLimit.GetAsync();
        float probe = original + 0.5f;
        await odrv.Axis0.Controller.Config.VelLimit.SetAsync(probe);
        float readBack = await odrv.Axis0.Controller.Config.VelLimit.GetAsync();
        await odrv.Axis0.Controller.Config.VelLimit.SetAsync(original);
        float restored = await odrv.Axis0.Controller.Config.VelLimit.GetAsync();
        Step($"écriture de {VelLimitPath}", readBack == probe && restored == original,
            $"{original} → {readBack} → {restored} tours/s");

        await odrv.ClearErrorsAsync();
        AxisError axisError = await odrv.Axis0.Error.GetAsync();
        Step("appel de clear_errors()", axisError == AxisError.None, $"axis0.error = {axisError}");

        var watch = Stopwatch.StartNew();
        await device.RebootAsync();
        Step("reboot() : liaison coupée", !device.IsConnected, "");
        await device.DisposeAsync();

        device = await ConnectAsync(TimeSpan.FromSeconds(15), serial);
        float vbusAfter = await new ODrive3(device).VbusVoltage.GetAsync();
        Step("reconnexion après reboot", device.SerialNumber == serial,
            $"{watch.ElapsedMilliseconds} ms, vbus_voltage = {vbusAfter:F2} V");

        Console.WriteLine(referenceOk
            ? "Acceptation de la phase 1 : OK."
            : "Protocole OK, mais le JSON de la carte diffère du JSON de référence (voir ci-dessus).");
        return referenceOk ? 0 : 2;
    }
    finally
    {
        await device.DisposeAsync();
    }

    static void Step(string name, bool ok, string detail)
    {
        Console.WriteLine($"[{(ok ? "OK" : "ÉCHEC")}] {name}{(detail.Length > 0 ? " : " + detail : "")}");
        if (!ok)
        {
            throw new ODriveProtocolException("Étape en échec : " + name);
        }
    }
}

int IntOption(string name, int fallback)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length
        && int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
        ? value
        : fallback;
}

// Écrit le préréglage hoverboard, sauvegarde et redémarre. Ne demande aucun état d'axe : le moteur ne reçoit
// aucun courant.
async Task<int> CommissionAsync()
{
    var options = new HoverboardOptions { Axis = IntOption("--axis", 0), BrakeResistorConnected = args.Contains("--brake") };
    IReadOnlyList<CommissioningSetting> settings = HoverboardPreset.Build(options);

    ODriveDevice device = await ConnectAsync(TimeSpan.Zero);
    ulong serial = device.SerialNumber;
    try
    {
        PrintIdentity(device);
        foreach (int axis in new[] { 0, 1 })
        {
            object state = await device.ReadAsync(device.Schema.FindProperty($"axis{axis}.current_state")!);
            if (Convert.ToInt32(state, CultureInfo.InvariantCulture) != (int)AxisState.Idle)
            {
                Console.Error.WriteLine($"axis{axis} n'est pas en IDLE : rien n'a été écrit.");
                return 1;
            }
        }

        int changed = 0;
        foreach (CommissioningSetting setting in settings)
        {
            if (device.Schema.FindProperty(setting.Path) is not { } property)
            {
                continue;
            }
            object before = await device.ReadAsync(property);
            object wanted = HoverboardPreset.ToEndpointValue(property.Type, setting.Value);
            if (!Equals(before, wanted))
            {
                changed++;
                Console.WriteLine($"  {setting.Path} : {before} → {wanted} {setting.Unit}");
            }
        }

        IReadOnlyList<CommissioningFailure> failures = await HoverboardPreset.ApplyAsync(device, settings);
        foreach (CommissioningFailure failure in failures)
        {
            Console.WriteLine($"  ÉCHEC {failure.Path} : {failure.Message}");
        }
        Console.WriteLine($"{changed} valeur(s) modifiée(s) sur {settings.Count}, {failures.Count} échec(s).");
        if (failures.Count > 0)
        {
            return 2;
        }

        bool saved = await device.SaveConfigurationAsync();
        Console.WriteLine(saved ? "Configuration sauvegardée, la carte redémarre." : "save_configuration() a répondu false.");
        await device.DisposeAsync();

        device = await ConnectAsync(TimeSpan.FromSeconds(15), serial);
        int wrong = 0;
        foreach (CommissioningSetting setting in settings)
        {
            if (device.Schema.FindProperty(setting.Path) is not { } property)
            {
                continue;
            }
            object after = await device.ReadAsync(property);
            object wanted = HoverboardPreset.ToEndpointValue(property.Type, setting.Value);
            if (!Equals(after, wanted))
            {
                wrong++;
                Console.WriteLine($"  NON CONSERVÉ {setting.Path} : {after} au lieu de {wanted}");
            }
        }
        Console.WriteLine($"Après redémarrage : {settings.Count - wrong}/{settings.Count} valeurs relues conformes.");
        await PrintAxisStatusAsync(device, options.Axis);
        return wrong == 0 ? 0 : 2;
    }
    finally
    {
        await device.DisposeAsync();
    }
}

async Task PrintAxisStatusAsync(ODriveDevice device, int axis)
{
    foreach (string path in new[]
    {
        "error", "vbus_voltage", $"axis{axis}.current_state", $"axis{axis}.error", $"axis{axis}.motor.error",
        $"axis{axis}.encoder.error", $"axis{axis}.controller.error", $"axis{axis}.motor.is_armed",
        $"axis{axis}.encoder.hall_state", $"axis{axis}.encoder.shadow_count",
    })
    {
        Console.WriteLine($"  {path} = {await device.ReadAsync(device.Schema.FindProperty(path)!)}");
    }
}

// Lecture seule : montre hall_state et shadow_count à chaque changement, puis le bilan des états vus.
async Task<int> HallAsync()
{
    int axis = IntOption("--axis", 0);
    int seconds = IntOption("--seconds", 30);
    await using ODriveDevice device = await ConnectAsync(TimeSpan.Zero);
    PrintIdentity(device);
    var hall = device.Schema.FindProperty($"axis{axis}.encoder.hall_state")!;
    var count = device.Schema.FindProperty($"axis{axis}.encoder.shadow_count")!;
    var error = device.Schema.FindProperty($"axis{axis}.encoder.error")!;

    var seen = new SortedDictionary<int, int>();
    int last = -1;
    int transitions = 0;
    var watch = Stopwatch.StartNew();
    Console.WriteLine($"Tourner la roue à la main ({seconds} s)…");
    while (watch.Elapsed < TimeSpan.FromSeconds(seconds))
    {
        int state = Convert.ToInt32(await device.ReadAsync(hall), CultureInfo.InvariantCulture);
        seen[state] = seen.GetValueOrDefault(state) + 1;
        if (state != last)
        {
            transitions += last < 0 ? 0 : 1;
            last = state;
            Console.WriteLine($"  {watch.Elapsed.TotalSeconds,6:F2} s  hall_state = {state} ({Convert.ToString(state, 2).PadLeft(3, '0')})"
                + $"  shadow_count = {await device.ReadAsync(count)}");
        }
    }

    Console.WriteLine($"{transitions} transition(s). États vus : "
        + string.Join(", ", seen.Select(pair => $"{pair.Key} ({pair.Value} lectures)")));
    Console.WriteLine($"  shadow_count final = {await device.ReadAsync(count)}, encoder.error = {await device.ReadAsync(error)}");
    return 0;
}

async Task<int> GetAsync()
{
    await using ODriveDevice device = await ConnectAsync(TimeSpan.Zero);
    foreach (string path in args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)))
    {
        Console.WriteLine(device.Schema.FindProperty(path) is { } property
            ? $"{path} = {ConfigProfile.FormatValue(await device.ReadAsync(property))}"
            : $"{path} : propriété absente de cette carte");
    }
    return 0;
}

// Écriture d'un réglage. Refusée si un axe n'est pas en IDLE, et pour requested_state : la console ne met
// jamais un moteur en mouvement.
async Task<int> SetAsync()
{
    string[] words = [.. args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal))];
    if (words.Length != 2)
    {
        Console.Error.WriteLine("Usage : odrive-cli set <chemin> <valeur> [--save]");
        return 1;
    }

    ODriveDevice device = await ConnectAsync(TimeSpan.Zero);
    ulong serial = device.SerialNumber;
    try
    {
        if (device.Schema.FindProperty(words[0]) is not { } property)
        {
            Console.Error.WriteLine($"{words[0]} : propriété absente de cette carte.");
            return 1;
        }
        object value = ConfigProfile.ParseValue(property.Type, words[1]);
        SafetyDecision decision = await SafetyGuard.AuthorizeConfigurationChangeAsync(device);
        if (!decision.IsAllowed || SafetyGuard.IsMotionRequest(property.Path, value))
        {
            Console.Error.WriteLine("Écriture refusée : " + (decision.Reason ?? "elle mettrait un moteur en mouvement."));
            return 1;
        }

        object before = await device.ReadAsync(property);
        await device.WriteAsync(property, value);
        Console.WriteLine($"{property.Path} : {ConfigProfile.FormatValue(before)} → {ConfigProfile.FormatValue(await device.ReadAsync(property))}");
        if (!args.Contains("--save"))
        {
            return 0;
        }

        bool saved = await device.SaveConfigurationAsync();
        Console.WriteLine(saved ? "Configuration sauvegardée, la carte redémarre." : "save_configuration() a répondu false.");
        await device.DisposeAsync();
        device = await ConnectAsync(TimeSpan.FromSeconds(15), serial);
        Console.WriteLine($"Après redémarrage : {property.Path} = {ConfigProfile.FormatValue(await device.ReadAsync(device.Schema.FindProperty(property.Path)!))}");
        return 0;
    }
    finally
    {
        await device.DisposeAsync();
    }
}

// Calibration d'un axe, par le même chemin que l'assistant (SafetyGuard puis AxisStateRunner). --yes tient
// lieu de confirmation « roues soulevées » ; Ctrl+C annule et remet l'axe en IDLE.
async Task<int> CalibrateAsync()
{
    string kind = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "";
    (AxisState state, double timeout) = kind switch
    {
        "motor" => (AxisState.MotorCalibration, 20d),
        "hall" => (AxisState.EncoderHallPolarityCalibration, 30d),
        "offset" => (AxisState.EncoderOffsetCalibration, 40d),
        "full" => (AxisState.FullCalibrationSequence, 90d),
        _ => (AxisState.Undefined, 0d),
    };
    if (state == AxisState.Undefined || !args.Contains("--yes"))
    {
        Console.Error.WriteLine("Usage : odrive-cli calibrate <motor|hall|offset|full> --yes [--axis n]");
        Console.Error.WriteLine("Le moteur est alimenté et peut tourner : roue libre ou bloquée mécaniquement, jamais posée.");
        return 1;
    }

    int axis = IntOption("--axis", 0);
    await using ODriveDevice device = await ConnectAsync(TimeSpan.Zero);
    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cancel.Cancel();
    };

    AxisState last = AxisState.Undefined;
    var progress = new InlineStateProgress(now =>
    {
        if (now != last)
        {
            last = now;
            Console.WriteLine($"  état : {now}");
        }
    });
    try
    {
        AxisRunResult result = await AxisStateRunner.RunAsync(device, axis, state, TimeSpan.FromSeconds(timeout),
            new SafetyGuard(), _ => true, progress, null, cancel.Token);
        Console.WriteLine(result.Message);
        foreach (string path in new[]
        {
            "vbus_voltage", $"axis{axis}.motor.is_calibrated", $"axis{axis}.motor.config.phase_resistance",
            $"axis{axis}.motor.config.phase_inductance", $"axis{axis}.encoder.config.hall_polarity_calibrated",
            $"axis{axis}.encoder.config.hall_polarity", $"axis{axis}.encoder.is_ready",
            $"axis{axis}.encoder.config.phase_offset_float", $"axis{axis}.encoder.shadow_count",
        })
        {
            Console.WriteLine($"  {path} = {ConfigProfile.FormatValue(await device.ReadAsync(device.Schema.FindProperty(path)!))}");
        }
        return result.Success ? 0 : 2;
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("Annulé : l'axe a été remis en IDLE.");
        return 1;
    }
}

// Ouvre la première carte (ou celle du numéro de série demandé), en attendant sa ré-énumération au besoin.
async Task<ODriveDevice> ConnectAsync(TimeSpan wait, ulong? serialNumber = null)
{
    var options = new ODriveDeviceOptions { SchemaCache = schemaCache };
    var watch = Stopwatch.StartNew();
    string lastError = "aucune carte ODrive trouvée en USB (essayer « odrive-cli list »).";

    while (true)
    {
        foreach (ITransport transport in OpenCandidates())
        {
            ODriveDevice? device = null;
            try
            {
                device = await ODriveDevice.ConnectAsync(transport, options);
                if (serialNumber is null || device.SerialNumber == serialNumber)
                {
                    return device;
                }
                await device.DisposeAsync();
            }
            catch (IOException e)
            {
                // La carte vient d'être ré-énumérée et n'est pas encore prête, ou elle est déjà ouverte ailleurs.
                lastError = e.Message;
                await transport.DisposeAsync();
            }
        }

        if (watch.Elapsed >= wait)
        {
            throw new IOException(lastError);
        }
        await Task.Delay(200);
    }

    IEnumerable<ITransport> OpenCandidates()
    {
        if (simulated)
        {
            yield return new SimulatedTransport(simulatedBoard);
            yield break;
        }

        foreach (WinUsbDeviceInfo info in WinUsbEnumerator.Enumerate())
        {
            WinUsbTransport? transport = null;
            try
            {
                transport = WinUsbTransport.Open(info);
            }
            catch (IOException e)
            {
                lastError = e.Message;
            }
            if (transport is not null)
            {
                yield return transport;
            }
        }
    }
}

void PrintIdentity(ODriveDevice device)
{
    Console.WriteLine($"Carte : {device.TransportDescription}");
    Console.WriteLine($"  numéro de série : {device.SerialNumber:X}");
    Console.WriteLine($"  json_version_id : 0x{device.Schema.JsonVersionId:X8} "
        + $"({device.Schema.Json.Length} octets, {device.Schema.Properties.Count} propriétés, "
        + $"{device.Schema.Functions.Count} fonctions)");
}

// Compare le JSON de la carte au JSON de référence régénéré depuis les sources du firmware 0.5.6.
bool ReportReferenceJson(ODriveDevice device)
{
    EndpointSchema reference = SimulatedODrive.CreateReference().Schema;
    if (device.Schema.JsonVersionId == reference.JsonVersionId)
    {
        Console.WriteLine("  JSON identique au JSON de référence du firmware 0.5.6 (v3.x).");
        return true;
    }

    Console.WriteLine($"  JSON DIFFÉRENT de la référence (attendu 0x{reference.JsonVersionId:X8}) : la carte n'exécute "
        + "pas le firmware officiel 0.5.6 pour v3.x.");
    var known = ODriveInterface.Properties.Select(p => p.Path).ToHashSet();
    var actual = device.Schema.Properties.Select(p => p.Path).ToHashSet();
    Console.WriteLine($"  propriétés absentes de la carte : {known.Except(actual).Count()}, "
        + $"inconnues du code généré : {actual.Except(known).Count()}");
    return false;
}

internal sealed class InlineProgress(Action<int> report) : IProgress<int>
{
    public void Report(int value) => report(value);
}

internal sealed class InlineStateProgress(Action<AxisState> report) : IProgress<AxisState>
{
    public void Report(AxisState value) => report(value);
}

internal sealed class DfuConsoleProgress : IProgress<DfuProgress>
{
    public void Report(DfuProgress value) =>
        Console.Write($"\r{value.Stage} : {value.Done}/{value.Total}        ");
}
