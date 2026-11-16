// The ten pitch types: ids, display names and the meaning of each adjustable parameter.
// The id is what the playlist stores and the order the UI lists them in. The physical
// definition of each type is PitchPropertiesCalculator.
public static class PitchType
{
    public const int FourSeam = 0;
    public const int TwoSeam = 1;
    public const int Sinker = 2;
    public const int Cutter = 3;
    public const int Slider = 4;
    public const int Sweeper = 5;
    public const int Curve = 6;
    public const int Split = 7;
    public const int Change = 8;
    public const int Knuckle = 9;
    public const int Count = 10;
    public const int MaxParameters = 3;

    private static readonly string[] Names =
        { "4-Seam", "2-Seam", "Sinker", "Cutter", "Slider", "Sweeper", "Curve", "Split", "Change", "Knuckle" };

    // Parameters A, B and C are used in order: a type with two uses A and B.
    private static readonly int[] ParameterCounts = { 0, 1, 2, 1, 3, 1, 2, 1, 2, 3 };

    // Indexed by id * MaxParameters + parameter.
    private static readonly string[] ParameterNames =
    {
        "", "", "",
        "Run", "", "",
        "Break", "Direction", "",
        "Cut", "", "",
        "Velocity", "Break", "Shape",
        "Shape", "", "",
        "Axis", "Tempo", "",
        "Arm-Side Run", "", "",
        "Tempo", "Direction", "",
        "Flutter Rate", "Direction", "Flutter Size"
    };

    private static readonly string[] LowLabels =
    {
        "", "", "",
        "Small", "", "",
        "Small", "Run", "",
        "Small", "", "",
        "Slow", "Small", "Horizontal",
        "Sweep", "", "",
        "Drop", "Normal", "",
        "Small", "", "",
        "Firm", "Arm Side", "",
        "Slow", "Glove Side", "Small"
    };

    private static readonly string[] HighLabels =
    {
        "", "", "",
        "Large", "", "",
        "Large", "Sink", "",
        "Large", "", "",
        "Fast", "Large", "Vertical",
        "Lift", "", "",
        "Slurve", "Slow", "",
        "Large", "", "",
        "Slow", "Down", "",
        "Fast", "Arm Side", "Large"
    };

    public static bool IsValid(int type) { return type >= 0 && type < Count; }

    // An unknown id (corrupted or newer data) pitches as the 4-Seam.
    public static int Safe(int type) { return IsValid(type) ? type : FourSeam; }

    public static string Name(int type) { return Names[Safe(type)]; }

    public static int ParameterCount(int type) { return ParameterCounts[Safe(type)]; }

    public static bool UsesParameter(int type, int parameter)
    {
        return parameter >= 0 && parameter < ParameterCount(type);
    }

    public static string ParameterName(int type, int parameter) { return Label(ParameterNames, type, parameter); }
    public static string ParameterLow(int type, int parameter) { return Label(LowLabels, type, parameter); }
    public static string ParameterHigh(int type, int parameter) { return Label(HighLabels, type, parameter); }

    private static string Label(string[] labels, int type, int parameter)
    {
        return UsesParameter(type, parameter) ? labels[Safe(type) * MaxParameters + parameter] : "";
    }
}
