using System.Security.Cryptography;
using System.Text.Json;
using CompanyDlp.Contracts;
using CompanyDlp.Core;

namespace CompanyDlp.Service;

// One record per PDF/image file that has ever been watermarked while ActionKeys.FileWatermarkDisable
// existed - see ContentWatermarker's class comment for why PDF/images (unlike Word/PowerPoint/Excel)
// cannot have their tile layer surgically removed after the fact: both watermark layers are baked
// into one flattened surface with no separable "tile" object. This store captures a corner-only
// (tile-free) render at first-watermark time, encrypted locally with a random per-record AES-256
// key (the DEK), so that render can be restored later without ever having kept an unencrypted copy
// of pre-tile content sitting on disk.
//
// The DEK itself is never persisted in the clear beyond the brief window between generation and a
// successful wrap call: WatermarkEscrowSyncWorker sends it to BackendApiClient.WrapFileKeyAsync -
// the exact same envelope-encryption endpoint FileProtectionEngine already uses for .dlpenc file
// keys (ASP.NET Core Data Protection on the backend, a master key the agent never sees) - and only
// the opaque WrappedKeyBase64/KeyId this returns is kept from then on. Restoring later means calling
// UnwrapFileKeyAsync (which only the backend, holding the master key, can actually do), decrypting
// the local blob with the returned plaintext DEK, then discarding the plaintext DEK again. A local
// admin who reads this store's files directly gets nothing usable without that round trip - the
// wrapped key is meaningless without the backend's master key, exactly like a stolen .dlpenc file's
// header is meaningless on its own.
//
// While a record's DEK has been generated but not yet confirmed wrapped by the backend (offline, or
// the upload hasn't happened yet), the plaintext DEK is held nowhere in the clear on disk: it is
// DPAPI-protected (MachineDataProtector, same posture as AuditOutbox's queued events) until the wrap
// round trip succeeds, at which point the protected copy is deleted for good - mirroring AuditOutbox's
// "retry until acknowledged, then delete" contract exactly.
public sealed record WatermarkEscrowRecord(
    Guid EscrowId,
    string ClassificationHash,
    string Extension,
    string LivePath,
    DateTimeOffset CreatedAtUtc,
    bool KeyWrapped,
    string? KeyId,
    string? WrappedKeyBase64,
    bool TileHidden,
    bool RestoreRequested);

public sealed class WatermarkEscrowStore(PolicyStore policyStore, MachineDataProtector protector, ILogger<WatermarkEscrowStore> logger)
{
    private const string PendingKeyPurpose = "CompanyDlp.WatermarkEscrow.PendingDek.v1";

    private readonly object _sync = new();
    private Dictionary<Guid, WatermarkEscrowRecord>? _records;

    private string RootDirectory => Path.Combine(GetRoot(), "WatermarkEscrow");
    private string BlobDirectory => Path.Combine(RootDirectory, "blobs");
    private string PendingKeyDirectory => Path.Combine(RootDirectory, "pending-keys");
    private string MetadataPath => Path.Combine(RootDirectory, "escrow.json");

    public WatermarkEscrowRecord? TryGetByClassificationHash(string classificationHash)
    {
        lock (_sync)
        {
            EnsureLoaded();
            return _records!.Values.FirstOrDefault(record =>
                record.ClassificationHash.Equals(classificationHash, StringComparison.OrdinalIgnoreCase));
        }
    }

    public IReadOnlyList<WatermarkEscrowRecord> GetAll()
    {
        lock (_sync)
        {
            EnsureLoaded();
            return _records!.Values.ToList();
        }
    }

    // Called once, the first time a PDF/image is watermarked, BEFORE the tile layer is drawn onto
    // the live file - cornerOnlyBytes must already be the tile-free render (see ContentWatermarker's
    // includeTileLayer:false path). Generates a fresh AES-256 DEK, encrypts cornerOnlyBytes with it,
    // writes the ciphertext to a local blob file, and DPAPI-protects the plaintext DEK into a
    // separate pending-key file for WatermarkEscrowSyncWorker to pick up and wrap server-side. The
    // plaintext DEK is zeroed from memory before this method returns.
    public WatermarkEscrowRecord CreateFromCornerOnlyBytes(
        string classificationHash, string extension, string livePath, byte[] cornerOnlyBytes)
    {
        var escrowId = Guid.NewGuid();
        var dek = RandomNumberGenerator.GetBytes(32);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[cornerOnlyBytes.Length];
        var tag = new byte[16];
        try
        {
            using var aesGcm = new AesGcm(dek, tag.Length);
            aesGcm.Encrypt(nonce, cornerOnlyBytes, ciphertext, tag);

            Directory.CreateDirectory(BlobDirectory);
            var blobPath = Path.Combine(BlobDirectory, $"{escrowId:N}.bin");
            using (var stream = File.Create(blobPath))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(nonce.Length);
                writer.Write(nonce);
                writer.Write(tag.Length);
                writer.Write(tag);
                writer.Write(ciphertext.Length);
                writer.Write(ciphertext);
            }

            Directory.CreateDirectory(PendingKeyDirectory);
            var protectedDek = protector.Protect(dek, PendingKeyPurpose);
            File.WriteAllBytes(Path.Combine(PendingKeyDirectory, $"{escrowId:N}.key"), protectedDek);
            CryptographicOperations.ZeroMemory(protectedDek);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }

        var record = new WatermarkEscrowRecord(
            escrowId, classificationHash, extension, livePath, DateTimeOffset.UtcNow,
            KeyWrapped: false, KeyId: null, WrappedKeyBase64: null, TileHidden: false, RestoreRequested: false);

        lock (_sync)
        {
            EnsureLoaded();
            _records![escrowId] = record;
            Save();
        }
        return record;
    }

    // Reads back the plaintext DEK for a record whose wrap upload hasn't completed yet - the pending
    // key file survives a service restart, so a queued-but-not-yet-uploaded escrow record is never
    // silently lost the way an in-memory-only queue would be.
    public byte[]? TryReadPendingPlainKey(Guid escrowId)
    {
        var path = Path.Combine(PendingKeyDirectory, $"{escrowId:N}.key");
        if (!File.Exists(path)) return null;
        try
        {
            return protector.Unprotect(File.ReadAllBytes(path), PendingKeyPurpose);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not read the pending DEK for watermark escrow record {EscrowId}.", escrowId);
            return null;
        }
    }

    // Called once WrapFileKeyAsync confirms the backend has taken custody of the DEK - deletes the
    // local pending-key file for good (nothing plaintext is left on disk from this point on) and
    // records the opaque wrapped form, which is meaningless without the backend's master key.
    public void MarkKeyWrapped(Guid escrowId, string keyId, string wrappedKeyBase64)
    {
        lock (_sync)
        {
            EnsureLoaded();
            if (!_records!.TryGetValue(escrowId, out var record)) return;
            _records[escrowId] = record with { KeyWrapped = true, KeyId = keyId, WrappedKeyBase64 = wrappedKeyBase64 };
            Save();
        }

        try
        {
            var path = Path.Combine(PendingKeyDirectory, $"{escrowId:N}.key");
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Watermark escrow record {EscrowId}'s DEK was wrapped, but its local pending-key file could not be deleted.", escrowId);
        }
    }

    // Set by FileInventoryScanner the moment it observes an active FileWatermarkDisable grant for a
    // PDF/image file that isn't hidden yet - WatermarkEscrowSyncWorker is the only thing that ever
    // actually performs the restore (it needs a network round trip to unwrap the DEK), so this just
    // marks intent for that worker to pick up on its own cadence, exactly like AuditOutbox's
    // EnqueueAsync marks intent for AuditSyncWorker to actually deliver.
    public void RequestRestore(Guid escrowId)
    {
        lock (_sync)
        {
            EnsureLoaded();
            if (!_records!.TryGetValue(escrowId, out var record) || record.RestoreRequested || record.TileHidden) return;
            _records[escrowId] = record with { RestoreRequested = true };
            Save();
        }
    }

    public void MarkTileHidden(Guid escrowId, bool hidden)
    {
        lock (_sync)
        {
            EnsureLoaded();
            if (!_records!.TryGetValue(escrowId, out var record)) return;
            _records[escrowId] = record with { TileHidden = hidden, RestoreRequested = false };
            Save();
        }
    }

    // Decrypts this record's stored corner-only blob using the plaintext DEK the caller already
    // unwrapped via the backend - never called with a DEK this store generated itself and still had
    // lying around (see the class comment: the whole point is that this store never holds a usable
    // plaintext key once wrapping succeeds).
    public byte[] DecryptBlob(Guid escrowId, byte[] plainDek)
    {
        var blobPath = Path.Combine(BlobDirectory, $"{escrowId:N}.bin");
        using var stream = File.OpenRead(blobPath);
        using var reader = new BinaryReader(stream);
        var nonce = reader.ReadBytes(reader.ReadInt32());
        var tag = reader.ReadBytes(reader.ReadInt32());
        var ciphertext = reader.ReadBytes(reader.ReadInt32());

        var plaintext = new byte[ciphertext.Length];
        using var aesGcm = new AesGcm(plainDek, tag.Length);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    public void UpdateLivePath(Guid escrowId, string livePath)
    {
        lock (_sync)
        {
            EnsureLoaded();
            if (!_records!.TryGetValue(escrowId, out var record) || record.LivePath == livePath) return;
            _records[escrowId] = record with { LivePath = livePath };
            Save();
        }
    }

    private void EnsureLoaded()
    {
        if (_records is not null) return;

        try
        {
            if (File.Exists(MetadataPath))
            {
                var values = JsonSerializer.Deserialize<List<WatermarkEscrowRecord>>(File.ReadAllText(MetadataPath), JsonDefaults.Options) ?? [];
                _records = values.ToDictionary(item => item.EscrowId);
                return;
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to load the watermark escrow store; starting empty.");
        }

        _records = new Dictionary<Guid, WatermarkEscrowRecord>();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(RootDirectory);
            var temporary = MetadataPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_records!.Values, JsonDefaults.Options));
            File.Move(temporary, MetadataPath, true);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to persist the watermark escrow store.");
        }
    }

    private string GetRoot()
    {
        var mode = policyStore.Get().Runtime.Mode;
        var root = mode.Equals("Production", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "CompanyDlp");
    }
}
