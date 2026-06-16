using System.IO.Pipelines;
using FluentModbus;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// FakeSerialPort — in-memory IModbusRtuSerialPort 구현 (S-RTU VT-2·3·4·5용)
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 물리 COM 포트 없이 in-process RTU 왕복을 테스트하기 위한 in-memory serial port 쌍.
///
/// 사용법:
///   var (clientPort, serverPort) = FakeSerialPortPair.Create();
///   ModbusRtuClient.Initialize(clientPort, ...)
///   ModbusRtuServer.Start(serverPort)
///
/// 내부는 두 Pipe: A→B·B→A. 클라이언트가 쓰면 서버가 읽고, 서버가 쓰면 클라이언트가 읽는다.
/// </summary>
public sealed class FakeSerialPort : IModbusRtuSerialPort, IAsyncDisposable
{
    private readonly Pipe   _readPipe;   // 상대방이 쓰고 우리가 읽는 파이프
    private readonly Pipe   _writePipe;  // 우리가 쓰고 상대방이 읽는 파이프
    private          bool   _isOpen;
    private          bool   _closed;

    /// <summary>
    /// IsOpen=true/false 제어로 강제 단절 시뮬레이션(VT-5 OFFLINE 테스트용).
    /// </summary>
    public bool SimulateClose
    {
        get => _closed;
        set => _closed = value;
    }

    private FakeSerialPort(Pipe readPipe, Pipe writePipe, string portName)
    {
        _readPipe  = readPipe;
        _writePipe = writePipe;
        PortName   = portName;
    }

    // ── IModbusRtuSerialPort ─────────────────────────────────────────────────

    public string PortName { get; }

    public bool IsOpen => _isOpen && !_closed;

    public void Open()  => _isOpen = true;
    public void Close() => _isOpen = false;

    /// <summary>
    /// 동기 읽기 — 현재 구현에서는 사용되지 않는다(FluentModbus는 ReadAsync 경로만 사용).
    /// 만약 동기 경로로 호출된다면 데이터 없을 때 0을 반환해 호출자 busy-spin을 유발할 수 있으므로,
    /// 명시적으로 <see cref="NotSupportedException"/>을 던져 fail-loud 처리한다.
    /// 동기 읽기가 필요한 경우에는 ReadAsync를 사용하거나 이 구현을 확장할 것. (S-RTU MINOR-3)
    /// </summary>
    public int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException(
            "FakeSerialPort: 동기 Read는 지원하지 않습니다. ReadAsync를 사용하세요. " +
            "(busy-spin 방지 — S-RTU MINOR-3)");

    public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
    {
        if (_closed)
            throw new System.IO.IOException("FakeSerialPort: 포트가 강제 닫힘(SimulateClose=true)");

        var result = await _readPipe.Reader.ReadAsync(token).ConfigureAwait(false);
        if (result.IsCanceled || result.IsCompleted && result.Buffer.IsEmpty)
            return 0;

        int read = 0;
        foreach (var seg in result.Buffer)
        {
            int toCopy = Math.Min(seg.Length, count - read);
            seg.Slice(0, toCopy).Span.CopyTo(buffer.AsSpan(offset + read, toCopy));
            read += toCopy;
            if (read >= count) break;
        }
        _readPipe.Reader.AdvanceTo(result.Buffer.GetPosition(read));
        return read;
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        var mem = _writePipe.Writer.GetMemory(count);
        buffer.AsSpan(offset, count).CopyTo(mem.Span);
        _writePipe.Writer.Advance(count);
        _writePipe.Writer.FlushAsync().GetAwaiter().GetResult();
    }

    public async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
    {
        if (_closed)
            throw new System.IO.IOException("FakeSerialPort: 포트가 강제 닫힘(SimulateClose=true)");

        var mem = _writePipe.Writer.GetMemory(count);
        buffer.AsSpan(offset, count).CopyTo(mem.Span);
        _writePipe.Writer.Advance(count);
        await _writePipe.Writer.FlushAsync(token).ConfigureAwait(false);
    }

    // ── 정리 ─────────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        await _readPipe.Reader.CompleteAsync().ConfigureAwait(false);
        await _writePipe.Writer.CompleteAsync().ConfigureAwait(false);
    }

    // ── 팩토리 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 연결된 FakeSerialPort 쌍을 생성한다.
    /// clientPort → ModbusRtuClient.Initialize(clientPort, ...)
    /// serverPort → ModbusRtuServer.Start(serverPort)
    /// </summary>
    public static (FakeSerialPort clientPort, FakeSerialPort serverPort) Create()
    {
        // client→server: client가 쓰고, server가 읽는 파이프
        var c2s = new Pipe(new PipeOptions(useSynchronizationContext: false));
        // server→client: server가 쓰고, client가 읽는 파이프
        var s2c = new Pipe(new PipeOptions(useSynchronizationContext: false));

        // client: readPipe=s2c(서버→클라이언트 읽기), writePipe=c2s(클라이언트→서버 쓰기)
        var client = new FakeSerialPort(readPipe: s2c, writePipe: c2s, portName: "FAKE_CLIENT");
        // server: readPipe=c2s(클라이언트→서버 읽기), writePipe=s2c(서버→클라이언트 쓰기)
        var server = new FakeSerialPort(readPipe: c2s, writePipe: s2c, portName: "FAKE_SERVER");

        client.Open();
        server.Open();
        return (client, server);
    }
}
