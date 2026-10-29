namespace Margin.Core.Storage;

/// <summary>
/// SuperMemo 2 (Woźniak, 1990), the classic spaced repetition schedule: grades 0-5. Under 3 the card
/// starts over (tomorrow) and its ease stays. Otherwise the interval grows (1 day, 6 days, then times
/// the ease) and the ease then moves with the grade, never below 1.3.
/// </summary>
public static class Sm2
{
    public sealed record State(double IntervalDays, double Ease, int Reps);

    public static State Next(State s, int grade)
    {
        if (grade is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(grade));
        if (grade < 3) return new State(1, s.Ease, 0);
        int reps = s.Reps + 1;
        double interval = reps switch
        {
            1 => 1,
            2 => 6,
            _ => Math.Round(s.IntervalDays * s.Ease, 1),
        };
        double ease = Math.Max(1.3, s.Ease + 0.1 - (5 - grade) * (0.08 + (5 - grade) * 0.02));
        return new State(interval, ease, reps);
    }
}
