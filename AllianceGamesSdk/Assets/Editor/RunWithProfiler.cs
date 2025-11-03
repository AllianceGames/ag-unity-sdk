// Assets/Editor/RunWithProfiler.cs
using UnityEditor;
using UnityEngine;
using System.IO;

public static class RunWithProfiler
{
    [MenuItem("Run/Play With Profiler (Server)")]
    public static void RunServerWithProfiler()
    {
        string path = Path.Combine(Application.dataPath, "../../Builds/Server/AllianceGamesSdk.exe");
        if (!File.Exists(path))
        {
            Debug.LogError($"Build not found at {path}");
            return;
        }

        var args = "-profiler-enable -profiler-logfile ./ServerProfile.raw -profiler-maxusedmemory 512";
        var proc = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                Arguments = args,
                WorkingDirectory = Path.GetDirectoryName(path)
            }
        };
        proc.Start();
        Debug.Log("Launched server with profiler enabled.");
    }
}
