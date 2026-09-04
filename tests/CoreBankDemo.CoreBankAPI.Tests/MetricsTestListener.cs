using System.Diagnostics.Metrics;
using CoreBankDemo.ServiceDefaults;

namespace CoreBankDemo.CoreBankAPI.Tests;

/// <summary>
/// Single recorded measurement, captured with its tags as a plain dictionary
/// so tests can assert both the value and the exact (bounded) attribute set.
/// </summary>
public sealed record CapturedMeasurement(string InstrumentName, object Value, IReadOnlyDictionary<string, object?> Tags);

/// <summary>
/// <see cref="MeterListener"/>-based capture helper for <see cref="BusinessMetrics"/>
/// (story 6.5), scoped to one specific <see cref="BusinessMetrics"/>
/// instance's own <c>Meter</c> (by reference, via the internal
/// <c>BusinessMetrics.Meter</c> accessor — see this project's
/// <c>InternalsVisibleTo</c> grant from <c>CoreBankDemo.ServiceDefaults</c>)
/// rather than <see cref="BusinessMetrics.MeterName"/> alone. Multiple
/// <see cref="BusinessMetrics"/> instances share the same meter name (every
/// test creates its own), so name-only filtering would leak measurements
/// across concurrently-running tests (xUnit runs different test classes in
/// parallel by default).
/// </summary>
public sealed class MetricsTestListener : IDisposable
{
    private readonly MeterListener _listener;

    public List<CapturedMeasurement> Measurements { get; } = [];

    public MetricsTestListener(BusinessMetrics businessMetrics)
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, businessMetrics.Meter))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };

        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            Measurements.Add(new CapturedMeasurement(instrument.Name, value, ToDictionary(tags))));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            Measurements.Add(new CapturedMeasurement(instrument.Name, value, ToDictionary(tags))));

        _listener.Start();
    }

    private static IReadOnlyDictionary<string, object?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dictionary = new Dictionary<string, object?>();
        foreach (var tag in tags)
        {
            dictionary[tag.Key] = tag.Value;
        }

        return dictionary;
    }

    public void Dispose() => _listener.Dispose();
}
