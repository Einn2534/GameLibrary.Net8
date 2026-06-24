using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GameLibrary.Net8;

public sealed class AudioSessionVolumeService
{
    private const int LaunchVolumeAttemptCount = 90;
    private const int LaunchVolumeDelayMilliseconds = 500;

    public async Task SetProcessVolumeAsync(int processId, int volumePercent, CancellationToken cancellationToken = default)
    {
        await SetLaunchVolumeAsync(processId, null, null, null, volumePercent, cancellationToken);
    }

    public async Task SetProcessTreeVolumeAsync(int rootProcessId, int volumePercent, CancellationToken cancellationToken = default)
    {
        await SetLaunchVolumeAsync(rootProcessId, null, null, null, volumePercent, cancellationToken);
    }

    public async Task SetLaunchVolumeAsync(
        int rootProcessId,
        string executablePath,
        string installDirectory,
        string displayName,
        int volumePercent,
        CancellationToken cancellationToken = default)
    {
        LaunchVolumeTarget target = LaunchVolumeTarget.Create(
            rootProcessId,
            executablePath,
            installDirectory,
            displayName);

        if (!target.HasAnyMatchValue)
        {
            return;
        }

        float volume = Math.Clamp(volumePercent, 0, 100) / 100f;

        for (int attempt = 0; attempt < LaunchVolumeAttemptCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlySet<int> processIds = GetCandidateProcessIds(target);
            if (TrySetMatchingSessionVolumes(target, processIds, volume))
            {
                return;
            }

            if (attempt + 1 < LaunchVolumeAttemptCount)
            {
                await Task.Delay(LaunchVolumeDelayMilliseconds, cancellationToken);
            }
        }
    }

    private static bool TrySetMatchingSessionVolumes(LaunchVolumeTarget target, IReadOnlySet<int> processIds, float volume)
    {
        IMMDeviceEnumerator deviceEnumerator = null;
        IMMDevice device = null;
        IAudioSessionManager2 sessionManager = null;
        IAudioSessionEnumerator sessionEnumerator = null;
        bool adjustedAnySession = false;

        try
        {
            deviceEnumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumerator();
            deviceEnumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device);
            device.Activate(typeof(IAudioSessionManager2).GUID, ClsCtx.CLSCTX_ALL, IntPtr.Zero, out object manager);
            sessionManager = (IAudioSessionManager2)manager;
            sessionManager.GetSessionEnumerator(out sessionEnumerator);
            sessionEnumerator.GetCount(out int sessionCount);

            for (int i = 0; i < sessionCount; i++)
            {
                sessionEnumerator.GetSession(i, out IAudioSessionControl sessionControl);
                try
                {
                    if (sessionControl is not IAudioSessionControl2 sessionControl2 ||
                        sessionControl is not ISimpleAudioVolume simpleAudioVolume)
                    {
                        continue;
                    }

                    sessionControl2.GetProcessId(out int sessionProcessId);
                    string sessionDisplayName = GetSessionDisplayName(sessionControl);

                    if (SessionMatchesTarget(target, processIds, sessionProcessId, sessionDisplayName))
                    {
                        Guid eventContext = Guid.Empty;
                        adjustedAnySession = simpleAudioVolume.SetMasterVolume(volume, ref eventContext) == 0 ||
                            adjustedAnySession;
                    }
                }
                finally
                {
                    ReleaseComObject(sessionControl);
                }
            }
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            ReleaseComObject(sessionEnumerator);
            ReleaseComObject(sessionManager);
            ReleaseComObject(device);
            ReleaseComObject(deviceEnumerator);
        }

        return adjustedAnySession;
    }

    private static IReadOnlySet<int> GetCandidateProcessIds(LaunchVolumeTarget target)
    {
        HashSet<int> processIds = target.RootProcessId > 0
            ? new HashSet<int>(GetProcessTreeIds(target.RootProcessId))
            : [];

        if (string.IsNullOrWhiteSpace(target.ExecutablePath) &&
            string.IsNullOrWhiteSpace(target.InstallDirectory))
        {
            return processIds;
        }

        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                string processPath = TryGetProcessPath(process);
                if (TargetMatchesProcessPath(target, processPath))
                {
                    processIds.Add(process.Id);
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return processIds;
    }

    private static bool SessionMatchesTarget(
        LaunchVolumeTarget target,
        IReadOnlySet<int> processIds,
        int sessionProcessId,
        string sessionDisplayName)
    {
        if (sessionProcessId > 0 && processIds.Contains(sessionProcessId))
        {
            return true;
        }

        string normalizedSessionName = NormalizeMatchName(sessionDisplayName);
        return ContainsUsefulMatch(normalizedSessionName, target.DisplayName) ||
            ContainsUsefulMatch(normalizedSessionName, target.ExecutableName);
    }

    private static bool ContainsUsefulMatch(string value, string candidate)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(candidate) || candidate.Length < 4)
        {
            return false;
        }

        return value.Contains(candidate, StringComparison.Ordinal) ||
            candidate.Contains(value, StringComparison.Ordinal);
    }

    private static bool TargetMatchesProcessPath(LaunchVolumeTarget target, string processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        string normalizedProcessPath = NormalizeFullPath(processPath);
        if (!string.IsNullOrWhiteSpace(target.ExecutablePath) &&
            string.Equals(normalizedProcessPath, target.ExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(target.InstallDirectory) &&
            normalizedProcessPath.StartsWith(target.InstallDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static string TryGetProcessPath(Process process)
    {
        try
        {
            string modulePath = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(modulePath))
            {
                return modulePath;
            }
        }
        catch
        {
        }

        IntPtr handle = OpenProcess(ProcessAccessFlags.PROCESS_QUERY_LIMITED_INFORMATION, false, process.Id);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            int capacity = 1024;
            StringBuilder pathBuilder = new(capacity);
            return QueryFullProcessImageName(handle, 0, pathBuilder, ref capacity)
                ? pathBuilder.ToString()
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string GetSessionDisplayName(IAudioSessionControl sessionControl)
    {
        IntPtr displayNamePointer = IntPtr.Zero;
        try
        {
            return sessionControl.GetDisplayName(out displayNamePointer) == 0 && displayNamePointer != IntPtr.Zero
                ? Marshal.PtrToStringUni(displayNamePointer)
                : null;
        }
        finally
        {
            if (displayNamePointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(displayNamePointer);
            }
        }
    }

    private static IReadOnlySet<int> GetProcessTreeIds(int rootProcessId)
    {
        HashSet<int> processIds = [rootProcessId];
        Dictionary<int, List<int>> childrenByParentId = BuildProcessTreeIndex();
        Queue<int> pendingProcessIds = new();
        pendingProcessIds.Enqueue(rootProcessId);

        while (pendingProcessIds.Count > 0)
        {
            int processId = pendingProcessIds.Dequeue();
            if (!childrenByParentId.TryGetValue(processId, out List<int> childProcessIds))
            {
                continue;
            }

            foreach (int childProcessId in childProcessIds)
            {
                if (processIds.Add(childProcessId))
                {
                    pendingProcessIds.Enqueue(childProcessId);
                }
            }
        }

        return processIds;
    }

    private static Dictionary<int, List<int>> BuildProcessTreeIndex()
    {
        var childrenByParentId = new Dictionary<int, List<int>>();
        IntPtr snapshot = CreateToolhelp32Snapshot(SnapshotFlags.TH32CS_SNAPPROCESS, 0);
        if (snapshot == InvalidHandleValue)
        {
            return childrenByParentId;
        }

        try
        {
            PROCESSENTRY32 entry = new()
            {
                dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>()
            };

            if (!Process32First(snapshot, ref entry))
            {
                return childrenByParentId;
            }

            do
            {
                int parentProcessId = unchecked((int)entry.th32ParentProcessID);
                int processId = unchecked((int)entry.th32ProcessID);

                if (!childrenByParentId.TryGetValue(parentProcessId, out List<int> childProcessIds))
                {
                    childProcessIds = [];
                    childrenByParentId[parentProcessId] = childProcessIds;
                }

                childProcessIds.Add(processId);
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return childrenByParentId;
    }

    private static string NormalizeFullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return path.Trim();
        }
    }

    private static string NormalizeDirectoryPath(string path)
    {
        string normalizedPath = NormalizeFullPath(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return null;
        }

        return normalizedPath.EndsWith(Path.DirectorySeparatorChar) ||
            normalizedPath.EndsWith(Path.AltDirectorySeparatorChar)
            ? normalizedPath
            : normalizedPath + Path.DirectorySeparatorChar;
    }

    private static string NormalizeMatchName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        StringBuilder builder = new(value.Length);
        foreach (char character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static void ReleaseComObject(object value)
    {
        if (value != null)
        {
            Marshal.ReleaseComObject(value);
        }
    }

    private sealed class LaunchVolumeTarget
    {
        private LaunchVolumeTarget(
            int rootProcessId,
            string executablePath,
            string installDirectory,
            string displayName,
            string executableName)
        {
            RootProcessId = rootProcessId;
            ExecutablePath = executablePath;
            InstallDirectory = installDirectory;
            DisplayName = displayName;
            ExecutableName = executableName;
        }

        public int RootProcessId { get; }

        public string ExecutablePath { get; }

        public string InstallDirectory { get; }

        public string DisplayName { get; }

        public string ExecutableName { get; }

        public bool HasAnyMatchValue =>
            RootProcessId > 0 ||
            !string.IsNullOrWhiteSpace(ExecutablePath) ||
            !string.IsNullOrWhiteSpace(InstallDirectory) ||
            !string.IsNullOrWhiteSpace(DisplayName) ||
            !string.IsNullOrWhiteSpace(ExecutableName);

        public static LaunchVolumeTarget Create(
            int rootProcessId,
            string executablePath,
            string installDirectory,
            string displayName)
        {
            string normalizedExecutablePath = NormalizeFullPath(executablePath);
            return new LaunchVolumeTarget(
                rootProcessId,
                normalizedExecutablePath,
                NormalizeDirectoryPath(installDirectory),
                NormalizeMatchName(displayName),
                NormalizeMatchName(Path.GetFileNameWithoutExtension(normalizedExecutablePath)));
        }
    }

    private enum EDataFlow
    {
        eRender,
        eCapture,
        eAll
    }

    private enum ERole
    {
        eConsole,
        eMultimedia,
        eCommunications
    }

    private enum AudioSessionState
    {
        AudioSessionStateInactive,
        AudioSessionStateActive,
        AudioSessionStateExpired
    }

    [Flags]
    private enum ClsCtx
    {
        CLSCTX_INPROC_SERVER = 0x1,
        CLSCTX_INPROC_HANDLER = 0x2,
        CLSCTX_LOCAL_SERVER = 0x4,
        CLSCTX_REMOTE_SERVER = 0x10,
        CLSCTX_ALL = CLSCTX_INPROC_SERVER | CLSCTX_INPROC_HANDLER | CLSCTX_LOCAL_SERVER | CLSCTX_REMOTE_SERVER
    }

    [Flags]
    private enum SnapshotFlags : uint
    {
        TH32CS_SNAPPROCESS = 0x00000002
    }

    [Flags]
    private enum ProcessAccessFlags : uint
    {
        PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
    }

    private static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(SnapshotFlags flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(ProcessAccessFlags desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumerator
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out object devices);

        void GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);

        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        void RegisterEndpointNotificationCallback(IntPtr client);

        void UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate([MarshalAs(UnmanagedType.LPStruct)] Guid iid, ClsCtx clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        void GetAudioSessionControl();

        void GetSimpleAudioVolume();

        void GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        void GetCount(out int sessionCount);

        void GetSession(int sessionIndex, out IAudioSessionControl session);
    }

    [ComImport]
    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig]
        int GetState(out AudioSessionState state);

        [PreserveSig]
        int GetDisplayName(out IntPtr displayName);

        [PreserveSig]
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

        [PreserveSig]
        int GetIconPath(out IntPtr iconPath);

        [PreserveSig]
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

        [PreserveSig]
        int GetGroupingParam(out Guid groupingId);

        [PreserveSig]
        int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);

        [PreserveSig]
        int RegisterAudioSessionNotification(IntPtr notifications);

        [PreserveSig]
        int UnregisterAudioSessionNotification(IntPtr notifications);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig]
        int GetState(out AudioSessionState state);

        [PreserveSig]
        int GetDisplayName(out IntPtr displayName);

        [PreserveSig]
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

        [PreserveSig]
        int GetIconPath(out IntPtr iconPath);

        [PreserveSig]
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

        [PreserveSig]
        int GetGroupingParam(out Guid groupingId);

        [PreserveSig]
        int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);

        [PreserveSig]
        int RegisterAudioSessionNotification(IntPtr notifications);

        [PreserveSig]
        int UnregisterAudioSessionNotification(IntPtr notifications);

        [PreserveSig]
        int GetSessionIdentifier(out IntPtr sessionId);

        [PreserveSig]
        int GetSessionInstanceIdentifier(out IntPtr sessionInstanceId);

        [PreserveSig]
        int GetProcessId(out int processId);

        [PreserveSig]
        int IsSystemSoundsSession();

        [PreserveSig]
        int SetDuckingPreference(bool optOut);
    }

    [ComImport]
    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig]
        int SetMasterVolume(float level, ref Guid eventContext);

        [PreserveSig]
        int GetMasterVolume(out float level);

        [PreserveSig]
        int SetMute(bool isMuted, ref Guid eventContext);

        [PreserveSig]
        int GetMute(out bool isMuted);
    }
}
