using System.Diagnostics;

namespace Aetheria.Server;

/// <summary>
/// A fixed-timestep driver. The simulation must advance in equal, deterministic steps regardless
/// of wall-clock jitter, so game logic never depends on frame time. Between steps the thread sleeps
/// to avoid burning a core. A catch-up cap prevents the "spiral of death" if the machine stalls.
/// </summary>
public sealed class FixedStepLoop
{
    private readonly double _stepSeconds;
    private readonly Action<float> _step;

    public FixedStepLoop(int tickRate, Action<float> step)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tickRate);
        ArgumentNullException.ThrowIfNull(step);

        _stepSeconds = 1.0 / tickRate;
        _step = step;
    }

    public void Run(CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew();
        double nextStepAt = clock.Elapsed.TotalSeconds;
        float dt = (float)_stepSeconds;

        while (!cancellation.IsCancellationRequested)
        {
            double now = clock.Elapsed.TotalSeconds;

            if (now >= nextStepAt)
            {
                _step(dt);
                nextStepAt += _stepSeconds;

                // If we fell more than a few steps behind, resync rather than trying to replay
                // every missed step at once.
                if (now - nextStepAt > _stepSeconds * 5)
                {
                    nextStepAt = now + _stepSeconds;
                }
            }
            else
            {
                double sleepSeconds = nextStepAt - now;
                if (sleepSeconds > 0.001)
                {
                    Thread.Sleep((int)(sleepSeconds * 1000));
                }
            }
        }
    }
}
