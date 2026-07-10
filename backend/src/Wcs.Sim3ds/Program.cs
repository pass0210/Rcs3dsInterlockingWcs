// 3DS PLC 시뮬레이터 entrypoint — SimServer 타입의 얇은 래퍼.
// 설정: appsettings.Sim3ds.json(기본 Transport=Tcp) < 환경변수(SIM3DS_*) < CLI(--transport/--port/...).
//   기본(인자 없음) = TCP 127.0.0.1:1502 (현행 보존).
//   현장 리허설    = dotnet run -- --transport rtu --port COMx [--baud 9600 --parity Even --stopbits One --unit 1]
using Microsoft.Extensions.Logging;
using Wcs.Sim3ds;

var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
var log           = loggerFactory.CreateLogger<SimServer>();

// ── 설정 해석(fail-loud) ─────────────────────────────────────────────────────
SimServer.Options opt;
try
{
    opt = Sim3dsConfig.Resolve(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[Sim3ds] 설정 오류: {ex.Message}");
    return 1;
}

var cts    = new CancellationTokenSource();
var server = new SimServer(opt, log, line => Console.WriteLine(line));

Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ── 기동(전송/PortName 오류는 fail-loud) ─────────────────────────────────────
try
{
    // StartAsync 내부에서 "Sim3ds 서버 기동 <TCP host:port | RTU COMx baud/parity/stop unit=n>"를 로그.
    await server.StartAsync(cts.Token);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[Sim3ds] 기동 실패(transport={opt.Transport}): {ex.Message}");
    await server.DisposeAsync();
    return 1;
}

Console.WriteLine($"Sim3ds 기동 완료 (transport={opt.Transport}). Ctrl+C로 종료.");

try { await Task.Delay(Timeout.Infinite, cts.Token); }
catch (OperationCanceledException) { }

await server.StopAsync();
await server.DisposeAsync();
return 0;
