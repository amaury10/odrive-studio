using System.Diagnostics;
using System.Globalization;
using System.Text;
using ODrive.Model;
using ODrive.Protocol;
using ODrive.Protocol.Simulation;
using ODrive.Protocol.Transport;
using ODrive.Protocol.Transport.WinUsb;

// Outil console de la phase 1 : énumération, dump et contrôle d'acceptation du protocole natif.
//   odrive-cli list
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
        "dump" => await DumpAsync(),
        "check" => await CheckAsync(),
        _ => Help(),
    };
}
catch (Exception e) when (e is IOException or ODriveProtocolException or TimeoutException)
{
    Console.Error.WriteLine("Erreur : " + e.Message);
    return 1;
}

int Help()
{
    Console.WriteLine(
        """
        odrive-cli list                      liste les cartes ODrive branchées en USB
        odrive-cli dump  [--sim] [--out f]   écrit le JSON d'endpoints et toutes les valeurs de la carte
        odrive-cli check [--sim]             contrôle d'acceptation de la phase 1 (lecture, écriture, fonction, reboot)

        --sim : utilise la carte simulée au lieu de l'USB.
        """);
    return command == "help" ? 0 : 1;
}

int List()
{
    IReadOnlyList<WinUsbDeviceInfo> devices = WinUsbEnumerator.Enumerate();
    IReadOnlyList<string> instances = WinUsbEnumerator.FindNativeInterfaceInstances();

    Console.WriteLine($"{devices.Count} carte(s) ODrive accessible(s) par WinUSB :");
    foreach (WinUsbDeviceInfo device in devices)
    {
        Console.WriteLine("  " + device.DevicePath);
    }

    if (instances.Count > devices.Count)
    {
        Console.WriteLine();
        Console.WriteLine($"{instances.Count} interface(s) native(s) ODrive vue(s) sur le bus USB (VID 1209, PID 0D32, MI_02) :");
        foreach (string instance in instances)
        {
            Console.WriteLine("  " + instance);
        }
        Console.WriteLine("Au moins une n'est pas pilotée par WinUSB : un autre pilote a été forcé sur l'interface");
        Console.WriteLine("native (libusb-win32, libusbK…). Avec Zadig : Options > List All Devices, choisir");
        Console.WriteLine("« ODrive 3.6 Native Interface (Interface 2) », pilote WinUSB, Replace Driver.");
    }
    return devices.Count > 0 ? 0 : 1;
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
