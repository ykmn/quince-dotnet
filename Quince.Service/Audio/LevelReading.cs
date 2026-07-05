namespace Quince.Service.Audio;

public sealed record LevelReading(
    double TruePeakDb = double.NegativeInfinity,
    double TruePeakMaxDb = double.NegativeInfinity,
    double LoudnessM = double.NegativeInfinity,
    double LoudnessS = double.NegativeInfinity,
    double LoudnessI = double.NegativeInfinity,
    double TruePeakLDb = double.NegativeInfinity,
    double TruePeakRDb = double.NegativeInfinity);
