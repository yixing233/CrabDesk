namespace CrabDesk.Core;

public sealed record AudioMixerApplication(string Id, string Name, string ExecutablePath, double VolumePercent, bool Muted);

public sealed record AudioMixerSnapshot(string DeviceId, string DeviceName, double? VolumePercent, bool? Muted,
    IReadOnlyList<AudioMixerApplication> Applications)
{
    public static AudioMixerSnapshot Empty { get; } = new("", "输出设备不可用", null, null, []);
}

public static class AudioMixerPresentation
{
    public static IReadOnlyList<AudioMixerApplication> GroupApplications(IEnumerable<AudioMixerApplication> sessions) =>
        sessions.GroupBy(session => session.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First() with
            {
                VolumePercent = group.Max(session => session.VolumePercent),
                Muted = group.All(session => session.Muted)
            })
            .OrderBy(application => application.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
}
