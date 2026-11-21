using Vortice.Direct3D11;

using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;

namespace FlyleafLib.MediaFramework.MediaRenderer;

/// <summary>
/// Fork: measures how long the GPU spends on work between <see cref="Begin"/> and <see cref="End"/>, without
/// waiting for it: results are collected frames later and averaged over 60 frames.
/// </summary>
internal sealed class GpuTimer : IDisposable
{
    const int Count = 8;
    const int Average = 60;

    readonly ID3D11Query[] disjoint = new ID3D11Query[Count], start = new ID3D11Query[Count], end = new ID3D11Query[Count];
    readonly bool[] pending = new bool[Count];
    int next, oldest;
    double total;
    int samples;

    public GpuTimer(ID3D11Device device)
    {
        try
        {
            for (int i = 0; i < Count; i++)
            {
                disjoint[i] = device.CreateQuery(QueryType.TimestampDisjoint);
                start[i] = device.CreateQuery(QueryType.Timestamp);
                end[i] = device.CreateQuery(QueryType.Timestamp);
            }
        }
        catch { Dispose(); throw; }
    }

    /// <summary>Starts timing, if a query is free (the GPU may be behind). Pass the result to <see cref="End"/>.</summary>
    public int Begin(ID3D11DeviceContext context)
    {
        int index = next;
        if (pending[index])
            return -1;

        context.Begin(disjoint[index]);
        context.End(start[index]);
        return index;
    }

    public void End(ID3D11DeviceContext context, int index)
    {
        if (index < 0)
            return;

        context.End(end[index]);
        context.End(disjoint[index]);
        pending[index] = true;
        next = (index + 1) % Count;
    }

    /// <summary>Collects finished timings. Returns the average in milliseconds once 60 are in, otherwise null.</summary>
    public double? Collect(ID3D11DeviceContext context)
    {
        while (pending[oldest])
        {
            int index = oldest;
            if (!context.IsDataAvailable(disjoint[index], AsyncGetDataFlags.DoNotFlush) ||
                !context.IsDataAvailable(end[index], AsyncGetDataFlags.DoNotFlush))
                break;

            var frequency = context.GetData<QueryDataTimestampDisjoint>(disjoint[index], AsyncGetDataFlags.DoNotFlush);
            ulong started = context.GetData<ulong>(start[index], AsyncGetDataFlags.DoNotFlush);
            ulong ended = context.GetData<ulong>(end[index], AsyncGetDataFlags.DoNotFlush);
            pending[index] = false;
            oldest = (index + 1) % Count;

            if (frequency.Disjoint || frequency.Frequency == 0 || ended <= started)
                continue;

            total += (ended - started) * 1000.0 / frequency.Frequency;
            samples++;
        }

        if (samples < Average)
            return null;

        double average = total / samples;
        total = 0;
        samples = 0;
        return average;
    }

    /// <summary>Drops the partial average, e.g. when what's being timed changes.</summary>
    public void Reset()
    {
        total = 0;
        samples = 0;
    }

    public void Dispose()
    {
        foreach (var queries in new[] { disjoint, start, end })
            foreach (var query in queries)
                query?.Dispose();
    }
}
