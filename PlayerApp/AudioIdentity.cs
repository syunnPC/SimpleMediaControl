using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PlayerApp;

internal sealed record AudioIdentity(string Executable, string ApplicationId, string PackageFamily)
{
    public bool MatchesAudioSource(string source)
    {
        // Packaged players can delegate playback to a different entry point in
        // their package (Apple Music: App -> LibraryServer). Use the process's
        // actual Windows package identity, never an executable/name heuristic.
        var separator = source.IndexOf('!');
        return Matches(source) || (separator > 0 && PackageFamily.Length > 0
            && string.Equals(source[..separator], PackageFamily, StringComparison.OrdinalIgnoreCase));
    }

    public bool Matches(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        if (string.Equals(source, ApplicationId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (source.Contains('!') || string.IsNullOrEmpty(Executable))
        {
            return false;
        }
        var sourceName = Path.GetFileName(source);
        if (sourceName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            sourceName = sourceName[..^4];
        }
        return string.Equals(sourceName,
            Path.GetFileNameWithoutExtension(Executable), StringComparison.OrdinalIgnoreCase);
    }

    public static AudioIdentity Read(uint processId)
    {
        using var process = OpenProcess(0x1000, false, processId);
        if (process.IsInvalid)
        {
            return new("", "", "");
        }

        var image = new StringBuilder(32768);
        var length = (uint)image.Capacity;
        var executable = QueryFullProcessImageName(process, 0, image, ref length) ? image.ToString() : "";
        uint idLength = 0;
        var result = GetApplicationUserModelId(process, ref idLength, null);
        var id = new StringBuilder((int)idLength);
        var applicationId = result == 122 && GetApplicationUserModelId(process, ref idLength, id) == 0
            ? id.ToString() : "";
        uint familyLength = 0;
        var familyResult = GetPackageFamilyName(process, ref familyLength, null);
        var family = new StringBuilder((int)familyLength);
        var packageFamily = familyResult == 122 && GetPackageFamilyName(process, ref familyLength, family) == 0
            ? family.ToString() : "";
        return new(executable, applicationId, packageFamily);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(SafeProcessHandle process, ref uint length, StringBuilder? id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(SafeProcessHandle process, ref uint length, StringBuilder? family);
}
