using System.Collections.Concurrent;
using Wcs.Api;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// S-TRACE-LOG-VIEWER 테스트 더블 — 전용 추적 sink 주입용.
//   · NopTraceLogger      — 무동작(기존 서비스 단위 테스트가 ITraceLogger 를 요구할 때 no-op 주입).
//   · CapturingTraceLogger — Log() 레코드를 인메모리로 수집(발화 계측·상관 검증 단위 테스트용).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>무동작 ITraceLogger — 관측/로깅 계측이 무관한 단위 테스트용.</summary>
public sealed class NopTraceLogger : ITraceLogger
{
    public void Log(TraceRecord record) { }
    public event Action<TraceRecord>? OnEntry { add { } remove { } }
}

/// <summary>Log() 레코드를 스레드 안전하게 수집하는 ITraceLogger — 발화·상관 검증용.</summary>
public sealed class CapturingTraceLogger : ITraceLogger
{
    private readonly ConcurrentQueue<TraceRecord> _records = new();

    public void Log(TraceRecord record)
    {
        _records.Enqueue(record);
        OnEntry?.Invoke(record);
    }

    public event Action<TraceRecord>? OnEntry;

    /// <summary>지금까지 수집된 레코드 스냅샷(발화 순서).</summary>
    public IReadOnlyList<TraceRecord> Records => _records.ToArray();
}
