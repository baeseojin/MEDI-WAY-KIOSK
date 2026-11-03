using System.IO;
using System.Text.Json;
using MediWay.Contracts;

namespace MediWay.Kiosk.Services;

public sealed record OfflineRegistration(CreateVisitRequest Request, IReadOnlyList<RoomResponse> Rooms, DateTimeOffset SavedAt);
public interface IOfflineRegistrationStore : IDisposable
{
    Task<OfflineRegistration?> LoadAsync(CancellationToken ct);
    Task SaveAsync(OfflineRegistration registration, CancellationToken ct);
    Task ClearAsync(Guid requestId, CancellationToken ct);
}

public sealed class OfflineRegistrationStore(string directory, string apiBaseUrl) : IOfflineRegistrationStore
{
    private sealed record Envelope(int Format, string Server, OfflineRegistration Registration);
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MEDI-WAY", "Offline");
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string directory = directory;
    private readonly string server = new Uri(apiBaseUrl.TrimEnd('/') + "/").AbsoluteUri;
    private FileStream? lease;
    private bool disposed;
    private string FilePath => Path.Combine(directory, "pending-registration.json");

    private void Open()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Directory.CreateDirectory(directory);
        lease ??= new FileStream(Path.Combine(directory, "kiosk.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private async Task<OfflineRegistration?> Read(CancellationToken ct)
    {
        if (!File.Exists(FilePath)) return null;
        if (new FileInfo(FilePath).Length > 65536) throw new InvalidDataException("Offline record is too large.");
        Envelope? value;
        try { value = JsonSerializer.Deserialize<Envelope>(await File.ReadAllTextAsync(FilePath, ct).ConfigureAwait(false)); }
        catch (JsonException ex) { throw new InvalidDataException("Offline record is invalid.", ex); }
        if (value is null || value.Format != 1 || value.Server != server) throw new InvalidDataException("Offline record or server identity is invalid.");
        Validate(value.Registration);
        return value.Registration;
    }
    private static void Validate(OfflineRegistration record)
    {
        if (record is not { Request: { } request, Rooms: { } rooms }
            || request.PatientId == Guid.Empty || request.RoomId == Guid.Empty || request.RequestId == Guid.Empty || request.ExpectedServiceDate is null
            || rooms.Count is < 1 or > 1000 || rooms.Any(r => r is null || r.Id == Guid.Empty || string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 200) || rooms.Select(r => r.Id).Distinct().Count() != rooms.Count
            || !rooms.Any(r => r.Id == request.RoomId) || record.SavedAt == default)
            throw new InvalidDataException("Offline registration is invalid.");
    }
    public async Task<OfflineRegistration?> LoadAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { Open(); return await Read(ct).ConfigureAwait(false); } finally { gate.Release(); }
    }
    public async Task SaveAsync(OfflineRegistration registration, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Open();
            Validate(registration);
            var old = await Read(ct).ConfigureAwait(false);
            if (old is not null)
            {
                if (old.Request != registration.Request) throw new IOException("Unresolved offline registration exists.");
                return;
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, server, registration));
            if (bytes.Length > 65536) throw new InvalidDataException("Offline record is too large.");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false); stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested(); File.Move(temporary, FilePath, overwrite: false);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            gate.Release();
        }
    }
    public async Task ClearAsync(Guid requestId, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Open(); var old = await Read(ct).ConfigureAwait(false);
            if (old is not null && old.Request.RequestId != requestId) throw new IOException("Offline identity mismatch.");
            ct.ThrowIfCancellationRequested(); File.Delete(FilePath);
        }
        finally { gate.Release(); }
    }
    public void Dispose()
    {
        gate.Wait();
        try { if (disposed) return; disposed = true; lease?.Dispose(); lease = null; }
        finally { gate.Release(); }
    }
}
