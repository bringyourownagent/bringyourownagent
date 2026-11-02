using System.Security.Cryptography;
using System.Text;
using Byoa.Wallet.Direct;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Providers;
using Byoa.Wallet.Registry;
using Byoa.Wallet.Secrets;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>
/// The hook the Android app uses to give the core its Keystore protector (<see cref="SecretProtectors.UsePlatform"/>). The Keystore itself
/// is Android's and is tried on the phone; here a protector over a key in memory (the same AES-256-GCM code the Keychain protector uses, with
/// the purpose in what the seal covers) stands in for it, so that what the core does with the hook runs for real.
/// </summary>
public class AndroidPlatformHookTests
{
    static readonly byte[] Plain = Encoding.UTF8.GetBytes("{\"openrouter\":\"test-secret-0123456789abcdef\"}");

    /// <summary>One key for the whole phone, like the Keystore's: every purpose seals under it, and the purpose keeps the files apart.</summary>
    sealed class PhoneProtectors
    {
        readonly MemoryKeychainItem _key = new();
        public List<string> Asked { get; } = [];

        public ISecretProtector For(string purpose)
        {
            Asked.Add(purpose);
            return new KeychainSecretProtector(_key, purpose);
        }
    }

    /// <summary>
    /// Runs a body with the hook set, and leaves things as they were: no hook, and the test-only factory (which a run away from Windows has set
    /// for every test) back in its place.
    /// </summary>
    static void WithPhone(PhoneProtectors phone, Action body)
    {
        var seam = SecretProtectors.TestFactory;
        SecretProtectors.TestFactory = null;
        SecretProtectors.UsePlatform(SecretStorage.AndroidKeystore, phone.For);
        try
        {
            body();
        }
        finally
        {
            SecretProtectors.ForgetPlatform();
            SecretProtectors.TestFactory = seam;
        }
    }

    [Fact]
    public void WithNothingGivenWindowsKeepsItsOwnStoreAndItsWords()
    {
        SecretProtectors.ForgetPlatform();
        if (!OperatingSystem.IsWindows()) return;               // the other systems have theirs, and tests of their own
        Assert.Equal(SecretStorage.WindowsDpapi, SecretProtectors.Storage);
        Assert.Equal("Keys are stored with Windows DPAPI for your account only.", SecretProtectors.StorageSentence);
        Assert.Equal("Windows could not protect the wallet data for this user profile. Try again in the wallet.", SecretProtectors.FailureSentence);
    }

    [Fact]
    public void TheProtectorTheAppGaveIsTheOneEveryStoreGetsAndTheSystemSaysAndroid()
    {
        var phone = new PhoneProtectors();
        WithPhone(phone, () =>
        {
            Assert.Equal(SecretStorage.AndroidKeystore, SecretProtectors.Storage);
            Assert.Contains("Android Keystore", SecretProtectors.StorageSentence, StringComparison.Ordinal);
            Assert.Contains("cannot be taken out", SecretProtectors.StorageSentence, StringComparison.Ordinal);
            Assert.Contains("Android Keystore", SecretProtectors.FailureSentence, StringComparison.Ordinal);
            Assert.DoesNotContain("Windows", SecretProtectors.StorageSentence, StringComparison.Ordinal);
            Assert.DoesNotContain("Windows", SecretProtectors.FailureSentence, StringComparison.Ordinal);

            var dir = TestFolder.New();
            var file = Path.Combine(dir, "secrets.dat");
            var store = new SecretStore(file);                  // no protector given: the platform's
            store.Set("openrouter", "test-secret-0123456789abcdef");
            Assert.Equal("test-secret-0123456789abcdef", new SecretStore(file).Get("openrouter"));
            Assert.DoesNotContain("test-secret", File.ReadAllText(file, Encoding.Latin1));

            var pairings = new PairingStore(Path.Combine(dir, "pairings.dat"));
            pairings.Set(new Pairing("https://site.example", "https://site.example/byoa", "{}", "tok-1", DateTimeOffset.UnixEpoch));
            Assert.Equal("tok-1", new PairingStore(Path.Combine(dir, "pairings.dat")).Get("https://site.example")?.Token);

            // each store asked for the protector of its own purpose
            Assert.Contains("secrets", phone.Asked);
            Assert.Contains("pairings", phone.Asked);
        });
    }

    [Fact]
    public void AFileOfOnePurposeDoesNotOpenAsTheOtherEvenUnderTheOneKey()
    {
        WithPhone(new PhoneProtectors(), () =>
        {
            var secrets = SecretProtectors.CreateDefault("secrets");
            var pairings = SecretProtectors.CreateDefault("pairings");
            var sealedSecrets = secrets.Protect(Plain);
            Assert.Equal(Plain, secrets.Unprotect(sealedSecrets));
            Assert.ThrowsAny<CryptographicException>(() => pairings.Unprotect(sealedSecrets));

            // the pairings file read as the secrets file is no keys at all, never a crash and never the pairings' content
            var dir = TestFolder.New();
            new PairingStore(Path.Combine(dir, "x.dat")).Set(new Pairing("https://site.example", "https://site.example/byoa", "{}", "tok-1", DateTimeOffset.UnixEpoch));
            var asKeys = new SecretStore(Path.Combine(dir, "x.dat"));
            Assert.False(asKeys.Has("tok-1"));
            Assert.True(asKeys.Unreadable);
        });
    }

    [Fact]
    public void TheTestOnlyFactoryStillWinsAndForgettingTheHookGivesTheSystemBack()
    {
        var phone = new PhoneProtectors();
        WithPhone(phone, () =>
        {
            var mine = new PhoneProtectors();
            SecretProtectors.TestFactory = mine.For;
            try
            {
                SecretProtectors.CreateDefault("secrets");
                Assert.Single(mine.Asked);
                Assert.Empty(phone.Asked);
            }
            finally
            {
                SecretProtectors.TestFactory = null;
            }
        });

        if (OperatingSystem.IsWindows()) Assert.Equal(SecretStorage.WindowsDpapi, SecretProtectors.Storage);
    }

    [Fact]
    public void ASystemTheWalletDoesNotKnowSaysWhatIsMissingInsteadOfFailingInTheDark()
    {
        SecretProtectors.ForgetPlatform();
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) return;
        var failure = Assert.Throws<PlatformNotSupportedException>(() => SecretProtectors.CreateDefault());
        Assert.Contains("UsePlatform", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AKeystoreFailureIsNotBlamedOnWindowsOnAnAndroidPhone()
    {
        // the registry's words about the key store were written for Windows; on Android they name the wallet (see WalletRegistry.KeyStoreSays)
        var says = typeof(WalletRegistry).GetMethod("KeyStoreSays", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(says);
        const string windows = "Windows could not protect the API key for this user profile. Try again in the wallet.";
        var text = (string)says!.Invoke(null, [windows])!;
        // away from Android the text is exactly as it always was
        if (!OperatingSystem.IsAndroid()) Assert.Equal(windows, text);
        else Assert.StartsWith("The wallet could not protect", text, StringComparison.Ordinal);
    }
}

/// <summary>
/// The words for the device the wallet runs on (<see cref="ThisDevice"/>): a computer says "this PC" and "this computer" as it always has, and a
/// phone, once its platform has said so, says "this phone" in every text that names where a model runs.
/// </summary>
public class ThisDeviceTests
{
    /// <summary>Runs a body as a phone: the word is scoped to this test (tests run in parallel; a process-wide word would show in the texts of the others).</summary>
    static void AsPhone(Action body)
    {
        using (ThisDevice.Scoped("this phone")) body();
    }

    [Fact]
    public void ADesktopWalletSaysThisPcAndThisComputerAsItAlwaysDid()
    {
        Assert.Equal("this PC", ThisDevice.Pc);
        Assert.Equal("this computer", ThisDevice.Computer);
        Assert.Equal("this PC", ModelFit.Place("local"));
        var source = new OpenAiCompatibleSource(SourceKinds.LmStudio, "qwen/qwen3.5-4b", null, "http://127.0.0.1:5321/v1", new HttpClient());
        Assert.Equal("qwen/qwen3.5-4b · this computer", source.Name);
        Assert.Equal("this PC", WalletEngine.Where(new FakeTextSource("lmstudio:qwen/qwen3.5-4b")));
    }

    [Fact]
    public void APhoneSaysThisPhoneWhereAModelRuns()
    {
        AsPhone(() =>
        {
            Assert.Equal("this phone", ThisDevice.Pc);
            Assert.Equal("this phone", ThisDevice.Computer);
            Assert.Equal("this phone", ModelFit.Place("local"));
            Assert.Equal("your home server", ModelFit.Place("home-server"));        // the other places keep their words
            var source = new OpenAiCompatibleSource(SourceKinds.LmStudio, "qwen/qwen3.5-4b", null, "http://127.0.0.1:5321/v1", new HttpClient());
            Assert.Equal("qwen/qwen3.5-4b · this phone", source.Name);
            Assert.Equal("this phone", WalletEngine.Where(new FakeTextSource("lmstudio:qwen/qwen3.5-4b")));
        });
    }

    [Fact]
    public void TheManagersReasonForALocalModelNamesThePhoneToo()
    {
        var local = new FakeTextSource("lmstudio:qwen/qwen3.5-4b");
        var settings = new WalletSettings();
        Router.EnsureTextChain(settings, [local]);
        var plan = Router.PlanText(settings.Chain(Vocabulary.Generate), [local], id => settings.Source(id).Enabled, null);
        Assert.Contains("because it runs on this PC with no cloud bill.", Router.ExplainText(plan), StringComparison.Ordinal);
        AsPhone(() => Assert.Contains("because it runs on this phone with no cloud bill.", Router.ExplainText(plan), StringComparison.Ordinal));
    }

    [Fact]
    public void ThePlatformMustNameTheDevice()
    {
        Assert.Throws<ArgumentException>(() => ThisDevice.Use(" "));
    }
}
