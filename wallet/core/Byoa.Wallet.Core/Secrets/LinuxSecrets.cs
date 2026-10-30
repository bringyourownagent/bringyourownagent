using System.Security.Cryptography;
using Byoa.Wallet.Protocol;
using Tmds.DBus.Protocol;

namespace Byoa.Wallet.Secrets;

/// <summary>
/// How the Linux build keeps the random key of the sealed files: in the Secret Service of the desktop session (D-Bus
/// <c>org.freedesktop.secrets</c>), or, when the session has none, in <c>secrets.key</c> in the data folder with mode 0600. A key file from
/// before a Secret Service appeared is imported into it; a key file is never deleted or rewritten. The choice is worked out once and is the
/// same for the rest of the run; <see cref="SecretProtectors.StorageSentence"/> tells the person which one it is.
/// </summary>
public static class LinuxSecrets
{
    /// <summary>The name the Secret Service item carries, so that the wallet finds it again and nothing else does.</summary>
    public const string ApplicationAttribute = "org.bringyourownagent.wallet";

    /// <summary>The kind of item: this key, not some other secret of the wallet's.</summary>
    public const string KindAttribute = "secrets-key";

    static readonly Lazy<SecretServiceKeyStore?> Service = new(ConnectService, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Where the key is kept now.</summary>
    public static SecretStorage Storage => Service.Value is null ? SecretStorage.KeyFile : SecretStorage.SecretService;

    /// <summary>
/// The protector for this system: both stores when there is a Secret Service, so that a key file from an earlier run is imported and can
/// open the files when the service's key cannot; the key file alone when there is no Secret Service.
/// </summary>
public static ISecretProtector CreateProtector(string purpose) =>
        new LinuxSecretProtector(Service.Value, new FileSecretKeyStore(Path.Combine(WalletPaths.Home, "secrets.key")), purpose);

    static SecretServiceKeyStore? ConnectService()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            var store = new SecretServiceKeyStore();
            store.CheckAvailable();
            return store;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // No session bus, no Secret Service, or a broken one: the wallet still works, under a key file it can tell the person about.
            return null;
        }
    }
}

/// <summary>
/// The Linux protector: the same AES-256-GCM file as the macOS one, under a key kept in the Secret Service or in <c>secrets.key</c>. When both
/// stores are there, the Secret Service is the primary one:
/// <list type="bullet">
/// <item>only one of them has a key: that key is used;</item>
/// <item>the key file has one and the Secret Service has none: the file's key is imported into the Secret Service once, and the file is left where it is;</item>
/// <item>both have a key and they differ: the Secret Service is tried first; when it cannot open the file, the key file's key is tried and, when
/// it works, stored in the Secret Service instead, so that no sealed file becomes unreadable while one of the two stores has the right key.</item>
/// </list>
/// The key file is never deleted or rewritten by this class.
/// </summary>
public sealed class LinuxSecretProtector : KeychainSecretProtector
{
    enum PrimaryStore
    {
        Service,
        File,
    }

    readonly ISecretKeyItem? _service;
    readonly ISecretKeyItem _file;
    readonly object _gate = new();
    bool _resolved;
    byte[]? _key;
    byte[]? _fileKey;
    PrimaryStore _primary;

    public LinuxSecretProtector(ISecretKeyItem? service, ISecretKeyItem file, string purpose = "secrets") : base(file, purpose)
    {
        _service = service;
        _file = file;
    }

    protected override string KeyStoreName => _service is null ? "key file" : "Secret Service";

    protected override byte[]? ReadKey()
    {
        Resolve();
        lock (_gate) return _key?.ToArray();
    }

    protected override void WriteKey(byte[] key)
    {
        lock (_gate)
        {
            if (_service is not null && _primary == PrimaryStore.Service)
            {
                try
                {
                    _service.Set(key);
                    _key = key.ToArray();
                    return;
                }
                catch (IOException e)
                {
                    throw new CryptographicException("the Secret Service would not keep the key of the secrets file: " + e.Message, e);
                }
            }

            try
            {
                _file.Set(key);
                _primary = PrimaryStore.File;
                _key = key.ToArray();
            }
            catch (IOException e)
            {
                throw new CryptographicException("the key file would not keep the key of the secrets file: " + e.Message, e);
            }
        }
    }

    protected override byte[]? FallbackKey()
    {
        lock (_gate) return _primary == PrimaryStore.Service ? _fileKey?.ToArray() : null;
    }

    protected override void FallbackKeyOpened(byte[] key)
    {
        lock (_gate)
        {
            _key = key.ToArray();
            if (_service is not null)
            {
                try
                {
                    _service.Set(key);
                    _primary = PrimaryStore.Service;
                    _fileKey = key.ToArray();
                    return;
                }
                catch (IOException)
                {
                    // The service cannot take it now (locked, for example): this run keeps using the key file.
                }
            }

            _primary = PrimaryStore.File;
        }
    }

    /// <summary>
    /// Looks at both stores once. The Secret Service wins when it has a key; without one it takes the key file's key; without either it waits
    /// for the first new key, which goes to the Secret Service when there is one.
    /// </summary>
    void Resolve()
    {
        lock (_gate)
        {
            if (_resolved) return;
            _resolved = true;

            byte[]? serviceKey = null;
            IOException? serviceError = null;
            if (_service is not null)
            {
                try { serviceKey = _service.Get(); }
                catch (IOException e) { serviceError = e; }
            }

            byte[]? fileKey = null;
            IOException? fileError = null;
            try { fileKey = _file.Get(); }
            catch (IOException e) { fileError = e; }
            _fileKey = fileKey;

            if (serviceKey is not null)
            {
                _key = serviceKey;
                _primary = PrimaryStore.Service;
                return;
            }

            if (fileKey is not null)
            {
                if (_service is not null)
                {
                    try
                    {
                        _service.Set(fileKey);              // import: the file keeps its copy
                        _key = fileKey;
                        _primary = PrimaryStore.Service;
                        return;
                    }
                    catch (IOException)
                    {
                        // The service cannot take the key now: the file's key is used for this run.
                    }
                }

                _key = fileKey;
                _primary = PrimaryStore.File;
                return;
            }

            if (serviceError is not null)
                throw new CryptographicException("the Secret Service did not give the key of the secrets file: " + serviceError.Message, serviceError);
            if (fileError is not null && _service is null)
                throw new CryptographicException("the key file did not give the key of the secrets file: " + fileError.Message, fileError);
            // A damaged key file next to a working Secret Service is left alone; a new key goes to the service.

            _key = null;
            _primary = _service is not null ? PrimaryStore.Service : PrimaryStore.File;
        }
    }
}

/// <summary>
/// The key file of a Linux desktop without a Secret Service: thirty-two random bytes, mode 0600, replaced as a whole so that a crash
/// never leaves half a key. A file of another length is an error, never silently replaced.
/// </summary>
sealed class FileSecretKeyStore(string path) : ISecretKeyItem
{
    const int KeySize = 32;

    public byte[]? Get()
    {
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != KeySize) throw new IOException("the key file is damaged (" + bytes.Length + " bytes instead of " + KeySize + "): " + path);
        return bytes;
    }

    public void Set(byte[] value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temp, options)) stream.Write(value);
        File.Move(temp, path, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the folder is the person's own; the key is not readable by others through it */ }
        }
    }
}

/// <summary>
/// The wallet's one item in the Secret Service of the session (D-Bus <c>org.freedesktop.secrets</c>): a plain session, the default
/// collection, attributes <c>application=org.bringyourownagent.wallet</c> and <c>kind=secrets-key</c>. It connects for each operation, so a
/// keyring that was restarted is found again, and it keeps no connection of its own.
/// </summary>
sealed class SecretServiceKeyStore : ISecretKeyItem
{
    const string BusName = "org.freedesktop.DBus";
    const string BusPath = "/org/freedesktop/DBus";
    const string BusInterface = "org.freedesktop.DBus";
    const string ServiceName = "org.freedesktop.secrets";
    const string ServicePath = "/org/freedesktop/secrets";
    const string ServiceInterface = "org.freedesktop.Secret.Service";
    const string CollectionInterface = "org.freedesktop.Secret.Collection";
    const string ItemInterface = "org.freedesktop.Secret.Item";
    const string Label = "BYOA agent wallet: key of the secrets file";
    const string ContentType = "application/octet-stream";

    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>True when the session bus has the Secret Service and a plain session opens.</summary>
    public void CheckAvailable()
    {
        WithSession((connection, session) => 0);
    }

    public byte[]? Get() => WithSession((connection, session) =>
    {
        var (unlocked, locked) = Search(connection);
        if (unlocked.Length == 0)
        {
            // A locked item would need a prompt the wallet does not show on its own; the file is kept for such a session.
            if (locked.Length > 0) throw new IOException("the key in the Secret Service is locked");
            return null;
        }

        return ReadSecret(connection, session, unlocked[0]);
    });

    public void Set(byte[] value) => WithSession((connection, session) =>
    {
        // CreateItem is a method of the collection, not of the service: the default collection comes from ReadAlias("default"), and the
        // call is Collection.CreateItem(properties a{sv}, secret (oayays), replace b). With replace=true an item with the same attributes
        // gets the new secret; there is never a second copy.
        var collection = DefaultCollection(connection);
        if (collection == "/") throw new IOException("the Secret Service has no default collection to keep the key in");
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(ServiceName, collection, CollectionInterface, "CreateItem", "a{sv}(oayays)b");
        var properties = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("org.freedesktop.Secret.Item.Label");
        writer.WriteVariantString(Label);
        writer.WriteDictionaryEntryStart();
        writer.WriteString("org.freedesktop.Secret.Item.Attributes");
        writer.WriteSignature("a{ss}");
        WriteAttributes(ref writer);
        writer.WriteDictionaryEnd(properties);
        writer.WriteStructureStart();
        writer.WriteObjectPath(session);
        writer.WriteArray(Array.Empty<byte>());
        writer.WriteArray(value);
        writer.WriteString(ContentType);
        writer.WriteBool(true);
        var reply = Call(connection, writer.CreateMessage(), static (message, _) =>
        {
            var reader = message.GetBodyReader();
            return (reader.ReadObjectPathAsString(), reader.ReadObjectPathAsString());
        });
        if (reply.Item2 != "/")
            throw new IOException("the Secret Service needs the collection unlocked before the wallet can keep its key");
        return 0;
    });

    /// <summary>The collection the service calls its default (usually the login keyring); "/" when it has none.</summary>
    static string DefaultCollection(Connection connection)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(ServiceName, ServicePath, ServiceInterface, "ReadAlias", "s");
        writer.WriteString("default");
        return Call(connection, writer.CreateMessage(), static (message, _) => message.GetBodyReader().ReadObjectPathAsString());
    }

    static (string[] Unlocked, string[] Locked) Search(Connection connection)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(ServiceName, ServicePath, ServiceInterface, "SearchItems", "a{ss}");
        WriteAttributes(ref writer);
        return Call(connection, writer.CreateMessage(), static (message, _) =>
        {
            var reader = message.GetBodyReader();
            var unlocked = reader.ReadArrayOfObjectPath().Select(path => path.ToString()).ToArray();
            var locked = reader.ReadArrayOfObjectPath().Select(path => path.ToString()).ToArray();
            return (unlocked, locked);
        });
    }

    static byte[] ReadSecret(Connection connection, string session, string item)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(ServiceName, item, ItemInterface, "GetSecret", "o");
        writer.WriteObjectPath(session);
        return Call(connection, writer.CreateMessage(), static (message, _) =>
        {
            var reader = message.GetBodyReader();
            reader.AlignStruct();
            reader.ReadObjectPathAsString();
            reader.ReadArrayOfByte();
            var value = reader.ReadArrayOfByte();
            reader.ReadString();
            return value;
        });
    }

    static void WriteAttributes(ref MessageWriter writer)
    {
        var attributes = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("application");
        writer.WriteString(LinuxSecrets.ApplicationAttribute);
        writer.WriteDictionaryEntryStart();
        writer.WriteString("kind");
        writer.WriteString(LinuxSecrets.KindAttribute);
        writer.WriteDictionaryEnd(attributes);
    }

    T WithSession<T>(Func<Connection, string, T> work)
    {
        var address = Address.Session;
        if (string.IsNullOrWhiteSpace(address)) throw new IOException("there is no D-Bus session bus");
        using var connection = new Connection(address);
        Run(connection.ConnectAsync());
        if (!Call(connection, NameHasOwnerCall(connection), static (message, _) => message.GetBodyReader().ReadBool()))
            throw new IOException("the session bus has no Secret Service");
        return work(connection, OpenPlainSession(connection));
    }

    static MessageBuffer NameHasOwnerCall(Connection connection)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(BusName, BusPath, BusInterface, "NameHasOwner", "s");
        writer.WriteString(ServiceName);
        return writer.CreateMessage();
    }

    static string OpenPlainSession(Connection connection)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(ServiceName, ServicePath, ServiceInterface, "OpenSession", "sv");
        writer.WriteString("plain");
        writer.WriteVariantString("");
        return Call(connection, writer.CreateMessage(), static (message, _) =>
        {
            var reader = message.GetBodyReader();
            reader.ReadVariantValue();                       // the session's output (a variant); plain gives nothing useful
            return reader.ReadObjectPathAsString();
        });
    }

    static T Call<T>(Connection connection, MessageBuffer message, MessageValueReader<T> reader)
    {
        try
        {
            return connection.CallMethodAsync(message, reader, null).WaitAsync(Timeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException e)
        {
            throw new IOException("the Secret Service did not answer in time", e);
        }
        catch (Exception e) when (e is not OutOfMemoryException and not IOException)
        {
            throw new IOException("the Secret Service did not answer: " + e.Message, e);
        }
    }

    static void Run(ValueTask task)
    {
        try
        {
            task.AsTask().WaitAsync(Timeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException e)
        {
            throw new IOException("the session bus did not answer in time", e);
        }
        catch (Exception e) when (e is not OutOfMemoryException and not IOException)
        {
            throw new IOException("the session bus could not be reached: " + e.Message, e);
        }
    }
}