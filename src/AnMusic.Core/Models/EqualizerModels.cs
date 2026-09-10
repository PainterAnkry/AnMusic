namespace AnMusic.Models;

/// <summary>
/// 均衡器预设。
/// </summary>
public sealed class EqualizerPreset
{
    public string Name { get; init; } = "";
    public float[] Gains { get; init; } = new float[10];

    public static readonly float[] Frequencies = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

    public static readonly EqualizerPreset Flat = new() { Name = "平直", Gains = new float[10] };
    public static readonly EqualizerPreset Pop = new()
    {
        Name = "流行",
        Gains = [-1, 2, 5, 2, -1, -1, -1, 2, 3, 1]
    };
    public static readonly EqualizerPreset Rock = new()
    {
        Name = "摇滚",
        Gains = [5, 4, 3, -1, -2, -1, 2, 4, 5, 5]
    };
    public static readonly EqualizerPreset Vocal = new()
    {
        Name = "人声",
        Gains = [-2, -1, 0, 2, 4, 4, 3, 1, 0, -1]
    };
    public static readonly EqualizerPreset BassBoost = new()
    {
        Name = "低音增强",
        Gains = [8, 6, 4, 2, 0, 0, 0, 0, 0, 0]
    };
}
