using System.Net.Sockets;

namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// WcsTeardownGuard — 종료 단계 관찰되지 않은 Task 예외 가드.
//
// 문제:
//   종속 라이브러리(FluentModbus)는 내부적으로 await되지 않는 백그라운드 루프 Task를 돌린다.
//     · ModbusTcpServer.Start: TcpListener.AcceptTcpClientAsync 루프 — Stop() 시
//       SocketException(10004/995, "I/O 작업이 취소되었습니다")으로 폴트.
//     · ModbusRtuClient/Server 읽기 루프 — 포트(파이프) 종료 시
//       IOException / InvalidOperationException("No reading allowed")으로 폴트.
//   이 Task들은 라이브러리가 보관·await하지 않으므로 "관찰되지 않은 Task 예외"가 되고,
//   GC가 해당 Task를 수거할 때 파이널라이저 스레드가 예외를 재던져
//   프로세스(WCS Windows 서비스 호스트 / 테스트호스트)를 비정상 종료시킨다.
//
// 정책 (Fail Loud 보존):
//   종료 신호로 명백한 양성(benign) 예외 — 소켓 취소·I/O 취소·dispose 경쟁 — 만
//   SetObserved()로 관찰 처리하고 반드시 stderr에 1줄 로깅한다.
//   그 외 예외는 관찰하지 않는다(기존 .NET 기본 동작 유지 — 진성 버그는 그대로 노출).
//   라이브러리 본문·Sim3ds·게이트웨이 본문을 건드리지 않고 호스트 경계에서만 방어한다.
//
// 멱등: 프로세스당 1회만 핸들러를 등록한다(Program.cs·테스트 어셈블리 양쪽에서 호출 가능).
// ════════════════════════════════════════════════════════════════════════════
public static class WcsTeardownGuard
{
    private static int _installed;

    /// <summary>관찰되지 않은 Task 예외 가드 핸들러를 1회 등록한다.</summary>
    public static void Install()
    {
        // 프로세스당 1회만 — Interlocked CAS로 중복 등록 방지.
        if (Interlocked.Exchange(ref _installed, 1) != 0)
            return;

        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // AggregateException의 모든 내부 예외가 양성 종료 신호일 때만 관찰 처리.
        // 하나라도 양성이 아니면 관찰하지 않아 기존 동작(노출)을 유지한다.
        var flat = e.Exception.Flatten().InnerExceptions;
        if (flat.Count == 0 || !flat.All(IsBenignTeardownException))
            return;

        e.SetObserved();

        // 양성이라도 무음 흡수 금지 — stderr에 1줄 남긴다(원인·노이즈 추적 가능).
        var first = flat[0];
        Console.Error.WriteLine(
            $"[WcsTeardownGuard] 종료 단계 관찰되지 않은 Task 예외 흡수: " +
            $"{first.GetType().Name}: {first.Message}");
    }

    /// <summary>종료 신호로 명백한 양성 예외인지 판정.</summary>
    private static bool IsBenignTeardownException(Exception ex) => ex switch
    {
        // 소켓 종료: 995(WSA_OPERATION_ABORTED)·10004(WSAEINTR)·OperationAborted·Interrupted.
        SocketException se =>
            se.SocketErrorCode is SocketError.OperationAborted
                              or SocketError.Interrupted
                              or SocketError.Shutdown
                              or SocketError.ConnectionReset
            || se.NativeErrorCode is 995 or 10004,

        // 파이프/스트림 종료 경쟁: ReadAsync 중 Reader.Complete → "No reading allowed".
        // (ObjectDisposedException은 InvalidOperationException의 하위형이라 이 ARM이 함께 포괄한다 —
        //  포트·소켓 dispose 경쟁도 여기서 처리됨.)
        InvalidOperationException => true,

        // 종료 취소 토큰 전파.
        OperationCanceledException => true,

        // I/O 취소(소켓·시리얼) — 메시지에 취소/aborted 신호가 있을 때만.
        System.IO.IOException io =>
            io.InnerException is SocketException
            || io.InnerException is OperationCanceledException
            || io.InnerException is ObjectDisposedException,

        _ => false,
    };
}
