using UnityEngine;

public class IncreaseProfilerBuffer : MonoBehaviour
{
    void Awake()
    {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
        // Double or quadruple the profiler memory (default ~128MB)
        // UnityEngine.Profiling.Profiler.maxUsedMemory = 512 * 1024 * 1024; // 512 MB
        // Debug.Log($"Profiler.maxUsedMemory set to {UnityEngine.Profiling.Profiler.maxUsedMemory / (1024 * 1024)} MB");
#endif
    }
}
