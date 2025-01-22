public class ValidatorMovingAverage
{
    private readonly int avgSize;
    private readonly Queue<double> entries;

    private double lowestEntry;
    private double highestEntry;
    private double lowestAvg;
    private double highestAvg;
    private double acummulation;
    private double current;

    private long count;
    private long highestEntryOccurance;
    private long lowestEntryOccurance;

    public ValidatorMovingAverage(int avgSize)
    {
        this.count = 0;
        this.avgSize = avgSize;
        this.entries = new Queue<double>();
    }

    private bool isInitialized;

    public long LowestEntryOccurance => this.lowestEntryOccurance;
    public long HighestEntryOccurance => this.highestEntryOccurance;

    public double LowestAvg => lowestAvg / (double)TimeSpan.TicksPerMillisecond;
    public double HighestAvg => highestAvg / (double)TimeSpan.TicksPerMillisecond;
    public double LowestEntry => lowestEntry / (double)TimeSpan.TicksPerMillisecond;
    public double HighestEntry => highestEntry / (double)TimeSpan.TicksPerMillisecond;
    public double CurrentAvg => current / (double)TimeSpan.TicksPerMillisecond;

    public long TotalEntries => count;

    public double GetAverage(double entry)
    {
        if (!isInitialized && this.count > 1) // Skipping the very first average for now
        {
            lowestEntry = entry;
            highestEntry = entry;
            highestEntryOccurance = count;
            lowestEntryOccurance = count;
        }
        else
        {
            if (entry < lowestEntry)
            {
                lowestEntry = entry;
                lowestEntryOccurance = count;
            }
            else if (entry > highestEntry)
            {
                highestEntry = entry;
                highestEntryOccurance = count;
            }
        }

        acummulation += entry;
        entries.Enqueue(entry);

        if (entries.Count >= avgSize)
        {
            acummulation -= entries.Dequeue();
        }

        current = acummulation / (double)entries.Count;

        if (!isInitialized && this.count > 1)
        {
            lowestAvg = entry;
            highestAvg = entry;
            isInitialized = true;
        }
        else
        {
            if (current < lowestAvg)
            {
                lowestAvg = current;
            }
            if (current > highestAvg)
            {
                highestAvg = current;
            }
        }

        count++;
        return current;
    }
}
