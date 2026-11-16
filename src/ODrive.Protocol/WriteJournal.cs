using System.Globalization;

namespace ODrive.Protocol;

public enum JournalEntryKind
{
    Write,
    Call,
}

/// <summary>Une action de l'outil sur une carte (docs/SAFETY.md : écritures tracées).</summary>
/// <param name="OldValue">Valeur lue juste avant l'écriture ; <c>null</c> pour un appel de fonction ou une écriture d'urgence.</param>
/// <param name="NewValue">Valeur écrite, ou arguments de l'appel (texte).</param>
public sealed record WriteJournalEntry(
    DateTimeOffset Timestamp, ulong SerialNumber, JournalEntryKind Kind, string Path, object? OldValue, object? NewValue)
{
    public static string Format(object? value) => value switch
    {
        null => "",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}

public interface IWriteJournal
{
    void Record(WriteJournalEntry entry);
}

/// <summary>Journal de session, en mémoire, exportable en CSV.</summary>
public sealed class WriteJournal : IWriteJournal
{
    private readonly Lock _gate = new();
    private readonly List<WriteJournalEntry> _entries = [];

    /// <summary>Levé sur le fil qui a fait l'écriture, pas sur celui de l'interface.</summary>
    public event EventHandler<WriteJournalEntry>? EntryAdded;

    public IReadOnlyList<WriteJournalEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public void Record(WriteJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _entries.Add(entry);
        }
        EntryAdded?.Invoke(this, entry);
    }

    public void ExportCsv(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLine("horodatage;carte;type;chemin;ancienne valeur;nouvelle valeur");
        foreach (WriteJournalEntry entry in Entries)
        {
            writer.WriteLine(string.Join(';',
                entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                entry.SerialNumber.ToString("X", CultureInfo.InvariantCulture),
                entry.Kind == JournalEntryKind.Write ? "écriture" : "appel",
                entry.Path,
                WriteJournalEntry.Format(entry.OldValue),
                WriteJournalEntry.Format(entry.NewValue)));
        }
    }
}
