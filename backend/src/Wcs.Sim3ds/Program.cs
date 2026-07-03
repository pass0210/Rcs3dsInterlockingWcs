// 3DS PLC 시뮬레이터 entrypoint — SimServer 타입의 얇은 래퍼.
// 설정은 appsettings.json 또는 환경변수 Sim3ds:* 로 제공.
using Microsoft.Extensions.Logging;
using Wcs.Sim3ds;

var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
var log           = loggerFactory.CreateLogger<SimServer>();

var opt = new SimServer.Options
{
    Host            = "127.0.0.1",
    Port            = 1502,
    TiltDelayMs     = 200,
    SortDurationMs  = 500,
    MoveDurationMs  = 300,
    InitialCurFloor = 1,
};

var cts    = new CancellationTokenSource();
var server = new SimServer(opt, log, line => Console.WriteLine(line));

Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await server.StartAsync(cts.Token);
Console.WriteLine($"Sim3ds 기동 완료 ({opt.Host}:{opt.Port}). Ctrl+C로 종료.");

try { await Task.Delay(Timeout.Infinite, cts.Token); }
catch (OperationCanceledException) { }

await server.StopAsync();
await server.DisposeAsync();
