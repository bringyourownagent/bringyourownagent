using System.Runtime.InteropServices;
using System.Text;

namespace Byoa.Wallet.Providers;

/// <summary>The result of the one startup Vulkan check. A name is diagnostic only and is never sent to a site.</summary>
public sealed record VulkanProbeResult(bool Usable, string? DeviceName = null);

/// <summary>
/// Looks for a real Vulkan physical device without opening a window or touching the Whisper loader. The check is
/// deliberately conservative: a discrete or integrated device is accepted, while CPU and software renderers are not.
/// </summary>
public sealed class VulkanProbe : IDisposable
{
    const int Success = 0;
    const int DeviceTypeIntegrated = 1;
    const int DeviceTypeDiscrete = 2;
    const int DeviceTypeVirtual = 3;
    const int PropertiesBytes = 4096;
    const int TimeoutMilliseconds = 500;

    static readonly string[] SoftwareNames = ["llvmpipe", "lavapipe", "softpipe", "swrast", "swiftshader", "microsoft basic render", "software"];

    /// <summary>Runs the native probe with a short wall-clock bound. Any failure means "no usable GPU".</summary>
    public static VulkanProbeResult Probe()
    {
        try
        {
            var work = Task.Run(ProbeCore);
            return work.Wait(TimeSpan.FromMilliseconds(TimeoutMilliseconds)) && work.IsCompletedSuccessfully
                ? work.Result
                : new VulkanProbeResult(false);
        }
        catch (Exception)
        {
            return new VulkanProbeResult(false);
        }
    }

    /// <summary>Instance form for callers that want an explicitly disposable probe object.</summary>
    public VulkanProbeResult Check() => Probe();

    public void Dispose()
    {
        // The native instance is created and destroyed inside each bounded check; there is no process handle to keep.
    }

    static VulkanProbeResult ProbeCore()
    {
        IntPtr appName = IntPtr.Zero;
        IntPtr engineName = IntPtr.Zero;
        IntPtr appInfo = IntPtr.Zero;
        IntPtr instanceInfo = IntPtr.Zero;
        IntPtr instance = IntPtr.Zero;
        try
        {
            appName = Marshal.StringToCoTaskMemUTF8("BYOA wallet");
            engineName = Marshal.StringToCoTaskMemUTF8("BYOA");
            var app = new VkApplicationInfo
            {
                SType = 0, // VK_STRUCTURE_TYPE_APPLICATION_INFO
                PApplicationName = appName,
                ApplicationVersion = 1,
                PEngineName = engineName,
                EngineVersion = 1,
                ApiVersion = 0x00400000, // Vulkan 1.0; loaders may expose a newer version.
            };
            appInfo = Marshal.AllocHGlobal(Marshal.SizeOf<VkApplicationInfo>());
            Marshal.StructureToPtr(app, appInfo, fDeleteOld: false);

            var create = new VkInstanceCreateInfo
            {
                SType = 1, // VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO
                PApplicationInfo = appInfo,
            };
            instanceInfo = Marshal.AllocHGlobal(Marshal.SizeOf<VkInstanceCreateInfo>());
            Marshal.StructureToPtr(create, instanceInfo, fDeleteOld: false);
            if (vkCreateInstance(instanceInfo, IntPtr.Zero, out instance) != Success || instance == IntPtr.Zero)
                return new VulkanProbeResult(false);

            uint count = 0;
            if (vkEnumeratePhysicalDevices(instance, ref count, null) != Success || count == 0) return new VulkanProbeResult(false);
            var devices = new IntPtr[checked((int)count)];
            if (vkEnumeratePhysicalDevices(instance, ref count, devices) != Success) return new VulkanProbeResult(false);
            foreach (var device in devices)
            {
                if (device == IntPtr.Zero) continue;
                var properties = Marshal.AllocHGlobal(PropertiesBytes);
                try
                {
                    var zeros = new byte[PropertiesBytes];
                    Marshal.Copy(zeros, 0, properties, PropertiesBytes);
                    vkGetPhysicalDeviceProperties(device, properties);
                    var type = Marshal.ReadInt32(properties, 16); // the fifth field is VkPhysicalDeviceType
                    var name = ReadName(properties + 20, 256);
                    if (IsUsablePhysicalDevice(type, name))
                        return new VulkanProbeResult(true, name);
                }
                finally
                {
                    Marshal.FreeHGlobal(properties);
                }
            }

            return new VulkanProbeResult(false);
        }
        catch (Exception)
        {
            return new VulkanProbeResult(false);
        }
        finally
        {
            if (instance != IntPtr.Zero)
            {
                try { vkDestroyInstance(instance, IntPtr.Zero); }
                catch { }
            }
            if (instanceInfo != IntPtr.Zero) Marshal.FreeHGlobal(instanceInfo);
            if (appInfo != IntPtr.Zero) Marshal.FreeHGlobal(appInfo);
            if (engineName != IntPtr.Zero) Marshal.FreeCoTaskMem(engineName);
            if (appName != IntPtr.Zero) Marshal.FreeCoTaskMem(appName);
        }
    }

    static string? ReadName(IntPtr pointer, int maxBytes)
    {
        var bytes = new byte[maxBytes];
        Marshal.Copy(pointer, bytes, 0, bytes.Length);
        var length = Array.IndexOf(bytes, (byte)0);
        if (length < 0) length = bytes.Length;
        return length == 0 ? null : Encoding.UTF8.GetString(bytes, 0, length);
    }

    internal static bool IsUsablePhysicalDevice(int type, string? name) =>
        type is DeviceTypeIntegrated or DeviceTypeDiscrete or DeviceTypeVirtual && !IsSoftware(name);

    static bool IsSoftware(string? name) => name is not null && SoftwareNames.Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase));

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi, EntryPoint = "vkCreateInstance")]
    static extern int vkCreateInstance(IntPtr createInfo, IntPtr allocator, out IntPtr instance);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi, EntryPoint = "vkEnumeratePhysicalDevices")]
    static extern int vkEnumeratePhysicalDevices(IntPtr instance, ref uint count, [Out] IntPtr[]? devices);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi, EntryPoint = "vkGetPhysicalDeviceProperties")]
    static extern void vkGetPhysicalDeviceProperties(IntPtr device, IntPtr properties);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi, EntryPoint = "vkDestroyInstance")]
    static extern void vkDestroyInstance(IntPtr instance, IntPtr allocator);

    [StructLayout(LayoutKind.Sequential)]
    struct VkApplicationInfo
    {
        public uint SType;
        public IntPtr PNext;
        public IntPtr PApplicationName;
        public uint ApplicationVersion;
        public IntPtr PEngineName;
        public uint EngineVersion;
        public uint ApiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct VkInstanceCreateInfo
    {
        public uint SType;
        public IntPtr PNext;
        public uint Flags;
        public IntPtr PApplicationInfo;
        public uint EnabledLayerCount;
        public IntPtr PpEnabledLayerNames;
        public uint EnabledExtensionCount;
        public IntPtr PpEnabledExtensionNames;
    }
}
