// Génère src/ODrive.Model/Generated/*.g.cs depuis ref/ODrive/Firmware/odrive-interface.yaml (CLAUDE.md, règle 2).
//
// Usage : dotnet run --project tools/ODrive.CodeGen [-- options]
//   --yaml <fichier>        définitions (défaut : ref/ODrive/Firmware/odrive-interface.yaml)
//   --out <dossier>         sortie (défaut : src/ODrive.Model/Generated)
//   --root <interface>      interface racine (défaut : ODrive3, cf. ref/ODrive/Firmware/Tupfile.lua:235)
//   --check-json <fichier>  compare le JSON d'endpoints reconstruit à celui du générateur Python de
//                           référence (tools/gen_reference_json.py) ; code de sortie 2 si différent
using System.Globalization;
using System.Text;
using ODrive.CodeGen;

// Sortie indépendante des paramètres régionaux de la machine.
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

string repoRoot = FindRepoRoot();
string yaml = Path.Combine(repoRoot, "ref", "ODrive", "Firmware", "odrive-interface.yaml");
string outDir = Path.Combine(repoRoot, "src", "ODrive.Model", "Generated");
string root = "ODrive3";
string? checkJson = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--yaml": yaml = args[++i]; break;
        case "--out": outDir = args[++i]; break;
        case "--root": root = args[++i]; break;
        case "--check-json": checkJson = args[++i]; break;
        default:
            Console.Error.WriteLine($"Option inconnue : {args[i]}");
            return 1;
    }
}

if (!File.Exists(yaml))
{
    Console.Error.WriteLine($"Introuvable : {yaml}");
    Console.Error.WriteLine("git clone --depth 1 --branch fw-v0.5.6 https://github.com/odriverobotics/ODrive.git ref/ODrive");
    return 1;
}

var model = InterfaceModel.Load(yaml);
var emitter = new Emitter(model, root);

if (checkJson is not null)
{
    string expected = File.ReadAllText(checkJson);
    string actual = emitter.EmitEndpointJson();
    if (expected != actual)
    {
        int at = Enumerable.Range(0, Math.Min(expected.Length, actual.Length)).FirstOrDefault(i => expected[i] != actual[i], -1);
        if (at < 0) at = Math.Min(expected.Length, actual.Length);
        Console.Error.WriteLine($"JSON différent de la référence à l'offset {at} (attendu {expected.Length} car., obtenu {actual.Length}).");
        Console.Error.WriteLine("  attendu : " + Excerpt(expected, at));
        Console.Error.WriteLine("  obtenu  : " + Excerpt(actual, at));
        return 2;
    }
    Console.WriteLine($"JSON d'endpoints identique à la référence ({actual.Length} octets).");
}

Directory.CreateDirectory(outDir);
Write("Enums.g.cs", emitter.EmitEnums());
Write("EnumMetadata.g.cs", emitter.EmitEnumMetadata());
Write("InterfaceMetadata.g.cs", emitter.EmitInterfaceMetadata());
Write("Wrappers.g.cs", emitter.EmitWrappers());

var (properties, functions) = emitter.Flatten();
Console.WriteLine($"{model.Enums.Count()} enums, {properties.Count} propriétés, {functions.Count} fonctions → {outDir}");
return 0;

void Write(string name, string content) =>
    File.WriteAllText(Path.Combine(outDir, name), content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

static string Excerpt(string text, int at) =>
    text[Math.Max(0, at - 60)..Math.Min(text.Length, at + 60)];

static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "ODriveStudio.slnx")))
            return dir.FullName;
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "ODriveStudio.slnx")))
            return dir.FullName;
    throw new InvalidOperationException("Racine du dépôt introuvable (ODriveStudio.slnx).");
}
