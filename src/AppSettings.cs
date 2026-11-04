using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;

[DataContract]
internal sealed class AppSettings
{
    [DataMember(IsRequired = true)] public string EdgePath { get; set; }
    [DataMember(IsRequired = true)] public string ProfileDirectory { get; set; }
    [DataMember(IsRequired = true)] public string PwaAppId { get; set; }
    [DataMember(IsRequired = true)] public string WindowAppId { get; set; }
    internal static AppSettings Current { get; private set; }

    internal static void Load(string path)
    {
        using (FileStream stream = File.OpenRead(path))
        {
            var settings = (AppSettings)new DataContractJsonSerializer(typeof(AppSettings)).ReadObject(stream);
            settings.Validate();
            Current = settings;
        }
    }

    internal void Validate()
    {
        string executable = Path.GetFileName(EdgePath ?? "");
        if (!Path.IsPathRooted(EdgePath ?? "") || !File.Exists(EdgePath)
            || (!String.Equals(executable, "msedge_proxy.exe", StringComparison.OrdinalIgnoreCase)
                && !String.Equals(executable, "msedge.exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("EdgePath must point to an installed Edge executable.");
        if (!Regex.IsMatch(ProfileDirectory ?? "", @"\A[A-Za-z0-9 _-]+\z"))
            throw new InvalidDataException("Invalid Edge profile directory.");
        if (!Regex.IsMatch(PwaAppId ?? "", @"\A[a-p]{32}\z"))
            throw new InvalidDataException("Invalid installed PWA app ID.");
        if (!Regex.IsMatch(WindowAppId ?? "", @"\Achatgpt\.com-[A-Za-z0-9_!.-]+\z"))
            throw new InvalidDataException("WindowAppId must identify the installed ChatGPT web app.");
    }

    internal string LaunchArguments(string marker)
    {
        // Markers are generated locally; reject delimiters before constructing process arguments.
        if (!Regex.IsMatch(marker ?? "", @"\A[a-z0-9-]+\z")) throw new ArgumentException("Invalid launch marker.");
        return "--profile-directory=\"" + ProfileDirectory + "\" --app-id=" + PwaAppId
            + " --app-launch-url-for-shortcuts-menu-item=\"https://chatgpt.com/?" + marker + "=1\" --new-window";
    }
}
